// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record CapturedViews(
    MarkerCenter Marker,
    MarkerRectangle? LocalContentBounds,
    float[] Local,
    float[] Wide,
    string LocalSha256,
    string WideSha256);

/// <summary>Candidate-only sampler harness, no inference or label processing.</summary>
internal static class SamplerCapture
{
    internal static IReadOnlyList<CapturedViews> Capture(
        MarkerImageFrame frame,
        IReadOnlyList<MarkerCenter> markers,
        IReadOnlyDictionary<string, MarkerRectangle> bounds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(bounds);
        var extractor = new MarkerPatchExtractor();
        IReadOnlyList<MarkerPatch> local = extractor.Extract(frame, markers,
            new MarkerPatchExtractionOptions(32, 32, 1)
            {
                RadiusScale = 2.25, MinimumHalfExtentFramePixels = 4,
                PaddingValue = 0, OriginalPixelContentBounds = bounds,
            }, cancellationToken);
        IReadOnlyList<MarkerPatch> wide = extractor.Extract(frame, markers,
            new MarkerPatchExtractionOptions(32, 32, 1)
            {
                RadiusScale = 6.75, MinimumHalfExtentFramePixels = 12, PaddingValue = 0,
                OriginalPixelContentBounds = null,
            }, cancellationToken);
        if (local.Count != markers.Count || wide.Count != markers.Count)
            throw new InvalidDataException("Sampler output count differs from classifier input.");
        var result = new CapturedViews[markers.Count];
        for (int i = 0; i < markers.Count; i++)
        {
            if (local[i].Marker.MarkerId != markers[i].MarkerId || wide[i].Marker.MarkerId != markers[i].MarkerId)
                throw new InvalidDataException("Sampler reordered classifier inputs.");
            float[] a = local[i].ChannelsFirstPixels.ToArray();
            float[] b = wide[i].ChannelsFirstPixels.ToArray();
            bounds.TryGetValue(markers[i].MarkerId, out MarkerRectangle content);
            MarkerRectangle? selected = bounds.ContainsKey(markers[i].MarkerId) ? content : null;
            result[i] = new CapturedViews(markers[i], selected, a, b, Hash(a), Hash(b));
        }
        return result;
    }

    private static string Hash(float[] values)
    {
        byte[] bytes = new byte[checked(values.Length * sizeof(float))];
        for (int i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i]) || values[i] < 0 || values[i] > 1)
                throw new InvalidDataException("Sampler values must be finite ink probabilities.");
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * sizeof(float)), BitConverter.SingleToInt32Bits(values[i]));
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
