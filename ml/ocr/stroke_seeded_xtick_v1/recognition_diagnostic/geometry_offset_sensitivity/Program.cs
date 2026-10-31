// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Inference;
using GraphReader.Ocr;
using OpenCvSharp;

internal static class Program
{
    private const string PredictionSha = "235d39476cf3ef498d0c026f05e4c9eb626fe43c853d0af49ab6d0bef2d54097";
    private const string ManifestSha = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba";
    private const string BaselineSha = "8bb9ee29ab4ac0ee0f09572fabd367624d82cc11d5a33d42a044752d56bd018e";
    private const string V2GeometryReportSha = "330df878f43020e10dd7332a6545b92fb36590f124d00efc23a2ba4155262df4";
    private const string ModelSha = "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84";
    private const string ProtocolSha = "3c4a169eb1b51e6895152c00e0ef2ff664e8154abc911a21cbcba17e0346adfa";
    private const string Corpus = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3";
    private const string ModelPath = "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private delegate object EncodeCropInvoker(ReadOnlySpan<float> pixels, LocalOnnxComponentTextRecognizerOptions options);
    private static readonly double[] Offsets = [0, -2, -1, 1, 2];

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 5)
        {
            Console.Error.WriteLine("usage: GeometryOffsetSensitivity <repo-root> <predictions.json> <baseline-report.json> <mask-dir> <report.json>");
            return 2;
        }

        try
        {
            string root = Path.GetFullPath(args[0]);
            byte[] predictionBytes = await File.ReadAllBytesAsync(Path.GetFullPath(args[1]));
            byte[] baselineBytes = await File.ReadAllBytesAsync(Path.GetFullPath(args[2]));
            byte[] v2Bytes = await ReadProjectAsync(root,
                "artifacts/stroke-seeded-xtick-v1-p2-v5-geometry-only-erase-v2/report.json", V2GeometryReportSha, 100_000);
            Require(Hash(predictionBytes) == PredictionSha && Hash(baselineBytes) == BaselineSha,
                "Pinned predictions or baseline report SHA mismatch.");
            byte[] manifestBytes = await ReadProjectAsync(root, $"{Corpus}/manifest.json", ManifestSha, 5_000_000);
            byte[] modelBytes = await ReadProjectAsync(root, ModelPath, ModelSha, 10_000_000);
            byte[] protocolBytes = await ReadProjectAsync(root, "ml/ocr/component_ensemble_v5/PROTOCOL.json", ProtocolSha, 1_000_000);
            _ = modelBytes; _ = protocolBytes;

            using JsonDocument predictionDoc = JsonDocument.Parse(predictionBytes);
            JsonElement predictionRoot = predictionDoc.RootElement;
            using JsonDocument baselineDoc = JsonDocument.Parse(baselineBytes);
            using JsonDocument manifestDoc = JsonDocument.Parse(manifestBytes);
            using JsonDocument v2Doc = JsonDocument.Parse(v2Bytes);
            JsonElement baselineRoot = baselineDoc.RootElement;
            Require(predictionRoot.GetProperty("probe_manifest_sha256").GetString() == ManifestSha &&
                    predictionRoot.GetProperty("v5_recognizer_onnx_sha256").GetString() == ModelSha &&
                    !predictionRoot.GetProperty("training_performed").GetBoolean(),
                "Prediction input identity is not the pinned inference-only probe.");
            var manifestRows = manifestDoc.RootElement.GetProperty("records").EnumerateArray()
                .ToDictionary(static item => item.GetProperty("case_id").GetString()!, StringComparer.Ordinal);
            var parsedRows = new List<ProbeRow>();
            foreach (JsonElement prediction in predictionRoot.GetProperty("records").EnumerateArray())
            {
                string caseId = prediction.GetProperty("case_id").GetString()!;
                Require(manifestRows.TryGetValue(caseId, out JsonElement manifestRow), $"Probe manifest lacks {caseId}.");
                byte[] annotationBytes = await ReadProjectAsync(root,
                    $"{Corpus}/{manifestRow.GetProperty("annotation_path").GetString()}",
                    manifestRow.GetProperty("annotation_sha256").GetString()!, 65_536);
                using JsonDocument annotationDoc = JsonDocument.Parse(annotationBytes);
                JsonElement annotation = annotationDoc.RootElement;
                JsonElement targetArray = annotation.GetProperty("targets");
                OcrRectangle? target = null;
                if (targetArray.GetArrayLength() == 1)
                {
                    JsonElement xyxy = targetArray[0].GetProperty("target_crop_xyxy");
                    target = new OcrRectangle(xyxy[0].GetDouble(), xyxy[1].GetDouble(),
                        xyxy[2].GetDouble() - xyxy[0].GetDouble(), xyxy[3].GetDouble() - xyxy[1].GetDouble());
                }
                else Require(targetArray.GetArrayLength() == 0, $"Ambiguous targets in {caseId}.");
                string? expected = annotation.GetProperty("expected_tick_label").ValueKind == JsonValueKind.String
                    ? annotation.GetProperty("expected_tick_label").GetString() : null;
                Require(expected == (prediction.GetProperty("expected_text").ValueKind == JsonValueKind.String
                    ? prediction.GetProperty("expected_text").GetString() : null), $"Target label mismatch: {caseId}.");
                JsonElement xyxyPred = prediction.GetProperty("top_one_xyxy_normalized");
                double x0 = xyxyPred[0].GetDouble() * 256, y0 = xyxyPred[1].GetDouble() * 96;
                double x1 = xyxyPred[2].GetDouble() * 256, y1 = xyxyPred[3].GetDouble() * 96;
                var predicted = new OcrRectangle(x0, y0, x1 - x0, y1 - y0);
                Require(predicted.IsValid && predicted.Left >= 0 && predicted.Top >= 0 && predicted.Right <= 256 && predicted.Bottom <= 96,
                    $"Invalid predicted box: {caseId}.");
                if (target is not null) Require(target.Value.IsValid, $"Invalid target box: {caseId}.");
                byte[] png = await ReadProjectAsync(root, $"{Corpus}/{manifestRow.GetProperty("image_path").GetString()}",
                    manifestRow.GetProperty("image_sha256").GetString()!, 1_000_000);
                using Mat decoded = Cv2.ImDecode(png, ImreadModes.Grayscale);
                Require(!decoded.Empty() && decoded.Width == 256 && decoded.Height == 96 && decoded.Type() == MatType.CV_8UC1,
                    $"Probe raster contract mismatch: {caseId}.");
                byte[] pixels = new byte[256 * 96];
                System.Runtime.InteropServices.Marshal.Copy(decoded.Data, pixels, 0, pixels.Length);
                (double[] p0, double[] p1, int fill) = MapSegment(annotation);
                parsedRows.Add(new ProbeRow(caseId, manifestRow.GetProperty("image_path").GetString()!,
                    manifestRow.GetProperty("image_sha256").GetString()!, manifestRow.GetProperty("annotation_sha256").GetString()!,
                    prediction.GetProperty("kind").GetString()!, expected, target, predicted,
                    prediction.GetProperty("top_one_score").GetDouble(), pixels, p0, p1, fill));
            }
            Require(parsedRows.Count == 323 && parsedRows.Count(static row => row.Target is not null) == 165 &&
                parsedRows.Count(static row => row.Target is null) == 158, "Probe counts changed.");

            MethodInfo filter = PrivateMethod("FilterForeground", 3), intervals = PrivateMethod("ActiveIntervals", 3),
                glyph = PrivateMethod("EncodeGlyph", 5), encodeMethod = PrivateMethod("EncodeCrop", 2);
            EncodeCropInvoker encodeCrop = CreateEncoder(encodeMethod);
            var options = new LocalOnnxComponentTextRecognizerOptions(
                new ModelIdentity("graph-numeric-component-ensemble-v5", "0.0.21-p1", ModelSha, Path.Combine(root, ModelPath)),
                "0123456789.-%");
            var variants = new Dictionary<string, List<CropEntry>>(StringComparer.Ordinal);
            var baselineEntries = new List<CropEntry>(488);
            foreach (ProbeRow row in parsedRows)
            {
                var originalImage = new OcrImage(256, 96, 256, row.Pixels.ToArray(), OcrSourceImage.Original, OcrFrameTransform.Identity,
                    CanonicalOriginalWidth: 256, CanonicalOriginalHeight: 96);
                bool[] noErasure = new bool[row.Pixels.Length];
                AddRoleCrops(baselineEntries, row, originalImage, noErasure, 0, options, encodeCrop, filter, intervals, glyph);
            }
            Require(baselineEntries.Count == 488, "Baseline crop count changed.");
            variants.Add("original", baselineEntries);
            foreach (double offset in Offsets)
            {
                string key = Key(offset);
                var entries = new List<CropEntry>(488);
                foreach (ProbeRow row in parsedRows)
                {
                    (byte[] transformed, bool[] eraseMask, int erasedPixelCount) = EraseShiftedCenterline(row, offset);
                    var image = new OcrImage(256, 96, 256, transformed, OcrSourceImage.Original, OcrFrameTransform.Identity,
                        CanonicalOriginalWidth: 256, CanonicalOriginalHeight: 96);
                    AddRoleCrops(entries, row, image, eraseMask, erasedPixelCount, options, encodeCrop, filter, intervals, glyph);
                }
                Require(entries.Count == 488, $"Crop count differs at {key}.");
                variants.Add(key, entries);
            }

            // All variants are created using geometry-only masks before any glyph sidecar is read.
            var providerCapture = new ProviderCaptureCache();
            IReadOnlyList<InferenceProvider> providers = [InferenceProvider.Cpu];
            var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(),
                new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance), CpuThreadConfiguration.Create(1));
            var scheduler = new BoundedInferenceScheduler(capacity: 4, workerCount: 1);
            await using var runtime = new InferenceRuntime(registry, scheduler, providerCapture, allowedProviders: providers);
            var recognizer = new LocalOnnxComponentTextRecognizer(runtime,
                options with { StageVersion = "stroke-geometry-offset-sensitivity-v1", AllowedProviders = providers, BypassCache = true });
            var outputs = new Dictionary<string, Dictionary<string, Outcome>>(StringComparer.Ordinal);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            foreach (string key in new[] { "original", "offset0", "offset-2", "offset-1", "offset+1", "offset+2" })
            {
                var map = new Dictionary<string, Outcome>(StringComparer.Ordinal);
                CropEntry[] entries = variants[key].ToArray();
                for (int offset = 0; offset < entries.Length; offset += 16)
                {
                    OcrCrop[] batch = entries.Skip(offset).Take(16).Select(static item => item.Crop).ToArray();
                    IReadOnlyList<OcrRecognition> recognized = await recognizer.RecognizeBatchAsync(batch, CancellationToken.None);
                    Require(recognized.Count == batch.Length, $"Recognizer output count mismatch: {key}.");
                    foreach (OcrRecognition result in recognized)
                    {
                        OcrRecognitionAlternative? best = result.Alternatives.OrderByDescending(static item => item.Confidence).FirstOrDefault();
                        map.Add(result.RegionId, new Outcome(best?.Text, result.Failure?.Code, result.Failure?.Severity));
                    }
                }
                outputs.Add(key, map);
            }
            timer.Stop();

            // Actual renderer glyph masks are read only after all six recognizer passes are complete.
            string maskRoot = Path.GetFullPath(args[3]);
            byte[] maskIndexBytes = await File.ReadAllBytesAsync(Path.Combine(maskRoot, "mask-index.json"));
            using JsonDocument maskIndexDoc = JsonDocument.Parse(maskIndexBytes);
            JsonElement maskIndex = maskIndexDoc.RootElement;
            Require(maskIndex.GetProperty("probe_manifest_sha256").GetString() == ManifestSha &&
                    maskIndex.GetProperty("record_count").GetInt32() == 165 &&
                    maskIndex.GetProperty("verified_probe_image_count").GetInt32() == 359,
                "Mask index identity/count mismatch.");
            Dictionary<string, JsonElement> maskRows = maskIndex.GetProperty("records").EnumerateArray()
                .ToDictionary(static item => item.GetProperty("case_id").GetString()!, StringComparer.Ordinal);
            var overlapMetrics = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (double offset in Offsets)
            {
                string variant = Key(offset);
                int overlapCases = 0, overlapPixels = 0, totalGlyphPixels = 0, totalErasePixels = 0;
                double maxBoxDelta = 0;
                foreach (ProbeRow row in parsedRows.Where(static item => item.Target is not null))
                {
                    Require(maskRows.TryGetValue(row.CaseId, out JsonElement maskRow), $"Missing glyph mask: {row.CaseId}.");
                    Require(maskRow.GetProperty("annotation_sha256").GetString() == row.AnnotationSha,
                        $"Glyph mask annotation binding mismatch: {row.CaseId}.");
                    byte[] maskBytes = await ReadMaskAsync(maskRoot, maskRow.GetProperty("path").GetString()!, maskRow.GetProperty("sha256").GetString()!);
                    using Mat maskImage = Cv2.ImDecode(maskBytes, ImreadModes.Grayscale);
                    Require(!maskImage.Empty() && maskImage.Width == 256 && maskImage.Height == 96 && maskImage.Type() == MatType.CV_8UC1,
                        $"Glyph mask contract mismatch: {row.CaseId}.");
                    byte[] maskPixels = new byte[256 * 96];
                    System.Runtime.InteropServices.Marshal.Copy(maskImage.Data, maskPixels, 0, maskPixels.Length);
                    bool[] eraseMask = variants[variant].First(entry => entry.Probe.CaseId == row.CaseId && entry.Role == "gt").EraseMask;
                    int glyphCount = 0, hits = 0, minX = 256, minY = 96, maxX = -1, maxY = -1;
                    for (int i = 0; i < maskPixels.Length; i++)
                    {
                        if (maskPixels[i] == 0) continue;
                        glyphCount++;
                        int x = i % 256, y = i / 256;
                        minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                        if (eraseMask[i]) hits++;
                    }
                    Require(glyphCount == maskRow.GetProperty("crop_mask_nonzero_pixels").GetInt32(), $"Mask pixel count mismatch {row.CaseId}.");
                    double[] edges = [Math.Abs(minX - row.Target!.Value.Left), Math.Abs(minY - row.Target.Value.Top),
                        Math.Abs(maxX + 1 - row.Target.Value.Right), Math.Abs(maxY + 1 - row.Target.Value.Bottom)];
                    maxBoxDelta = Math.Max(maxBoxDelta, edges.Max());
                    bool overlaps = hits > 0;
                    if (overlaps) overlapCases++;
                    overlapPixels += hits; totalGlyphPixels += glyphCount;
                    totalErasePixels += variants[variant].First(entry => entry.Probe.CaseId == row.CaseId && entry.Role == "gt").ErasedPixelCount;
                }
                Require(maxBoxDelta <= 1.0, $"Crop-mask bounds diverge by >1px at {variant}.");
                overlapMetrics.Add(variant, new
                {
                    positive_cases = 165,
                    cases_with_actual_glyph_pixel_overlap = overlapCases,
                    cases_without_overlap = 165 - overlapCases,
                    actual_glyph_pixels_erased = overlapPixels,
                    total_nonzero_actual_glyph_mask_pixels = totalGlyphPixels,
                    overlap_fraction = overlapPixels / (double)Math.Max(1, totalGlyphPixels),
                    centerline_pixels_erased_across_positive_rows = totalErasePixels,
                    crop_mask_target_box_max_edge_delta_pixels = maxBoxDelta,
                });
            }

            bool BaselineParity(string role, string reportKey)
            {
                JsonElement expected = baselineRoot.GetProperty("per_score_threshold").GetProperty("0.25").GetProperty(reportKey);
                OutcomeSummary measured = SummarizeCounts(variants["original"].Where(item => item.Role == role).ToArray(), outputs["original"]);
                return expected.GetProperty("sample_count").GetInt32() == measured.Count &&
                    expected.GetProperty("exact").GetInt32() == measured.Exact && expected.GetProperty("wrong").GetInt32() == measured.Wrong &&
                    expected.GetProperty("empty_without_alternative").GetInt32() == measured.Empty &&
                    expected.GetProperty("explicit_failures_by_severity_and_code").EnumerateObject().Sum(static p => p.Value.GetInt32()) == measured.Failures;
            }
            var parity = new { ground_truth_positive_boxes = BaselineParity("gt", "ground_truth_boxes"),
                predicted_positive_boxes = BaselineParity("pred", "predicted_boxes") };
            Require(parity.ground_truth_positive_boxes && parity.predicted_positive_boxes, "Original baseline parity failed.");

            var variantMetrics = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string variant in new[] { "offset0", "offset-2", "offset-1", "offset+1", "offset+2" })
            {
                var roles = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string role in new[] { "gt", "pred", "background_forced" })
                {
                    CropEntry[] selected = variants[variant].Where(item => item.Role == role).ToArray();
                    OutcomeSummary before = SummarizeCounts(variants["original"].Where(item => item.Role == role).ToArray(), outputs["original"]);
                    OutcomeSummary after = SummarizeCounts(selected, outputs[variant]);
                    int changed = 0, gained = 0, lost = 0, newlyWrong = 0, exactToWrong = 0, emptyToWrong = 0,
                        newBackgroundText = 0, changedWrongText = 0;
                    for (int i = 0; i < selected.Length; i++)
                    {
                        CropEntry row = selected[i];
                        Outcome old = outputs["original"][row.Crop.RegionId];
                        Outcome current = outputs[variant][row.Crop.RegionId];
                        if (!SameOutcome(old, current)) changed++;
                        bool oldExact = IsExact(old, row.Expected), newExact = IsExact(current, row.Expected);
                        bool oldWrong = IsWrong(old, row.Expected), newWrong = IsWrong(current, row.Expected);
                        if (!oldExact && newExact) gained++;
                        if (oldExact && !newExact) lost++;
                        if (newWrong && !oldWrong) newlyWrong++;
                        if (oldExact && newWrong) exactToWrong++;
                        if (old.Text is null && old.FailureCode is null && newWrong) emptyToWrong++;
                        if (role == "background_forced" && old.Text is null && current.Text is not null) newBackgroundText++;
                        if (oldWrong && newWrong && old.Text != current.Text) changedWrongText++;
                    }
                    roles[RoleName(role)] = new
                    {
                        crop_count = after.Count,
                        original = new { exact = before.Exact, wrong = before.Wrong, empty_without_alternative = before.Empty, failures = before.Failures },
                        offset_result = new { exact = after.Exact, wrong = after.Wrong, empty_without_alternative = after.Empty, failures = after.Failures },
                        paired = new { outcomes_changed = changed, exact_gained = gained, exact_lost = lost,
                            newly_wrong_text_outputs = newlyWrong, original_exact_became_wrong = exactToWrong,
                            original_empty_became_wrong = emptyToWrong, wrong_text_changed = changedWrongText,
                            newly_returned_background_text = newBackgroundText },
                        tall_component_rejects = new
                        {
                            original = variants["original"].Count(item => item.Role == role && item.TallReject),
                            offset = variants[variant].Count(item => item.Role == role && item.TallReject),
                        },
                    };
                }
                variantMetrics[variant] = new { offset_pixels_perpendicular_to_segment = OffsetValue(variant), roles };
            }

            Require(providerCapture.Providers.Count == 1 && providerCapture.Providers.ContainsKey(InferenceProvider.Cpu),
                "Inference provider was not pinned to CPU.");
            string sourcePath = Path.Combine(root, "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/geometry_offset_sensitivity/Program.cs");
            string maskExtractorPath = Path.Combine(root, "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/geometry_only_erase/extract_glyph_masks.py");
            var report = new
            {
                schema = "graphreader.stroke-seeded-xtick-v1-p2-v5-geometry-offset-sensitivity.v1",
                recipe_id = "stroke-seeded-xtick-v1-p2-v5-centerline-offset-sensitivity-v1",
                acceptance_status = "consumed synthetic perturbation sensitivity diagnostic only",
                runner_sha256 = Hash(await File.ReadAllBytesAsync(sourcePath)),
                glyph_mask_extractor_sha256 = Hash(await File.ReadAllBytesAsync(maskExtractorPath)),
                predictions_sha256 = Hash(predictionBytes), baseline_recognition_report_sha256 = Hash(baselineBytes),
                exact_geometry_erase_report_sha256 = Hash(v2Bytes), probe_manifest_sha256 = ManifestSha,
                detector_checkpoint_sha256 = predictionRoot.GetProperty("detector_checkpoint_sha256").GetString(),
                recognizer_onnx_sha256 = ModelSha, v5_protocol_sha256 = ProtocolSha,
                glyph_mask_index_sha256 = Hash(maskIndexBytes),
                provider_counts = providerCapture.Providers.ToDictionary(static pair => pair.Key.ToString(), static pair => pair.Value),
                crop_contract = new { cropper = "GraphReader.Ocr.OcrCropBatcher", canvas = "128x32 grayscale",
                    vertical_content_padding_ratio = 0.25, horizontal_padding_pixels = 12, vertical_padding_pixels = 1,
                    resize_mode = "preserve-aspect-ratio-pad", detection_score_threshold = 0.25 },
                perturbation = new
                {
                    offsets_pixels = new[] { -2, -1, 0, 1, 2 },
                    offset_direction = "signed perpendicular to the affine-mapped centerline in 256x96 probe-raster pixels",
                    width_pixels = 1, rasterization = "Bresenham 8-connected; endpoints rounded midpoint-to-even",
                    fill_value = 255, each_variant_starts_from_original_image = true,
                    no_glyph_mask_or_text_used_to_choose_erasure = true,
                    interpretation = "perturbation sensitivity only; offset lines are counterfactual geometric shifts, not observed production stroke locations",
                },
                sample_counts = new
                {
                    positive_rows = parsedRows.Count(static row => row.Target is not null),
                    background_rows = parsedRows.Count(static row => row.Target is null),
                    background_unique_image_hashes = parsedRows.Where(static row => row.Target is null).Select(static row => row.ImageSha).Distinct(StringComparer.Ordinal).Count(),
                    scored_rows_are_not_deduplicated = true,
                    detector_selected_background_rows_at_threshold = parsedRows.Count(static row => row.Target is null && row.Score >= 0.25),
                    crop_rows_per_variant = 488,
                },
                original_baseline_parity_to_prior_report = parity,
                offset_results = variantMetrics,
                actual_glyph_mask_intersections = overlapMetrics,
                elapsed_all_variant_inference_milliseconds = timer.Elapsed.TotalMilliseconds,
                training_performed = false, dev_sealed_private_inputs_read = false,
                production_behavior_changed = false, production_approval = false, release_eligible = false,
                case_level_predictions_retained = false,
                limitation = "The probe is already consumed synthetic development data, not held-out acceptance. The source annotation does not carry rendered stroke width/antialiasing; all variants test only one-pixel centerlines. Offset variants are controlled geometric perturbations and must not be interpreted as evidence about actual production stroke location.",
            };
            string reportPath = Path.GetFullPath(args[4]);
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

    private static void AddRoleCrops(List<CropEntry> entries, ProbeRow row, OcrImage image, bool[] eraseMask, int erasedPixelCount,
        LocalOnnxComponentTextRecognizerOptions options, EncodeCropInvoker encoder, MethodInfo filter,
        MethodInfo intervals, MethodInfo glyph)
    {
        if (row.Target is not null)
        {
            entries.Add(CreateCrop(row, "gt", row.Target.Value, image, options, encoder, filter, intervals, glyph, eraseMask, erasedPixelCount));
            if (row.Score >= 0.25)
                entries.Add(CreateCrop(row, "pred", row.Predicted, image, options, encoder, filter, intervals, glyph, eraseMask, erasedPixelCount));
        }
        else entries.Add(CreateCrop(row, "background_forced", row.Predicted, image, options, encoder, filter, intervals, glyph, eraseMask, erasedPixelCount));
    }

    private static CropEntry CreateCrop(ProbeRow row, string role, OcrRectangle box, OcrImage image,
        LocalOnnxComponentTextRecognizerOptions options, EncodeCropInvoker encoder, MethodInfo filter,
        MethodInfo intervals, MethodInfo glyph, bool[] eraseMask, int erasedPixelCount)
    {
        string id = $"{row.CaseId}:{role}";
        var detected = new OcrDetectedRegion(id, OcrPolygon.FromRectangle(box), 0, row.Score);
        IReadOnlyList<IReadOnlyList<OcrCrop>> batches = OcrCropBatcher.CreateBatches(image, [detected], new OcrCropBatcherOptions
        {
            TargetWidth = 128, TargetHeight = 32, BatchSize = 16, HorizontalPaddingPixels = 12, VerticalPaddingPixels = 1,
            VerticalContentPaddingRatio = 0.25, ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad, PaddingValue = 1.0f,
        });
        Require(batches.Count == 1 && batches[0].Count == 1, $"Cropper output mismatch {row.CaseId}/{role}.");
        OcrCrop crop = batches[0][0];
        Require(crop.Width == 128 && crop.Height == 32 && SameRect(crop.OriginalPolygon.Bounds, box),
            $"Cropper coordinate mismatch {row.CaseId}/{role}.");
        bool tall = EncodeFlags(crop, options, encoder, filter, intervals, glyph);
        return new CropEntry(row, role, row.Expected, crop, eraseMask, tall, erasedPixelCount);
    }

    private static bool EncodeFlags(OcrCrop crop, LocalOnnxComponentTextRecognizerOptions options, EncodeCropInvoker encoder,
        MethodInfo filterMethod, MethodInfo intervalsMethod, MethodInfo glyphMethod)
    {
        object encoded = encoder(crop.Pixels.Span, options);
        Type resultType = encoded.GetType();
        bool rejected = (bool)resultType.GetProperty("Rejected")!.GetValue(encoded)!;
        bool ambiguous = (bool)resultType.GetProperty("HasAmbiguousBaselineSingleton")!.GetValue(encoded)!;
        byte[] raster = crop.Pixels.Span.ToArray().Select(static value => (byte)Math.Clamp(
            (int)Math.Round(value * 255d, MidpointRounding.ToEven), 0, 255)).ToArray();
        object filtered = filterMethod.Invoke(null, [raster, 128, 32])!;
        bool[] foreground = (bool[])filtered.GetType().GetProperty("Pixels")!.GetValue(filtered)!;
        int tall = 0, count = 0;
        foreach (object? interval in (IEnumerable)intervalsMethod.Invoke(null, [foreground, 128, 32])!)
        {
            if (interval is not System.Runtime.CompilerServices.ITuple tuple || tuple.Length != 2)
                throw new InvalidDataException("ActiveIntervals signature changed.");
            object? component = glyphMethod.Invoke(null, [raster, foreground, (int)tuple[0]!, (int)tuple[1]!, options]);
            if (component is null) continue;
            count++;
            double heightRatio = Convert.ToDouble(component.GetType().GetProperty("HeightRatio")!.GetValue(component),
                System.Globalization.CultureInfo.InvariantCulture);
            if (heightRatio >= options.StructuralRejectMinimumHeightRatio) tall++;
        }
        bool tooMany = count > options.MaximumGlyphs;
        Require(rejected == (!ambiguous && (tall > 0 || tooMany)), "Adapter rejection predicate parity failed.");
        return tall > 0;
    }

    private static (double[] P0, double[] P1, int Fill) MapSegment(JsonElement annotation)
    {
        JsonElement line = annotation.GetProperty("stroke_line_original_xyxy");
        double[] m = annotation.GetProperty("crop_transform").GetProperty("original_to_crop_pixel")
            .EnumerateArray().Select(static item => item.GetDouble()).ToArray();
        Require(m.Length == 6 && Math.Abs(m[1]) < 1e-9 && Math.Abs(m[3]) < 1e-9, "Non-axis-aligned crop transform.");
        double[] p0 = [m[0] * line[0].GetDouble() + m[1] * line[1].GetDouble() + m[2],
            m[3] * line[0].GetDouble() + m[4] * line[1].GetDouble() + m[5]];
        double[] p1 = [m[0] * line[2].GetDouble() + m[1] * line[3].GetDouble() + m[2],
            m[3] * line[2].GetDouble() + m[4] * line[3].GetDouble() + m[5]];
        int fill = annotation.GetProperty("crop_transform").GetProperty("outside_panel_value").GetInt32();
        Require(fill == 255, "Neutral fill differs from pinned probe contract.");
        return (p0, p1, fill);
    }

    private static (byte[] Image, bool[] Mask, int PixelCount) EraseShiftedCenterline(ProbeRow row, double offset)
    {
        byte[] copy = row.Pixels.ToArray();
        var mask = new bool[copy.Length];
        int count = Rasterize(row.P0, row.P1, offset, row.Fill, copy, mask);
        return (copy, mask, count);
    }

    private static int Rasterize(double[] p0, double[] p1, double offset, int fill, byte[] image, bool[] mask)
    {
        double dx = p1[0] - p0[0], dy = p1[1] - p0[1];
        double length = Math.Sqrt(dx * dx + dy * dy);
        Require(length > 0 && double.IsFinite(length), "Invalid stroke segment length.");
        double nx = -dy / length, ny = dx / length;
        int x0 = checked((int)Math.Round(p0[0] + offset * nx, MidpointRounding.ToEven));
        int y0 = checked((int)Math.Round(p0[1] + offset * ny, MidpointRounding.ToEven));
        int x1 = checked((int)Math.Round(p1[0] + offset * nx, MidpointRounding.ToEven));
        int y1 = checked((int)Math.Round(p1[1] + offset * ny, MidpointRounding.ToEven));
        int adx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int ady = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int error = adx + ady, count = 0;
        while (true)
        {
            if ((uint)x0 < 256 && (uint)y0 < 96)
            {
                int index = y0 * 256 + x0;
                if (!mask[index]) { mask[index] = true; image[index] = (byte)fill; count++; }
            }
            if (x0 == x1 && y0 == y1) break;
            int twice = error * 2;
            if (twice >= ady) { error += ady; x0 += sx; }
            if (twice <= adx) { error += adx; y0 += sy; }
        }
        return count;
    }

    private static object Summarize(CropEntry[] rows, Dictionary<string, Outcome> outputs)
    {
        OutcomeSummary summary = SummarizeCounts(rows, outputs);
        return new { crop_count = summary.Count, exact = summary.Exact, wrong = summary.Wrong,
            empty_without_alternative = summary.Empty, explicit_failures = summary.Failures,
            tall_component_rejects = rows.Count(static row => row.TallReject) };
    }

    private static OutcomeSummary SummarizeCounts(CropEntry[] rows, Dictionary<string, Outcome> outputs)
    {
        int exact = 0, wrong = 0, empty = 0, failures = 0;
        foreach (CropEntry row in rows)
        {
            Outcome outcome = outputs[row.Crop.RegionId];
            if (outcome.FailureCode is not null) failures++;
            else if (outcome.Text is null) empty++;
            else if (row.Expected is not null && outcome.Text == row.Expected) exact++;
            else wrong++;
        }
        return new OutcomeSummary(rows.Length, exact, wrong, empty, failures);
    }

    private static bool IsExact(Outcome output, string? expected) => expected is not null && output.FailureCode is null && output.Text == expected;
    private static bool IsWrong(Outcome output, string? expected) => output.FailureCode is null && output.Text is not null && output.Text != expected;
    private static bool SameOutcome(Outcome a, Outcome b) => a.Text == b.Text && a.FailureCode == b.FailureCode && a.FailureSeverity == b.FailureSeverity;
    private static bool SameRect(OcrRectangle a, OcrRectangle b) => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6 &&
        Math.Abs(a.Width - b.Width) < 1e-6 && Math.Abs(a.Height - b.Height) < 1e-6;
    private static string RoleName(string role) => role switch { "gt" => "ground_truth_positive_boxes", "pred" => "predicted_positive_boxes", _ => "forced_background_top_one_boxes" };
    private static string Key(double offset) => offset == 0 ? "offset0" : $"offset{offset:+0;-0}";
    private static double OffsetValue(string key) => double.Parse(key.Replace("offset", "", StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static MethodInfo PrivateMethod(string name, int count) => typeof(LocalOnnxComponentTextRecognizer)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(method => method.Name == name && method.GetParameters().Length == count);
    private static EncodeCropInvoker CreateEncoder(MethodInfo method)
    {
        Require(method.ReturnType.IsClass && method.GetParameters()[0].ParameterType == typeof(ReadOnlySpan<float>), "EncodeCrop signature changed.");
        var dynamic = new DynamicMethod("InvokeOffsetSensitivityEncoder", typeof(object),
            [typeof(ReadOnlySpan<float>), typeof(LocalOnnxComponentTextRecognizerOptions)], typeof(Program).Module, true);
        ILGenerator il = dynamic.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, method); il.Emit(OpCodes.Ret);
        return (EncodeCropInvoker)dynamic.CreateDelegate(typeof(EncodeCropInvoker));
    }

    private static async Task<byte[]> ReadProjectAsync(string root, string relative, string expectedHash, int maxBytes)
    {
        string rootPath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase), "Input path escapes project root.");
        var file = new FileInfo(path);
        Require(file.Exists && file.Length is > 0 && file.Length <= maxBytes, $"Input size mismatch {relative}.");
        for (FileSystemInfo? current = file; current is not null &&
             !string.Equals(current.FullName, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase); current = current switch
             {
                 FileInfo info => info.Directory,
                 DirectoryInfo directory => directory.Parent,
                 _ => null,
             }) Require((current.Attributes & FileAttributes.ReparsePoint) == 0, $"Reparse point in {relative}.");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Require(Hash(bytes) == expectedHash, $"Input hash mismatch {relative}.");
        return bytes;
    }

    private static async Task<byte[]> ReadMaskAsync(string root, string relative, string expectedHash)
    {
        string basePath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase), "Mask path escapes its output directory.");
        var file = new FileInfo(path);
        Require(file.Exists && file.Length is > 0 and <= 1_000_000 && (file.Attributes & FileAttributes.ReparsePoint) == 0,
            "Glyph mask is missing, oversized, or reparse-backed.");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Require(Hash(bytes) == expectedHash, "Glyph mask SHA mismatch.");
        return bytes;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }

    private sealed record ProbeRow(string CaseId, string ImagePath, string ImageSha, string AnnotationSha, string Kind,
        string? Expected, OcrRectangle? Target, OcrRectangle Predicted, double Score, byte[] Pixels,
        double[] P0, double[] P1, int Fill);
    private sealed record CropEntry(ProbeRow Probe, string Role, string? Expected, OcrCrop Crop, bool[] EraseMask,
        bool TallReject, int ErasedPixelCount);
    private sealed record Outcome(string? Text, string? FailureCode, string? FailureSeverity);
    private sealed record OutcomeSummary(int Count, int Exact, int Wrong, int Empty, int Failures);

    private sealed class ProviderCaptureCache : IStageCache
    {
        public Dictionary<InferenceProvider, int> Providers { get; } = [];
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken token) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken token)
        {
            if (value.Length < 6) throw new InvalidDataException("Inference response missing provider.");
            InferenceProvider provider = (InferenceProvider)value.Span[5];
            Providers[provider] = Providers.GetValueOrDefault(provider) + 1;
            return ValueTask.CompletedTask;
        }
    }
}
