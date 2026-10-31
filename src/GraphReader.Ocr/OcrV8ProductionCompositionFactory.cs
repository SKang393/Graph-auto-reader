// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;
using GraphReader.Inference;

namespace GraphReader.Ocr;

public sealed record OcrV8ProductionPayloadSet(
    ModelIdentity Detector,
    ModelIdentity OfficialRecognizer,
    ModelIdentity NumericRecognizer,
    ModelIdentity AmbiguityRecognizer,
    string OfficialAlphabet);

/// <summary>
/// Creates only the immutable four-payload V8 composition that passed the
/// public Python gate. This is composition evidence, not model approval. A
/// production caller must still resolve these exact bytes from an approved
/// production model store before enabling Auto Detect.
/// </summary>
public static class OcrV8ProductionCompositionFactory
{
    public const string DetectorSha256 =
        "474b8468dbd91416f4e4978dafc46cb2317775d59d821c0470e0cd3e0f6203db";
    public const string OfficialRecognizerSha256 =
        "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743";
    public const string NumericRecognizerSha256 =
        "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84";
    public const string AmbiguityRecognizerSha256 =
        "b8e2773ca3966469081875fc36b3981ef4eb458356d8dfdae2be2722602f0096";
    public const string OfficialAlphabetSha256 =
        "8b31115bc8675a58b670879950b550e7d9840984954d9584d251fd72a764477a";

    private const string DetectorModelId = "graph-text-spaced-component-recall-v10-p2";
    private const string OfficialModelId = "en_PP-OCRv5_mobile_rec";
    private const string NumericModelId = "graph-numeric-component-ensemble-v5";
    private const string AmbiguityModelId = "graph-ambiguity-source-group-v3-p2";

    public static OcrV8ProductionCompositionPipeline Create(
        InferenceRuntime runtime,
        OcrV8ProductionPayloadSet payloads,
        IReadOnlyList<InferenceProvider> allowedProviders,
        bool bypassCache = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(payloads);
        IReadOnlyList<InferenceProvider> providers = ValidateProviderPolicy(allowedProviders);
        ValidatePayloads(payloads);

        var detector = new LocalOnnxProposalTextRegionDetector(
            runtime,
            new LocalOnnxProposalTextRegionDetectorOptions(payloads.Detector)
            {
                StageVersion = "0.0.21-v10-p2",
                AllowedProviders = providers,
                BypassCache = bypassCache,
            });
        return CreateWithDetector(
            runtime,
            payloads,
            providers,
            detector,
            bypassCache);
    }

    internal static OcrV8ProductionCompositionPipeline CreateWithDetector(
        InferenceRuntime runtime,
        OcrV8ProductionPayloadSet payloads,
        IReadOnlyList<InferenceProvider> providers,
        ITextRegionProposalDetector detector,
        bool bypassCache,
        OcrV8ProductionCompositionOptions? compositionOptions = null)
    {
        var officialBase = new LocalOnnxTextRecognizer(
            runtime,
            new LocalOnnxTextRecognizerOptions(
                payloads.OfficialRecognizer,
                payloads.OfficialAlphabet)
            {
                InputWidth = 320,
                InputHeight = 48,
                InputChannels = 3,
                InputLayout = OcrTensorLayout.ChannelsFirst,
                InputColorMode = OcrTensorColorMode.GrayscaleReplicated,
                OutputLayout = OcrOutputLayout.BatchTimeClass,
                OutputActivation = OcrRecognitionOutputActivation.Probabilities,
                ExpectedTimeSteps = 40,
                BlankClassIndex = 0,
                MaximumAlternatives = 1,
                InputName = "x",
                OutputName = "fetch_name_0",
                StageVersion = "0.0.21-ppocrv5",
                ChannelMeans = [0.5f, 0.5f, 0.5f],
                ChannelScales = [2f, 2f, 2f],
                AllowedProviders = providers,
                BypassCache = bypassCache,
            });
        var sourcePostprocessor = new OcrV8SourcePostprocessor(
            runtime,
            new LocalOnnxAmbiguitySourceGroupOptions(payloads.AmbiguityRecognizer)
            {
                StageVersion = "0.0.21-p2",
                AllowedProviders = providers,
                BypassCache = bypassCache,
            });
        var official = new OcrV8OfficialTextRecognizer(officialBase, sourcePostprocessor);
        var numeric = new LocalOnnxComponentTextRecognizer(
            runtime,
            new LocalOnnxComponentTextRecognizerOptions(
                payloads.NumericRecognizer,
                "0123456789.-%")
            {
                StageVersion = "0.0.21-p1",
                AllowedProviders = V5NumericRecognizerProviderPolicy(providers),
                BypassCache = bypassCache,
            });

        return new OcrV8ProductionCompositionPipeline(
            detector,
            official,
            new MemoryOcrResultCache(),
            new OcrPipelineOptions
            {
                StageVersion = "0.0.21-v8-official-horizontal",
                BatchSize = 16,
                CropWidth = 320,
                CropHeight = 48,
                CropHorizontalPaddingPixels = 8,
                CropVerticalPaddingPixels = 2,
                CropResizeMode = OcrCropResizeMode.PreserveAspectRatioPad,
                CropPaddingValue = 0.5f,
                InferVerticalOrientationForTallRegions = false,
            },
            numeric,
            new MemoryOcrResultCache(),
            new OcrPipelineOptions
            {
                StageVersion = "0.0.21-v8-numeric-horizontal",
                BatchSize = 16,
                CropWidth = 128,
                CropHeight = 32,
                CropHorizontalPaddingPixels = 12,
                CropVerticalPaddingPixels = 1,
                CropVerticalContentPaddingRatio = 0.25,
                CropResizeMode = OcrCropResizeMode.PreserveAspectRatioPad,
                CropPaddingValue = 1f,
                InferVerticalOrientationForTallRegions = false,
            },
            compositionOptions);
    }

    internal static void ValidateRecognitionPayloads(OcrV8ProductionPayloadSet payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ValidatePayload(
            payloads.OfficialRecognizer,
            OfficialModelId,
            OfficialRecognizerSha256,
            nameof(payloads.OfficialRecognizer));
        ValidatePayload(
            payloads.NumericRecognizer,
            NumericModelId,
            NumericRecognizerSha256,
            nameof(payloads.NumericRecognizer));
        ValidatePayload(
            payloads.AmbiguityRecognizer,
            AmbiguityModelId,
            AmbiguityRecognizerSha256,
            nameof(payloads.AmbiguityRecognizer));

        string alphabetHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(payloads.OfficialAlphabet ?? string.Empty)));
        int runeCount = payloads.OfficialAlphabet?.EnumerateRunes().Count() ?? 0;
        if (runeCount != 437 ||
            !string.Equals(alphabetHash, OfficialAlphabetSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The PP-OCRv5 alphabet is not the exact 437-symbol reviewed dictionary plus space class.");
        }
    }

    public static void ValidatePayloads(OcrV8ProductionPayloadSet payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ValidatePayload(payloads.Detector, DetectorModelId, DetectorSha256, nameof(payloads.Detector));
        ValidateRecognitionPayloads(payloads);
    }

    internal static void ValidatePayload(
        ModelIdentity model,
        string expectedModelId,
        string expectedSha256,
        string field)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Validate();
        if (!string.Equals(model.ModelId, expectedModelId, StringComparison.Ordinal) ||
            !string.Equals(model.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(model.FilePath))
        {
            throw new InvalidDataException($"OCR V8 payload '{field}' is absent or has an unreviewed identity.");
        }

        string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(model.FilePath)));
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"OCR V8 payload '{field}' failed its exact byte checksum.");
        }
    }

    internal static System.Collections.ObjectModel.ReadOnlyCollection<InferenceProvider> ValidateProviderPolicy(
        IReadOnlyList<InferenceProvider> allowedProviders)
    {
        ArgumentNullException.ThrowIfNull(allowedProviders);
        InferenceProvider[] providers = allowedProviders.Distinct().ToArray();
        if (providers.Length != allowedProviders.Count ||
            providers.Length == 0 ||
            !providers.Contains(InferenceProvider.Cpu) ||
            providers.Any(static provider =>
                provider is not (InferenceProvider.Cpu or InferenceProvider.DirectMl)))
        {
            throw new ArgumentException(
                "OCR V8 provider policy must be unique, production-only, and retain CPU fallback.",
                nameof(allowedProviders));
        }

        return Array.AsReadOnly(providers);
    }

    internal static IReadOnlyList<InferenceProvider> V5NumericRecognizerProviderPolicy(
        IReadOnlyList<InferenceProvider> allowedProviders)
    {
        _ = ValidateProviderPolicy(allowedProviders);
        return Array.AsReadOnly(new[] { InferenceProvider.Cpu });
    }
}
