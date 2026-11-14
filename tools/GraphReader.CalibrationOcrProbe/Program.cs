// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.Inference;
using GraphReader.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static class Program
{
    internal const string ModelHash = "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743";
    internal const string ManifestHash = "17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a";
    private const int MaximumOutputBytes = 1_000_000;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static int emitted;

    public static async Task<int> Main(string[] args)
    {
        bool workerCheck = args.Length > 0 && args[0] is "--worker-check" or "--gpu-worker-check";
        bool hardwareCheck = args.Length > 0 && args[0] == "--hardware-check";
        bool hardwareChild = args.Length > 0 && args[0] == "--hardware-child";
        bool automaticCheck = args.Length > 0 && args[0] == "--automatic-device-check";
        bool tickCheck = args.Length > 0 && args[0] == "--tick-proposal-check";
        bool markerCheck = args.Length > 0 && args[0] == "--marker-exemplar-check";
        bool markerShapeCheck = args.Length > 0 && args[0] == "--marker-shape-control-check";
        bool markerWorkflow = args.Length > 0 && args[0] == "--marker-workflow-check";
        bool panelCrop = args.Length > 0 && args[0] == "--panel-crop-check";
        bool ocrPadding = args.Length > 0 && args[0] == "--ocr-padding-check";
        bool ocrPaddingGpu = args.Length > 0 && args[0] == "--ocr-padding-gpu-check";
        bool ocrPaddingAdapters = args.Length > 0 && args[0] == "--ocr-padding-adapters";
        bool realDevGeometry = args.Length > 0 && args[0] == "--real-dev-geometry-properties-check";
        bool realDevCalibration = args.Length > 0 && args[0] == "--real-dev-calibration-check";
        bool realDevPrivate = realDevCalibration || realDevGeometry;
        bool realDevSelfTest = args.Length == 1 && args[0] == "--real-dev-calibration-self-test";
        bool cpuBudgetSelfTest = args.Length == 1 && args[0] == "--cpu-budget-self-test";
        bool cpuBudgetChildSelfTest = args.Length == 1 && args[0] == "--cpu-budget-child-self-test";
        int seconds = ocrPaddingAdapters ? 10 : ocrPadding || ocrPaddingGpu ? 120 : cpuBudgetSelfTest ? 15 : cpuBudgetChildSelfTest ? 10 : realDevPrivate ? 900 : panelCrop ? 25 : markerWorkflow ? 95 : markerShapeCheck ? 65 : markerCheck ? 43 : tickCheck ? 75 : automaticCheck ? 99 : workerCheck ? 360 : hardwareCheck ? 149 : hardwareChild ? 43 : 170;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        using var watchdog = new Timer(_ =>
        {
            Emit(new { error = "DEADLINE_EXCEEDED", review_only = true });
            Environment.Exit(3);
        }, null, TimeSpan.FromSeconds(ocrPaddingAdapters ? 15 : ocrPadding || ocrPaddingGpu ? 125 : cpuBudgetSelfTest ? 20 : cpuBudgetChildSelfTest ? 12 : realDevPrivate ? 905 : panelCrop ? 30 : markerWorkflow ? 100 : markerShapeCheck ? 70 : markerCheck ? 45 : tickCheck ? 80 : automaticCheck ? 100 : hardwareCheck ? 150 : hardwareChild ? 44 : workerCheck ? 365 : 175), Timeout.InfiniteTimeSpan);
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (cpuBudgetSelfTest)
            {
                Emit(await CpuBudgetJob.RunChildBudgetSelfTestAsync(deadline.Token));
                return 0;
            }
            if (cpuBudgetChildSelfTest)
            {
                using CpuBudgetJob childBudget = CpuBudgetJob.Apply();
                Emit(new { childBudget.LogicalProcessors, childBudget.Rate, childBudget.MaximumLogicalCores,
                    childBudget.SilentBreakawayVerified, priority_class = "BelowNormal" });
                byte[] acknowledgement = new byte[1];
                return await Console.OpenStandardInput().ReadAsync(acknowledgement, deadline.Token) == 1 ? 0 : 1;
            }
            if (realDevSelfTest)
            {
                Emit(RealDevCalibrationCheck.SelfTest());
                return 0;
            }
            if (realDevPrivate)
            {
                // Every failure in this private mode is sanitized here, before the owned-mode handler.
                try
                {
                    if (args.Length != 6 || args[5] != "--explicit-opt-in")
                        throw new InvalidDataException();
                    using CpuBudgetJob parentBudget = CpuBudgetJob.Apply(allowChildBreakaway: true);
                    Emit(realDevGeometry
                        ? await RealDevCalibrationCheck.RunGeometryAsync(args[1], args[2], args[3], args[4], parentBudget, deadline.Token)
                        : await RealDevCalibrationCheck.RunAsync(args[1], args[2], args[3], args[4], parentBudget, deadline.Token));
                    return 0;
                }
                catch (Exception)
                {
                    Emit(new { schema = realDevGeometry ? "graphreader.real-dev-geometry-properties-diagnostic.v1" : "graphreader.real-dev-calibration-assistance-diagnostic.v1",
                        status = "blocked_preflight_or_runtime", error = "REAL_DEV_CALIBRATION_CHECK_FAILED",
                        denominator = 120, sealed_payload_reads = 0, review_only = true, production_approved = false });
                    return 1;
                }
            }
            if (args is ["--panel-crop-check", var cropOutput])
            {
                Emit(await PanelCropCheck.RunAsync(cropOutput, deadline.Token));
                return 0;
            }
            if (panelCrop) throw new InvalidDataException("Usage: --panel-crop-check <absolute-empty-output-root>");
            if (args is ["--marker-shape-control-check", var shapeApp])
            {
                Emit(await MarkerShapeControlCheck.RunAsync(shapeApp, deadline.Token));
                return 0;
            }
            if (markerShapeCheck) throw new InvalidDataException("Usage: --marker-shape-control-check <absolute-App-exe>");
            if (markerWorkflow && (args.Length == 6 || args.Length == 7 && args[6] == "prepared-crop"))
            {
                byte[] input = ReadBounded(args[1], 32_768);
                ProbeRequest owned = JsonSerializer.Deserialize<ProbeRequest>(input, Json)
                    ?? throw new InvalidDataException("Missing request.");
                Emit(await MarkerWorkflowCheck.RunAsync(owned, args[2], args[3], args[4], args[5], args.Length == 7, deadline.Token));
                return 0;
            }
            if (markerWorkflow) throw new InvalidDataException("Usage: --marker-workflow-check <owned-request.json> <absolute-App-exe> <absolute-assets> <absolute-Rust-worker> <absolute-empty-output-root> [prepared-crop]");
            if (args is ["--marker-exemplar-check", var markerApp])
            {
                Emit(await MarkerExemplarCheck.RunAsync(markerApp, deadline.Token));
                return 0;
            }
            if (args is ["--marker-exemplar-check", var largeMarkerApp, "near-pixel-limit"])
            {
                Emit(await MarkerExemplarCheck.RunAsync(largeMarkerApp, deadline.Token, nearPixelLimit: true));
                return 0;
            }
            if (markerCheck) throw new InvalidDataException("Usage: --marker-exemplar-check <absolute-App-exe> [near-pixel-limit]");
            if (args is ["--tick-proposal-check", var tickRequest, var tickApp, var tickAssets, var tickWorker])
            {
                byte[] input = ReadBounded(tickRequest, 32_768);
                ProbeRequest ownedRequest = JsonSerializer.Deserialize<ProbeRequest>(input, Json)
                    ?? throw new InvalidDataException("Missing request.");
                Emit(await TickProposalCheck.RunAsync(ownedRequest, Hash(input), tickApp, tickAssets, tickWorker, deadline.Token));
                return 0;
            }
            if (tickCheck) throw new InvalidDataException("Usage: --tick-proposal-check <owned-request.json> <absolute-App-exe> <absolute-assets> <absolute-Rust-worker>");
            if (args is ["--automatic-device-check", var autoApp, var autoAssets, var settingsRoot])
            {
                Emit(await AutomaticDeviceCheck.RunAsync(autoApp, autoAssets, settingsRoot, deadline.Token));
                return 0;
            }
            if (automaticCheck) throw new InvalidDataException("Usage: --automatic-device-check <absolute-App-exe> <absolute-assets> <absolute-empty-settings-root>");
            if (args is ["--hardware-check", var hardwareRequest])
            {
                Emit(await HardwareCheck.RunParentAsync(hardwareRequest, deadline.Token));
                return 0;
            }
            if (hardwareCheck) throw new InvalidDataException("Usage: --hardware-check <absolute-owned-request.json>");
            if (args is ["--hardware-child", var childRequest, var childProvider, var childIndex, var childLuid])
            {
                using CpuBudgetJob childBudget = CpuBudgetJob.Apply();
                Emit(await HardwareCheck.RunChildAsync(childRequest, childProvider, childIndex, childLuid, childBudget, deadline.Token));
                return 0;
            }
            if (hardwareChild) throw new InvalidDataException("Invalid hardware child arguments.");
            if (args is ["--worker-check", var workerRequest, var appExe, var assetRoot])
            {
                byte[] input = ReadBounded(workerRequest, 32_768);
                ProbeRequest ownedRequest = JsonSerializer.Deserialize<ProbeRequest>(input, Json)
                    ?? throw new InvalidDataException("Missing request.");
                Emit(await SelfHostedWorkerCheck.RunAsync(ownedRequest, Hash(input), appExe, assetRoot, null, deadline.Token));
                return 0;
            }
            if (args is ["--gpu-worker-check", var gpuRequest, var gpuApp, var gpuAssets, var gpuIndex, var gpuLuid])
            {
                if (!int.TryParse(gpuIndex, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0 ||
                    !long.TryParse(gpuLuid, NumberStyles.Integer, CultureInfo.InvariantCulture, out long luid))
                    throw new InvalidDataException("Explicit DXGI index and LUID required.");
                byte[] input = ReadBounded(gpuRequest, 32_768);
                ProbeRequest ownedRequest = JsonSerializer.Deserialize<ProbeRequest>(input, Json)
                    ?? throw new InvalidDataException("Missing request.");
                DxgiAdapter adapter = new WindowsDxgiAdapterInventory().Enumerate()
                    .Single(candidate => candidate.Index == index && candidate.Luid == luid && !candidate.IsSoftware);
                Emit(await SelfHostedWorkerCheck.RunAsync(ownedRequest, Hash(input), gpuApp, gpuAssets,
                    new CalibrationOcrDeviceSelection("DirectMl", index, luid, adapter.Name), deadline.Token));
                return 0;
            }
            if (workerCheck) throw new InvalidDataException("Usage: --worker-check <request.json> <absolute-App-exe> <asset-root>");
            // Apply and query the aggregate cap before entering any OCR/runtime code.
            using CpuBudgetJob budget = CpuBudgetJob.Apply();
            if (args is ["--ocr-padding-adapters"])
            {
                var inventory = new WindowsDxgiAdapterInventory();
                Emit(new { adapters = inventory.Enumerate().Where(item => !item.IsSoftware).Select(item => new
                {
                    item.Index, item.Luid, item.Name,
                    budget = WindowsDxgiAdapterInventory.TryReadLocalMemoryBudget(new(item.Index, item.Luid)),
                }).ToArray() });
                return 0;
            }
            if (ocrPaddingAdapters) throw new InvalidDataException("Usage: --ocr-padding-adapters");
            if (args is ["--ocr-padding-check", var paddingAssets])
            {
                Emit(await OcrBatchPaddingCheck.RunAsync(paddingAssets, budget, deadline.Token));
                return 0;
            }
            if (ocrPadding) throw new InvalidDataException("Usage: --ocr-padding-check <absolute-reviewed-assets-root>");
            if (args is ["--ocr-padding-gpu-check", var gpuPaddingAssets, var gpuPaddingIndex, var gpuPaddingLuid])
            {
                if (!int.TryParse(gpuPaddingIndex, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0 ||
                    !long.TryParse(gpuPaddingLuid, NumberStyles.Integer, CultureInfo.InvariantCulture, out long luid))
                    throw new InvalidDataException("An explicit DXGI index and LUID are required.");
                Emit(await OcrBatchPaddingCheck.RunGpuAsync(gpuPaddingAssets, index, luid, budget, deadline.Token));
                return 0;
            }
            if (ocrPaddingGpu) throw new InvalidDataException("Usage: --ocr-padding-gpu-check <absolute-reviewed-assets-root> <DXGI-index> <LUID>");
            if (args is ["--self-test"])
            {
                await ProbeSelfTest.RunAsync(deadline.Token);
                Emit(new { self_test = "passed", budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores });
                return 0;
            }
            if (args is not ["--request", var requestPath])
                throw new InvalidDataException("Usage: --request <explicit-owned-input.json> or --self-test");
            byte[] requestBytes = ReadBounded(requestPath, 32_768);
            ProbeRequest request = JsonSerializer.Deserialize<ProbeRequest>(requestBytes, Json)
                ?? throw new InvalidDataException("Missing request.");
            object report = await RunAsync(request, Hash(requestBytes), budget, deadline.Token);
            Emit(report);
            return 0;
        }
        catch (Exception error)
        {
            if (realDevPrivate)
            {
                Emit(new { error = "REAL_DEV_CALIBRATION_CHECK_FAILED", review_only = true });
                return 1;
            }
            Emit(new { error = error.GetType().Name, detail = error.Message[..Math.Min(error.Message.Length, 512)], review_only = true });
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task<object> RunAsync(ProbeRequest request, string requestHash, CpuBudgetJob budget, CancellationToken token)
    {
        var total = Stopwatch.StartNew();
        if (request.Scope != "owned-synthetic-review-only" || request.SourceCropXywh.Length != 4 || request.PlotLtrb.Length != 4)
            throw new InvalidDataException("Only one explicit owned synthetic source/crop/plot is supported.");
        byte[] encoded = ReadBounded(request.ImagePath, 64 * 1024 * 1024);
        RequireHash(encoded, request.ImageSha256);
        RequireHash(ReadBounded(request.ModelPath, 64 * 1024 * 1024), ModelHash);
        RequireHash(ReadBounded(request.ManifestPath, 128 * 1024), ManifestHash);
        RequireHash(ReadBounded(request.LicensePath, 128 * 1024), "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4");
        RequireHash(ReadBounded(request.NoticePath, 128 * 1024), "8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729");

        var stage = Stopwatch.StartNew();
        (OcrImage image, int sourceWidth, int sourceHeight) = Decode(encoded, request.SourceCropXywh);
        double decodeMilliseconds = stage.Elapsed.TotalMilliseconds;
        double[] plot = request.PlotLtrb;
        var bounds = new OcrRectangle(plot[0], plot[1], plot[2] - plot[0], plot[3] - plot[1]);
        if (!bounds.IsValid || plot.Any(value => !double.IsFinite(value)) || bounds.Left < 0 || bounds.Top < 0 ||
            bounds.Right >= image.Width || bounds.Bottom >= image.Height)
            throw new InvalidDataException("Plot must be inside the in-memory source crop.");

        stage.Restart();
        IReadOnlyList<OcrDetectedRegion> components = await new ConnectedComponentTextRegionDetector().DetectAsync(image, token);
        OcrDetectedRegion[] candidates = SelectCandidates(components, bounds);
        if (candidates.Length > 128) throw new InvalidDataException("Candidate budget exceeded; no candidates were silently dropped.");
        double componentMilliseconds = stage.Elapsed.TotalMilliseconds;
        var identity = new ModelIdentity("en_PP-OCRv5_mobile_rec", "5.0.0", ModelHash, Path.GetFullPath(request.ModelPath));
        var options = ProductionOcrAdapter.ReadRecognitionOptions(identity, request.ManifestPath);
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
        stage.Restart();
        IReadOnlyList<IReadOnlyList<OcrCrop>> batches = OcrCropBatcher.CreateBatches(image, candidates, cropOptions, token);
        double cropMilliseconds = stage.Elapsed.TotalMilliseconds;

        stage.Restart();
        var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(),
            new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled), CpuThreadConfiguration.Create(1));
        await using var runtime = new InferenceRuntime(registry, new BoundedInferenceScheduler(1, 1), new NoCache(),
            allowedProviders: [InferenceProvider.Cpu]);
        SessionAcquisition acquired = await registry.GetOrCreateAsync(identity, [InferenceProvider.Cpu], token);
        if (!acquired.Succeeded) throw new InvalidOperationException(acquired.Error?.TechnicalMessage ?? "Session initialization failed.");
        double initializationMilliseconds = stage.Elapsed.TotalMilliseconds;
        ITextRecognizer recognizer = new LocalOnnxTextRecognizer(runtime, options.Recognizer with { BypassCache = true });
        if (ProductionOcrAdapter.UsesOfficialRecognitionSpacingV2Manifest(request.ManifestPath))
            recognizer = new OfficialRecognitionSpacingV2TextRecognizer(recognizer);
        stage.Restart();
        var recognized = new List<OcrRecognition>();
        foreach (IReadOnlyList<OcrCrop> batch in batches)
            recognized.AddRange(await recognizer.RecognizeBatchAsync(batch, token));
        double recognitionMilliseconds = stage.Elapsed.TotalMilliseconds;
        if (recognized.Count != candidates.Length || recognized.Select(row => row.RegionId).Distinct().Count() != candidates.Length)
            throw new InvalidDataException("Recognizer population mismatch.");
        var byId = recognized.ToDictionary(row => row.RegionId, StringComparer.Ordinal);
        var proposals = candidates.Select(candidate => ToProposal(candidate, byId[candidate.RegionId], bounds,
            request.SourceCropXywh[0], request.SourceCropXywh[1])).ToArray();
        return new
        {
            schema = "graphreader.calibration-ocr-probe.v1", review_only = true, production_approved = false,
            request_sha256 = requestHash, source_sha256 = request.ImageSha256, source_width = sourceWidth, source_height = sourceHeight,
            source_crop_xywh = request.SourceCropXywh, plot_ltrb_in_crop = plot, coordinate_space = "original_source_pixels",
            model_sha256 = ModelHash, manifest_sha256 = ManifestHash,
            cpu_budget = new { budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores, priority = "BelowNormal", verified = true },
            runtime = new { provider = "Cpu", intra_op_threads = 1, inter_op_threads = 1, worker_count = 1,
                graph_optimization = "Disabled", registry.CreatedSessionCount, persistent_cache = false,
                assemblies = new[] { typeof(Program).Assembly, typeof(ProductionOcrAdapter).Assembly,
                    typeof(LocalOnnxTextRecognizer).Assembly, typeof(InferenceRuntime).Assembly }
                    .Select(assembly => new { name = assembly.GetName().Name, sha256 = FileHash(assembly.Location) }).ToArray(),
                native = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                    .Where(module => string.Equals(module.ModuleName, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase))
                    .Select(module => new { path = module.FileName, sha256 = FileHash(module.FileName) }).ToArray() },
            component_count = components.Count, candidate_count = candidates.Length, batch_count = batches.Count,
            timing_ms = new { decode = decodeMilliseconds, components = componentMilliseconds, crop = cropMilliseconds,
                cold_initialization = initializationMilliseconds, recognition = recognitionMilliseconds,
                reported_recognition_inference = recognized.Sum(row => row.InferenceMilliseconds), total = total.Elapsed.TotalMilliseconds },
            proposals,
        };
    }

    internal static OcrDetectedRegion[] SelectCandidates(IReadOnlyList<OcrDetectedRegion> components, OcrRectangle plot) =>
        components.Where(region => GraphTextRoleClassifier.Classify(region with
        {
            Context = new OcrRegionContext(NumericExpected: true),
        }, "", plot).Role is OcrTextRole.XTick or OcrTextRole.YTick).ToArray();

    internal static object ToProposal(OcrDetectedRegion candidate, OcrRecognition recognition, OcrRectangle plot, int offsetX, int offsetY)
    {
        OcrRecognitionAlternative? best = recognition.Alternatives.Count > 0 ? recognition.Alternatives[0] : null;
        string text = best?.Text ?? "";
        bool literal = recognition.Failure is null && GraphNumericParser.IsLiteralGraphNumber(text);
        NumericParseResult? parsed = literal ? GraphNumericParser.Parse(text) : null;
        return new
        {
            candidate.RegionId, polygon = candidate.Polygon.Points.Select(point => new[] { point.X + offsetX, point.Y + offsetY }).ToArray(),
            text, confidence = best?.Confidence ?? 0, numeric_value = parsed?.Value, normalized_text = parsed?.NormalizedText,
            numeric_literal = literal, role = GraphTextRoleClassifier.Classify(candidate, text, plot).Role.ToString(),
            failure = recognition.Failure?.Code, requires_review = true,
        };
    }

    internal static (OcrImage Image, int Width, int Height) Decode(byte[] encoded, int[] crop)
    {
        using var stream = new MemoryStream(encoded, writable: false);
        BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (frame.PixelWidth > 4096 || frame.PixelHeight > 4096 || crop[0] < 0 || crop[1] < 0 || crop[2] < 32 || crop[3] < 32 ||
            (long)crop[0] + crop[2] > frame.PixelWidth || (long)crop[1] + crop[3] > frame.PixelHeight)
            throw new InvalidDataException("Invalid original image or source crop dimensions.");
        var cropped = new CroppedBitmap(frame, new Int32Rect(crop[0], crop[1], crop[2], crop[3]));
        var gray = new FormatConvertedBitmap(cropped, PixelFormats.Gray8, null, 0);
        var bgr = new FormatConvertedBitmap(cropped, PixelFormats.Bgr24, null, 0);
        byte[] pixels = new byte[checked(crop[2] * crop[3])], color = new byte[checked(pixels.Length * 3)];
        gray.CopyPixels(pixels, crop[2], 0);
        bgr.CopyPixels(color, crop[2] * 3, 0);
        return (new(crop[2], crop[3], crop[2], pixels, OcrSourceImage.Original, OcrFrameTransform.Identity,
            BgrPixels: new(crop[2] * 3, color)), frame.PixelWidth, frame.PixelHeight);
    }

    internal static byte[] ReadBounded(string path, long maximum)
    {
        using var file = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 || file.Length > maximum) throw new InvalidDataException("Input exceeds its resource bound.");
        byte[] bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    internal static void RequireHash(byte[] bytes, string expected)
    {
        if (expected.Length != 64 || !string.Equals(Hash(bytes), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Input checksum mismatch.");
    }
    private static void Emit(object value)
    {
        byte[] output = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (output.Length > MaximumOutputBytes) throw new InvalidDataException("OUTPUT_LIMIT_EXCEEDED");
        if (Interlocked.Exchange(ref emitted, 1) == 0)
        {
            using Stream stdout = Console.OpenStandardOutput();
            stdout.Write(output);
            stdout.WriteByte((byte)'\n');
        }
    }

    internal sealed class NoCache : IStageCache
    {
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    internal sealed record ProbeRequest(string Scope, string ImagePath, string ImageSha256, int[] SourceCropXywh,
        double[] PlotLtrb, string ModelPath, string ManifestPath, string LicensePath, string NoticePath);
}
