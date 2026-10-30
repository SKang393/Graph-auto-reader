// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using GraphReader.Markers.Detection;

namespace GraphReader.RealAcceptance.Ocr;

// Counts only. Mixed means that the union of blocker types across all nearby
// grid anchors contains more than one type, even when they affect one anchor.
internal sealed record EngaugeDevProposalCoverageCounts(
    long CenterCount, long Matched, long NoNearbyGrid, long TextOnly,
    long ArtifactOnly, long MissingInkOnly, long Mixed)
{
    internal long Unmatched => NoNearbyGrid + TextOnly + ArtifactOnly + MissingInkOnly + Mixed;
}

internal sealed class EngaugeDevProposalCoverageAccumulator
{
    private long centers, matched, noGrid, text, artifact, ink, mixed;
    private bool finished;
    private readonly HashSet<string> seenImages = new(StringComparer.Ordinal);

    internal void Add(EngaugeDevRuntimePlanes planes, EngaugeDevImageGroup group, CancellationToken token)
    {
        if (finished) throw new InvalidOperationException("ENGAUGE_COVERAGE_FINISHED");
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(group);
        token.ThrowIfCancellationRequested();
        if (group.Partition != EngaugeDevStudyPartition.Train || group.Width != planes.Width ||
            group.Height != planes.Height || group.PositiveCenters.Count == 0 ||
            !group.PositiveCenters.SequenceEqual(planes.PositiveCenters) ||
            !string.Equals(group.ImageSha256, planes.ImageSha256, StringComparison.Ordinal) ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(group.CopyImageBytes())), planes.ImageSha256, StringComparison.Ordinal) ||
            group.PositiveCenters.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
                p.X < 0 || p.Y < 0 || p.X >= group.Width || p.Y >= group.Height))
            throw new InvalidDataException("ENGAUGE_COVERAGE_SOURCE_MISMATCH");
        if (seenImages.Contains(group.ImageSha256))
            throw new InvalidDataException("ENGAUGE_COVERAGE_DUPLICATE_IMAGE");

        var perCenter = new List<MarkerPoint[]>(group.PositiveCenters.Count);
        var requested = new HashSet<MarkerPoint>();
        const int stride = NormalizedMarkerProposalContract.ProposalStride;
        double radius = EngaugeDevPositiveProposalSelector.PositiveDistancePixels;
        foreach (EngaugePositiveCenter center in group.PositiveCenters)
        {
            token.ThrowIfCancellationRequested();
            var nearby = new List<MarkerPoint>();
            int firstX = Math.Max(0, (int)Math.Ceiling((center.X - radius) / stride));
            int lastX = Math.Min((group.Width - 1) / stride, (int)Math.Floor((center.X + radius) / stride));
            int firstY = Math.Max(0, (int)Math.Ceiling((center.Y - radius) / stride));
            int lastY = Math.Min((group.Height - 1) / stride, (int)Math.Floor((center.Y + radius) / stride));
            for (int gy = firstY; gy <= lastY; gy++)
            for (int gx = firstX; gx <= lastX; gx++)
            {
                int x = gx * stride, y = gy * stride;
                double dx = x - center.X, dy = y - center.Y;
                if (dx * dx + dy * dy <= radius * radius)
                {
                    var point = new MarkerPoint(x, y);
                    nearby.Add(point);
                    requested.Add(point);
                }
            }
            perCenter.Add(nearby.ToArray());
        }
        var frame = new MarkerImageFrame(planes.Width, planes.Height, 1, planes.Gray, MarkerSourceImage.Original,
            MarkerAffineTransform.Identity, new MarkerMask(planes.Width, planes.Height, planes.Ocr),
            new MarkerMask(planes.Width, planes.Height, planes.Artifact));
        NormalizedMarkerProposalBatch actual = NormalizedMarkerProposalPreprocessor.PrepareSelected(frame, requested, token);
        var eligible = actual.Coordinates.ToHashSet();
        if (eligible.Count != actual.Count || !eligible.IsSubsetOf(requested))
            throw new InvalidDataException("ENGAUGE_COVERAGE_RUNTIME_PARITY_FAILURE");
        var failures = new Dictionary<MarkerPoint, int>();
        foreach (MarkerPoint anchor in requested)
        {
            token.ThrowIfCancellationRequested();
            int x = checked((int)anchor.X), y = checked((int)anchor.Y), reasons = 0;
            if (!HasHit(planes.Gray, planes.Width, planes.Height, x, y,
                    NormalizedMarkerProposalContract.InkSupportRadius,
                    NormalizedMarkerProposalContract.InkSupportThreshold, invert: true)) reasons |= 1;
            if (HasHit(planes.Ocr, planes.Width, planes.Height, x, y,
                    NormalizedMarkerProposalContract.MaskRejectionRadius,
                    NormalizedMarkerProposalContract.MaskRejectionThreshold, invert: false)) reasons |= 2;
            if (HasHit(planes.Artifact, planes.Width, planes.Height, x, y,
                    NormalizedMarkerProposalContract.MaskRejectionRadius,
                    NormalizedMarkerProposalContract.MaskRejectionThreshold, invert: false)) reasons |= 4;
            if ((reasons == 0) != eligible.Contains(anchor))
                throw new InvalidDataException("ENGAUGE_COVERAGE_RUNTIME_PARITY_FAILURE");
            failures.Add(anchor, reasons);
        }
        long nextCenters = 0, nextMatched = 0, nextNoGrid = 0, nextText = 0, nextArtifact = 0, nextInk = 0, nextMixed = 0;
        foreach (MarkerPoint[] nearby in perCenter)
        {
            token.ThrowIfCancellationRequested();
            nextCenters++;
            if (nearby.Length == 0) { nextNoGrid++; continue; }
            if (nearby.Any(eligible.Contains)) { nextMatched++; continue; }
            int reasons = 0;
            foreach (MarkerPoint point in nearby) reasons |= failures[point];
            switch (reasons)
            {
                case 1: nextInk++; break;
                case 2: nextText++; break;
                case 4: nextArtifact++; break;
                case 3 or 5 or 6 or 7: nextMixed++; break;
                default: throw new InvalidDataException("ENGAUGE_COVERAGE_UNEXPLAINED");
            }
        }
        checked
        {
            centers += nextCenters; matched += nextMatched; noGrid += nextNoGrid;
            text += nextText; artifact += nextArtifact; ink += nextInk; mixed += nextMixed;
        }
        seenImages.Add(group.ImageSha256);
    }

    internal EngaugeDevProposalCoverageCounts Finish()
    {
        if (finished) throw new InvalidOperationException("ENGAUGE_COVERAGE_FINISHED");
        finished = true;
        return new(centers, matched, noGrid, text, artifact, ink, mixed);
    }

    private static bool HasHit(ReadOnlyMemory<float> input, int width, int height, int centerX, int centerY,
        int radius, double threshold, bool invert)
    {
        ReadOnlySpan<float> pixels = input.Span;
        for (int y = Math.Max(0, centerY - radius); y < Math.Min(height, centerY + radius + 1); y++)
        for (int x = Math.Max(0, centerX - radius); x < Math.Min(width, centerX + radius + 1); x++)
        {
            float value = pixels[y * width + x];
            if ((invert ? 1f - value : value) >= threshold) return true;
        }
        return false;
    }
}
