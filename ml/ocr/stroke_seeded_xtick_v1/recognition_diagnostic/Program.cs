// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Inference;
using GraphReader.Ocr;
using OpenCvSharp;

internal static class Program
{
    private const string ExpectedOnnxSha256 = "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84";
    private const string ExpectedModelId = "graph-numeric-component-ensemble-v5";
    private const string ProbeManifestSha256 = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba";
    private const string ProbeCorpusRoot = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3";
    private const string RecipeId = "stroke-seeded-xtick-v1-p2-v5-top-one-recognition-probe-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: StrokeSeededRecognitionDiagnostic <repo-root> <predictions.json> <report.json>");
            return 2;
        }

        try
        {
            string repoRoot = Path.GetFullPath(args[0]);
            string predictionsPath = Path.GetFullPath(args[1]);
            string reportPath = Path.GetFullPath(args[2]);
            string modelPath = Path.Combine(repoRoot,
                "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx");
            byte[] predictionsBytes = await File.ReadAllBytesAsync(predictionsPath);
            byte[] modelBytes = await File.ReadAllBytesAsync(modelPath);
            Require(Hash(modelBytes) == ExpectedOnnxSha256, "V5 ONNX checksum mismatch.");
            using JsonDocument input = JsonDocument.Parse(predictionsBytes);
            JsonElement root = input.RootElement;
            Require(root.GetProperty("recipe_id").GetString() == RecipeId, "Prediction recipe identity mismatch.");
            Require(root.GetProperty("probe_manifest_sha256").GetString() == ProbeManifestSha256,
                "Probe manifest identity mismatch.");
            Require(root.GetProperty("v5_recognizer_onnx_sha256").GetString() == ExpectedOnnxSha256,
                "Prediction record does not pin V5 ONNX.");
            Require(root.GetProperty("training_performed").GetBoolean() == false,
                "Recognition diagnostic input claims training.");
            Require(root.GetProperty("probe_eval_report_sha256").GetString() ==
                    "97932d6f2b12debdd009014d2cbcfd5ff63db8754ccc664079df2551d7242340",
                "Probe evaluation report identity mismatch.");
            byte[] manifestBytes = await ReadProjectBytesAsync(repoRoot, $"{ProbeCorpusRoot}/manifest.json",
                ProbeManifestSha256, 5_000_000);
            byte[] protocolBytes = await ReadProjectBytesAsync(repoRoot, "ml/ocr/component_ensemble_v5/PROTOCOL.json",
                "3c4a169eb1b51e6895152c00e0ef2ff664e8154abc911a21cbcba17e0346adfa", 1_000_000);
            _ = manifestBytes;
            _ = protocolBytes;

            JsonElement[] records = root.GetProperty("records").EnumerateArray().ToArray();
            Require(records.Length == 323, $"Expected 323 admitted procedural crops, found {records.Length}.");
            var capture = new ProviderCaptureCache();
            var providerPolicy = new WindowsExecutionProviderPolicy();
            var discovery = new OrtExecutionProviderDiscovery();
            IReadOnlyList<InferenceProvider> providers = [InferenceProvider.Cpu];
            var registry = new OnnxSessionRegistry(discovery, providerPolicy,
                new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance), CpuThreadConfiguration.Create(1));
            var scheduler = new BoundedInferenceScheduler(capacity: 4, workerCount: 1);
            await using var runtime = new InferenceRuntime(registry, scheduler, capture, allowedProviders: providers);
            var identity = new ModelIdentity(ExpectedModelId, "0.0.21-p1", ExpectedOnnxSha256, modelPath);
            var recognizer = new LocalOnnxComponentTextRecognizer(runtime,
                new LocalOnnxComponentTextRecognizerOptions(identity, "0123456789.-%")
                {
                    StageVersion = "stroke-seeded-v5-diagnostic-v1",
                    AllowedProviders = providers,
                    BypassCache = false,
                });

            var cropEntries = new List<CropEntry>();
            var rowMetadata = new List<RowMetadata>();
            foreach (JsonElement record in records)
            {
                RowMetadata metadata = ReadMetadata(repoRoot, record);
                rowMetadata.Add(metadata);
                if (metadata.Score is null || metadata.Score < 0.25)
                {
                    continue;
                }

                byte[] png = await ReadProjectBytesAsync(repoRoot, $"{ProbeCorpusRoot}/{metadata.ImagePath}", metadata.ImageSha256, 1_000_000);
                using Mat decoded = Cv2.ImDecode(png, ImreadModes.Grayscale);
                Require(!decoded.Empty() && decoded.Width == 256 && decoded.Height == 96 && decoded.Type() == MatType.CV_8UC1,
                    $"Probe image shape/mode mismatch: {metadata.CaseId}.");
                byte[] pixels = new byte[checked(decoded.Width * decoded.Height)];
                System.Runtime.InteropServices.Marshal.Copy(decoded.Data, pixels, 0, pixels.Length);

                OcrImage image = new(decoded.Width, decoded.Height, decoded.Width, pixels,
                    OcrSourceImage.Original, OcrFrameTransform.Identity,
                    CanonicalOriginalWidth: decoded.Width, CanonicalOriginalHeight: decoded.Height);
                OcrRectangle box = metadata.PredictedPixels;
                var region = new OcrDetectedRegion(metadata.CaseId,
                    OcrPolygon.FromRectangle(box), 0, metadata.Score.Value);
                IReadOnlyList<IReadOnlyList<OcrCrop>> batches = OcrCropBatcher.CreateBatches(image, [region],
                    ProductionNumericCropOptions());
                Require(batches.Count == 1 && batches[0].Count == 1, "Predicted region did not yield one production crop.");
                OcrCrop crop = batches[0][0];
                Require(crop.Width == 128 && crop.Height == 32 && crop.SourceImage == OcrSourceImage.Original,
                    "Cropper output differs from numeric recognizer contract.");
                Require(crop.OriginalPolygon.Bounds == box, "Cropper did not retain the original-pixel detector box.");
                cropEntries.Add(new CropEntry(crop, metadata));
            }

            var recognized = new Dictionary<string, RecognitionOutcome>(StringComparer.Ordinal);
            const int batchSize = 16;
            for (int offset = 0; offset < cropEntries.Count; offset += batchSize)
            {
                CropEntry[] batch = cropEntries.Skip(offset).Take(batchSize).ToArray();
                IReadOnlyList<OcrRecognition> outputs = await recognizer.RecognizeBatchAsync(
                    batch.Select(static entry => entry.Crop).ToArray(), CancellationToken.None);
                Require(outputs.Count == batch.Length, "Recognizer batch result count mismatch.");
                for (int index = 0; index < outputs.Count; index++)
                {
                    OcrRecognition output = outputs[index];
                    var best = output.Alternatives.OrderByDescending(static value => value.Confidence).FirstOrDefault();
                    recognized.Add(batch[index].Metadata.CaseId,
                        new RecognitionOutcome(best?.Text, best?.Confidence, output.Failure?.Code, output.Failure?.Severity,
                            output.InferenceMilliseconds));
                }
            }

            var thresholds = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (double threshold in new[] { 0.25, 0.5 })
            {
                int tp = 0, fp = 0, fn = 0, bgFp = 0, selectedDetections = 0;
                int matchedRecognized = 0, exact = 0, wrong = 0, review = 0, errors = 0;
                foreach (RowMetadata row in rowMetadata)
                {
                    bool selected = row.Score is not null && row.Score >= threshold;
                    if (selected) selectedDetections++;
                    bool match = selected && row.TargetPixels is not null && IoU(row.PredictedPixels, row.TargetPixels.Value) >= 0.5;
                    if (match)
                    {
                        tp++;
                        if (!recognized.TryGetValue(row.CaseId, out RecognitionOutcome? outcome) || outcome.Text is null)
                        {
                            review++;
                        }
                        else
                        {
                            matchedRecognized++;
                            if (string.Equals(outcome.Text, row.ExpectedText, StringComparison.Ordinal)) exact++;
                            else wrong++;
                        }

                        if (outcome?.FailureSeverity == "error") errors++;
                    }
                    else if (row.TargetPixels is not null)
                    {
                        fn++;
                        if (selected) fp++;
                    }
                    else if (selected)
                    {
                        fp++;
                        bgFp++;
                    }
                }

                int positives = rowMetadata.Count(static row => row.TargetPixels is not null);
                int count = rowMetadata.Count;
                thresholds[threshold.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)] = new
                {
                    image_count = count, positive_count = positives,
                    background_only_count = count - positives,
                    true_positives = tp, false_positives = fp, false_negatives = fn,
                    selected_detections = selectedDetections,
                    precision = tp / (double)Math.Max(1, tp + fp),
                    recall = tp / (double)Math.Max(1, tp + fn),
                    false_positives_per_image = fp / (double)Math.Max(1, count),
                    background_false_positives = bgFp,
                    recognition_among_matched = new
                    {
                        exact, wrong, review_or_withheld = review, runtime_errors = errors,
                        recognized_exact_accuracy = exact / (double)Math.Max(1, matchedRecognized),
                        matched_count = tp, non_reviewed_recognition_count = matchedRecognized,
                    },
                };
            }

            Require(thresholds.Count == 2 && thresholds.ContainsKey("0.25") && thresholds.ContainsKey("0.5"),
                "Score threshold report labels are not lossless.");

            Require(capture.Providers.Count > 0, "No ONNX provider evidence was captured.");
            string runnerPath = Path.Combine(repoRoot,
                "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/Program.cs");
            string runnerSha256 = Hash(await File.ReadAllBytesAsync(runnerPath));
            int withheldTotal = recognized.Values.Count(static outcome => outcome.Text is null);
            int failuresTotal = recognized.Values.Count(static outcome => outcome.FailureCode is not null);
            int failureErrorsTotal = recognized.Values.Count(static outcome => outcome.FailureSeverity == "error");
            int withTextTotal = recognized.Values.Count(static outcome => outcome.Text is not null);
            var report = new
            {
                schema = "graphreader.stroke-seeded-xtick-v1-p2-v5-recognition-result.v3",
                recipe_id = RecipeId,
                diagnostic_runner_revision = 3,
                diagnostic_runner_sha256 = runnerSha256,
                acceptance_status = "consumed synthetic diagnostic only",
                detector_identity = root.GetProperty("detector_identity").GetString(),
                detector_checkpoint_sha256 = root.GetProperty("detector_checkpoint_sha256").GetString(),
                detector_gpu_runtime = root.GetProperty("runtime"),
                recognizer_identity = ExpectedModelId,
                recognizer_onnx_sha256 = ExpectedOnnxSha256,
                recognizer_provider_counts = capture.Providers.ToDictionary(
                    static pair => pair.Key == InferenceProvider.Cpu ? "CPUExecutionProvider" : pair.Key.ToString(),
                    static pair => pair.Value),
                recognizer_provider_policy = "CPUExecutionProvider only, pinned after DirectML native crash",
                recognizer_crop_contract = new
                {
                    input_canvas = "128x32 grayscale", cropper = "GraphReader.Ocr.OcrCropBatcher",
                    coordinate_space = OcrContract.CoordinateSpace, original_to_image = "identity",
                    resize = "preserve-aspect-ratio-pad", horizontal_padding_pixels = 12,
                    vertical_padding_pixels = 1, vertical_content_padding_ratio = 0.25,
                    padding_value = 1.0f,
                },
                probe_manifest_sha256 = ProbeManifestSha256,
                p2_top_one_report_sha256 = "66ba6032cdd9ef66fb14ac8b810bbb3de5e79422dbf9eaca04b096e8cacab970",
                match_iou_threshold = 0.5, score_threshold_metrics = thresholds,
                recognized_crop_count = cropEntries.Count,
                recognizer_result_counts = new
                {
                    selected_at_primary_threshold = cropEntries.Count,
                    returned_text = withTextTotal, review_or_withheld = withheldTotal,
                    failures = failuresTotal, runtime_errors = failureErrorsTotal,
                },
                inference_milliseconds = recognized.Values.Sum(static value => value.InferenceMilliseconds),
                training_performed = false, dev_sealed_private_inputs_read = false,
                production_approval = false, release_eligible = false,
                case_level_predictions_retained = false,
            };
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

    private static OcrCropBatcherOptions ProductionNumericCropOptions() => new()
    {
        TargetWidth = 128, TargetHeight = 32, BatchSize = 16,
        HorizontalPaddingPixels = 12, VerticalPaddingPixels = 1,
        VerticalContentPaddingRatio = 0.25,
        ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad,
        PaddingValue = 1.0f,
    };

    private static RowMetadata ReadMetadata(string repoRoot, JsonElement record)
    {
        string caseId = record.GetProperty("case_id").GetString() ?? throw new InvalidDataException("Missing case_id.");
        string annotationPath = record.GetProperty("annotation_path").GetString() ?? throw new InvalidDataException("Missing annotation path.");
        string annotationHash = record.GetProperty("annotation_sha256").GetString() ?? throw new InvalidDataException("Missing annotation hash.");
        byte[] annotationBytes = ReadProjectBytesAsync(repoRoot, $"{ProbeCorpusRoot}/{annotationPath}", annotationHash, 65_536).GetAwaiter().GetResult();
        using JsonDocument annotation = JsonDocument.Parse(annotationBytes);
        JsonElement root = annotation.RootElement;
        string? expected = record.GetProperty("expected_text").ValueKind == JsonValueKind.String
            ? record.GetProperty("expected_text").GetString() : null;
        string? annotationText = root.TryGetProperty("expected_tick_label", out JsonElement text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() : null;
        Require(expected == annotationText, $"Expected label differs from hashed annotation: {caseId}.");
        OcrRectangle? target = null;
        JsonElement targets = root.GetProperty("targets");
        if (targets.GetArrayLength() > 0)
        {
            Require(targets.GetArrayLength() == 1, $"Probe target is ambiguous: {caseId}.");
            JsonElement targetBox = targets[0].GetProperty("target_crop_xyxy");
            target = new OcrRectangle(targetBox[0].GetDouble(), targetBox[1].GetDouble(),
                targetBox[2].GetDouble() - targetBox[0].GetDouble(), targetBox[3].GetDouble() - targetBox[1].GetDouble());
        }

        string? imagePath = record.GetProperty("image_path").GetString();
        string? imageHash = record.GetProperty("image_sha256").GetString();
        double? score = record.GetProperty("top_one_score").ValueKind == JsonValueKind.Number
            ? record.GetProperty("top_one_score").GetDouble() : null;
        OcrRectangle box = default;
        if (score is not null)
        {
            JsonElement coords = record.GetProperty("top_one_xyxy_normalized");
            Require(coords.GetArrayLength() == 4, $"Detector box shape invalid: {caseId}.");
            double x0 = coords[0].GetDouble() * 256, y0 = coords[1].GetDouble() * 96;
            double x1 = coords[2].GetDouble() * 256, y1 = coords[3].GetDouble() * 96;
            box = new OcrRectangle(x0, y0, x1 - x0, y1 - y0);
            Require(box.IsValid && x0 >= 0 && y0 >= 0 && x1 <= 256 && y1 <= 96,
                $"Detector box is invalid/out of image coordinates: {caseId}.");
        }
        return new RowMetadata(caseId, record.GetProperty("source_id").GetString() ?? "",
            record.GetProperty("kind").GetString() ?? "", imagePath ?? "", imageHash ?? "",
            expected, target, score, box);
    }

    private static async Task<byte[]> ReadProjectBytesAsync(string repoRoot, string relativePath, string expectedHash, int maximumBytes)
    {
        string root = Path.GetFullPath(repoRoot) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Require(full.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Input path escaped the project root.");
        var info = new FileInfo(full);
        Require(info.Exists && info.Length is > 0 && info.Length <= maximumBytes, $"Input file size invalid: {relativePath}.");
        for (FileSystemInfo? current = info; current is not null && !string.Equals(current.FullName, Path.GetFullPath(repoRoot), StringComparison.OrdinalIgnoreCase); current = current switch
        {
            FileInfo file => file.Directory,
            DirectoryInfo directory => directory.Parent,
            _ => null,
        })
        {
            Require((current.Attributes & FileAttributes.ReparsePoint) == 0, $"Input path contains a reparse point: {relativePath}.");
        }
        byte[] bytes = await File.ReadAllBytesAsync(full);
        Require(Hash(bytes) == expectedHash, $"Input file hash mismatch: {relativePath}.");
        return bytes;
    }

    private static double IoU(OcrRectangle predicted, OcrRectangle target)
    {
        double intersection = Math.Max(0, Math.Min(predicted.Right, target.Right) - Math.Max(predicted.Left, target.Left)) *
                             Math.Max(0, Math.Min(predicted.Bottom, target.Bottom) - Math.Max(predicted.Top, target.Top));
        double union = predicted.Width * predicted.Height + target.Width * target.Height - intersection;
        return union > 0 ? intersection / union : 0;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException(message);
    }

    private sealed record RowMetadata(string CaseId, string SourceId, string Kind, string ImagePath,
        string ImageSha256, string? ExpectedText, OcrRectangle? TargetPixels, double? Score, OcrRectangle PredictedPixels);
    private sealed record CropEntry(OcrCrop Crop, RowMetadata Metadata);
    private sealed record RecognitionOutcome(string? Text, double? Confidence, string? FailureCode,
        string? FailureSeverity, double InferenceMilliseconds);

    private sealed class ProviderCaptureCache : IStageCache
    {
        public Dictionary<InferenceProvider, int> Providers { get; } = [];
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            if (value.Length < 6) throw new InvalidDataException("Inference cache record lacks provider evidence.");
            InferenceProvider provider = (InferenceProvider)value.Span[5];
            Providers[provider] = Providers.GetValueOrDefault(provider) + 1;
            return ValueTask.CompletedTask;
        }
    }
}
