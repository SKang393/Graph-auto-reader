// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Ocr;
using Microsoft.ML.OnnxRuntime;

namespace GraphReader.CalibrationOcrProbe;

internal static class OcrBatchPaddingCheck
{
    private static readonly string[][] Glyphs =
    [
        ["01100", "11100", "01100", "01100", "01100", "01100", "11110"],
        ["11110", "00001", "00001", "01110", "10000", "10000", "11111"],
        ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
        ["10010", "10010", "10010", "11111", "00010", "00010", "00010"],
        ["11111", "10000", "10000", "11110", "00001", "00001", "11110"],
        ["01111", "10000", "10000", "11110", "10001", "10001", "01110"],
        ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
    ];

    internal static Task<object> RunAsync(string assetRoot, CpuBudgetJob budget, CancellationToken token) =>
        RunCoreAsync(assetRoot, budget, null, null, token);

    internal static Task<object> RunGpuAsync(string assetRoot, int index, long luid, CpuBudgetJob budget, CancellationToken token)
    {
        var inventory = new WindowsDxgiAdapterInventory();
        DxgiAdapter adapter = inventory.Enumerate().Single(item => item.Index == index && item.Luid == luid && !item.IsSoftware);
        var selection = new DirectMlAdapterSelection(index, luid);
        RequireGpuHeadroom(inventory, selection);
        return RunCoreAsync(assetRoot, budget, selection, adapter.Name, token);
    }

    private static async Task<object> RunCoreAsync(string assetRoot, CpuBudgetJob budget,
        DirectMlAdapterSelection? selection, string? adapterName, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(assetRoot) || !Directory.Exists(assetRoot))
            throw new InvalidDataException("An existing absolute reviewed asset root is required.");
        string model = Path.Combine(assetRoot, "en_PP-OCRv5_mobile_rec.onnx");
        string manifest = Path.Combine(assetRoot, "recognizer.json");
        Program.RequireHash(Program.ReadBounded(model, 64 * 1024 * 1024), Program.ModelHash);
        Program.RequireHash(Program.ReadBounded(manifest, 128 * 1024), Program.ManifestHash);
        Program.RequireHash(Program.ReadBounded(Path.Combine(assetRoot, "PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt"), 128 * 1024), "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4");
        Program.RequireHash(Program.ReadBounded(Path.Combine(assetRoot, "PaddlePaddle-PP-OCRv5-Models-Notice.txt"), 128 * 1024), "8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729");
        var (image, regions, fixtureHash) = CreateFixture();
        var identity = new ModelIdentity("en_PP-OCRv5_mobile_rec", "5.0.0", Program.ModelHash, model);
        var options = ProductionOcrAdapter.ReadRecognitionOptions(identity, manifest);
        var cropOptions = new OcrCropBatcherOptions
        {
            TargetWidth = options.Pipeline.CropWidth, TargetHeight = options.Pipeline.CropHeight,
            WidthMode = options.Pipeline.CropWidthMode, MaximumTargetWidth = options.Pipeline.MaximumCropWidth,
            BatchSize = options.Pipeline.BatchSize, PaddingPixels = options.Pipeline.CropPaddingPixels,
            HorizontalPaddingPixels = options.Pipeline.CropHorizontalPaddingPixels,
            VerticalPaddingPixels = options.Pipeline.CropVerticalPaddingPixels,
            VerticalContentPaddingRatio = options.Pipeline.CropVerticalContentPaddingRatio,
            ResizeMode = options.Pipeline.CropResizeMode, PaddingValue = options.Pipeline.CropPaddingValue,
        };
        if (cropOptions.WidthMode != OcrCropWidthMode.PaddleBatchMaximumAspectRatio)
            throw new InvalidDataException("The reviewed reader does not use dynamic-width crops.");
        var baseline = OcrCropBatcher.CreateBatches(image, regions, cropOptions, token);
        var bounded = OcrCropBatcher.CreateBatches(image, regions, cropOptions with { MaximumPaddingOverhead = 2 }, token);
        string[] expected = regions.Select(region => region.RegionId).Order(StringComparer.Ordinal).ToArray();
        if (!baseline.SelectMany(batch => batch).Select(crop => crop.RegionId).Order(StringComparer.Ordinal).SequenceEqual(expected) ||
            !bounded.SelectMany(batch => batch).Select(crop => crop.RegionId).Order(StringComparer.Ordinal).SequenceEqual(expected))
            throw new InvalidDataException("Crop population changed.");
        Run baselineRun = await RecognizeAsync(identity, manifest, options.Recognizer, baseline, selection, token);
        Run boundedRun = await RecognizeAsync(identity, manifest, options.Recognizer, bounded, selection, token);
        var comparisons = baselineRun.Rounds.Zip(boundedRun.Rounds).Select((pair, round) => new
        {
            round,
            rows = pair.First.Rows.Zip(pair.Second.Rows).Select(row => new { row.First.Id,
                same_text = row.First.Text == row.Second.Text, same_numeric_value = row.First.Value == row.Second.Value,
                same_failure = row.First.Failure == row.Second.Failure,
                same_geometry = row.First.Polygon.SequenceEqual(row.Second.Polygon),
                confidence_delta = row.Second.Confidence - row.First.Confidence }).ToArray(),
        }).ToArray();
        bool matches = comparisons.All(round => round.rows.All(row => row.same_text && row.same_numeric_value && row.same_failure && row.same_geometry));
        if (selection is not null)
            return new { schema = "graphreader.ocr-padding-owned-gpu-comparison.v1", review_only = true, production_approved = false,
                fixture_sha256 = fixtureHash, model_sha256 = Program.ModelHash, manifest_sha256 = Program.ManifestHash,
                cpu_budget = new { budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores, priority = "BelowNormal" },
                provider = "DirectMl", adapter_index = selection.Index, adapter_luid = selection.Luid, adapter_name = adapterName,
                intra_op_threads = 1, inter_op_threads = 1, padding_factor = 2, baseline = baselineRun, bounded = boundedRun,
                comparisons, all_compared_fields_match = matches,
                note = "DirectML session may execute CPU kernels; pacing is not a GPU utilization cap. Owned input only; no accuracy claim." };
        return new { schema = "graphreader.ocr-padding-owned-comparison.v1", review_only = true, production_approved = false,
            fixture_sha256 = fixtureHash, model_sha256 = Program.ModelHash, manifest_sha256 = Program.ManifestHash,
            cpu_budget = new { budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores, priority = "BelowNormal" },
            provider = "Cpu", intra_op_threads = 1, inter_op_threads = 1, padding_factor = 2,
            baseline = baselineRun, bounded = boundedRun, comparisons, all_compared_fields_match = matches,
            note = "Owned input and one fixed reader only; no production activation or accuracy claim." };
    }

    private static (OcrImage Image, OcrDetectedRegion[] Regions, string Hash) CreateFixture()
    {
        const int width = 1600, height = 160;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int i = 0; i < Glyphs.Length; i++) DrawGlyph(pixels, width, 20 + i * 48, 20, Glyphs[i]);
        for (int x = 20; x < 1420; x += 18) DrawGlyph(pixels, width, x, 90, Glyphs[(x / 18) % Glyphs.Length]);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width)));
        using var output = new MemoryStream(); encoder.Save(output);
        byte[] bytes = output.ToArray();
        var (image, _, _) = Program.Decode(bytes, [0, 0, width, height]);
        var regions = Enumerable.Range(0, 7).Select(i => Region($"tick-{i}", 20 + i * 48, 20, 15, 21))
            .Append(Region("wide-caption", 20, 90, 1400, 21)).ToArray();
        return (image, regions, Program.Hash(bytes));
    }

    private static OcrDetectedRegion Region(string id, int x, int y, int width, int height) =>
        new(id, OcrPolygon.FromRectangle(new OcrRectangle(x, y, width, height)), 0, 1);

    private static void DrawGlyph(byte[] pixels, int stride, int x, int y, string[] glyph)
    {
        for (int row = 0; row < 7; row++) for (int col = 0; col < 5; col++)
            if (glyph[row][col] == '1')
                for (int dy = 0; dy < 3; dy++) for (int dx = 0; dx < 3; dx++)
                    pixels[(y + row * 3 + dy) * stride + x + col * 3 + dx] = 0;
    }

    private static async Task<Run> RecognizeAsync(ModelIdentity identity, string manifest,
        LocalOnnxTextRecognizerOptions recognizerOptions, IReadOnlyList<IReadOnlyList<OcrCrop>> batches,
        DirectMlAdapterSelection? selection, CancellationToken token)
    {
        var inventory = new WindowsDxgiAdapterInventory();
        if (selection is not null) RequireGpuHeadroom(inventory, selection);
        OnnxInferenceSessionFactory factory = selection is null
            ? new(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled)
            : new(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled, OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR, selection, inventory);
        IInferenceSessionFactory guarded = selection is null ? factory : new NoCpuFactory(factory);
        IReadOnlyList<InferenceProvider> allowed = selection is null ? [InferenceProvider.Cpu] : [InferenceProvider.DirectMl, InferenceProvider.Cpu];
        var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(),
            guarded, CpuThreadConfiguration.Create(1));
        await using var runtime = new InferenceRuntime(registry, new BoundedInferenceScheduler(1, 1), new Program.NoCache(),
            allowedProviders: allowed);
        var startup = Stopwatch.StartNew();
        SessionAcquisition acquired = await registry.GetOrCreateAsync(identity, allowed, token);
        if (!acquired.Succeeded || acquired.Session!.Provider != (selection is null ? InferenceProvider.Cpu : InferenceProvider.DirectMl))
            throw new InvalidOperationException("Requested reader session unavailable; no CPU substitution is accepted.");
        startup.Stop();
        ITextRecognizer recognizer = selection is null
            ? new LocalOnnxTextRecognizer(runtime, recognizerOptions with { BypassCache = true })
            : new LocalOnnxTextRecognizer(runtime, recognizerOptions with { BypassCache = true, AllowedProviders = allowed });
        if (ProductionOcrAdapter.UsesOfficialRecognitionSpacingV2Manifest(manifest))
            recognizer = new OfficialRecognitionSpacingV2TextRecognizer(recognizer);
        var rounds = new List<Round>();
        for (int round = 0; round < 4; round++)
        {
            var timer = Stopwatch.StartNew();
            double pacingMilliseconds = 0, recognitionMilliseconds = 0;
            var readings = new List<OcrRecognition>();
            foreach (var batch in batches)
            {
                if (selection is not null) RequireGpuHeadroom(inventory, selection);
                var batchTimer = Stopwatch.StartNew();
                readings.AddRange(await recognizer.RecognizeBatchAsync(batch, token));
                batchTimer.Stop();
                recognitionMilliseconds += batchTimer.Elapsed.TotalMilliseconds;
                if (selection is not null)
                {
                    var pacing = Stopwatch.StartNew();
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(batchTimer.Elapsed.TotalMilliseconds)), token);
                    pacing.Stop();
                    pacingMilliseconds += pacing.Elapsed.TotalMilliseconds;
                }
            }
            timer.Stop();
            OcrCrop[] crops = batches.SelectMany(batch => batch).OrderBy(crop => crop.RegionId, StringComparer.Ordinal).ToArray();
            if (readings.Count != crops.Length || readings.Select(row => row.RegionId).Distinct(StringComparer.Ordinal).Count() != crops.Length)
                throw new InvalidDataException("Reader population changed.");
            var byId = readings.ToDictionary(row => row.RegionId, StringComparer.Ordinal);
            Row[] rows = crops.Select(crop => MakeRow(crop, byId[crop.RegionId])).ToArray();
            rounds.Add(new(selection is null ? timer.Elapsed.TotalMilliseconds : recognitionMilliseconds, rows)
                { PacingMilliseconds = selection is null ? null : pacingMilliseconds });
        }
        Batch[] dimensions = batches.Select(batch => new Batch(batch.Count, batch[0].Width, batch[0].Height,
            (long)batch.Count * 3 * batch[0].Width * batch[0].Height)).ToArray();
        return new(startup.Elapsed.TotalMilliseconds, dimensions, dimensions.Sum(batch => batch.TensorElements), rounds.ToArray());
    }

    private static Row MakeRow(OcrCrop crop, OcrRecognition recognition)
    {
        OcrRecognitionAlternative? choice = recognition.Alternatives.Count > 0 ? recognition.Alternatives[0] : null;
        string text = choice?.Text ?? string.Empty;
        double? value = recognition.Failure is null && GraphNumericParser.IsLiteralGraphNumber(text)
            ? GraphNumericParser.Parse(text).Value : null;
        return new(crop.RegionId, text, value, recognition.Failure?.Code, choice?.Confidence ?? 0,
            crop.OriginalPolygon.Points.ToArray());
    }

    private sealed record Batch(int Count, int Width, int Height, long TensorElements);
    private sealed record Row(string Id, string Text, double? Value, string? Failure, double Confidence, OcrPoint[] Polygon);
    private sealed record Round(double RecognitionMilliseconds, Row[] Rows)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? PacingMilliseconds { get; init; }
    }
    private sealed record Run(double InitializationMilliseconds, Batch[] Batches, long TensorElements, Round[] Rounds);

    private static void RequireGpuHeadroom(WindowsDxgiAdapterInventory inventory, DirectMlAdapterSelection selection)
    {
        if (!inventory.Enumerate().Any(item => item.Index == selection.Index && item.Luid == selection.Luid && !item.IsSoftware))
            throw new InvalidOperationException("The selected DirectML adapter is unavailable.");
        DxgiLocalMemoryBudget? budget = WindowsDxgiAdapterInventory.TryReadLocalMemoryBudget(selection);
        const ulong required = 256UL * 1024 * 1024;
        if (budget is null || budget.CurrentUsageBytes > budget.BudgetBytes || budget.BudgetBytes - budget.CurrentUsageBytes < required)
            throw new InvalidOperationException("The selected adapter's process memory budget has insufficient or unavailable headroom.");
    }

    private sealed class NoCpuFactory(IInferenceSessionFactory inner) : IInferenceSessionFactory, IInferenceAdapterSelection
    {
        public string? DirectMlSelectionIdentity => ((IInferenceAdapterSelection)inner).DirectMlSelectionIdentity;
        public void ValidateDirectMlSelection() => ((IInferenceAdapterSelection)inner).ValidateDirectMlSelection();
        public ValueTask<IInferenceSession> CreateAsync(ModelIdentity model, InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration, CancellationToken cancellationToken) => provider == InferenceProvider.Cpu
            ? ValueTask.FromException<IInferenceSession>(new InvalidOperationException("CPU session fallback is disabled for this diagnostic."))
            : inner.CreateAsync(model, provider, cpuConfiguration, cancellationToken);
    }
}
