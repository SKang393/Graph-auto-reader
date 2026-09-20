// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Phases;

public sealed class PhaseReasoningService : IPhaseReasoningService
{
    private readonly IPhaseDividerDetector _dividerDetector;

    public PhaseReasoningService()
        : this(new PhaseDividerDetector())
    {
    }

    public PhaseReasoningService(IPhaseDividerDetector dividerDetector)
    {
        _dividerDetector = dividerDetector ?? throw new ArgumentNullException(nameof(dividerDetector));
    }

    public Task<PhaseReasoningResult> ResolveAsync(
        PhaseReasoningRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var totalTimer = Stopwatch.StartNew();
        var preprocessTimer = Stopwatch.StartNew();
        PhaseReasoningFailure? validationFailure = Validate(request);
        preprocessTimer.Stop();

        string runId = CreateRunId();
        if (validationFailure is not null)
        {
            totalTimer.Stop();
            return Task.FromResult(Failed(
                request,
                runId,
                validationFailure,
                preprocessTimer.Elapsed.TotalMilliseconds,
                0,
                0,
                totalTimer.Elapsed.TotalMilliseconds));
        }

        IReadOnlyList<PhaseDivider> dividers;
        var inferenceTimer = Stopwatch.StartNew();
        try
        {
            dividers = _dividerDetector.Detect(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            inferenceTimer.Stop();
            totalTimer.Stop();
            return Task.FromResult(Failed(
                request,
                runId,
                Error(
                    "PHASE_DIVIDER_DETECTION_FAILED",
                    $"Phase divider detection failed: {exception.GetType().Name}: {exception.Message}"),
                preprocessTimer.Elapsed.TotalMilliseconds,
                inferenceTimer.Elapsed.TotalMilliseconds,
                0,
                totalTimer.Elapsed.TotalMilliseconds));
        }

        inferenceTimer.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        PhaseReasoningFailure? dividerFailure = ValidateDetectedDividers(request, dividers);
        if (dividerFailure is not null)
        {
            totalTimer.Stop();
            return Task.FromResult(Failed(
                request,
                runId,
                dividerFailure,
                preprocessTimer.Elapsed.TotalMilliseconds,
                inferenceTimer.Elapsed.TotalMilliseconds,
                0,
                totalTimer.Elapsed.TotalMilliseconds));
        }

        var postprocessTimer = Stopwatch.StartNew();
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        PhaseDivider[] orderedDividers = dividers
            .OrderBy(static divider => divider.OriginalX)
            .ThenBy(static divider => divider.DividerId, StringComparer.Ordinal)
            .ToArray();
        PhaseRegion[] phases = ResolvePhases(request, orderedDividers, warnings, cancellationToken);
        PhasePointAssignment[] assignments = AssignPoints(request.Points, phases, cancellationToken);
        PhaseSeriesRelation[] relations = ResolveSeriesRelations(request.Series, warnings, cancellationToken);
        postprocessTimer.Stop();
        totalTimer.Stop();

        return Task.FromResult(new PhaseReasoningResult(
            PhaseReasoningContract.Version,
            runId,
            request.ProjectId,
            request.PanelId,
            PhaseReasoningContract.Stage,
            request.Options.StageVersion,
            request.InputSha256,
            PhaseReasoningContract.CoordinateSpace,
            new PhaseReasoningPayload(
                orderedDividers,
                phases,
                assignments,
                relations,
                request.ManualOverrides),
            new PhaseReasoningTiming(
                preprocessTimer.Elapsed.TotalMilliseconds,
                inferenceTimer.Elapsed.TotalMilliseconds,
                postprocessTimer.Elapsed.TotalMilliseconds,
                totalTimer.Elapsed.TotalMilliseconds),
            OverallConfidence(orderedDividers, phases),
            warnings,
            null));
    }

    private static PhaseRegion[] ResolvePhases(
        PhaseReasoningRequest request,
        PhaseDivider[] dividers,
        SortedSet<string> warnings,
        CancellationToken cancellationToken)
    {
        var boundaries = new double[dividers.Length + 2];
        boundaries[0] = request.PlotBounds.Left;
        for (var index = 0; index < dividers.Length; index++)
        {
            boundaries[index + 1] = dividers[index].OriginalX;
        }

        boundaries[^1] = request.PlotBounds.Right;
        var phaseIds = new string[boundaries.Length - 1];
        for (var index = 0; index < phaseIds.Length; index++)
        {
            string leftId = index == 0 ? "plot-left" : dividers[index - 1].DividerId;
            string rightId = index == dividers.Length ? "plot-right" : dividers[index].DividerId;
            phaseIds[index] = StableGuid($"phase|{request.PanelId}|{leftId}|{rightId}");
        }

        HeadingCandidate[] headings = ResolveHeadingCandidates(request, cancellationToken);
        var decisions = new SemanticDecision[phaseIds.Length];
        var matchedManualLabelIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < decisions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PhaseLabelOverride? manual = request.ManualOverrides.Labels
                .FirstOrDefault(label => string.Equals(label.PhaseId, phaseIds[index], StringComparison.Ordinal));
            if (manual is null)
            {
                manual = BestManualLabelByGeometry(
                    request.ManualOverrides.Labels,
                    matchedManualLabelIds,
                    boundaries[index],
                    boundaries[index + 1],
                    request.Options.DividerClusterTolerancePixels);
                if (manual is not null)
                {
                    warnings.Add("manual_phase_label_matched_by_geometry");
                }
            }

            if (manual is not null)
            {
                matchedManualLabelIds.Add(manual.PhaseId);
                decisions[index] = new SemanticDecision(
                    manual.NormalizedType,
                    manual.Code.Trim(),
                    manual.LabelText.Trim(),
                    1,
                    PhaseEvidenceSource.Manual,
                    ManualCode: true);
                continue;
            }

            HeadingCandidate? heading = BestHeading(
                headings,
                boundaries[index],
                boundaries[index + 1],
                index == decisions.Length - 1);
            if (heading is not null)
            {
                decisions[index] = new SemanticDecision(
                    heading.Type,
                    Code: string.Empty,
                    heading.Text.Trim(),
                    heading.Confidence,
                    heading.Source,
                    ManualCode: false);
                if (heading.Source == PhaseEvidenceSource.CrossPanel)
                {
                    warnings.Add("phase_heading_propagated_from_aligned_panel");
                }

                continue;
            }

            decisions[index] = index switch
            {
                0 => new SemanticDecision(
                    PhaseNormalizedType.Baseline,
                    string.Empty,
                    "Baseline",
                    0.65,
                    PhaseEvidenceSource.ProfilePrior,
                    ManualCode: false),
                1 => new SemanticDecision(
                    PhaseNormalizedType.Intervention,
                    string.Empty,
                    "Intervention",
                    0.65,
                    PhaseEvidenceSource.ProfilePrior,
                    ManualCode: false),
                _ => new SemanticDecision(
                    PhaseNormalizedType.Unknown,
                    string.Empty,
                    $"Phase {index + 1}",
                    0.50,
                    PhaseEvidenceSource.ProfilePrior,
                    ManualCode: false),
            };
            if (index >= 2)
            {
                warnings.Add("later_phase_semantic_unknown");
            }
        }

        PhaseNormalizedType[] profileTypes =
        [
            PhaseNormalizedType.Baseline,
            PhaseNormalizedType.Intervention,
        ];
        string[] profileLabels = ["Baseline", "Intervention"];
        for (var index = 0; index < Math.Min(profileTypes.Length, decisions.Length); index++)
        {
            SemanticDecision decision = decisions[index];
            if (decision.Source == PhaseEvidenceSource.Manual || decision.Type == profileTypes[index])
            {
                continue;
            }

            warnings.Add("phase_heading_conflicts_with_ab_profile");
            decisions[index] = new SemanticDecision(
                profileTypes[index],
                string.Empty,
                profileLabels[index],
                0.65,
                PhaseEvidenceSource.ProfilePrior,
                ManualCode: false);
        }

        var counts = decisions
            .Where(static decision => decision.Type != PhaseNormalizedType.Unknown && !decision.ManualCode)
            .GroupBy(static decision => decision.Type)
            .ToDictionary(static group => group.Key, static group => group.Count());
        var occurrences = new Dictionary<PhaseNormalizedType, int>();
        var phases = new PhaseRegion[decisions.Length];
        for (var index = 0; index < phases.Length; index++)
        {
            SemanticDecision decision = decisions[index];
            string code;
            if (decision.ManualCode)
            {
                code = decision.Code;
            }
            else if (decision.Type == PhaseNormalizedType.Unknown)
            {
                code = $"phase{index + 1}";
            }
            else
            {
                int occurrence = occurrences.GetValueOrDefault(decision.Type) + 1;
                occurrences[decision.Type] = occurrence;
                string prefix = TypeCode(decision.Type);
                code = counts.GetValueOrDefault(decision.Type) > 1
                    ? prefix + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : prefix;
            }

            phases[index] = new PhaseRegion(
                phaseIds[index],
                index + 1,
                code,
                decision.Type,
                decision.LabelText,
                boundaries[index],
                boundaries[index + 1],
                index == 0 ? null : dividers[index - 1].DividerId,
                index == dividers.Length ? null : dividers[index].DividerId,
                decision.Confidence,
                decision.Source);
        }

        if (request.ManualOverrides.Labels.Any(label => !matchedManualLabelIds.Contains(label.PhaseId)))
        {
            warnings.Add("manual_phase_label_target_not_found");
        }

        if (headings.Length == 0)
        {
            warnings.Add("phase_heading_not_detected");
        }

        return phases;
    }

    private static HeadingCandidate[] ResolveHeadingCandidates(
        PhaseReasoningRequest request,
        CancellationToken cancellationToken)
    {
        var candidates = new List<HeadingCandidate>();
        AddHeadingCandidates(
            candidates,
            request.Headings,
            request.PlotBounds,
            request.PlotBounds,
            request.PanelId,
            PhaseEvidenceSource.Ocr,
            request.Options,
            cancellationToken);

        foreach (PhasePanelEvidence panel in request.AlignedPanels
                     .OrderBy(static panel => panel.PlotBounds.Top)
                     .ThenBy(static panel => panel.PanelId, StringComparer.Ordinal))
        {
            AddHeadingCandidates(
                candidates,
                panel.Headings,
                panel.PlotBounds,
                request.PlotBounds,
                panel.PanelId,
                PhaseEvidenceSource.CrossPanel,
                request.Options,
                cancellationToken);
        }

        return candidates.ToArray();
    }

    private static void AddHeadingCandidates(
        ICollection<HeadingCandidate> candidates,
        IReadOnlyList<PhaseHeadingEvidence> headings,
        PhaseRectangle sourcePlot,
        PhaseRectangle targetPlot,
        string sourcePanelId,
        PhaseEvidenceSource source,
        PhaseReasoningOptions options,
        CancellationToken cancellationToken)
    {
        foreach (PhaseHeadingEvidence heading in headings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (heading.Rejected || heading.Confidence < options.MinimumConfidence ||
                VerticalDistance(heading.Bounds, sourcePlot) > options.MaximumHeadingDistancePixels)
            {
                continue;
            }

            PhaseNormalizedType? type = NormalizeHeading(heading.Text);
            if (type is null)
            {
                continue;
            }

            double normalizedX = (heading.Bounds.Center.X - sourcePlot.Left) / sourcePlot.Width;
            double targetX = targetPlot.Left + (normalizedX * targetPlot.Width);
            candidates.Add(new HeadingCandidate(
                heading.HeadingId,
                sourcePanelId,
                targetX,
                heading.Text,
                type.Value,
                source == PhaseEvidenceSource.CrossPanel
                    ? Math.Clamp(heading.Confidence * 0.95, 0, 1)
                    : heading.Confidence,
                source));
        }
    }

    private static HeadingCandidate? BestHeading(
        IReadOnlyList<HeadingCandidate> headings,
        double left,
        double right,
        bool finalPhase)
    {
        double center = left + ((right - left) / 2);
        return headings
            .Where(heading => heading.TargetX >= left && (heading.TargetX < right || (finalPhase && heading.TargetX <= right)))
            .OrderBy(static heading => heading.Source == PhaseEvidenceSource.Ocr ? 0 : 1)
            .ThenByDescending(static heading => heading.Confidence)
            .ThenBy(heading => Math.Abs(heading.TargetX - center))
            .ThenBy(static heading => heading.SourcePanelId, StringComparer.Ordinal)
            .ThenBy(static heading => heading.HeadingId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static PhaseLabelOverride? BestManualLabelByGeometry(
        IReadOnlyList<PhaseLabelOverride> labels,
        HashSet<string> matchedLabelIds,
        double originalXMinimum,
        double originalXMaximum,
        double tolerance) =>
        labels
            .Where(label => !matchedLabelIds.Contains(label.PhaseId))
            .Where(label => LabelMatchesBounds(label, originalXMinimum, originalXMaximum, tolerance))
            .Where(static label => label.OriginalXMinimum is not null && label.OriginalXMaximum is not null)
            .OrderBy(label =>
                Math.Abs(label.OriginalXMinimum!.Value - originalXMinimum) +
                Math.Abs(label.OriginalXMaximum!.Value - originalXMaximum))
            .ThenBy(static label => label.PhaseId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool LabelMatchesBounds(
        PhaseLabelOverride label,
        double originalXMinimum,
        double originalXMaximum,
        double tolerance) =>
        label.OriginalXMinimum is null && label.OriginalXMaximum is null ||
        label.OriginalXMinimum is double labelMinimumValue &&
        label.OriginalXMaximum is double labelMaximumValue &&
        Math.Abs(labelMinimumValue - originalXMinimum) <= tolerance &&
        Math.Abs(labelMaximumValue - originalXMaximum) <= tolerance;

    private static PhasePointAssignment[] AssignPoints(
        IReadOnlyList<PhasePointEvidence> points,
        PhaseRegion[] phases,
        CancellationToken cancellationToken)
    {
        var assignments = new List<PhasePointAssignment>(points.Count);
        foreach (PhasePointEvidence point in points
                     .OrderBy(static point => point.Center.X)
                     .ThenBy(static point => point.PointId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PhaseRegion phase = phases.First(candidate =>
                point.Center.X >= candidate.OriginalXMinimum &&
                (point.Center.X < candidate.OriginalXMaximum || candidate.Order == phases.Length));
            assignments.Add(new PhasePointAssignment(point.PointId, phase.PhaseId, point.Center.X));
        }

        return assignments.ToArray();
    }

    private static PhaseSeriesRelation[] ResolveSeriesRelations(
        IReadOnlyList<PhaseSeriesEvidence> series,
        SortedSet<string> warnings,
        CancellationToken cancellationToken)
    {
        PhaseSeriesEvidence[] interventions = series
            .Where(static item => item.SemanticRole == PhaseNormalizedType.Intervention)
            .OrderBy(static item => item.SeriesId, StringComparer.Ordinal)
            .ToArray();
        PhaseSeriesEvidence[] baselines = series
            .Where(static item => item.SemanticRole == PhaseNormalizedType.Baseline)
            .OrderBy(static item => item.SeriesId, StringComparer.Ordinal)
            .ToArray();
        PhaseSeriesEvidence[] probes = series
            .Where(static item => item.SemanticRole is PhaseNormalizedType.Maintenance or PhaseNormalizedType.Generalization)
            .OrderBy(static item => item.SemanticRole.ToString(), StringComparer.Ordinal)
            .ThenBy(static item => item.SeriesId, StringComparer.Ordinal)
            .ToArray();

        var relations = new List<PhaseSeriesRelation>(interventions.Length);
        foreach (PhaseSeriesEvidence intervention in interventions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PhaseSeriesEvidence[] targetedBaselines = baselines
                .Where(item => item.ApplicableInterventionSeriesIds.Contains(
                    intervention.SeriesId,
                    StringComparer.Ordinal))
                .ToArray();
            PhaseSeriesEvidence[] unscopedBaselines = baselines
                .Where(static item => item.ApplicableInterventionSeriesIds.Count == 0)
                .ToArray();
            string? baselineId = targetedBaselines.Length switch
            {
                1 => targetedBaselines[0].SeriesId,
                > 1 => null,
                _ when unscopedBaselines.Length == 1 => unscopedBaselines[0].SeriesId,
                _ => null,
            };
            if (targetedBaselines.Length > 1 || (targetedBaselines.Length == 0 && unscopedBaselines.Length > 1))
            {
                warnings.Add("shared_baseline_relation_ambiguous");
            }

            string[] applicableProbeIds = probes
                .Where(item => item.ApplicableInterventionSeriesIds.Contains(
                    intervention.SeriesId,
                    StringComparer.Ordinal))
                .Select(static item => item.SeriesId)
                .ToArray();
            relations.Add(new PhaseSeriesRelation(
                intervention.SeriesId,
                baselineId,
                applicableProbeIds));
        }

        return relations.ToArray();
    }

    private static PhaseNormalizedType? NormalizeHeading(string text)
    {
        string normalized = string.Join(
            ' ',
            text.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Contains("generalization", StringComparison.Ordinal) ||
            normalized.Contains("generalisation", StringComparison.Ordinal))
        {
            return PhaseNormalizedType.Generalization;
        }

        if (normalized.Contains("maintenance", StringComparison.Ordinal))
        {
            return PhaseNormalizedType.Maintenance;
        }

        if (normalized.Contains("baseline", StringComparison.Ordinal) ||
            normalized is "withdrawal" or "withdrawal continued")
        {
            return PhaseNormalizedType.Baseline;
        }

        if (normalized.Contains("intervention", StringComparison.Ordinal) ||
            normalized.Contains("treatment", StringComparison.Ordinal) ||
            normalized is "reintroduction" or "reintroduction continued")
        {
            return PhaseNormalizedType.Intervention;
        }

        string compact = new(normalized
            .Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character))
            .ToArray());
        if (IsLetterCode(compact, 'a'))
        {
            return PhaseNormalizedType.Baseline;
        }

        if (IsLetterCode(compact, 'b'))
        {
            return PhaseNormalizedType.Intervention;
        }

        if (IsLetterCode(compact, 'm'))
        {
            return PhaseNormalizedType.Maintenance;
        }

        return IsLetterCode(compact, 'g')
            ? PhaseNormalizedType.Generalization
            : null;
    }

    private static bool IsLetterCode(string value, char prefix) =>
        value.Length >= 1 && value[0] == prefix &&
        (value.Length == 1 || value[1..].All(char.IsDigit));

    private static double VerticalDistance(PhaseRectangle heading, PhaseRectangle plot)
    {
        if (heading.Bottom < plot.Top)
        {
            return plot.Top - heading.Bottom;
        }

        return heading.Top > plot.Bottom ? heading.Top - plot.Bottom : 0;
    }

    private static string TypeCode(PhaseNormalizedType type) => type switch
    {
        PhaseNormalizedType.Baseline => "a",
        PhaseNormalizedType.Intervention => "b",
        PhaseNormalizedType.Maintenance => "m",
        PhaseNormalizedType.Generalization => "g",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown phases use an order-based code."),
    };

    private static PhaseReasoningFailure? Validate(PhaseReasoningRequest? request)
    {
        if (request is null)
        {
            return Error("PHASE_INVALID_REQUEST", "Phase reasoning request is required.");
        }

        if (request.ContractVersion != PhaseReasoningContract.Version)
        {
            return Error("PHASE_CONTRACT_UNSUPPORTED", "The phase reasoning contract version is unsupported.");
        }

        if (!IsUuid(request.ProjectId) || !IsUuid(request.PanelId) || !request.PlotBounds.IsValid)
        {
            return Error("PHASE_INVALID_REQUEST", "Project ID, panel ID, and valid original-pixel plot bounds are required.");
        }

        if (!IsSha256(request.InputSha256))
        {
            return Error("PHASE_INVALID_INPUT_HASH", "Input SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        PhaseReasoningOptions options = request.Options;
        if (!IsPositiveFinite(options.MaximumVerticalDriftPixels) ||
            !IsPositiveFinite(options.DividerClusterTolerancePixels) ||
            !IsPositiveFinite(options.CrossPanelAlignmentTolerancePixels) ||
            !IsProbability(options.MinimumVerticalCoverageFraction) ||
            options.MinimumVerticalCoverageFraction == 0 ||
            !IsNonnegativeFinite(options.BorderExclusionPixels) ||
            !IsPositiveFinite(options.MaximumHeadingDistancePixels) ||
            !IsProbability(options.MinimumConfidence) ||
            string.IsNullOrWhiteSpace(options.StageVersion))
        {
            return Error("PHASE_INVALID_OPTIONS", "Phase thresholds and stage version must be finite and valid.");
        }

        var segmentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (PhaseDividerSegment segment in request.Segments)
        {
            if (!ValidSegment(segment, request.PanelId, segmentIds))
            {
                return Error("PHASE_INVALID_SEGMENT", "Divider segment evidence must be unique, finite, and panel-local.");
            }
        }

        var headingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (PhaseHeadingEvidence heading in request.Headings)
        {
            if (!ValidHeading(heading, request.PanelId, headingIds))
            {
                return Error("PHASE_INVALID_HEADING", "Heading evidence must be unique, finite, and panel-local.");
            }
        }

        var pointIds = new HashSet<string>(StringComparer.Ordinal);
        var pointsById = new Dictionary<string, PhasePointEvidence>(StringComparer.Ordinal);
        foreach (PhasePointEvidence point in request.Points)
        {
            if (point is null || !IsUuid(point.PointId) || !pointIds.Add(point.PointId) ||
                !IsUuid(point.SeriesId) || !string.Equals(point.PanelId, request.PanelId, StringComparison.Ordinal) ||
                !point.Center.IsFinite || !request.PlotBounds.Contains(point.Center))
            {
                return Error("PHASE_INVALID_POINT", "Point evidence must be unique and inside the target plot in original pixels.");
            }

            pointsById.Add(point.PointId, point);
        }

        var seriesIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (PhaseSeriesEvidence item in request.Series)
        {
            if (item is null || !IsUuid(item.SeriesId) || !seriesIds.Add(item.SeriesId) ||
                !Enum.IsDefined(item.SemanticRole) ||
                item.PointIds.Any(pointId => !IsUuid(pointId)) ||
                item.PointIds.Distinct(StringComparer.Ordinal).Count() != item.PointIds.Count ||
                item.ApplicableInterventionSeriesIds.Any(seriesId => !IsUuid(seriesId)) ||
                item.ApplicableInterventionSeriesIds.Distinct(StringComparer.Ordinal).Count() !=
                item.ApplicableInterventionSeriesIds.Count)
            {
                return Error("PHASE_INVALID_SERIES", "Series evidence and reference IDs must be unique and valid.");
            }
        }

        var ownedPointIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (PhaseSeriesEvidence item in request.Series)
        {
            foreach (string pointId in item.PointIds)
            {
                if (!pointsById.TryGetValue(pointId, out PhasePointEvidence? point) ||
                    !string.Equals(point.SeriesId, item.SeriesId, StringComparison.Ordinal) ||
                    !ownedPointIds.Add(pointId))
                {
                    return Error("PHASE_INVALID_SERIES", "Every point must belong to exactly one matching series.");
                }
            }
        }

        if (ownedPointIds.Count != request.Points.Count)
        {
            return Error("PHASE_INVALID_SERIES", "Every supplied point must be referenced by its owning series.");
        }

        HashSet<string> interventionIds = request.Series
            .Where(static item => item.SemanticRole == PhaseNormalizedType.Intervention)
            .Select(static item => item.SeriesId)
            .ToHashSet(StringComparer.Ordinal);
        if (request.Series.SelectMany(static item => item.ApplicableInterventionSeriesIds)
            .Any(seriesId => !interventionIds.Contains(seriesId)))
        {
            return Error("PHASE_INVALID_SERIES_RELATION", "Applicable intervention references must target intervention series.");
        }

        var panelIds = new HashSet<string>(StringComparer.Ordinal) { request.PanelId };
        foreach (PhasePanelEvidence panel in request.AlignedPanels)
        {
            if (panel is null || !IsUuid(panel.PanelId) || !panelIds.Add(panel.PanelId) || !panel.PlotBounds.IsValid)
            {
                return Error("PHASE_INVALID_ALIGNED_PANEL", "Aligned panels must have unique IDs and valid plot bounds.");
            }

            var peerSegmentIds = new HashSet<string>(StringComparer.Ordinal);
            if (panel.Segments.Any(segment => !ValidSegment(segment, panel.PanelId, peerSegmentIds)))
            {
                return Error("PHASE_INVALID_ALIGNED_PANEL", "Aligned-panel divider evidence is invalid.");
            }

            var peerHeadingIds = new HashSet<string>(StringComparer.Ordinal);
            if (panel.Headings.Any(heading => !ValidHeading(heading, panel.PanelId, peerHeadingIds)))
            {
                return Error("PHASE_INVALID_ALIGNED_PANEL", "Aligned-panel heading evidence is invalid.");
            }
        }

        var manualDividerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (PhaseManualDivider divider in request.ManualOverrides.Dividers)
        {
            if (divider is null || !IsUuid(divider.DividerId) ||
                 !manualDividerIds.Add(divider.DividerId) || !double.IsFinite(divider.OriginalX) ||
                 divider.OriginalX <= request.PlotBounds.Left || divider.OriginalX >= request.PlotBounds.Right ||
                 (divider.ReplacedAutomaticOriginalX is double replacedX &&
                  (!double.IsFinite(replacedX) || replacedX <= request.PlotBounds.Left ||
                   replacedX >= request.PlotBounds.Right)) ||
                 !Enum.IsDefined(divider.Style) || !IsProbability(divider.Confidence))
            {
                return Error("PHASE_INVALID_MANUAL_OVERRIDE", "Manual dividers must be unique and inside the plot.");
            }
        }

        var deletedDividerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (PhaseDeletedDivider deletedDivider in request.ManualOverrides.DeletedDividers)
        {
            if (!IsUuid(deletedDivider.DividerId) || !deletedDividerIds.Add(deletedDivider.DividerId) ||
                (deletedDivider.ReplacedAutomaticOriginalX is double replacedX &&
                 (!double.IsFinite(replacedX) || replacedX <= request.PlotBounds.Left ||
                  replacedX >= request.PlotBounds.Right)))
            {
                return Error("PHASE_INVALID_MANUAL_OVERRIDE", "Deleted divider lineage must be unique and inside the plot.");
            }
        }

        if (manualDividerIds.Overlaps(deletedDividerIds))
        {
            return Error("PHASE_INVALID_MANUAL_OVERRIDE", "Deleted divider IDs must be unique and cannot remain active.");
        }

        var overridePhaseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (PhaseLabelOverride label in request.ManualOverrides.Labels)
        {
            if (label is null || !IsUuid(label.PhaseId) || !overridePhaseIds.Add(label.PhaseId) ||
                string.IsNullOrWhiteSpace(label.Code) || string.IsNullOrWhiteSpace(label.LabelText) ||
                !ValidOptionalLabelBounds(label, request.PlotBounds) ||
                !Enum.IsDefined(label.NormalizedType))
            {
                return Error("PHASE_INVALID_MANUAL_OVERRIDE", "Manual labels must be unique and contain a code, type, and text.");
            }
        }

        return null;
    }

    private static bool ValidSegment(
        PhaseDividerSegment? segment,
        string panelId,
        HashSet<string> ids) =>
        segment is not null &&
        !string.IsNullOrWhiteSpace(segment.SegmentId) &&
        ids.Add(segment.SegmentId) &&
        string.Equals(segment.PanelId, panelId, StringComparison.Ordinal) &&
        segment.Start.IsFinite && segment.End.IsFinite &&
        IsPositiveFinite(segment.Thickness) &&
        Enum.IsDefined(segment.Style) && Enum.IsDefined(segment.Kind) &&
        IsProbability(segment.Confidence);

    private static bool ValidHeading(
        PhaseHeadingEvidence? heading,
        string panelId,
        HashSet<string> ids) =>
        heading is not null &&
        !string.IsNullOrWhiteSpace(heading.HeadingId) &&
        ids.Add(heading.HeadingId) &&
        string.Equals(heading.PanelId, panelId, StringComparison.Ordinal) &&
        heading.Bounds.IsValid && heading.Text is not null &&
        IsProbability(heading.Confidence);

    private static bool ValidOptionalLabelBounds(PhaseLabelOverride label, PhaseRectangle plotBounds) =>
        label.OriginalXMinimum is null && label.OriginalXMaximum is null ||
        label.OriginalXMinimum is double minimum && label.OriginalXMaximum is double maximum &&
        double.IsFinite(minimum) && double.IsFinite(maximum) &&
        minimum >= plotBounds.Left && maximum <= plotBounds.Right && minimum < maximum;

    private static PhaseReasoningFailure? ValidateDetectedDividers(
        PhaseReasoningRequest request,
        IReadOnlyList<PhaseDivider>? dividers)
    {
        if (dividers is null)
        {
            return Error("PHASE_INVALID_DIVIDER_OUTPUT", "The divider detector returned a null collection.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> knownPanelIds = request.AlignedPanels
            .Select(static panel => panel.PanelId)
            .Append(request.PanelId)
            .ToHashSet(StringComparer.Ordinal);
        double previousX = double.NegativeInfinity;
        foreach (PhaseDivider divider in dividers.OrderBy(static item => item.OriginalX))
        {
            if (divider is null || !IsUuid(divider.DividerId) || !ids.Add(divider.DividerId) ||
                !double.IsFinite(divider.OriginalX) ||
                divider.OriginalX <= request.PlotBounds.Left || divider.OriginalX >= request.PlotBounds.Right ||
                divider.OriginalX - previousX < request.Options.DividerClusterTolerancePixels ||
                !Enum.IsDefined(divider.Style) || !Enum.IsDefined(divider.Source) ||
                !IsProbability(divider.Confidence) ||
                divider.SegmentIds.Any(string.IsNullOrWhiteSpace) ||
                divider.SegmentIds.Distinct(StringComparer.Ordinal).Count() != divider.SegmentIds.Count ||
                divider.SourcePanelIds.Count == 0 ||
                divider.SourcePanelIds.Any(panelId => !IsUuid(panelId) || !knownPanelIds.Contains(panelId)) ||
                divider.SourcePanelIds.Distinct(StringComparer.Ordinal).Count() != divider.SourcePanelIds.Count)
            {
                return Error("PHASE_INVALID_DIVIDER_OUTPUT", "Detected dividers must be unique, separated, and inside the plot.");
            }

            previousX = divider.OriginalX;
        }

        return null;
    }

    private static double OverallConfidence(
        IReadOnlyList<PhaseDivider> dividers,
        IReadOnlyList<PhaseRegion> phases)
    {
        double[] evidence = dividers.Select(static divider => divider.Confidence)
            .Concat(phases.Select(static phase => phase.Confidence))
            .ToArray();
        return evidence.Length == 0 ? 0 : Math.Clamp(evidence.Average(), 0, 1);
    }

    private static PhaseReasoningResult Failed(
        PhaseReasoningRequest? request,
        string runId,
        PhaseReasoningFailure failure,
        double preprocessMilliseconds,
        double inferenceMilliseconds,
        double postprocessMilliseconds,
        double totalMilliseconds) =>
        new(
            PhaseReasoningContract.Version,
            runId,
            IsUuid(request?.ProjectId ?? string.Empty) ? request!.ProjectId : Guid.Empty.ToString(),
            IsUuid(request?.PanelId ?? string.Empty) ? request!.PanelId : Guid.Empty.ToString(),
            PhaseReasoningContract.Stage,
            string.IsNullOrWhiteSpace(request?.Options?.StageVersion)
                ? PhaseReasoningContract.StageVersion
                : request.Options.StageVersion,
            IsSha256(request?.InputSha256 ?? string.Empty) ? request!.InputSha256 : new string('0', 64),
            PhaseReasoningContract.CoordinateSpace,
            new PhaseReasoningPayload(
                Array.Empty<PhaseDivider>(),
                Array.Empty<PhaseRegion>(),
                Array.Empty<PhasePointAssignment>(),
                Array.Empty<PhaseSeriesRelation>(),
                request?.ManualOverrides ?? new PhaseManualOverrides()),
            new PhaseReasoningTiming(
                preprocessMilliseconds,
                inferenceMilliseconds,
                postprocessMilliseconds,
                totalMilliseconds),
            0,
            Array.Empty<string>(),
            failure);

    private static PhaseReasoningFailure Error(string code, string technicalMessage) =>
        new(code, "error", "Errors." + code, technicalMessage, true, "review_phase_evidence");

    private static string CreateRunId() => Guid.NewGuid().ToString();

    private static string StableGuid(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString();
    }

    private static bool IsUuid(string value) => Guid.TryParseExact(value, "D", out _);

    private static bool IsSha256(string value) =>
        value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsProbability(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static bool IsNonnegativeFinite(double value) => double.IsFinite(value) && value >= 0;

    private sealed record HeadingCandidate(
        string HeadingId,
        string SourcePanelId,
        double TargetX,
        string Text,
        PhaseNormalizedType Type,
        double Confidence,
        PhaseEvidenceSource Source);

    private sealed record SemanticDecision(
        PhaseNormalizedType Type,
        string Code,
        string LabelText,
        double Confidence,
        PhaseEvidenceSource Source,
        bool ManualCode);
}
