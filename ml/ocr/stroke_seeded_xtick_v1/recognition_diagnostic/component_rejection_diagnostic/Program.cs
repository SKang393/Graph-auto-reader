// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Inference;
using GraphReader.Ocr;
using OpenCvSharp;

internal static class Program
{
    private const string PredictionsSha256 = "235d39476cf3ef498d0c026f05e4c9eb626fe43c853d0af49ab6d0bef2d54097";
    private const string P2CheckpointSha256 = "9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351";
    private const string ProbeManifestSha256 = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba";
    private const string V5ModelSha256 = "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84";
    private const string CorpusRoot = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3";
    private const string RecipeId = "stroke-seeded-xtick-v1-p2-v5-component-rejection-diagnostic-v1";
    private const string Alphabet = "0123456789.-%";
    private const int RejectClassIndex = 13;
    private const double ConfidenceThreshold = 0.65;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private delegate object EncodeCropInvoker(ReadOnlySpan<float> pixels, LocalOnnxComponentTextRecognizerOptions options);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: ComponentRejectionDiagnostic <repo-root> <pinned-predictions.json> <report.json>");
            return 2;
        }

        try
        {
            string root = Path.GetFullPath(args[0]);
            byte[] predictionsBytes = await File.ReadAllBytesAsync(Path.GetFullPath(args[1]));
            Require(Hash(predictionsBytes) == PredictionsSha256, "P2 prediction snapshot SHA-256 mismatch.");
            byte[] manifestBytes = await ReadBoundedAsync(root, $"{CorpusRoot}/manifest.json", ProbeManifestSha256, 5_000_000);
            byte[] modelBytes = await ReadBoundedAsync(root,
                "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx",
                V5ModelSha256, 10_000_000);
            using JsonDocument predictionsDocument = JsonDocument.Parse(predictionsBytes);
            JsonElement predictionRoot = predictionsDocument.RootElement;
            Require(predictionRoot.GetProperty("probe_manifest_sha256").GetString() == ProbeManifestSha256,
                "Prediction snapshot manifest identity mismatch.");
            Require(predictionRoot.GetProperty("detector_checkpoint_sha256").GetString() == P2CheckpointSha256,
                "Prediction snapshot checkpoint identity mismatch.");
            Require(predictionRoot.GetProperty("v5_recognizer_onnx_sha256").GetString() == V5ModelSha256,
                "Prediction snapshot recognizer identity mismatch.");
            Require(predictionRoot.GetProperty("training_performed").GetBoolean() == false,
                "Inference-only snapshot expected.");

            using JsonDocument manifestDocument = JsonDocument.Parse(manifestBytes);
            var manifestRows = manifestDocument.RootElement.GetProperty("records").EnumerateArray()
                .ToDictionary(static row => row.GetProperty("case_id").GetString()!, StringComparer.Ordinal);
            var records = new List<ProbeRow>();
            foreach (JsonElement row in predictionRoot.GetProperty("records").EnumerateArray())
            {
                if (row.GetProperty("kind").GetString() != "positive") continue;
                double score = row.GetProperty("top_one_score").GetDouble();
                if (score < 0.25) continue;
                string caseId = row.GetProperty("case_id").GetString()!;
                Require(manifestRows.TryGetValue(caseId, out JsonElement manifestRow), $"Manifest lacks case {caseId}.");
                byte[] annotationBytes = await ReadBoundedAsync(root,
                    $"{CorpusRoot}/{manifestRow.GetProperty("annotation_path").GetString()}",
                    manifestRow.GetProperty("annotation_sha256").GetString()!, 65_536);
                using JsonDocument annotationDoc = JsonDocument.Parse(annotationBytes);
                JsonElement annotation = annotationDoc.RootElement;
                string targetText = annotation.GetProperty("expected_tick_label").GetString()!;
                Require(row.GetProperty("expected_text").GetString() == targetText, $"Target label mismatch {caseId}.");
                JsonElement target = annotation.GetProperty("targets");
                Require(target.GetArrayLength() == 1, $"Target is not single-box {caseId}.");
                JsonElement predictedArray = row.GetProperty("top_one_xyxy_normalized");
                double x0 = predictedArray[0].GetDouble() * 256, y0 = predictedArray[1].GetDouble() * 96;
                double x1 = predictedArray[2].GetDouble() * 256, y1 = predictedArray[3].GetDouble() * 96;
                var predictedBox = new OcrRectangle(x0, y0, x1 - x0, y1 - y0);
                Require(predictedBox.IsValid && x0 >= 0 && y0 >= 0 && x1 <= 256 && y1 <= 96,
                    $"Predicted box invalid {caseId}.");
                records.Add(new ProbeRow(caseId, targetText,
                    manifestRow.GetProperty("image_path").GetString()!, manifestRow.GetProperty("image_sha256").GetString()!,
                    predictedBox));
            }
            Require(records.Count == 165, $"Expected 165 threshold-selected positive crops, got {records.Count}.");

            string modelPath = Path.Combine(root,
                "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx");
            var model = new ModelIdentity("graph-numeric-component-ensemble-v5", "0.0.21-p1", V5ModelSha256, modelPath);
            var providers = new[] { InferenceProvider.Cpu };
            var discovery = new OrtExecutionProviderDiscovery();
            var policy = new WindowsExecutionProviderPolicy();
            var registry = new OnnxSessionRegistry(discovery, policy,
                new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance), CpuThreadConfiguration.Create(1));
            var providerCapture = new ProviderCaptureCache();
            var scheduler = new BoundedInferenceScheduler(capacity: 4, workerCount: 1);
            await using var runtime = new InferenceRuntime(registry, scheduler, providerCapture, allowedProviders: providers);
            var options = new LocalOnnxComponentTextRecognizerOptions(model, Alphabet)
            {
                StageVersion = "stroke-seeded-v5-rejection-diagnostic-v1",
                AllowedProviders = providers,
                BypassCache = false,
            };
            var recognizer = new LocalOnnxComponentTextRecognizer(runtime, options);
            EncodeCropInvoker encodeCrop = CreateEncoder();

            var crops = new List<OcrCrop>(records.Count);
            var encoded = new List<EncodedInfo>(records.Count);
            foreach (ProbeRow row in records)
            {
                byte[] png = await ReadBoundedAsync(root, $"{CorpusRoot}/{row.ImagePath}", row.ImageSha256, 1_000_000);
                using Mat raster = Cv2.ImDecode(png, ImreadModes.Grayscale);
                Require(!raster.Empty() && raster.Width == 256 && raster.Height == 96 && raster.Type() == MatType.CV_8UC1,
                    $"Probe image shape/mode mismatch {row.CaseId}.");
                byte[] pixels = new byte[256 * 96];
                System.Runtime.InteropServices.Marshal.Copy(raster.Data, pixels, 0, pixels.Length);
                var image = new OcrImage(256, 96, 256, pixels, OcrSourceImage.Original,
                    OcrFrameTransform.Identity, CanonicalOriginalWidth: 256, CanonicalOriginalHeight: 96);
                var region = new OcrDetectedRegion(row.CaseId,
                    OcrPolygon.FromRectangle(row.Box), 0, 1.0);
                IReadOnlyList<IReadOnlyList<OcrCrop>> batches = OcrCropBatcher.CreateBatches(image, [region], new OcrCropBatcherOptions
                {
                    TargetWidth = 128, TargetHeight = 32, BatchSize = 16,
                    HorizontalPaddingPixels = 12, VerticalPaddingPixels = 1,
                    VerticalContentPaddingRatio = 0.25,
                    ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad,
                    PaddingValue = 1.0f,
                });
                Require(batches.Count == 1 && batches[0].Count == 1, $"Unexpected crop count {row.CaseId}.");
                OcrCrop crop = batches[0][0];
                Require(crop.Width == 128 && crop.Height == 32 && crop.OriginalPolygon.Bounds == row.Box,
                    $"Crop transform contract mismatch {row.CaseId}.");
                crops.Add(crop);
                object details = encodeCrop(crop.Pixels.Span, options);
                encoded.Add(EncodedInfo.From(details));
            }

            int totalGlyphs = encoded.Sum(static item => item.Count);
            Require(totalGlyphs > 0, "No component features were encoded across the probe batch.");
            IReadOnlyList<OcrRecognition> adapterResults = await recognizer.RecognizeBatchAsync(crops, CancellationToken.None);
            Require(adapterResults.Count == crops.Count, "Production adapter result count mismatch.");
            Require(providerCapture.Providers.Count == 1 && providerCapture.Providers.ContainsKey(InferenceProvider.Cpu),
                "Expected one CPU inference batch/provider record.");
            float[] logits = providerCapture.GetOutput();
            int classCount = Alphabet.Length + 1;
            Require(logits.Length == checked(totalGlyphs * classCount),
                "Captured adapter logits do not match reflection-inspected component encoding.");

            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int adapterParityMismatches = 0, exact = 0, wrong = 0;
            var adapterFailureCodes = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int glyphOffset = 0;
            for (int index = 0; index < encoded.Count; index++)
            {
                EncodedInfo item = encoded[index];
                OcrRecognition adapter = adapterResults[index];
                string? inferred = null;
                string? diagnosticCategory;
                if (adapter.Failure is not null)
                {
                    diagnosticCategory = "explicit_failure";
                    string failureKey = $"{adapter.Failure.Severity}:{adapter.Failure.Code}";
                    adapterFailureCodes[failureKey] = adapterFailureCodes.GetValueOrDefault(failureKey) + 1;
                }
                else if (item.AmbiguousBaselineSingleton)
                {
                    diagnosticCategory = "ambiguous_baseline_singleton";
                }
                else if (item.Rejected)
                {
                    diagnosticCategory = "component_geometry_or_glyph_count_reject";
                }
                else if (item.Count == 0)
                {
                    diagnosticCategory = "no_recognizable_components";
                }
                else
                {
                    (diagnosticCategory, inferred) = DiagnoseLogits(
                        logits.AsSpan(glyphOffset * classCount, item.Count * classCount), item.Count, options);
                }

                if (adapter.Failure is null && adapter.Alternatives.Count > 0)
                {
                    string actualText = adapter.Alternatives.OrderByDescending(static value => value.Confidence).First().Text;
                    if (inferred is null || !string.Equals(inferred, actualText, StringComparison.Ordinal)) adapterParityMismatches++;
                    if (string.Equals(actualText, records[index].ExpectedText, StringComparison.Ordinal)) exact++;
                    else wrong++;
                    diagnosticCategory = string.Equals(actualText, records[index].ExpectedText, StringComparison.Ordinal)
                        ? "recognized_exact" : "recognized_wrong";
                }
                else if (diagnosticCategory is "recognized_exact" or "recognized_wrong")
                {
                    adapterParityMismatches++;
                }

                if (adapter.Failure is not null && !item.AmbiguousBaselineSingleton) adapterParityMismatches++;
                if (adapter.Failure?.Code == "OCR_COMPONENT_BASELINE_SINGLETON_AMBIGUOUS" && !item.AmbiguousBaselineSingleton)
                    adapterParityMismatches++;
                counts[diagnosticCategory] = counts.GetValueOrDefault(diagnosticCategory) + 1;
                glyphOffset += item.Count;
            }

            Require(counts.Values.Sum() == records.Count, "Diagnostic categories do not account for every crop.");
            Require(adapterParityMismatches == 0,
                $"Private preprocessing/logit explanation differed from production adapter on {adapterParityMismatches} crops.");
            string sourcePath = Path.Combine(root,
                "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/component_rejection_diagnostic/Program.cs");
            var report = new
            {
                schema = "graphreader.stroke-seeded-xtick-v1-p2-v5-component-rejection-result.v1",
                recipe_id = RecipeId,
                acceptance_status = "consumed synthetic diagnostic only",
                runner_sha256 = Hash(await File.ReadAllBytesAsync(sourcePath)),
                prediction_snapshot_sha256 = Hash(predictionsBytes),
                detector_checkpoint_sha256 = P2CheckpointSha256,
                recognizer_onnx_sha256 = V5ModelSha256,
                probe_manifest_sha256 = ProbeManifestSha256,
                selected_positive_crops = records.Count,
                score_threshold = 0.25,
                component_feature_count = totalGlyphs,
                confidence_threshold = ConfidenceThreshold,
                reject_class_index = RejectClassIndex,
                rejection_categories = counts,
                accepted_exact = exact,
                accepted_wrong = wrong,
                explicit_failure_codes = adapterFailureCodes,
                adapter_logit_preprocessing_parity = new
                {
                    sample_count = records.Count, mismatches = adapterParityMismatches,
                    captured_output_values = logits.Length,
                    captured_output_sha256 = Hash(MemoryMarshalBytes(logits)),
                    captured_provider = "CPUExecutionProvider",
                    provider_calls = providerCapture.Providers[InferenceProvider.Cpu],
                },
                attribution_limit = "Uses reflection to invoke the exact private EncodeCrop implementation and reads the exact adapter ONNX logits from the runtime cache seam. It derives reject/confidence/grammar decisions using the adapter code order. Geometry-rejected combines tall-component and over-maximum-glyph rejection because production returns one Rejected flag; no per-crop encoder counts or logits are retained in the report.",
                inference_milliseconds = adapterResults.Sum(static result => result.InferenceMilliseconds),
                training_performed = false, dev_sealed_private_inputs_read = false,
                production_approval = false, release_eligible = false,
                case_level_predictions_retained = false,
            };
            string reportPath = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"failed: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static (string Category, string? Text) DiagnoseLogits(
        ReadOnlySpan<float> logits, int glyphs, LocalOnnxComponentTextRecognizerOptions options)
    {
        int classCount = options.Alphabet.Length + 1;
        var text = new System.Text.StringBuilder(glyphs);
        for (int glyph = 0; glyph < glyphs; glyph++)
        {
            ReadOnlySpan<float> row = logits.Slice(glyph * classCount, classCount);
            float maximum = float.NegativeInfinity;
            int bestClass = -1;
            for (int index = 0; index < row.Length; index++)
            {
                Require(float.IsFinite(row[index]), "Captured model logit is non-finite.");
                if (row[index] > maximum)
                {
                    maximum = row[index];
                    bestClass = index;
                }
            }
            double denominator = 0;
            for (int index = 0; index < row.Length; index++) denominator += Math.Exp(row[index] - maximum);
            double bestProbability = Math.Exp(row[bestClass] - maximum) / denominator;
            if (bestClass == options.RejectClassIndex) return ("model_reject_class", null);
            if (bestProbability < options.ConfidenceThreshold) return ("confidence_below_threshold", null);
            text.Append(options.Alphabet[bestClass]);
        }

        string value = text.ToString();
        return GraphNumericParser.IsLiteralGraphNumber(value)
            ? ("recognized", value)
            : ("numeric_grammar_reject", null);
    }

    private static EncodeCropInvoker CreateEncoder()
    {
        MethodInfo method = typeof(LocalOnnxComponentTextRecognizer).GetMethod("EncodeCrop",
            BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException("Private EncodeCrop method not found.");
        Require(method.ReturnType.IsClass && method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType == typeof(ReadOnlySpan<float>) &&
                method.GetParameters()[1].ParameterType == typeof(LocalOnnxComponentTextRecognizerOptions),
            "Production private preprocessing signature changed; do not use this diagnostic against a different adapter.");
        var dynamic = new DynamicMethod("InvokePinnedV5EncodeCrop", typeof(object),
            [typeof(ReadOnlySpan<float>), typeof(LocalOnnxComponentTextRecognizerOptions)],
            typeof(Program).Module, skipVisibility: true);
        ILGenerator il = dynamic.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);
        return (EncodeCropInvoker)dynamic.CreateDelegate(typeof(EncodeCropInvoker));
    }

    private static async Task<byte[]> ReadBoundedAsync(string root, string relative, string sha, int maximum)
    {
        string projectRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(path.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase), "Input escaped project root.");
        var info = new FileInfo(path);
        Require(info.Exists && info.Length is > 0 && info.Length <= maximum, $"Input size invalid: {relative}.");
        for (FileSystemInfo? current = info; current is not null &&
             !string.Equals(current.FullName, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase); current = current switch
             {
                 FileInfo file => file.Directory,
                 DirectoryInfo directory => directory.Parent,
                 _ => null,
             })
            Require((current.Attributes & FileAttributes.ReparsePoint) == 0, $"Reparse point in path: {relative}.");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Require(Hash(bytes) == sha, $"Input SHA mismatch: {relative}.");
        return bytes;
    }

    private static byte[] MemoryMarshalBytes(float[] values) =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record ProbeRow(string CaseId, string ExpectedText, string ImagePath, string ImageSha256, OcrRectangle Box);

    private sealed record EncodedInfo(float[] Values, int Count, bool Rejected, bool AmbiguousBaselineSingleton)
    {
        public static EncodedInfo From(object value)
        {
            Type type = value.GetType();
            float[] values = (float[]?)type.GetProperty("Values")?.GetValue(value)
                ?? throw new InvalidDataException("Exact production encoder did not expose its encoded feature vector.");
            int count = (int)(type.GetProperty("Count")?.GetValue(value) ?? -1);
            bool rejected = (bool)(type.GetProperty("Rejected")?.GetValue(value) ?? false);
            bool ambiguous = (bool)(type.GetProperty("HasAmbiguousBaselineSingleton")?.GetValue(value) ?? false);
            Require(count >= 0 && values.Length == count * 24 * 26, "Private encoder output shape differs from V5 contract.");
            return new EncodedInfo(values, count, rejected, ambiguous);
        }
    }

    private sealed class ProviderCaptureCache : IStageCache
    {
        private byte[]? _last;
        public Dictionary<InferenceProvider, int> Providers { get; } = [];
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            byte[] bytes = value.ToArray();
            if (bytes.Length < 42) throw new InvalidDataException("ONNX cache entry header is incomplete.");
            InferenceProvider provider = (InferenceProvider)bytes[5];
            Providers[provider] = Providers.GetValueOrDefault(provider) + 1;
            _last = bytes;
            return ValueTask.CompletedTask;
        }

        public float[] GetOutput()
        {
            byte[] bytes = _last ?? throw new InvalidDataException("No adapter ONNX output was captured.");
            int count = BitConverter.ToInt32(bytes, 6);
            Require(count >= 0 && bytes.Length == 42 + count * sizeof(float), "Captured ONNX cache output length is invalid.");
            var values = new float[count];
            for (int index = 0; index < count; index++) values[index] = BitConverter.ToSingle(bytes, 42 + index * sizeof(float));
            return values;
        }
    }
}
