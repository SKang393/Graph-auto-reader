// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.Inference;
using GraphReader.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static class CalibrationOrientationReadingCheck
{
    private sealed record Control(string Name, OcrImage Image, string[] Expected);
    private sealed record Prepared(Control Control, OcrDetectedRegion[] Before, OcrDetectedRegion[] After,
        IReadOnlyList<IReadOnlyList<OcrCrop>> Baseline, IReadOnlyList<IReadOnlyList<OcrCrop>> Observed);

    internal static async Task<object> RunAsync(string assetRoot, CpuBudgetJob budget, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(assetRoot)) throw new InvalidDataException("Absolute reviewed asset root required.");
        string model = Path.Combine(assetRoot, "en_PP-OCRv5_mobile_rec.onnx");
        string manifest = Path.Combine(assetRoot, "recognizer.json");
        Program.RequireHash(Program.ReadBounded(model, 64 * 1024 * 1024), Program.ModelHash);
        Program.RequireHash(Program.ReadBounded(manifest, 131_072), Program.ManifestHash);
        Program.RequireHash(Program.ReadBounded(Path.Combine(assetRoot, "PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt"), 131_072),
            "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4");
        Program.RequireHash(Program.ReadBounded(Path.Combine(assetRoot, "PaddlePaddle-PP-OCRv5-Models-Notice.txt"), 131_072),
            "8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729");
        Control[] controls = CreateControls(); // WPF rendering precedes the first asynchronous continuation.
        var identity = new ModelIdentity("en_PP-OCRv5_mobile_rec", "5.0.0", Program.ModelHash, model);
        var options = ProductionOcrAdapter.ReadRecognitionOptions(identity, manifest);
        var crops = new OcrCropBatcherOptions
        {
            TargetWidth = options.Pipeline.CropWidth, TargetHeight = options.Pipeline.CropHeight,
            WidthMode = options.Pipeline.CropWidthMode, MaximumTargetWidth = options.Pipeline.MaximumCropWidth,
            BatchSize = options.Pipeline.BatchSize, PaddingPixels = options.Pipeline.CropPaddingPixels,
            HorizontalPaddingPixels = options.Pipeline.CropHorizontalPaddingPixels,
            VerticalPaddingPixels = options.Pipeline.CropVerticalPaddingPixels,
            VerticalContentPaddingRatio = options.Pipeline.CropVerticalContentPaddingRatio,
            ResizeMode = options.Pipeline.CropResizeMode, PaddingValue = options.Pipeline.CropPaddingValue,
            MaximumPaddingOverhead = options.Pipeline.CropWidthMode == OcrCropWidthMode.PaddleBatchMaximumAspectRatio ? 2 : null,
        };
        var prepared = new List<Prepared>();
        foreach (Control control in controls)
        {
            OcrDetectedRegion[] before = (await new ConnectedComponentTextRegionDetector().DetectAsync(control.Image, token))
                .OrderBy(row => row.Polygon.Bounds.Left).ToArray();
            OcrDetectedRegion[] after = (await new ConnectedComponentTextRegionDetector(new()
                { UseObservedVerticalGroupingOrientation = true }).DetectAsync(control.Image, token))
                .OrderBy(row => row.Polygon.Bounds.Left).ToArray();
            if (before.Length != control.Expected.Length || after.Length != before.Length ||
                !before.Zip(after).All(pair => pair.First.RegionId == pair.Second.RegionId &&
                    pair.First.Polygon.Points.SequenceEqual(pair.Second.Polygon.Points)))
                throw new InvalidDataException("Fixed owned glyph population or geometry differs; no rendering retry.");
            var baseline = OcrCropBatcher.CreateBatches(control.Image, before, crops, token);
            var observed = OcrCropBatcher.CreateBatches(control.Image, after, crops, token);
            if (control.Name == "rotated_777" && !baseline.SelectMany(batch => batch).OrderBy(row => row.RegionId, StringComparer.Ordinal)
                .Zip(observed.SelectMany(batch => batch).OrderBy(row => row.RegionId, StringComparer.Ordinal))
                .All(pair => pair.First.CropSha256 == pair.Second.CropSha256))
                throw new InvalidDataException("Genuine rotation safety crop changed.");
            prepared.Add(new(control, before, after, baseline, observed));
        }

        var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(),
            new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled), CpuThreadConfiguration.Create(1));
        await using var runtime = new InferenceRuntime(registry, new BoundedInferenceScheduler(1, 1), new Program.NoCache(),
            allowedProviders: [InferenceProvider.Cpu]);
        var total = Stopwatch.StartNew();
        var initialization = Stopwatch.StartNew();
        SessionAcquisition acquired = await registry.GetOrCreateAsync(identity, [InferenceProvider.Cpu], token);
        if (!acquired.Succeeded || acquired.Session!.Provider != InferenceProvider.Cpu || registry.CreatedSessionCount != 1)
            throw new InvalidDataException("Reviewed CPU session unavailable.");
        initialization.Stop();
        ITextRecognizer recognizer = new LocalOnnxTextRecognizer(runtime, options.Recognizer with
            { BypassCache = true, AllowedProviders = [InferenceProvider.Cpu] });
        if (ProductionOcrAdapter.UsesOfficialRecognitionSpacingV2Manifest(manifest))
            recognizer = new OfficialRecognitionSpacingV2TextRecognizer(recognizer);
        var outcomes = new List<object>();
        foreach (Prepared item in prepared)
        {
            var passes = new List<object>();
            foreach ((string name, OcrDetectedRegion[] regions, IReadOnlyList<IReadOnlyList<OcrCrop>> batches) in new[]
            { ("default", item.Before, item.Baseline), ("observed_grouping", item.After, item.Observed) })
            {
                var timer = Stopwatch.StartNew();
                var readings = new List<OcrRecognition>();
                foreach (IReadOnlyList<OcrCrop> batch in batches)
                    readings.AddRange(await recognizer.RecognizeBatchAsync(batch, token));
                timer.Stop();
                if (readings.Count != regions.Length || readings.Select(row => row.RegionId).Distinct(StringComparer.Ordinal).Count() != regions.Length)
                    throw new InvalidDataException("Reader population differs.");
                var byId = readings.ToDictionary(row => row.RegionId, StringComparer.Ordinal);
                var byCrop = batches.SelectMany(batch => batch).ToDictionary(row => row.RegionId, StringComparer.Ordinal);
                object[] rows = regions.Select((region, index) =>
                {
                    OcrRecognition reading = byId[region.RegionId];
                    OcrRecognitionAlternative? choice = reading.Alternatives.Count > 0 ? reading.Alternatives[0] : null;
                    string text = choice?.Text ?? "";
                    bool literal = reading.Failure is null && CalibrationNumericLiteral.IsLiteral(text);
                    return (object)new { expected = item.Control.Expected[index], region.RegionId, polygon = region.Polygon.Points,
                        region.OrientationDegrees, crop_sha256 = byCrop[region.RegionId].CropSha256, text,
                        numeric_value = literal ? GraphNumericParser.Parse(text).Value : null, numeric_literal = literal,
                        failure = reading.Failure?.Code, confidence = choice?.Confidence ?? 0 };
                }).ToArray();
                passes.Add(new { mode = name, rows, recognition_milliseconds = timer.Elapsed.TotalMilliseconds,
                    batch_count = batches.Count, session_creation_count = registry.CreatedSessionCount });
            }
            outcomes.Add(new { control = item.Control.Name, image_width = item.Control.Image.Width, image_height = item.Control.Image.Height,
                gray_pixel_sha256 = Program.Hash(item.Control.Image.Pixels.ToArray()), same_source_boxes = true, passes });
        }
        return new { schema = "graphreader.owned-orientation-reading-check.v1", review_only = true, production_approved = false,
            private_payload_reads = 0, image_writes = false, font = "Segoe UI", font_size = 16,
            model_sha256 = Program.ModelHash, manifest_sha256 = Program.ManifestHash, provider = "Cpu",
            cpu_budget = new { budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores, verified = true, priority = "BelowNormal" },
            intra_op_threads = 1, inter_op_threads = 1, worker_count = 1, registry.CreatedSessionCount,
            cold_initialization_milliseconds = initialization.Elapsed.TotalMilliseconds, total_inference_milliseconds = total.Elapsed.TotalMilliseconds,
            assemblies = new[] { typeof(Program).Assembly, typeof(ProductionOcrAdapter).Assembly, typeof(LocalOnnxTextRecognizer).Assembly,
                typeof(InferenceRuntime).Assembly }.Select(assembly => new { name = assembly.GetName().Name, sha256 = FileHash(assembly.Location) }).ToArray(),
            native = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Where(module => string.Equals(module.ModuleName, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase))
                .Select(module => new { name = module.ModuleName, sha256 = FileHash(module.FileName) }).ToArray(), outcomes,
            limitation = "Fixed owned reading observations only; no representative accuracy or steady-state latency claim." };
    }

    private static Control[] CreateControls()
    {
        string[] digits = Enumerable.Range(1, 9).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray();
        byte[] upright = Render(900, 160, digits, 80);
        byte[] three = Render(120, 64, ["7", "7", "7"], 13);
        int left = 120, top = 64, right = -1, bottom = -1;
        for (int y = 0; y < 64; y++) for (int x = 0; x < 120; x++)
            if (three[y * 120 + x] < 255)
            { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        if (right < left) throw new InvalidDataException("Fixed glyphs did not render.");
        byte[] rotated = Enumerable.Repeat((byte)255, 256 * 256).ToArray();
        for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
            rotated[(30 + x - left) * 256 + 30 + bottom - y] = three[y * 120 + x];
        return [new("upright_digits_1_to_9", Image(900, 160, upright), digits), new("rotated_777", Image(256, 256, rotated), ["777"])];
    }

    private static byte[] Render(int width, int height, string[] texts, int spacing)
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int i = 0; i < texts.Length; i++)
                drawing.DrawText(new FormattedText(texts[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 16, Brushes.Black, 1), new Point(30 + spacing * i, 20));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
        byte[] pixels = new byte[width * height]; gray.CopyPixels(pixels, width, 0); return pixels;
    }

    private static OcrImage Image(int width, int height, byte[] pixels)
    {
        byte[] bgr = new byte[pixels.Length * 3];
        for (int i = 0; i < pixels.Length; i++) bgr[i * 3] = bgr[i * 3 + 1] = bgr[i * 3 + 2] = pixels[i];
        return new(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity, BgrPixels: new(width * 3, bgr));
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
