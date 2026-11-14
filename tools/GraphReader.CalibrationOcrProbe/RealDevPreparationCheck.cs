// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Pdf;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private sealed class PreparationStats
    {
        internal int Zero, One, Many, Failed, Panels, PanelAttempted, RawSuccess, PreparedSuccess;
        internal int RawX, RawY, PreparedX, PreparedY, UnsupportedRaw, UnsupportedPanel, PanelErrors, FallbackSuccess;
        internal int RawZero, RawOne, RawMany, RawFailed, EffectiveSourceSuccess;
        internal readonly List<double> SourceWidths = [], SourceHeights = [], PanelWidths = [], PanelHeights = [], Coverage = [], PrepareMs = [], RawMs = [], PanelMs = [];
    }

    // Resolve the actual packaged implementation before selecting private payloads.
    private static (object Instance, MethodInfo Method) ResolvePanelizer(Assembly assembly)
    {
        Type type = assembly.GetType("GraphReader.App.Integration.Workflow.ProductionRasterPanelizer", false) ?? throw new InvalidDataException();
        MethodInfo method = type.GetMethod("PanelizeAsync", [typeof(ImmutableByteBuffer), typeof(string), typeof(int), typeof(int), typeof(CancellationToken)]) ?? throw new InvalidDataException();
        object instance = Activator.CreateInstance(type, [null]) ?? throw new InvalidDataException();
        return (instance, method);
    }

    private static bool IsGenuineNoPanel(Exception error) =>
        error.GetType().FullName == "GraphReader.App.Integration.Workflow.ProductionRasterPanelizationException" &&
        error.GetType().GetProperty("Code")?.GetValue(error) as string == "RASTER_NO_PANEL_DETECTED";

    private static async Task<IReadOnlyList<object>> PrepareAsync((object Instance, MethodInfo Method) panelizer,
        byte[] image, string hash, int width, int height, CancellationToken token)
    {
        object? invocation = panelizer.Method.Invoke(panelizer.Instance, [new ImmutableByteBuffer(image), hash, width, height, token]);
        Task task = invocation as Task ?? throw new InvalidDataException();
        await task.ConfigureAwait(false);
        object result = task.GetType().GetProperty("Result")?.GetValue(task) ?? throw new InvalidDataException();
        IEnumerable panels = result.GetType().GetProperty("Panels")?.GetValue(result) as IEnumerable ?? throw new InvalidDataException();
        return panels.Cast<object>().ToArray();
    }

    private static (byte[] Bytes, int Width, int Height, PdfRectD Crop) ReadPreparedPanel(object panel, int sourceWidth, int sourceHeight)
    {
        Type type = panel.GetType();
        int width = (int)(type.GetProperty("Width")?.GetValue(panel) ?? throw new InvalidDataException());
        int height = (int)(type.GetProperty("Height")?.GetValue(panel) ?? throw new InvalidDataException());
        var crop = (PdfRectD)(type.GetProperty("EncodedCropInSourcePixels")?.GetValue(panel) ?? throw new InvalidDataException());
        string hash = (string)(type.GetProperty("Sha256")?.GetValue(panel) ?? throw new InvalidDataException());
        byte[] bytes = (byte[])(type.GetMethod("CopyEncodedBytes")?.Invoke(panel, null) ?? throw new InvalidDataException());
        if (width <= 0 || height <= 0 || !crop.IsValid || crop.X < 0 || crop.Y < 0 ||
            crop.Right > sourceWidth || crop.Bottom > sourceHeight || crop.Width != width || crop.Height != height ||
            Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash) throw new InvalidDataException();
        double[] forward = ((IEnumerable<double>)(type.GetProperty("SourceToPanelMatrix")?.GetValue(panel) ?? throw new InvalidDataException())).ToArray();
        double[] reverse = ((IEnumerable<double>)(type.GetProperty("PanelToSourceMatrix")?.GetValue(panel) ?? throw new InvalidDataException())).ToArray();
        if (!forward.SequenceEqual(new double[] { 1, 0, -crop.X, 0, 1, -crop.Y, 0, 0, 1 }) ||
            !reverse.SequenceEqual(new double[] { 1, 0, crop.X, 0, 1, crop.Y, 0, 0, 1 })) throw new InvalidDataException();
        return (bytes, width, height, crop);
    }

    private static async Task<RustAxisProposalResult?> GeometryAsync(byte[] image, int width, int height, string rust, CancellationToken token)
    {
        if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216) return null;
        var (decoded, _, _) = Program.Decode(image, [0, 0, width, height], token);
        return await RustAxisProposal.RunAsync(rust, width, height, decoded.Pixels, cancellationToken: token).ConfigureAwait(false);
    }

    internal static async Task<object> RunPreparationAsync(string corpusRoot, string appExe, string assetRoot, string rustExe,
        CpuBudgetJob parentBudget, CancellationToken token)
    {
        if (!parentBudget.SilentBreakawayVerified || !Path.IsPathFullyQualified(corpusRoot) || !Path.IsPathFullyQualified(appExe) ||
            !Path.IsPathFullyQualified(assetRoot) || !Path.IsPathFullyQualified(rustExe)) throw new InvalidDataException();
        using Candidate candidate = Candidate.Verify(appExe, assetRoot, rustExe, token);
        var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpusRoot, token);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        FrozenRealCorpusProject[] selected = selector.EnumerateSelectedProjects(token).OrderBy(p => p.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
        if (selected.Length != Denominator || selected.Any(p => p.Split != FrozenRealCorpusInventory.RealDev)) throw new InvalidDataException();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var totals = new Totals(); var stats = new PreparationStats(); var wall = Stopwatch.StartNew();
        foreach (FrozenRealCorpusProject project in selected)
        {
            if (token.IsCancellationRequested) { totals.Reason("OVERALL_DEADLINE_UNATTEMPTED", Denominator - totals.Attempted); break; }
            totals.Attempted++;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                Payload payload = ReadLocked(selector, project, digest, totals, deadline.Token);
                stats.SourceWidths.Add(payload.Width); stats.SourceHeights.Add(payload.Height);
                var timer = Stopwatch.StartNew();
                RustAxisProposalResult? raw = null;
                try
                {
                    raw = await GeometryAsync(payload.Image, payload.Width, payload.Height, rustExe, deadline.Token).ConfigureAwait(false);
                    if (raw is null) stats.UnsupportedRaw++;
                    else if (raw.Geometry is { } rawGeometry)
                    { stats.RawSuccess++; if (rawGeometry.XTickPositions.Count >= 3) stats.RawX++; if (rawGeometry.YTickPositions.Count >= 3) stats.RawY++; }
                    else totals.Reason("RAW_" + AxisReason(raw.ErrorCode));
                }
                catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
                { totals.Reason("RAW_GEOMETRY_FAILED"); }
                finally { stats.RawMs.Add(timer.Elapsed.TotalMilliseconds); }
                timer.Restart();
                IReadOnlyList<object> panels;
                try { panels = await PrepareAsync(panelizer, payload.Image, payload.ImageHash, payload.Width, payload.Height, deadline.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
                {
                    if (IsGenuineNoPanel(error))
                    { stats.Zero++; if (raw?.Geometry is not null) { stats.FallbackSuccess++; stats.RawZero++; stats.EffectiveSourceSuccess++; } }
                    else { stats.Failed++; if (raw?.Geometry is not null) stats.RawFailed++; totals.Reason("PREPARATION_FAILED"); }
                    continue;
                }
                finally { stats.PrepareMs.Add(timer.Elapsed.TotalMilliseconds); }
                if (panels.Count == 0) { stats.Failed++; if (raw?.Geometry is not null) stats.RawFailed++; totals.Reason("PREPARATION_EMPTY_UNEXPECTED"); continue; }
                if (panels.Count == 1) { stats.One++; if (raw?.Geometry is not null) stats.RawOne++; }
                else { stats.Many++; if (raw?.Geometry is not null) stats.RawMany++; }
                stats.Panels += panels.Count;
                bool sourceGeometryCounted = false;
                foreach (object panel in panels)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    stats.PanelAttempted++;
                    try
                    {
                        var (bytes, width, height, crop) = ReadPreparedPanel(panel, payload.Width, payload.Height);
                        stats.PanelWidths.Add(width); stats.PanelHeights.Add(height);
                        stats.Coverage.Add(crop.Width * crop.Height / ((double)payload.Width * payload.Height));
                        timer.Restart();
                        RustAxisProposalResult? result = await GeometryAsync(bytes, width, height, rustExe, deadline.Token).ConfigureAwait(false);
                        stats.PanelMs.Add(timer.Elapsed.TotalMilliseconds);
                        if (result is null) stats.UnsupportedPanel++;
                        else if (result.Geometry is { } geometry)
                        { stats.PreparedSuccess++; if (!sourceGeometryCounted) { stats.EffectiveSourceSuccess++; sourceGeometryCounted = true; }
                            if (geometry.XTickPositions.Count >= 3) stats.PreparedX++; if (geometry.YTickPositions.Count >= 3) stats.PreparedY++; }
                        else totals.Reason("PREPARED_" + AxisReason(result.ErrorCode));
                    }
                    catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
                    { stats.PanelErrors++; totals.Reason("PREPARED_PANEL_FAILED"); }
                }
            }
            catch (OperationCanceledException) { totals.Reason(token.IsCancellationRequested ? "OVERALL_DEADLINE_CASE" : "CASE_TIMEOUT"); }
            catch (Exception error) when (error is not OutOfMemoryException) { totals.Reason("SOURCE_PROCESSING_FAILED"); }
            finally { SampleMemory(totals, 0); }
        }
        _ = selector.EnumerateSelectedProjects(CancellationToken.None);
        var report = new { schema = "graphreader.real-dev-preparation-diagnostic.v1",
            status = totals.Attempted == Denominator && !totals.Reasons.ContainsKey("OVERALL_DEADLINE_CASE") ? "completed" : "incomplete_deadline",
            protocol = new { version = "0.4.47", denominator = Denominator, scope = "all_frozen_real_dev", sealed_payload_reads = 0,
                ocr_calls = 0, review_only = true, production_approved = false, model_selection = false, image_writes = false,
                per_case_output = false, source_identity_policy = "first_observed_locked_payloads_not_preregistered_content" },
            candidate = new { app_exe_sha256 = AppExeHash, app_dll_sha256 = AppDllHash, rust_sha256 = RustHash, runtime_files = candidate.Files },
            metadata.AssignmentSha256, metadata.SelectedInventorySha256, source_identity_digest = Convert.ToHexStringLower(digest.GetHashAndReset()),
            source_identity_digest_complete = totals.PayloadsHashed == Denominator && totals.ImagesBound == Denominator,
            parent_cpu_budget = new { parentBudget.LogicalProcessors, parentBudget.Rate, parentBudget.MaximumLogicalCores, parentBudget.SilentBreakawayVerified,
                scope = "parent_preprocessing_only_children_independently_capped", priority = "BelowNormal" },
            sources = new { denominator = Denominator, attempted = totals.Attempted, payloads_hashed = totals.PayloadsHashed, images_bound = totals.ImagesBound,
                zero_panels = stats.Zero, one_panel = stats.One, multiple_panels = stats.Many, preparation_failed = stats.Failed,
                preparation_unclassified = totals.Attempted - stats.Zero - stats.One - stats.Many - stats.Failed,
                raw_geometry_success = stats.RawSuccess, raw_success_zero_panels = stats.RawZero, raw_success_one_panel = stats.RawOne,
                raw_success_multiple_panels = stats.RawMany, raw_success_preparation_failed = stats.RawFailed,
                raw_success_preparation_unclassified = stats.RawSuccess - stats.RawZero - stats.RawOne - stats.RawMany - stats.RawFailed,
                raw_x_ticks_at_least_three = stats.RawX, raw_y_ticks_at_least_three = stats.RawY, unsupported_raw = stats.UnsupportedRaw },
            panels = new { returned = stats.Panels, attempted = stats.PanelAttempted, geometry_success = stats.PreparedSuccess,
                x_ticks_at_least_three = stats.PreparedX, y_ticks_at_least_three = stats.PreparedY, unsupported = stats.UnsupportedPanel,
                processing_errors = stats.PanelErrors, unattempted = stats.Panels - stats.PanelAttempted },
            effective_import = new { full_image_no_panel_fallback_sources = stats.Zero, returned_or_fallback_panels = stats.Panels + stats.Zero,
                geometry_success = stats.PreparedSuccess + stats.FallbackSuccess, sources_with_at_least_one_geometry_success = stats.EffectiveSourceSuccess,
                fallback_geometry_reuses_raw_result = true },
            reason_counts = totals.Reasons.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value),
            dimensions = new { source_width = Stats(stats.SourceWidths), source_height = Stats(stats.SourceHeights), panel_width = Stats(stats.PanelWidths), panel_height = Stats(stats.PanelHeights), crop_coverage_fraction = Stats(stats.Coverage) },
            timing_milliseconds = new { total_wall = wall.Elapsed.TotalMilliseconds, preparation = Stats(stats.PrepareMs), raw_geometry = Stats(stats.RawMs), panel_geometry = Stats(stats.PanelMs) },
            memory = new { totals.PeakSampledParentBytes, totals.PeakParentWorkingSetBytes, sampling_is_not_a_hard_memory_ceiling = true } };
        EnsureAggregateOnly(JsonSerializer.SerializeToElement(report, Program.Json));
        return report;
    }

    internal static async Task<object> PreparationSelfTestAsync(string rustExe, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(rustExe) || !File.Exists(rustExe)) throw new InvalidDataException();
        var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
        using Process process = Process.GetCurrentProcess();
        TimeSpan cpuStart = process.TotalProcessorTime;
        var overall = Stopwatch.StartNew();
        var outcomes = new List<object>();
        for (int mode = 0; mode < 3; mode++)
        {
            var controlTimer = Stopwatch.StartNew();
            const int width = 640, height = 480;
            byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
            void Plot(int top, int bottom) {
                for (int x = 100; x <= 540; x++) pixels[bottom * width + x] = 0;
                for (int y = top; y <= bottom; y++) pixels[y * width + 100] = 0;
                for (int x = 140; x <= 540; x += 200) for (int y = bottom + 1; y <= bottom + 4; y++) pixels[y * width + x] = 0;
                foreach (int y in new[] { top + 10, (top + bottom) / 2, bottom - 10 }) for (int x = 96; x < 100; x++) pixels[y * width + x] = 0;
            }
            if (mode == 1) { Plot(30, 150); Plot(230, 350); } else Plot(80, 350);
            if (mode == 2) for (int x = 0; x < width; x += 2) pixels[450 * width + x] = 0;
            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); byte[] image = stream.ToArray();
            string hash = Convert.ToHexStringLower(SHA256.HashData(image));
            var (sourceImage, _, _) = Program.Decode(image, [0, 0, width, height], token);
            var stageTimer = Stopwatch.StartNew();
            RustAxisProposalResult? raw = await GeometryAsync(image, width, height, rustExe, token).ConfigureAwait(false);
            double rawMilliseconds = stageTimer.Elapsed.TotalMilliseconds;
            int count = 0, success = 0; string status = "ok";
            double preparationMilliseconds = 0, panelsMilliseconds = 0;
            stageTimer.Restart();
            try
            {
                var panels = await PrepareAsync(panelizer, image, hash, width, height, token).ConfigureAwait(false);
                preparationMilliseconds = stageTimer.Elapsed.TotalMilliseconds;
                count = panels.Count;
                stageTimer.Restart();
                foreach (object panel in panels)
                {
                    var (bytes, panelWidth, panelHeight, crop) = ReadPreparedPanel(panel, width, height);
                    if (crop.X != Math.Truncate(crop.X) || crop.Y != Math.Truncate(crop.Y)) throw new InvalidDataException();
                    var (panelImage, decodedWidth, decodedHeight) = Program.Decode(bytes, [0, 0, panelWidth, panelHeight], token);
                    if (decodedWidth != panelWidth || decodedHeight != panelHeight) throw new InvalidDataException();
                    int cropX = checked((int)crop.X), cropY = checked((int)crop.Y);
                    for (int row = 0; row < panelHeight; row++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!sourceImage.Pixels.Span.Slice((cropY + row) * width + cropX, panelWidth)
                            .SequenceEqual(panelImage.Pixels.Span.Slice(row * panelWidth, panelWidth))) throw new InvalidDataException();
                    }
                    var result = await GeometryAsync(bytes, panelWidth, panelHeight, rustExe, token).ConfigureAwait(false);
                    if (result?.Geometry is not null) success++;
                }
                panelsMilliseconds = stageTimer.Elapsed.TotalMilliseconds;
            }
            catch (Exception error) when (IsGenuineNoPanel(error))
            { status = "no_panel"; preparationMilliseconds = stageTimer.Elapsed.TotalMilliseconds; }
            if (Convert.ToHexStringLower(SHA256.HashData(image)) != hash) throw new InvalidDataException();
            outcomes.Add(new { control = mode switch { 0 => "single_plot", 1 => "two_plots", _ => "caption_margin" },
                raw_geometry = raw?.Geometry is not null, preparation_status = status, panel_count = count, panel_geometry_success = success,
                timing_milliseconds = new { raw_geometry = rawMilliseconds, preparation = preparationMilliseconds,
                    panel_validation_and_geometry = panelsMilliseconds, total = controlTimer.Elapsed.TotalMilliseconds } });
        }
        bool rejected = false;
        try { ResolvePanelizer(typeof(string).Assembly); } catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new InvalidDataException();
        process.Refresh();
        var output = new { schema = "graphreader.preparation-self-test.v1", source_payload_reads = 0, missing_capability_rejected = rejected,
            total_wall_milliseconds = overall.Elapsed.TotalMilliseconds,
            parent_process_cpu_milliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
            cpu_scope = "parent_only_excludes_rust_children", controls = outcomes };
        EnsureAggregateOnly(JsonSerializer.SerializeToElement(output, Program.Json));
        return output;
    }
}
