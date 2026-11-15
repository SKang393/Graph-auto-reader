// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Pdf;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const int TraceSampleCount = 12;

    private readonly record struct TracePanelOutcome(string Status, object Payload, bool PrintedXReviewDraftAvailable = false);

    private static readonly string[] TracePanelStatuses =
        ["NO_GEOMETRY", "OCR_FAILED", "UNSUPPORTED_DIMENSIONS", "PANEL_FAILED", "OCR_RETURNED"];
    private static readonly string[] NativeRejectionPanelStatuses =
        ["NO_GEOMETRY", "GEOMETRY_RETURNED", "UNSUPPORTED_DIMENSIONS", "PANEL_FAILED"];

    private static Dictionary<string, int> NewTracePanelStatusCounts(bool geometryOnly = false) =>
        (geometryOnly ? NativeRejectionPanelStatuses : TracePanelStatuses)
            .ToDictionary(static status => status, static _ => 0, StringComparer.Ordinal);

    private static void CountTracePanelStatus(Dictionary<string, int> counts, TracePanelOutcome outcome)
    {
        if (!counts.TryGetValue(outcome.Status, out int count)) throw new InvalidDataException();
        counts[outcome.Status] = count + 1;
    }

    private sealed class TraceAxis(IAxisProposalService inner) : IAxisProposalService
    {
        internal RustAxisProposalResult? Last { get; private set; }
        internal int Calls { get; private set; }
        public async Task<RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token)
        {
            Calls++;
            return Last = await inner.SuggestAsync(image, token).ConfigureAwait(false);
        }
    }

    private sealed class TraceRustAxis(string worker) : IAxisProposalService
    {
        internal int Width { get; set; }
        internal int Height { get; set; }
        internal AxisDecisionDiagnosticResult? Last { get; private set; }
        public async Task<RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token)
        {
            var (gray, _, _) = Program.Decode(image, [0, 0, Width, Height], token);
            Last = await AxisDecisionDiagnosticClient.RunAsync(worker, Width, Height, gray.Pixels, token).ConfigureAwait(false);
            return Last.Proposal;
        }
    }

    internal static Task<object> Run452TraceAsync(string corpusRoot, string app, string assets, string rust,
        CpuBudgetJob budget, CancellationToken token) =>
        RunTraceAsync(corpusRoot, app, assets, rust, budget, sourcePostGrouping: false, geometryOnly: false, token);

    internal static Task<object> RunPrintedXTraceAsync(string corpusRoot, string app, string assets, string rust,
        CpuBudgetJob budget, CancellationToken token) =>
        RunTraceAsync(corpusRoot, app, assets, rust, budget, sourcePostGrouping: true, geometryOnly: false, token, printedXTrace: true);

    internal static Task<object> RunSpatialTraceAsync(string corpusRoot, string app, string assets, string rust,
        CpuBudgetJob budget, CancellationToken token) =>
        RunTraceAsync(corpusRoot, app, assets, rust, budget, sourcePostGrouping: true, geometryOnly: false, token, spatialTrace: true);

    internal static Task<object> RunPostGroupingTraceAsync(string corpusRoot, string app, string assets, string rust,
        CpuBudgetJob budget, CancellationToken token) =>
        RunTraceAsync(corpusRoot, app, assets, rust, budget, sourcePostGrouping: true, geometryOnly: false, token);

    internal static Task<object> RunNativeRejectionTraceAsync(string corpusRoot, string app, string assets, string rust,
        CpuBudgetJob budget, CancellationToken token) =>
        RunTraceAsync(corpusRoot, app, assets, rust, budget, sourcePostGrouping: true, geometryOnly: true, token);

    internal static Task<object> RunPairPruningTraceAsync(string corpusRoot, string app, string assets, string rust,
        CpuBudgetJob budget, CancellationToken token) =>
        RunTraceAsync(corpusRoot, app, assets, rust, budget, sourcePostGrouping: true, geometryOnly: true, token, pairPruning: true);

    internal static Task<object> RunIntersectionTraceAsync(string corpusRoot, string app, string assets, string rust,
        CpuBudgetJob budget, CancellationToken token) =>
        RunTraceAsync(corpusRoot, app, assets, rust, budget, sourcePostGrouping: true, geometryOnly: true, token, intersection: true);

    internal static Task<object> RunDirectionalTraceAsync(string corpusRoot, string app, string assets, string rust,
        CpuBudgetJob budget, CancellationToken token) =>
        RunTraceAsync(corpusRoot, app, assets, rust, budget, sourcePostGrouping: true, geometryOnly: false, token, directional: true);

    private static async Task<object> RunTraceAsync(string corpusRoot, string app, string assets, string rust,
        CpuBudgetJob budget, bool sourcePostGrouping, bool geometryOnly, CancellationToken token, bool pairPruning = false, bool intersection = false, bool directional = false, bool printedXTrace = false, bool spatialTrace = false)
    {
        if ((pairPruning || intersection) && !geometryOnly || pairPruning && intersection || directional && (geometryOnly || !sourcePostGrouping) ||
            printedXTrace && (!sourcePostGrouping || geometryOnly || pairPruning || intersection || directional || spatialTrace) ||
            spatialTrace && (!sourcePostGrouping || geometryOnly || pairPruning || intersection || directional)) throw new InvalidDataException();
        if (!budget.SilentBreakawayVerified || !Path.IsPathFullyQualified(corpusRoot) || !Path.IsPathFullyQualified(app) ||
            !Path.IsPathFullyQualified(assets) || !Path.IsPathFullyQualified(rust)) throw new InvalidDataException();
        using Candidate candidateLease = spatialTrace ? Candidate.VerifySpatial(app, assets, rust, token) :
            printedXTrace ? Candidate.VerifyPrintedX(app, assets, rust, token) : directional ? Candidate.VerifyDirectional(app, assets, rust, token) :
            intersection ? Candidate.VerifyIntersection(app, assets, rust, token) :
            pairPruning ? Candidate.VerifyPairPruning(app, assets, rust, token) :
            geometryOnly ? Candidate.VerifyNativeRejection(app, assets, rust, token) : sourcePostGrouping
            ? Candidate.VerifyPostGrouping(app, assets, rust, token)
            : Candidate.Verify452(app, assets, rust, token);
        IReadOnlyDictionary<string, string> candidateFiles = candidateLease.Files;
        _ = AxisDecisionDiagnosticClient.SelfTest();
        var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpusRoot, token);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        FrozenRealCorpusProject[] all = selector.EnumerateSelectedProjects(token).OrderBy(p => p.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
        if (all.Length != Denominator || all.Any(p => p.Split != FrozenRealCorpusInventory.RealDev)) throw new InvalidDataException();
        var projects = new List<object>(); var totals = new Totals();
        var primary = new TraceRustAxis(rust);
        var secondary = new TraceAxis(new OpenCvAxisProposalService(app));
        IAxisProposalService axis = new RustFirstAxisProposalService(primary, secondary);
        await using CalibrationOcrWorkerClient? ocr = geometryOnly ? null : new(app, assets, () => CalibrationOcrDeviceSelection.Cpu);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int attempted = 0, returned = 0, fallback = 0, panelAttempted = 0, panelFinished = 0, printedXReviewDraftPanels = 0;
        Dictionary<string, int> terminalStatusCounts = NewTracePanelStatusCounts(geometryOnly);
        for (int i = 0; i < TraceSampleCount && !token.IsCancellationRequested; i++)
        {
            attempted++; var panelResults = new List<object>(); string status = "completed"; string? errorCategory = null; int effective = 0;
            double? preparationMs = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                Payload payload = ReadLocked(selector, all[i], digest, totals, deadline.Token);
                IReadOnlyList<object>? panels = null;
                var preparation = Stopwatch.StartNew();
                try { panels = await PrepareAsync(panelizer, payload.Image, payload.ImageHash, payload.Width, payload.Height, deadline.Token).ConfigureAwait(false); }
                catch (Exception error) when (IsGenuineNoPanel(error)) { fallback++; }
                finally { preparation.Stop(); preparationMs = preparation.Elapsed.TotalMilliseconds; }
                if (panels is null)
                {
                    effective = 1; panelAttempted++;
                    try
                    {
                        TracePanelOutcome outcome = await TracePanelAsync(payload.Image, payload.Width, payload.Height,
                            new PdfRectD(0, 0, payload.Width, payload.Height), payload.Width, payload.Height, payload.ProjectHash,
                            candidateFiles, axis, primary, secondary, ocr, sourcePostGrouping, geometryOnly, deadline.Token,
                            includePrintedXReviewDraft: printedXTrace || spatialTrace).ConfigureAwait(false);
                        CountTracePanelStatus(terminalStatusCounts, outcome);
                        if (outcome.PrintedXReviewDraftAvailable) printedXReviewDraftPanels++;
                        panelResults.Add(outcome.Payload);
                    }
                    catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                    {
                        var outcome = PrintedXOutcome("PANEL_FAILED", new { status = "PANEL_FAILED", error_category = TraceExceptionCategory(error) }, printedXTrace || spatialTrace);
                        CountTracePanelStatus(terminalStatusCounts, outcome);
                        panelResults.Add(outcome.Payload);
                        status = "incomplete";
                    }
                    panelFinished++;
                }
                else
                {
                    if (panels.Count == 0) throw new InvalidDataException();
                    returned += panels.Count; effective = panels.Count;
                    foreach (object panel in panels)
                    {
                        deadline.Token.ThrowIfCancellationRequested(); panelAttempted++;
                        try
                        {
                            var (bytes, width, height, crop) = ReadPreparedPanel(panel, payload.Width, payload.Height);
                            TracePanelOutcome outcome = await TracePanelAsync(bytes, width, height, crop, payload.Width, payload.Height,
                                payload.ProjectHash, candidateFiles, axis, primary, secondary, ocr, sourcePostGrouping, geometryOnly, deadline.Token,
                                includePrintedXReviewDraft: printedXTrace || spatialTrace).ConfigureAwait(false);
                            CountTracePanelStatus(terminalStatusCounts, outcome);
                            if (outcome.PrintedXReviewDraftAvailable) printedXReviewDraftPanels++;
                            panelResults.Add(outcome.Payload);
                            panelFinished++;
                        }
                        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                        {
                            var outcome = PrintedXOutcome("PANEL_FAILED", new { status = "PANEL_FAILED", error_category = TraceExceptionCategory(error) }, printedXTrace || spatialTrace);
                            CountTracePanelStatus(terminalStatusCounts, outcome);
                            panelResults.Add(outcome.Payload);
                            status = "incomplete";
                            panelFinished++;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { status = token.IsCancellationRequested ? "overall_deadline" : "project_timeout"; }
            catch (Exception error) when (error is not OutOfMemoryException) { status = "project_failed"; errorCategory = TraceExceptionCategory(error); }
            projects.Add(new { index = i, status, error_category = errorCategory, preparation_milliseconds = preparationMs, effective_panels = effective, panels = panelResults });
        }
        if (geometryOnly)
            return new { schema = intersection ? IntersectionTraceSchema : pairPruning ? PairPruningTraceSchema : NativeRejectionTraceSchema, scope = "development_only_sensitive_metadata",
                selection = "first_12_sorted_anonymized_case_id", frozen_dev_denominator = Denominator, sample_denominator = TraceSampleCount,
                attempted, unattempted = TraceSampleCount - attempted, returned_panels = returned, fallback_panels = fallback,
                attempted_panels = panelAttempted, finished_panels = panelFinished, unfinished_execution_panels = panelAttempted - panelFinished,
                terminal_status_counts = terminalStatusCounts, unattempted_panels = returned + fallback - panelAttempted,
                metadata.AssignmentSha256, metadata.SelectedInventorySha256, source_digest = Convert.ToHexStringLower(digest.GetHashAndReset()),
                candidate = new { app_exe_sha256 = intersection ? IntersectionAppExeHash : NativeRejectionAppExeHash, app_dll_sha256 = intersection ? IntersectionAppDllHash : NativeRejectionAppDllHash,
                    rust_sha256 = NativeRejectionRustHash,
                    runtime_files = intersection ? IntersectionRuntimeLibraries : pairPruning ? PairPruningRuntimeLibraries : NativeRejectionRuntimeLibraries },
                sealed_payload_reads = 0, ocr_calls = 0, truth_scoring = false, image_writes = false, review_only = true,
                project_deadline_seconds = 45, overall_deadline_seconds = 900, projects };
        object result = new { schema = spatialTrace ? SpatialTraceSchema : printedXTrace ? PrintedXTraceSchema : directional ? DirectionalTraceSchema : sourcePostGrouping ? PostGroupingTraceSchema : "graphreader.real-dev-452-association-trace.v1", scope = "development_only_sensitive_metadata",
            selection = "first_12_sorted_anonymized_case_id", frozen_dev_denominator = Denominator, sample_denominator = TraceSampleCount,
            attempted, unattempted = TraceSampleCount - attempted, returned_panels = returned, fallback_panels = fallback,
            attempted_panels = panelAttempted, finished_panels = panelFinished, unfinished_execution_panels = panelAttempted - panelFinished,
            terminal_status_counts = terminalStatusCounts,
            unattempted_panels = returned + fallback - panelAttempted, metadata.AssignmentSha256, metadata.SelectedInventorySha256,
            source_digest = Convert.ToHexStringLower(digest.GetHashAndReset()),
            candidate = spatialTrace ? SpatialTraceIdentity() : printedXTrace ? PrintedXTraceIdentity() : directional ? DirectionalTraceIdentity() : sourcePostGrouping ? PostGroupingTraceIdentity() : (object)App452ExeHash,
            sealed_payload_reads = 0, truth_scoring = false, image_writes = false, review_only = true,
            project_deadline_seconds = 45, overall_deadline_seconds = 900, projects };
        return printedXTrace || spatialTrace ? AddPrintedXAggregate(result, printedXReviewDraftPanels) : result;
    }

    private static async Task<TracePanelOutcome> TracePanelAsync(byte[] image, int width, int height, PdfRectD crop, int sourceWidth, int sourceHeight,
        string projectHash, IReadOnlyDictionary<string, string> candidateFiles, IAxisProposalService axis, TraceRustAxis primary, TraceAxis secondary,
        CalibrationOcrWorkerClient? ocr, bool sourcePostGrouping, bool geometryOnly, CancellationToken token, bool includePrintedXReviewDraft = false)
    {
        if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216)
            return PrintedXOutcome("UNSUPPORTED_DIMENSIONS", new { status = "UNSUPPORTED_DIMENSIONS", width, height, crop }, includePrintedXReviewDraft);
        // New observers per request are unnecessary: the fallback result is read only if invoked for this result.
        primary.Width = width; primary.Height = height;
        int nativeCallsBefore = secondary.Calls;
        var timer = Stopwatch.StartNew();
        RustAxisProposalResult result = await axis.SuggestAsync(image, token).ConfigureAwait(false);
        double axisMs = timer.Elapsed.TotalMilliseconds;
        bool fallbackCalled = sourcePostGrouping ? secondary.Calls > nativeCallsBefore :
            primary.Last?.Proposal.Geometry is null && primary.Last?.Proposal.ErrorCode is ("NO_AXIS" or "NO_INTERSECTION");
        var stages = new { rust_error = primary.Last?.Proposal.ErrorCode, rust_returned = primary.Last?.Proposal.Geometry is not null,
            rust_trace = primary.Last?.Trace,
            native_attempted = fallbackCalled, native_error = fallbackCalled ? secondary.Last?.ErrorCode : null,
            native_review_reasons = fallbackCalled ? secondary.Last?.ReviewReasons : null,
            native_returned = fallbackCalled && secondary.Last?.Geometry is not null, result.Provider, result.ReviewReasons };
        if (result.Geometry is not { } geometry)
            return PrintedXOutcome("NO_GEOMETRY", new { status = "NO_GEOMETRY", width, height, crop, stages, axis_milliseconds = axisMs }, includePrintedXReviewDraft);
        if (geometryOnly)
            return new("GEOMETRY_RETURNED", new { status = "GEOMETRY_RETURNED", width, height, crop, stages,
                axis_milliseconds = axisMs, geometry });
        string panelHash = Convert.ToHexStringLower(SHA256.HashData(image));
        CalibrationOcrRequest request = TraceRequest(projectHash, panelHash, width, height, geometry, sourcePostGrouping);
        timer.Restart();
        CalibrationOcrResponse response = await (ocr ?? throw new InvalidDataException()).SuggestAsync(request, image, token).ConfigureAwait(false);
        double ocrMs = timer.Elapsed.TotalMilliseconds;
        if (response.FailureCode is not null)
            return PrintedXOutcome("OCR_FAILED", new { status = "OCR_FAILED", width, height, crop, stages, axis_milliseconds = axisMs, ocr_milliseconds = ocrMs, ocr_error = response.FailureCode }, includePrintedXReviewDraft);
        CalibrationOcrRuntime runtime = response.Runtime ?? throw new InvalidDataException();
        if (runtime.Provider != "Cpu" || runtime.RequestedProvider != "Cpu" || runtime.ModelSha256 != Program.ModelHash ||
            runtime.ManifestSha256 != Program.ManifestHash || runtime.Files.Any(f => !candidateFiles.TryGetValue(f.Name, out string? expected) || f.Sha256 != expected))
            throw new InvalidDataException();
        timer.Restart();
        CalibrationTickProposalResult proposal = CalibrationTickProposal.CreateReviewDraft(geometry, response);
        ValidatePanelMapping(response.Proposals, proposal, geometry, crop, width, height, sourceWidth, sourceHeight);
        var rows = response.Proposals.Select(row => TraceRow(row, geometry, proposal)).ToArray();
        double associationMs = timer.Elapsed.TotalMilliseconds;
        object panelPayload = new { status = "OCR_RETURNED", width, height, crop, source_width = sourceWidth, source_height = sourceHeight,
            mapping = "source_position_equals_panel_position_plus_crop_offset", stages,
            axis_milliseconds = axisMs, ocr_milliseconds = ocrMs, association_milliseconds = associationMs,
            association_trace_scope = "geometric_distances_only_actual_decisions_in_paired_flags_and_reasons",
            geometry = new { geometry.Left, geometry.Top, geometry.Right, geometry.Bottom, x_ticks = geometry.XTickPositions, y_ticks = geometry.YTickPositions },
            rows, proposal.Reasons, proposal.IsComplete, proposal.UsesEstimatedPositions,
            x_review_draft_available = proposal.XReviewDraft is not null, y_review_draft_available = proposal.YReviewDraft is not null,
            paired_count = proposal.PairedTicks.Count };
        return PrintedXOutcome("OCR_RETURNED", panelPayload, includePrintedXReviewDraft, proposal.PrintedXReviewDraft is not null);
    }

    private const string PrintedXTraceSchema = "graphreader.real-dev-printed-x-review-trace.v1";
    private const string SpatialTraceSchema = "graphreader.real-dev-spatial-review-trace.v1";

    private static TracePanelOutcome PrintedXOutcome(string status, object payload, bool includePrintedX, bool available = false)
    {
        if (!includePrintedX) return new(status, payload);
        JsonElement serialized = System.Text.Json.JsonSerializer.SerializeToElement(payload, Program.Json);
        var fields = serialized.EnumerateObject().ToDictionary(property => property.Name,
            property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        fields["printed_x_review_draft_available"] = available;
        return new(status, fields, available);
    }

    private static Dictionary<string, object?> AddPrintedXAggregate(object result, int availablePanels)
    {
        JsonElement serialized = System.Text.Json.JsonSerializer.SerializeToElement(result, Program.Json);
        var fields = serialized.EnumerateObject().ToDictionary(property => property.Name,
            property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        fields["printed_x_review_draft_available_panels"] = availablePanels;
        return fields;
    }

    private static string TraceExceptionCategory(Exception error) => error switch
    {
        InvalidDataException => "INVALID_DATA",
        IOException => "IO_ERROR",
        TimeoutException => "TIMEOUT",
        UnauthorizedAccessException => "ACCESS_DENIED",
        ArgumentException => "INVALID_ARGUMENT",
        _ => "OTHER_FAILURE",
    };

    private static object TraceRow(CalibrationOcrProposal row, RustAxisGeometry geometry, CalibrationTickProposalResult proposal)
    {
        // The existing OCR wire permits responses up to one million bytes. Do not truncate text.
        if (System.Text.Encoding.UTF8.GetByteCount(row.Text) > 1_000_000) throw new InvalidDataException();
        double left = row.Polygon.Min(p => p.X), right = row.Polygon.Max(p => p.X);
        double top = row.Polygon.Min(p => p.Y), bottom = row.Polygon.Max(p => p.Y);
        bool x = row.Role == "XTick", y = row.Role == "YTick";
        IReadOnlyList<int> ticks = x ? geometry.XTickPositions : y ? geometry.YTickPositions : Array.Empty<int>();
        double center = x ? (left + right) / 2 : (top + bottom) / 2;
        double? nearest = ticks.Count > 0 ? ticks.Min(t => Math.Abs(t - center)) : null;
        int ties = nearest is null ? 0 : ticks.Count(t => Math.Abs(Math.Abs(t - center) - nearest.Value) <= 1e-9);
        double? tolerance = null;
        if (nearest is not null && ties == 1)
        {
            int index = Enumerable.Range(0, ticks.Count).Single(i => Math.Abs(Math.Abs(ticks[i] - center) - nearest.Value) <= 1e-9);
            double neighbor = Math.Min(index > 0 ? ticks[index] - ticks[index - 1] : double.PositiveInfinity,
                index + 1 < ticks.Count ? ticks[index + 1] - ticks[index] : double.PositiveInfinity);
            double labelTolerance = x ? Math.Max((right - left) / 2, bottom - top) : bottom - top;
            tolerance = Math.Min(labelTolerance, neighbor / 2);
        }
        return new { row.Text, row.NumericValue, row.Confidence, row.Role, row.SuggestedAxis, row.NumericLiteral, row.FailureCode, bounds = new { left, top, right, bottom },
            nearest_tick_distance = nearest, nearest_tick_ties = ties, association_tolerance = tolerance,
            associated = proposal.PairedTicks.Any(p => p.Label.RegionId == row.RegionId),
            estimated = proposal.PairedTicks.Any(p => p.Label.RegionId == row.RegionId && p.IsEstimated) };
    }

    internal static object Trace452SelfTest()
    {
        var geometry = new RustAxisGeometry(10, 90, 110, 10, .9) { XTickPositions = [20, 60, 100] };
        var row = new CalibrationOcrProposal("owned", [new(34, 94), new(46, 94), new(46, 100), new(34, 100)],
            "1", .9, 1, "1", true, "XTick", "X", null);
        var request = new CalibrationOcrRequest("owned", "owned", new string('a', 64), new string('a', 64), 120, 120, new(10, 10, 110, 90));
        var response = new CalibrationOcrResponse(request, [row], null, null, null, null, null);
        CalibrationTickProposalResult proposal = CalibrationTickProposal.CreateReviewDraft(geometry, response);
        var trace = System.Text.Json.JsonSerializer.SerializeToElement(TraceRow(row, geometry, proposal), Program.Json);
        if (trace.GetProperty("nearest_tick_ties").GetInt32() != 2 || trace.GetProperty("associated").GetBoolean() ||
            trace.GetProperty("text").GetString() != "1" || trace.GetProperty("numeric_value").GetDouble() != 1 ||
            trace.GetProperty("confidence").GetDouble() != .9) throw new InvalidDataException();
        CalibrationOcrPoint mapped = MirageComparisonMapping.ToSource(new CalibrationOcrPoint(40, 94), 15, 20, 120, 120, 200, 200);
        if (mapped.X != 55 || mapped.Y != 114) throw new InvalidDataException();
        Dictionary<string, int> statusCounts = NewTracePanelStatusCounts();
        foreach (string status in new[] { "NO_GEOMETRY", "OCR_FAILED", "UNSUPPORTED_DIMENSIONS", "PANEL_FAILED", "OCR_RETURNED" })
            CountTracePanelStatus(statusCounts, new TracePanelOutcome(status, new { status }));
        const int selfTestAttemptedPanels = 7;
        const int selfTestFinishedPanels = 5;
        int selfTestUnfinishedPanels = selfTestAttemptedPanels - selfTestFinishedPanels;
        if (statusCounts.Values.Any(static count => count != 1) || statusCounts.Values.Sum() != selfTestFinishedPanels ||
            selfTestUnfinishedPanels != 2) throw new InvalidDataException();
        return new { schema = "graphreader.real-dev-452-trace-self-test.v1", owned_tie_rejected = true,
            crop_offset_once = true, terminal_status_accounting = true, private_reads = 0 };
    }

    internal static object PrintedXTraceSelfTest()
    {
        var geometry = new RustAxisGeometry(10, 90, 110, 10, .9)
        {
            XTickPositions = [20, 60, 100],
            YTickPositions = [20, 50, 80],
        };
        CalibrationOcrProposal[] rows =
        [
            Tick("x0", "0", 20, 95, "XTick", "X"),
            Tick("x5", "5", 60, 95, "XTick", "X"),
            Tick("x10", "10", 100, 95, "XTick", "X"),
            Tick("y10", "10", 5, 20, "YTick", "Y"),
            Tick("y5", "5", 5, 50, "YTick", "Y"),
            Tick("y0", "0", 5, 80, "YTick", "Y"),
        ];
        var request = new CalibrationOcrRequest("owned", "owned", new string('a', 64), new string('b', 64),
            120, 120, new(10, 10, 110, 90));
        CalibrationTickProposalResult proposal = CalibrationTickProposal.CreateReviewDraft(geometry,
            new CalibrationOcrResponse(request, rows, null, null, null, null, null));
        TracePanelOutcome outcome = PrintedXOutcome("OCR_RETURNED", new { status = "OCR_RETURNED" }, true,
            proposal.PrintedXReviewDraft is not null);
        JsonElement panel = JsonSerializer.SerializeToElement(outcome.Payload, Program.Json);
        if (!panel.GetProperty("printed_x_review_draft_available").GetBoolean() || proposal.IsComplete ||
            proposal.PrintedXReviewDraft is null || proposal.XReviewDraft is not null ||
            proposal.PrintedXReviewDraft.FirstX != 0 || proposal.PrintedXReviewDraft.FinalX != 10 ||
            proposal.PrintedXReviewDraft.YMaximum != 10)
            throw new InvalidDataException();
        Dictionary<string, int> statuses = NewTracePanelStatusCounts();
        foreach (string status in TracePanelStatuses)
        {
            CountTracePanelStatus(statuses, new TracePanelOutcome(status, new { status }));
            JsonElement terminalPanel = JsonSerializer.SerializeToElement(
                PrintedXOutcome(status, new { status }, includePrintedX: true).Payload, Program.Json);
            if (terminalPanel.GetProperty("printed_x_review_draft_available").GetBoolean()) throw new InvalidDataException();
        }
        if (statuses.Values.Sum() != TracePanelStatuses.Length || statuses.Values.Any(static count => count != 1))
            throw new InvalidDataException();
        JsonElement aggregate = JsonSerializer.SerializeToElement(AddPrintedXAggregate(new { review_only = true }, 1), Program.Json);
        if (aggregate.GetProperty("printed_x_review_draft_available_panels").GetInt32() != 1) throw new InvalidDataException();
        return new { schema = "graphreader.real-dev-printed-x-review-trace-self-test.v1",
            printed_x_review_draft_available = true, separate_from_complete_calibration = true,
            terminal_status_accounting = true, private_reads = 0 };

        static CalibrationOcrProposal Tick(string id, string text, double centerX, double centerY, string role, string axis) =>
            new(id, [new(centerX - 3, centerY - 2), new(centerX + 3, centerY - 2),
                new(centerX + 3, centerY + 2), new(centerX - 3, centerY + 2)],
                text, .9, double.Parse(text, CultureInfo.InvariantCulture), text, true, role, axis, null);
    }

    internal static object SpatialTraceSelfTest()
    {
        JsonElement printedX = JsonSerializer.SerializeToElement(PrintedXTraceSelfTest(), Program.Json);
        if (printedX.GetProperty("printed_x_review_draft_available").GetBoolean() is false ||
            !printedX.GetProperty("separate_from_complete_calibration").GetBoolean() ||
            !printedX.GetProperty("terminal_status_accounting").GetBoolean() ||
            printedX.GetProperty("private_reads").GetInt32() != 0)
            throw new InvalidDataException();
        return new { schema = "graphreader.real-dev-spatial-trace-self-test.v1",
            printed_x_review_draft_availability = true,
            distinct_from_complete_or_applied_calibration = true,
            terminal_failure_accounting_reused = true,
            private_reads = 0 };
    }

    internal static object NativeRejectionTraceSelfTest()
    {
        Dictionary<string, int> counts = NewTracePanelStatusCounts(geometryOnly: true);
        foreach (string status in NativeRejectionPanelStatuses)
            CountTracePanelStatus(counts, new TracePanelOutcome(status, new { status }));
        if (counts.Values.Any(static count => count != 1) || counts.Values.Sum() != 4) throw new InvalidDataException();
        bool ocrStatusRejected = false;
        try { CountTracePanelStatus(counts, new TracePanelOutcome("OCR_RETURNED", new { })); }
        catch (InvalidDataException) { ocrStatusRejected = true; }
        bool selectorReached = false, missingPinsRejected = false;
        try
        {
            RequireNativeRejectionPins("", "", "", new Dictionary<string, string>(StringComparer.Ordinal));
            selectorReached = true;
        }
        catch (InvalidDataException) { missingPinsRejected = true; }
        if (!ocrStatusRejected || !missingPinsRejected || selectorReached) throw new InvalidDataException();
        return new { schema = "graphreader.real-dev-native-rejection-geometry-self-test.v1",
            geometry_terminal_statuses = counts.Count, ocr_status_rejected = true,
            missing_pins_rejected_before_selector = true, private_reads = 0 };
    }
}
