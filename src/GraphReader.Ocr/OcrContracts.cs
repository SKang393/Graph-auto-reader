// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections;

namespace GraphReader.Ocr;

public enum OcrTensorColorMode
{
    GrayscaleReplicated,
    Bgr,
}

public static class OcrContract
{
    public const int Version = 1;
    public const string Stage = "ocr";
    public const string CoordinateSpace = "original_pixels";
}

public enum OcrTextRole
{
    YTick,
    XTick,
    AxisTitle,
    PhaseHeading,
    LegendText,
    Participant,
    Annotation,
    Other,
}

public enum OcrSourceImage
{
    Original,
    Enhanced,
}

public enum OcrReviewStatus
{
    Unreviewed,
    Accepted,
    Corrected,
    Rejected,
}

public enum OcrOrientation
{
    Horizontal,
    RotatedClockwise,
    RotatedCounterClockwise,
    Arbitrary,
}

public readonly record struct OcrPoint(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

public readonly record struct OcrRectangle(double X, double Y, double Width, double Height)
{
    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public OcrPoint Center => new(X + (Width / 2d), Y + (Height / 2d));

    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && Width > 0 && Height > 0;
}

public sealed record OcrPolygon
{
    public OcrPolygon(IReadOnlyList<OcrPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3 || points.Any(static point => !point.IsFinite))
        {
            throw new ArgumentException("An OCR polygon requires at least three finite points.", nameof(points));
        }

        Points = OcrCollections.Freeze(points);
    }

    public IReadOnlyList<OcrPoint> Points { get; }

    public OcrRectangle Bounds
    {
        get
        {
            var minimumX = Points.Min(static point => point.X);
            var maximumX = Points.Max(static point => point.X);
            var minimumY = Points.Min(static point => point.Y);
            var maximumY = Points.Max(static point => point.Y);
            return new OcrRectangle(minimumX, minimumY, maximumX - minimumX, maximumY - minimumY);
        }
    }

    public static OcrPolygon FromRectangle(OcrRectangle rectangle)
    {
        if (!rectangle.IsValid)
        {
            throw new ArgumentException("OCR rectangle must be finite and have positive dimensions.", nameof(rectangle));
        }

        return new OcrPolygon([
            new OcrPoint(rectangle.Left, rectangle.Top),
            new OcrPoint(rectangle.Right, rectangle.Top),
            new OcrPoint(rectangle.Right, rectangle.Bottom),
            new OcrPoint(rectangle.Left, rectangle.Bottom),
        ]);
    }
}

/// <summary>
/// Maps immutable original pixels to pixels in the supplied frame.
/// </summary>
public readonly record struct OcrFrameTransform(double ScaleX, double ScaleY, double OffsetX, double OffsetY)
{
    public static OcrFrameTransform Identity { get; } = new(1, 1, 0, 0);

    public bool IsInvertible =>
        double.IsFinite(ScaleX) && double.IsFinite(ScaleY) &&
        double.IsFinite(OffsetX) && double.IsFinite(OffsetY) &&
        ScaleX != 0 && ScaleY != 0;

    public OcrPoint MapFromOriginal(OcrPoint point) =>
        new((point.X * ScaleX) + OffsetX, (point.Y * ScaleY) + OffsetY);

    public OcrPoint MapToOriginal(OcrPoint point)
    {
        if (!IsInvertible)
        {
            throw new InvalidOperationException("OCR frame transform is not invertible.");
        }

        return new OcrPoint((point.X - OffsetX) / ScaleX, (point.Y - OffsetY) / ScaleY);
    }
}

public sealed record OcrImage(
    int Width,
    int Height,
    int Stride,
    ReadOnlyMemory<byte> Pixels,
    OcrSourceImage SourceImage,
    OcrFrameTransform OriginalToImage,
    string CoordinateSpace = OcrContract.CoordinateSpace,
    int? CanonicalOriginalWidth = null,
    int? CanonicalOriginalHeight = null,
    OcrBgrBytePixels? BgrPixels = null);

/// <summary>
/// Optional interleaved BGR24 pixels retained alongside the canonical Gray8
/// OCR plane. The separate plane lets graph-structure code keep its stable
/// grayscale contract while color-sensitive production models consume the
/// exact channel order declared by their manifests.
/// </summary>
public sealed record OcrBgrBytePixels(int Stride, ReadOnlyMemory<byte> Pixels);

public sealed record OcrRegionEvidence(
    int ComponentCount,
    double InkDensity,
    double TextLikelihood,
    double StructureLikelihood,
    bool LikelyGraphStructure,
    IReadOnlyList<string> Reasons);

public sealed record OcrRegionContext(
    bool NearLegendGlyph = false,
    bool NearPhaseDivider = false,
    bool InParticipantBand = false,
    bool NearAnnotationArrow = false,
    bool NumericExpected = false,
    bool AxisTitleExpected = false,
    OcrTextRole? ExplicitRoleHint = null);

public sealed record OcrDetectedRegion(
    string RegionId,
    OcrPolygon Polygon,
    double OrientationDegrees,
    double DetectionConfidence,
    OcrRegionContext? Context = null,
    string CoordinateSpace = OcrContract.CoordinateSpace,
    OcrRegionEvidence? Evidence = null);

public sealed record OcrCrop(
    string RegionId,
    OcrSourceImage SourceImage,
    int Width,
    int Height,
    ReadOnlyMemory<float> Pixels,
    string CropSha256,
    OcrPolygon OriginalPolygon,
    OcrBgrFloatPixels? BgrPixels = null,
    OcrV8SourceCrop? SourceCrop = null);

/// <summary>
/// Optional interleaved BGR crop samples normalized to [0,1].
/// </summary>
public sealed record OcrBgrFloatPixels(int Stride, ReadOnlyMemory<float> Pixels);

public sealed record OcrRecognitionAlternative(
    string Text,
    double Confidence,
    OcrSourceImage SourceImage);

public sealed record OcrFailure(
    string Code,
    string Severity,
    string UserMessageKey,
    string TechnicalMessage,
    bool Recoverable,
    string SuggestedAction);

public sealed record OcrRecognition(
    string RegionId,
    OcrSourceImage SourceImage,
    IReadOnlyList<OcrRecognitionAlternative> Alternatives,
    double InferenceMilliseconds,
    OcrFailure? Failure = null);

public sealed record OcrRegionFailure(
    string RegionId,
    OcrSourceImage SourceImage,
    OcrFailure Failure);

public sealed record OcrMask(
    string RegionId,
    OcrPolygon Polygon,
    double Confidence,
    string CoordinateSpace = OcrContract.CoordinateSpace);

public sealed record OcrRegion(
    string RegionId,
    OcrPolygon Polygon,
    string Text,
    IReadOnlyList<OcrRecognitionAlternative> Alternatives,
    OcrTextRole Role,
    double Confidence,
    OcrSourceImage SourceImage,
    OcrReviewStatus ReviewStatus,
    string CoordinateSpace = OcrContract.CoordinateSpace);

public sealed record OcrTiming(
    double PreprocessMilliseconds,
    double InferenceMilliseconds,
    double PostprocessMilliseconds,
    double TotalMilliseconds);

public sealed record OcrCacheDiagnostics(
    bool CacheHit,
    string CacheKey,
    int CropCount,
    int BatchCount,
    bool RecognitionCacheHit = false,
    string? RecognitionCacheKey = null);

/// <summary>
/// Optional checksum-bound derivative used only for text-region detection.
/// Recognition crops always come from <see cref="OcrRequest.OriginalImage"/>.
/// </summary>
public sealed record OcrDetectorImage(
    OcrImage Image,
    string PixelSha256,
    string? BgrPixelSha256 = null);

public sealed record OcrRequest(
    string ProjectId,
    string PanelId,
    string InputSha256,
    OcrImage OriginalImage,
    OcrRectangle PlotBounds,
    OcrImage? EnhancedImage = null,
    IReadOnlyList<OcrDetectedRegion>? DetectedRegions = null,
    int ContractVersion = OcrContract.Version,
    string TransformChain = "identity",
    OcrDetectorImage? DetectorImage = null);

public sealed record OcrResult(
    int ContractVersion,
    string RunId,
    string ProjectId,
    string PanelId,
    string Stage,
    string StageVersion,
    string InputSha256,
    string CoordinateSpace,
    IReadOnlyList<OcrRegion> Regions,
    IReadOnlyList<OcrMask> Masks,
    OcrTiming Timing,
    double Confidence,
    IReadOnlyList<string> Warnings,
    OcrCacheDiagnostics Cache,
    OcrFailure? Failure,
    IReadOnlyList<OcrRegionFailure>? RegionFailures = null)
{
    public bool Succeeded => Failure is null;
}

public interface ITextRegionDetector
{
    string ConfigurationFingerprint => GetType().FullName ?? GetType().Name;

    ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage image,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional detector seam that returns accepted DB regions and their pre-unclip
/// contour geometry as one immutable result from the same inference execution.
/// </summary>
public interface IAtomicDbGeometryTextRegionDetector : ITextRegionDetector
{
    bool SupportsAtomicDbGeometry { get; }

    ValueTask<OcrAtomicDbDetection> DetectWithAtomicDbGeometryAsync(
        OcrImage image,
        CancellationToken cancellationToken);
}

public sealed record OcrAtomicDbDetection
{
    public OcrAtomicDbDetection(
        IReadOnlyList<OcrDetectedRegion> regions,
        OcrDbGeometryObservation geometry)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(geometry);
        Regions = OcrCollections.Freeze(regions);
        Geometry = geometry;
    }

    public IReadOnlyList<OcrDetectedRegion> Regions { get; }

    public OcrDbGeometryObservation Geometry { get; }
}

/// <summary>
/// Optional detector composition seam for stages that require both immutable
/// original pixels and a coordinate-aligned detector derivative. Implementers
/// must retain the single-input behavior required by
/// <see cref="ITextRegionDetector"/>.
/// </summary>
public interface IDualInputTextRegionDetector : ITextRegionDetector
{
    ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage originalImage,
        OcrImage detectorImage,
        CancellationToken cancellationToken);
}

/// <summary>
/// Internal production-composition seam for checksum-bound detectors whose
/// reviewed acceptance policy includes recognizer-gated rescue bands. Proposal
/// confidence is retained so the composition can apply those fixed bands
/// without accepting low-confidence graph structure as text.
/// </summary>
public interface ITextRegionProposalDetector : ITextRegionDetector
{
    ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectProposalsAsync(
        OcrImage image,
        CancellationToken cancellationToken);
}

public interface ITextRecognizer
{
    string ModelId => "unspecified";

    string ModelVersion => "unspecified";

    string ModelSha256 => new('0', 64);

    string ConfigurationFingerprint => "default";

    ValueTask<IReadOnlyList<OcrRecognition>> RecognizeBatchAsync(
        IReadOnlyList<OcrCrop> crops,
        CancellationToken cancellationToken);
}

public interface IOcrResultCache
{
    ValueTask<OcrCachedPayload?> TryGetAsync(string key, CancellationToken cancellationToken);

    ValueTask PutAsync(string key, OcrCachedPayload payload, CancellationToken cancellationToken);

    ValueTask<OcrRecognitionCachePayload?> TryGetRecognitionAsync(
        string key,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<OcrRecognitionCachePayload?>(null);

    ValueTask PutRecognitionAsync(
        string key,
        OcrRecognitionCachePayload payload,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed record OcrCachedPayload(
    IReadOnlyList<OcrRegion> Regions,
    IReadOnlyList<OcrMask> Masks,
    double Confidence,
    IReadOnlyList<string> Warnings,
    int CropCount,
    int BatchCount,
    string? ContentCacheKey = null,
    IReadOnlyList<OcrRegionFailure>? RegionFailures = null,
    string? RecognitionCacheKey = null);

public sealed record OcrRecognitionCachePayload(IReadOnlyList<OcrRecognition> Recognitions);

internal static class OcrCollections
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) =>
        new FrozenList<T>(values);

    private sealed class FrozenList<T> : IReadOnlyList<T>, IEquatable<FrozenList<T>>
    {
        private readonly T[] _items;

        public FrozenList(IEnumerable<T> items) => _items = items.ToArray();

        public int Count => _items.Length;

        public T this[int index] => _items[index];

        public bool Equals(FrozenList<T>? other) =>
            other is not null && _items.SequenceEqual(other._items);

        public override bool Equals(object? obj) =>
            obj is IEnumerable<T> items && _items.SequenceEqual(items);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var item in _items)
            {
                hash.Add(item);
            }

            return hash.ToHashCode();
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }
}
