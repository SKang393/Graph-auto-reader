// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using GraphReader.App.Integration.Workflow;
using GraphReader.Markers.Detection;

namespace GraphReader.RealAcceptance.Ocr;

// Counts only. Mixed means that the union of blocker types across all nearby
// grid anchors contains more than one type, even when they affect one anchor.
internal sealed record EngaugeDevProposalCoverageCounts(
    long CenterCount, long Matched, long NoNearbyGrid, long TextOnly,
    long ArtifactOnly, long MissingInkOnly, long Mixed,
    long GeometrySeedArtifactOnly, long ResidualArtifactOnly, long BothArtifactOnly,
    long ResidualArrowOnly, long ResidualBracketOnly, long ResidualLegendOnly,
    long ResidualIntersectionOnly, long ResidualMultiple)
{
    internal long Unmatched => NoNearbyGrid + TextOnly + ArtifactOnly + MissingInkOnly + Mixed;
    internal long ArtifactOnlyProvenanceTotal => GeometrySeedArtifactOnly + ResidualArtifactOnly + BothArtifactOnly;
    internal long ResidualOnlyCategoryTotal => ResidualArrowOnly + ResidualBracketOnly + ResidualLegendOnly +
        ResidualIntersectionOnly + ResidualMultiple;
}

internal sealed class EngaugeDevProposalCoverageAccumulator
{
    private long centers, matched, noGrid, text, artifact, ink, mixed, artifactSeed, artifactResidual, artifactBoth,
        residualArrow, residualBracket, residualLegend, residualIntersection, residualMultiple;
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
        var failures = new Dictionary<MarkerPoint, AnchorFailure>();
        foreach (MarkerPoint anchor in requested)
        {
            token.ThrowIfCancellationRequested();
            int x = checked((int)anchor.X), y = checked((int)anchor.Y);
            bool missingInk = !HasHit(planes.Gray, planes.Width, planes.Height, x, y,
                    NormalizedMarkerProposalContract.InkSupportRadius,
                    NormalizedMarkerProposalContract.InkSupportThreshold, invert: true);
            bool textBlocked = HasHit(planes.Ocr, planes.Width, planes.Height, x, y,
                    NormalizedMarkerProposalContract.MaskRejectionRadius,
                    NormalizedMarkerProposalContract.MaskRejectionThreshold, invert: false);
            bool artifactBlocked = HasHit(planes.Artifact, planes.Width, planes.Height, x, y,
                    NormalizedMarkerProposalContract.MaskRejectionRadius,
                    NormalizedMarkerProposalContract.MaskRejectionThreshold, invert: false);
            bool seedBlocked = false, residualBlocked = false;
            if (artifactBlocked)
            {
                if (!planes.HasArtifactProvenance)
                    throw new InvalidDataException("ENGAUGE_COVERAGE_ARTIFACT_PROVENANCE_MISSING");
                seedBlocked = HasHit(planes.ArtifactSeed, planes.Width, planes.Height, x, y,
                    NormalizedMarkerProposalContract.MaskRejectionRadius,
                    NormalizedMarkerProposalContract.MaskRejectionThreshold, invert: false);
                residualBlocked = HasHit(planes.ResidualArtifact, planes.Width, planes.Height, x, y,
                    NormalizedMarkerProposalContract.MaskRejectionRadius,
                    NormalizedMarkerProposalContract.MaskRejectionThreshold, invert: false);
                if (!seedBlocked && !residualBlocked)
                    throw new InvalidDataException("ENGAUGE_COVERAGE_ARTIFACT_PROVENANCE_MISMATCH");
                if (residualBlocked && !planes.HasResidualCategoryBits)
                    throw new InvalidDataException("ENGAUGE_COVERAGE_RESIDUAL_CATEGORY_MISSING");
            }
            byte residualCategories = residualBlocked
                ? CategoryHits(planes.ResidualArtifact, planes.ResidualCategoryBits, planes.Width, planes.Height, x, y)
                : (byte)0;
            if (residualBlocked && residualCategories == 0)
                throw new InvalidDataException("ENGAUGE_COVERAGE_RESIDUAL_CATEGORY_MISMATCH");
            var failure = new AnchorFailure(missingInk, textBlocked, artifactBlocked, seedBlocked, residualBlocked, residualCategories);
            if (!failure.Blocked != eligible.Contains(anchor))
                throw new InvalidDataException("ENGAUGE_COVERAGE_RUNTIME_PARITY_FAILURE");
            failures.Add(anchor, failure);
        }
        long nextCenters = 0, nextMatched = 0, nextNoGrid = 0, nextText = 0, nextArtifact = 0, nextInk = 0, nextMixed = 0,
            nextArtifactSeed = 0, nextArtifactResidual = 0, nextArtifactBoth = 0, nextResidualArrow = 0,
            nextResidualBracket = 0, nextResidualLegend = 0, nextResidualIntersection = 0, nextResidualMultiple = 0;
        foreach (MarkerPoint[] nearby in perCenter)
        {
            token.ThrowIfCancellationRequested();
            nextCenters++;
            if (nearby.Length == 0) { nextNoGrid++; continue; }
            if (nearby.Any(eligible.Contains)) { nextMatched++; continue; }
            bool missingInk = nearby.Any(point => failures[point].MissingInk);
            bool textBlocked = nearby.Any(point => failures[point].TextBlocked);
            bool artifactBlocked = nearby.Any(point => failures[point].ArtifactBlocked);
            bool seedBlocked = nearby.Any(point => failures[point].SeedBlocked);
            bool residualBlocked = nearby.Any(point => failures[point].ResidualBlocked);
            byte residualCategories = nearby.Aggregate((byte)0, (current, point) =>
                (byte)(current | failures[point].ResidualCategories));
            if (!missingInk && !textBlocked && artifactBlocked)
            {
                nextArtifact++;
                if (seedBlocked && residualBlocked) nextArtifactBoth++;
                else if (seedBlocked) nextArtifactSeed++;
                else if (residualBlocked)
                {
                    nextArtifactResidual++;
                    switch ((RasterResidualArtifactCategoryBits)residualCategories)
                    {
                        case RasterResidualArtifactCategoryBits.AnnotationArrow: nextResidualArrow++; break;
                        case RasterResidualArtifactCategoryBits.Bracket: nextResidualBracket++; break;
                        case RasterResidualArtifactCategoryBits.LegendStructure: nextResidualLegend++; break;
                        case RasterResidualArtifactCategoryBits.ConnectingLineIntersection: nextResidualIntersection++; break;
                        default: nextResidualMultiple++; break;
                    }
                }
                else throw new InvalidDataException("ENGAUGE_COVERAGE_ARTIFACT_PROVENANCE_MISMATCH");
            }
            else if (missingInk || textBlocked || artifactBlocked)
            {
                if (missingInk && !textBlocked && !artifactBlocked) nextInk++;
                else if (!missingInk && textBlocked && !artifactBlocked) nextText++;
                else nextMixed++;
            }
            else throw new InvalidDataException("ENGAUGE_COVERAGE_UNEXPLAINED");
        }
        checked
        {
            centers += nextCenters; matched += nextMatched; noGrid += nextNoGrid;
            text += nextText; artifact += nextArtifact; ink += nextInk; mixed += nextMixed;
            artifactSeed += nextArtifactSeed; artifactResidual += nextArtifactResidual; artifactBoth += nextArtifactBoth;
            residualArrow += nextResidualArrow; residualBracket += nextResidualBracket;
            residualLegend += nextResidualLegend; residualIntersection += nextResidualIntersection;
            residualMultiple += nextResidualMultiple;
        }
        seenImages.Add(group.ImageSha256);
    }

    internal EngaugeDevProposalCoverageCounts Finish()
    {
        if (finished) throw new InvalidOperationException("ENGAUGE_COVERAGE_FINISHED");
        finished = true;
        if (artifact != artifactSeed + artifactResidual + artifactBoth)
            throw new InvalidDataException("ENGAUGE_COVERAGE_ARTIFACT_CONSERVATION_FAILED");
        if (artifactResidual != residualArrow + residualBracket + residualLegend + residualIntersection + residualMultiple)
            throw new InvalidDataException("ENGAUGE_COVERAGE_RESIDUAL_CATEGORY_CONSERVATION_FAILED");
        return new(centers, matched, noGrid, text, artifact, ink, mixed,
            artifactSeed, artifactResidual, artifactBoth, residualArrow, residualBracket, residualLegend,
            residualIntersection, residualMultiple);
    }

    private static byte CategoryHits(ReadOnlyMemory<float> residual, ReadOnlyMemory<byte> categories, int width, int height,
        int centerX, int centerY)
    {
        byte result = 0;
        for (int y = Math.Max(0, centerY - NormalizedMarkerProposalContract.MaskRejectionRadius);
            y < Math.Min(height, centerY + NormalizedMarkerProposalContract.MaskRejectionRadius + 1); y++)
        for (int x = Math.Max(0, centerX - NormalizedMarkerProposalContract.MaskRejectionRadius);
            x < Math.Min(width, centerX + NormalizedMarkerProposalContract.MaskRejectionRadius + 1); x++)
        {
            int index = (y * width) + x;
            if (residual.Span[index] >= NormalizedMarkerProposalContract.MaskRejectionThreshold)
                result |= categories.Span[index];
        }
        return result;
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

    private readonly record struct AnchorFailure(
        bool MissingInk, bool TextBlocked, bool ArtifactBlocked, bool SeedBlocked, bool ResidualBlocked,
        byte ResidualCategories)
    {
        internal bool Blocked => MissingInk || TextBlocked || ArtifactBlocked;
    }
}
