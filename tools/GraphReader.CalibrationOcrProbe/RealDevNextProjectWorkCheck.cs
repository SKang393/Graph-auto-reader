// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.NativeAxis;
using GraphReader.Axis;
using GraphReader.Pdf;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string NextWorkLocatorHash = "1aee099f1153ea3774e17cb047a743e6215cae643e0ec69d94868ba25df1e274";
    private static readonly string[] NextWorkStages = ["runtime", "locator", "selection", "source_read", "preparation", "panel_binding", "native_work", "final_identity"];

    private static JsonElement ParseNextWorkLocator(byte[] bytes)
    {
        Program.RequireHash(bytes, NextWorkLocatorHash);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        if (root.GetProperty("zero_based_ordinal").GetInt32() != NextProjectOrdinal ||
            root.GetProperty("assignment_sha256").GetString() != CachedAxisAssignmentHash ||
            root.GetProperty("inventory_sha256").GetString() != CachedAxisInventoryHash ||
            root.GetProperty("panels").GetArrayLength() != 1 || root.GetProperty("panels")[0].GetProperty("index").GetInt32() != 0)
            throw new InvalidDataException("LOCATOR_MISMATCH");
        return root.Clone();
    }

    internal static Task<NextProjectOutcome> RunNextProjectWorkAsync(string corpus, string app, string assets,
        string rust, string locator, CpuBudgetJob budget, CancellationToken token) =>
        RunNextProjectWorkCoreAsync(corpus, app, assets, rust, locator, budget, false, token);

    internal static Task<NextProjectOutcome> RunNextProjectRustWorkAsync(string corpus, string app, string assets,
        string rust, string locator, CpuBudgetJob budget, CancellationToken token) =>
        RunNextProjectWorkCoreAsync(corpus, app, assets, rust, locator, budget, true, token);

    private static async Task<NextProjectOutcome> RunNextProjectWorkCoreAsync(string corpus, string app, string assets,
        string rust, string locator, CpuBudgetJob budget, bool rustOnly, CancellationToken token)
    {
        var stages = NextWorkStages.ToDictionary(s => rustOnly && s == "native_work" ? "rust_work" : s,
            _ => "unattempted", StringComparer.Ordinal);
        var timings = new Dictionary<string, double>(StringComparer.Ordinal);
        string stage = "runtime"; string? failure = null, projectHash = null, imageHash = null, panelHash = null, digestHash = null;
        int sourceAttempts = 0, sourceCompleted = 0, panelAttempts = 0, nativeCalls = 0, rustCalls = 0;
        bool identityVerified = false, nativeFinished = false, rustFinished = false;
        AxisGeometryWorkSnapshot? snapshot = null; RustAxisProposalResult? result = null;
        AxisDecisionTrace? decision = null; ReadOnlyMemory<byte> gray = default; string? grayHash = null;
        var total = Stopwatch.StartNew(); var step = Stopwatch.StartNew();
        Candidate? pins = null; FileStream? sourceLock = null; FileStream? nativeLock = null;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Next(string next) { stages[stage] = "completed"; timings[stage] = step.Elapsed.TotalMilliseconds; stage = next; step.Restart(); }
        try
        {
            token.ThrowIfCancellationRequested();
            if (budget.MaximumLogicalCores is <= 0 or > .5 || !new[] { corpus, app, assets, rust, locator }.All(Path.IsPathFullyQualified))
                throw new InvalidDataException("PREFLIGHT");
            if (rustOnly && !budget.SilentBreakawayVerified) throw new InvalidDataException("CHILD_BUDGET_UNAVAILABLE");
            pins = Candidate.VerifyNextProject(app, assets, rust, token);
            OpenCvSharp.Cv2.SetNumThreads(1);
            if (OpenCvSharp.Cv2.GetNumThreads() != 1) throw new InvalidDataException("OPENCV_THREADS");
            using (Process process = Process.GetCurrentProcess())
            {
                string[] modules = process.Modules.Cast<ProcessModule>().Select(m => m.FileName)
                    .Where(p => string.Equals(Path.GetFileName(p), "OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (modules.Length != 1 || !Path.IsPathFullyQualified(modules[0])) throw new InvalidDataException("OPENCV_MODULE");
                nativeLock = new FileStream(modules[0], FileMode.Open, FileAccess.Read, FileShare.Read);
                if (HashStream(nativeLock, token) != NextProjectRuntimeLibraries["OpenCvSharpExtern.dll"]) throw new InvalidDataException("OPENCV_IDENTITY");
            }
            var reader = ResolveEmbeddedImageReader();
            var panelizer = ResolvePanelizer(typeof(GraphReader.App.Services.CalibrationTickProposal).Assembly);
            Next("locator");
            JsonElement saved = ParseNextWorkLocator(Program.ReadBounded(locator, 4096));
            Next("selection");
            FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpus, token);
            FrozenRealCorpusProject selected = SelectNextProject(selector.Describe(), selector.EnumerateSelectedProjects(token));
            if (selected.AnonymizedCaseId != saved.GetProperty("anonymized_case_id").GetString()) throw new InvalidDataException("SELECTION_MISMATCH");
            _ = selector.RequireProject(selected.GetProjectPath(), selected.AnonymizedCaseId, selected.AnonymizedStudyId, token);
            Next("source_read"); sourceAttempts++;
            sourceLock = new FileStream(selected.GetProjectPath(), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(sourceLock, selected.GetProjectPath());
            if (sourceLock.Length is <= 0 or > EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes) throw new InvalidDataException("SOURCE_SIZE");
            projectHash = HashStream(sourceLock, token);
            ImageOnlyPayload payload = ReadLockedImageOnly(selector, selected, digest, reader, token);
            sourceCompleted++; imageHash = Program.Hash(payload.Image); digestHash = Convert.ToHexStringLower(digest.GetHashAndReset());
            if (projectHash != saved.GetProperty("project_sha256").GetString() || imageHash != saved.GetProperty("embedded_image_sha256").GetString() ||
                digestHash != saved.GetProperty("single_source_identity_digest").GetString()) throw new InvalidDataException("SOURCE_MISMATCH");
            Next("preparation");
            IReadOnlyList<object> prepared = await PrepareAsync(panelizer, payload.Image, imageHash, payload.Width, payload.Height, token).ConfigureAwait(false);
            if (prepared.Count != 1) throw new InvalidDataException("PANEL_COUNT");
            Next("panel_binding"); panelAttempts++;
            var panel = ReadPreparedPanel(prepared[0], payload.Width, payload.Height);
            panelHash = Program.Hash(panel.Bytes);
            JsonElement expected = saved.GetProperty("panels")[0]; JsonElement crop = expected.GetProperty("crop");
            if (panelHash != expected.GetProperty("image_sha256").GetString() || panel.Width != expected.GetProperty("width").GetInt32() ||
                panel.Height != expected.GetProperty("height").GetInt32() || panel.Crop != new PdfRectD(crop.GetProperty("x").GetDouble(),
                    crop.GetProperty("y").GetDouble(), crop.GetProperty("width").GetDouble(), crop.GetProperty("height").GetDouble()))
                throw new InvalidDataException("PANEL_MISMATCH");
            Next(rustOnly ? "rust_work" : "native_work");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            if (rustOnly)
            {
                var decoded = Program.Decode(panel.Bytes, [0, 0, panel.Width, panel.Height], deadline.Token);
                if (decoded.Width != panel.Width || decoded.Height != panel.Height ||
                    decoded.Image.Width != panel.Width || decoded.Image.Height != panel.Height)
                    throw new InvalidDataException("GRAY_DIMENSIONS_MISMATCH");
                gray = decoded.Image.Pixels;
                grayHash = Convert.ToHexStringLower(SHA256.HashData(gray.Span));
                rustCalls++;
                AxisDecisionDiagnosticResult diagnostic = await AxisDecisionDiagnosticClient.RunAsync(rust,
                    panel.Width, panel.Height, gray, deadline.Token).ConfigureAwait(false);
                result = diagnostic.Proposal; decision = diagnostic.Trace; rustFinished = true;
                if (Convert.ToHexStringLower(SHA256.HashData(gray.Span)) != grayHash) throw new InvalidDataException("GRAY_BYTES_CHANGED");
            }
            else
            {
                var axis = new NativeWorkTraceAxis();
                nativeCalls++;
                try { result = await axis.SuggestAsync(panel.Bytes, deadline.Token).ConfigureAwait(false); nativeFinished = true; }
                finally { snapshot = axis.LastSnapshot; }
                if (snapshot is null) throw new InvalidDataException("WORK_SNAPSHOT_MISSING");
            }
            Next("final_identity");
            sourceLock.Position = 0;
            EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(sourceLock, selected.GetProjectPath());
            identityVerified = HashStream(sourceLock, token) == projectHash && Program.Hash(payload.Image) == imageHash &&
                Program.Hash(panel.Bytes) == panelHash && (!rustOnly || Convert.ToHexStringLower(SHA256.HashData(gray.Span)) == grayHash) &&
                SelectNextProject(selector.Describe(), selector.EnumerateSelectedProjects(token)).AnonymizedCaseId == selected.AnonymizedCaseId;
            if (!identityVerified) throw new InvalidDataException("FINAL_IDENTITY");
            stages[stage] = "completed"; timings[stage] = step.Elapsed.TotalMilliseconds;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = error is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : TraceExceptionCategory(error);
            stages[stage] = "failed"; timings[stage] = step.Elapsed.TotalMilliseconds;
        }
        finally { nativeLock?.Dispose(); sourceLock?.Dispose(); pins?.Dispose(); }
        bool success = failure is null && identityVerified && (rustOnly ? rustFinished && decision is not null : nativeFinished && snapshot is not null);
        if (rustOnly)
            return new(new { schema = "graphreader.real-dev-next-project-rust-work.v1", status = success ? "completed" : "refused_or_failed",
                failure, stages, timings, declared_projects = 1, source_attempted = sourceAttempts, source_completed = sourceCompleted,
                source_failed = sourceAttempts - sourceCompleted, source_unattempted = 1 - sourceAttempts, declared_panels = 1,
                panel_attempted = panelAttempts, panel_unattempted = 1 - panelAttempts, rust_worker_calls = rustCalls,
                rust_completed = rustFinished ? 1 : 0, rust_failed = rustCalls - (rustFinished ? 1 : 0), rust_unattempted = 1 - rustCalls,
                rust_error = result?.ErrorCode, rust_reasons = result?.ReviewReasons, decision_trace = decision,
                decision_route = decision?.Route.ToString(), decision_reason = decision?.Reason.ToString(),
                geometry_returned = result?.Geometry is not null,
                source_project_sha256 = projectHash, source_image_sha256 = imageHash, panel_image_sha256 = panelHash,
                gray_sha256 = grayHash, gray_bytes = gray.Length, gray_normalization = "WPF WhiteBackgroundRaster then Gray8, full bound panel, no resize",
                single_source_identity_digest = digestHash, locator_sha256 = NextWorkLocatorHash,
                runtime_profile_sha256 = NextProjectRuntimePinsHash, final_identity_verified = identityVerified,
                elapsed_milliseconds = total.Elapsed.TotalMilliseconds, budget.MaximumLogicalCores,
                timing_scope = "One Rust diagnostic child and the bound preparation pipeline; no native geometry or historical latency comparison.",
                private_source_projects_attempted = sourceAttempts, embedded_image_extractions_completed = sourceCompleted,
                other_project_body_reads = 0, sealed_reads = 0,
                native_calls = nativeCalls, ocr_calls = 0, tick_calls = 0, truth_reads = 0, images_written = false }, success);
        return new(new { schema = "graphreader.real-dev-next-project-native-work.v1", status = success ? "completed" : "refused_or_failed",
            failure, stages, timings, declared_projects = 1, source_attempted = sourceAttempts, source_completed = sourceCompleted,
            source_failed = sourceAttempts - sourceCompleted, source_unattempted = 1 - sourceAttempts, declared_panels = 1,
            panel_attempted = panelAttempts, panel_unattempted = 1 - panelAttempts, native_calls = nativeCalls,
            native_completed = nativeFinished ? 1 : 0, native_failed = nativeCalls - (nativeFinished ? 1 : 0), native_unattempted = 1 - nativeCalls,
            native_error = result?.ErrorCode, native_reasons = result?.ReviewReasons, work = snapshot,
            source_project_sha256 = projectHash, source_image_sha256 = imageHash, panel_image_sha256 = panelHash,
            single_source_identity_digest = digestHash, locator_sha256 = NextWorkLocatorHash,
            runtime_profile_sha256 = NextProjectRuntimePinsHash, final_identity_verified = identityVerified,
            elapsed_milliseconds = total.Elapsed.TotalMilliseconds, budget.MaximumLogicalCores,
            timing_scope = "In-process native observation; not comparable to child-process latency.",
            private_source_projects_attempted = sourceAttempts, embedded_image_extractions_completed = sourceCompleted,
            other_project_body_reads = 0, sealed_reads = 0,
            rust_worker_calls = 0, ocr_calls = 0, tick_calls = 0, truth_reads = 0, images_written = false }, success);
    }

    internal static async Task<object> NextProjectWorkSelfTestAsync(CancellationToken token)
    {
        OpenCvSharp.Cv2.SetNumThreads(1);
        if (OpenCvSharp.Cv2.GetNumThreads() != 1) throw new InvalidDataException("OPENCV_THREADS");
        bool refused = false;
        try { _ = ParseNextWorkLocator("{}"u8.ToArray()); } catch (InvalidDataException) { refused = true; }
        if (!refused) throw new InvalidDataException("LOCATOR_TEST");
        byte[] gray = Enumerable.Repeat((byte)255, 320 * 240).ToArray();
        for (int x = 40; x <= 280; x++) gray[200 * 320 + x] = 0;
        for (int y = 40; y <= 200; y++) gray[y * 320 + 40] = 0;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(320, 240, 96, 96, PixelFormats.Gray8, null, gray, 320)));
        using var buffer = new MemoryStream(); encoder.Save(buffer); byte[] image = buffer.ToArray();
        RustAxisProposalResult plain = await NativeAxisWorker.ProcessAsync(image, token).ConfigureAwait(false);
        var observed = new NativeWorkTraceAxis();
        RustAxisProposalResult withObserver = await observed.SuggestAsync(image, token).ConfigureAwait(false);
        if (observed.LastSnapshot is null || JsonSerializer.Serialize(plain, Program.Json) != JsonSerializer.Serialize(withObserver, Program.Json))
            throw new InvalidDataException("OBSERVER_PARITY");
        return new { schema = "graphreader.real-dev-next-project-native-work-self-test.v1", locator_tampering_refused = true,
            native_owned_calls = 2, observer_parity = true, private_reads = 0 };
    }
}
