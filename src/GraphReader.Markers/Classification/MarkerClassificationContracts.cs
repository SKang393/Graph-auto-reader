// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections;
using System.Collections.Frozen;
using GraphReader.Inference;
using GraphReader.Markers.Detection;

namespace GraphReader.Markers.Classification;

public static class MarkerClassificationContract
{
    public const int Version = 1;
    public const string Stage = MarkerContract.Stage;
    public const string CoordinateSpace = MarkerContract.CoordinateSpace;
    public const int ShapeClassCount = 9;
    public const int FillClassCount = 3;
    public const string ShapeOutputOrder =
        "circle,square,triangle_up,triangle_down,diamond,star,asterisk,cross,other";
    public const string FillOutputOrder = "filled,open,unknown";
}

public enum MarkerShape
{
    Circle,
    Square,
    TriangleUp,
    TriangleDown,
    Diamond,
    Star,
    Asterisk,
    Cross,
    Other,
}

public enum MarkerFill
{
    Filled,
    Open,
    Unknown,
}

public enum MarkerClassifierOutputEncoding
{
    Logits,
    Probabilities,
}

public sealed record MarkerClassifierTensorContract
{
    public MarkerClassifierTensorContract(
        string inputName,
        string outputName,
        int patchWidth,
        int patchHeight,
        int inputChannelCount,
        int embeddingLength)
    {
        InputName = inputName;
        OutputName = outputName;
        PatchWidth = patchWidth;
        PatchHeight = patchHeight;
        InputChannelCount = inputChannelCount;
        EmbeddingLength = embeddingLength;
    }

    public string InputName { get; }

    public string OutputName { get; }

    public int PatchWidth { get; }

    public int PatchHeight { get; }

    public int InputChannelCount { get; }

    public int EmbeddingLength { get; }

    public float NormalizeMean { get; init; }

    public float NormalizeScale { get; init; } = 1f;

    public MarkerClassifierOutputEncoding OutputEncoding { get; init; } =
        MarkerClassifierOutputEncoding.Logits;

    public static int ShapeOffset => 0;

    public static int FillOffset => MarkerClassificationContract.ShapeClassCount;

    public static int ArtifactOffset => FillOffset + MarkerClassificationContract.FillClassCount;

    public static int EmbeddingOffset => ArtifactOffset + 1;

    public int ValuesPerMarker => checked(EmbeddingOffset + EmbeddingLength);
}

public sealed record MarkerPatchExtractionOptions(
    int Width,
    int Height,
    int ChannelCount)
{
    public double RadiusScale { get; init; } = 2.25;

    public double MinimumHalfExtentFramePixels { get; init; } = 4;

    public float PaddingValue { get; init; }

    /// <summary>Optional measured symbol bounds; samples outside them become white padding.</summary>
    public IReadOnlyDictionary<string, MarkerRectangle>? OriginalPixelContentBounds { get; init; }
}

public sealed record MarkerClassificationOptions(MarkerClassifierTensorContract TensorContract)
{
    public int BatchSize { get; init; } = 64;

    public double PatchRadiusScale { get; init; } = 2.25;

    public double MinimumPatchHalfExtentFramePixels { get; init; } = 4;

    public float PatchPaddingValue { get; init; }

    public IReadOnlyDictionary<string, MarkerRectangle>? OriginalPixelContentBounds { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public string StageVersion { get; init; } = "0.1.0";
}

public sealed class MarkerClassificationRequest
{
    public MarkerClassificationRequest(
        string projectId,
        string panelId,
        string inputSha256,
        ModelIdentity model,
        MarkerImageFrame image,
        IEnumerable<MarkerCenter> markers,
        MarkerClassificationOptions options,
        int contractVersion = MarkerClassificationContract.Version,
        string transformChain = "identity")
    {
        ProjectId = projectId;
        PanelId = panelId;
        InputSha256 = inputSha256;
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Image = CopyFrame(image ?? throw new ArgumentNullException(nameof(image)));
        Markers = ClassificationCollections.Freeze(
            markers ?? throw new ArgumentNullException(nameof(markers)));
        ArgumentNullException.ThrowIfNull(options);
        Options = options with
        {
            OriginalPixelContentBounds = options.OriginalPixelContentBounds?.ToFrozenDictionary(StringComparer.Ordinal),
        };
        ContractVersion = contractVersion;
        TransformChain = transformChain;
    }

    public string ProjectId { get; }

    public string PanelId { get; }

    public string InputSha256 { get; }

    public ModelIdentity Model { get; }

    public MarkerImageFrame Image { get; }

    public IReadOnlyList<MarkerCenter> Markers { get; }

    public MarkerClassificationOptions Options { get; }

    public int ContractVersion { get; }

    public string TransformChain { get; }

    private static MarkerImageFrame CopyFrame(MarkerImageFrame frame) =>
        new(
            frame.Width,
            frame.Height,
            frame.ChannelCount,
            frame.ChannelsFirstPixels.ToArray(),
            frame.SourceImage,
            frame.OriginalToFrame,
            CopyMask(frame.OcrMask),
            CopyMask(frame.ArtifactMask));

    private static MarkerMask CopyMask(MarkerMask mask) =>
        new(mask.Width, mask.Height, mask.Values.ToArray());
}

public sealed class MarkerPatch
{
    public MarkerPatch(
        MarkerCenter marker,
        int width,
        int height,
        int channelCount,
        ReadOnlyMemory<float> channelsFirstPixels)
    {
        Marker = marker ?? throw new ArgumentNullException(nameof(marker));
        Width = width;
        Height = height;
        ChannelCount = channelCount;
        ChannelsFirstPixels = new ReadOnlyMemory<float>(channelsFirstPixels.ToArray());
    }

    public MarkerCenter Marker { get; }

    public int Width { get; }

    public int Height { get; }

    public int ChannelCount { get; }

    public ReadOnlyMemory<float> ChannelsFirstPixels { get; }
}

public sealed record MarkerSymbolDescriptor(string Symbol, string AccessibleName);

public sealed class ClassifiedMarker
{
    public ClassifiedMarker(
        MarkerCenter marker,
        MarkerShape shape,
        MarkerFill fill,
        string symbol,
        string accessibleName,
        double artifactProbability,
        double shapeConfidence,
        double fillConfidence,
        IEnumerable<float> embedding)
    {
        Marker = marker ?? throw new ArgumentNullException(nameof(marker));
        Shape = shape;
        Fill = fill;
        Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        AccessibleName = accessibleName ?? throw new ArgumentNullException(nameof(accessibleName));
        ArtifactProbability = artifactProbability;
        ShapeConfidence = shapeConfidence;
        FillConfidence = fillConfidence;
        Embedding = ClassificationCollections.Freeze(
            embedding ?? throw new ArgumentNullException(nameof(embedding)));
    }

    public MarkerCenter Marker { get; }

    public MarkerShape Shape { get; }

    public MarkerFill Fill { get; }

    public string Symbol { get; }

    public string AccessibleName { get; }

    public double ArtifactProbability { get; }

    public double ShapeConfidence { get; }

    public double FillConfidence { get; }

    public IReadOnlyList<float> Embedding { get; }

    public double Confidence => Math.Min(ShapeConfidence, FillConfidence);
}

public sealed record MarkerClassificationTiming(
    double PatchExtractionMilliseconds,
    double InferenceMilliseconds,
    double PostprocessMilliseconds,
    double TotalMilliseconds);

public sealed record MarkerClassificationFailure(
    string Code,
    string Severity,
    string UserMessageKey,
    string TechnicalMessage,
    bool Recoverable,
    string SuggestedAction);

public sealed class MarkerClassificationBatchReport
{
    public MarkerClassificationBatchReport(
        int batchIndex,
        int markerCount,
        InferenceProvider? provider,
        IEnumerable<ProviderAttempt> providerAttempts,
        MarkerClassificationTiming timing,
        bool cacheHit,
        MarkerClassificationFailure? failure)
    {
        BatchIndex = batchIndex;
        MarkerCount = markerCount;
        Provider = provider;
        ProviderAttempts = ClassificationCollections.Freeze(
            providerAttempts ?? throw new ArgumentNullException(nameof(providerAttempts)));
        Timing = timing ?? throw new ArgumentNullException(nameof(timing));
        CacheHit = cacheHit;
        Failure = failure;
    }

    public int BatchIndex { get; }

    public int MarkerCount { get; }

    public InferenceProvider? Provider { get; }

    public IReadOnlyList<ProviderAttempt> ProviderAttempts { get; }

    public MarkerClassificationTiming Timing { get; }

    public bool CacheHit { get; }

    public MarkerClassificationFailure? Failure { get; }
}

public sealed record MarkerClassificationModelReport(
    string ModelId,
    string Version,
    string Sha256,
    InferenceProvider? Provider);

public sealed class MarkerClassificationResult
{
    public MarkerClassificationResult(
        int contractVersion,
        string runId,
        string projectId,
        string panelId,
        string stage,
        string stageVersion,
        string inputSha256,
        string coordinateSpace,
        IEnumerable<ClassifiedMarker> markers,
        MarkerClassificationTiming timing,
        double confidence,
        IEnumerable<string> warnings,
        IEnumerable<MarkerClassificationBatchReport> batches,
        MarkerClassificationModelReport model,
        MarkerClassificationFailure? failure)
    {
        ContractVersion = contractVersion;
        RunId = runId;
        ProjectId = projectId;
        PanelId = panelId;
        Stage = stage;
        StageVersion = stageVersion;
        InputSha256 = inputSha256;
        CoordinateSpace = coordinateSpace;
        Markers = ClassificationCollections.Freeze(
            markers ?? throw new ArgumentNullException(nameof(markers)));
        Timing = timing ?? throw new ArgumentNullException(nameof(timing));
        Confidence = confidence;
        Warnings = ClassificationCollections.Freeze(
            warnings ?? throw new ArgumentNullException(nameof(warnings)));
        Batches = ClassificationCollections.Freeze(
            batches ?? throw new ArgumentNullException(nameof(batches)));
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Failure = failure;
    }

    public int ContractVersion { get; }

    public string RunId { get; }

    public string ProjectId { get; }

    public string PanelId { get; }

    public string Stage { get; }

    public string StageVersion { get; }

    public string InputSha256 { get; }

    public string CoordinateSpace { get; }

    public IReadOnlyList<ClassifiedMarker> Markers { get; }

    public MarkerClassificationTiming Timing { get; }

    public double Confidence { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<MarkerClassificationBatchReport> Batches { get; }

    public MarkerClassificationModelReport Model { get; }

    public MarkerClassificationFailure? Failure { get; }

    public bool Succeeded => Failure is null;
}

public interface IMarkerPatchExtractor
{
    IReadOnlyList<MarkerPatch> Extract(
        MarkerImageFrame image,
        IReadOnlyList<MarkerCenter> markers,
        MarkerPatchExtractionOptions options,
        CancellationToken cancellationToken);
}

public interface IMarkerClassificationInferenceRunner
{
    ValueTask<InferenceResponse> RunAsync(
        InferenceRequest request,
        CancellationToken cancellationToken);
}

public interface IMarkerClassificationService
{
    ValueTask<MarkerClassificationResult> ClassifyAsync(
        MarkerClassificationRequest request,
        CancellationToken cancellationToken);
}

internal static class ClassificationCollections
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
