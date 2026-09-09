// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.Security.Cryptography;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record EngaugeWorkflowProjectIdentity(
    string CaseKey,
    string ProjectSha256,
    int SeriesCount,
    int PointCount,
    IReadOnlyList<EngaugeDigAxisAnchor> Anchors);

/// <summary>
/// Evaluator-only grouping. The runtime receives only CopyImageBytes and the
/// source identity, never Projects, TruthCase, or curve and point identities.
/// </summary>
internal sealed class EngaugeWorkflowImageGroup
{
    private readonly byte[] imageBytes;

    internal EngaugeWorkflowImageGroup(
        byte[] imageBytes,
        WholeWorkflowTruthCase truthCase,
        IReadOnlyList<EngaugeWorkflowProjectIdentity> projects,
        int coincidentCrossProjectPointPairs)
    {
        this.imageBytes = (byte[])imageBytes.Clone();
        TruthCase = truthCase;
        Projects = Array.AsReadOnly(projects.ToArray());
        CoincidentCrossProjectPointPairs = coincidentCrossProjectPointPairs;
    }

    internal WholeWorkflowTruthCase TruthCase { get; }
    internal IReadOnlyList<EngaugeWorkflowProjectIdentity> Projects { get; }
    internal int CoincidentCrossProjectPointPairs { get; }
    internal byte[] CopyImageBytes() => (byte[])imageBytes.Clone();
}

internal static class EngaugeDigWholeWorkflowGrouping
{
    /// <summary>
    /// Combine exact shared image bytes while retaining every project, series,
    /// point and per-project calibration. Coincident points are counted, never
    /// deduplicated or interpreted as a shared-baseline relationship.
    /// </summary>
    internal static IReadOnlyList<EngaugeWorkflowImageGroup> Build(
        IReadOnlyList<EngaugeDigWholeWorkflowTruth> projects,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projects);
        cancellationToken.ThrowIfCancellationRequested();
        if (projects.Count == 0 || projects.Any(static item => item is null))
        {
            throw new ArgumentException("A complete nonempty project inventory is required.", nameof(projects));
        }
        if (projects.Select(static item => item.TruthCase.CaseKey)
                .Distinct(StringComparer.Ordinal).Count() != projects.Count)
        {
            throw new InvalidOperationException("DIG_GROUP_DUPLICATE_CASE_IDENTITY");
        }
        if (projects.Select(static item => item.ProjectSha256)
                .Distinct(StringComparer.Ordinal).Count() != projects.Count)
        {
            throw new InvalidOperationException("DIG_GROUP_DUPLICATE_PROJECT_IDENTITY");
        }

        var groups = new List<EngaugeWorkflowImageGroup>();
        foreach (IGrouping<string, EngaugeDigWholeWorkflowTruth> imageGroup in projects
            .GroupBy(static item => item.SourceImageSha256, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EngaugeDigWholeWorkflowTruth[] members = imageGroup
                .OrderBy(static item => item.TruthCase.CaseKey, StringComparer.Ordinal).ToArray();
            EngaugeDigWholeWorkflowTruth first = members[0];
            byte[] imageBytes = first.CopyImageBytes();
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(imageBytes)),
                    first.SourceImageSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("DIG_GROUP_IMAGE_CHECKSUM_MISMATCH");
            }

            var identities = new List<EngaugeWorkflowProjectIdentity>();
            var series = new List<WholeWorkflowTruthSeries>();
            var points = new List<WholeWorkflowTruthPoint>();
            var pointOwners = new List<int>();
            for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EngaugeDigWholeWorkflowTruth member = members[memberIndex];
                WholeWorkflowTruthCase truth = member.TruthCase;
                if (member.SourceWidth != first.SourceWidth || member.SourceHeight != first.SourceHeight ||
                    truth.SourceWidth != first.SourceWidth || truth.SourceHeight != first.SourceHeight ||
                    !string.Equals(truth.SourceSha256, first.SourceImageSha256, StringComparison.Ordinal) ||
                    !member.CopyImageBytes().AsSpan().SequenceEqual(imageBytes))
                {
                    throw new InvalidOperationException("DIG_GROUP_IMAGE_IDENTITY_CONFLICT");
                }
                if (truth.Relations is not null || truth.Points.Any(static point => point.AuthoritativePhaseCode is not null))
                {
                    throw new InvalidOperationException("DIG_GROUP_UNEXPECTED_RELATION_OR_PHASE_TRUTH");
                }
                if (truth.Series.Count == 0 || truth.Points.Count == 0 ||
                    truth.Series.Select(static item => item.SeriesKey).Distinct(StringComparer.Ordinal).Count() != truth.Series.Count ||
                    truth.Points.Select(static item => item.PointKey).Distinct(StringComparer.Ordinal).Count() != truth.Points.Count)
                {
                    throw new InvalidOperationException("DIG_GROUP_INVALID_TRUTH_INVENTORY");
                }

                var seriesKeys = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int seriesIndex = 0; seriesIndex < truth.Series.Count; seriesIndex++)
                {
                    string key = string.Create(CultureInfo.InvariantCulture,
                        $"project-{memberIndex:D4}-series-{seriesIndex:D4}");
                    seriesKeys.Add(truth.Series[seriesIndex].SeriesKey, key);
                    series.Add(new WholeWorkflowTruthSeries(key));
                }
                for (int pointIndex = 0; pointIndex < truth.Points.Count; pointIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WholeWorkflowTruthPoint point = truth.Points[pointIndex];
                    if (!seriesKeys.TryGetValue(point.SeriesKey, out string? seriesKey))
                    {
                        throw new InvalidOperationException("DIG_GROUP_UNKNOWN_POINT_SERIES");
                    }
                    points.Add(point with
                    {
                        PointKey = string.Create(CultureInfo.InvariantCulture,
                            $"project-{memberIndex:D4}-point-{pointIndex:D6}"),
                        SeriesKey = seriesKey,
                    });
                    pointOwners.Add(memberIndex);
                }
                identities.Add(new EngaugeWorkflowProjectIdentity(
                    truth.CaseKey, member.ProjectSha256, truth.Series.Count, truth.Points.Count,
                    Array.AsReadOnly(member.Anchors.ToArray())));
            }

            int coincidentPairs = 0;
            var observedAtPixel = new Dictionary<(double X, double Y), Dictionary<int, int>>();
            for (int index = 0; index < points.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var location = (points[index].SourcePixelX, points[index].SourcePixelY);
                int owner = pointOwners[index];
                if (!observedAtPixel.TryGetValue(location, out Dictionary<int, int>? owners))
                {
                    owners = [];
                    observedAtPixel.Add(location, owners);
                }
                foreach ((int previousOwner, int count) in owners)
                {
                    if (previousOwner != owner)
                    {
                        coincidentPairs = checked(coincidentPairs + count);
                    }
                }
                owners[owner] = owners.GetValueOrDefault(owner) + 1;
            }
            var groupedTruth = new WholeWorkflowTruthCase(
                "image-" + first.SourceImageSha256,
                first.SourceImageSha256,
                first.SourceWidth,
                first.SourceHeight,
                Array.AsReadOnly(series.ToArray()),
                Array.AsReadOnly(points.ToArray()),
                Relations: null);
            groups.Add(new EngaugeWorkflowImageGroup(
                imageBytes, groupedTruth, identities, coincidentPairs));
        }
        return Array.AsReadOnly(groups.ToArray());
    }
}
