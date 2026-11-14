// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.Inference;
using GraphReader.Ocr;
using Microsoft.ML.OnnxRuntime;

namespace GraphReader.App.Integration.CalibrationOcr;

internal sealed class CalibrationOcrSession : IAsyncDisposable
{
    internal const string ModelSha256 = "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743";
    internal const string ManifestSha256 = "17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a";
    private readonly CalibrationOcrCpuBudget budget;
    private readonly Func<CancellationToken, Task<CalibrationOcrReader>> readerFactory;
    private readonly CalibrationOcrDeviceSelection deviceSelection;
    private readonly Func<string, CancellationToken, Task<CalibrationOcrReader>>? fallbackFactory;
    private readonly Func<string?>? adapterCheck;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CalibrationOcrReader? reader;
    private int sessionCreationCount;
    private bool disposed;

    internal CalibrationOcrSession(string assetRoot, CalibrationOcrCpuBudget budget,
        CalibrationOcrDeviceSelection? deviceSelection = null)
    {
        this.budget = budget;
        this.deviceSelection = deviceSelection ?? CalibrationOcrDeviceSelection.Cpu;
        readerFactory = token => CreateReaderAsync(assetRoot, this.deviceSelection, null, RecordSessionCreation, token);
        fallbackFactory = (reason, token) => CreateReaderAsync(assetRoot, CalibrationOcrDeviceSelection.Cpu, reason, RecordSessionCreation, token);
        adapterCheck = () => CheckAdapter(this.deviceSelection).Reason;
    }

    internal CalibrationOcrSession(CalibrationOcrCpuBudget budget,
        Func<CancellationToken, Task<CalibrationOcrReader>> readerFactory,
        CalibrationOcrDeviceSelection? selection = null,
        Func<string, CancellationToken, Task<CalibrationOcrReader>>? fallback = null,
        Func<string?>? checkAdapter = null)
    {
        this.budget = budget;
        this.readerFactory = async token => { CalibrationOcrReader value = await readerFactory(token).ConfigureAwait(false); RecordSessionCreation(); return value; };
        deviceSelection = selection ?? CalibrationOcrDeviceSelection.Cpu;
        fallbackFactory = fallback is null ? null : async (reason, token) =>
        { CalibrationOcrReader value = await fallback(reason, token).ConfigureAwait(false); RecordSessionCreation(); return value; };
        adapterCheck = checkAdapter;
    }

    internal async Task<CalibrationOcrResponse> RecognizeAsync(CalibrationOcrWorkItem item, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var total = Stopwatch.StartNew();
            Validate(item);
            var before = MemorySnapshot();
            bool reused = reader is not null;
            var stage = Stopwatch.StartNew();
            OcrImage image = Decode(item, token);
            double decode = stage.Elapsed.TotalMilliseconds;
            CalibrationOcrPlot plot = item.Request.Plot;
            var bounds = new OcrRectangle(plot.Left, plot.Top, plot.Right - plot.Left, plot.Bottom - plot.Top);

            stage.Restart();
            IReadOnlyList<OcrDetectedRegion> components = await new ConnectedComponentTextRegionDetector()
                .DetectAsync(image, item.Request.MeasuredXTicks is { Length: >= 2 } ticks
                    ? new OcrMeasuredTickGrouping(ticks, item.Request.Plot.Bottom) : null, token).ConfigureAwait(false);
            OcrDetectedRegion[] candidates = components.Where(region => AxisFor(region, bounds) is "X" or "Y").ToArray();
            if (candidates.Length > 128) throw new CalibrationOcrException(CalibrationOcrReasonCodes.TooManyCandidates);
            double component = stage.Elapsed.TotalMilliseconds;

            stage.Restart();
            if (reader is { Provider: "DirectMl" } && fallbackFactory is not null && adapterCheck?.Invoke() is { } unavailable)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                reader = await fallbackFactory(unavailable, token).ConfigureAwait(false);
                reused = false;
            }
            reader ??= await readerFactory(token).ConfigureAwait(false);
            double initialization = reused ? 0 : stage.Elapsed.TotalMilliseconds;
            OcrPipelineOptions options = reader.Options;
            var cropOptions = new OcrCropBatcherOptions
            {
                TargetWidth = options.CropWidth,
                TargetHeight = options.CropHeight,
                WidthMode = options.CropWidthMode,
                MaximumTargetWidth = options.MaximumCropWidth,
                BatchSize = options.BatchSize,
                // Split dynamic batches at twice the minimum allocation needed
                // for their preserved content widths. Sampling stays unchanged;
                // only excess right padding is removed within original batches.
                MaximumPaddingOverhead = options.CropWidthMode == OcrCropWidthMode.PaddleBatchMaximumAspectRatio
                    ? 2d : null,
                PaddingPixels = options.CropPaddingPixels,
                HorizontalPaddingPixels = options.CropHorizontalPaddingPixels,
                VerticalPaddingPixels = options.CropVerticalPaddingPixels,
                VerticalContentPaddingRatio = options.CropVerticalContentPaddingRatio,
                ResizeMode = options.CropResizeMode,
                PaddingValue = options.CropPaddingValue,
            };
            stage.Restart();
            IReadOnlyList<IReadOnlyList<OcrCrop>> batches = OcrCropBatcher.CreateBatches(image, candidates, cropOptions, token);
            double crop = stage.Elapsed.TotalMilliseconds;
            List<OcrRecognition> readings;
            var attemptTiming = new RecognitionAttemptTiming();
            bool gpuFailed = false;
            try { readings = await RecognizeBatchesAsync(reader, batches, attemptTiming, token).ConfigureAwait(false); }
            catch (Exception error) when (reader.Provider == "DirectMl" && fallbackFactory is not null &&
                error is not OperationCanceledException and not OutOfMemoryException)
            { readings = []; gpuFailed = true; }
            if (reader.Provider == "DirectMl" && fallbackFactory is not null && (gpuFailed || readings.Any(row => row.Failure?.Code is
                    "INFERENCE_FAILED" or "INFERENCE_PROVIDER_UNAVAILABLE" or "INFERENCE_TIMEOUT" or "OCR_INFERENCE_FAILED")))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                stage.Restart();
                reader = await fallbackFactory("CALIBRATION_OCR_DIRECTML_INFERENCE_FAILED", token).ConfigureAwait(false);
                initialization += stage.Elapsed.TotalMilliseconds;
                reused = false;
                readings = await RecognizeBatchesAsync(reader, batches, attemptTiming, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (readings.Count != candidates.Length || readings.Select(row => row.RegionId).Distinct(StringComparer.Ordinal).Count() != candidates.Length)
                throw new CalibrationOcrException(CalibrationOcrReasonCodes.RecognitionFailed);
            var byId = readings.ToDictionary(row => row.RegionId, StringComparer.Ordinal);
            if (candidates.Any(candidate => !byId.ContainsKey(candidate.RegionId)))
                throw new CalibrationOcrException(CalibrationOcrReasonCodes.RecognitionFailed);
            CalibrationOcrProposal[] proposals = candidates.Select(candidate => Proposal(candidate, byId[candidate.RegionId], bounds)).ToArray();
            var after = MemorySnapshot();
            var memory = new CalibrationOcrProcessMemory(before.Private, after.Private, before.Working, after.Working, before.Managed, after.Managed);
            return new(item.Request, proposals, CheckSequence(proposals, "X"), CheckSequence(proposals, "Y"),
                new CalibrationOcrTimings(decode, component, crop, initialization, attemptTiming.Recognition, total.Elapsed.TotalMilliseconds, reused)
                    { PacingMilliseconds = attemptTiming.Pacing },
                new CalibrationOcrRuntime(ModelSha256, ManifestSha256, reader.Provider, 1, 1, 1, sessionCreationCount, false, budget, memory, reader.RuntimeFiles)
                { RequestedProvider = deviceSelection.Provider, AdapterIndex = reader.Adapter?.Index, AdapterLuid = reader.Adapter?.Luid,
                    AdapterName = reader.AdapterName, FallbackReason = reader.FallbackReason, PacingRatio = reader.Provider == "DirectMl" ? 1 : 0 }, null);
        }
        finally { gate.Release(); }
    }

    internal static CalibrationOcrSequenceCheck CheckSequence(IReadOnlyList<CalibrationOcrProposal> proposals, string axis)
    {
        CalibrationOcrProposal[] numeric = proposals.Where(row => row.SuggestedAxis == axis && row.NumericLiteral &&
            row.NumericValue is not null && row.FailureCode is null && row.Role == (axis == "X" ? "XTick" : "YTick")).ToArray();
        TickCandidate[] ticks = numeric.Select(row => new TickCandidate(row.RegionId,
            axis == "X" ? row.Polygon.Average(point => point.X) : row.Polygon.Average(point => point.Y),
            row.NumericValue!.Value, row.Confidence)).ToArray();
        TickResolutionResult check = MonotonicTickResolver.Resolve(ticks,
            axis == "X" ? TickAxisDirection.IncreasingWithPixels : TickAxisDirection.DecreasingWithPixels);
        var reasons = new List<string>();
        if (check.ResolvedTicks.Count < 2) reasons.Add(CalibrationOcrReasonCodes.SequenceInsufficient);
        if (check.RejectedTicks.Count > 0) reasons.Add(CalibrationOcrReasonCodes.SequenceConflict);
        if (check.NeedsReview && check.ResolvedTicks.Count >= 2 && check.RejectedTicks.Count == 0)
            reasons.Add(CalibrationOcrReasonCodes.SequenceSpacing);
        if (proposals.Any(row => row.SuggestedAxis == axis && row.FailureCode is not null))
            reasons.Add(CalibrationOcrReasonCodes.RecognitionFailed);
        if (proposals.Any(row => row.SuggestedAxis == axis && !row.NumericLiteral && row.Role == "Other"))
            reasons.Add(CalibrationOcrReasonCodes.NonnumericReading);
        return new(check.NeedsReview || check.RejectedTicks.Count > 0 || reasons.Count > 0,
            ticks.Length, check.ResolvedTicks.Count, check.RejectedTicks.Count, reasons);
    }

    private static CalibrationOcrProposal Proposal(OcrDetectedRegion candidate, OcrRecognition reading, OcrRectangle plot)
    {
        OcrRecognitionAlternative? best = reading.Alternatives.Count > 0 ? reading.Alternatives[0] : null;
        string text = best?.Text ?? "";
        double confidence = best?.Confidence ?? 0;
        bool valid = double.IsFinite(confidence) && confidence is >= 0 and <= 1 && reading.SourceImage == OcrSourceImage.Original;
        string? failure = reading.Failure is not null || !valid ? CalibrationOcrReasonCodes.RecognitionFailed : null;
        bool literal = failure is null && CalibrationNumericLiteral.IsLiteral(text);
        NumericParseResult? parsed = literal ? GraphNumericParser.Parse(text) : null;
        return new(candidate.RegionId, candidate.Polygon.Points.Select(point => new CalibrationOcrPoint(point.X, point.Y)).ToArray(),
            text, valid ? confidence : 0, parsed?.Value, parsed?.NormalizedText, literal,
            GraphTextRoleClassifier.Classify(candidate, text, plot).Role.ToString(), AxisFor(candidate, plot), failure);
    }

    private static string AxisFor(OcrDetectedRegion region, OcrRectangle plot) =>
        GraphTextRoleClassifier.Classify(region with { Context = new OcrRegionContext(NumericExpected: true) }, "", plot).Role switch
        {
            OcrTextRole.XTick => "X",
            OcrTextRole.YTick => "Y",
            _ => "",
        };

    private static void Validate(CalibrationOcrWorkItem item)
    {
        CalibrationOcrRequest request = item.Request;
        if (request.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.Binding) || request.Binding.Length > 1024 ||
            !IsHash(request.SourceFileSha256) || !IsHash(request.PanelImageSha256) ||
            request.Width is < 32 or > 4096 || request.Height is < 32 or > 4096 ||
            item.EncodedImage.Length is <= 0 or > CalibrationOcrWire.MaximumImageBytes || request.Plot is null ||
            !double.IsFinite(request.Plot.Left) || !double.IsFinite(request.Plot.Top) ||
            !double.IsFinite(request.Plot.Right) || !double.IsFinite(request.Plot.Bottom) ||
            request.Plot.Left < 0 || request.Plot.Top < 0 || request.Plot.Right >= request.Width || request.Plot.Bottom >= request.Height ||
            request.Plot.Left >= request.Plot.Right || request.Plot.Top >= request.Plot.Bottom || !request.ValidMeasuredXTicks() ||
            !string.Equals(Hash(item.EncodedImage), request.PanelImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new CalibrationOcrException(CalibrationOcrReasonCodes.InvalidInput);
    }

    private static OcrImage Decode(CalibrationOcrWorkItem item, CancellationToken token)
    {
        using var stream = new MemoryStream(item.EncodedImage, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != item.Request.Width || decoder.Frames[0].PixelHeight != item.Request.Height)
            throw new CalibrationOcrException(CalibrationOcrReasonCodes.InvalidInput);
        BitmapSource frame = WhiteBackgroundRaster.Normalize(decoder.Frames[0], token);
        var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
        var bgr = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        int width = item.Request.Width, height = item.Request.Height;
        byte[] pixels = new byte[checked(width * height)], color = new byte[checked(width * height * 3)];
        gray.CopyPixels(pixels, width, 0);
        bgr.CopyPixels(color, width * 3, 0);
        return new(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity,
            BgrPixels: new(width * 3, color));
    }

    private sealed class RecognitionAttemptTiming
    {
        public double Recognition { get; set; }
        public double Pacing { get; set; }
    }

    private void RecordSessionCreation() => sessionCreationCount = checked(sessionCreationCount + 1);

    private static async Task<List<OcrRecognition>> RecognizeBatchesAsync(
        CalibrationOcrReader activeReader, IReadOnlyList<IReadOnlyList<OcrCrop>> batches,
        RecognitionAttemptTiming timing, CancellationToken token)
    {
        var readings = new List<OcrRecognition>();
        foreach (IReadOnlyList<OcrCrop> batch in batches)
        {
            var timer = Stopwatch.StartNew();
            try { readings.AddRange(await activeReader.Recognizer.RecognizeBatchAsync(batch, token).ConfigureAwait(false)); }
            finally { timer.Stop(); timing.Recognition += timer.Elapsed.TotalMilliseconds; }
            if (activeReader.Provider == "DirectMl")
            {
                var delay = Stopwatch.StartNew();
                try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(timer.Elapsed.TotalMilliseconds)), token).ConfigureAwait(false); }
                finally { delay.Stop(); timing.Pacing += delay.Elapsed.TotalMilliseconds; }
            }
        }
        return readings;
    }

    private static (DxgiAdapter? Adapter, string? Reason) CheckAdapter(CalibrationOcrDeviceSelection selection)
    {
        try
        {
            if (!CalibrationOcrWorkerClient.ValidSelection(selection) || selection.Provider != "DirectMl")
                return (null, "CALIBRATION_OCR_ADAPTER_UNAVAILABLE");
            var inventory = new WindowsDxgiAdapterInventory();
            return ResolveAdapter(selection, inventory.Enumerate(), WindowsDxgiAdapterInventory.TryReadLocalMemoryBudget);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
        { return (null, "CALIBRATION_OCR_ADAPTER_UNAVAILABLE"); }
    }

    internal static (DxgiAdapter? Adapter, string? Reason) ResolveAdapter(CalibrationOcrDeviceSelection selection,
        IReadOnlyList<DxgiAdapter> adapters, Func<DirectMlAdapterSelection, DxgiLocalMemoryBudget?> getBudget)
    {
        if (!CalibrationOcrWorkerClient.ValidSelection(selection) || selection.Provider != "DirectMl")
            return (null, "CALIBRATION_OCR_ADAPTER_UNAVAILABLE");
        DxgiAdapter? adapter = adapters.SingleOrDefault(item => item.Index == selection.AdapterIndex &&
            item.Luid == selection.AdapterLuid && !item.IsSoftware);
        if (adapter is null) return (null, "CALIBRATION_OCR_ADAPTER_UNAVAILABLE");
        DxgiLocalMemoryBudget? memory = getBudget(new(adapter.Index, adapter.Luid));
        if (memory is null) return (null, "CALIBRATION_OCR_GPU_MEMORY_BUDGET_UNAVAILABLE");
        if (memory.CurrentUsageBytes > memory.BudgetBytes || memory.BudgetBytes - memory.CurrentUsageBytes < 256UL * 1024 * 1024)
            return (null, "CALIBRATION_OCR_GPU_MEMORY_HEADROOM_LOW");
        return (adapter, null);
    }

    private static async Task<CalibrationOcrReader> CreateReaderAsync(string assetRoot,
        CalibrationOcrDeviceSelection selection, string? fallbackReason, Action sessionCreated, CancellationToken token)
    {
        string root = Path.GetFullPath(assetRoot);
        string modelPath = Path.Combine(root, "en_PP-OCRv5_mobile_rec.onnx");
        string manifestPath = Path.Combine(root, "recognizer.json");
        await VerifyAssetAsync(modelPath, ModelSha256, 64 * 1024 * 1024, token).ConfigureAwait(false);
        await VerifyAssetAsync(manifestPath, ManifestSha256, 128 * 1024, token).ConfigureAwait(false);
        await VerifyAssetAsync(Path.Combine(root, "PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt"),
            "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4", 128 * 1024, token).ConfigureAwait(false);
        await VerifyAssetAsync(Path.Combine(root, "PaddlePaddle-PP-OCRv5-Models-Notice.txt"),
            "8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729", 128 * 1024, token).ConfigureAwait(false);
        var identity = new ModelIdentity("en_PP-OCRv5_mobile_rec", "5.0.0", ModelSha256, modelPath);
        var options = ProductionOcrAdapter.ReadRecognitionOptions(identity, manifestPath);
        DxgiAdapter? adapter = null;
        if (selection.Provider == "DirectMl")
        {
            var check = CheckAdapter(selection);
            adapter = check.Adapter;
            fallbackReason = check.Reason;
        }
        if (adapter is not null)
        {
            try { return await CreateRuntimeAsync(identity, manifestPath, options, adapter, null, sessionCreated, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            { fallbackReason = "CALIBRATION_OCR_DIRECTML_SESSION_FAILED"; }
        }
        return await CreateRuntimeAsync(identity, manifestPath, options, null, fallbackReason, sessionCreated, token).ConfigureAwait(false);
    }

    private static async Task<CalibrationOcrReader> CreateRuntimeAsync(ModelIdentity identity, string manifestPath,
        (LocalOnnxTextRecognizerOptions Recognizer, OcrPipelineOptions Pipeline) options,
        DxgiAdapter? adapter, string? fallbackReason, Action sessionCreated, CancellationToken token)
    {
        var inventory = new WindowsDxgiAdapterInventory();
        OnnxInferenceSessionFactory factory = adapter is null
            ? new(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled)
            : new(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled, OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
                new(adapter.Index, adapter.Luid), inventory);
        IInferenceSessionFactory selectedFactory = adapter is null ? factory : new NoCpuFactory(factory);
        IReadOnlyList<InferenceProvider> allowed = adapter is null ? [InferenceProvider.Cpu] : [InferenceProvider.DirectMl, InferenceProvider.Cpu];
        var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(), selectedFactory, CpuThreadConfiguration.Create(1));
        var runtime = new InferenceRuntime(registry, new BoundedInferenceScheduler(1, 1), new NoCache(), allowedProviders: allowed);
        try
        {
            SessionAcquisition acquired = await registry.GetOrCreateAsync(identity, allowed, token).ConfigureAwait(false);
            if (acquired.Succeeded) sessionCreated();
            if (!acquired.Succeeded || registry.CreatedSessionCount != 1 ||
                acquired.Session!.Provider != (adapter is null ? InferenceProvider.Cpu : InferenceProvider.DirectMl))
                throw new CalibrationOcrException(CalibrationOcrReasonCodes.RecognitionFailed);
            ITextRecognizer recognizer = new LocalOnnxTextRecognizer(runtime, options.Recognizer with { BypassCache = true, AllowedProviders = allowed });
            if (ProductionOcrAdapter.UsesOfficialRecognitionSpacingV2Manifest(manifestPath))
                recognizer = new OfficialRecognitionSpacingV2TextRecognizer(recognizer);
            return new(recognizer, options.Pipeline, runtime, RuntimeFiles(), adapter is null ? "Cpu" : "DirectMl",
                adapter is null ? null : new DirectMlAdapterSelection(adapter.Index, adapter.Luid), adapter?.Name, fallbackReason);
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static List<CalibrationOcrRuntimeFile> RuntimeFiles()
    {
        var files = new[] { typeof(CalibrationOcrSession).Assembly, typeof(LocalOnnxTextRecognizer).Assembly, typeof(InferenceRuntime).Assembly }
            .Select(assembly => new CalibrationOcrRuntimeFile(assembly.GetName().Name!, FileHash(assembly.Location))).ToList();
        using Process process = Process.GetCurrentProcess();
        ProcessModule[] native = process.Modules.Cast<ProcessModule>()
            .Where(module => string.Equals(module.ModuleName, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (native.Length != 1) throw new CalibrationOcrException(CalibrationOcrReasonCodes.ResourceUnavailable);
        files.Add(new("onnxruntime.dll", FileHash(native[0].FileName)));
        return files;
    }

    private static (long Private, long Working, long Managed) MemorySnapshot()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return (process.PrivateMemorySize64, process.WorkingSet64, GC.GetTotalMemory(false));
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static async Task VerifyAssetAsync(string path, string expected, long maximum, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length <= 0 || stream.Length > maximum ||
            !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), expected, StringComparison.OrdinalIgnoreCase))
            throw new CalibrationOcrException(CalibrationOcrReasonCodes.AssetMismatch);
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private sealed class NoCache : IStageCache
    {
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class NoCpuFactory(OnnxInferenceSessionFactory inner) : IInferenceSessionFactory, IInferenceAdapterSelection
    {
        public string? DirectMlSelectionIdentity => inner.DirectMlSelectionIdentity;
        public void ValidateDirectMlSelection() => inner.ValidateDirectMlSelection();
        public ValueTask<IInferenceSession> CreateAsync(ModelIdentity model, InferenceProvider provider,
            CpuThreadConfiguration configuration, CancellationToken token) => provider == InferenceProvider.Cpu
                ? ValueTask.FromException<IInferenceSession>(new InvalidOperationException("Explicit GPU session failed."))
                : inner.CreateAsync(model, provider, configuration, token);
    }
}

internal sealed class CalibrationOcrReader(ITextRecognizer recognizer, OcrPipelineOptions options,
    IAsyncDisposable owner, IReadOnlyList<CalibrationOcrRuntimeFile> runtimeFiles, string provider = "Cpu",
    DirectMlAdapterSelection? adapter = null, string? adapterName = null, string? fallbackReason = null) : IAsyncDisposable
{
    internal ITextRecognizer Recognizer { get; } = recognizer;
    internal OcrPipelineOptions Options { get; } = options;
    internal IReadOnlyList<CalibrationOcrRuntimeFile> RuntimeFiles { get; } = runtimeFiles;
    internal string Provider { get; } = provider;
    internal DirectMlAdapterSelection? Adapter { get; } = adapter;
    internal string? AdapterName { get; } = adapterName;
    internal string? FallbackReason { get; } = fallbackReason;
    public ValueTask DisposeAsync() => owner.DisposeAsync();
}

internal sealed class CalibrationOcrException(string reason) : Exception(reason)
{
    internal string Reason { get; } = reason;
}
