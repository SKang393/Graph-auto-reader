// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Phases;

public sealed class PhaseDividerDetector : IPhaseDividerDetector
{
    public IReadOnlyList<PhaseDivider> Detect(
        PhaseReasoningRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsValidRequest(request))
        {
            return Array.Empty<PhaseDivider>();
        }

        DetectedDivider[] localDividers = DetectPanel(
            request.ProjectId,
            request.PanelId,
            request.PlotBounds,
            request.Segments,
            request.Options,
            PhaseEvidenceSource.ProfilePrior,
            cancellationToken);

        List<DetectedDivider> propagatedDividers = [];
        foreach (PhasePanelEvidence panel in request.AlignedPanels
                     .Where(panel => !string.Equals(panel.PanelId, request.PanelId, StringComparison.Ordinal))
                     .Where(static panel => panel.ShareDividersWithTarget)
                     .OrderBy(static panel => panel.PanelId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidPanel(panel))
            {
                continue;
            }

            DetectedDivider[] panelDividers = DetectPanel(
                request.ProjectId,
                panel.PanelId,
                panel.PlotBounds,
                panel.Segments,
                request.Options,
                PhaseEvidenceSource.CrossPanel,
                cancellationToken);

            foreach (DetectedDivider divider in panelDividers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double relativeX = (divider.X - panel.PlotBounds.Left) / panel.PlotBounds.Width;
                double mappedX = request.PlotBounds.Left + (relativeX * request.PlotBounds.Width);
                if (IsInteriorX(mappedX, request.PlotBounds, request.Options.BorderExclusionPixels))
                {
                    propagatedDividers.Add(divider with { X = mappedX });
                }
            }
        }

        DetectedDivider[] combined = CombinePanelEvidence(
            localDividers,
            propagatedDividers,
            request.Options.CrossPanelAlignmentTolerancePixels,
            cancellationToken);

        PhaseDivider[] automaticDividers = combined
            .Select(divider => ToContract(request, divider))
            .ToArray();

        return ApplyManualOverrides(
            automaticDividers,
            request.ManualOverrides,
            request.PanelId,
            request.PlotBounds,
            request.Options.DividerClusterTolerancePixels,
            cancellationToken);
    }

    private static DetectedDivider[] DetectPanel(
        string projectId,
        string panelId,
        PhaseRectangle plotBounds,
        IEnumerable<PhaseDividerSegment> segments,
        PhaseReasoningOptions options,
        PhaseEvidenceSource source,
        CancellationToken cancellationToken)
    {
        SegmentEvidence[] candidates = segments
            .Where(segment => IsEligibleSegment(segment, panelId, plotBounds, options))
            .Select(segment => ToEvidence(segment, plotBounds))
            .OrderBy(static segment => segment.X)
            .ThenBy(static segment => segment.Segment.PanelId, StringComparer.Ordinal)
            .ThenBy(static segment => segment.Segment.SegmentId, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            return [];
        }

        List<List<SegmentEvidence>> clusters = [];
        List<SegmentEvidence>? current = null;
        double clusterWeightedX = 0;
        double clusterWeight = 0;

        foreach (SegmentEvidence candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current is null || candidate.X - (clusterWeight > 0 ? clusterWeightedX / clusterWeight : current[0].X) >
                options.DividerClusterTolerancePixels)
            {
                current = [];
                clusters.Add(current);
                clusterWeightedX = 0;
                clusterWeight = 0;
            }

            current.Add(candidate);
            clusterWeightedX += candidate.X * candidate.Weight;
            clusterWeight += candidate.Weight;
        }

        List<DetectedDivider> dividers = [];
        foreach (List<SegmentEvidence> cluster in clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double coverage = CalculateCoverage(cluster, plotBounds);
            if (coverage < options.MinimumVerticalCoverageFraction)
            {
                continue;
            }

            double totalWeight = cluster.Sum(static segment => segment.Weight);
            if (!double.IsFinite(totalWeight) || totalWeight <= 0)
            {
                continue;
            }

            double x = cluster.All(segment => segment.X == cluster[0].X)
                ? cluster[0].X
                : cluster.Sum(segment => segment.X * segment.Weight) / totalWeight;
            double confidence = cluster.Sum(
                segment => segment.Segment.Confidence * segment.Weight) / totalWeight;
            PhaseDividerStyle style = cluster
                .GroupBy(static segment => segment.Segment.Style)
                .Select(group => new
                {
                    Style = group.Key,
                    Weight = group.Sum(static segment => segment.Weight),
                })
                .OrderByDescending(static group => group.Weight)
                .ThenBy(static group => group.Style)
                .First()
                .Style;

            string[] segmentIds = cluster
                .Select(static segment => segment.Segment.SegmentId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            dividers.Add(new DetectedDivider(
                string.Empty,
                x,
                style,
                segmentIds,
                [panelId],
                Math.Clamp(confidence, 0, 1),
                source));
        }

        return dividers
            .OrderBy(static divider => divider.X)
            .Select(divider => divider with
            {
                DividerId = StableId(
                    "phase-divider",
                    projectId,
                    panelId,
                    divider.X.ToString("R", CultureInfo.InvariantCulture)),
            })
            .ToArray();
    }

    private static DetectedDivider[] CombinePanelEvidence(
        IReadOnlyList<DetectedDivider> localDividers,
        IReadOnlyList<DetectedDivider> propagatedDividers,
        double tolerance,
        CancellationToken cancellationToken)
    {
        List<DetectedDivider> combined = localDividers
            .OrderBy(static divider => divider.X)
            .ThenBy(static divider => divider.DividerId, StringComparer.Ordinal)
            .ToList();
        List<DetectedDivider> unmatchedPropagated = [];

        foreach (DetectedDivider propagated in propagatedDividers
                     .OrderBy(static divider => divider.X)
                     .ThenBy(static divider => divider.DividerId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int matchIndex = Enumerable.Range(0, combined.Count)
                .Where(index => combined[index].Source != PhaseEvidenceSource.CrossPanel)
                .Where(index => Math.Abs(combined[index].X - propagated.X) <= tolerance)
                .OrderBy(index => Math.Abs(combined[index].X - propagated.X))
                .ThenBy(index => combined[index].DividerId, StringComparer.Ordinal)
                .DefaultIfEmpty(-1)
                .First();

            if (matchIndex < 0)
            {
                unmatchedPropagated.Add(propagated);
                continue;
            }

            DetectedDivider local = combined[matchIndex];
            combined[matchIndex] = local with
            {
                SegmentIds = MergeStrings(local.SegmentIds, propagated.SegmentIds),
                SourcePanelIds = MergeStrings(local.SourcePanelIds, propagated.SourcePanelIds),
                Confidence = CombineConfidence(local.Confidence, propagated.Confidence),
            };
        }

        foreach (IReadOnlyList<DetectedDivider> cluster in ClusterPropagated(
                     unmatchedPropagated,
                     tolerance,
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            double totalWeight = cluster.Sum(static divider => Math.Max(divider.Confidence, double.Epsilon));
            double x = cluster.Sum(
                divider => divider.X * Math.Max(divider.Confidence, double.Epsilon)) / totalWeight;
            PhaseDividerStyle style = cluster
                .GroupBy(static divider => divider.Style)
                .Select(group => new
                {
                    Style = group.Key,
                    Confidence = group.Sum(static divider => divider.Confidence),
                })
                .OrderByDescending(static group => group.Confidence)
                .ThenBy(static group => group.Style)
                .First()
                .Style;
            string[] segmentIds = cluster
                .SelectMany(static divider => divider.SegmentIds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] panelIds = cluster
                .SelectMany(static divider => divider.SourcePanelIds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] identity = cluster
                .Select(static divider => divider.DividerId)
                .Order(StringComparer.Ordinal)
                .ToArray();

            combined.Add(new DetectedDivider(
                StableId("cross-panel-phase-divider", identity),
                x,
                style,
                segmentIds,
                panelIds,
                cluster.Select(static divider => divider.Confidence).Aggregate(CombineConfidence),
                PhaseEvidenceSource.CrossPanel));
        }

        return combined
            .OrderBy(static divider => divider.X)
            .ThenBy(static divider => divider.DividerId, StringComparer.Ordinal)
            .ToArray();
    }

    private static List<IReadOnlyList<DetectedDivider>> ClusterPropagated(
        IEnumerable<DetectedDivider> propagatedDividers,
        double tolerance,
        CancellationToken cancellationToken)
    {
        DetectedDivider[] ordered = propagatedDividers
            .OrderBy(static divider => divider.X)
            .ThenBy(static divider => divider.DividerId, StringComparer.Ordinal)
            .ToArray();
        List<IReadOnlyList<DetectedDivider>> clusters = [];
        List<DetectedDivider>? current = null;
        double clusterMinimumX = 0;

        foreach (DetectedDivider divider in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current is null || divider.X - clusterMinimumX > tolerance)
            {
                current = [];
                clusters.Add(current);
                clusterMinimumX = divider.X;
            }

            current.Add(divider);
        }

        return clusters;
    }

    private static PhaseDivider ToContract(PhaseReasoningRequest request, DetectedDivider divider) =>
        new(
            divider.Source == PhaseEvidenceSource.CrossPanel
                ? StableId(
                    "cross-panel-phase-divider-target",
                    request.ProjectId,
                    request.PanelId,
                    divider.DividerId)
                : divider.DividerId,
            divider.X,
            divider.Style,
            divider.SegmentIds,
            divider.SourcePanelIds,
            divider.Confidence,
            divider.Source);

    private static IReadOnlyList<PhaseDivider> ApplyManualOverrides(
        IEnumerable<PhaseDivider> automaticDividers,
        PhaseManualOverrides overrides,
        string panelId,
        PhaseRectangle plotBounds,
        double lineageTolerance,
        CancellationToken cancellationToken)
    {
        Dictionary<string, PhaseDivider> byId = automaticDividers
            .ToDictionary(static divider => divider.DividerId, StringComparer.Ordinal);

        foreach (PhaseDeletedDivider deleted in overrides.DeletedDividers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deleted.ReplacedAutomaticOriginalX is not double replacedX)
            {
                byId.Remove(deleted.DividerId);
                continue;
            }

            if (byId.TryGetValue(deleted.DividerId, out PhaseDivider? exact) &&
                Math.Abs(exact.OriginalX - replacedX) <= lineageTolerance)
            {
                byId.Remove(deleted.DividerId);
            }

            string? lineageId = byId.Values
                .Where(divider => Math.Abs(divider.OriginalX - replacedX) <= lineageTolerance)
                .OrderBy(divider => Math.Abs(divider.OriginalX - replacedX))
                .ThenBy(static divider => divider.DividerId, StringComparer.Ordinal)
                .Select(static divider => divider.DividerId)
                .FirstOrDefault();
            if (lineageId is not null)
            {
                byId.Remove(lineageId);
            }
        }

        foreach (PhaseManualDivider manual in overrides.Dividers
                     .Where(IsValidManualDivider)
                     .Where(manual => manual.OriginalX > plotBounds.Left && manual.OriginalX < plotBounds.Right)
                     .Where(manual => manual.ReplacedAutomaticOriginalX is not double replacedX ||
                                      (replacedX > plotBounds.Left && replacedX < plotBounds.Right))
                     .GroupBy(static manual => manual.DividerId, StringComparer.Ordinal)
                     .Select(static group => group
                         .OrderByDescending(static manual => manual.Confidence)
                         .ThenBy(static manual => manual.OriginalX)
                         .ThenBy(static manual => manual.Style)
                         .First())
                     .OrderBy(static manual => manual.DividerId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (overrides.DeletedDividerIds.Contains(manual.DividerId, StringComparer.Ordinal))
            {
                continue;
            }

            byId.TryGetValue(manual.DividerId, out PhaseDivider? detected);
            if (detected is not null &&
                manual.ReplacedAutomaticOriginalX is double directReplacedX &&
                Math.Abs(detected.OriginalX - directReplacedX) > lineageTolerance)
            {
                detected = null;
            }

            if (detected is null && manual.ReplacedAutomaticOriginalX is double replacedX)
            {
                string? replacedId = byId.Values
                    .Where(divider => Math.Abs(divider.OriginalX - replacedX) <= lineageTolerance)
                    .OrderBy(divider => Math.Abs(divider.OriginalX - replacedX))
                    .ThenBy(static divider => divider.DividerId, StringComparer.Ordinal)
                    .Select(static divider => divider.DividerId)
                    .FirstOrDefault();
                if (replacedId is not null)
                {
                    detected = byId[replacedId];
                    byId.Remove(replacedId);
                }
            }

            byId[manual.DividerId] = new PhaseDivider(
                manual.DividerId,
                manual.OriginalX,
                manual.Style,
                detected?.SegmentIds ?? Array.Empty<string>(),
                detected?.SourcePanelIds ?? new[] { panelId },
                manual.Confidence,
                PhaseEvidenceSource.Manual);
        }

        return PhaseCollections.Freeze(byId.Values
            .OrderBy(static divider => divider.OriginalX)
            .ThenBy(static divider => divider.DividerId, StringComparer.Ordinal));
    }

    private static SegmentEvidence ToEvidence(
        PhaseDividerSegment segment,
        PhaseRectangle plotBounds)
    {
        double top = Math.Max(Math.Min(segment.Start.Y, segment.End.Y), plotBounds.Top);
        double bottom = Math.Min(Math.Max(segment.Start.Y, segment.End.Y), plotBounds.Bottom);
        double length = bottom - top;
        return new SegmentEvidence(
            segment,
            (segment.Start.X + segment.End.X) / 2,
            top,
            bottom,
            length * segment.Confidence);
    }

    private static double CalculateCoverage(
        IReadOnlyList<SegmentEvidence> cluster,
        PhaseRectangle plotBounds)
    {
        (double Top, double Bottom)[] intervals = cluster
            .Select(static segment => (segment.Top, segment.Bottom))
            .OrderBy(static interval => interval.Top)
            .ThenBy(static interval => interval.Bottom)
            .ToArray();

        double covered = 0;
        double currentTop = intervals[0].Top;
        double currentBottom = intervals[0].Bottom;
        for (int index = 1; index < intervals.Length; index++)
        {
            (double top, double bottom) = intervals[index];
            if (top <= currentBottom)
            {
                currentBottom = Math.Max(currentBottom, bottom);
                continue;
            }

            covered += currentBottom - currentTop;
            currentTop = top;
            currentBottom = bottom;
        }

        covered += currentBottom - currentTop;
        double unionCoverage = covered / plotBounds.Height;
        bool isSegmented = cluster.Count >= 2 && cluster.Any(
            static segment => segment.Segment.Style is PhaseDividerStyle.Dashed or PhaseDividerStyle.Dotted);
        if (!isSegmented)
        {
            return unionCoverage;
        }

        double spanCoverage = (intervals[^1].Bottom - intervals[0].Top) / plotBounds.Height;
        return Math.Max(unionCoverage, spanCoverage);
    }

    private static bool IsEligibleSegment(
        PhaseDividerSegment segment,
        string panelId,
        PhaseRectangle plotBounds,
        PhaseReasoningOptions options)
    {
        if (segment.Kind != PhaseSegmentKind.Candidate ||
            !string.Equals(segment.PanelId, panelId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(segment.SegmentId) ||
            !segment.Start.IsFinite ||
            !segment.End.IsFinite ||
            !IsPositiveFinite(segment.Thickness) ||
            !IsUnitInterval(segment.Confidence) ||
            segment.Confidence < options.MinimumConfidence ||
            Math.Abs(segment.Start.X - segment.End.X) > options.MaximumVerticalDriftPixels)
        {
            return false;
        }

        double x = (segment.Start.X + segment.End.X) / 2;
        double top = Math.Max(Math.Min(segment.Start.Y, segment.End.Y), plotBounds.Top);
        double bottom = Math.Min(Math.Max(segment.Start.Y, segment.End.Y), plotBounds.Bottom);
        return IsInteriorX(x, plotBounds, options.BorderExclusionPixels) && bottom > top;
    }

    private static bool IsInteriorX(double x, PhaseRectangle plotBounds, double exclusion) =>
        double.IsFinite(x) &&
        x > plotBounds.Left + exclusion &&
        x < plotBounds.Right - exclusion;

    private static bool IsValidRequest(PhaseReasoningRequest request)
    {
        PhaseReasoningOptions options = request.Options;
        return request.ContractVersion == PhaseReasoningContract.Version &&
               !string.IsNullOrWhiteSpace(request.ProjectId) &&
               !string.IsNullOrWhiteSpace(request.PanelId) &&
               request.PlotBounds.IsValid &&
               IsNonnegativeFinite(options.MaximumVerticalDriftPixels) &&
               IsPositiveFinite(options.DividerClusterTolerancePixels) &&
               IsPositiveFinite(options.CrossPanelAlignmentTolerancePixels) &&
               IsUnitInterval(options.MinimumVerticalCoverageFraction) &&
               IsNonnegativeFinite(options.BorderExclusionPixels) &&
               IsUnitInterval(options.MinimumConfidence);
    }

    private static bool IsValidPanel(PhasePanelEvidence panel) =>
        !string.IsNullOrWhiteSpace(panel.PanelId) && panel.PlotBounds.IsValid;

    private static bool IsValidManualDivider(PhaseManualDivider divider) =>
        !string.IsNullOrWhiteSpace(divider.DividerId) &&
        double.IsFinite(divider.OriginalX) &&
        (divider.ReplacedAutomaticOriginalX is not double replacedX || double.IsFinite(replacedX)) &&
        Enum.IsDefined(divider.Style) &&
        IsUnitInterval(divider.Confidence);

    private static bool IsNonnegativeFinite(double value) => double.IsFinite(value) && value >= 0;

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static bool IsUnitInterval(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    private static double CombineConfidence(double left, double right) =>
        Math.Clamp(1 - ((1 - left) * (1 - right)), 0, 1);

    private static string[] MergeStrings(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Concat(right)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string StableId(string kind, params string[] components)
    {
        var material = new StringBuilder();
        foreach (string component in new[] { kind }.Concat(components))
        {
            material.Append(Encoding.UTF8.GetByteCount(component).ToString(CultureInfo.InvariantCulture));
            material.Append(':');
            material.Append(component);
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
        return new Guid(digest.AsSpan(0, 16)).ToString("D", CultureInfo.InvariantCulture);
    }

    private sealed record SegmentEvidence(
        PhaseDividerSegment Segment,
        double X,
        double Top,
        double Bottom,
        double Weight);

    private sealed record DetectedDivider(
        string DividerId,
        double X,
        PhaseDividerStyle Style,
        IReadOnlyList<string> SegmentIds,
        IReadOnlyList<string> SourcePanelIds,
        double Confidence,
        PhaseEvidenceSource Source);
}
