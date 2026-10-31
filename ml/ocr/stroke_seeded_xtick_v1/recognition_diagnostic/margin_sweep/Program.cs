// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Inference;
using GraphReader.Ocr;
using OpenCvSharp;

internal static class Program
{
    private const string PredictionsSha256 = "235d39476cf3ef498d0c026f05e4c9eb626fe43c853d0af49ab6d0bef2d54097";
    private const string ManifestSha256 = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba";
    private const string ModelSha256 = "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84";
    private const string ProtocolSha256 = "3c4a169eb1b51e6895152c00e0ef2ff664e8154abc911a21cbcba17e0346adfa";
    private const string CorpusRoot = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3";
    private const string ModelRelative = "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private delegate object EncodeCropInvoker(ReadOnlySpan<float> pixels, LocalOnnxComponentTextRecognizerOptions options);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("usage: MarginSweep <repo-root> <pinned-predictions.json> <baseline-report.json> <report.json>");
            return 2;
        }

        try
        {
            string root = Path.GetFullPath(args[0]);
            byte[] predictionsBytes = await File.ReadAllBytesAsync(Path.GetFullPath(args[1]));
            Require(Hash(predictionsBytes) == PredictionsSha256, "Pinned predictions SHA mismatch.");
            byte[] baselineBytes = await File.ReadAllBytesAsync(Path.GetFullPath(args[2]));
            byte[] manifestBytes = await ReadProjectBytesAsync(root, $"{CorpusRoot}/manifest.json", ManifestSha256, 5_000_000);
            byte[] modelBytes = await ReadProjectBytesAsync(root, ModelRelative, ModelSha256, 10_000_000);
            byte[] protocolBytes = await ReadProjectBytesAsync(root, "ml/ocr/component_ensemble_v5/PROTOCOL.json", ProtocolSha256, 1_000_000);
            _ = modelBytes; _ = protocolBytes;

            using JsonDocument predictionsDoc = JsonDocument.Parse(predictionsBytes);
            JsonElement predictionRoot = predictionsDoc.RootElement;
            Require(predictionRoot.GetProperty("probe_manifest_sha256").GetString() == ManifestSha256, "Probe manifest identity mismatch.");
            Require(predictionRoot.GetProperty("v5_recognizer_onnx_sha256").GetString() == ModelSha256, "Recognizer identity mismatch.");
            Require(predictionRoot.GetProperty("training_performed").GetBoolean() == false, "Prediction input is not inference-only.");
            using JsonDocument baselineDoc = JsonDocument.Parse(baselineBytes);
            JsonElement baselineRoot = baselineDoc.RootElement;
            Require(Hash(baselineBytes) == "8bb9ee29ab4ac0ee0f09572fabd367624d82cc11d5a33d42a044752d56bd018e",
                "Pinned comparison baseline SHA mismatch.");
            var manifestRows = JsonDocument.Parse(manifestBytes).RootElement.GetProperty("records").EnumerateArray()
                .ToDictionary(static row => row.GetProperty("case_id").GetString()!, StringComparer.Ordinal);

            MethodInfo filterMethod = PrivateMethod("FilterForeground", 3);
            MethodInfo intervalsMethod = PrivateMethod("ActiveIntervals", 3);
            MethodInfo glyphMethod = PrivateMethod("EncodeGlyph", 5);
            EncodeCropInvoker encodeCrop = CreateEncodeCropInvoker(PrivateMethod("EncodeCrop", 2));
            var baseOptions = new LocalOnnxComponentTextRecognizerOptions(
                new ModelIdentity("graph-numeric-component-ensemble-v5", "0.0.21-p1", ModelSha256,
                    Path.Combine(root, ModelRelative)), "0123456789.-%");

            var rows = new List<ProbeRow>();
            foreach (JsonElement prediction in predictionRoot.GetProperty("records").EnumerateArray())
            {
                string caseId = prediction.GetProperty("case_id").GetString()!;
                Require(manifestRows.TryGetValue(caseId, out JsonElement manifest), $"Missing manifest row {caseId}.");
                string annotationPath = manifest.GetProperty("annotation_path").GetString()!;
                byte[] annotationBytes = await ReadProjectBytesAsync(root, $"{CorpusRoot}/{annotationPath}",
                    manifest.GetProperty("annotation_sha256").GetString()!, 65_536);
                using JsonDocument annotationDoc = JsonDocument.Parse(annotationBytes);
                JsonElement annotation = annotationDoc.RootElement;
                JsonElement targets = annotation.GetProperty("targets");
                OcrRectangle? target = null;
                if (targets.GetArrayLength() == 1)
                {
                    JsonElement xyxy = targets[0].GetProperty("target_crop_xyxy");
                    target = new OcrRectangle(xyxy[0].GetDouble(), xyxy[1].GetDouble(),
                        xyxy[2].GetDouble() - xyxy[0].GetDouble(), xyxy[3].GetDouble() - xyxy[1].GetDouble());
                }
                else Require(targets.GetArrayLength() == 0, $"Ambiguous target count: {caseId}.");
                string? expected = annotation.GetProperty("expected_tick_label").ValueKind == JsonValueKind.String
                    ? annotation.GetProperty("expected_tick_label").GetString() : null;
                Require(expected == (prediction.GetProperty("expected_text").ValueKind == JsonValueKind.String
                    ? prediction.GetProperty("expected_text").GetString() : null), $"Text label mismatch {caseId}.");
                double score = prediction.GetProperty("top_one_score").GetDouble();
                JsonElement coords = prediction.GetProperty("top_one_xyxy_normalized");
                double px0 = coords[0].GetDouble() * 256, py0 = coords[1].GetDouble() * 96;
                double px1 = coords[2].GetDouble() * 256, py1 = coords[3].GetDouble() * 96;
                var predicted = new OcrRectangle(px0, py0, px1 - px0, py1 - py0);
                Require(predicted.IsValid && predicted.X >= 0 && predicted.Y >= 0 && predicted.Right <= 256 && predicted.Bottom <= 96,
                    $"Invalid predicted box {caseId}.");
                if (target is not null) Require(target.Value.IsValid, $"Invalid ground truth box {caseId}.");
                rows.Add(new ProbeRow(caseId, manifest.GetProperty("image_path").GetString()!,
                    manifest.GetProperty("image_sha256").GetString()!, prediction.GetProperty("kind").GetString()!,
                    expected, target, predicted, score));
            }
            Require(rows.Count == 323 && rows.Count(static row => row.Target is not null) == 165 &&
                rows.Count(static row => row.Target is null) == 158, "Probe composition changed.");

            var capture = new ProviderCaptureCache();
            IReadOnlyList<InferenceProvider> providers = [InferenceProvider.Cpu];
            var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(),
                new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance), CpuThreadConfiguration.Create(1));
            var scheduler = new BoundedInferenceScheduler(capacity: 4, workerCount: 1);
            await using var runtime = new InferenceRuntime(registry, scheduler, capture, allowedProviders: providers);
            var experimentResults = new Dictionary<string, object>(StringComparer.Ordinal);
            var baselineParity = new Dictionary<string, bool>(StringComparer.Ordinal);
            var elapsedByMargin = new Dictionary<string, double>(StringComparer.Ordinal);

            foreach (double margin in new[] { 0.25, 0.5, 0.75 })
            {
                string marginKey = margin.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                var options = baseOptions with
                {
                    StageVersion = $"stroke-seeded-v5-margin-diagnostic-v1-{marginKey}",
                    AllowedProviders = providers,
                    BypassCache = true,
                };
                var recognizer = new LocalOnnxComponentTextRecognizer(runtime, options);
                var crops = new List<CropEntry>(488);
                foreach (ProbeRow row in rows)
                {
                    byte[] png = await ReadProjectBytesAsync(root, $"{CorpusRoot}/{row.ImagePath}", row.ImageSha256, 1_000_000);
                    using Mat decoded = Cv2.ImDecode(png, ImreadModes.Grayscale);
                    Require(!decoded.Empty() && decoded.Width == 256 && decoded.Height == 96 && decoded.Type() == MatType.CV_8UC1,
                        $"Image contract mismatch {row.CaseId}.");
                    byte[] pixels = new byte[256 * 96];
                    System.Runtime.InteropServices.Marshal.Copy(decoded.Data, pixels, 0, pixels.Length);
                    var image = new OcrImage(256, 96, 256, pixels, OcrSourceImage.Original,
                        OcrFrameTransform.Identity, CanonicalOriginalWidth: 256, CanonicalOriginalHeight: 96);
                    if (row.Target is not null)
                        crops.Add(CreateEntry(image, row, "gt", row.Target.Value, margin, encodeCrop, filterMethod, intervalsMethod, glyphMethod, options));
                    if (row.Target is not null && row.Score >= 0.25)
                        crops.Add(CreateEntry(image, row, "pred", row.Predicted, margin, encodeCrop, filterMethod, intervalsMethod, glyphMethod, options));
                    if (row.Target is null)
                        crops.Add(CreateEntry(image, row, "background_forced", row.Predicted, margin, encodeCrop, filterMethod, intervalsMethod, glyphMethod, options));
                }
                Require(crops.Count == 488, $"Unexpected crop admissions at margin {marginKey}: {crops.Count}.");
                var outcomes = new Dictionary<string, Outcome>(StringComparer.Ordinal);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                for (int offset = 0; offset < crops.Count; offset += 16)
                {
                    CropEntry[] batch = crops.Skip(offset).Take(16).ToArray();
                    IReadOnlyList<OcrRecognition> output = await recognizer.RecognizeBatchAsync(
                        batch.Select(static item => item.Crop).ToArray(), CancellationToken.None);
                    Require(output.Count == batch.Length, "Recognizer batch output count mismatch.");
                    for (int i = 0; i < output.Count; i++)
                    {
                        OcrRecognition item = output[i];
                        OcrRecognitionAlternative? best = item.Alternatives.OrderByDescending(static alt => alt.Confidence).FirstOrDefault();
                        outcomes.Add(batch[i].Crop.RegionId, new Outcome(best?.Text, item.Failure?.Code, item.Failure?.Severity));
                    }
                }
                timer.Stop();
                elapsedByMargin[marginKey] = timer.Elapsed.TotalMilliseconds;

                object Summarize(string role)
                {
                    CropEntry[] subset = crops.Where(row => row.Role == role).ToArray();
                    int exact = 0, wrong = 0, empty = 0, failed = 0, tall = 0, glyphCount = 0, either = 0;
                    var failureCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
                    foreach (CropEntry row in subset)
                    {
                        if (row.StructuralTall) tall++;
                        if (row.TooManyGlyphs) glyphCount++;
                        if (row.StructuralTall || row.TooManyGlyphs) either++;
                        Outcome outcome = outcomes[row.Crop.RegionId];
                        if (outcome.FailureCode is not null)
                        {
                            failed++;
                            string key = $"{outcome.FailureSeverity}:{outcome.FailureCode}";
                            failureCounts[key] = failureCounts.GetValueOrDefault(key) + 1;
                        }
                        else if (outcome.Text is null) empty++;
                        else if (row.ExpectedText is not null && outcome.Text == row.ExpectedText) exact++;
                        else wrong++;
                    }
                    return new
                    {
                        crop_count = subset.Length,
                        exact, wrong, empty_without_alternative = empty, explicit_failures = failureCounts,
                        accounted = exact + wrong + empty + failed,
                        structural_tall_component_reject = tall,
                        over_maximum_glyph_count_reject = glyphCount,
                        either_structural_reject = either,
                        tall_reject_crop_percent = 100.0 * tall / Math.Max(1, subset.Length),
                        returned_text_false_outputs = role == "background_forced" ? wrong : (int?)null,
                        detector_score_at_least_025 = role == "background_forced" ? subset.Count(static row => row.Score >= 0.25) : (int?)null,
                    };
                }

                var metrics = new
                {
                    vertical_content_padding_ratio = margin,
                    ground_truth_positive_boxes = Summarize("gt"),
                    predicted_positive_boxes = Summarize("pred"),
                    forced_background_top_one_boxes = Summarize("background_forced"),
                };
                experimentResults.Add(marginKey, metrics);
                if (margin == 0.25)
                {
                    baselineParity["ground_truth_positive_boxes"] = MatchesBaseline(baselineRoot, "ground_truth_boxes", crops, outcomes, "gt");
                    baselineParity["predicted_positive_boxes"] = MatchesBaseline(baselineRoot, "predicted_boxes", crops, outcomes, "pred");
                }
            }

            Require(capture.Providers.Count == 1 && capture.Providers.ContainsKey(InferenceProvider.Cpu), "Expected CPU provider only.");
            var backgroundIdentity = rows.Where(static row => row.Target is null).GroupBy(static row => row.ImageSha256, StringComparer.Ordinal).ToArray();
            int backgroundConflictingTargets = backgroundIdentity.Count(static group => group.Select(static row => row.ExpectedText).Distinct(StringComparer.Ordinal).Count() > 1);
            Require(backgroundConflictingTargets == 0, "Identical background image bytes have conflicting text labels.");
            string sourcePath = Path.Combine(root, "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/margin_sweep/Program.cs");
            var report = new
            {
                schema = "graphreader.stroke-seeded-xtick-v1-p2-v5-vertical-margin-sweep.v1",
                recipe_id = "stroke-seeded-xtick-v1-p2-v5-margin-sweep-diagnostic-v1",
                acceptance_status = "consumed synthetic development diagnostic only",
                runner_sha256 = Hash(await File.ReadAllBytesAsync(sourcePath)),
                predictions_sha256 = Hash(predictionsBytes), baseline_report_sha256 = Hash(baselineBytes),
                probe_manifest_sha256 = ManifestSha256, detector_checkpoint_sha256 = predictionRoot.GetProperty("detector_checkpoint_sha256").GetString(),
                recognizer_onnx_sha256 = ModelSha256, v5_protocol_sha256 = ProtocolSha256,
                cpu_provider_execution_calls = capture.Providers.GetValueOrDefault(InferenceProvider.Cpu),
                crop_contract = new
                {
                    cropper = "GraphReader.Ocr.OcrCropBatcher", input_canvas = "128x32 grayscale",
                    resize = "preserve-aspect-ratio-pad", horizontal_padding_pixels = 12,
                    vertical_padding_pixels = 1, varied_vertical_content_padding_ratio = new[] { 0.25, 0.5, 0.75 },
                    padding_value = 1.0f, coordinate_space = "original pixels, per-probe raster identity transform",
                    score_threshold_for_predicted_positive_boxes = 0.25,
                    background_handling = "all 158 background-only top-one boxes are forced through recognition at all margins, even though none meets detector score threshold; these are diagnostic false outputs, not production-admitted detections",
                },
                positive_crop_count = 165, background_only_crop_count = 158,
                background_image_identity = new
                {
                    background_rows_scored = 158,
                    unique_image_hashes = backgroundIdentity.Length,
                    repeated_image_rows = 158 - backgroundIdentity.Length,
                    conflicting_expected_text_by_identical_hash = backgroundConflictingTargets,
                    scoring_policy = "score every admitted probe row; do not deduplicate examples",
                },
                crops_per_margin = 488,
                margins = experimentResults,
                margin_025_parity_to_existing_recognition_report = baselineParity,
                inference_elapsed_milliseconds_by_margin = elapsedByMargin,
                training_performed = false, dev_sealed_private_inputs_read = false,
                production_defaults_changed = false, production_approval = false, release_eligible = false,
                retained_case_level_predictions = false,
                interpretation_limit = "The margin sweep is a controlled preprocessing diagnostic on an already-consumed synthetic probe, not held-out acceptance. Forced background OCR uses top-one boxes below detector threshold to measure recognizer false text propensity; actual detector-admitted background proposals are reported separately. Structural rejections are derived from the same private encoder methods and thresholds; result text is produced by the real LocalOnnxComponentTextRecognizer.",
            };
            string reportPath = Path.GetFullPath(args[3]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static CropEntry CreateEntry(OcrImage image, ProbeRow row, string role, OcrRectangle box, double margin,
        EncodeCropInvoker encodeCrop, MethodInfo filterMethod, MethodInfo intervalsMethod, MethodInfo glyphMethod,
        LocalOnnxComponentTextRecognizerOptions options)
    {
        var detected = new OcrDetectedRegion($"{row.CaseId}:{role}:m{margin:0.00}", OcrPolygon.FromRectangle(box), 0, row.Score);
        var batch = OcrCropBatcher.CreateBatches(image, [detected], new OcrCropBatcherOptions
        {
            TargetWidth = 128, TargetHeight = 32, BatchSize = 16, HorizontalPaddingPixels = 12,
            VerticalPaddingPixels = 1, VerticalContentPaddingRatio = margin,
            ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad, PaddingValue = 1.0f,
        });
        Require(batch.Count == 1 && batch[0].Count == 1, $"Cropper output mismatch {row.CaseId}/{role}.");
        OcrCrop crop = batch[0][0];
        Require(crop.Width == 128 && crop.Height == 32 && crop.OriginalPolygon.Bounds == box,
            $"Cropper coordinate contract mismatch {row.CaseId}/{role}.");
        var encoded = encodeCrop(crop.Pixels.Span, options);
        Type type = encoded.GetType();
        bool rejected = (bool)type.GetProperty("Rejected")!.GetValue(encoded)!;
        bool ambiguous = (bool)type.GetProperty("HasAmbiguousBaselineSingleton")!.GetValue(encoded)!;
        byte[] raster = crop.Pixels.Span.ToArray().Select(static value => (byte)Math.Clamp(
            (int)Math.Round(value * 255d, MidpointRounding.ToEven), 0, 255)).ToArray();
        object filtered = filterMethod.Invoke(null, [raster, 128, 32])!;
        bool[] foreground = (bool[])filtered.GetType().GetProperty("Pixels")!.GetValue(filtered)!;
        var intervals = (IEnumerable)intervalsMethod.Invoke(null, [foreground, 128, 32])!;
        int tall = 0, glyphCount = 0, count = 0;
        foreach (object? interval in intervals)
        {
            if (interval is not ITuple tuple || tuple.Length != 2) throw new InvalidDataException("ActiveIntervals shape changed.");
            object? glyph = glyphMethod.Invoke(null, [raster, foreground, (int)tuple[0]!, (int)tuple[1]!, options]);
            if (glyph is null) continue;
            count++;
            double ratio = Convert.ToDouble(glyph.GetType().GetProperty("HeightRatio")!.GetValue(glyph),
                System.Globalization.CultureInfo.InvariantCulture);
            if (ratio >= options.StructuralRejectMinimumHeightRatio) tall++;
        }
        glyphCount = count > options.MaximumGlyphs ? 1 : 0;
        Require(rejected == (!ambiguous && (tall > 0 || glyphCount > 0)), "Exact encoder reject parity failed.");
        return new CropEntry(crop, role, row.ExpectedText, row.Score, tall > 0, glyphCount > 0);
    }

    private static bool MatchesBaseline(JsonElement baseline, string key, List<CropEntry> crops,
        Dictionary<string, Outcome> outcomes, string role)
    {
        JsonElement expected = baseline.GetProperty("per_score_threshold").GetProperty("0.25").GetProperty(key);
        CropEntry[] selected = crops.Where(item => item.Role == role).ToArray();
        int exact = selected.Count(item => outcomes[item.Crop.RegionId].Text is { } text && text == item.ExpectedText && outcomes[item.Crop.RegionId].FailureCode is null);
        int wrong = selected.Count(item => outcomes[item.Crop.RegionId].Text is { } text && text != item.ExpectedText && outcomes[item.Crop.RegionId].FailureCode is null);
        int empty = selected.Count(item => outcomes[item.Crop.RegionId].Text is null && outcomes[item.Crop.RegionId].FailureCode is null);
        int failed = selected.Count(item => outcomes[item.Crop.RegionId].FailureCode is not null);
        return expected.GetProperty("sample_count").GetInt32() == selected.Length &&
            expected.GetProperty("exact").GetInt32() == exact && expected.GetProperty("wrong").GetInt32() == wrong &&
            expected.GetProperty("empty_without_alternative").GetInt32() == empty &&
            expected.GetProperty("explicit_failures_by_severity_and_code").EnumerateObject().Sum(static item => item.Value.GetInt32()) == failed;
    }

    private static MethodInfo PrivateMethod(string name, int parameterCount) =>
        typeof(LocalOnnxComponentTextRecognizer).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == parameterCount);

    private static EncodeCropInvoker CreateEncodeCropInvoker(MethodInfo method)
    {
        Require(method.ReturnType.IsClass && method.GetParameters()[0].ParameterType == typeof(ReadOnlySpan<float>),
            "EncodeCrop signature changed.");
        var dynamic = new DynamicMethod("InvokeMarginSweepEncodeCrop", typeof(object),
            [typeof(ReadOnlySpan<float>), typeof(LocalOnnxComponentTextRecognizerOptions)], typeof(Program).Module, true);
        ILGenerator il = dynamic.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, method); il.Emit(OpCodes.Ret);
        return (EncodeCropInvoker)dynamic.CreateDelegate(typeof(EncodeCropInvoker));
    }

    private static async Task<byte[]> ReadProjectBytesAsync(string root, string relative, string expectedSha, int maximum)
    {
        string projectRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(path.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase), "Input path escapes project root.");
        var info = new FileInfo(path);
        Require(info.Exists && info.Length is > 0 && info.Length <= maximum, $"Input size invalid: {relative}.");
        for (FileSystemInfo? current = info; current is not null &&
             !string.Equals(current.FullName, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase); current = current switch
             {
                 FileInfo file => file.Directory,
                 DirectoryInfo directory => directory.Parent,
                 _ => null,
             })
            Require((current.Attributes & FileAttributes.ReparsePoint) == 0, $"Reparse point in input path: {relative}.");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Require(Hash(bytes) == expectedSha, $"Input SHA mismatch: {relative}.");
        return bytes;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record ProbeRow(string CaseId, string ImagePath, string ImageSha256, string Kind,
        string? ExpectedText, OcrRectangle? Target, OcrRectangle Predicted, double Score);
    private sealed record CropEntry(OcrCrop Crop, string Role, string? ExpectedText, double Score, bool StructuralTall, bool TooManyGlyphs);
    private sealed record Outcome(string? Text, string? FailureCode, string? FailureSeverity);

    private sealed class ProviderCaptureCache : IStageCache
    {
        public Dictionary<InferenceProvider, int> Providers { get; } = [];
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            if (value.Length < 6) throw new InvalidDataException("Inference result has no provider field.");
            InferenceProvider provider = (InferenceProvider)value.Span[5];
            Providers[provider] = Providers.GetValueOrDefault(provider) + 1;
            return ValueTask.CompletedTask;
        }
    }
}
