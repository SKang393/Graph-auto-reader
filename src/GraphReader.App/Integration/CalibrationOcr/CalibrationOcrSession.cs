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

namespace GraphReader.App.Integration.CalibrationOcr;

internal sealed class CalibrationOcrSession : IAsyncDisposable
{
    internal const string ModelSha256 = "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743";
    internal const string ManifestSha256 = "17259b3260c0d39b292be70b36e68d53a067992de3afeaf0809d4d981de4327a";
    private readonly CalibrationOcrCpuBudget budget;
    private readonly Func<CancellationToken, Task<CalibrationOcrReader>> readerFactory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CalibrationOcrReader? reader;
    private bool disposed;

    internal CalibrationOcrSession(string assetRoot, CalibrationOcrCpuBudget budget)
        : this(budget, token => CreateReaderAsync(assetRoot, token)) { }

    internal CalibrationOcrSession(CalibrationOcrCpuBudget budget,
        Func<CancellationToken, Task<CalibrationOcrReader>> readerFactory)
    {
        this.budget = budget;
        this.readerFactory = readerFactory;
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
            OcrImage image = Decode(item);
            double decode = stage.Elapsed.TotalMilliseconds;
            CalibrationOcrPlot plot = item.Request.Plot;
            var bounds = new OcrRectangle(plot.Left, plot.Top, plot.Right - plot.Left, plot.Bottom - plot.Top);

            stage.Restart();
            IReadOnlyList<OcrDetectedRegion> components = await new ConnectedComponentTextRegionDetector()
                .DetectAsync(image, token).ConfigureAwait(false);
            OcrDetectedRegion[] candidates = components.Where(region => AxisFor(region, bounds) is "X" or "Y").ToArray();
            if (candidates.Length > 128) throw new CalibrationOcrException(CalibrationOcrReasonCodes.TooManyCandidates);
            double component = stage.Elapsed.TotalMilliseconds;

            stage.Restart();
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
            stage.Restart();
            var readings = new List<OcrRecognition>();
            foreach (IReadOnlyList<OcrCrop> batch in batches)
                readings.AddRange(await reader.Recognizer.RecognizeBatchAsync(batch, token).ConfigureAwait(false));
            double recognition = stage.Elapsed.TotalMilliseconds;
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
                new(decode, component, crop, initialization, recognition, total.Elapsed.TotalMilliseconds, reused),
                new(ModelSha256, ManifestSha256, "Cpu", 1, 1, 1, 1, false, budget, memory, reader.RuntimeFiles), null);
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
        bool literal = failure is null && GraphNumericParser.IsLiteralGraphNumber(text);
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
            request.Plot.Left >= request.Plot.Right || request.Plot.Top >= request.Plot.Bottom ||
            !string.Equals(Hash(item.EncodedImage), request.PanelImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new CalibrationOcrException(CalibrationOcrReasonCodes.InvalidInput);
    }

    private static OcrImage Decode(CalibrationOcrWorkItem item)
    {
        using var stream = new MemoryStream(item.EncodedImage, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != item.Request.Width || decoder.Frames[0].PixelHeight != item.Request.Height)
            throw new CalibrationOcrException(CalibrationOcrReasonCodes.InvalidInput);
        BitmapFrame frame = decoder.Frames[0];
        var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
        var bgr = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        int width = item.Request.Width, height = item.Request.Height;
        byte[] pixels = new byte[checked(width * height)], color = new byte[checked(width * height * 3)];
        gray.CopyPixels(pixels, width, 0);
        bgr.CopyPixels(color, width * 3, 0);
        return new(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity,
            BgrPixels: new(width * 3, color));
    }

    private static async Task<CalibrationOcrReader> CreateReaderAsync(string assetRoot, CancellationToken token)
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
        var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(),
            new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled), CpuThreadConfiguration.Create(1));
        var runtime = new InferenceRuntime(registry, new BoundedInferenceScheduler(1, 1), new NoCache(), allowedProviders: [InferenceProvider.Cpu]);
        try
        {
            SessionAcquisition acquired = await registry.GetOrCreateAsync(identity, [InferenceProvider.Cpu], token).ConfigureAwait(false);
            if (!acquired.Succeeded || registry.CreatedSessionCount != 1)
                throw new CalibrationOcrException(CalibrationOcrReasonCodes.RecognitionFailed);
            ITextRecognizer recognizer = new LocalOnnxTextRecognizer(runtime, options.Recognizer with { BypassCache = true });
            if (ProductionOcrAdapter.UsesOfficialRecognitionSpacingV2Manifest(manifestPath))
                recognizer = new OfficialRecognitionSpacingV2TextRecognizer(recognizer);
            return new(recognizer, options.Pipeline, runtime, RuntimeFiles());
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
}

internal sealed class CalibrationOcrReader(ITextRecognizer recognizer, OcrPipelineOptions options,
    IAsyncDisposable owner, IReadOnlyList<CalibrationOcrRuntimeFile> runtimeFiles) : IAsyncDisposable
{
    internal ITextRecognizer Recognizer { get; } = recognizer;
    internal OcrPipelineOptions Options { get; } = options;
    internal IReadOnlyList<CalibrationOcrRuntimeFile> RuntimeFiles { get; } = runtimeFiles;
    public ValueTask DisposeAsync() => owner.DisposeAsync();
}

internal sealed class CalibrationOcrException(string reason) : Exception(reason)
{
    internal string Reason { get; } = reason;
}
