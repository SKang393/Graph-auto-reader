// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace GraphReader.Ocr;

public static class OcrCacheKeyDeriver
{
    public static string Create(
        IReadOnlyList<OcrCrop> crops,
        ITextRecognizer recognizer,
        int contractVersion)
        => CreateCore(crops, recognizer, contractVersion, Array.Empty<string>());

    public static string CreateRecognition(
        IReadOnlyList<OcrCrop> crops,
        ITextRecognizer recognizer,
        int contractVersion,
        string transformChain) =>
        CreateCore(crops, recognizer, contractVersion, ["recognition", transformChain]);

    public static string Create(
        IReadOnlyList<OcrCrop> crops,
        ITextRecognizer recognizer,
        OcrRequest request,
        OcrPipelineOptions options,
        string detectorConfigurationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        return CreateCore(
            crops,
            recognizer,
            request.ContractVersion,
            RequestMaterial(request, options, detectorConfigurationFingerprint));
    }

    public static string CreateRequestAlias(
        OcrRequest request,
        ITextRecognizer recognizer,
        OcrPipelineOptions options,
        string detectorConfigurationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(options);
        return CreateCore(
            Array.Empty<OcrCrop>(),
            recognizer,
            request.ContractVersion,
            RequestMaterial(request, options, detectorConfigurationFingerprint).Concat(["request_alias"]));
    }

    private static string CreateCore(
        IReadOnlyList<OcrCrop> crops,
        ITextRecognizer recognizer,
        int contractVersion,
        IEnumerable<string> requestMaterial)
    {
        ArgumentNullException.ThrowIfNull(crops);
        ArgumentNullException.ThrowIfNull(recognizer);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, contractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, recognizer.ModelId);
        Append(hash, recognizer.ModelVersion);
        Append(hash, recognizer.ModelSha256);
        Append(hash, recognizer.ConfigurationFingerprint);
        foreach (var value in requestMaterial)
        {
            Append(hash, value);
        }

        foreach (var crop in crops
                     .OrderBy(static crop => crop.RegionId, StringComparer.Ordinal)
                     .ThenBy(static crop => crop.SourceImage))
        {
            Append(hash, crop.RegionId);
            Append(hash, crop.SourceImage.ToString());
            Append(hash, crop.CropSha256);
            Append(hash, crop.SourceCrop?.PixelSha256 ?? "no_source_crop");
            foreach (var point in crop.OriginalPolygon.Points)
            {
                Append(hash, FormattableString.Invariant($"{point.X:R},{point.Y:R}"));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<string> RequestMaterial(
        OcrRequest request,
        OcrPipelineOptions options,
        string detectorConfigurationFingerprint)
    {
        yield return request.InputSha256;
        yield return request.TransformChain;
        yield return detectorConfigurationFingerprint;
        yield return options.StageVersion;
        yield return GraphTextRoleClassifier.Version;
        yield return string.Join(
            ',',
            options.BatchSize.ToString(CultureInfo.InvariantCulture),
            options.CropWidth.ToString(CultureInfo.InvariantCulture),
            options.CropHeight.ToString(CultureInfo.InvariantCulture),
            options.CropPaddingPixels.ToString("R", CultureInfo.InvariantCulture),
            NullableDouble(options.CropHorizontalPaddingPixels),
            NullableDouble(options.CropVerticalPaddingPixels),
            options.CropVerticalContentPaddingRatio.ToString("R", CultureInfo.InvariantCulture),
            options.CropResizeMode.ToString(),
            options.CropPaddingValue.ToString("R", CultureInfo.InvariantCulture),
            options.MaskPaddingPixels.ToString("R", CultureInfo.InvariantCulture),
            options.MinimumMaskRecognitionConfidence.ToString("R", CultureInfo.InvariantCulture),
            options.MaximumTickCombinationEvaluations.ToString(CultureInfo.InvariantCulture),
            options.InferVerticalOrientationForTallRegions.ToString(CultureInfo.InvariantCulture));
        yield return RectangleMaterial(request.PlotBounds);
        yield return ImageMaterial(request.OriginalImage);
        yield return request.EnhancedImage is null ? "no_enhanced_image" : ImageMaterial(request.EnhancedImage);
        yield return request.DetectorImage is null
            ? "no_detector_image"
            : $"{request.DetectorImage.PixelSha256.ToLowerInvariant()}:{request.DetectorImage.BgrPixelSha256?.ToLowerInvariant() ?? "no_bgr_hash"}:{ImageMaterial(request.DetectorImage.Image)}";
        if (request.DetectedRegions is null)
        {
            yield return "detect_regions";
            yield break;
        }

        foreach (var region in request.DetectedRegions.OrderBy(static region => region.RegionId, StringComparer.Ordinal))
        {
            yield return region.RegionId;
            yield return FormattableString.Invariant($"{region.OrientationDegrees:R},{region.DetectionConfidence:R}");
            foreach (var point in region.Polygon.Points)
            {
                yield return FormattableString.Invariant($"{point.X:R},{point.Y:R}");
            }

            yield return region.Context?.ToString() ?? "no_context";
            yield return region.Evidence?.ToString() ?? "no_region_evidence";
        }
    }

    private static string NullableDouble(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? "symmetric";

    private static string ImageMaterial(OcrImage image)
    {
        var pixelHash = Convert.ToHexString(SHA256.HashData(image.Pixels.Span)).ToLowerInvariant();
        string bgrMaterial = image.BgrPixels is null
            ? "no_bgr"
            : $"bgr24,{image.BgrPixels.Stride}:{Convert.ToHexString(SHA256.HashData(image.BgrPixels.Pixels.Span)).ToLowerInvariant()}";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{image.SourceImage}:{image.Width},{image.Height},{image.Stride}:{image.OriginalToImage.ScaleX:R},{image.OriginalToImage.ScaleY:R},{image.OriginalToImage.OffsetX:R},{image.OriginalToImage.OffsetY:R}:{image.CanonicalOriginalWidth?.ToString(CultureInfo.InvariantCulture) ?? "unspecified"},{image.CanonicalOriginalHeight?.ToString(CultureInfo.InvariantCulture) ?? "unspecified"}:{pixelHash}:{bgrMaterial}");
    }

    private static string RectangleMaterial(OcrRectangle rectangle) =>
        FormattableString.Invariant($"{rectangle.X:R},{rectangle.Y:R},{rectangle.Width:R},{rectangle.Height:R}");

    private static void Append(IncrementalHash hash, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        hash.AppendData(bytes);
        hash.AppendData([0]);
    }
}

public sealed class MemoryOcrResultCache : IOcrResultCache
{
    private readonly ConcurrentDictionary<string, OcrCachedPayload> _entries =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OcrRecognitionCachePayload> _recognitionEntries =
        new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public ValueTask<OcrCachedPayload?> TryGetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _entries.TryGetValue(key, out var value);
        return ValueTask.FromResult(value is null ? null : Freeze(value));
    }

    public ValueTask PutAsync(string key, OcrCachedPayload payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(payload);
        _entries[key] = Freeze(payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask<OcrRecognitionCachePayload?> TryGetRecognitionAsync(
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _recognitionEntries.TryGetValue(key, out var value);
        return ValueTask.FromResult(value is null ? null : Freeze(value));
    }

    public ValueTask PutRecognitionAsync(
        string key,
        OcrRecognitionCachePayload payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(payload);
        _recognitionEntries[key] = Freeze(payload);
        return ValueTask.CompletedTask;
    }

    private static OcrCachedPayload Freeze(OcrCachedPayload payload) =>
        payload with
        {
            Regions = OcrCollections.Freeze(payload.Regions.Select(region => region with
            {
                Alternatives = OcrCollections.Freeze(region.Alternatives),
            })),
            Masks = OcrCollections.Freeze(payload.Masks),
            Warnings = OcrCollections.Freeze(payload.Warnings),
            RegionFailures = payload.RegionFailures is null
                ? Array.Empty<OcrRegionFailure>()
                : OcrCollections.Freeze(payload.RegionFailures),
        };

    private static OcrRecognitionCachePayload Freeze(OcrRecognitionCachePayload payload) =>
        payload with
        {
            Recognitions = OcrCollections.Freeze(payload.Recognitions.Select(recognition => recognition with
            {
                Alternatives = OcrCollections.Freeze(recognition.Alternatives),
            })),
        };
}

public sealed class NullOcrResultCache : IOcrResultCache
{
    public static NullOcrResultCache Instance { get; } = new();

    private NullOcrResultCache()
    {
    }

    public ValueTask<OcrCachedPayload?> TryGetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<OcrCachedPayload?>(null);
    }

    public ValueTask PutAsync(string key, OcrCachedPayload payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<OcrRecognitionCachePayload?> TryGetRecognitionAsync(
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<OcrRecognitionCachePayload?>(null);
    }

    public ValueTask PutRecognitionAsync(
        string key,
        OcrRecognitionCachePayload payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
