// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;

namespace GraphReader.Axis;

/// <summary>
/// Fits SCD plot geometry from detector-neutral LSD/Hough line candidates.
/// This stage emits axes, ticks, dividers, and diagnostics only.
/// </summary>
public sealed class AxisGeometryDetector : IAxisGeometryDetector
{
    public ValueTask<AxisGeometryResult> DetectAsync(
        AxisGeometryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var result = DetectCore(request, cancellationToken);
        return ValueTask.FromResult(result);
    }

    public async ValueTask<AxisGeometryResult> DetectAsync(
        GrayscaleLineCandidateFrame frame,
        ILineCandidateProvider candidateProvider,
        AxisGeometryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(candidateProvider);
        ValidateFrame(frame);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = await candidateProvider
            .DetectLinesAsync(frame, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (candidates is null)
        {
            throw new InvalidOperationException("The line candidate provider returned null.");
        }

        return DetectCore(
            new AxisGeometryRequest(
                frame.Width,
                frame.Height,
                candidates,
                options,
                frame.CoordinateSpace),
            cancellationToken);
    }

    private static AxisGeometryResult DetectCore(
        AxisGeometryRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var options = request.Options ?? new AxisGeometryOptions();
        ValidateRequest(request, options);

        var warnings = new List<string>();
        var accepted = new List<Observation>(request.LineCandidates.Count);
        var rejectedCount = 0;

        for (var index = 0; index < request.LineCandidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = request.LineCandidates[index];
            if (!TryCreateObservation(candidate, request, options, out var observation))
            {
                rejectedCount++;
                continue;
            }

            accepted.Add(observation);
        }

        if (accepted.Count == 0)
        {
            throw DetectionFailure("No finite line candidates were available for axis fitting.");
        }

        EnsureUniqueIds(accepted);
        var horizontal = accepted.Where(item => item.Orientation == Orientation.Horizontal).ToArray();
        var vertical = accepted.Where(item => item.Orientation == Orientation.Vertical).ToArray();

        if (horizontal.Length == 0 || vertical.Length == 0)
        {
            throw DetectionFailure("Both horizontal and vertical line evidence are required.");
        }

        var horizontalFamilies = BuildFamilies(
            horizontal,
            Orientation.Horizontal,
            options,
            cancellationToken);
        var verticalFamilies = BuildFamilies(
            vertical,
            Orientation.Vertical,
            options,
            cancellationToken);

        var frameFamilies = FindEnclosingFrameFamilies(
            horizontalFamilies,
            verticalFamilies,
            request.ImageWidth,
            request.ImageHeight);

        var pairCandidates = new List<AxisPair>();
        foreach (var horizontalFamily in horizontalFamilies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frameFamilies.Contains(horizontalFamily))
            {
                continue;
            }

            foreach (var verticalFamily in verticalFamilies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frameFamilies.Contains(verticalFamily) ||
                    !TryIntersect(horizontalFamily, verticalFamily, out var intersection))
                {
                    continue;
                }

                if (!IsInsideWithTolerance(
                        intersection,
                        request.ImageWidth,
                        request.ImageHeight,
                        options.MergeDistancePixels * 2d))
                {
                    continue;
                }

                var pair = ScoreAxisPair(
                    horizontalFamily,
                    verticalFamily,
                    intersection,
                    request.ImageWidth,
                    request.ImageHeight);
                if (pair.RightDistance > 0d && pair.TopDistance > 0d)
                {
                    pairCandidates.Add(pair);
                }
            }
        }

        if (pairCandidates.Count == 0)
        {
            throw DetectionFailure("No intersecting x-axis and y-axis pair could be fitted.");
        }

        pairCandidates.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        var bestPair = pairCandidates[0];
        var alternativeMargin = pairCandidates.Count == 1
            ? 1d
            : Clamp01(bestPair.Score - pairCandidates[1].Score);

        var plot = CreatePlotPolygon(bestPair);
        var plotHeight = bestPair.TopDistance;
        var plotWidth = bestPair.RightDistance;

        var candidateDividerFamilies = FindDividerFamilies(
            verticalFamilies,
            bestPair,
            frameFamilies,
            plotWidth,
            plotHeight,
            options,
            cancellationToken);

        var gridFamilies = FindRegularGridFamilies(candidateDividerFamilies, bestPair, options);
        var dividerFamilies = candidateDividerFamilies
            .Where(family => !gridFamilies.Contains(family))
            .ToArray();

        var dividers = CreateDividers(dividerFamilies, bestPair, plotHeight, options);
        var ambiguousGridOrDividers = CreateAmbiguousGridOrDividers(
            gridFamilies,
            bestPair,
            plotHeight);
        var dividerCandidateIds = dividers
            .SelectMany(divider => divider.SupportingCandidateIds)
            .ToHashSet(StringComparer.Ordinal);

        var ticks = CreateTicks(
            accepted,
            bestPair,
            plotWidth,
            plotHeight,
            options,
            dividerCandidateIds,
            cancellationToken);

        var xConfidence = AxisConfidence(
            bestPair.Horizontal,
            bestPair.Score,
            request.ImageWidth,
            options.MinimumAxisSpanFraction);
        var yConfidence = AxisConfidence(
            bestPair.Vertical,
            bestPair.Score,
            request.ImageHeight,
            options.MinimumAxisSpanFraction);
        var confidence = Clamp01((xConfidence + yConfidence + bestPair.Score) / 3d);

        var uncertaintyReasons = new List<string>();
        if (alternativeMargin < 0.08d)
        {
            uncertaintyReasons.Add("axis_pair_ambiguous");
        }

        if (bestPair.Horizontal.Span / request.ImageWidth < options.MinimumAxisSpanFraction)
        {
            uncertaintyReasons.Add("x_axis_partial");
        }

        if (bestPair.Vertical.Span / request.ImageHeight < options.MinimumAxisSpanFraction)
        {
            uncertaintyReasons.Add("y_axis_partial");
        }

        if (gridFamilies.Count > 0)
        {
            warnings.Add(
                "Regular full-height line families were withheld from confirmed dividers as ambiguous grid or phase-divider evidence.");
            uncertaintyReasons.Add("grid_or_phase_divider_ambiguous");
        }

        if (frameFamilies.Count > 0)
        {
            warnings.Add("An enclosing frame was excluded from axis selection.");
        }

        if (confidence < options.NeedsReviewConfidenceThreshold)
        {
            uncertaintyReasons.Add("low_geometry_confidence");
        }

        stopwatch.Stop();
        var uncertainty = new AxisGeometryUncertainty(
            bestPair.Horizontal.RootMeanSquareError,
            bestPair.Vertical.RootMeanSquareError,
            alternativeMargin,
            uncertaintyReasons.Count > 0,
            uncertaintyReasons.AsReadOnly());

        var diagnostics = new AxisGeometryDiagnostics(
            request.LineCandidates.Count,
            accepted.Count,
            rejectedCount,
            horizontal.Length,
            vertical.Length,
            ticks.Count,
            dividers.Count,
            frameFamilies.Count + gridFamilies.Count,
            stopwatch.Elapsed,
            warnings.AsReadOnly());

        return new AxisGeometryResult(
            AxisGeometryCoordinateSpaces.OriginalPixels,
            plot,
            CreateAxisFit(
                bestPair.Horizontal,
                new GeometryLineSegment(plot.BottomLeft, plot.BottomRight),
                xConfidence),
            CreateAxisFit(
                bestPair.Vertical,
                new GeometryLineSegment(plot.BottomLeft, plot.TopLeft),
                yConfidence),
            ticks.AsReadOnly(),
            dividers.AsReadOnly(),
            ambiguousGridOrDividers.AsReadOnly(),
            confidence,
            uncertainty,
            diagnostics);
    }

    private static void ValidateFrame(GrayscaleLineCandidateFrame frame)
    {
        if (!string.Equals(
                frame.CoordinateSpace,
                AxisGeometryCoordinateSpaces.OriginalPixels,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Line candidates must be normalized to original_pixels before geometry detection.",
                nameof(frame));
        }

        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride < frame.Width)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "Frame dimensions and stride are invalid.");
        }

        var requiredLength = checked(frame.Stride * frame.Height);
        if (frame.Pixels.Length < requiredLength)
        {
            throw new ArgumentException("The grayscale buffer is shorter than stride times height.", nameof(frame));
        }
    }

    private static void ValidateRequest(AxisGeometryRequest request, AxisGeometryOptions options)
    {
        if (!string.Equals(
                request.CoordinateSpace,
                AxisGeometryCoordinateSpaces.OriginalPixels,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Axis geometry accepts only original_pixels candidates. Map derivative evidence back first.",
                nameof(request));
        }

        if (request.ImageWidth <= 0 || request.ImageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Image dimensions must be positive.");
        }

        ArgumentNullException.ThrowIfNull(request.LineCandidates);

        if (options.MaximumAxisDeviationDegrees is <= 0d or >= 45d ||
            options.MergeAngleToleranceDegrees is <= 0d or >= 45d ||
            options.MergeDistancePixels <= 0d ||
            options.MinimumCandidateLengthPixels <= 0d ||
            !IsFraction(options.MinimumAxisSpanFraction) ||
            !IsFraction(options.TickMaximumLengthFraction) ||
            options.TickAxisDistancePixels <= 0d ||
            !IsFraction(options.DividerMinimumSpanFraction) ||
            !IsFraction(options.DividerMinimumCoverageFraction) ||
            options.DottedDividerMinimumSegments < 2 ||
            !IsFraction(options.DottedDividerMaximumSegmentFraction) ||
            !IsFraction(options.PlotEdgeExclusionFraction) ||
            options.GridSpacingCoefficientOfVariation <= 0d ||
            options.GridMinimumAlignedLines < 3 ||
            !IsFraction(options.NeedsReviewConfidenceThreshold))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Axis geometry options are outside their valid ranges.");
        }
    }

    private static bool TryCreateObservation(
        GeometryLineCandidate? candidate,
        AxisGeometryRequest request,
        AxisGeometryOptions options,
        out Observation observation)
    {
        observation = default!;
        if (candidate is null ||
            string.IsNullOrWhiteSpace(candidate.CandidateId) ||
            !candidate.Segment.Start.IsFinite ||
            !candidate.Segment.End.IsFinite ||
            !double.IsFinite(candidate.Strength) ||
            candidate.Strength is <= 0d or > 1d ||
            !double.IsFinite(candidate.StrokeWidthPixels) ||
            candidate.StrokeWidthPixels <= 0d ||
            !IsInside(candidate.Segment.Start, request.ImageWidth, request.ImageHeight) ||
            !IsInside(candidate.Segment.End, request.ImageWidth, request.ImageHeight) ||
            candidate.Segment.Length < options.MinimumCandidateLengthPixels)
        {
            return false;
        }

        var deltaX = candidate.Segment.End.X - candidate.Segment.Start.X;
        var deltaY = candidate.Segment.End.Y - candidate.Segment.Start.Y;
        var acuteAngle = Math.Atan2(Math.Abs(deltaY), Math.Abs(deltaX)) * 180d / Math.PI;
        var horizontalDeviation = acuteAngle;
        var verticalDeviation = 90d - acuteAngle;

        Orientation orientation;
        double signedAngle;
        if (horizontalDeviation <= options.MaximumAxisDeviationDegrees &&
            horizontalDeviation <= verticalDeviation)
        {
            orientation = Orientation.Horizontal;
            if (deltaX < 0d)
            {
                deltaX = -deltaX;
                deltaY = -deltaY;
            }

            signedAngle = Math.Atan2(deltaY, deltaX) * 180d / Math.PI;
        }
        else if (verticalDeviation <= options.MaximumAxisDeviationDegrees)
        {
            orientation = Orientation.Vertical;
            if (deltaY < 0d)
            {
                deltaX = -deltaX;
                deltaY = -deltaY;
            }

            signedAngle = Math.Atan2(deltaX, deltaY) * 180d / Math.PI;
        }
        else
        {
            return false;
        }

        observation = new Observation(candidate, orientation, signedAngle);
        return true;
    }

    private static void EnsureUniqueIds(IReadOnlyList<Observation> observations)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            if (!ids.Add(observation.Candidate.CandidateId))
            {
                throw new ArgumentException(
                    $"Line candidate ID '{observation.Candidate.CandidateId}' is duplicated.");
            }
        }
    }

    private static List<LineFamily> BuildFamilies(
        IReadOnlyList<Observation> observations,
        Orientation orientation,
        AxisGeometryOptions options,
        CancellationToken cancellationToken)
    {
        var byMembers = new Dictionary<string, LineFamily>(StringComparer.Ordinal);

        foreach (var hypothesis in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inliers = observations
                .Where(candidate =>
                    AngleDifferenceDegrees(candidate, hypothesis) <= options.MergeAngleToleranceDegrees &&
                    SegmentFitsLine(
                        candidate.Candidate.Segment,
                        hypothesis.Candidate.Segment,
                        options.MergeDistancePixels +
                        ((candidate.Candidate.StrokeWidthPixels + hypothesis.Candidate.StrokeWidthPixels) / 2d)))
                .ToArray();

            var fitted = FitFamily(inliers, orientation);
            var refined = observations
                .Where(candidate =>
                    AngleDifferenceDegrees(candidate, fitted.AngleDegrees) <= options.MergeAngleToleranceDegrees &&
                    fitted.SegmentFits(
                        candidate.Candidate.Segment,
                        options.MergeDistancePixels + candidate.Candidate.StrokeWidthPixels))
                .ToArray();

            if (refined.Length == 0)
            {
                continue;
            }

            fitted = FitFamily(refined, orientation);
            var key = string.Join(
                '\u001f',
                fitted.Members
                    .Select(member => member.Candidate.CandidateId)
                    .Order(StringComparer.Ordinal));

            if (!byMembers.TryGetValue(key, out var existing) ||
                fitted.RootMeanSquareError < existing.RootMeanSquareError)
            {
                byMembers[key] = fitted;
            }
        }

        return byMembers.Values.ToList();
    }

    private static LineFamily FitFamily(IReadOnlyList<Observation> members, Orientation orientation)
    {
        if (members.Count == 0)
        {
            throw new InvalidOperationException("Cannot fit an empty line family.");
        }

        double totalWeight = 0d;
        double primaryMean = 0d;
        double secondaryMean = 0d;
        foreach (var member in members)
        {
            var weight = MemberWeight(member);
            AddPoint(member.Candidate.Segment.Start, weight);
            AddPoint(member.Candidate.Segment.End, weight);
        }

        primaryMean /= totalWeight;
        secondaryMean /= totalWeight;

        double covariance = 0d;
        double variance = 0d;
        foreach (var member in members)
        {
            var weight = MemberWeight(member);
            Accumulate(member.Candidate.Segment.Start, weight);
            Accumulate(member.Candidate.Segment.End, weight);
        }

        var slope = variance <= 1e-9d ? 0d : covariance / variance;
        var intercept = secondaryMean - (slope * primaryMean);
        var basePoint = orientation == Orientation.Horizontal
            ? new PixelPoint(0d, intercept)
            : new PixelPoint(intercept, 0d);
        var direction = orientation == Orientation.Horizontal
            ? Normalize(new Vector2(1d, slope))
            : Normalize(new Vector2(slope, 1d));

        var intervals = new List<(double Start, double End)>(members.Count);
        double squareError = 0d;
        double errorWeight = 0d;
        foreach (var member in members)
        {
            var startProjection = Project(member.Candidate.Segment.Start, basePoint, direction);
            var endProjection = Project(member.Candidate.Segment.End, basePoint, direction);
            intervals.Add((Math.Min(startProjection, endProjection), Math.Max(startProjection, endProjection)));

            var weight = MemberWeight(member);
            var startError = DistanceToFittedLine(member.Candidate.Segment.Start, slope, intercept, orientation);
            var endError = DistanceToFittedLine(member.Candidate.Segment.End, slope, intercept, orientation);
            squareError += weight * ((startError * startError) + (endError * endError));
            errorWeight += 2d * weight;
        }

        intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var minimumProjection = intervals[0].Start;
        var maximumProjection = intervals.Max(item => item.End);
        var coverageLength = UnionLength(intervals);
        var span = maximumProjection - minimumProjection;
        var coverageFraction = span <= 1e-9d ? 0d : Clamp01(coverageLength / span);
        var line = new GeometryLineSegment(
            Add(basePoint, direction, minimumProjection),
            Add(basePoint, direction, maximumProjection));

        return new LineFamily(
            orientation,
            slope,
            intercept,
            direction,
            line,
            members.ToArray(),
            span,
            coverageFraction,
            errorWeight <= 0d ? 0d : Math.Sqrt(squareError / errorWeight));

        void AddPoint(PixelPoint point, double weight)
        {
            var primary = orientation == Orientation.Horizontal ? point.X : point.Y;
            var secondary = orientation == Orientation.Horizontal ? point.Y : point.X;
            totalWeight += weight;
            primaryMean += primary * weight;
            secondaryMean += secondary * weight;
        }

        void Accumulate(PixelPoint point, double weight)
        {
            var primary = orientation == Orientation.Horizontal ? point.X : point.Y;
            var secondary = orientation == Orientation.Horizontal ? point.Y : point.X;
            covariance += weight * (primary - primaryMean) * (secondary - secondaryMean);
            variance += weight * (primary - primaryMean) * (primary - primaryMean);
        }
    }

    private static HashSet<LineFamily> FindEnclosingFrameFamilies(
        IReadOnlyList<LineFamily> horizontal,
        IReadOnlyList<LineFamily> vertical,
        int width,
        int height)
    {
        const double edgeFraction = 0.1d;
        const double spanFraction = 0.7d;

        var left = vertical
            .Where(family => family.Midpoint.X <= width * edgeFraction && family.Span >= height * spanFraction)
            .OrderBy(family => family.Midpoint.X)
            .FirstOrDefault();
        var right = vertical
            .Where(family => family.Midpoint.X >= width * (1d - edgeFraction) && family.Span >= height * spanFraction)
            .OrderByDescending(family => family.Midpoint.X)
            .FirstOrDefault();
        var top = horizontal
            .Where(family => family.Midpoint.Y <= height * edgeFraction && family.Span >= width * spanFraction)
            .OrderBy(family => family.Midpoint.Y)
            .FirstOrDefault();
        var bottom = horizontal
            .Where(family => family.Midpoint.Y >= height * (1d - edgeFraction) && family.Span >= width * spanFraction)
            .OrderByDescending(family => family.Midpoint.Y)
            .FirstOrDefault();

        if (left is null || right is null || top is null || bottom is null)
        {
            return [];
        }

        return [left, right, top, bottom];
    }

    private static AxisPair ScoreAxisPair(
        LineFamily horizontal,
        LineFamily vertical,
        PixelPoint intersection,
        int imageWidth,
        int imageHeight)
    {
        var xDirection = horizontal.Direction.X >= 0d
            ? horizontal.Direction
            : horizontal.Direction.Negate();
        var upDirection = vertical.Direction.Y <= 0d
            ? vertical.Direction
            : vertical.Direction.Negate();

        var horizontalProjections = Endpoints(horizontal.Line)
            .Select(point => Project(point, intersection, xDirection))
            .ToArray();
        var verticalProjections = Endpoints(vertical.Line)
            .Select(point => Project(point, intersection, upDirection))
            .ToArray();
        var rightDistance = Math.Min(
            horizontalProjections.Max(),
            MaximumRayDistanceInsideFrame(intersection, xDirection, imageWidth, imageHeight));
        var leftExtension = Math.Max(0d, -horizontalProjections.Min());
        var topDistance = Math.Min(
            verticalProjections.Max(),
            MaximumRayDistanceInsideFrame(intersection, upDirection, imageWidth, imageHeight));
        var topLeft = Add(intersection, upDirection, topDistance);
        rightDistance = Math.Min(
            rightDistance,
            MaximumRayDistanceInsideFrame(topLeft, xDirection, imageWidth, imageHeight));
        var bottomRight = Add(intersection, xDirection, rightDistance);
        topDistance = Math.Min(
            topDistance,
            MaximumRayDistanceInsideFrame(bottomRight, upDirection, imageWidth, imageHeight));
        var bottomExtension = Math.Max(0d, -verticalProjections.Min());

        var horizontalSpanScore = Clamp01(horizontal.Span / (imageWidth * 0.75d));
        var verticalSpanScore = Clamp01(vertical.Span / (imageHeight * 0.75d));
        var meetingScore = 1d - Clamp01(
            ((leftExtension / Math.Max(1d, rightDistance)) +
             (bottomExtension / Math.Max(1d, topDistance))) / 2d);
        var positionScore = (
            Clamp01(1d - (intersection.X / imageWidth)) +
            Clamp01(intersection.Y / imageHeight)) / 2d;
        var perpendicularScore = 1d - Math.Abs(Dot(xDirection, upDirection));
        var coverageScore = (horizontal.CoverageFraction + vertical.CoverageFraction) / 2d;
        var strengthScore = (MeanStrength(horizontal) + MeanStrength(vertical)) / 2d;

        var score = Clamp01(
            (0.22d * ((horizontalSpanScore + verticalSpanScore) / 2d)) +
            (0.24d * meetingScore) +
            (0.18d * positionScore) +
            (0.12d * perpendicularScore) +
            (0.10d * coverageScore) +
            (0.14d * strengthScore));

        return new AxisPair(
            horizontal,
            vertical,
            intersection,
            xDirection,
            upDirection,
            rightDistance,
            topDistance,
            score);
    }

    private static PlotPolygon CreatePlotPolygon(AxisPair pair)
    {
        var bottomRight = Add(pair.Intersection, pair.XDirection, pair.RightDistance);
        var topLeft = Add(pair.Intersection, pair.UpDirection, pair.TopDistance);
        var topRight = new PixelPoint(
            bottomRight.X + topLeft.X - pair.Intersection.X,
            bottomRight.Y + topLeft.Y - pair.Intersection.Y);
        return new PlotPolygon(pair.Intersection, bottomRight, topRight, topLeft);
    }

    private static List<LineFamily> FindDividerFamilies(
        IReadOnlyList<LineFamily> verticalFamilies,
        AxisPair pair,
        IReadOnlySet<LineFamily> frameFamilies,
        double plotWidth,
        double plotHeight,
        AxisGeometryOptions options,
        CancellationToken cancellationToken)
    {
        var result = new List<LineFamily>();
        foreach (var family in verticalFamilies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(family, pair.Vertical) || frameFamilies.Contains(family))
            {
                continue;
            }

            var horizontalPosition = Project(family.Midpoint, pair.Intersection, pair.XDirection);
            var normalizedPosition = horizontalPosition / Math.Max(1d, plotWidth);
            if (normalizedPosition <= options.PlotEdgeExclusionFraction ||
                normalizedPosition >= 1d - options.PlotEdgeExclusionFraction)
            {
                continue;
            }

            var clippedSpan = VerticalPlotSpan(family, pair, plotHeight);
            var spanFraction = clippedSpan / Math.Max(1d, plotHeight);
            if (spanFraction < options.DividerMinimumSpanFraction)
            {
                continue;
            }

            var hasDottedEvidence = family.Members.Count(member =>
                member.Candidate.PatternHint == LinePatternHint.Dotted) >=
                options.DottedDividerMinimumSegments;
            var qualifiesByCoverage = family.CoverageFraction >= options.DividerMinimumCoverageFraction;
            var qualifiesSolid = family.Members.Any(member =>
                member.Candidate.PatternHint == LinePatternHint.Solid &&
                member.Candidate.Segment.Length / Math.Max(1d, plotHeight) >=
                options.DividerMinimumSpanFraction);

            if (hasDottedEvidence || qualifiesByCoverage || qualifiesSolid)
            {
                result.Add(family);
            }
        }

        return result;
    }

    private static HashSet<LineFamily> FindRegularGridFamilies(
        IReadOnlyList<LineFamily> candidates,
        AxisPair pair,
        AxisGeometryOptions options)
    {
        var fullSolid = candidates
            .Where(family =>
                family.CoverageFraction >= 0.85d &&
                family.Members.All(member => member.Candidate.PatternHint is
                    LinePatternHint.Solid or LinePatternHint.Unknown))
            .OrderBy(family => Project(family.Midpoint, pair.Intersection, pair.XDirection))
            .ToArray();

        if (fullSolid.Length < options.GridMinimumAlignedLines)
        {
            return [];
        }

        var result = new HashSet<LineFamily>();
        for (var start = 0; start <= fullSolid.Length - options.GridMinimumAlignedLines; start++)
        {
            for (var end = start + options.GridMinimumAlignedLines; end <= fullSolid.Length; end++)
            {
                var run = fullSolid[start..end];
                var positions = run
                    .Select(family => Project(family.Midpoint, pair.Intersection, pair.XDirection))
                    .ToArray();
                var gaps = positions.Zip(positions.Skip(1), (left, right) => right - left).ToArray();
                var mean = gaps.Average();
                if (mean <= 0d)
                {
                    continue;
                }

                var variance = gaps.Sum(gap => (gap - mean) * (gap - mean)) / gaps.Length;
                var coefficientOfVariation = Math.Sqrt(variance) / mean;
                if (coefficientOfVariation <= options.GridSpacingCoefficientOfVariation)
                {
                    foreach (var family in run)
                    {
                        result.Add(family);
                    }
                }
            }
        }

        return result;
    }

    private static List<PhaseDividerGeometry> CreateDividers(
        IReadOnlyList<LineFamily> families,
        AxisPair pair,
        double plotHeight,
        AxisGeometryOptions options)
    {
        var ordered = families
            .OrderBy(family => Project(family.Midpoint, pair.Intersection, pair.XDirection))
            .ToArray();
        var result = new List<PhaseDividerGeometry>(ordered.Length);

        for (var index = 0; index < ordered.Length; index++)
        {
            var family = ordered[index];
            var style = ClassifyDividerStyle(family, plotHeight, options);
            HashSet<string> alignedTickIds = new(StringComparer.Ordinal);
            if (style is DividerStyle.Dotted or DividerStyle.Dashed)
            {
                alignedTickIds = family.Members
                    .Where(member =>
                        member.Candidate.PatternHint is not LinePatternHint.Dotted and
                            not LinePatternHint.Dashed &&
                        IsAlignedXAxisTick(member.Candidate.Segment, pair.Horizontal.Line, plotHeight, options))
                    .Select(member => member.Candidate.CandidateId)
                    .ToHashSet(StringComparer.Ordinal);
            }

            var selectedMembers = family.Members
                .Where(member => !alignedTickIds.Contains(member.Candidate.CandidateId))
                .ToArray();

            var selectedFamily = selectedMembers.Length == family.Members.Count
                ? family
                : FitFamily(selectedMembers, Orientation.Vertical);
            var line = ClipVerticalFamilyToPlot(selectedFamily, pair, plotHeight);
            var spanFraction = VerticalPlotSpan(selectedFamily, pair, plotHeight) / Math.Max(1d, plotHeight);
            var patternConfidence = style == DividerStyle.Unknown ? 0.45d : 0.9d;
            var confidence = Clamp01(
                (0.35d * spanFraction) +
                (0.30d * selectedFamily.CoverageFraction) +
                (0.20d * MeanStrength(selectedFamily)) +
                (0.15d * patternConfidence));

            result.Add(new PhaseDividerGeometry(
                $"divider-{index + 1:D3}",
                line,
                style,
                confidence,
                Clamp01(spanFraction),
                selectedFamily.CoverageFraction,
                selectedMembers
                    .Select(member => member.Candidate.CandidateId)
                    .Order(StringComparer.Ordinal)
                    .ToArray()));
        }

        return result;
    }

    private static List<AmbiguousGridOrDividerGeometry> CreateAmbiguousGridOrDividers(
        IReadOnlySet<LineFamily> families,
        AxisPair pair,
        double plotHeight)
    {
        var ordered = families
            .OrderBy(family => Project(family.Midpoint, pair.Intersection, pair.XDirection))
            .ToArray();
        var result = new List<AmbiguousGridOrDividerGeometry>(ordered.Length);

        for (var index = 0; index < ordered.Length; index++)
        {
            var family = ordered[index];
            var spanFraction = Clamp01(
                VerticalPlotSpan(family, pair, plotHeight) / Math.Max(1d, plotHeight));
            var confidence = Clamp01(
                (0.40d * spanFraction) +
                (0.30d * family.CoverageFraction) +
                (0.20d * MeanStrength(family)) +
                (0.10d / (1d + family.RootMeanSquareError)));
            result.Add(new AmbiguousGridOrDividerGeometry(
                $"grid-or-divider-{index + 1:D3}",
                ClipVerticalFamilyToPlot(family, pair, plotHeight),
                confidence,
                spanFraction,
                family.CoverageFraction,
                family.Members
                    .Select(member => member.Candidate.CandidateId)
                    .Order(StringComparer.Ordinal)
                    .ToArray()));
        }

        return result;
    }

    private static bool IsAlignedXAxisTick(
        GeometryLineSegment segment,
        GeometryLineSegment xAxis,
        double plotHeight,
        AxisGeometryOptions options)
    {
        if (segment.Length > plotHeight * options.TickMaximumLengthFraction)
        {
            return false;
        }

        var midpointDistance = DistanceToInfiniteLine(segment.Midpoint, xAxis);
        return SegmentCrossesInfiniteLine(segment, xAxis) ||
            midpointDistance <= options.TickAxisDistancePixels;
    }

    private static bool SegmentCrossesInfiniteLine(
        GeometryLineSegment segment,
        GeometryLineSegment line)
    {
        var direction = new Vector2(line.End.X - line.Start.X, line.End.Y - line.Start.Y);
        var startSide = Cross(
            direction,
            new Vector2(segment.Start.X - line.Start.X, segment.Start.Y - line.Start.Y));
        var endSide = Cross(
            direction,
            new Vector2(segment.End.X - line.Start.X, segment.End.Y - line.Start.Y));
        return startSide * endSide < -1e-9d;
    }

    private static List<AxisTickGeometry> CreateTicks(
        IReadOnlyList<Observation> observations,
        AxisPair pair,
        double plotWidth,
        double plotHeight,
        AxisGeometryOptions options,
        HashSet<string> dividerCandidateIds,
        CancellationToken cancellationToken)
    {
        var axisIds = pair.Horizontal.Members
            .Concat(pair.Vertical.Members)
            .Select(member => member.Candidate.CandidateId)
            .ToHashSet(StringComparer.Ordinal);
        var ticks = new List<TickCandidate>();

        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = observation.Candidate;
            var isLongAxisMember = axisIds.Contains(candidate.CandidateId) &&
                candidate.Segment.Length > (observation.Orientation == Orientation.Vertical
                    ? plotHeight * options.TickMaximumLengthFraction
                    : plotWidth * options.TickMaximumLengthFraction);
            if (isLongAxisMember || dividerCandidateIds.Contains(candidate.CandidateId))
            {
                continue;
            }

            var midpoint = candidate.Segment.Midpoint;
            if (observation.Orientation == Orientation.Vertical &&
                candidate.Segment.Length <= plotHeight * options.TickMaximumLengthFraction &&
                DistanceToInfiniteLine(midpoint, pair.Horizontal.Line) <=
                options.TickAxisDistancePixels + (candidate.Segment.Length / 2d) &&
                IsWithinProjection(midpoint, pair.Intersection, pair.XDirection, plotWidth, options.MergeDistancePixels))
            {
                var position = Project(midpoint, pair.Intersection, pair.XDirection);
                ticks.Add(new TickCandidate(observation, position, new AxisTickGeometry(
                    candidate.CandidateId,
                    TickAxis.XAxis,
                    midpoint,
                    candidate.Segment,
                    TickConfidence(candidate, pair.Horizontal.Line),
                    new[] { candidate.CandidateId })));
            }
            else if (observation.Orientation == Orientation.Horizontal &&
                     candidate.Segment.Length <= plotWidth * options.TickMaximumLengthFraction &&
                     DistanceToInfiniteLine(midpoint, pair.Vertical.Line) <=
                     options.TickAxisDistancePixels + (candidate.Segment.Length / 2d) &&
                     IsWithinProjection(midpoint, pair.Intersection, pair.UpDirection, plotHeight, options.MergeDistancePixels))
            {
                var position = Project(midpoint, pair.Intersection, pair.UpDirection);
                ticks.Add(new TickCandidate(observation, position, new AxisTickGeometry(
                    candidate.CandidateId,
                    TickAxis.YAxis,
                    midpoint,
                    candidate.Segment,
                    TickConfidence(candidate, pair.Vertical.Line),
                    new[] { candidate.CandidateId })));
            }
        }

        return ConsolidateTicks(ticks, options.MergeDistancePixels);
    }

    private static List<AxisTickGeometry> ConsolidateTicks(
        IReadOnlyList<TickCandidate> candidates,
        double mergeDistancePixels)
    {
        var result = new List<(TickAxis Axis, double Position, AxisTickGeometry Tick)>();
        foreach (var axisGroup in candidates.GroupBy(candidate => candidate.Tick.Axis))
        {
            var ordered = axisGroup.OrderBy(candidate => candidate.Position).ToArray();
            var cluster = new List<TickCandidate>();
            double clusterPosition = 0d;

            foreach (var candidate in ordered)
            {
                if (cluster.Count == 0 ||
                    Math.Abs(candidate.Position - clusterPosition) <= mergeDistancePixels)
                {
                    cluster.Add(candidate);
                    clusterPosition = cluster.Average(item => item.Position);
                    continue;
                }

                result.Add(CreateConsolidatedTick(cluster));
                cluster.Clear();
                cluster.Add(candidate);
                clusterPosition = candidate.Position;
            }

            if (cluster.Count > 0)
            {
                result.Add(CreateConsolidatedTick(cluster));
            }
        }

        return result
            .OrderBy(item => item.Axis)
            .ThenBy(item => item.Position)
            .Select(item => item.Tick)
            .ToList();
    }

    private static (TickAxis Axis, double Position, AxisTickGeometry Tick) CreateConsolidatedTick(
        IReadOnlyList<TickCandidate> cluster)
    {
        var representative = cluster
            .OrderByDescending(candidate =>
                candidate.Observation.Candidate.Source == LineCandidateSource.RecordedFixture)
            .ThenByDescending(candidate => candidate.Tick.Confidence)
            .ThenBy(candidate => candidate.Tick.TickId, StringComparer.Ordinal)
            .First();
        var totalWeight = cluster.Sum(candidate => candidate.Tick.Confidence);
        if (totalWeight <= 1e-9d)
        {
            totalWeight = cluster.Count;
        }

        var center = new PixelPoint(
            cluster.Sum(candidate => candidate.Tick.Center.X * Math.Max(candidate.Tick.Confidence, 1e-9d)) /
                totalWeight,
            cluster.Sum(candidate => candidate.Tick.Center.Y * Math.Max(candidate.Tick.Confidence, 1e-9d)) /
                totalWeight);
        var representativeMidpoint = representative.Tick.Line.Midpoint;
        var deltaX = center.X - representativeMidpoint.X;
        var deltaY = center.Y - representativeMidpoint.Y;
        var line = new GeometryLineSegment(
            new PixelPoint(
                representative.Tick.Line.Start.X + deltaX,
                representative.Tick.Line.Start.Y + deltaY),
            new PixelPoint(
                representative.Tick.Line.End.X + deltaX,
                representative.Tick.Line.End.Y + deltaY));
        var confidence = Clamp01(cluster.Average(candidate => candidate.Tick.Confidence));
        var supportingIds = cluster
            .SelectMany(candidate => candidate.Tick.SupportingCandidateIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var tick = new AxisTickGeometry(
            representative.Tick.TickId,
            representative.Tick.Axis,
            center,
            line,
            confidence,
            supportingIds);

        return (
            representative.Tick.Axis,
            cluster.Average(candidate => candidate.Position),
            tick);
    }

    private static AxisLineFit CreateAxisFit(
        LineFamily family,
        GeometryLineSegment line,
        double confidence) =>
        new(
            line,
            confidence,
            family.RootMeanSquareError,
            family.CoverageFraction,
            family.Members
                .Select(member => member.Candidate.CandidateId)
                .Order(StringComparer.Ordinal)
                .ToArray());

    private static DividerStyle ClassifyDividerStyle(
        LineFamily family,
        double plotHeight,
        AxisGeometryOptions options)
    {
        var dottedCount = family.Members.Count(member =>
            member.Candidate.PatternHint == LinePatternHint.Dotted);
        if (dottedCount >= options.DottedDividerMinimumSegments)
        {
            return DividerStyle.Dotted;
        }

        if (family.Members.Any(member => member.Candidate.PatternHint == LinePatternHint.Dashed))
        {
            return DividerStyle.Dashed;
        }

        var maximumSegmentFraction = family.Members.Max(member =>
            member.Candidate.Segment.Length / Math.Max(1d, plotHeight));
        if (family.Members.Count >= options.DottedDividerMinimumSegments &&
            maximumSegmentFraction <= options.DottedDividerMaximumSegmentFraction)
        {
            return DividerStyle.Dotted;
        }

        if (family.CoverageFraction < 0.8d && family.Members.Count >= 2)
        {
            return DividerStyle.Dashed;
        }

        return family.CoverageFraction >= 0.8d ? DividerStyle.Solid : DividerStyle.Unknown;
    }

    private static GeometryLineSegment ClipVerticalFamilyToPlot(
        LineFamily family,
        AxisPair pair,
        double plotHeight)
    {
        var bottom = IntersectWithParallelThrough(family, pair.Intersection, pair.XDirection);
        var topAxisPoint = Add(pair.Intersection, pair.UpDirection, plotHeight);
        var top = IntersectWithParallelThrough(family, topAxisPoint, pair.XDirection);
        return new GeometryLineSegment(bottom, top);
    }

    private static double VerticalPlotSpan(LineFamily family, AxisPair pair, double plotHeight)
    {
        var projections = Endpoints(family.Line)
            .Select(point => Project(point, pair.Intersection, pair.UpDirection))
            .ToArray();
        var minimum = Math.Max(0d, projections.Min());
        var maximum = Math.Min(plotHeight, projections.Max());
        return Math.Max(0d, maximum - minimum);
    }

    private static PixelPoint IntersectWithParallelThrough(
        LineFamily verticalFamily,
        PixelPoint parallelOrigin,
        Vector2 parallelDirection)
    {
        var verticalOrigin = verticalFamily.Line.Start;
        var verticalDirection = verticalFamily.Direction;
        var denominator = Cross(verticalDirection, parallelDirection);
        if (Math.Abs(denominator) <= 1e-9d)
        {
            return verticalFamily.Line.Midpoint;
        }

        var delta = new Vector2(
            parallelOrigin.X - verticalOrigin.X,
            parallelOrigin.Y - verticalOrigin.Y);
        var alongVertical = Cross(delta, parallelDirection) / denominator;
        return Add(verticalOrigin, verticalDirection, alongVertical);
    }

    private static bool TryIntersect(
        LineFamily horizontal,
        LineFamily vertical,
        out PixelPoint intersection)
    {
        var denominator = 1d - (vertical.Slope * horizontal.Slope);
        if (Math.Abs(denominator) <= 1e-9d)
        {
            intersection = default;
            return false;
        }

        var x = ((vertical.Slope * horizontal.Intercept) + vertical.Intercept) / denominator;
        var y = (horizontal.Slope * x) + horizontal.Intercept;
        intersection = new PixelPoint(x, y);
        return intersection.IsFinite;
    }

    private static double AxisConfidence(
        LineFamily family,
        double pairScore,
        int imageExtent,
        double minimumSpanFraction)
    {
        var spanScore = Clamp01(family.Span / Math.Max(1d, imageExtent * minimumSpanFraction));
        var errorScore = 1d / (1d + family.RootMeanSquareError);
        return Clamp01(
            (0.35d * spanScore) +
            (0.20d * family.CoverageFraction) +
            (0.20d * MeanStrength(family)) +
            (0.15d * errorScore) +
            (0.10d * pairScore));
    }

    private static double TickConfidence(
        GeometryLineCandidate candidate,
        GeometryLineSegment axis)
    {
        var distance = DistanceToInfiniteLine(candidate.Segment.Midpoint, axis);
        return Clamp01((0.7d * candidate.Strength) + (0.3d / (1d + distance)));
    }

    private static double MeanStrength(LineFamily family) =>
        family.Members.Average(member => member.Candidate.Strength);

    private static double MemberWeight(Observation observation) =>
        Math.Sqrt(observation.Candidate.Segment.Length) * observation.Candidate.Strength;

    private static double AngleDifferenceDegrees(Observation left, Observation right) =>
        Math.Abs(left.SignedAngleDegrees - right.SignedAngleDegrees);

    private static double AngleDifferenceDegrees(Observation observation, double fittedAngleDegrees) =>
        Math.Abs(observation.SignedAngleDegrees - fittedAngleDegrees);

    private static bool SegmentFitsLine(
        GeometryLineSegment candidate,
        GeometryLineSegment hypothesis,
        double tolerance) =>
        DistanceToInfiniteLine(candidate.Start, hypothesis) <= tolerance &&
        DistanceToInfiniteLine(candidate.End, hypothesis) <= tolerance;

    private static double DistanceToFittedLine(
        PixelPoint point,
        double slope,
        double intercept,
        Orientation orientation)
    {
        var numerator = orientation == Orientation.Horizontal
            ? Math.Abs(point.Y - (slope * point.X) - intercept)
            : Math.Abs(point.X - (slope * point.Y) - intercept);
        return numerator / Math.Sqrt(1d + (slope * slope));
    }

    private static double DistanceToInfiniteLine(PixelPoint point, GeometryLineSegment line)
    {
        var direction = new Vector2(line.End.X - line.Start.X, line.End.Y - line.Start.Y);
        var length = direction.Length;
        if (length <= 1e-9d)
        {
            return Distance(point, line.Start);
        }

        var relative = new Vector2(point.X - line.Start.X, point.Y - line.Start.Y);
        return Math.Abs(Cross(relative, direction)) / length;
    }

    private static double UnionLength(List<(double Start, double End)> intervals)
    {
        var total = 0d;
        var currentStart = intervals[0].Start;
        var currentEnd = intervals[0].End;
        for (var index = 1; index < intervals.Count; index++)
        {
            var interval = intervals[index];
            if (interval.Start <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, interval.End);
            }
            else
            {
                total += currentEnd - currentStart;
                currentStart = interval.Start;
                currentEnd = interval.End;
            }
        }

        return total + currentEnd - currentStart;
    }

    private static bool IsInside(PixelPoint point, int width, int height) =>
        point.X >= 0d && point.X <= width - 1d && point.Y >= 0d && point.Y <= height - 1d;

    private static bool IsInsideWithTolerance(
        PixelPoint point,
        int width,
        int height,
        double tolerance) =>
        point.X >= -tolerance && point.X <= width - 1d + tolerance &&
        point.Y >= -tolerance && point.Y <= height - 1d + tolerance;

    private static bool IsWithinProjection(
        PixelPoint point,
        PixelPoint origin,
        Vector2 direction,
        double extent,
        double tolerance)
    {
        var projection = Project(point, origin, direction);
        return projection >= -tolerance && projection <= extent + tolerance;
    }

    private static bool IsFraction(double value) =>
        double.IsFinite(value) && value > 0d && value < 1d;

    private static double Distance(PixelPoint left, PixelPoint right)
    {
        var deltaX = right.X - left.X;
        var deltaY = right.Y - left.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }

    private static double Project(PixelPoint point, PixelPoint origin, Vector2 direction) =>
        ((point.X - origin.X) * direction.X) + ((point.Y - origin.Y) * direction.Y);

    private static PixelPoint Add(PixelPoint point, Vector2 direction, double distance) =>
        new(point.X + (direction.X * distance), point.Y + (direction.Y * distance));

    private static double MaximumRayDistanceInsideFrame(
        PixelPoint origin,
        Vector2 direction,
        int imageWidth,
        int imageHeight)
    {
        var maximum = double.PositiveInfinity;
        Constrain(origin.X, direction.X, imageWidth - 1d);
        Constrain(origin.Y, direction.Y, imageHeight - 1d);
        return Math.Max(0d, maximum);

        void Constrain(double coordinate, double component, double upperBound)
        {
            if (component > 1e-12d)
            {
                maximum = Math.Min(maximum, (upperBound - coordinate) / component);
            }
            else if (component < -1e-12d)
            {
                maximum = Math.Min(maximum, -coordinate / component);
            }
        }
    }

    private static IEnumerable<PixelPoint> Endpoints(GeometryLineSegment line)
    {
        yield return line.Start;
        yield return line.End;
    }

    private static Vector2 Normalize(Vector2 vector)
    {
        var length = vector.Length;
        return length <= 1e-12d ? new Vector2(1d, 0d) : new Vector2(vector.X / length, vector.Y / length);
    }

    private static double Dot(Vector2 left, Vector2 right) =>
        (left.X * right.X) + (left.Y * right.Y);

    private static double Cross(Vector2 left, Vector2 right) =>
        (left.X * right.Y) - (left.Y * right.X);

    private static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);

    private static AxisGeometryDetectionException DetectionFailure(string technicalMessage) =>
        new(technicalMessage);

    private enum Orientation
    {
        Horizontal,
        Vertical,
    }

    private sealed record Observation(
        GeometryLineCandidate Candidate,
        Orientation Orientation,
        double SignedAngleDegrees);

    private sealed record LineFamily(
        Orientation Orientation,
        double Slope,
        double Intercept,
        Vector2 Direction,
        GeometryLineSegment Line,
        IReadOnlyList<Observation> Members,
        double Span,
        double CoverageFraction,
        double RootMeanSquareError)
    {
        public PixelPoint Midpoint => Line.Midpoint;

        public double AngleDegrees => Math.Atan(Slope) * 180d / Math.PI;

        public double Distance(PixelPoint point) =>
            DistanceToFittedLine(point, Slope, Intercept, Orientation);

        public bool SegmentFits(GeometryLineSegment segment, double tolerance) =>
            Distance(segment.Start) <= tolerance && Distance(segment.End) <= tolerance;
    }

    private sealed record AxisPair(
        LineFamily Horizontal,
        LineFamily Vertical,
        PixelPoint Intersection,
        Vector2 XDirection,
        Vector2 UpDirection,
        double RightDistance,
        double TopDistance,
        double Score);

    private sealed record TickCandidate(
        Observation Observation,
        double Position,
        AxisTickGeometry Tick);

    private readonly record struct Vector2(double X, double Y)
    {
        public double Length => Math.Sqrt((X * X) + (Y * Y));

        public Vector2 Negate() => new(-X, -Y);
    }
}
