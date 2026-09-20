// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;

namespace GraphReader.Axis;

public static class SessionLattice
{
    private const double MinimumPitchPixels = 0.25d;
    private const int MaximumCandidatePitches = 50_000;

    public static SessionLatticeResult Fit(
        SessionLatticeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch stopwatch = Stopwatch.StartNew();
        LinearTransformFitResult? printedAxisFit = request.PrintedTicks.Count >= 2
            ? RobustCalibration.FitX(request.PrintedTicks, cancellationToken: cancellationToken)
            : null;
        int printedInputCount = request.PrintedTicks.Count;
        if (printedAxisFit is { IsValid: true })
        {
            HashSet<string> inliers = printedAxisFit.Diagnostics.InlierIds.ToHashSet(StringComparer.Ordinal);
            request = request with { PrintedTicks = request.PrintedTicks.Where(tick => inliers.Contains(tick.Id)).ToArray() };
        }

        List<WeightedColumn> observedColumns = CollectObservedColumns(request, cancellationToken);
        List<WeightedColumn> alignmentColumns = [.. observedColumns];
        List<WeightedColumn> printedColumns = [];
        foreach (PrintedXTickEvidence tick in request.PrintedTicks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var column = new WeightedColumn(tick.PixelX, tick.Confidence);
            printedColumns.Add(column);
            alignmentColumns.Add(column);
        }
        foreach (UnlabeledXTickEvidence tick in request.UnlabeledTicks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            alignmentColumns.Add(new WeightedColumn(tick.PixelX, tick.Confidence));
        }
        WeightedColumn[] uniqueAlignmentColumns = ClusterColumns(
            alignmentColumns,
            request.DuplicateColumnTolerancePixels,
            cancellationToken);
        WeightedColumn[] uniqueObservedColumns = ClusterColumns(
            observedColumns,
            request.DuplicateColumnTolerancePixels,
            cancellationToken);
        WeightedColumn[] assignmentColumns = ClusterColumns(
            observedColumns.Count > 0 ? observedColumns : printedColumns,
            request.DuplicateColumnTolerancePixels,
            cancellationToken);

        double[] pitchCandidates = BuildPitchCandidates(
            request,
            uniqueAlignmentColumns,
            cancellationToken);
        if (pitchCandidates.Length == 0)
        {
            stopwatch.Stop();
            return InsufficientResult(
                request,
                uniqueAlignmentColumns.Length,
                "Session pitch cannot be inferred from fewer than two aligned columns or an external lattice.",
                stopwatch.Elapsed);
        }

        List<ScoredLattice> scored = new(pitchCandidates.Length);
        foreach (double pitch in pitchCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OriginResolution origin = ResolveOrigin(
                request,
                uniqueAlignmentColumns,
                pitch,
                cancellationToken);
            scored.Add(Score(
                request,
                uniqueAlignmentColumns,
                pitch,
                origin,
                cancellationToken));
        }

        scored.Sort(CompareScoredLattices);
        ScoredLattice best = scored[0];
        ScoredLattice? alternative = scored.Count > 1 ? scored[1] : null;
        double alternativeMargin = alternative is null
            ? 1d
            : Math.Max(0d, alternative.Value.Score - best.Score);
        bool hasAuthoritativePitch = request.SharedPanels.Count > 0 || request.PrintedTicks.Count >= 2;
        bool candidateHarmonicAmbiguity = !hasAuthoritativePitch &&
            alternative is { } other &&
            alternativeMargin < 0.02d &&
            IsHarmonicRatio(best.Pitch, other.Pitch);
        PitchConflictKind crossSourcePitchConflict = AnalyzeCrossSourcePitchConflict(
            request,
            uniqueObservedColumns,
            cancellationToken);
        bool harmonicAmbiguity = candidateHarmonicAmbiguity ||
            crossSourcePitchConflict == PitchConflictKind.HarmonicAlias;

        List<string> reasons = [];
        List<string> warnings = printedAxisFit is null ? [] : [.. printedAxisFit.Diagnostics.Warnings];
        CalibrationValidity validity = CalibrationValidity.Valid;
        if (printedAxisFit is { Validity: not CalibrationValidity.Valid })
        {
            validity = CalibrationValidity.NeedsReview;
            reasons.Add("Printed x-axis tick evidence does not define a valid left-to-right session transform.");
            reasons.AddRange(printedAxisFit.Reasons);
        }

        if (request.OriginOverride is not null)
        {
            warnings.Add("Manual session-origin override applied and recorded as authoritative provenance.");
            if (string.IsNullOrWhiteSpace(request.OriginOverride.ProvenanceId) ||
                string.IsNullOrWhiteSpace(request.OriginOverride.Reason) ||
                request.OriginOverride.ConfirmedAtUtc is null)
            {
                warnings.Add("Manual session-origin override provenance is incomplete; record an ID, reason, and confirmation timestamp.");
            }
        }

        if (!best.Origin.HasAbsoluteOrigin)
        {
            validity = CalibrationValidity.NeedsReview;
            reasons.Add("No printed, shared-panel, expected, or manual session-1 origin is available.");
            warnings.Add("The provisional lattice preserves column order only and does not assert exact session values.");
        }

        if (best.Origin.MaximumSourceDisagreementPixels >
            request.AlignmentToleranceFraction * best.Pitch)
        {
            if (request.OriginOverride is null)
            {
                validity = CalibrationValidity.NeedsReview;
                reasons.Add("Independent session-origin sources disagree beyond the configured tolerance.");
            }
            else
            {
                reasons.Add("Automatic session-origin evidence disagrees with the explicit manual override; the override supersedes it.");
                warnings.Add("Manual origin override superseded conflicting automatic origin evidence while preserving the conflict record.");
            }
        }

        if (crossSourcePitchConflict == PitchConflictKind.Disagreement)
        {
            validity = CalibrationValidity.NeedsReview;
            reasons.Add("Printed-tick and marker-column pitch evidence disagree beyond the configured tolerance.");
        }
        else if (crossSourcePitchConflict == PitchConflictKind.HarmonicAlias)
        {
            warnings.Add("Marker spacing is a harmonic multiple of the printed session pitch; this may represent legitimate missing sessions or an OCR label alias.");
        }

        if (SharedPanelAuthoritiesConflict(request, cancellationToken))
        {
            validity = CalibrationValidity.NeedsReview;
            reasons.Add("Authoritative shared-panel pitch or origin evidence conflicts beyond the configured tolerance.");
        }

        if (request.ExpectedSession1PixelX is { } expected &&
            Math.Abs(expected - best.Origin.PixelX) > request.AlignmentToleranceFraction * best.Pitch)
        {
            if (request.OriginOverride is null)
            {
                validity = CalibrationValidity.InvalidSessionOrigin;
            }

            reasons.Add(request.OriginOverride is null
                ? "The fitted session origin conflicts with the trusted session-1 location."
                : "A manual session-origin override supersedes conflicting automatic origin evidence.");
        }

        int? firstObservedSession = ResolveSessionNumber(
            uniqueObservedColumns.FirstOrDefault().PixelX,
            best.Origin.PixelX,
            best.Pitch,
            uniqueObservedColumns.Length > 0);
        if (request.RequireFirstObservedSessionOne &&
            firstObservedSession is { } firstSession &&
            firstSession != 1)
        {
            if (request.OriginOverride is null)
            {
                validity = CalibrationValidity.InvalidSessionOrigin;
                reasons.Add($"The first observed column maps to session {firstSession}, not session 1.");
            }
            else
            {
                reasons.Add($"The first observed column maps to session {firstSession}; the explicit manual origin override was retained.");
                warnings.Add("Manual origin override accepted for a staggered or sparse-start profile.");
            }
        }

        if (harmonicAmbiguity && validity == CalibrationValidity.Valid)
        {
            validity = CalibrationValidity.NeedsReview;
            reasons.Add("Multiple harmonic session pitches fit the available column gaps almost equally well.");
        }

        if (best.RootMeanSquarePixels > request.AlignmentToleranceFraction * best.Pitch &&
            validity == CalibrationValidity.Valid)
        {
            validity = CalibrationValidity.NeedsReview;
            reasons.Add("Column alignment residual exceeds the configured session-lattice tolerance.");
        }

        SessionXEvidence[] assignments = BuildAssignments(
            request,
            assignmentColumns,
            best,
            cancellationToken);
        if (best.Origin.HasAbsoluteOrigin && assignments.Any(static point =>
                point.PrintedX is null && point.EstimatedX is null))
        {
            warnings.Add("Marker columns outside the supported session lattice retain unknown x values and require review.");
        }
        SessionLatticeSource[] contributingSources = ResolveContributingSources(request);
        SessionLatticeSource source = ResolveSource(contributingSources, best.Origin.HasAbsoluteOrigin);
        double relativePitchUncertainty = CalculateRelativePitchUncertainty(
            request,
            best,
            alternative);
        double confidence = CalculateConfidence(request, best, alternativeMargin, validity);
        stopwatch.Stop();

        return new SessionLatticeResult(
            best.Origin.PixelX,
            best.Pitch,
            confidence,
            source,
            validity,
            Distinct(reasons),
            best.Origin.HasAbsoluteOrigin,
            !best.Origin.HasAbsoluteOrigin,
            assignments,
            new SessionLatticeUncertainty(
                best.RootMeanSquarePixels,
                best.MaximumResidualPixels,
                relativePitchUncertainty,
                alternativeMargin,
                harmonicAmbiguity),
            new SessionLatticeDiagnostics(
                pitchCandidates.Length,
                uniqueObservedColumns.Length,
                printedInputCount,
                request.ConnectedSequences.Count,
                request.SharedPanels.Count,
                warnings.AsReadOnly(),
                stopwatch.Elapsed,
                request.UnlabeledTicks.Count),
            request.OriginOverride is not null,
            contributingSources,
            request.OriginOverride);
    }

    private static List<WeightedColumn> CollectObservedColumns(
        SessionLatticeRequest request,
        CancellationToken cancellationToken)
    {
        List<WeightedColumn> columns = [];
        foreach (MarkerColumnEvidence marker in request.MarkerColumns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            columns.Add(new WeightedColumn(marker.PixelX, marker.Confidence));
        }

        foreach (ConnectedSequenceEvidence sequence in request.ConnectedSequences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (double pixelX in sequence.PixelXs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                columns.Add(new WeightedColumn(pixelX, sequence.Confidence));
            }
        }

        return columns;
    }

    private static WeightedColumn[] ClusterColumns(
        IReadOnlyList<WeightedColumn> columns,
        double tolerance,
        CancellationToken cancellationToken)
    {
        if (columns.Count == 0)
        {
            return [];
        }

        WeightedColumn[] sorted = [.. columns.OrderBy(static column => column.PixelX)];
        List<WeightedColumn> clustered = [];
        int start = 0;
        while (start < sorted.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = start + 1;
            double weightedPixel = sorted[start].PixelX * sorted[start].Weight;
            double totalWeight = sorted[start].Weight;
            double runningCenter = sorted[start].PixelX;
            while (end < sorted.Length && sorted[end].PixelX - runningCenter <= tolerance)
            {
                cancellationToken.ThrowIfCancellationRequested();
                weightedPixel += sorted[end].PixelX * sorted[end].Weight;
                totalWeight += sorted[end].Weight;
                runningCenter = weightedPixel / totalWeight;
                end++;
            }

            clustered.Add(new WeightedColumn(weightedPixel / totalWeight, Math.Min(1d, totalWeight)));
            start = end;
        }

        return [.. clustered];
    }

    private static double[] BuildPitchCandidates(
        SessionLatticeRequest request,
        IReadOnlyList<WeightedColumn> columns,
        CancellationToken cancellationToken)
    {
        Dictionary<long, double> candidates = [];
        foreach (SharedPanelLatticeEvidence shared in request.SharedPanels)
        {
            AddCandidate(candidates, shared.PitchPixels);
        }

        for (int left = 0; left < request.PrintedTicks.Count - 1; left++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int right = left + 1; right < request.PrintedTicks.Count; right++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PrintedXTickEvidence first = request.PrintedTicks[left];
                PrintedXTickEvidence second = request.PrintedTicks[right];
                double sessionDifference = Math.Abs(second.PrintedValue - first.PrintedValue);
                double pixelDifference = Math.Abs(second.PixelX - first.PixelX);
                if (sessionDifference > 1e-9)
                {
                    AddCandidate(candidates, pixelDifference / sessionDifference);
                }
            }
        }

        for (int left = 0; left < columns.Count - 1; left++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int last = Math.Min(columns.Count, left + request.MaxSessionGap + 1);
            for (int right = left + 1; right < last; right++)
            {
                double difference = columns[right].PixelX - columns[left].PixelX;
                for (int sessionGap = 1; sessionGap <= request.MaxSessionGap; sessionGap++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddCandidate(candidates, difference / sessionGap);
                    if (candidates.Count >= MaximumCandidatePitches)
                    {
                        return [.. candidates.Values];
                    }
                }
            }
        }

        return [.. candidates.Values];
    }

    private static void AddCandidate(IDictionary<long, double> candidates, double pitch)
    {
        if (!double.IsFinite(pitch) || pitch < MinimumPitchPixels)
        {
            return;
        }

        if (pitch > long.MaxValue / 1000d)
        {
            return;
        }

        long key = checked((long)Math.Round(pitch * 1000d, MidpointRounding.AwayFromZero));
        candidates.TryAdd(key, pitch);
    }

    private static PitchConflictKind AnalyzeCrossSourcePitchConflict(
        SessionLatticeRequest request,
        IReadOnlyList<WeightedColumn> observedColumns,
        CancellationToken cancellationToken)
    {
        if (request.PrintedTicks.Count < 2 || observedColumns.Count < 3)
        {
            return PitchConflictKind.None;
        }

        LinearTransformFitResult printedFit = RobustCalibration.FitX(
            request.PrintedTicks,
            cancellationToken: cancellationToken);
        if (printedFit.Transform is not { } transform || Math.Abs(transform.Slope) <= 1e-12)
        {
            return PitchConflictKind.None;
        }

        double printedPitch = Math.Abs(1d / transform.Slope);
        // A stray short gap must not hide a coherent half/double-pitch alias.
        if (HasRegularDenseRun(observedColumns, printedPitch / 2d,
                request.AlignmentToleranceFraction, cancellationToken) ||
            HasRegularDenseRun(observedColumns, printedPitch * 2d,
                request.AlignmentToleranceFraction, cancellationToken))
        {
            return PitchConflictKind.HarmonicAlias;
        }

        int compatibleGaps = 0;
        for (int index = 1; index < observedColumns.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double gap = observedColumns[index].PixelX - observedColumns[index - 1].PixelX;
            double gapInPrintedPitches = gap / printedPitch;
            double nearestPositiveInteger = Math.Max(1d, Math.Round(gapInPrintedPitches));
            double relativeDisagreement = Math.Abs(gapInPrintedPitches - nearestPositiveInteger) /
                nearestPositiveInteger;
            if (relativeDisagreement <= request.AlignmentToleranceFraction)
            {
                compatibleGaps++;
            }
            else if (HasRegularDenseRun(observedColumns, gap,
                request.AlignmentToleranceFraction, cancellationToken))
            {
                // A second coherent grid is conflicting evidence, even in a minority.
                return PitchConflictKind.Disagreement;
            }
        }

        // Isolated detections can split a real gap in two. Require a strict
        // consensus; per-point assignment and residual checks still apply.
        return compatibleGaps > (observedColumns.Count - 1) / 2
            ? PitchConflictKind.None
            : PitchConflictKind.Disagreement;
    }

    private static bool HasRegularDenseRun(
        IReadOnlyList<WeightedColumn> observedColumns,
        double referenceGap,
        double toleranceFraction,
        CancellationToken cancellationToken)
    {
        int consecutiveGaps = 0;
        for (int index = 1; index < observedColumns.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double gap = observedColumns[index].PixelX - observedColumns[index - 1].PixelX;
            if (Math.Abs(gap - referenceGap) / referenceGap <= toleranceFraction)
            {
                consecutiveGaps++;
                if (consecutiveGaps >= 3)
                {
                    return true;
                }
            }
            else
            {
                consecutiveGaps = 0;
            }
        }

        return false;
    }

    private static bool SharedPanelAuthoritiesConflict(
        SessionLatticeRequest request,
        CancellationToken cancellationToken)
    {
        for (int left = 0; left < request.SharedPanels.Count - 1; left++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int right = left + 1; right < request.SharedPanels.Count; right++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SharedPanelLatticeEvidence first = request.SharedPanels[left];
                SharedPanelLatticeEvidence second = request.SharedPanels[right];
                double referencePitch = Math.Min(first.PitchPixels, second.PitchPixels);
                double relativePitchDifference = Math.Abs(first.PitchPixels - second.PitchPixels) /
                    referencePitch;
                double relativeOriginDifference = Math.Abs(first.Session1PixelX - second.Session1PixelX) /
                    referencePitch;
                if (relativePitchDifference > request.AlignmentToleranceFraction ||
                    relativeOriginDifference > request.AlignmentToleranceFraction)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static OriginResolution ResolveOrigin(
        SessionLatticeRequest request,
        IReadOnlyList<WeightedColumn> columns,
        double pitch,
        CancellationToken cancellationToken)
    {
        List<WeightedValue> origins = [];
        foreach (PrintedXTickEvidence tick in request.PrintedTicks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            origins.Add(new WeightedValue(
                tick.PixelX - ((tick.PrintedValue - 1d) * pitch),
                tick.Confidence));
        }

        foreach (SharedPanelLatticeEvidence shared in request.SharedPanels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            origins.Add(new WeightedValue(shared.Session1PixelX, shared.Confidence));
        }

        if (request.ExpectedSession1PixelX is { } expected)
        {
            origins.Add(new WeightedValue(expected, 1d));
        }

        if (request.OriginOverride is { } manual)
        {
            double session1 = manual.Session1PixelX + ((1d - manual.Session1Value) * pitch);
            double manualDisagreement = origins.Count == 0
                ? 0d
                : origins.Max(item => Math.Abs(item.Value - session1));
            return new OriginResolution(session1, true, manualDisagreement);
        }

        if (origins.Count == 0)
        {
            double provisional = columns.Count > 0 ? columns[0].PixelX : 0d;
            return new OriginResolution(provisional, false, 0d);
        }

        double origin = WeightedMedian(origins);
        double maximumDisagreement = origins.Max(item => Math.Abs(item.Value - origin));
        return new OriginResolution(origin, true, maximumDisagreement);
    }

    private static ScoredLattice Score(
        SessionLatticeRequest request,
        IReadOnlyList<WeightedColumn> columns,
        double pitch,
        OriginResolution origin,
        CancellationToken cancellationToken)
    {
        double weightedSquaredPixels = 0d;
        double totalWeight = 0d;
        double maximumResidual = 0d;
        foreach (WeightedColumn column in columns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double latticeIndex = Math.Round((column.PixelX - origin.PixelX) / pitch);
            double alignedPixel = origin.PixelX + (latticeIndex * pitch);
            double residual = Math.Abs(column.PixelX - alignedPixel);
            weightedSquaredPixels += column.Weight * residual * residual;
            totalWeight += column.Weight;
            maximumResidual = Math.Max(maximumResidual, residual);
        }

        double rootMeanSquare = totalWeight <= 0d
            ? 0d
            : Math.Sqrt(weightedSquaredPixels / totalWeight);
        double normalizedAlignment = rootMeanSquare / pitch;
        double printedPenalty = 0d;
        double printedWeight = 0d;
        foreach (PrintedXTickEvidence tick in request.PrintedTicks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double expectedPixel = origin.PixelX + ((tick.PrintedValue - 1d) * pitch);
            printedPenalty += tick.Confidence * Math.Abs(tick.PixelX - expectedPixel) / pitch;
            printedWeight += tick.Confidence;
        }

        if (printedWeight > 0d)
        {
            printedPenalty /= printedWeight;
        }

        double sharedPenalty = 0d;
        double sharedWeight = 0d;
        foreach (SharedPanelLatticeEvidence shared in request.SharedPanels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double pitchError = Math.Abs(shared.PitchPixels - pitch) / shared.PitchPixels;
            double originError = Math.Abs(shared.Session1PixelX - origin.PixelX) / shared.PitchPixels;
            sharedPenalty += shared.Confidence * (pitchError + originError);
            sharedWeight += shared.Confidence;
        }

        if (sharedWeight > 0d)
        {
            sharedPenalty /= sharedWeight;
        }

        double gapComplexityPenalty = CalculateGapComplexityPenalty(
            request.ConnectedSequences,
            pitch,
            cancellationToken);
        double score = normalizedAlignment +
            (2d * printedPenalty) +
            (2d * sharedPenalty) +
            gapComplexityPenalty;
        return new ScoredLattice(
            pitch,
            origin,
            score,
            rootMeanSquare,
            maximumResidual);
    }

    private static double CalculateGapComplexityPenalty(
        IReadOnlyList<ConnectedSequenceEvidence> sequences,
        double pitch,
        CancellationToken cancellationToken)
    {
        double totalPenalty = 0d;
        double totalWeight = 0d;
        foreach (ConnectedSequenceEvidence sequence in sequences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double[] sorted = [.. sequence.PixelXs.Order()];
            for (int index = 1; index < sorted.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double gap = (sorted[index] - sorted[index - 1]) / pitch;
                int integerGap = Math.Max(1, (int)Math.Round(gap));
                double integerResidual = Math.Abs(gap - integerGap);
                totalPenalty += sequence.Confidence *
                    (integerResidual + (0.005d * Math.Max(0, integerGap - 1)));
                totalWeight += sequence.Confidence;
            }
        }

        return totalWeight <= 0d ? 0d : totalPenalty / totalWeight;
    }

    private static SessionXEvidence[] BuildAssignments(
        SessionLatticeRequest request,
        IReadOnlyList<WeightedColumn> columns,
        ScoredLattice lattice,
        CancellationToken cancellationToken)
    {
        List<SessionXEvidence> assignments = new(columns.Count);
        double matchingTolerance = Math.Max(
            request.DuplicateColumnTolerancePixels,
            request.AlignmentToleranceFraction * lattice.Pitch);
        for (int index = 0; index < columns.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WeightedColumn column = columns[index];
            PrintedXTickEvidence? printed = null;
            double nearestPrintedDistance = double.PositiveInfinity;
            foreach (PrintedXTickEvidence tick in request.PrintedTicks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double distance = Math.Abs(tick.PixelX - column.PixelX);
                if (distance <= matchingTolerance && distance < nearestPrintedDistance)
                {
                    printed = tick;
                    nearestPrintedDistance = distance;
                }
            }

            double? printedX = lattice.Origin.HasAbsoluteOrigin ? printed?.PrintedValue : null;
            double? estimatedX = null;
            if (lattice.Origin.HasAbsoluteOrigin)
            {
                double rawSession = 1d + ((column.PixelX - lattice.Origin.PixelX) / lattice.Pitch);
                double roundedSession = Math.Round(rawSession);
                if (roundedSession >= 1d &&
                    Math.Abs(rawSession - roundedSession) <= request.AlignmentToleranceFraction)
                {
                    estimatedX = roundedSession;
                }
            }

            SessionXEvidenceKind kind = !lattice.Origin.HasAbsoluteOrigin
                ? SessionXEvidenceKind.OrdinalOnly
                : printedX.HasValue
                    ? SessionXEvidenceKind.Printed
                    : request.OriginOverride is not null
                        ? SessionXEvidenceKind.Manual
                        : SessionXEvidenceKind.Estimated;
            double alignmentError = Math.Abs(
                column.PixelX -
                (lattice.Origin.PixelX +
                    (Math.Round((column.PixelX - lattice.Origin.PixelX) / lattice.Pitch) * lattice.Pitch)));
            double confidence = Math.Clamp(
                column.Weight * (1d - Math.Min(1d, alignmentError / (0.5d * lattice.Pitch))),
                0d,
                1d);
            assignments.Add(new SessionXEvidence(
                column.PixelX,
                index + 1,
                printedX,
                estimatedX,
                kind,
                confidence));
        }

        return [.. assignments];
    }

    private static SessionLatticeSource ResolveSource(
        SessionLatticeSource[] contributingSources,
        bool hasAbsoluteOrigin)
    {
        if (contributingSources.Length > 1)
        {
            return SessionLatticeSource.Mixed;
        }

        if (contributingSources.Length == 1)
        {
            return contributingSources[0];
        }

        return hasAbsoluteOrigin ? SessionLatticeSource.Manual : SessionLatticeSource.None;
    }

    private static SessionLatticeSource[] ResolveContributingSources(SessionLatticeRequest request)
    {
        List<SessionLatticeSource> sources = [];
        if (request.PrintedTicks.Count > 0)
        {
            sources.Add(SessionLatticeSource.OcrTicks);
        }

        if (request.UnlabeledTicks.Count > 0)
        {
            sources.Add(SessionLatticeSource.AxisTicks);
        }

        if (request.SharedPanels.Count > 0)
        {
            sources.Add(SessionLatticeSource.SharedPanel);
        }

        if (request.MarkerColumns.Count > 0 || request.ConnectedSequences.Count > 0)
        {
            sources.Add(SessionLatticeSource.MarkerLattice);
        }

        if (request.OriginOverride is not null || request.ExpectedSession1PixelX.HasValue)
        {
            sources.Add(SessionLatticeSource.Manual);
        }

        return [.. sources.Distinct()];
    }

    private static double CalculateRelativePitchUncertainty(
        SessionLatticeRequest request,
        ScoredLattice best,
        ScoredLattice? alternative)
    {
        List<double> trustedPitches = [.. request.SharedPanels.Select(static panel => panel.PitchPixels)];
        double sourceUncertainty = 0d;
        if (trustedPitches.Count > 1)
        {
            double mean = trustedPitches.Average();
            sourceUncertainty = Math.Sqrt(trustedPitches.Average(value => Math.Pow(value - mean, 2d))) / mean;
        }

        double alignmentUncertainty = best.RootMeanSquarePixels / best.Pitch;
        double alternativeUncertainty = alternative is null
            ? 0d
            : Math.Min(1d, Math.Abs(alternative.Value.Pitch - best.Pitch) / best.Pitch);
        return Math.Clamp(Math.Max(sourceUncertainty, Math.Max(alignmentUncertainty, alternativeUncertainty)), 0d, 1d);
    }

    private static double CalculateConfidence(
        SessionLatticeRequest request,
        ScoredLattice best,
        double alternativeMargin,
        CalibrationValidity validity)
    {
        double evidenceFactor = Math.Min(
            1d,
            0.2d +
            (0.08d * request.PrintedTicks.Count) +
            (0.06d * request.UnlabeledTicks.Count) +
            (0.08d * request.MarkerColumns.Count) +
            (0.15d * request.ConnectedSequences.Count) +
            (0.2d * request.SharedPanels.Count) +
            (request.OriginOverride is null ? 0d : 0.25d));
        double fitFactor = 1d / (1d + (4d * best.Score));
        double separationFactor = Math.Clamp(0.5d + (5d * alternativeMargin), 0.5d, 1d);
        double validityFactor = validity switch
        {
            CalibrationValidity.Valid => 1d,
            CalibrationValidity.NeedsReview => 0.7d,
            CalibrationValidity.InvalidSessionOrigin => 0.4d,
            _ => 0d,
        };
        return Math.Clamp(evidenceFactor * fitFactor * separationFactor * validityFactor, 0d, 1d);
    }

    private static int CompareScoredLattices(ScoredLattice left, ScoredLattice right)
    {
        const double equivalentScore = 1e-6;
        double scoreDifference = left.Score - right.Score;
        if (Math.Abs(scoreDifference) > equivalentScore)
        {
            return scoreDifference < 0d ? -1 : 1;
        }

        return right.Pitch.CompareTo(left.Pitch);
    }

    private static bool IsHarmonicRatio(double first, double second)
    {
        double ratio = Math.Max(first, second) / Math.Min(first, second);
        return Math.Abs(ratio - Math.Round(ratio)) <= 0.03d;
    }

    private static int? ResolveSessionNumber(
        double pixelX,
        double origin,
        double pitch,
        bool hasObservation)
    {
        if (!hasObservation)
        {
            return null;
        }

        return checked(1 + (int)Math.Round((pixelX - origin) / pitch));
    }

    private static double WeightedMedian(IReadOnlyList<WeightedValue> values)
    {
        WeightedValue[] sorted = [.. values.OrderBy(static item => item.Value)];
        double halfWeight = sorted.Sum(static item => item.Weight) / 2d;
        double cumulative = 0d;
        foreach (WeightedValue item in sorted)
        {
            cumulative += item.Weight;
            if (cumulative >= halfWeight)
            {
                return item.Value;
            }
        }

        return sorted[^1].Value;
    }

    private static SessionLatticeResult InsufficientResult(
        SessionLatticeRequest request,
        int uniqueColumnCount,
        string reason,
        TimeSpan elapsed) =>
        new(
            null,
            null,
            0d,
            SessionLatticeSource.None,
            CalibrationValidity.InsufficientEvidence,
            [reason],
            false,
            true,
            Array.Empty<SessionXEvidence>(),
            new SessionLatticeUncertainty(
                double.PositiveInfinity,
                double.PositiveInfinity,
                double.PositiveInfinity,
                0d,
                false),
            new SessionLatticeDiagnostics(
                0,
                uniqueColumnCount,
                request.PrintedTicks.Count,
                request.ConnectedSequences.Count,
                request.SharedPanels.Count,
                [reason],
                elapsed,
                request.UnlabeledTicks.Count),
            request.OriginOverride is not null,
            ResolveContributingSources(request),
            request.OriginOverride);

    private static void ValidateRequest(
        SessionLatticeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.PrintedTicks);
        ArgumentNullException.ThrowIfNull(request.UnlabeledTicks);
        ArgumentNullException.ThrowIfNull(request.MarkerColumns);
        ArgumentNullException.ThrowIfNull(request.ConnectedSequences);
        ArgumentNullException.ThrowIfNull(request.SharedPanels);
        ValidateCoordinateSpace(request.CoordinateSpace, nameof(request));
        if (request.MaxSessionGap < 1 || request.MaxSessionGap > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "MaxSessionGap must be in [1, 10000].");
        }

        if (!double.IsFinite(request.AlignmentToleranceFraction) ||
            request.AlignmentToleranceFraction <= 0d ||
            request.AlignmentToleranceFraction > 0.5d)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Alignment tolerance must be in (0, 0.5].");
        }

        if (!double.IsFinite(request.DuplicateColumnTolerancePixels) ||
            request.DuplicateColumnTolerancePixels <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Duplicate-column tolerance must be positive and finite.");
        }

        if (request.ExpectedSession1PixelX is { } expected && !double.IsFinite(expected))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Expected session-1 position must be finite.");
        }

        foreach (PrintedXTickEvidence tick in request.PrintedTicks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCoordinateSpace(tick.CoordinateSpace, nameof(request.PrintedTicks));
            ValidateEvidence(tick.Id, tick.PixelX, tick.Confidence, nameof(request.PrintedTicks));
            if (!double.IsFinite(tick.PrintedValue))
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Printed session values must be finite.");
            }
        }

        foreach (UnlabeledXTickEvidence tick in request.UnlabeledTicks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCoordinateSpace(tick.CoordinateSpace, nameof(request.UnlabeledTicks));
            ValidateEvidence(tick.Id, tick.PixelX, tick.Confidence, nameof(request.UnlabeledTicks));
        }

        foreach (MarkerColumnEvidence marker in request.MarkerColumns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCoordinateSpace(marker.CoordinateSpace, nameof(request.MarkerColumns));
            ValidateEvidence("marker column", marker.PixelX, marker.Confidence, nameof(request.MarkerColumns));
        }

        foreach (ConnectedSequenceEvidence sequence in request.ConnectedSequences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCoordinateSpace(sequence.CoordinateSpace, nameof(request.ConnectedSequences));
            if (string.IsNullOrWhiteSpace(sequence.SequenceId) ||
                !double.IsFinite(sequence.Confidence) ||
                sequence.Confidence <= 0d ||
                sequence.Confidence > 1d ||
                sequence.PixelXs is null)
            {
                throw new ArgumentException("Connected-sequence evidence must be named, finite, and confidence weighted.", nameof(request));
            }

            foreach (double pixel in sequence.PixelXs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!double.IsFinite(pixel))
                {
                    throw new ArgumentException("Connected-sequence pixel positions must be finite.", nameof(request));
                }
            }
        }

        foreach (SharedPanelLatticeEvidence shared in request.SharedPanels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCoordinateSpace(shared.CoordinateSpace, nameof(request.SharedPanels));
            ValidateEvidence(shared.PanelId, shared.Session1PixelX, shared.Confidence, nameof(request.SharedPanels));
            if (!double.IsFinite(shared.PitchPixels) || shared.PitchPixels <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Shared-panel pitch must be positive and finite.");
            }
        }

        if (request.OriginOverride is { } manual)
        {
            ValidateCoordinateSpace(manual.CoordinateSpace, nameof(request.OriginOverride));
            ValidateEvidence("manual origin", manual.Session1PixelX, manual.Confidence, nameof(request.OriginOverride));
            if (!double.IsFinite(manual.Session1Value))
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Manual session value must be finite.");
            }
        }
    }

    private static void ValidateCoordinateSpace(string coordinateSpace, string parameter)
    {
        if (!string.Equals(
            coordinateSpace,
            AxisGeometryCoordinateSpaces.OriginalPixels,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Session-lattice evidence must be normalized to original_pixels.",
                parameter);
        }
    }

    private static void ValidateEvidence(string id, double pixel, double confidence, string parameter)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            !double.IsFinite(pixel) ||
            !double.IsFinite(confidence) ||
            confidence <= 0d ||
            confidence > 1d)
        {
            throw new ArgumentException("Evidence identifiers, pixels, and confidence values must be valid.", parameter);
        }
    }

    private static string[] Distinct(IEnumerable<string> reasons) =>
        [
            .. reasons
                .Where(static reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct(StringComparer.Ordinal),
        ];

    private readonly record struct WeightedColumn(double PixelX, double Weight);

    private readonly record struct WeightedValue(double Value, double Weight);

    private readonly record struct OriginResolution(
        double PixelX,
        bool HasAbsoluteOrigin,
        double MaximumSourceDisagreementPixels);

    private readonly record struct ScoredLattice(
        double Pitch,
        OriginResolution Origin,
        double Score,
        double RootMeanSquarePixels,
        double MaximumResidualPixels);

    private enum PitchConflictKind
    {
        None,
        Disagreement,
        HarmonicAlias,
    }
}
