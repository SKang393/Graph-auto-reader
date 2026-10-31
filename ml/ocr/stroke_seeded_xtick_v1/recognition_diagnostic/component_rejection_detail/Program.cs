// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Ocr;
using OpenCvSharp;

internal static class Program
{
    private const string PredictionsSha256 = "235d39476cf3ef498d0c026f05e4c9eb626fe43c853d0af49ab6d0bef2d54097";
    private const string ProbeManifestSha256 = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba";
    private const string V5ModelSha256 = "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84";
    private const string CorpusRoot = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3";
    private const string RecipeId = "stroke-seeded-xtick-v1-p2-v5-rejection-detail-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private delegate object EncodeCropInvoker(ReadOnlySpan<float> pixels, LocalOnnxComponentTextRecognizerOptions options);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: ComponentRejectionDetail <repo-root> <predictions.json> <report.json>");
            return 2;
        }

        try
        {
            string root = Path.GetFullPath(args[0]);
            byte[] predictionBytes = await File.ReadAllBytesAsync(Path.GetFullPath(args[1]));
            Require(Hash(predictionBytes) == PredictionsSha256, "Pinned prediction file hash changed.");
            byte[] manifestBytes = await ReadBoundedAsync(root, $"{CorpusRoot}/manifest.json", ProbeManifestSha256, 5_000_000);
            byte[] modelBytes = await ReadBoundedAsync(root,
                "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx",
                V5ModelSha256, 10_000_000);
            _ = modelBytes;

            using JsonDocument predictionDocument = JsonDocument.Parse(predictionBytes);
            JsonElement predictionRoot = predictionDocument.RootElement;
            Require(predictionRoot.GetProperty("probe_manifest_sha256").GetString() == ProbeManifestSha256,
                "Prediction probe manifest SHA mismatch.");
            Require(predictionRoot.GetProperty("v5_recognizer_onnx_sha256").GetString() == V5ModelSha256,
                "Prediction recognizer SHA mismatch.");
            Require(predictionRoot.GetProperty("training_performed").GetBoolean() == false,
                "Training must not be performed.");
            using JsonDocument manifestDocument = JsonDocument.Parse(manifestBytes);
            Dictionary<string, JsonElement> manifestRows = manifestDocument.RootElement.GetProperty("records")
                .EnumerateArray().ToDictionary(static item => item.GetProperty("case_id").GetString()!, StringComparer.Ordinal);

            Type recognizerType = typeof(LocalOnnxComponentTextRecognizer);
            MethodInfo encodeMethod = PrivateMethod(recognizerType, "EncodeCrop", 2);
            MethodInfo filterMethod = PrivateMethod(recognizerType, "FilterForeground", 3);
            MethodInfo intervalsMethod = PrivateMethod(recognizerType, "ActiveIntervals", 3);
            MethodInfo glyphMethod = PrivateMethod(recognizerType, "EncodeGlyph", 5);
            EncodeCropInvoker encodeCrop = CreateEncodeCropInvoker(encodeMethod);
            var options = new LocalOnnxComponentTextRecognizerOptions(
                new GraphReader.Inference.ModelIdentity("graph-numeric-component-ensemble-v5", "0.0.21-p1", V5ModelSha256,
                    Path.Combine(root, "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx")),
                "0123456789.-%");

            var rows = new List<CropDiagnostics>();
            foreach (JsonElement prediction in predictionRoot.GetProperty("records").EnumerateArray())
            {
                if (prediction.GetProperty("kind").GetString() != "positive" ||
                    prediction.GetProperty("top_one_score").GetDouble() < 0.25) continue;
                string caseId = prediction.GetProperty("case_id").GetString()!;
                Require(manifestRows.TryGetValue(caseId, out JsonElement sourceRow), $"Probe manifest lacks {caseId}.");
                string annotationPath = sourceRow.GetProperty("annotation_path").GetString()!;
                byte[] annotationBytes = await ReadBoundedAsync(root, $"{CorpusRoot}/{annotationPath}",
                    sourceRow.GetProperty("annotation_sha256").GetString()!, 65_536);
                using JsonDocument annotationDocument = JsonDocument.Parse(annotationBytes);
                JsonElement annotation = annotationDocument.RootElement;
                Require(annotation.GetProperty("expected_tick_label").GetString() == prediction.GetProperty("expected_text").GetString(),
                    $"Prediction target label differs from annotation {caseId}.");
                JsonElement targetArray = annotation.GetProperty("targets");
                Require(targetArray.GetArrayLength() == 1, $"Expected one target in {caseId}.");
                JsonElement targetBox = targetArray[0].GetProperty("target_crop_xyxy");
                var groundTruth = new OcrRectangle(targetBox[0].GetDouble(), targetBox[1].GetDouble(),
                    targetBox[2].GetDouble() - targetBox[0].GetDouble(), targetBox[3].GetDouble() - targetBox[1].GetDouble());
                JsonElement predBox = prediction.GetProperty("top_one_xyxy_normalized");
                var box = new OcrRectangle(predBox[0].GetDouble() * 256, predBox[1].GetDouble() * 96,
                    (predBox[2].GetDouble() - predBox[0].GetDouble()) * 256,
                    (predBox[3].GetDouble() - predBox[1].GetDouble()) * 96);
                Require(box.IsValid && groundTruth.IsValid, $"Invalid box geometry {caseId}.");
                byte[] png = await ReadBoundedAsync(root, $"{CorpusRoot}/{sourceRow.GetProperty("image_path").GetString()}",
                    sourceRow.GetProperty("image_sha256").GetString()!, 1_000_000);
                using Mat imageMat = Cv2.ImDecode(png, ImreadModes.Grayscale);
                Require(!imageMat.Empty() && imageMat.Width == 256 && imageMat.Height == 96 && imageMat.Type() == MatType.CV_8UC1,
                    $"Image contract mismatch {caseId}.");
                byte[] pixels = new byte[256 * 96];
                System.Runtime.InteropServices.Marshal.Copy(imageMat.Data, pixels, 0, pixels.Length);
                var ocrImage = new OcrImage(256, 96, 256, pixels, OcrSourceImage.Original,
                    OcrFrameTransform.Identity, CanonicalOriginalWidth: 256, CanonicalOriginalHeight: 96);
                var detectedRegion = new OcrDetectedRegion(caseId, OcrPolygon.FromRectangle(box), 0, 1.0);
                IReadOnlyList<IReadOnlyList<OcrCrop>> cropBatches = OcrCropBatcher.CreateBatches(ocrImage, [detectedRegion],
                    new OcrCropBatcherOptions
                    {
                        TargetWidth = 128, TargetHeight = 32, BatchSize = 16,
                        HorizontalPaddingPixels = 12, VerticalPaddingPixels = 1,
                        VerticalContentPaddingRatio = 0.25,
                        ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad,
                        PaddingValue = 1.0f,
                    });
                Require(cropBatches.Count == 1 && cropBatches[0].Count == 1, $"Crop output count mismatch {caseId}.");
                OcrCrop crop = cropBatches[0][0];
                Require(crop.Width == 128 && crop.Height == 32 && SameRect(crop.OriginalPolygon.Bounds, box),
                    $"Production crop geometry mismatch {caseId}.");

                object encodedCrop = encodeCrop(crop.Pixels.Span, options);
                EncodedState adapter = EncodedState.From(encodedCrop);
                byte[] raster = crop.Pixels.Span.ToArray().Select(static value => (byte)Math.Clamp(
                    (int)Math.Round(value * 255d, MidpointRounding.ToEven), 0, 255)).ToArray();
                object foregroundResult = filterMethod.Invoke(null, [raster, 128, 32])!;
                bool[] foreground = (bool[])foregroundResult.GetType().GetProperty("Pixels")!.GetValue(foregroundResult)!;
                bool ambiguous = (bool)foregroundResult.GetType().GetProperty("HasAmbiguousBaselineSingleton")!.GetValue(foregroundResult)!;
                List<(int Left, int Right)> intervals = ReadIntervals(intervalsMethod.Invoke(null, [foreground, 128, 32])!);
                var componentHeights = new List<double>();
                int noGlyphComponents = 0;
                foreach ((int left, int right) in intervals)
                {
                    object? glyph = glyphMethod.Invoke(null, [raster, foreground, left, right, options]);
                    if (glyph is null)
                    {
                        noGlyphComponents++;
                        continue;
                    }
                    componentHeights.Add(Convert.ToDouble(glyph.GetType().GetProperty("HeightRatio")!.GetValue(glyph),
                        System.Globalization.CultureInfo.InvariantCulture));
                }

                bool tallReject = componentHeights.Any(height => height >= options.StructuralRejectMinimumHeightRatio);
                bool glyphCountReject = componentHeights.Count > options.MaximumGlyphs;
                bool predictedReject = !ambiguous && (tallReject || glyphCountReject);
                Require(adapter.Rejected == predictedReject,
                    $"Recomputed tall/count flags disagree with exact EncodeCrop Rejected for {caseId}.");
                Require(adapter.Ambiguous == ambiguous, $"Ambiguous singleton flag mismatch for {caseId}.");
                Require(adapter.Count == (predictedReject ? 0 : ambiguous ? 0 : componentHeights.Count),
                    $"Encoded glyph count differs from exact component scan for {caseId}.");

                JsonElement line = annotation.GetProperty("stroke_line_original_xyxy");
                double[] matrix = annotation.GetProperty("crop_transform").GetProperty("original_to_crop_pixel")
                    .EnumerateArray().Select(static value => value.GetDouble()).ToArray();
                Require(matrix.Length == 6 && Math.Abs(matrix[1]) < 1e-9 && Math.Abs(matrix[3]) < 1e-9,
                    $"Non-axis-aligned crop transform in {caseId}; refusing approximate stroke overlap.");
                var p0 = Map(matrix, line[0].GetDouble(), line[1].GetDouble());
                var p1 = Map(matrix, line[2].GetDouble(), line[3].GetDouble());
                double horizontalPadding = 12;
                double verticalPadding = 1 + box.Height * 0.25;
                var expanded = new OcrRectangle(box.X - horizontalPadding, box.Y - verticalPadding,
                    box.Width + 2 * horizontalPadding, box.Height + 2 * verticalPadding);
                var visibleClip = Intersect(expanded, new OcrRectangle(0, 0, 256, 96));
                bool lineInExtraction = visibleClip.IsValid && SegmentIntersectsRect(p0, p1, visibleClip);
                bool lineInTruthBox = SegmentIntersectsRect(p0, p1, groundTruth);
                bool lineVisibleOnCrop = SegmentIntersectsRect(p0, p1, new OcrRectangle(0, 0, 256, 96));
                rows.Add(new CropDiagnostics(box, groundTruth, intervals.Count, componentHeights.ToArray(),
                    noGlyphComponents, ambiguous, adapter.Rejected, tallReject, glyphCountReject,
                    adapter.Count, adapter.Values.Length, lineInExtraction, lineInTruthBox, lineVisibleOnCrop));
            }

            Require(rows.Count == 165, $"Selected positive crop count changed: {rows.Count}.");
            int rejected = rows.Count(static row => row.AdapterRejected);
            int tallOnly = rows.Count(static row => row.TallReject && !row.GlyphCountReject);
            int glyphCountOnly = rows.Count(static row => !row.TallReject && row.GlyphCountReject);
            int both = rows.Count(static row => row.TallReject && row.GlyphCountReject);
            Require(rejected == 82 && tallOnly + glyphCountOnly + both == rejected,
                "Detailed component rejection partition does not reproduce the prior adapter total.");

            object Group(IEnumerable<CropDiagnostics> subset)
            {
                CropDiagnostics[] items = subset.ToArray();
                return new
                {
                    crop_count = items.Length,
                    adapter_rejected = items.Count(static row => row.AdapterRejected),
                    tall_component_reject = items.Count(static row => row.TallReject),
                    too_many_glyphs_reject = items.Count(static row => row.GlyphCountReject),
                    tall_only = items.Count(static row => row.TallReject && !row.GlyphCountReject),
                    glyph_count_only = items.Count(static row => !row.TallReject && row.GlyphCountReject),
                    both_reject_reasons = items.Count(static row => row.TallReject && row.GlyphCountReject),
                    no_component_rows = items.Count(static row => row.Intervals == 0),
                    ambiguous_singleton_rows = items.Count(static row => row.Ambiguous),
                    predicted_box_median = RectSummary(items.Select(static row => row.PredictedBox)),
                    target_box_median = RectSummary(items.Select(static row => row.TargetBox)),
                    interval_count_total = items.Sum(static row => row.Intervals),
                    encoded_component_count_total = items.Sum(static row => row.ComponentHeights.Length),
                    interval_without_glyph_encoding_total = items.Sum(static row => row.NoGlyphComponents),
                    component_height_ratio = HeightSummary(items.SelectMany(static row => row.ComponentHeights)),
                    annotated_stroke_segment_intersects_padded_extraction = items.Count(static row => row.LineInExtraction),
                    annotated_stroke_segment_intersects_target_box = items.Count(static row => row.LineInTruthBox),
                    annotated_stroke_segment_visible_in_probe_raster = items.Count(static row => row.LineVisibleOnCrop),
                };
            }

            var tallCountsByLine = rows.GroupBy(static row => row.LineInExtraction)
                .ToDictionary(static group => group.Key ? "line_intersects_padded_crop" : "line_does_not_intersect_padded_crop",
                    group => Group(group));
            string runnerPath = Path.Combine(root,
                "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/component_rejection_detail/Program.cs");
            var report = new
            {
                schema = "graphreader.stroke-seeded-xtick-v1-p2-v5-component-rejection-detail.v1",
                recipe_id = RecipeId,
                acceptance_status = "consumed synthetic diagnostic only",
                runner_sha256 = Hash(await File.ReadAllBytesAsync(runnerPath)),
                predictions_sha256 = Hash(predictionBytes),
                probe_manifest_sha256 = ProbeManifestSha256,
                v5_model_sha256 = V5ModelSha256,
                score_threshold = 0.25,
                adapter_encoder_contract = new
                {
                    maximum_glyphs = options.MaximumGlyphs,
                    structural_height_ratio_threshold = options.StructuralRejectMinimumHeightRatio,
                    production_rejected_flag_verified = true,
                    all_selected_positive_crops = rows.Count,
                    adapter_rejected_crops = rejected,
                    tall_only = tallOnly, glyph_count_only = glyphCountOnly, both = both,
                    rejected_flag_mismatches = 0,
                    ambiguous_singleton_rows = rows.Count(static row => row.Ambiguous),
                },
                all_crops = Group(rows),
                rejection_rows = Group(rows.Where(static row => row.AdapterRejected)),
                rejected_reason_partition = new { tall_only = tallOnly, glyph_count_only = glyphCountOnly, both },
                line_overlap_stratification = tallCountsByLine,
                component_height_ratio_threshold = options.StructuralRejectMinimumHeightRatio,
                extraction_box_padding = new { horizontal = 12.0, vertical_absolute = 1.0, vertical_content_fraction = 0.25 },
                stroke_overlap_definition = "The annotation's renderer stroke line segment is transformed by its checksum-bound original_to_crop_pixel affine map. Segment intersection is tested against the cropper's predicted-box padding intersected with the 256x96 raster, against the target glyph rectangle, and against the full raster. This identifies geometric overlap only; it does not infer semantic graph-line cause.",
                no_training_or_inference = true,
                dev_sealed_private_inputs_read = false,
                production_approval = false,
                release_eligible = false,
                case_level_data_retained = false,
                attribution_limit = "Exact adapter private methods were invoked by reflection. Adapter combines tall-component and over-maximum-glyph causes in its Rejected boolean; this diagnostic recomputes each exact predicate from the same private FilterForeground, ActiveIntervals and EncodeGlyph methods and asserts their OR equals Rejected for every crop.",
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

    private static bool SameRect(OcrRectangle left, OcrRectangle right) =>
        Math.Abs(left.X - right.X) < 1e-6 && Math.Abs(left.Y - right.Y) < 1e-6 &&
        Math.Abs(left.Width - right.Width) < 1e-6 && Math.Abs(left.Height - right.Height) < 1e-6;

    private static List<(int Left, int Right)> ReadIntervals(object value)
    {
        var intervals = new List<(int Left, int Right)>();
        foreach (object? item in (IEnumerable)value)
        {
            if (item is not ITuple tuple || tuple.Length != 2) throw new InvalidDataException("Active interval shape changed.");
            intervals.Add(((int)tuple[0]!, (int)tuple[1]!));
        }
        return intervals;
    }

    private static EncodeCropInvoker CreateEncodeCropInvoker(MethodInfo method)
    {
        Require(method.ReturnType.IsClass && method.GetParameters()[0].ParameterType == typeof(ReadOnlySpan<float>),
            "EncodeCrop private signature changed.");
        var dynamic = new DynamicMethod("InvokePinnedEncodeCrop", typeof(object),
            [typeof(ReadOnlySpan<float>), typeof(LocalOnnxComponentTextRecognizerOptions)], typeof(Program).Module, true);
        ILGenerator il = dynamic.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, method); il.Emit(OpCodes.Ret);
        return (EncodeCropInvoker)dynamic.CreateDelegate(typeof(EncodeCropInvoker));
    }

    private static MethodInfo PrivateMethod(Type type, string name, int parameters) =>
        type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == parameters);

    private static (double X, double Y) Map(double[] matrix, double x, double y) =>
        (matrix[0] * x + matrix[1] * y + matrix[2], matrix[3] * x + matrix[4] * y + matrix[5]);

    private static OcrRectangle Intersect(OcrRectangle left, OcrRectangle right)
    {
        double x0 = Math.Max(left.X, right.X), y0 = Math.Max(left.Y, right.Y);
        double x1 = Math.Min(left.Right, right.Right), y1 = Math.Min(left.Bottom, right.Bottom);
        return x1 > x0 && y1 > y0 ? new OcrRectangle(x0, y0, x1 - x0, y1 - y0) : default;
    }

    private static bool SegmentIntersectsRect((double X, double Y) p0, (double X, double Y) p1, OcrRectangle rect)
    {
        // Liang-Barsky segment clipping avoids bounding-box false positives for diagonal renderer strokes.
        double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
        double t0 = 0, t1 = 1;
        return Clip(-dx, p0.X - rect.Left, ref t0, ref t1) &&
               Clip(dx, rect.Right - p0.X, ref t0, ref t1) &&
               Clip(-dy, p0.Y - rect.Top, ref t0, ref t1) &&
               Clip(dy, rect.Bottom - p0.Y, ref t0, ref t1);
    }

    private static bool Clip(double p, double q, ref double t0, ref double t1)
    {
        if (p == 0) return q >= 0;
        double ratio = q / p;
        if (p < 0)
        {
            if (ratio > t1) return false;
            if (ratio > t0) t0 = ratio;
        }
        else
        {
            if (ratio < t0) return false;
            if (ratio < t1) t1 = ratio;
        }
        return true;
    }

    private static object RectSummary(IEnumerable<OcrRectangle> rects)
    {
        OcrRectangle[] values = rects.ToArray();
        return new
        {
            count = values.Length,
            width = NumberSummary(values.Select(static value => value.Width)),
            height = NumberSummary(values.Select(static value => value.Height)),
            area = NumberSummary(values.Select(static value => value.Width * value.Height)),
        };
    }

    private static object HeightSummary(IEnumerable<double> values) => NumberSummary(values);

    private static object NumberSummary(IEnumerable<double> source)
    {
        double[] values = source.Order().ToArray();
        double At(double q) => values.Length == 0 ? 0 : values[(int)Math.Clamp(Math.Ceiling(q * values.Length) - 1, 0, values.Length - 1)];
        return new
        {
            count = values.Length,
            mean = values.Length == 0 ? 0 : values.Average(),
            min = values.Length == 0 ? 0 : values[0],
            median = At(0.5), p90 = At(0.9), p95 = At(0.95), max = values.Length == 0 ? 0 : values[^1],
        };
    }

    private static async Task<byte[]> ReadBoundedAsync(string root, string relative, string expectedSha, int maximum)
    {
        string projectRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(path.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase), "Input path escapes project root.");
        var file = new FileInfo(path);
        Require(file.Exists && file.Length is > 0 && file.Length <= maximum, $"Invalid input size: {relative}.");
        for (FileSystemInfo? current = file; current is not null &&
             !string.Equals(current.FullName, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase); current = current switch
             {
                 FileInfo info => info.Directory,
                 DirectoryInfo directory => directory.Parent,
                 _ => null,
             })
            Require((current.Attributes & FileAttributes.ReparsePoint) == 0, $"Reparse point in input path: {relative}.");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Require(Hash(bytes) == expectedSha, $"Input hash mismatch: {relative}.");
        return bytes;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record EncodedState(float[] Values, int Count, bool Rejected, bool Ambiguous)
    {
        public static EncodedState From(object value)
        {
            Type type = value.GetType();
            return new EncodedState(
                (float[]?)type.GetProperty("Values")?.GetValue(value) ?? throw new InvalidDataException("No values."),
                (int)(type.GetProperty("Count")?.GetValue(value) ?? -1),
                (bool)(type.GetProperty("Rejected")?.GetValue(value) ?? false),
                (bool)(type.GetProperty("HasAmbiguousBaselineSingleton")?.GetValue(value) ?? false));
        }
    }

    private sealed record CropDiagnostics(OcrRectangle PredictedBox, OcrRectangle TargetBox,
        int Intervals, double[] ComponentHeights, int NoGlyphComponents, bool Ambiguous,
        bool AdapterRejected, bool TallReject, bool GlyphCountReject, int AdapterCount, int AdapterValueCount,
        bool LineInExtraction, bool LineInTruthBox, bool LineVisibleOnCrop);
}
