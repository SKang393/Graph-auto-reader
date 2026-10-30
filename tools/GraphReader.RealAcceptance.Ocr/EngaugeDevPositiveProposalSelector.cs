// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using GraphReader.Markers.Detection;

namespace GraphReader.RealAcceptance.Ocr;

// Private in-memory positive examples only; no unlabeled proposal is exported.
internal sealed class EngaugeDevPositiveProposals
{
    internal EngaugeDevPositiveProposals(string imageSha256, float[] tensor, MarkerPoint[] coordinates,
        int unmatchedCenterCount)
    {
        ImageSha256 = imageSha256;
        Tensor = tensor;
        Coordinates = Array.AsReadOnly(coordinates);
        UnmatchedCenterCount = unmatchedCenterCount;
    }
    internal string ImageSha256 { get; }
    internal ReadOnlyMemory<float> Tensor { get; }
    internal IReadOnlyList<MarkerPoint> Coordinates { get; }
    internal int UnmatchedCenterCount { get; }
}

internal static class EngaugeDevPositiveProposalSelector
{
    internal const double PositiveDistancePixels = 3.0;

    internal static EngaugeDevPositiveProposals Select(
        EngaugeDevRuntimePlanes planes, EngaugeDevImageGroup group, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(group);
        token.ThrowIfCancellationRequested();
        if (group.Partition != EngaugeDevStudyPartition.Train ||
            group.Width != planes.Width || group.Height != planes.Height ||
            !string.Equals(group.ImageSha256, planes.ImageSha256, StringComparison.Ordinal) ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(group.CopyImageBytes())), planes.ImageSha256, StringComparison.Ordinal) ||
            !group.PositiveCenters.SequenceEqual(planes.PositiveCenters) || group.PositiveCenters.Count == 0 ||
            group.PositiveCenters.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X < 0 || point.Y < 0 || point.X >= group.Width || point.Y >= group.Height))
            throw new InvalidDataException("ENGAUGE_POSITIVE_SOURCE_MISMATCH");

        var frame = new MarkerImageFrame(planes.Width, planes.Height, 1, planes.Gray, MarkerSourceImage.Original,
            MarkerAffineTransform.Identity, new MarkerMask(planes.Width, planes.Height, planes.Ocr),
            new MarkerMask(planes.Width, planes.Height, planes.Artifact));
        NormalizedMarkerProposalBatch proposals = NormalizedMarkerProposalPreprocessor.Prepare(frame, token);
        int patchValues = checked(NormalizedMarkerProposalContract.ChannelCount *
            NormalizedMarkerProposalContract.PatchSize * NormalizedMarkerProposalContract.PatchSize);
        var selected = new List<int>();
        var matched = new bool[group.PositiveCenters.Count];
        for (int index = 0; index < proposals.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            MarkerPoint anchor = proposals.Coordinates[index];
            bool positive = false;
            for (int label = 0; label < group.PositiveCenters.Count; label++)
            {
                EngaugePositiveCenter center = group.PositiveCenters[label];
                double dx = anchor.X - center.X, dy = anchor.Y - center.Y;
                if (dx * dx + dy * dy <= PositiveDistancePixels * PositiveDistancePixels)
                {
                    matched[label] = true;
                    positive = true;
                }
            }
            if (positive) selected.Add(index);
        }
        var tensor = new float[checked(selected.Count * patchValues)];
        var coordinates = new MarkerPoint[selected.Count];
        for (int index = 0; index < selected.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            int sourceIndex = selected[index];
            proposals.Tensor.Span.Slice(sourceIndex * patchValues, patchValues)
                .CopyTo(tensor.AsSpan(index * patchValues, patchValues));
            coordinates[index] = proposals.Coordinates[sourceIndex];
        }
        return new EngaugeDevPositiveProposals(planes.ImageSha256, tensor, coordinates,
            matched.Count(static value => !value));
    }
}
