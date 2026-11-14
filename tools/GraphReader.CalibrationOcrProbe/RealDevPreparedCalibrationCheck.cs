// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Pdf;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private sealed class PreparedCalibrationStats
    {
        internal int Zero, One, Many, Returned, Fallback, Attempted, Unsupported, PanelErrors, ProjectErrors;
        internal int ProjectsAnyComplete, ProjectsNoneComplete, ProjectsIncomplete, ProjectsIncompleteAny, CompletedPanels, EstimatedPanels;
        internal readonly HashSet<string> ImageHashes = new(StringComparer.Ordinal);
        internal readonly List<double> PreparationMs = [];
    }

    private sealed class ObservedAxis(IAxisProposalService inner) : IAxisProposalService
    {
        internal RustAxisProposalResult? Result { get; private set; }
        public async Task<RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token) =>
            Result = await inner.SuggestAsync(image, token).ConfigureAwait(false);
    }

    internal static async Task<object> PreparedKnownControlAsync(string appExe, string assetRoot, string rustExe,
        CpuBudgetJob budget, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified) throw new InvalidDataException();
        using Candidate candidate = Candidate.Verify(appExe, assetRoot, rustExe, token);
        var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
        IAxisProposalService inner = new RustFirstAxisProposalService(new RustAxisProposalService(rustExe), new OpenCvAxisProposalService(appExe));
        await using var ocr = new CalibrationOcrWorkerClient(appExe, assetRoot, () => CalibrationOcrDeviceSelection.Cpu);
        const int width = 640, height = 480;
        byte[] basePixels = OwnedPreparationPixels(0);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            int[] xs = [140, 340, 540]; string[] xv = ["1", "2", "3"];
            int[] ys = [90, 215, 340]; string[] yv = ["10", "5", "0"];
            for (int i = 0; i < 3; i++)
            {
                drawing.DrawText(new FormattedText(xv[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 16, Brushes.Black, 1), new Point(xs[i] - 5, 356));
                drawing.DrawText(new FormattedText(yv[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 16, Brushes.Black, 1), new Point(70, ys[i] - 8));
            }
        }
        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); rendered.Render(visual);
        var labels = new FormatConvertedBitmap(rendered, PixelFormats.Gray8, null, 0);
        byte[] labelPixels = new byte[width * height]; labels.CopyPixels(labelPixels, width, 0);
        byte[] combined = (byte[])basePixels.Clone();
        for (int i = 0; i < combined.Length; i++) if (labelPixels[i] < combined[i]) combined[i] = labelPixels[i];
        for (int i = 0; i < combined.Length; i++) if (basePixels[i] == 0 && combined[i] != 0) throw new InvalidDataException();
        byte[] baseline = EncodeOwnedGray(basePixels, width, height);
        byte[] single = EncodeOwnedGray(combined, width, height);
        var controls = new List<object>();
        foreach ((string name, byte[] image) in new[] { ("unlabelled_raw", baseline), ("labelled_raw", single) })
        {
            var observed = new ObservedAxis(inner); var totals = new Totals(); var stats = new PreparedCalibrationStats();
            string hash = Convert.ToHexStringLower(SHA256.HashData(image));
            _ = await InferPreparedPanelAsync(image, width, height, new PdfRectD(0, 0, width, height), width, height,
                hash, observed, ocr, candidate, totals, stats, token).ConfigureAwait(false);
            controls.Add(new { control = name, geometry = observed.Result, ocr_returned = totals.OcrReturned,
                numeric_rows = totals.NumericRows, measured_complete = stats.CompletedPanels, estimated_complete = stats.EstimatedPanels,
                reason_counts = totals.Reasons });
            if (name == "labelled_raw" && totals.OcrReturned != 1)
                return new { schema = "graphreader.prepared-known-owned-check.v1", status = "raw_control_incomplete", controls };
        }
        byte[] duplicated = new byte[combined.Length * 2]; combined.CopyTo(duplicated, 0); combined.CopyTo(duplicated, combined.Length);
        byte[] stacked = EncodeOwnedGray(duplicated, width, height * 2);
        foreach ((string name, byte[] image, int sourceHeight) in new[] { ("single_prepared", single, height), ("stacked_prepared", stacked, height * 2) })
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(image));
            var panels = await PrepareAsync(panelizer, image, hash, width, sourceHeight, token).ConfigureAwait(false);
            var outcomes = new List<object>();
            foreach (object panel in panels)
            {
                var (bytes, panelWidth, panelHeight, crop) = ReadPreparedPanel(panel, width, sourceHeight);
                var observed = new ObservedAxis(inner); var totals = new Totals(); var stats = new PreparedCalibrationStats();
                _ = await InferPreparedPanelAsync(bytes, panelWidth, panelHeight, crop, width, sourceHeight,
                    hash, observed, ocr, candidate, totals, stats, token).ConfigureAwait(false);
                outcomes.Add(new { width = panelWidth, height = panelHeight, crop, geometry = observed.Result,
                    ocr_returned = totals.OcrReturned, numeric_rows = totals.NumericRows, measured_complete = stats.CompletedPanels,
                    estimated_complete = stats.EstimatedPanels, reason_counts = totals.Reasons });
            }
            controls.Add(new { control = name, source_width = width, source_height = sourceHeight, returned_panels = panels.Count, panels = outcomes });
        }
        return new { schema = "graphreader.prepared-known-owned-check.v1", status = "completed", review_only = true,
            accuracy_claim = false, source_payload_reads = 0, image_writes = false, baseline_axes_preserved = true, controls };
    }

    private static byte[] EncodeOwnedGray(byte[] pixels, int width, int height)
    {
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private static async Task<bool> InferPreparedPanelAsync(byte[] image, int width, int height, PdfRectD crop,
        int sourceWidth, int sourceHeight, string projectHash, IAxisProposalService axis,
        CalibrationOcrWorkerClient ocr, Candidate candidate, Totals totals, PreparedCalibrationStats stats, CancellationToken token)
    {
        stats.Attempted++;
        if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216)
        { stats.Unsupported++; return false; }
        string panelHash = Convert.ToHexStringLower(SHA256.HashData(image));
        var (measured, draft) = await InferAsync(image, width, height, projectHash, panelHash, axis, ocr, candidate, totals, token).ConfigureAwait(false);
        if (measured is { IsComplete: true }) { ValidatePreparedMapping(measured, crop, sourceWidth, sourceHeight); stats.CompletedPanels++; }
        if (draft is { IsComplete: true })
        {
            ValidatePreparedMapping(draft, crop, sourceWidth, sourceHeight);
            if (draft.UsesEstimatedPositions) stats.EstimatedPanels++;
        }
        return measured is { IsComplete: true } || draft is { IsComplete: true };
    }

    private static void ValidatePreparedMapping(CalibrationTickProposalResult proposal, PdfRectD crop, int width, int height)
    {
        CalibrationTickAnchors anchors = proposal.Anchors ?? throw new InvalidDataException();
        foreach (CalibrationOcrPoint p in new[] { anchors.FirstSessionY0, anchors.FirstSessionYMaximum, anchors.FinalSessionY0 })
        {
            double x = p.X + crop.X, y = p.Y + crop.Y;
            if (!double.IsFinite(x) || !double.IsFinite(y) || p.X < 0 || p.Y < 0 || p.X >= crop.Width || p.Y >= crop.Height ||
                x < 0 || y < 0 || x >= width || y >= height) throw new InvalidDataException();
        }
    }

    internal static async Task<object> PreparedCalibrationOwnedCheckAsync(string appExe, string assetRoot, string rustExe,
        CpuBudgetJob budget, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified) throw new InvalidDataException();
        using Candidate candidate = Candidate.Verify(appExe, assetRoot, rustExe, token);
        var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
        IAxisProposalService axis = new RustFirstAxisProposalService(new RustAxisProposalService(rustExe), new OpenCvAxisProposalService(appExe));
        await using var ocr = new CalibrationOcrWorkerClient(appExe, assetRoot, () => CalibrationOcrDeviceSelection.Cpu);
        var controls = new List<object>();
        for (int mode = 0; mode < 2; mode++)
        {
        const int width = 640, height = 480;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var pen = new Pen(Brushes.Black, 1);
            (int Top, int Bottom)[] ranges = mode == 0 ? [(30, 150), (230, 350)] : [(80, 380)];
            foreach ((int top, int bottom) in ranges)
            {
                drawing.DrawLine(pen, new Point(100, top), new Point(100, bottom));
                drawing.DrawLine(pen, new Point(100, bottom), new Point(540, bottom));
                int[] xs = [140, 340, 540]; string[] xv = ["1", "2", "3"];
                for (int i = 0; i < xs.Length; i++)
                {
                    drawing.DrawLine(pen, new Point(xs[i], bottom), new Point(xs[i], bottom + 4));
                    drawing.DrawText(new FormattedText(xv[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 16, Brushes.Black, 1), new Point(xs[i] - 5, bottom + 5));
                }
                int[] ys = mode == 0 ? [top + 10, (top + bottom) / 2, bottom - 10] : [top, (top + bottom) / 2, bottom];
                string[] yv = ["10", "5", "0"];
                for (int i = 0; i < ys.Length; i++)
                {
                    drawing.DrawLine(pen, new Point(96, ys[i]), new Point(100, ys[i]));
                    drawing.DrawText(new FormattedText(yv[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 16, Brushes.Black, 1), new Point(70, ys[i] - 8));
                }
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var encoded = new MemoryStream(); encoder.Save(encoded); byte[] image = encoded.ToArray();
        string hash = Convert.ToHexStringLower(SHA256.HashData(image));
        var (source, _, _) = Program.Decode(image, [0, 0, width, height], token);
        IReadOnlyList<object> panels = await PrepareAsync(panelizer, image, hash, width, height, token).ConfigureAwait(false);
        if (panels.Count == 0) throw new InvalidDataException();
        var totals = new Totals(); var stats = new PreparedCalibrationStats(); var timer = Stopwatch.StartNew();
        foreach (object panel in panels)
        {
            var (bytes, panelWidth, panelHeight, crop) = ReadPreparedPanel(panel, width, height);
            var (decoded, _, _) = Program.Decode(bytes, [0, 0, panelWidth, panelHeight], token);
            int x = checked((int)crop.X), y = checked((int)crop.Y);
            if (x != crop.X || y != crop.Y) throw new InvalidDataException();
            for (int row = 0; row < panelHeight; row++)
            {
                token.ThrowIfCancellationRequested();
                if (!source.Pixels.Span.Slice((y + row) * width + x, panelWidth).SequenceEqual(decoded.Pixels.Span.Slice(row * panelWidth, panelWidth)))
                    throw new InvalidDataException();
            }
            _ = await InferPreparedPanelAsync(bytes, panelWidth, panelHeight, crop, width, height, hash, axis, ocr, candidate, totals, stats, token).ConfigureAwait(false);
        }
        if (stats.Attempted != panels.Count || Convert.ToHexStringLower(SHA256.HashData(image)) != hash) throw new InvalidDataException();
        controls.Add(new { control = mode == 0 ? "stacked" : "single",
            status = (mode == 0 ? panels.Count == 2 : panels.Count == 1) && totals.GeometryReturned == panels.Count && totals.OcrReturned == panels.Count
                ? "ocr_path_exercised_for_all_panels" : "incomplete",
            source_unchanged = true,
            crop_pixels_and_mapping_verified = true, returned_panels = panels.Count, attempted_panels = stats.Attempted,
            geometry_returned = totals.GeometryReturned, ocr_returned = totals.OcrReturned, numeric_rows = totals.NumericRows,
            measured_complete = stats.CompletedPanels, estimated_complete = stats.EstimatedPanels,
            reason_counts = totals.Reasons, total_wall_ms = timer.Elapsed.TotalMilliseconds });
        }
        var report = new { schema = "graphreader.prepared-calibration-owned-check.v1", review_only = true,
            accuracy_claim = false, source_payload_reads = 0, no_images_written = true, controls };
        EnsureAggregateOnly(JsonSerializer.SerializeToElement(report, Program.Json));
        return report;
    }

    internal static async Task<object> RunPreparedCalibrationAsync(string corpusRoot, string appExe, string assetRoot,
        string rustExe, CpuBudgetJob budget, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified || !Path.IsPathFullyQualified(corpusRoot) || !Path.IsPathFullyQualified(appExe) ||
            !Path.IsPathFullyQualified(assetRoot) || !Path.IsPathFullyQualified(rustExe)) throw new InvalidDataException();
        using Candidate candidate = Candidate.Verify(appExe, assetRoot, rustExe, token);
        var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpusRoot, token);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        FrozenRealCorpusProject[] selected = selector.EnumerateSelectedProjects(token).OrderBy(p => p.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
        if (selected.Length != Denominator || selected.Any(p => p.Split != FrozenRealCorpusInventory.RealDev)) throw new InvalidDataException();
        IAxisProposalService axis = new RustFirstAxisProposalService(new RustAxisProposalService(rustExe), new OpenCvAxisProposalService(appExe));
        await using var ocr = new CalibrationOcrWorkerClient(appExe, assetRoot, () => CalibrationOcrDeviceSelection.Cpu);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var totals = new Totals(); var stats = new PreparedCalibrationStats(); var wall = Stopwatch.StartNew();
        foreach (FrozenRealCorpusProject project in selected)
        {
            if (token.IsCancellationRequested) { totals.Reason("OVERALL_DEADLINE_UNATTEMPTED", Denominator - totals.Attempted); break; }
            totals.Attempted++;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
            bool any = false, finished = false;
            try
            {
                Payload payload = ReadLocked(selector, project, digest, totals, deadline.Token);
                stats.ImageHashes.Add(payload.ImageHash);
                IReadOnlyList<object>? panels = null;
                var timer = Stopwatch.StartNew();
                try { panels = await PrepareAsync(panelizer, payload.Image, payload.ImageHash, payload.Width, payload.Height, deadline.Token).ConfigureAwait(false); }
                catch (Exception error) when (IsGenuineNoPanel(error)) { stats.Zero++; stats.Fallback++; }
                finally { stats.PreparationMs.Add(timer.Elapsed.TotalMilliseconds); }
                if (panels is null)
                {
                    int unsupportedBefore = stats.Unsupported;
                    any = await InferPreparedPanelAsync(payload.Image, payload.Width, payload.Height, new PdfRectD(0, 0, payload.Width, payload.Height),
                        payload.Width, payload.Height, payload.ProjectHash, axis, ocr, candidate, totals, stats, deadline.Token).ConfigureAwait(false);
                    finished = stats.Unsupported == unsupportedBefore;
                }
                else
                {
                    if (panels.Count == 0) throw new InvalidDataException();
                    if (panels.Count == 1) stats.One++; else stats.Many++;
                    stats.Returned += panels.Count;
                    bool allPanelsProcessed = true;
                    foreach (object panel in panels)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        try
                        {
                            var (bytes, width, height, crop) = ReadPreparedPanel(panel, payload.Width, payload.Height);
                            if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216)
                                allPanelsProcessed = false;
                            any |= await InferPreparedPanelAsync(bytes, width, height, crop, payload.Width, payload.Height,
                                payload.ProjectHash, axis, ocr, candidate, totals, stats, deadline.Token).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
                        { allPanelsProcessed = false; stats.PanelErrors++; totals.Reason("PANEL_PROCESSING_FAILED"); }
                    }
                    finished = allPanelsProcessed;
                }
            }
            catch (OperationCanceledException) { totals.Reason(token.IsCancellationRequested ? "OVERALL_DEADLINE_CASE" : "CASE_TIMEOUT"); }
            catch (Exception error) when (error is not OutOfMemoryException) { stats.ProjectErrors++; totals.Reason("PROJECT_PROCESSING_FAILED"); }
            finally
            {
                if (!finished) { stats.ProjectsIncomplete++; if (any) stats.ProjectsIncompleteAny++; }
                else if (any) stats.ProjectsAnyComplete++;
                else stats.ProjectsNoneComplete++;
                SampleMemory(totals, 0);
            }
        }
        _ = selector.EnumerateSelectedProjects(CancellationToken.None);
        var report = new { schema = "graphreader.real-dev-prepared-calibration-diagnostic.v1",
            status = totals.Attempted == Denominator && stats.ProjectsIncomplete == 0 ? "completed" : "incomplete",
            protocol = new { version = "0.4.49", scope = "fixed_real_dev_prepared_panels", denominator = Denominator,
                selection = "every_panel_no_truth_selection", manual_truth_used_for_inference = false, scored_accuracy = false,
                sealed_payload_reads = 0, train_optimizer_steps = 0, image_writes = false, per_case_output = false,
                review_only = true, applied = false, production_approved = false,
                panel_to_source_mapping = "add_encoded_crop_offset_once", case_deadline_seconds = 45, overall_deadline_seconds = 900 },
            candidate = new { app_exe_sha256 = AppExeHash, app_dll_sha256 = AppDllHash, rust_sha256 = RustHash, runtime_files = candidate.Files },
            metadata.AssignmentSha256, metadata.SelectedInventorySha256,
            source_identity_digest = Convert.ToHexStringLower(digest.GetHashAndReset()),
            source_identity_digest_complete = totals.PayloadsHashed == Denominator && totals.ImagesBound == Denominator,
            projects = new { denominator = Denominator, attempted = totals.Attempted, payloads_hashed = totals.PayloadsHashed,
                distinct_embedded_images = stats.ImageHashes.Count, zero_panels = stats.Zero, one_panel = stats.One, multiple_panels = stats.Many,
                preparation_unclassified = totals.Attempted - stats.Zero - stats.One - stats.Many, processing_errors = stats.ProjectErrors,
                any_complete_fully_processed = stats.ProjectsAnyComplete, none_complete_fully_processed = stats.ProjectsNoneComplete,
                incomplete = stats.ProjectsIncomplete, incomplete_with_any_complete = stats.ProjectsIncompleteAny },
            panels = new { returned = stats.Returned, full_image_fallback = stats.Fallback, attempted = stats.Attempted,
                unattempted = stats.Returned + stats.Fallback - stats.Attempted, unsupported = stats.Unsupported, errors = stats.PanelErrors,
                geometry_returned = totals.GeometryReturned, ocr_returned = totals.OcrReturned, numeric_rows = totals.NumericRows,
                measured_complete = stats.CompletedPanels, estimated_complete = stats.EstimatedPanels },
            reason_counts = totals.Reasons.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value),
            preparation_ms = Stats(stats.PreparationMs), total_wall_ms = wall.Elapsed.TotalMilliseconds,
            memory = new { totals.PeakSampledParentBytes, totals.PeakParentWorkingSetBytes, totals.PeakSampledCombinedBytes, sampling_is_not_a_hard_memory_ceiling = true },
            parent_cpu_budget = new { budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores, budget.SilentBreakawayVerified,
                scope = "parent_only_children_independently_capped" } };
        EnsureAggregateOnly(JsonSerializer.SerializeToElement(report, Program.Json));
        return report;
    }
}
