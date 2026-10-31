// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Inference;
using GraphReader.Ocr;
using OpenCvSharp;

internal static class Program
{
    private const string P2PredictionsSha256 = "235d39476cf3ef498d0c026f05e4c9eb626fe43c853d0af49ab6d0bef2d54097";
    private const string P2CheckpointSha256 = "9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351";
    private const string ProbeManifestSha256 = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba";
    private const string V5OnnxSha256 = "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84";
    private const string CorpusRoot = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3";
    private const string RecipeId = "stroke-seeded-xtick-v1-p2-v5-ground-truth-box-comparison-v2";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: GroundTruthComparison <repo-root> <pinned-predictions.json> <report.json>");
            return 2;
        }

        try
        {
            string repoRoot = Path.GetFullPath(args[0]);
            byte[] predictionBytes = await File.ReadAllBytesAsync(Path.GetFullPath(args[1]));
            Require(Hash(predictionBytes) == P2PredictionsSha256, "P2 predictions JSON hash mismatch.");
            byte[] manifestBytes = await ReadBytesAsync(repoRoot, $"{CorpusRoot}/manifest.json", ProbeManifestSha256, 5_000_000);
            byte[] protocolBytes = await ReadBytesAsync(repoRoot, "ml/ocr/component_ensemble_v5/PROTOCOL.json",
                "3c4a169eb1b51e6895152c00e0ef2ff664e8154abc911a21cbcba17e0346adfa", 1_000_000);
            using JsonDocument predictionsDoc = JsonDocument.Parse(predictionBytes);
            JsonElement predictionRoot = predictionsDoc.RootElement;
            Require(predictionRoot.GetProperty("probe_manifest_sha256").GetString() == ProbeManifestSha256,
                "Prediction file probe identity mismatch.");
            Require(predictionRoot.GetProperty("detector_checkpoint_sha256").GetString() == P2CheckpointSha256,
                "Prediction file detector identity mismatch.");
            Require(predictionRoot.GetProperty("v5_recognizer_onnx_sha256").GetString() == V5OnnxSha256,
                "Prediction file recognizer identity mismatch.");
            Require(predictionRoot.GetProperty("training_performed").GetBoolean() == false,
                "Prediction file must be inference-only.");

            using JsonDocument manifestDoc = JsonDocument.Parse(manifestBytes);
            var sourceRows = manifestDoc.RootElement.GetProperty("records").EnumerateArray()
                .ToDictionary(static row => row.GetProperty("case_id").GetString()!, StringComparer.Ordinal);
            var sampleRows = new List<SampleRow>();
            foreach (JsonElement prediction in predictionRoot.GetProperty("records").EnumerateArray())
            {
                if (prediction.GetProperty("kind").GetString() != "positive") continue;
                string caseId = prediction.GetProperty("case_id").GetString()!;
                Require(sourceRows.TryGetValue(caseId, out JsonElement source), $"Probe manifest lacks case {caseId}.");
                string annotationPath = source.GetProperty("annotation_path").GetString()!;
                string imagePath = source.GetProperty("image_path").GetString()!;
                byte[] annotationBytes = await ReadBytesAsync(repoRoot, $"{CorpusRoot}/{annotationPath}",
                    source.GetProperty("annotation_sha256").GetString()!, 65_536);
                using JsonDocument annotationDoc = JsonDocument.Parse(annotationBytes);
                JsonElement annotation = annotationDoc.RootElement;
                string expectedText = annotation.GetProperty("expected_tick_label").GetString()
                    ?? throw new InvalidDataException("Positive annotation has no label.");
                Require(prediction.GetProperty("expected_text").GetString() == expectedText,
                    $"Prediction label differs from manifest annotation for {caseId}.");
                JsonElement target = annotation.GetProperty("targets");
                Require(target.GetArrayLength() == 1, $"Positive annotation is not a single box: {caseId}.");
                JsonElement targetBox = target[0].GetProperty("target_crop_xyxy");
                var groundTruth = new OcrRectangle(targetBox[0].GetDouble(), targetBox[1].GetDouble(),
                    targetBox[2].GetDouble() - targetBox[0].GetDouble(),
                    targetBox[3].GetDouble() - targetBox[1].GetDouble());
                JsonElement normalized = prediction.GetProperty("top_one_xyxy_normalized");
                Require(normalized.GetArrayLength() == 4, $"Missing top-one predicted box: {caseId}.");
                double px0 = normalized[0].GetDouble() * 256, py0 = normalized[1].GetDouble() * 96;
                double px1 = normalized[2].GetDouble() * 256, py1 = normalized[3].GetDouble() * 96;
                var predicted = new OcrRectangle(px0, py0, px1 - px0, py1 - py0);
                Require(groundTruth.IsValid && predicted.IsValid && px0 >= 0 && py0 >= 0 && px1 <= 256 && py1 <= 96,
                    $"Invalid target/predicted box for {caseId}.");
                double score = prediction.GetProperty("top_one_score").GetDouble();
                Require(score >= 0.25, $"Positive crop lacks a primary-threshold detection: {caseId}.");
                sampleRows.Add(new SampleRow(caseId, prediction.GetProperty("source_id").GetString()!,
                    imagePath, source.GetProperty("image_sha256").GetString()!, expectedText,
                    groundTruth, predicted, score, IoU(predicted, groundTruth)));
            }
            Require(sampleRows.Count == 165, $"Expected 165 positive probe rows, found {sampleRows.Count}.");

            byte[] modelBytes = await ReadBytesAsync(repoRoot,
                "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx",
                V5OnnxSha256, 10_000_000);
            string modelPath = Path.Combine(repoRoot,
                "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx");
            _ = modelBytes;
            var cache = new ProviderCaptureCache();
            var policy = new WindowsExecutionProviderPolicy();
            var discovery = new OrtExecutionProviderDiscovery();
            IReadOnlyList<InferenceProvider> providers = [InferenceProvider.Cpu];
            var registry = new OnnxSessionRegistry(discovery, policy,
                new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance), CpuThreadConfiguration.Create(1));
            var scheduler = new BoundedInferenceScheduler(capacity: 4, workerCount: 1);
            await using var runtime = new InferenceRuntime(registry, scheduler, cache, allowedProviders: providers);
            var identity = new ModelIdentity("graph-numeric-component-ensemble-v5", "0.0.21-p1", V5OnnxSha256, modelPath);
            var recognizer = new LocalOnnxComponentTextRecognizer(runtime,
                new LocalOnnxComponentTextRecognizerOptions(identity, "0123456789.-%")
                {
                    StageVersion = "stroke-seeded-v5-ground-truth-diagnostic-v1",
                    AllowedProviders = providers,
                    BypassCache = false,
                });

            var comparisons = new List<CropPair>(sampleRows.Count);
            foreach (SampleRow row in sampleRows)
            {
                byte[] png = await ReadBytesAsync(repoRoot, $"{CorpusRoot}/{row.ImagePath}", row.ImageSha256, 1_000_000);
                using Mat decoded = Cv2.ImDecode(png, ImreadModes.Grayscale);
                Require(!decoded.Empty() && decoded.Width == 256 && decoded.Height == 96 && decoded.Type() == MatType.CV_8UC1,
                    $"Probe image dimensions/mode mismatch: {row.CaseId}.");
                byte[] pixels = new byte[checked(decoded.Width * decoded.Height)];
                System.Runtime.InteropServices.Marshal.Copy(decoded.Data, pixels, 0, pixels.Length);
                var image = new OcrImage(decoded.Width, decoded.Height, decoded.Width, pixels,
                    OcrSourceImage.Original, OcrFrameTransform.Identity,
                    CanonicalOriginalWidth: decoded.Width, CanonicalOriginalHeight: decoded.Height);
                OcrCrop gtCrop = CreateCrop(image, row.CaseId + ":gt", row.GroundTruth);
                OcrCrop predictedCrop = CreateCrop(image, row.CaseId + ":pred", row.Predicted);
                comparisons.Add(new CropPair(row, gtCrop, predictedCrop));
            }

            var outcomes = new Dictionary<string, Outcome>(StringComparer.Ordinal);
            var allCrops = comparisons.SelectMany(static pair => new[] { pair.GroundTruthCrop, pair.PredictedCrop }).ToArray();
            for (int offset = 0; offset < allCrops.Length; offset += 16)
            {
                OcrCrop[] batch = allCrops.Skip(offset).Take(16).ToArray();
                IReadOnlyList<OcrRecognition> recognized = await recognizer.RecognizeBatchAsync(batch, CancellationToken.None);
                Require(recognized.Count == batch.Length, "Recognizer result count differs from crop batch.");
                for (int index = 0; index < recognized.Count; index++)
                {
                    OcrRecognition result = recognized[index];
                    OcrRecognitionAlternative? best = result.Alternatives.OrderByDescending(static item => item.Confidence).FirstOrDefault();
                    outcomes.Add(result.RegionId, new Outcome(best?.Text, best?.Confidence,
                        result.Failure?.Code, result.Failure?.Severity, result.InferenceMilliseconds));
                }
            }

            object Summarize(IEnumerable<(SampleRow Row, bool GroundTruth)> rows)
            {
                int count = 0, exact = 0, wrong = 0, emptyNoAlternative = 0;
                var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach ((SampleRow row, bool groundTruth) in rows)
                {
                    count++;
                    Outcome outcome = outcomes[groundTruth ? row.CaseId + ":gt" : row.CaseId + ":pred"];
                    if (outcome.FailureCode is not null)
                    {
                        string key = $"{outcome.FailureSeverity}:{outcome.FailureCode}";
                        failures[key] = failures.GetValueOrDefault(key) + 1;
                    }
                    else if (outcome.Text is not null)
                    {
                        if (string.Equals(outcome.Text, row.ExpectedText, StringComparison.Ordinal)) exact++;
                        else wrong++;
                    }
                    else
                    {
                        // Per-crop component count and Decode rejection reason are not part of the runtime result contract.
                        emptyNoAlternative++;
                    }
                }
                return new
                {
                    sample_count = count, exact, wrong,
                    empty_without_alternative = emptyNoAlternative,
                    explicit_failures_by_severity_and_code = failures,
                    accounted = exact + wrong + emptyNoAlternative + failures.Values.Sum(),
                    exact_accuracy_all = exact / (double)Math.Max(1, count),
                    exact_accuracy_with_text = exact / (double)Math.Max(1, exact + wrong),
                };
            }

            var thresholds = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (double threshold in new[] { 0.25, 0.5 })
            {
                SampleRow[] subset = sampleRows.Where(row => row.Score >= threshold).ToArray();
                thresholds[threshold.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)] = new
                {
                    selected_positive_rows = subset.Length,
                    ground_truth_boxes = Summarize(subset.Select(static row => (row, true))),
                    predicted_boxes = Summarize(subset.Select(static row => (row, false))),
                    predicted_boxes_iou_at_least_0_5 = Summarize(subset.Where(static row => row.PredictedIoU >= 0.5)
                        .Select(static row => (row, false))),
                    predicted_boxes_iou_below_0_5 = Summarize(subset.Where(static row => row.PredictedIoU < 0.5)
                        .Select(static row => (row, false))),
                };
            }

            Require(cache.Providers.Count == 1 && cache.Providers.ContainsKey(InferenceProvider.Cpu),
                "Recognition provider differed from the pinned CPU policy.");
            string sourcePath = Path.Combine(repoRoot,
                "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ground_truth_comparison/Program.cs");
            var report = new
            {
                schema = "graphreader.stroke-seeded-xtick-v1-p2-v5-ground-truth-comparison.v2",
                recipe_id = RecipeId,
                acceptance_status = "consumed synthetic diagnostic only",
                runner_sha256 = Hash(await File.ReadAllBytesAsync(sourcePath)),
                prediction_file_sha256 = Hash(predictionBytes),
                detector_checkpoint_sha256 = P2CheckpointSha256,
                recognizer_onnx_sha256 = V5OnnxSha256,
                probe_manifest_sha256 = ProbeManifestSha256,
                v5_protocol_sha256 = Hash(protocolBytes),
                match_iou_threshold = 0.5,
                primary_score_threshold = 0.25,
                per_score_threshold = thresholds,
                recognizer_provider = "CPUExecutionProvider",
                recognizer_provider_batches = cache.Providers[InferenceProvider.Cpu],
                crop_contract = new { cropper = "GraphReader.Ocr.OcrCropBatcher", coordinate_space = "original_pixels",
                    transform = "identity for each 256x96 probe raster", width = 128, height = 32,
                    resize_mode = "preserve-aspect-ratio-pad", horizontal_padding = 12,
                    vertical_padding = 1, vertical_content_padding_ratio = 0.25, padding_value = 1.0f },
                outcome_attribution = "Failure code/severity and returned text are explicit. Empty without alternatives and without failure is counted as empty. Per-crop component count is not exposed; timing is divided from batch inference, so it cannot attribute an empty to no per-crop glyphs versus reject class, confidence threshold, or numeric grammar. All 21 submitted recognizer batches executed via the captured provider.",
                inference_milliseconds = outcomes.Values.Sum(static item => item.InferenceMilliseconds),
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

    private static OcrCrop CreateCrop(OcrImage image, string id, OcrRectangle box)
    {
        var region = new OcrDetectedRegion(id, OcrPolygon.FromRectangle(box), 0, 1.0);
        IReadOnlyList<IReadOnlyList<OcrCrop>> batches = OcrCropBatcher.CreateBatches(image, [region], new OcrCropBatcherOptions
        {
            TargetWidth = 128, TargetHeight = 32, BatchSize = 16,
            HorizontalPaddingPixels = 12, VerticalPaddingPixels = 1,
            VerticalContentPaddingRatio = 0.25,
            ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad,
            PaddingValue = 1.0f,
        });
        Require(batches.Count == 1 && batches[0].Count == 1, "One box did not produce exactly one crop.");
        OcrCrop crop = batches[0][0];
        Require(crop.Width == 128 && crop.Height == 32 && crop.SourceImage == OcrSourceImage.Original,
            "OcrCropBatcher output differs from V5 contract.");
        Require(crop.OriginalPolygon.Bounds == box, "Cropper changed its original-pixel box mapping.");
        return crop;
    }

    private static async Task<byte[]> ReadBytesAsync(string repoRoot, string relative, string expectedHash, int maximum)
    {
        string root = Path.GetFullPath(repoRoot) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Input path escaped repository root.");
        var info = new FileInfo(path);
        Require(info.Exists && info.Length is > 0 && info.Length <= maximum, $"Input size invalid: {relative}.");
        for (FileSystemInfo? current = info; current is not null &&
             !string.Equals(current.FullName, Path.GetFullPath(repoRoot), StringComparison.OrdinalIgnoreCase); current = current switch
             {
                 FileInfo file => file.Directory,
                 DirectoryInfo directory => directory.Parent,
                 _ => null,
             })
        {
            Require((current.Attributes & FileAttributes.ReparsePoint) == 0, $"Reparse point in input path: {relative}.");
        }
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Require(Hash(bytes) == expectedHash, $"Input hash mismatch: {relative}.");
        return bytes;
    }

    private static double IoU(OcrRectangle a, OcrRectangle b)
    {
        double intersection = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
                             Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        double union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union > 0 ? intersection / union : 0;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record SampleRow(string CaseId, string SourceId, string ImagePath, string ImageSha256,
        string ExpectedText, OcrRectangle GroundTruth, OcrRectangle Predicted, double Score, double PredictedIoU);
    private sealed record CropPair(SampleRow Row, OcrCrop GroundTruthCrop, OcrCrop PredictedCrop);
    private sealed record Outcome(string? Text, double? Confidence, string? FailureCode, string? FailureSeverity,
        double InferenceMilliseconds);

    private sealed class ProviderCaptureCache : IStageCache
    {
        public Dictionary<InferenceProvider, int> Providers { get; } = [];
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            if (value.Length < 6) throw new InvalidDataException("Inference cache entry lacks provider field.");
            InferenceProvider provider = (InferenceProvider)value.Span[5];
            Providers[provider] = Providers.GetValueOrDefault(provider) + 1;
            return ValueTask.CompletedTask;
        }
    }
}
