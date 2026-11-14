// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Ocr;
using Microsoft.ML.OnnxRuntime;

namespace GraphReader.CalibrationOcrProbe;

internal static class HardwareCheck
{
    private const int OutputLimit = 1_000_000;
    private const int ErrorLimit = 16_384;

    internal static async Task<object> RunParentAsync(string requestPath, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(requestPath)) throw new InvalidDataException("Owned request path must be absolute.");
        byte[] requestBytes = Program.ReadBounded(requestPath, 32_768);
        Program.ProbeRequest request = JsonSerializer.Deserialize<Program.ProbeRequest>(requestBytes, Program.Json)
            ?? throw new InvalidDataException("Missing owned request.");
        if (request.Scope != "owned-synthetic-review-only") throw new InvalidDataException("Owned synthetic scope required.");
        string requestHash = Program.Hash(requestBytes);
        var inventory = new WindowsDxgiAdapterInventory();
        DxgiAdapter[] adapters = inventory.Enumerate().Where(adapter => !adapter.IsSoftware).Take(2).ToArray();
        var children = new List<ChildOutcome>();
        children.Add(await StartChildAsync(requestPath, requestHash, "cpu", null, token));
        foreach (DxgiAdapter adapter in adapters)
            children.Add(await StartChildAsync(requestPath, requestHash, "gpu", adapter, token));
        ChildReport? cpu = children[0].Report;
        object[] comparisons = children.Skip(1).Where(outcome => outcome.Report is not null).Select(outcome =>
        {
            ChildReport gpu = outcome.Report!;
            if (cpu is null) return (object)new { gpu.AdapterIndex, status = "cpu_unavailable" };
            var roundComparisons = Enumerable.Range(0, 4).Select(round =>
            {
                var expected = cpu.Rounds[round].Rows.ToDictionary(row => row.RegionId, StringComparer.Ordinal);
                var observed = gpu.Rounds[round].Rows.ToDictionary(row => row.RegionId, StringComparer.Ordinal);
                string[] common = expected.Keys.Where(observed.ContainsKey).ToArray();
                return new { round, missing = expected.Keys.Count(key => !observed.ContainsKey(key)),
                    extra = observed.Keys.Count(key => !expected.ContainsKey(key)),
                    text_differences = common.Count(key => expected[key].Text != observed[key].Text),
                    value_differences = common.Count(key => expected[key].NumericValue != observed[key].NumericValue),
                    polygon_differences = common.Count(key => !SamePolygon(expected[key].Polygon, observed[key].Polygon)) };
            }).ToArray();
            return new { gpu.AdapterIndex, status = "compared",
                candidate_count_difference = gpu.CandidateCount - cpu.CandidateCount, rounds = roundComparisons };
        }).ToArray();
        return new { schema = "graphreader.ocr-hardware-check.v1", review_only = true, production_approved = false,
            request_sha256 = requestHash, adapters, children, comparisons,
            note = "DirectML sessions may execute some kernels on CPU. GPU pacing is not a utilization cap or accuracy claim." };
    }

    private static bool SamePolygon(double[][] a, double[][] b) => a.Length == b.Length &&
        a.Zip(b).All(pair => pair.First.SequenceEqual(pair.Second));

    private static async Task<ChildOutcome> StartChildAsync(string requestPath, string requestHash,
        string provider, DxgiAdapter? adapter, CancellationToken token)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Probe executable is unavailable.");
        if (!string.Equals(Path.GetFileNameWithoutExtension(exe), "GraphReader.CalibrationOcrProbe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hardware child requires the probe apphost.");
        using var process = new Process { StartInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        }};
        foreach (string value in new[] { "--hardware-child", requestPath, provider,
            adapter?.Index.ToString(CultureInfo.InvariantCulture) ?? "-", adapter?.Luid.ToString(CultureInfo.InvariantCulture) ?? "-" })
            process.StartInfo.ArgumentList.Add(value);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var timer = Stopwatch.StartNew();
        try
        {
            if (!process.Start()) return new(provider, adapter?.Index, "start_failed", null, timer.Elapsed.TotalMilliseconds);
            Task<byte[]> stdout = Task.Run(() => ReadBoundedAsync(process.StandardOutput.BaseStream, OutputLimit, deadline.Token), CancellationToken.None);
            Task<byte[]> stderr = Task.Run(() => ReadBoundedAsync(process.StandardError.BaseStream, ErrorLimit, deadline.Token), CancellationToken.None);
            await process.WaitForExitAsync(deadline.Token);
            byte[] result = await stdout;
            _ = await stderr;
            if (process.ExitCode != 0) return new(provider, adapter?.Index, ChildFailure(result, process.ExitCode), null, timer.Elapsed.TotalMilliseconds);
            ChildReport? report = JsonSerializer.Deserialize<ChildReport>(result, Program.Json);
            if (report is null || report.Provider != provider || report.RequestSha256 != requestHash ||
                report.AdapterIndex != adapter?.Index || report.AdapterLuid != adapter?.Luid || report.Rounds.Length != 4)
                return new(provider, adapter?.Index, "invalid_child_report", null, timer.Elapsed.TotalMilliseconds);
            return new(provider, adapter?.Index, null, report, timer.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(provider, adapter?.Index, "timeout", null, timer.Elapsed.TotalMilliseconds); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new(provider, adapter?.Index, "child_failed", null, timer.Elapsed.TotalMilliseconds); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static string ChildFailure(byte[] response, int exitCode)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(response);
            string? error = document.RootElement.TryGetProperty("error", out JsonElement value) ? value.GetString() : null;
            string? detail = document.RootElement.TryGetProperty("detail", out JsonElement text) ? text.GetString() : null;
            string combined = $"exit_{exitCode}:{error}:{detail}";
            return combined[..Math.Min(combined.Length, 512)];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { return $"exit_{exitCode}:invalid_error_response"; }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("Child output exceeds limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static async Task<object> RunChildAsync(string requestPath, string provider, string indexText,
        string luidText, CpuBudgetJob budget, CancellationToken token)
    {
        var total = Stopwatch.StartNew();
        if (!Path.IsPathFullyQualified(requestPath) || provider is not ("cpu" or "gpu"))
            throw new InvalidDataException("Invalid child scope.");
        DirectMlAdapterSelection? selection = null;
        var inventory = new WindowsDxgiAdapterInventory();
        if (provider == "gpu")
        {
            if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0 ||
                !long.TryParse(luidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long luid))
                throw new InvalidDataException("Invalid adapter identity.");
            selection = new(index, luid);
            if (!inventory.Enumerate().Any(adapter => adapter.Index == index && adapter.Luid == luid && !adapter.IsSoftware))
                throw new InvalidDataException("Adapter changed or is software-only.");
        }
        else if (indexText != "-" || luidText != "-") throw new InvalidDataException("CPU child has an adapter identity.");
        byte[] requestBytes = Program.ReadBounded(requestPath, 32_768);
        Program.ProbeRequest request = JsonSerializer.Deserialize<Program.ProbeRequest>(requestBytes, Program.Json)
            ?? throw new InvalidDataException("Missing request.");
        if (request.Scope != "owned-synthetic-review-only" || request.SourceCropXywh is not { Length: 4 } || request.PlotLtrb is not { Length: 4 })
            throw new InvalidDataException("Owned synthetic request required.");
        if (new[] { request.ImagePath, request.ModelPath, request.ManifestPath, request.LicensePath, request.NoticePath }
            .Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException("Owned input and reviewed asset paths must be absolute.");
        byte[] encoded = Program.ReadBounded(request.ImagePath, 64 * 1024 * 1024);
        Program.RequireHash(encoded, request.ImageSha256);
        Program.RequireHash(Program.ReadBounded(request.ModelPath, 64 * 1024 * 1024), Program.ModelHash);
        Program.RequireHash(Program.ReadBounded(request.ManifestPath, 128 * 1024), Program.ManifestHash);
        Program.RequireHash(Program.ReadBounded(request.LicensePath, 128 * 1024), "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4");
        Program.RequireHash(Program.ReadBounded(request.NoticePath, 128 * 1024), "8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729");
        (OcrImage image, _, _) = Program.Decode(encoded, request.SourceCropXywh, token);
        double[] p = request.PlotLtrb;
        var plot = new OcrRectangle(p[0], p[1], p[2] - p[0], p[3] - p[1]);
        if (!plot.IsValid || p.Any(value => !double.IsFinite(value)) || plot.Left < 0 || plot.Top < 0 ||
            plot.Right >= image.Width || plot.Bottom >= image.Height) throw new InvalidDataException("Plot is outside the owned crop.");
        IReadOnlyList<OcrDetectedRegion> components = await new ConnectedComponentTextRegionDetector().DetectAsync(image, token);
        OcrDetectedRegion[] candidates = Program.SelectCandidates(components, plot);
        if (candidates.Length is 0 or > 128) throw new InvalidDataException("Candidate count is outside the diagnostic bound.");
        var identity = new ModelIdentity("en_PP-OCRv5_mobile_rec", "5.0.0", Program.ModelHash, Path.GetFullPath(request.ModelPath));
        var options = ProductionOcrAdapter.ReadRecognitionOptions(identity, request.ManifestPath);
        var cropOptions = new OcrCropBatcherOptions
        {
            TargetWidth = options.Pipeline.CropWidth, TargetHeight = options.Pipeline.CropHeight,
            WidthMode = options.Pipeline.CropWidthMode, MaximumTargetWidth = options.Pipeline.MaximumCropWidth,
            BatchSize = options.Pipeline.BatchSize, PaddingPixels = options.Pipeline.CropPaddingPixels,
            HorizontalPaddingPixels = options.Pipeline.CropHorizontalPaddingPixels, VerticalPaddingPixels = options.Pipeline.CropVerticalPaddingPixels,
            VerticalContentPaddingRatio = options.Pipeline.CropVerticalContentPaddingRatio, ResizeMode = options.Pipeline.CropResizeMode,
            PaddingValue = options.Pipeline.CropPaddingValue,
        };
        IReadOnlyList<IReadOnlyList<OcrCrop>> batches = OcrCropBatcher.CreateBatches(image, candidates, cropOptions, token);
        var memoryBefore = Snapshot();
        DxgiLocalMemoryBudget? gpuBefore = selection is null ? null : WindowsDxgiAdapterInventory.TryReadLocalMemoryBudget(selection);
        OnnxInferenceSessionFactory factory = selection is null
            ? new(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled)
            : new(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled, OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR, selection, inventory);
        IInferenceSessionFactory guarded = selection is null ? factory : new NoCpuFactory(factory);
        var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(), guarded, CpuThreadConfiguration.Create(1));
        InferenceProvider expectedProvider = selection is null ? InferenceProvider.Cpu : InferenceProvider.DirectMl;
        IReadOnlyList<InferenceProvider> allowed = selection is null ? [InferenceProvider.Cpu] : [InferenceProvider.DirectMl, InferenceProvider.Cpu];
        await using var runtime = new InferenceRuntime(registry, new BoundedInferenceScheduler(1, 1), new Program.NoCache(), allowedProviders: allowed);
        var initialization = Stopwatch.StartNew();
        SessionAcquisition acquired = await registry.GetOrCreateAsync(identity, allowed, token);
        if (!acquired.Succeeded || acquired.Session!.Provider != expectedProvider) throw new InvalidOperationException("Requested provider unavailable; no CPU substitution was accepted.");
        initialization.Stop();
        ITextRecognizer recognizer = new LocalOnnxTextRecognizer(runtime, options.Recognizer with { BypassCache = true, AllowedProviders = allowed });
        if (ProductionOcrAdapter.UsesOfficialRecognitionSpacingV2Manifest(request.ManifestPath)) recognizer = new OfficialRecognitionSpacingV2TextRecognizer(recognizer);
        var rounds = new List<Round>();
        for (int round = 0; round < 4; round++)
        {
            var reads = new List<OcrRecognition>();
            double inferenceMs = 0, pacingMs = 0;
            foreach (IReadOnlyList<OcrCrop> batch in batches)
            {
                var stopwatch = Stopwatch.StartNew();
                reads.AddRange(await recognizer.RecognizeBatchAsync(batch, token));
                stopwatch.Stop();
                inferenceMs += stopwatch.Elapsed.TotalMilliseconds;
                if (selection is not null)
                {
                    var pace = Stopwatch.StartNew();
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(stopwatch.Elapsed.TotalMilliseconds)), token);
                    pacingMs += pace.Elapsed.TotalMilliseconds;
                }
            }
            if (reads.Count != candidates.Length || reads.Any(row => row.Failure is not null) ||
                reads.Select(row => row.RegionId).Distinct(StringComparer.Ordinal).Count() != candidates.Length)
                throw new InvalidDataException("Recognition failed or candidate count changed.");
            var byId = reads.ToDictionary(row => row.RegionId, StringComparer.Ordinal);
            Row[] rows = candidates.Select(candidate => MakeRow(candidate, byId[candidate.RegionId], request.SourceCropXywh)).ToArray();
            rounds.Add(new(inferenceMs, reads.Sum(row => row.InferenceMilliseconds), pacingMs, rows));
        }
        if (registry.CreatedSessionCount != 1) throw new InvalidOperationException("Expected exactly one session.");
        return new ChildReport(provider, selection?.Index, selection?.Luid, Program.Hash(requestBytes), request.ImageSha256,
            candidates.Length, initialization.Elapsed.TotalMilliseconds, total.Elapsed.TotalMilliseconds,
            new(budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores), memoryBefore, Snapshot(), gpuBefore,
            selection is null ? null : WindowsDxgiAdapterInventory.TryReadLocalMemoryBudget(selection), rounds.ToArray());
    }

    private static Row MakeRow(OcrDetectedRegion candidate, OcrRecognition reading, int[] crop)
    {
        string text = reading.Alternatives.Count > 0 ? reading.Alternatives[0].Text : "";
        double? value = GraphNumericParser.IsLiteralGraphNumber(text) ? GraphNumericParser.Parse(text).Value : null;
        return new(candidate.RegionId, text, value, candidate.Polygon.Points.Select(point => new[] { point.X + crop[0], point.Y + crop[1] }).ToArray());
    }

    private static ProcessMemory Snapshot()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return new(process.PrivateMemorySize64, process.WorkingSet64);
    }

    private sealed class NoCpuFactory(IInferenceSessionFactory inner) : IInferenceSessionFactory, IInferenceAdapterSelection
    {
        public string? DirectMlSelectionIdentity => ((IInferenceAdapterSelection)inner).DirectMlSelectionIdentity;
        public void ValidateDirectMlSelection() => ((IInferenceAdapterSelection)inner).ValidateDirectMlSelection();
        public ValueTask<IInferenceSession> CreateAsync(ModelIdentity model, InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration, CancellationToken cancellationToken) => provider == InferenceProvider.Cpu
                ? ValueTask.FromException<IInferenceSession>(new InvalidOperationException("CPU fallback disabled for hardware diagnostic."))
                : inner.CreateAsync(model, provider, cpuConfiguration, cancellationToken);
    }

    internal sealed record ChildOutcome(string Provider, int? AdapterIndex, string? Failure, ChildReport? Report, double WallMilliseconds);
    internal sealed record ChildReport(string Provider, int? AdapterIndex, long? AdapterLuid, string RequestSha256,
        string SourceSha256, int CandidateCount, double InitializationMilliseconds, double TotalMilliseconds,
        Budget CpuBudget, ProcessMemory MemoryBefore, ProcessMemory MemoryAfter, DxgiLocalMemoryBudget? GpuMemoryBefore,
        DxgiLocalMemoryBudget? GpuMemoryAfter, Round[] Rounds);
    internal sealed record Budget(uint LogicalProcessors, uint Rate, double MaximumLogicalCores);
    internal sealed record ProcessMemory(long PrivateBytes, long WorkingSetBytes);
    internal sealed record Round(double RecognitionMilliseconds, double ReportedModelInferenceMilliseconds, double PacingMilliseconds, Row[] Rows);
    internal sealed record Row(string RegionId, string Text, double? NumericValue, double[][] Polygon);
}
