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
    private const string PredictionsSha = "235d39476cf3ef498d0c026f05e4c9eb626fe43c853d0af49ab6d0bef2d54097";
    private const string ManifestSha = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba";
    private const string BaselineSha = "8bb9ee29ab4ac0ee0f09572fabd367624d82cc11d5a33d42a044752d56bd018e";
    private const string ModelSha = "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84";
    private const string ProtocolSha = "3c4a169eb1b51e6895152c00e0ef2ff664e8154abc911a21cbcba17e0346adfa";
    private const string CorpusRoot = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3";
    private const string ModelRelative = "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private delegate object EncodeCropInvoker(ReadOnlySpan<float> pixels, LocalOnnxComponentTextRecognizerOptions options);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 5)
        {
            Console.Error.WriteLine("usage: GeometryOnlyErase <repo-root> <predictions.json> <baseline-report.json> <mask-output-dir> <report.json>");
            return 2;
        }

        try
        {
            string root = Path.GetFullPath(args[0]);
            byte[] predictionsBytes = await File.ReadAllBytesAsync(Path.GetFullPath(args[1]));
            byte[] baselineBytes = await File.ReadAllBytesAsync(Path.GetFullPath(args[2]));
            Require(Hash(predictionsBytes) == PredictionsSha && Hash(baselineBytes) == BaselineSha,
                "Pinned predictions or comparison baseline hash changed.");
            byte[] manifestBytes = await ReadProjectBytesAsync(root, $"{CorpusRoot}/manifest.json", ManifestSha, 5_000_000);
            byte[] modelBytes = await ReadProjectBytesAsync(root, ModelRelative, ModelSha, 10_000_000);
            byte[] protocolBytes = await ReadProjectBytesAsync(root, "ml/ocr/component_ensemble_v5/PROTOCOL.json", ProtocolSha, 1_000_000);
            _ = modelBytes; _ = protocolBytes;

            using JsonDocument predictionsDoc = JsonDocument.Parse(predictionsBytes);
            JsonElement predictionRoot = predictionsDoc.RootElement;
            Require(predictionRoot.GetProperty("probe_manifest_sha256").GetString() == ManifestSha &&
                    predictionRoot.GetProperty("v5_recognizer_onnx_sha256").GetString() == ModelSha &&
                    !predictionRoot.GetProperty("training_performed").GetBoolean(),
                "Prediction identities are not the pinned inference-only probe.");
            using JsonDocument baselineDoc = JsonDocument.Parse(baselineBytes);
            JsonElement baselineRoot = baselineDoc.RootElement;
            using JsonDocument manifestDoc = JsonDocument.Parse(manifestBytes);
            Dictionary<string, JsonElement> manifestRows = manifestDoc.RootElement.GetProperty("records").EnumerateArray()
                .ToDictionary(static row => row.GetProperty("case_id").GetString()!, StringComparer.Ordinal);

            MethodInfo filter = PrivateMethod("FilterForeground", 3);
            MethodInfo activeIntervals = PrivateMethod("ActiveIntervals", 3);
            MethodInfo encodeGlyph = PrivateMethod("EncodeGlyph", 5);
            EncodeCropInvoker encodeCrop = CreateEncodeCropInvoker(PrivateMethod("EncodeCrop", 2));
            var options = new LocalOnnxComponentTextRecognizerOptions(
                new ModelIdentity("graph-numeric-component-ensemble-v5", "0.0.21-p1", ModelSha,
                    Path.Combine(root, ModelRelative)), "0123456789.-%");

            var probeRows = new List<ProbeRow>();
            foreach (JsonElement prediction in predictionRoot.GetProperty("records").EnumerateArray())
            {
                string caseId = prediction.GetProperty("case_id").GetString()!;
                Require(manifestRows.TryGetValue(caseId, out JsonElement manifestRow), $"Manifest row missing: {caseId}.");
                byte[] annotationBytes = await ReadProjectBytesAsync(root,
                    $"{CorpusRoot}/{manifestRow.GetProperty("annotation_path").GetString()}",
                    manifestRow.GetProperty("annotation_sha256").GetString()!, 65_536);
                using JsonDocument annotationDoc = JsonDocument.Parse(annotationBytes);
                JsonElement annotation = annotationDoc.RootElement;
                JsonElement targetRows = annotation.GetProperty("targets");
                OcrRectangle? targetBox = null;
                if (targetRows.GetArrayLength() == 1)
                {
                    JsonElement target = targetRows[0].GetProperty("target_crop_xyxy");
                    targetBox = new OcrRectangle(target[0].GetDouble(), target[1].GetDouble(),
                        target[2].GetDouble() - target[0].GetDouble(), target[3].GetDouble() - target[1].GetDouble());
                }
                else Require(targetRows.GetArrayLength() == 0, $"Ambiguous text targets: {caseId}.");
                string? expected = annotation.GetProperty("expected_tick_label").ValueKind == JsonValueKind.String
                    ? annotation.GetProperty("expected_tick_label").GetString() : null;
                Require(expected == (prediction.GetProperty("expected_text").ValueKind == JsonValueKind.String
                    ? prediction.GetProperty("expected_text").GetString() : null), $"Target text mismatch {caseId}.");
                JsonElement boxValues = prediction.GetProperty("top_one_xyxy_normalized");
                double x0 = boxValues[0].GetDouble() * 256, y0 = boxValues[1].GetDouble() * 96;
                double x1 = boxValues[2].GetDouble() * 256, y1 = boxValues[3].GetDouble() * 96;
                var predictedBox = new OcrRectangle(x0, y0, x1 - x0, y1 - y0);
                Require(predictedBox.IsValid && predictedBox.X >= 0 && predictedBox.Y >= 0 &&
                    predictedBox.Right <= 256 && predictedBox.Bottom <= 96, $"Invalid predicted box: {caseId}.");
                if (targetBox is not null) Require(targetBox.Value.IsValid, $"Invalid target box: {caseId}.");

                byte[] originalBytes = await ReadProjectBytesAsync(root,
                    $"{CorpusRoot}/{manifestRow.GetProperty("image_path").GetString()}",
                    manifestRow.GetProperty("image_sha256").GetString()!, 1_000_000);
                using Mat decoded = Cv2.ImDecode(originalBytes, ImreadModes.Grayscale);
                Require(!decoded.Empty() && decoded.Width == 256 && decoded.Height == 96 && decoded.Type() == MatType.CV_8UC1,
                    $"Probe raster contract mismatch: {caseId}.");
                byte[] original = new byte[256 * 96];
                System.Runtime.InteropServices.Marshal.Copy(decoded.Data, original, 0, original.Length);

                // The erasure mask is made from only the pinned stroke segment, its affine crop transform,
                // and the transform's neutral outside-panel value. Text labels and glyph masks are not read here.
                (byte[] erased, bool[] eraseMask, int erasedPixelCount) = EraseSegmentOnly(original, annotation);
                probeRows.Add(new ProbeRow(caseId, manifestRow.GetProperty("image_path").GetString()!,
                    manifestRow.GetProperty("image_sha256").GetString()!,
                    prediction.GetProperty("kind").GetString()!, expected, targetBox, predictedBox,
                    prediction.GetProperty("top_one_score").GetDouble(), original, erased, eraseMask, erasedPixelCount,
                    manifestRow.GetProperty("annotation_sha256").GetString()!));
            }
            Require(probeRows.Count == 323 && probeRows.Count(static row => row.TargetBox is not null) == 165 &&
                    probeRows.Count(static row => row.TargetBox is null) == 158,
                "Probe composition differs from the pinned 165 positive / 158 background rows.");

            var entries = new List<CropEntry>(976);
            foreach (ProbeRow row in probeRows)
            {
                var originalImage = CreateImage(row.OriginalPixels);
                var erasedImage = CreateImage(row.ErasedPixels);
                if (row.TargetBox is not null)
                {
                    entries.Add(CreateCrop(row, "gt", row.TargetBox.Value, originalImage, erasedImage, options, filter,
                        activeIntervals, encodeGlyph, encodeCrop));
                    if (row.Score >= 0.25)
                        entries.Add(CreateCrop(row, "pred", row.PredictedBox, originalImage, erasedImage, options, filter,
                            activeIntervals, encodeGlyph, encodeCrop));
                }
                else
                {
                    entries.Add(CreateCrop(row, "background_forced", row.PredictedBox, originalImage, erasedImage, options,
                        filter, activeIntervals, encodeGlyph, encodeCrop));
                }
            }
            Require(entries.Count == 488, $"Unexpected baseline/erased crop pair count: {entries.Count}.");

            // Recognition uses the cropper and adapter on the original and geometry-erased copies.
            // Actual glyph-mask sidecars are deliberately not opened until after both inference passes finish.
            var providerCapture = new ProviderCaptureCache();
            IReadOnlyList<InferenceProvider> providers = [InferenceProvider.Cpu];
            var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(),
                new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance), CpuThreadConfiguration.Create(1));
            var scheduler = new BoundedInferenceScheduler(capacity: 4, workerCount: 1);
            await using var runtime = new InferenceRuntime(registry, scheduler, providerCapture, allowedProviders: providers);
            var recognizer = new LocalOnnxComponentTextRecognizer(runtime,
                options with { StageVersion = "stroke-geometry-only-erase-v1", AllowedProviders = providers, BypassCache = true });
            var baselineOutcomes = new Dictionary<string, Outcome>(StringComparer.Ordinal);
            var erasedOutcomes = new Dictionary<string, Outcome>(StringComparer.Ordinal);
            var inferenceTimer = System.Diagnostics.Stopwatch.StartNew();
            foreach (bool erasedVersion in new[] { false, true })
            {
                OcrCrop[] crops = entries.Select(entry => erasedVersion ? entry.ErasedCrop : entry.OriginalCrop).ToArray();
                Dictionary<string, Outcome> destination = erasedVersion ? erasedOutcomes : baselineOutcomes;
                for (int offset = 0; offset < crops.Length; offset += 16)
                {
                    OcrCrop[] batch = crops.Skip(offset).Take(16).ToArray();
                    IReadOnlyList<OcrRecognition> results = await recognizer.RecognizeBatchAsync(batch, CancellationToken.None);
                    Require(results.Count == batch.Length, "Recognizer returned wrong batch size.");
                    foreach (OcrRecognition result in results)
                    {
                        OcrRecognitionAlternative? best = result.Alternatives.OrderByDescending(static alt => alt.Confidence).FirstOrDefault();
                        destination.Add(result.RegionId, new Outcome(best?.Text, result.Failure?.Code, result.Failure?.Severity));
                    }
                }
            }
            inferenceTimer.Stop();

            // Post-erasure safety analysis. Reconstructed masks are read only after both image versions were recognized.
            string maskRoot = Path.GetFullPath(args[3]);
            string maskIndexPath = Path.Combine(maskRoot, "mask-index.json");
            byte[] maskIndexBytes = await File.ReadAllBytesAsync(maskIndexPath);
            using JsonDocument maskIndexDoc = JsonDocument.Parse(maskIndexBytes);
            JsonElement maskIndex = maskIndexDoc.RootElement;
            Require(maskIndex.GetProperty("probe_manifest_sha256").GetString() == ManifestSha &&
                    maskIndex.GetProperty("record_count").GetInt32() == 165 &&
                    maskIndex.GetProperty("verified_probe_image_count").GetInt32() == 359,
                "Reconstructed mask index scope is not the pinned probe.");
            Dictionary<string, JsonElement> masks = maskIndex.GetProperty("records").EnumerateArray()
                .ToDictionary(static row => row.GetProperty("case_id").GetString()!, StringComparer.Ordinal);
            var overlapByCase = new Dictionary<string, MaskOverlap>(StringComparer.Ordinal);
            double maxCropMaskBoxDelta = 0;
            foreach (ProbeRow row in probeRows.Where(static row => row.TargetBox is not null))
            {
                Require(masks.TryGetValue(row.CaseId, out JsonElement maskRow), $"Missing glyph mask for {row.CaseId}.");
                Require(maskRow.GetProperty("annotation_sha256").GetString() == row.AnnotationSha,
                    $"Glyph mask annotation identity mismatch: {row.CaseId}.");
                byte[] maskBytes = await ReadLocalMaskAsync(maskRoot, maskRow.GetProperty("path").GetString()!,
                    maskRow.GetProperty("sha256").GetString()!);
                using Mat maskImage = Cv2.ImDecode(maskBytes, ImreadModes.Grayscale);
                Require(!maskImage.Empty() && maskImage.Width == 256 && maskImage.Height == 96 && maskImage.Type() == MatType.CV_8UC1,
                    $"Glyph mask raster shape mismatch: {row.CaseId}.");
                byte[] glyphPixels = new byte[256 * 96];
                System.Runtime.InteropServices.Marshal.Copy(maskImage.Data, glyphPixels, 0, glyphPixels.Length);
                int maskGlyphPixelCount = 0, overlap = 0, minX = 256, minY = 96, maxX = -1, maxY = -1;
                for (int i = 0; i < glyphPixels.Length; i++)
                {
                    if (glyphPixels[i] == 0) continue;
                    maskGlyphPixelCount++;
                    int x = i % 256, y = i / 256;
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                    if (row.EraseMask[i]) overlap++;
                }
                Require(maskGlyphPixelCount == maskRow.GetProperty("crop_mask_nonzero_pixels").GetInt32(),
                    $"Glyph mask pixel count mismatch: {row.CaseId}.");
                OcrRectangle truth = row.TargetBox!.Value;
                double[] deltas = [Math.Abs(minX - truth.Left), Math.Abs(minY - truth.Top),
                    Math.Abs(maxX + 1 - truth.Right), Math.Abs(maxY + 1 - truth.Bottom)];
                maxCropMaskBoxDelta = Math.Max(maxCropMaskBoxDelta, deltas.Max());
                overlapByCase.Add(row.CaseId, new MaskOverlap(overlap > 0, maskGlyphPixelCount, overlap, row.ErasePixelCount));
            }
            Require(maxCropMaskBoxDelta <= 1.0, "Reconstructed crop glyph-mask bounds differ by more than 1 pixel from pinned annotation mapping.");

            bool BaselineMatches(string role, string baselineKey)
            {
                JsonElement expected = baselineRoot.GetProperty("per_score_threshold").GetProperty("0.25").GetProperty(baselineKey);
                CropEntry[] selected = entries.Where(item => item.Role == role).ToArray();
                var summary = CountOutcomes(selected, baselineOutcomes);
                return expected.GetProperty("sample_count").GetInt32() == selected.Length &&
                       expected.GetProperty("exact").GetInt32() == summary.Exact &&
                       expected.GetProperty("wrong").GetInt32() == summary.Wrong &&
                       expected.GetProperty("empty_without_alternative").GetInt32() == summary.Empty &&
                       expected.GetProperty("explicit_failures_by_severity_and_code").EnumerateObject()
                           .Sum(static item => item.Value.GetInt32()) == summary.Failures;
            }
            var baselineParity = new
            {
                ground_truth_positive_boxes = BaselineMatches("gt", "ground_truth_boxes"),
                predicted_positive_boxes = BaselineMatches("pred", "predicted_boxes"),
            };

            var roleMetrics = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string role in new[] { "gt", "pred", "background_forced" })
            {
                CropEntry[] roleRows = entries.Where(item => item.Role == role).ToArray();
                roleMetrics.Add(role switch { "gt" => "ground_truth_positive_boxes", "pred" => "predicted_positive_boxes", _ => "forced_background_top_one_boxes" },
                    Compare(roleRows, baselineOutcomes, erasedOutcomes));
            }
            var strata = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (bool overlaps in new[] { false, true })
            {
                string key = overlaps ? "erase_mask_overlaps_actual_glyph_pixels" : "no_actual_glyph_pixel_overlap";
                var roleStrata = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string role in new[] { "gt", "pred" })
                {
                    CropEntry[] subset = entries.Where(item => item.Role == role &&
                        overlapByCase[item.Probe.CaseId].OverlapsGlyph == overlaps).ToArray();
                    roleStrata[role == "gt" ? "ground_truth_positive_boxes" : "predicted_positive_boxes"] =
                        Compare(subset, baselineOutcomes, erasedOutcomes);
                }
                strata[key] = new { positive_case_count = overlapByCase.Values.Count(value => value.OverlapsGlyph == overlaps), crops_by_role = roleStrata };
            }
            int overlapCases = overlapByCase.Values.Count(static item => item.OverlapsGlyph);
            int totalGlyphPixels = overlapByCase.Values.Sum(static item => item.GlyphPixels);
            int totalErasureOverlap = overlapByCase.Values.Sum(static item => item.OverlappingPixels);
            int totalErasePixels = probeRows.Sum(static row => row.ErasePixelCount);
            var report = new
            {
                schema = "graphreader.stroke-seeded-xtick-v1-p2-v5-geometry-only-tick-erase.v1",
                recipe_id = "stroke-seeded-xtick-v1-p2-v5-geometry-only-centerline-erase-diagnostic-v1",
                acceptance_status = "consumed synthetic development diagnostic only",
                runner_sha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(root,
                    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/geometry_only_erase/Program.cs"))),
                glyph_mask_extractor_sha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(root,
                    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/geometry_only_erase/extract_glyph_masks.py"))),
                predictions_sha256 = Hash(predictionsBytes), baseline_report_sha256 = Hash(baselineBytes),
                probe_manifest_sha256 = ManifestSha, detector_checkpoint_sha256 = predictionRoot.GetProperty("detector_checkpoint_sha256").GetString(),
                recognizer_onnx_sha256 = ModelSha, v5_protocol_sha256 = ProtocolSha,
                glyph_mask_index_sha256 = Hash(maskIndexBytes),
                provider_counts = providerCapture.Providers.ToDictionary(static item => item.Key.ToString(), static item => item.Value),
                crop = new { cropper = "GraphReader.Ocr.OcrCropBatcher", width = 128, height = 32, vertical_content_padding_ratio = 0.25,
                    horizontal_padding_pixels = 12, vertical_padding_pixels = 1, resize = "preserve-aspect-ratio-pad" },
                geometry_erasure = new
                {
                    input_geometry = "stroke_line_original_xyxy transformed by each annotation's original_to_crop_pixel matrix",
                    rasterizer = "Bresenham 8-connected 1-pixel digital centerline, endpoints rounded to nearest pixel using midpoint-to-even",
                    fill_value_source = "annotation crop_transform.outside_panel_value, expected 255",
                    pixels_set_to_neutral_total_across_323_rows = totalErasePixels,
                    centerline_width_pixels = 1,
                    no_label_or_glyph_mask_used_to_choose_erased_pixels = true,
                    source_images_modified_on_disk = false,
                },
                sample_counts = new
                {
                    positive_rows = probeRows.Count(static row => row.TargetBox is not null),
                    background_rows = probeRows.Count(static row => row.TargetBox is null),
                    background_unique_image_hashes = probeRows.Where(static row => row.TargetBox is null).Select(static row => row.ImageSha).Distinct(StringComparer.Ordinal).Count(),
                    all_rows_scored_without_deduplication = true,
                    detector_selected_background_rows_score_at_least_025 = probeRows.Count(static row => row.TargetBox is null && row.Score >= 0.25),
                },
                baseline_parity_at_margin_025 = baselineParity,
                metrics_by_role = roleMetrics,
                glyph_mask_safety = new
                {
                    actual_masks_from_pinned_renderer = true,
                    full_panel_mask_hash_mismatches = 0,
                    probe_image_hash_mismatches = 0,
                    positive_cases_with_any_erased_pixel_overlapping_actual_glyph_mask = overlapCases,
                    positive_cases_without_actual_glyph_overlap = overlapByCase.Count - overlapCases,
                    total_glyph_mask_nonzero_pixels = totalGlyphPixels,
                    actual_glyph_mask_pixels_erased_by_centerline = totalErasureOverlap,
                    total_glyph_mask_pixel_overlap_fraction = totalErasureOverlap / (double)Math.Max(1, totalGlyphPixels),
                    per_overlap_stratum_metrics = strata,
                    mask_index_records = masks.Count,
                    mask_index_sha256 = Hash(maskIndexBytes),
                    crop_mask_target_box_max_edge_delta_pixels = maxCropMaskBoxDelta,
                    crop_mask_target_box_alignment_verified_within_1px = true,
                },
                elapsed_inference_and_batch_milliseconds = inferenceTimer.Elapsed.TotalMilliseconds,
                training_performed = false, dev_sealed_private_inputs_read = false,
                production_behavior_changed = false, production_approval = false, release_eligible = false,
                retained_case_level_predictions = false,
                limitation = "The annotations carry the centerline and crop transform but not rendered stroke width/antialiasing. This diagnostic erases only a one-pixel Bresenham centerline using the annotation's neutral outside-panel value. It does not claim to erase the full rendered stroke, so unchanged OCR would not disprove a wider-stroke effect. The 323 rows are already consumed synthetic probe data, not held-out acceptance.",
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

    private static (byte[] Erased, bool[] Mask, int PixelCount) EraseSegmentOnly(byte[] original, JsonElement annotation)
    {
        JsonElement line = annotation.GetProperty("stroke_line_original_xyxy");
        double[] affine = annotation.GetProperty("crop_transform").GetProperty("original_to_crop_pixel")
            .EnumerateArray().Select(static value => value.GetDouble()).ToArray();
        Require(affine.Length == 6 && Math.Abs(affine[1]) < 1e-9 && Math.Abs(affine[3]) < 1e-9 &&
                Math.Abs(affine[0]) > 0 && Math.Abs(affine[4]) > 0,
            "Unsupported non-axis-aligned stroke transform.");
        int fill = annotation.GetProperty("crop_transform").GetProperty("outside_panel_value").GetInt32();
        Require(fill == 255, "Unexpected neutral fill value.");
        double ax = affine[0] * line[0].GetDouble() + affine[1] * line[1].GetDouble() + affine[2];
        double ay = affine[3] * line[0].GetDouble() + affine[4] * line[1].GetDouble() + affine[5];
        double bx = affine[0] * line[2].GetDouble() + affine[1] * line[3].GetDouble() + affine[2];
        double by = affine[3] * line[2].GetDouble() + affine[4] * line[3].GetDouble() + affine[5];
        int x0 = checked((int)Math.Round(ax, MidpointRounding.ToEven));
        int y0 = checked((int)Math.Round(ay, MidpointRounding.ToEven));
        int x1 = checked((int)Math.Round(bx, MidpointRounding.ToEven));
        int y1 = checked((int)Math.Round(by, MidpointRounding.ToEven));
        byte[] erased = original.ToArray();
        bool[] mask = new bool[original.Length];
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int error = dx + dy, count = 0;
        while (true)
        {
            if ((uint)x0 < 256 && (uint)y0 < 96)
            {
                int index = y0 * 256 + x0;
                if (!mask[index]) { mask[index] = true; erased[index] = (byte)fill; count++; }
            }
            if (x0 == x1 && y0 == y1) break;
            int twice = 2 * error;
            if (twice >= dy) { error += dy; x0 += sx; }
            if (twice <= dx) { error += dx; y0 += sy; }
        }
        return (erased, mask, count);
    }

    private static CropEntry CreateCrop(ProbeRow row, string role, OcrRectangle box, OcrImage original,
        OcrImage erased, LocalOnnxComponentTextRecognizerOptions options, MethodInfo filter, MethodInfo intervals,
        MethodInfo glyph, EncodeCropInvoker encoder)
    {
        OcrCrop Make(OcrImage image, string suffix)
        {
            var region = new OcrDetectedRegion($"{row.CaseId}:{role}:{suffix}", OcrPolygon.FromRectangle(box), 0, row.Score);
            IReadOnlyList<IReadOnlyList<OcrCrop>> batches = OcrCropBatcher.CreateBatches(image, [region], new OcrCropBatcherOptions
            {
                TargetWidth = 128, TargetHeight = 32, BatchSize = 16, HorizontalPaddingPixels = 12,
                VerticalPaddingPixels = 1, VerticalContentPaddingRatio = 0.25,
                ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad, PaddingValue = 1.0f,
            });
            Require(batches.Count == 1 && batches[0].Count == 1, $"Expected one crop: {row.CaseId}/{role}/{suffix}.");
            OcrCrop crop = batches[0][0];
            Require(crop.Width == 128 && crop.Height == 32 && SameRect(crop.OriginalPolygon.Bounds, box),
                $"Cropper coordinate contract mismatch: {row.CaseId}/{role}/{suffix}.");
            return crop;
        }
        OcrCrop originalCrop = Make(original, "original"), erasedCrop = Make(erased, "erased");
        var originalFlags = EncoderFlags(originalCrop, options, filter, intervals, glyph, encoder);
        var erasedFlags = EncoderFlags(erasedCrop, options, filter, intervals, glyph, encoder);
        return new CropEntry(row, role, row.ExpectedText, originalCrop, erasedCrop,
            originalFlags.Tall, erasedFlags.Tall, originalFlags.TooMany, erasedFlags.TooMany);
    }

    private static (bool Tall, bool TooMany) EncoderFlags(OcrCrop crop, LocalOnnxComponentTextRecognizerOptions options,
        MethodInfo filterMethod, MethodInfo intervalsMethod, MethodInfo glyphMethod, EncodeCropInvoker encodeCrop)
    {
        object encoded = encodeCrop(crop.Pixels.Span, options);
        Type encodedType = encoded.GetType();
        bool rejected = (bool)encodedType.GetProperty("Rejected")!.GetValue(encoded)!;
        bool ambiguous = (bool)encodedType.GetProperty("HasAmbiguousBaselineSingleton")!.GetValue(encoded)!;
        byte[] raster = crop.Pixels.Span.ToArray().Select(static value => (byte)Math.Clamp(
            (int)Math.Round(value * 255d, MidpointRounding.ToEven), 0, 255)).ToArray();
        object filtered = filterMethod.Invoke(null, [raster, 128, 32])!;
        bool[] foreground = (bool[])filtered.GetType().GetProperty("Pixels")!.GetValue(filtered)!;
        int componentCount = 0, tallCount = 0;
        foreach (object? interval in (IEnumerable)intervalsMethod.Invoke(null, [foreground, 128, 32])!)
        {
            if (interval is not ITuple tuple || tuple.Length != 2) throw new InvalidDataException("ActiveIntervals signature changed.");
            object? component = glyphMethod.Invoke(null, [raster, foreground, (int)tuple[0]!, (int)tuple[1]!, options]);
            if (component is null) continue;
            componentCount++;
            double height = Convert.ToDouble(component.GetType().GetProperty("HeightRatio")!.GetValue(component),
                System.Globalization.CultureInfo.InvariantCulture);
            if (height >= options.StructuralRejectMinimumHeightRatio) tallCount++;
        }
        bool tooMany = componentCount > options.MaximumGlyphs;
        Require(rejected == (!ambiguous && (tallCount > 0 || tooMany)), "Exact adapter rejection predicate parity failed.");
        return (tallCount > 0, tooMany);
    }

    private static object Compare(CropEntry[] entries, Dictionary<string, Outcome> before, Dictionary<string, Outcome> after)
    {
        var original = CountOutcomes(entries, before);
        var erased = CountOutcomes(entries, after, erasedVersion: true);
        return new
        {
            crop_count = entries.Length,
            baseline = new { exact = original.Exact, wrong = original.Wrong, empty_without_alternative = original.Empty,
                explicit_failures = original.Failures, tall_component_rejects = entries.Count(static item => item.OriginalTall),
                over_maximum_glyph_count_rejects = entries.Count(static item => item.OriginalTooMany) },
            geometry_erased = new { exact = erased.Exact, wrong = erased.Wrong, empty_without_alternative = erased.Empty,
                explicit_failures = erased.Failures, tall_component_rejects = entries.Count(static item => item.ErasedTall),
                over_maximum_glyph_count_rejects = entries.Count(static item => item.ErasedTooMany) },
            paired_changes = new
            {
                outcome_changed = entries.Count(item => !SameOutcome(before[item.OriginalCrop.RegionId], after[item.ErasedCrop.RegionId])),
                exact_gained = entries.Count(item => IsExact(after[item.ErasedCrop.RegionId], item.ExpectedText) &&
                    !IsExact(before[item.OriginalCrop.RegionId], item.ExpectedText)),
                exact_lost = entries.Count(item => IsExact(before[item.OriginalCrop.RegionId], item.ExpectedText) &&
                    !IsExact(after[item.ErasedCrop.RegionId], item.ExpectedText)),
                newly_returned_text_on_background = entries.Count(item => item.Role == "background_forced" &&
                    before[item.OriginalCrop.RegionId].Text is null && after[item.ErasedCrop.RegionId].Text is not null),
                background_text_removed = entries.Count(item => item.Role == "background_forced" &&
                    before[item.OriginalCrop.RegionId].Text is not null && after[item.ErasedCrop.RegionId].Text is null),
            },
        };
    }

    private static (int Exact, int Wrong, int Empty, int Failures) CountOutcomes(CropEntry[] entries,
        Dictionary<string, Outcome> outcomes, bool erasedVersion = false)
    {
        int exact = 0, wrong = 0, empty = 0, failed = 0;
        foreach (CropEntry entry in entries)
        {
            Outcome output = outcomes[erasedVersion ? entry.ErasedCrop.RegionId : entry.OriginalCrop.RegionId];
            if (output.FailureCode is not null) failed++;
            else if (output.Text is null) empty++;
            else if (entry.ExpectedText is not null && output.Text == entry.ExpectedText) exact++;
            else wrong++;
        }
        return (exact, wrong, empty, failed);
    }

    private static bool IsExact(Outcome outcome, string? expected) => expected is not null && outcome.FailureCode is null && outcome.Text == expected;
    private static bool SameOutcome(Outcome left, Outcome right) => left.Text == right.Text && left.FailureCode == right.FailureCode && left.FailureSeverity == right.FailureSeverity;
    private static OcrImage CreateImage(byte[] pixels) => new(256, 96, 256, pixels.ToArray(), OcrSourceImage.Original,
        OcrFrameTransform.Identity, CanonicalOriginalWidth: 256, CanonicalOriginalHeight: 96);
    private static bool SameRect(OcrRectangle left, OcrRectangle right) => Math.Abs(left.X - right.X) < 1e-6 &&
        Math.Abs(left.Y - right.Y) < 1e-6 && Math.Abs(left.Width - right.Width) < 1e-6 && Math.Abs(left.Height - right.Height) < 1e-6;

    private static MethodInfo PrivateMethod(string name, int count) => typeof(LocalOnnxComponentTextRecognizer)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(method => method.Name == name && method.GetParameters().Length == count);
    private static EncodeCropInvoker CreateEncodeCropInvoker(MethodInfo method)
    {
        Require(method.ReturnType.IsClass && method.GetParameters()[0].ParameterType == typeof(ReadOnlySpan<float>), "EncodeCrop signature changed.");
        var dynamic = new DynamicMethod("InvokeGeometryEraseEncodeCrop", typeof(object),
            [typeof(ReadOnlySpan<float>), typeof(LocalOnnxComponentTextRecognizerOptions)], typeof(Program).Module, true);
        ILGenerator il = dynamic.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, method); il.Emit(OpCodes.Ret);
        return (EncodeCropInvoker)dynamic.CreateDelegate(typeof(EncodeCropInvoker));
    }

    private static async Task<byte[]> ReadProjectBytesAsync(string root, string relative, string expectedSha, int maxBytes)
    {
        string projectRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(path.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase), "Input path escapes project root.");
        var file = new FileInfo(path);
        Require(file.Exists && file.Length is > 0 && file.Length <= maxBytes, $"Input size mismatch: {relative}.");
        for (FileSystemInfo? current = file; current is not null &&
             !string.Equals(current.FullName, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase); current = current switch
             {
                 FileInfo info => info.Directory,
                 DirectoryInfo directory => directory.Parent,
                 _ => null,
             }) Require((current.Attributes & FileAttributes.ReparsePoint) == 0, $"Reparse point in {relative}.");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Require(Hash(bytes) == expectedSha, $"Input SHA mismatch: {relative}.");
        return bytes;
    }

    private static async Task<byte[]> ReadLocalMaskAsync(string root, string relative, string expectedSha)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string localRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        Require(path.StartsWith(localRoot, StringComparison.OrdinalIgnoreCase), "Mask path escapes mask directory.");
        var info = new FileInfo(path);
        Require(info.Exists && info.Length is > 0 and <= 1_000_000 && (info.Attributes & FileAttributes.ReparsePoint) == 0,
            "Glyph mask file is missing, oversized, or reparse-backed.");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Require(Hash(bytes) == expectedSha, $"Glyph mask file hash mismatch: {relative}.");
        return bytes;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record ProbeRow(string CaseId, string ImagePath, string ImageSha, string Kind,
        string? ExpectedText, OcrRectangle? TargetBox, OcrRectangle PredictedBox, double Score,
        byte[] OriginalPixels, byte[] ErasedPixels, bool[] EraseMask, int ErasePixelCount, string AnnotationSha);
    private sealed record CropEntry(ProbeRow Probe, string Role, string? ExpectedText, OcrCrop OriginalCrop, OcrCrop ErasedCrop,
        bool OriginalTall, bool ErasedTall, bool OriginalTooMany, bool ErasedTooMany);
    private sealed record Outcome(string? Text, string? FailureCode, string? FailureSeverity);
    private sealed record MaskOverlap(bool OverlapsGlyph, int GlyphPixels, int OverlappingPixels, int ErasedPixels);

    private sealed class ProviderCaptureCache : IStageCache
    {
        public Dictionary<InferenceProvider, int> Providers { get; } = [];
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken token) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken token)
        {
            if (value.Length < 6) throw new InvalidDataException("Inference response does not carry provider identity.");
            InferenceProvider provider = (InferenceProvider)value.Span[5];
            Providers[provider] = Providers.GetValueOrDefault(provider) + 1;
            return ValueTask.CompletedTask;
        }
    }
}
