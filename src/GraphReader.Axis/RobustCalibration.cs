// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;

namespace GraphReader.Axis;

public static class RobustCalibration
{
    private const double MinimumSlopeMagnitude = 1e-12;

    public static LinearTransformFitResult FitY(
        IReadOnlyList<NumericTickEvidence> ticks,
        RobustFitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LinearTransformFitResult fit = Fit(
            ticks,
            static tick => new FitSample(
                tick.Id,
                tick.PixelPosition,
                tick.Value,
                tick.Confidence,
                tick.CoordinateSpace),
            options,
            "y",
            cancellationToken);
        return ValidateDirection(
            fit,
            expectedPositiveSlope: false,
            "Y-axis graph values must increase upward while original-pixel y coordinates increase downward.");
    }

    public static LinearTransformFitResult FitX(
        IReadOnlyList<PrintedXTickEvidence> ticks,
        RobustFitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LinearTransformFitResult fit = Fit(
            ticks,
            static tick => new FitSample(
                tick.Id,
                tick.PixelX,
                tick.PrintedValue,
                tick.Confidence,
                tick.CoordinateSpace),
            options,
            "x",
            cancellationToken);
        return ValidateDirection(
            fit,
            expectedPositiveSlope: true,
            "Printed session values must increase from left to right in original pixels.");
    }

    public static SessionFirstCalibrationResult FitSessionFirst(
        SessionFirstCalibrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Lattice);
        cancellationToken.ThrowIfCancellationRequested();

        Stopwatch stopwatch = Stopwatch.StartNew();
        LinearTransformFitResult yFit = FitY(
            request.YTicks,
            request.FitOptions,
            cancellationToken);

        PrintedXTickEvidence[] printedTicks = MergePrintedTicks(
            request.PrintedXTicks,
            request.Lattice.PrintedTicks,
            cancellationToken);
        LinearTransformFitResult? xFit = printedTicks.Length >= 2
            ? FitX(printedTicks, request.FitOptions, cancellationToken)
            : null;
        PrintedXTickEvidence[] acceptedXTicks = printedTicks;
        if (xFit is { IsValid: true })
        {
            HashSet<string> inlierIds = xFit.Diagnostics.InlierIds.ToHashSet(StringComparer.Ordinal);
            acceptedXTicks = printedTicks.Where(tick => inlierIds.Contains(tick.Id)).ToArray();
        }

        SessionLatticeRequest latticeRequest = request.Lattice with
        {
            PrintedTicks = acceptedXTicks,
        };
        SessionLatticeResult lattice = SessionLattice.Fit(latticeRequest, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        List<string> reasons = [];
        reasons.AddRange(yFit.Reasons);
        reasons.AddRange(lattice.Reasons);
        if (xFit is { Validity: not CalibrationValidity.Valid })
        {
            reasons.AddRange(xFit.Reasons);
        }

        List<CalibrationAnchor> anchors = [];
        if (yFit.Transform is { } yTransform && lattice.Session1PixelX is { } session1PixelX)
        {
            double? yMaximum = ResolveYMaximum(request, yFit);
            double? xMaximum = ResolveXMaximum(request, acceptedXTicks, lattice);
            double y0Pixel = yTransform.GraphToPixel(0d);
            double? exactSessionOne = lattice.HasAbsoluteSessionOrigin ? 1d : null;

            anchors.Add(new CalibrationAnchor(
                CalibrationAnchorKind.Session1Y0,
                new PixelPoint(session1PixelX, y0Pixel),
                exactSessionOne,
                0d,
                Math.Min(yFit.Confidence, lattice.Confidence),
                lattice.HasAbsoluteSessionOrigin));
            if (yMaximum is { } maximumY)
            {
                anchors.Add(new CalibrationAnchor(
                    CalibrationAnchorKind.Session1YMaximum,
                    new PixelPoint(session1PixelX, yTransform.GraphToPixel(maximumY)),
                    exactSessionOne,
                    maximumY,
                    Math.Min(yFit.Confidence, lattice.Confidence),
                    lattice.HasAbsoluteSessionOrigin));
            }
            else
            {
                reasons.Add("No positive y-axis maximum is supported by the accepted numeric ticks.");
            }

            if (xMaximum is { } maximum && TryResolvePixelX(maximum, xFit, lattice, out double maximumPixelX))
            {
                bool exactMaximum = request.XMaximum.HasValue ||
                    acceptedXTicks.Any(tick => NearlyEqual(tick.PrintedValue, maximum));
                anchors.Add(new CalibrationAnchor(
                    CalibrationAnchorKind.SessionMaximumY0,
                    new PixelPoint(maximumPixelX, y0Pixel),
                    maximum,
                    0d,
                    Math.Min(yFit.Confidence, lattice.Confidence),
                    exactMaximum));
            }
            else
            {
                reasons.Add("The final printed session is unavailable, so the sessionmax_y0 anchor was not invented.");
            }
        }

        CalibrationValidity validity = CombineValidity(yFit.Validity, xFit?.Validity, lattice.Validity);
        if (anchors.Count < 3 && validity == CalibrationValidity.Valid)
        {
            validity = CalibrationValidity.NeedsReview;
        }

        if (lattice.IsOrdinalOnly)
        {
            reasons.Add("Only ordinal x evidence is available; exact printed and estimated x values remain null.");
        }

        stopwatch.Stop();
        double confidence = Math.Clamp(
            Math.Min(yFit.Confidence, lattice.Confidence),
            0d,
            1d);
        return new SessionFirstCalibrationResult(
            yFit,
            xFit,
            lattice,
            anchors.AsReadOnly(),
            validity,
            DistinctReasons(reasons),
            confidence,
            stopwatch.Elapsed);
    }

    private static LinearTransformFitResult Fit<T>(
        IReadOnlyList<T> evidence,
        Func<T, FitSample> convert,
        RobustFitOptions? options,
        string axisName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(convert);
        RobustFitOptions actualOptions = options ?? new RobustFitOptions();
        ValidateOptions(actualOptions);

        Stopwatch stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        List<FitSample> samples = [];
        List<string> invalidIds = [];
        for (int i = 0; i < evidence.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FitSample sample = convert(evidence[i]);
            if (string.IsNullOrWhiteSpace(sample.Id))
            {
                throw new ArgumentException("Numeric tick evidence identifiers cannot be empty.", nameof(evidence));
            }

            if (!double.IsFinite(sample.Pixel) || !double.IsFinite(sample.Value))
            {
                throw new ArgumentException("Numeric tick pixel positions and values must be finite.", nameof(evidence));
            }

            if (!double.IsFinite(sample.Weight) || sample.Weight <= 0d || sample.Weight > 1d)
            {
                throw new ArgumentException("Numeric tick confidence must be finite and in (0, 1].", nameof(evidence));
            }

            if (!string.Equals(
                sample.CoordinateSpace,
                AxisGeometryCoordinateSpaces.OriginalPixels,
                StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Calibration tick evidence must be normalized to original_pixels.",
                    nameof(evidence));
            }

            samples.Add(sample);
        }

        samples.Sort(CompareSamples);

        if (samples.Count < 2)
        {
            stopwatch.Stop();
            return InsufficientFit(
                evidence.Count,
                invalidIds,
                $"At least two finite {axisName}-axis numeric ticks are required.",
                stopwatch.Elapsed);
        }

        CandidateFit? best = null;
        Dictionary<string, CandidateFit> hypothesesBySupport = new(StringComparer.Ordinal);
        for (int left = 0; left < samples.Count - 1; left++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int right = left + 1; right < samples.Count; right++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double pixelDifference = samples[right].Pixel - samples[left].Pixel;
                if (Math.Abs(pixelDifference) <= MinimumSlopeMagnitude)
                {
                    continue;
                }

                double slope = (samples[right].Value - samples[left].Value) / pixelDifference;
                if (!double.IsFinite(slope) || Math.Abs(slope) <= MinimumSlopeMagnitude)
                {
                    continue;
                }

                double intercept = samples[left].Value - (slope * samples[left].Pixel);
                CandidateFit candidate = ScoreCandidate(
                    samples,
                    slope,
                    intercept,
                    actualOptions.InlierTolerancePixels,
                    cancellationToken);
                string supportKey = CreateSupportKey(candidate.InlierIndices);
                if (!hypothesesBySupport.TryGetValue(supportKey, out CandidateFit existing) ||
                    IsBetter(candidate, existing))
                {
                    hypothesesBySupport[supportKey] = candidate;
                }

                if (best is null || IsBetter(candidate, best.Value))
                {
                    best = candidate;
                }
            }
        }

        if (best is null || best.Value.InlierIndices.Count < 2)
        {
            stopwatch.Stop();
            return InsufficientFit(
                evidence.Count,
                invalidIds,
                $"The {axisName}-axis ticks do not define a non-degenerate numeric transform.",
                stopwatch.Elapsed);
        }

        CandidateFit refined = best.Value;
        for (int iteration = 0; iteration < actualOptions.MaximumRefinementIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryWeightedLeastSquares(samples, refined.InlierIndices, out double slope, out double intercept))
            {
                break;
            }

            CandidateFit next = ScoreCandidate(
                samples,
                slope,
                intercept,
                actualOptions.InlierTolerancePixels,
                cancellationToken);
            bool unchanged = next.InlierIndices.SequenceEqual(refined.InlierIndices);
            refined = next;
            if (unchanged)
            {
                break;
            }
        }

        double totalWeight = samples.Sum(static sample => sample.Weight);
        double inlierFraction = totalWeight <= 0d ? 0d : refined.InlierWeight / totalWeight;
        bool hasStrictMajority = inlierFraction > 0.5d;
        bool hasCompetingModel = HasCompetingModel(
            samples,
            hypothesesBySupport.Values,
            refined,
            actualOptions.InlierTolerancePixels,
            cancellationToken);
        CalibrationValidity validity = refined.InlierIndices.Count >= 2 &&
            hasStrictMajority &&
            !hasCompetingModel &&
            inlierFraction >= actualOptions.MinimumInlierWeightFraction
                ? CalibrationValidity.Valid
                : CalibrationValidity.NeedsReview;

        HashSet<int> inlierSet = refined.InlierIndices.ToHashSet();
        List<string> inlierIds = [];
        List<string> outlierIds = [.. invalidIds];
        for (int i = 0; i < samples.Count; i++)
        {
            (inlierSet.Contains(i) ? inlierIds : outlierIds).Add(samples[i].Id);
        }

        List<string> warnings = [];
        if (outlierIds.Count > 0)
        {
            warnings.Add($"Rejected {outlierIds.Count} invalid or outlying {axisName}-tick values.");
        }

        if (validity != CalibrationValidity.Valid)
        {
            warnings.Add(!hasStrictMajority
                ? "Competing transforms are ambiguous because no fit has a strict majority of evidence weight."
                : hasCompetingModel
                    ? "Competing alternative transforms have equal support and materially different calibration predictions."
                    : "The robust inlier weight is below the configured acceptance threshold.");
        }

        double slopeStandardError = CalculateSlopeStandardError(samples, refined);
        bool extrapolatesToZero = ExtrapolatesToZero(samples, refined.InlierIndices);
        double confidence = Math.Clamp(
            inlierFraction * (1d / (1d + (refined.RootMeanSquarePixels / actualOptions.InlierTolerancePixels))),
            0d,
            1d);
        if (hasCompetingModel)
        {
            confidence = Math.Min(confidence, 0.35d);
        }

        stopwatch.Stop();

        IReadOnlyList<string> reasons = validity == CalibrationValidity.Valid
            ? Array.Empty<string>()
            : !hasStrictMajority
                ? ["Numeric tick evidence is ambiguous because no transform has a strict majority of evidence weight."]
                : hasCompetingModel
                    ? ["Numeric tick evidence is ambiguous because competing alternative models have equal support."]
                    : ["Numeric tick evidence requires review because reliable ticks do not meet the configured acceptance threshold."];
        return new LinearTransformFitResult(
            new LinearAxisTransform(refined.Slope, refined.Intercept),
            confidence,
            validity,
            reasons,
            new CalibrationUncertainty(
                refined.RootMeanSquarePixels,
                refined.MaximumResidualPixels,
                inlierFraction,
                slopeStandardError,
                extrapolatesToZero),
            new CalibrationDiagnostics(
                evidence.Count,
                refined.InlierIndices.Count,
                inlierIds.AsReadOnly(),
                outlierIds.AsReadOnly(),
                warnings.AsReadOnly(),
                stopwatch.Elapsed));
    }

    private static CandidateFit ScoreCandidate(
        IReadOnlyList<FitSample> samples,
        double slope,
        double intercept,
        double tolerancePixels,
        CancellationToken cancellationToken)
    {
        List<int> inliers = [];
        double inlierWeight = 0d;
        double weightedSquaredError = 0d;
        double maximumResidual = 0d;
        for (int i = 0; i < samples.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double predictedPixel = (samples[i].Value - intercept) / slope;
            double residual = Math.Abs(predictedPixel - samples[i].Pixel);
            if (residual <= tolerancePixels)
            {
                inliers.Add(i);
                inlierWeight += samples[i].Weight;
                weightedSquaredError += samples[i].Weight * residual * residual;
                maximumResidual = Math.Max(maximumResidual, residual);
            }
        }

        double rootMeanSquare = inlierWeight <= 0d
            ? double.PositiveInfinity
            : Math.Sqrt(weightedSquaredError / inlierWeight);
        return new CandidateFit(
            slope,
            intercept,
            inliers,
            inlierWeight,
            rootMeanSquare,
            maximumResidual);
    }

    private static bool TryWeightedLeastSquares(
        IReadOnlyList<FitSample> samples,
        IReadOnlyList<int> indices,
        out double slope,
        out double intercept)
    {
        double weightSum = 0d;
        double weightedPixel = 0d;
        double weightedValue = 0d;
        foreach (int index in indices)
        {
            FitSample sample = samples[index];
            weightSum += sample.Weight;
            weightedPixel += sample.Weight * sample.Pixel;
            weightedValue += sample.Weight * sample.Value;
        }

        if (weightSum <= 0d)
        {
            slope = 0d;
            intercept = 0d;
            return false;
        }

        double meanPixel = weightedPixel / weightSum;
        double meanValue = weightedValue / weightSum;
        double numerator = 0d;
        double denominator = 0d;
        foreach (int index in indices)
        {
            FitSample sample = samples[index];
            double pixelDelta = sample.Pixel - meanPixel;
            numerator += sample.Weight * pixelDelta * (sample.Value - meanValue);
            denominator += sample.Weight * pixelDelta * pixelDelta;
        }

        if (denominator <= MinimumSlopeMagnitude)
        {
            slope = 0d;
            intercept = 0d;
            return false;
        }

        slope = numerator / denominator;
        intercept = meanValue - (slope * meanPixel);
        return double.IsFinite(slope) && double.IsFinite(intercept) && Math.Abs(slope) > MinimumSlopeMagnitude;
    }

    private static bool IsBetter(CandidateFit candidate, CandidateFit incumbent)
    {
        const double epsilon = 1e-9;
        if (candidate.InlierWeight > incumbent.InlierWeight + epsilon)
        {
            return true;
        }

        if (Math.Abs(candidate.InlierWeight - incumbent.InlierWeight) <= epsilon &&
            candidate.InlierIndices.Count > incumbent.InlierIndices.Count)
        {
            return true;
        }

        return Math.Abs(candidate.InlierWeight - incumbent.InlierWeight) <= epsilon &&
            candidate.InlierIndices.Count == incumbent.InlierIndices.Count &&
            candidate.RootMeanSquarePixels < incumbent.RootMeanSquarePixels;
    }

    private static bool HasCompetingModel(
        IReadOnlyList<FitSample> samples,
        IEnumerable<CandidateFit> hypotheses,
        CandidateFit selected,
        double tolerancePixels,
        CancellationToken cancellationToken)
    {
        string selectedSupport = CreateSupportKey(selected.InlierIndices);
        foreach (CandidateFit hypothesis in hypotheses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CandidateFit alternative = CanonicalizeCandidate(
                samples,
                hypothesis,
                tolerancePixels,
                cancellationToken);
            if (string.Equals(
                    CreateSupportKey(alternative.InlierIndices),
                    selectedSupport,
                    StringComparison.Ordinal) ||
                !HasComparableQuality(alternative, selected, tolerancePixels) ||
                !PredictsMateriallyDifferentCalibration(
                    samples,
                    alternative,
                    selected,
                    tolerancePixels))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static CandidateFit CanonicalizeCandidate(
        IReadOnlyList<FitSample> samples,
        CandidateFit candidate,
        double tolerancePixels,
        CancellationToken cancellationToken)
    {
        CandidateFit canonical = candidate;
        for (int iteration = 0; iteration < 3; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryWeightedLeastSquares(
                samples,
                canonical.InlierIndices,
                out double slope,
                out double intercept))
            {
                break;
            }

            CandidateFit next = ScoreCandidate(
                samples,
                slope,
                intercept,
                tolerancePixels,
                cancellationToken);
            bool unchanged = next.InlierIndices.SequenceEqual(canonical.InlierIndices);
            canonical = next;
            if (unchanged)
            {
                break;
            }
        }

        return canonical;
    }

    private static bool HasComparableQuality(
        CandidateFit candidate,
        CandidateFit selected,
        double tolerancePixels)
    {
        const double maximumRelativeWeightGap = 0.05d;
        double weightScale = Math.Max(candidate.InlierWeight, selected.InlierWeight);
        double relativeWeightGap = weightScale <= 1e-12d
            ? 0d
            : Math.Abs(candidate.InlierWeight - selected.InlierWeight) / weightScale;
        double maximumErrorGap = Math.Max(1e-6d, tolerancePixels * 0.1d);
        return relativeWeightGap <= maximumRelativeWeightGap &&
            Math.Abs(candidate.RootMeanSquarePixels - selected.RootMeanSquarePixels) <= maximumErrorGap;
    }

    private static bool PredictsMateriallyDifferentCalibration(
        IReadOnlyList<FitSample> samples,
        CandidateFit candidate,
        CandidateFit selected,
        double tolerancePixels)
    {
        double minimumPixel = samples.Min(static sample => sample.Pixel);
        double maximumPixel = samples.Max(static sample => sample.Pixel);
        double slopeScale = Math.Max(Math.Abs(candidate.Slope), Math.Abs(selected.Slope));
        if (slopeScale <= MinimumSlopeMagnitude)
        {
            return false;
        }

        return PixelEquivalentDifference(minimumPixel, candidate, selected, slopeScale) > tolerancePixels ||
            PixelEquivalentDifference(maximumPixel, candidate, selected, slopeScale) > tolerancePixels;
    }

    private static double PixelEquivalentDifference(
        double pixel,
        CandidateFit candidate,
        CandidateFit selected,
        double slopeScale)
    {
        double candidateValue = (candidate.Slope * pixel) + candidate.Intercept;
        double selectedValue = (selected.Slope * pixel) + selected.Intercept;
        return Math.Abs(candidateValue - selectedValue) / slopeScale;
    }

    private static string CreateSupportKey(IReadOnlyList<int> indices) =>
        string.Join(',', indices);

    private static int CompareSamples(FitSample left, FitSample right)
    {
        int comparison = StringComparer.Ordinal.Compare(left.Id, right.Id);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Pixel.CompareTo(right.Pixel);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Value.CompareTo(right.Value);
        return comparison != 0 ? comparison : left.Weight.CompareTo(right.Weight);
    }

    private static LinearTransformFitResult ValidateDirection(
        LinearTransformFitResult fit,
        bool expectedPositiveSlope,
        string reason)
    {
        if (fit.Transform is not { } transform ||
            (expectedPositiveSlope ? transform.Slope > 0d : transform.Slope < 0d))
        {
            return fit;
        }

        string[] reasons = DistinctReasons(fit.Reasons.Append(reason));
        string[] warnings = DistinctReasons(fit.Diagnostics.Warnings.Append(reason));
        return fit with
        {
            Confidence = Math.Min(fit.Confidence, 0.25d),
            Validity = CalibrationValidity.NeedsReview,
            Reasons = reasons,
            Diagnostics = fit.Diagnostics with
            {
                Warnings = warnings,
            },
        };
    }

    private static double CalculateSlopeStandardError(
        IReadOnlyList<FitSample> samples,
        CandidateFit fit)
    {
        if (fit.InlierIndices.Count <= 2)
        {
            return double.PositiveInfinity;
        }

        double meanPixel = fit.InlierIndices.Average(index => samples[index].Pixel);
        double sumSquares = fit.InlierIndices.Sum(index =>
        {
            double delta = samples[index].Pixel - meanPixel;
            return delta * delta;
        });
        if (sumSquares <= MinimumSlopeMagnitude)
        {
            return double.PositiveInfinity;
        }

        double graphResidualSquares = fit.InlierIndices.Sum(index =>
        {
            FitSample sample = samples[index];
            double residual = sample.Value - ((fit.Slope * sample.Pixel) + fit.Intercept);
            return residual * residual;
        });
        return Math.Sqrt((graphResidualSquares / (fit.InlierIndices.Count - 2d)) / sumSquares);
    }

    private static bool ExtrapolatesToZero(
        IReadOnlyList<FitSample> samples,
        IReadOnlyList<int> inlierIndices)
    {
        double minimum = inlierIndices.Min(index => samples[index].Value);
        double maximum = inlierIndices.Max(index => samples[index].Value);
        return 0d < minimum || 0d > maximum;
    }

    private static LinearTransformFitResult InsufficientFit(
        int inputCount,
        IReadOnlyList<string> invalidIds,
        string reason,
        TimeSpan elapsed) =>
        new(
            null,
            0d,
            CalibrationValidity.InsufficientEvidence,
            [reason],
            new CalibrationUncertainty(
                double.PositiveInfinity,
                double.PositiveInfinity,
                0d,
                double.PositiveInfinity,
                true),
            new CalibrationDiagnostics(
                inputCount,
                0,
                Array.Empty<string>(),
                invalidIds,
                [reason],
                elapsed));

    private static void ValidateOptions(RobustFitOptions options)
    {
        if (!double.IsFinite(options.InlierTolerancePixels) || options.InlierTolerancePixels <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Inlier tolerance must be positive and finite.");
        }

        if (!double.IsFinite(options.MinimumInlierWeightFraction) ||
            options.MinimumInlierWeightFraction <= 0d ||
            options.MinimumInlierWeightFraction > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum inlier weight fraction must be in (0, 1].");
        }

        if (options.MaximumRefinementIterations < 0 || options.MaximumRefinementIterations > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum refinement iterations must be in [0, 100].");
        }
    }

    private static PrintedXTickEvidence[] MergePrintedTicks(
        IReadOnlyList<PrintedXTickEvidence> first,
        IReadOnlyList<PrintedXTickEvidence> second,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        Dictionary<string, PrintedXTickEvidence> byId = new(StringComparer.Ordinal);
        foreach (PrintedXTickEvidence tick in first.Concat(second))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePrintedTick(tick);
            if (byId.TryGetValue(tick.Id, out PrintedXTickEvidence? existing) && existing != tick)
            {
                throw new ArgumentException(
                    $"Printed x-tick ID '{tick.Id}' has conflicting evidence records.",
                    nameof(second));
            }

            byId.TryAdd(tick.Id, tick);
        }

        return [.. byId.Values];
    }

    private static void ValidatePrintedTick(PrintedXTickEvidence tick)
    {
        if (string.IsNullOrWhiteSpace(tick.Id) ||
            !double.IsFinite(tick.PixelX) ||
            !double.IsFinite(tick.PrintedValue) ||
            !double.IsFinite(tick.Confidence) ||
            tick.Confidence <= 0d ||
            tick.Confidence > 1d)
        {
            throw new ArgumentException("Printed x-tick evidence must be named, finite, and confidence weighted.");
        }

        if (!string.Equals(
            tick.CoordinateSpace,
            AxisGeometryCoordinateSpaces.OriginalPixels,
            StringComparison.Ordinal))
        {
            throw new ArgumentException("Printed x-tick evidence must be normalized to original_pixels.");
        }
    }

    private static double? ResolveYMaximum(
        SessionFirstCalibrationRequest request,
        LinearTransformFitResult yFit)
    {
        if (request.YMaximum is { } requested)
        {
            if (!double.IsFinite(requested) || requested <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "YMaximum must be positive and finite.");
            }

            return requested;
        }

        HashSet<string> inlierIds = yFit.Diagnostics.InlierIds.ToHashSet(StringComparer.Ordinal);
        double maximum = request.YTicks
            .Where(tick => inlierIds.Contains(tick.Id))
            .Select(static tick => tick.Value)
            .DefaultIfEmpty(0d)
            .Max();
        return maximum > 0d ? maximum : null;
    }

    private static double? ResolveXMaximum(
        SessionFirstCalibrationRequest request,
        IReadOnlyList<PrintedXTickEvidence> printedTicks,
        SessionLatticeResult lattice)
    {
        if (request.XMaximum is { } requested)
        {
            if (!double.IsFinite(requested) || requested < 1d)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "XMaximum must be finite and at least one.");
            }

            return requested;
        }

        double? finalPrintedSession = printedTicks
            .Where(static tick => double.IsFinite(tick.PrintedValue))
            .Select(static tick => (double?)tick.PrintedValue)
            .Max();
        if (finalPrintedSession.HasValue)
        {
            return finalPrintedSession;
        }

        return lattice.Assignments
            .Where(static assignment => assignment.EstimatedX.HasValue)
            .Select(static assignment => assignment.EstimatedX)
            .Max();
    }

    private static bool TryResolvePixelX(
        double xMaximum,
        LinearTransformFitResult? xFit,
        SessionLatticeResult lattice,
        out double pixelX)
    {
        if (xFit?.Transform is { } transform)
        {
            pixelX = transform.GraphToPixel(xMaximum);
            return double.IsFinite(pixelX);
        }

        if (lattice.Session1PixelX is { } origin && lattice.PitchPixels is { } pitch)
        {
            pixelX = origin + ((xMaximum - 1d) * pitch);
            return double.IsFinite(pixelX);
        }

        pixelX = 0d;
        return false;
    }

    private static CalibrationValidity CombineValidity(
        CalibrationValidity y,
        CalibrationValidity? x,
        CalibrationValidity lattice)
    {
        CalibrationValidity[] values = x.HasValue ? [y, x.Value, lattice] : [y, lattice];
        if (values.Contains(CalibrationValidity.InvalidSessionOrigin))
        {
            return CalibrationValidity.InvalidSessionOrigin;
        }

        if (values.Contains(CalibrationValidity.InsufficientEvidence))
        {
            return CalibrationValidity.InsufficientEvidence;
        }

        return values.Contains(CalibrationValidity.NeedsReview)
            ? CalibrationValidity.NeedsReview
            : CalibrationValidity.Valid;
    }

    private static string[] DistinctReasons(IEnumerable<string> reasons) =>
        [
            .. reasons
                .Where(static reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct(StringComparer.Ordinal),
        ];

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 1e-9;

    private readonly record struct FitSample(
        string Id,
        double Pixel,
        double Value,
        double Weight,
        string CoordinateSpace);

    private readonly record struct CandidateFit(
        double Slope,
        double Intercept,
        IReadOnlyList<int> InlierIndices,
        double InlierWeight,
        double RootMeanSquarePixels,
        double MaximumResidualPixels);
}
