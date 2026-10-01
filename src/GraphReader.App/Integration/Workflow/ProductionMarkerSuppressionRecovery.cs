// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Reconsiders suppressed proposals after their blocking artifacts are rejected.</summary>
internal static class ProductionMarkerSuppressionRecovery
{
    internal const string Version = "original-pixel-rejected-suppression-recovery-v1";

    internal static IReadOnlyList<MarkerCenter> Find(
        ProposalMarkerSuppressionEvidence evidence,
        IReadOnlyList<MarkerCenter> initialCenters,
        IReadOnlyList<ClassifiedMarker> initialClassification,
        IReadOnlyList<ClassifiedMarker> accepted,
        MarkerPolygon plot,
        IReadOnlyList<OcrRegion> regions,
        IReadOnlyList<OcrMask> eligibleTextMasks,
        IReadOnlyList<OcrRectangle> legendFrames,
        double artifactThreshold,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(evidence);
        var initialGeometry = initialCenters.Select(Geometry).ToHashSet();
        var classified = initialClassification.ToDictionary(static marker => marker.Marker.MarkerId,
            StringComparer.Ordinal);
        var maskIds = eligibleTextMasks.Select(static mask => mask.RegionId).ToHashSet(StringComparer.Ordinal);
        MarkerPolygon[] text = regions.Where(region => maskIds.Contains(region.RegionId))
            .Select(static region => new MarkerPolygon(region.Polygon.Points
                .Select(static point => new MarkerPoint(point.X, point.Y)).ToArray())).ToArray();
        var found = new List<MarkerCenter>();
        foreach (MarkerCenter candidate in Ordered(evidence.Candidates).DistinctBy(Geometry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (initialGeometry.Contains(Geometry(candidate)) || !plot.Contains(candidate.Center) ||
                text.Any(polygon => polygon.Contains(candidate.Center)) ||
                legendFrames.Any(box => Contains(box, candidate.Center)) ||
                accepted.Any(marker => Conflicts(candidate, marker.Marker, evidence))) continue;

            MarkerCenter[] blockers = initialCenters.Where(marker => Conflicts(candidate, marker, evidence)).ToArray();
            // Missing classification or a surviving neighbor cannot authorize recovery.
            if (blockers.Length == 0 || blockers.Any(marker =>
                !classified.TryGetValue(marker.MarkerId, out ClassifiedMarker? result) ||
                result.ArtifactProbability < artifactThreshold)) continue;
            found.Add(candidate);
        }
        return found.AsReadOnly();
    }

    internal static IReadOnlyList<ClassifiedMarker> SelectNew(
        ProposalMarkerSuppressionEvidence evidence,
        IReadOnlyList<ClassifiedMarker> existing,
        IReadOnlyList<ClassifiedMarker> classified,
        double artifactThreshold,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(evidence);
        var retained = existing.Select(static marker => marker.Marker).ToList();
        var added = new List<ClassifiedMarker>();
        foreach (ClassifiedMarker candidate in classified
                     .OrderByDescending(static marker => marker.Marker.CenterConfidence)
                     .ThenBy(static marker => marker.Marker.Center.Y)
                     .ThenBy(static marker => marker.Marker.Center.X))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.ArtifactProbability >= artifactThreshold ||
                retained.Any(marker => Conflicts(candidate.Marker, marker, evidence))) continue;
            retained.Add(candidate.Marker);
            added.Add(candidate);
        }
        return added.AsReadOnly();
    }

    private static IOrderedEnumerable<MarkerCenter> Ordered(IEnumerable<MarkerCenter> markers) =>
        markers.OrderByDescending(static marker => marker.CenterConfidence)
            .ThenBy(static marker => marker.Center.Y).ThenBy(static marker => marker.Center.X);

    private static (double X, double Y, double Radius) Geometry(MarkerCenter marker) =>
        (marker.Center.X, marker.Center.Y, marker.Radius);

    private static bool Conflicts(MarkerCenter left, MarkerCenter right, ProposalMarkerSuppressionEvidence evidence) =>
        Math.Sqrt(Math.Pow(left.Center.X - right.Center.X, 2) + Math.Pow(left.Center.Y - right.Center.Y, 2)) <
        Math.Max(evidence.MinimumSeparation, evidence.RadiusScale * Math.Max(left.Radius, right.Radius));

    private static bool Contains(OcrRectangle box, MarkerPoint point) =>
        point.X >= box.Left && point.X < box.Right && point.Y >= box.Top && point.Y < box.Bottom;

    private static void Validate(ProposalMarkerSuppressionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!double.IsFinite(evidence.MinimumSeparation) || evidence.MinimumSeparation <= 0 ||
            !double.IsFinite(evidence.RadiusScale) || evidence.RadiusScale <= 0)
            throw new ArgumentException("Suppression recovery requires the detector's finite positive NMS distances.", nameof(evidence));
    }
}
