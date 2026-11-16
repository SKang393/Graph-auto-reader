// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Pdf;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal const string NextProjectSchema = "graphreader.real-dev-next-project-calibration.v1";
    private const int NextProjectOrdinal = 12;
    private static readonly string[] NextProjectStages = ["runtime", "selection", "source_read", "preparation", "panels", "final_identity"];
    private static readonly string[] NextPanelStages = ["geometry", "rectification", "tick_measurement", "ocr", "association", "preservation"];
    internal sealed record NextProjectOutcome(object Report, bool Completed);
    private sealed record NextStageCount(int Attempted, int Completed, int Failed, int Unattempted);

    private static Dictionary<string, NextStageCount> CountNextPanelStages(int planned, IReadOnlyList<JsonElement> panels) =>
        NextPanelStages.ToDictionary(name => name, name =>
        {
            int completed = panels.Count(panel => panel.GetProperty("stages").GetProperty(name).GetString() == "completed");
            int failed = panels.Count(panel => panel.GetProperty("stages").GetProperty(name).GetString() == "failed");
            return new NextStageCount(completed + failed, completed, failed, planned - completed - failed);
        }, StringComparer.Ordinal);

    private static string RejectNextUnsupported(Dictionary<string, string> stages, Dictionary<string, string> reasons)
    {
        stages["geometry"] = "failed";
        return reasons["geometry"] = "UNSUPPORTED_DIMENSIONS";
    }

    private static NextProjectOutcome BoundNextProjectReport(object report, bool completed)
    {
        JsonElement full = JsonSerializer.SerializeToElement(report, Program.Json);
        if (JsonSerializer.SerializeToUtf8Bytes(full, Program.Json).Length <= Program.MaximumOutputBytes) return new(full, completed);

        JsonElement[] panels = full.GetProperty("panels").EnumerateArray().ToArray();
        var bounded = full.EnumerateObject().Where(property => property.Name is not "panels" and not "active_input_locator")
            .ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
        bounded["original_status"] = full.GetProperty("status");
        bounded["status"] = "failed";
        bounded["report_failure"] = "REPORT_LIMIT_EXCEEDED";
        bounded["detail_omission"] = "Raw panel receipts, geometry and locator panel details omitted; operation accounting is unchanged";
        bounded["active_input_locator"] = full.GetProperty("active_input_locator").EnumerateObject()
            .Where(property => property.Name != "panels").ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        bounded["panels"] = panels.Select(panel => panel.EnumerateObject().Where(property => property.Name is
            "index" or "status" or "operational_failure" or "failure" or "stages" or "reasons" or "timings" or
            "geometry_calls" or "rust_geometry_calls" or "native_geometry_calls" or "tick_service_calls" or "ocr_service_calls" or "rust_decision")
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal)).ToArray();
        if (JsonSerializer.SerializeToUtf8Bytes(bounded, Program.Json).Length <= Program.MaximumOutputBytes) return new(bounded, false);

        // Even an unusually large panel list must not erase the completed run's
        // denominators. Every reason and timing contributes to these summaries.
        bounded.Remove("panels");
        bounded.Remove("active_input_locator");
        bounded["rust_decisions"] = panels.Where(panel => panel.TryGetProperty("rust_decision", out JsonElement decision) && decision.ValueKind == JsonValueKind.Object)
            .Select(panel => panel.GetProperty("rust_decision")).ToArray();
        bounded["detail_omission"] = "Raw receipts, locator and individual panel details omitted; all aggregate operation counts, reasons and timing totals retained";
        bounded["omitted_panel_records"] = panels.Length;
        bounded["panel_reason_counts"] = panels.SelectMany(panel => panel.TryGetProperty("reasons", out JsonElement reasons)
                ? reasons.EnumerateObject().Select(property => property.Name + ":" + property.Value.GetString()) : Enumerable.Empty<string>())
            .GroupBy(reason => reason, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        bounded["panel_failure_counts"] = panels.Where(panel => panel.TryGetProperty("failure", out JsonElement failure) && failure.ValueKind == JsonValueKind.String)
            .GroupBy(panel => panel.GetProperty("failure").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        bounded["panel_stage_timing_totals_milliseconds"] = NextPanelStages.ToDictionary(name => name,
            name => panels.Sum(panel => panel.TryGetProperty("timings", out JsonElement times) && times.TryGetProperty(name, out JsonElement time)
                ? time.GetDouble() : 0), StringComparer.Ordinal);
        return new(bounded, false);
    }

    private static FrozenRealCorpusProject SelectNextProject(FrozenDevTrainingMetadata metadata, IReadOnlyList<FrozenRealCorpusProject> projects)
    {
        if (metadata.AssignmentSha256 != CachedAxisAssignmentHash || metadata.SelectedInventorySha256 != CachedAxisInventoryHash ||
            projects.Count != Denominator || projects.Any(project => project.Split != FrozenRealCorpusInventory.RealDev) ||
            projects.Select(project => project.AnonymizedCaseId).Distinct(StringComparer.Ordinal).Count() != Denominator)
            throw new InvalidDataException("NEXT_PROJECT_SELECTION_CHANGED");
        return projects.OrderBy(project => project.AnonymizedCaseId, StringComparer.Ordinal).ElementAt(NextProjectOrdinal);
    }

    internal static Task<NextProjectOutcome> RunNextProjectCalibrationAsync(string corpus, string app, string assets, string rust,
        CpuBudgetJob budget, CancellationToken token) => RunNextProjectCalibrationCoreAsync(corpus, app, assets, rust, null, budget, token);

    internal static Task<NextProjectOutcome> RunNextProjectRoutingCalibrationAsync(string corpus, string app, string assets, string rust,
        string locator, CpuBudgetJob budget, CancellationToken token) => RunNextProjectCalibrationCoreAsync(corpus, app, assets, rust, locator, budget, token);

    private static async Task<NextProjectOutcome> RunNextProjectCalibrationCoreAsync(string corpus, string app, string assets, string rust,
        string? locator, CpuBudgetJob budget, CancellationToken token)
    {
        bool routing = locator is not null;
        JsonElement? fixedLocator = null;
        var stages = NextProjectStages.ToDictionary(name => name, _ => "unattempted", StringComparer.Ordinal);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var timings = new Dictionary<string, double>(StringComparer.Ordinal);
        var panels = new List<JsonElement>();
        var total = Stopwatch.StartNew(); var step = Stopwatch.StartNew();
        string stage = "runtime"; string? failure = null, caseId = null, sourceHash = null, sourceDigest = null, projectHash = null;
        int sourceAttempted = 0, sourceCompleted = 0, planned = 0, attempted = 0, finished = 0, failed = 0;
        bool fallback = false, identitiesVerified = false;
        Candidate? candidate = null; ImageOnlyPayload? payload = null; FileStream? sourceLock = null;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Finish() { stages[stage] = "completed"; timings[stage] = step.Elapsed.TotalMilliseconds; step.Restart(); }
        try
        {
            token.ThrowIfCancellationRequested();
            if (!budget.SilentBreakawayVerified || budget.MaximumLogicalCores is <= 0 or > .5 ||
                !new[] { corpus, app, assets, rust }.All(Path.IsPathFullyQualified)) throw new InvalidDataException("NEXT_PROJECT_PREFLIGHT");
            if (routing && !Path.IsPathFullyQualified(locator!)) throw new InvalidDataException("NEXT_ROUTING_LOCATOR_PATH");
            candidate = routing ? Candidate.VerifyNextProjectRouting(app, assets, rust, token) : Candidate.VerifyNextProject(app, assets, rust, token);
            if (routing) fixedLocator = ParseNextWorkLocator(Program.ReadBounded(locator!, 4096));
            MethodInfo reader = ResolveEmbeddedImageReader();
            var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
            MethodInfo binder = typeof(ManualPreviewWorkspaceService).GetMethod("TryBindMeasuredTicks", BindingFlags.NonPublic | BindingFlags.Static,
                null, [typeof(AxisTickMeasurementResult), typeof(RustAxisGeometry), typeof(string), typeof(int), typeof(int)], null)
                ?? throw new InvalidDataException("NEXT_PROJECT_TICK_BINDER");
            Finish(); stage = "selection";
            FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpus, token);
            FrozenDevTrainingMetadata metadata = selector.Describe();
            IReadOnlyList<FrozenRealCorpusProject> projects = selector.EnumerateSelectedProjects(token);
            FrozenRealCorpusProject selected = SelectNextProject(metadata, projects);
            caseId = selected.AnonymizedCaseId;
            if (routing && caseId != fixedLocator!.Value.GetProperty("anonymized_case_id").GetString()) throw new InvalidDataException("NEXT_ROUTING_SELECTION");
            _ = selector.RequireProject(selected.GetProjectPath(), selected.AnonymizedCaseId, selected.AnonymizedStudyId, token);
            Finish(); stage = "source_read"; sourceAttempted++;
            // Keep the selected original immutable through all derived processing.
            // These body hashes concern this one source, not the other inventory entries.
            sourceLock = new FileStream(selected.GetProjectPath(), FileMode.Open, FileAccess.Read, FileShare.Read,
                65_536, FileOptions.SequentialScan);
            EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(sourceLock, selected.GetProjectPath());
            if (sourceLock.Length is <= 0 or > EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes) throw new InvalidDataException();
            projectHash = HashStream(sourceLock, token);
            payload = ReadLockedImageOnly(selector, selected, digest, reader, token);
            sourceCompleted++; sourceHash = Program.Hash(payload.Image);
            sourceDigest = Convert.ToHexStringLower(digest.GetHashAndReset());
            if (routing && (projectHash != fixedLocator!.Value.GetProperty("project_sha256").GetString() ||
                sourceHash != fixedLocator.Value.GetProperty("embedded_image_sha256").GetString() ||
                sourceDigest != fixedLocator.Value.GetProperty("single_source_identity_digest").GetString()))
                throw new InvalidDataException("NEXT_ROUTING_SOURCE_IDENTITY");
            Finish(); stage = "preparation";
            IReadOnlyList<object>? prepared;
            try { prepared = await PrepareAsync(panelizer, payload.Image, sourceHash, payload.Width, payload.Height, token).ConfigureAwait(false); }
            catch (Exception error) when (IsGenuineNoPanel(error)) { prepared = null; fallback = true; }
            if (prepared is { Count: 0 }) throw new InvalidDataException("NEXT_PROJECT_EMPTY_PREPARATION");
            if (routing && prepared is not { Count: 1 }) throw new InvalidDataException("NEXT_ROUTING_PANEL_COUNT");
            planned = prepared?.Count ?? 1;
            Finish(); stage = "panels";
            for (int index = 0; index < planned; index++)
            {
                if (token.IsCancellationRequested) { reasons["panels"] = "CANCELLED_REMAINING_UNATTEMPTED"; break; }
                attempted++;
                try
                {
                    var panel = prepared is null ? (payload.Image, payload.Width, payload.Height, new PdfRectD(0, 0, payload.Width, payload.Height))
                        : ReadPreparedPanel(prepared[index], payload.Width, payload.Height);
                    if (routing)
                    {
                        JsonElement expected = fixedLocator!.Value.GetProperty("panels")[0], crop = expected.GetProperty("crop");
                        if (Program.Hash(panel.Item1) != expected.GetProperty("image_sha256").GetString() ||
                            panel.Item2 != expected.GetProperty("width").GetInt32() || panel.Item3 != expected.GetProperty("height").GetInt32() ||
                            panel.Item4 != new PdfRectD(crop.GetProperty("x").GetDouble(), crop.GetProperty("y").GetDouble(),
                                crop.GetProperty("width").GetDouble(), crop.GetProperty("height").GetDouble()))
                            throw new InvalidDataException("NEXT_ROUTING_PANEL_IDENTITY");
                    }
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(45));
                    JsonElement result = await RunNextPanelAsync(index, panel.Item1, panel.Item2, panel.Item3, panel.Item4,
                        payload.Width, payload.Height, sourceHash, app, assets, rust, binder, candidate.Files, routing, deadline.Token).ConfigureAwait(false);
                    panels.Add(result); finished++;
                    if (result.GetProperty("operational_failure").GetBoolean()) failed++;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    failed++; finished++;
                    panels.Add(JsonSerializer.SerializeToElement(new { index, status = "PANEL_FAILED", operational_failure = true,
                        failure = error is OperationCanceledException ? "CANCELLED_OR_PANEL_DEADLINE" : TraceExceptionCategory(error),
                        stages = NextPanelStages.ToDictionary(name => name, _ => "unattempted", StringComparer.Ordinal) }, Program.Json));
                }
            }
            if (finished != planned) throw new OperationCanceledException(token);
            Finish(); stage = "final_identity";
            var final = selector.EnumerateSelectedProjects(token);
            sourceLock.Position = 0;
            EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(sourceLock, selected.GetProjectPath());
            identitiesVerified = SelectNextProject(selector.Describe(), final).AnonymizedCaseId == caseId &&
                Program.Hash(payload.Image) == sourceHash && HashStream(sourceLock, token) == projectHash;
            if (!identitiesVerified) throw new InvalidDataException("NEXT_PROJECT_FINAL_IDENTITY");
            Finish();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = error is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : TraceExceptionCategory(error);
            stages[stage] = "failed"; reasons[stage] = failure; timings[stage] = step.Elapsed.TotalMilliseconds;
        }
        finally { sourceLock?.Dispose(); candidate?.Dispose(); }
        sourceDigest ??= Convert.ToHexStringLower(digest.GetHashAndReset());
        var statusCounts = panels.GroupBy(panel => panel.GetProperty("status").GetString() ?? "UNKNOWN")
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var panelStageCounts = CountNextPanelStages(planned, panels);
        object aggregate = new
        {
            declared_projects = 1, projects_attempted = sourceAttempted, sources_completed = sourceCompleted, sources_failed = sourceAttempted - sourceCompleted,
            projects_unattempted = 1 - sourceAttempted, planned_panels = planned, attempted_panels = attempted, finished_panels = finished,
            failed_panels = failed, unattempted_panels = planned - attempted, full_image_fallbacks = fallback ? 1 : 0,
            status_counts = statusCounts, panel_stage_counts = panelStageCounts,
            geometry_calls = panels.Sum(panel => panel.TryGetProperty("geometry_calls", out JsonElement calls) ? calls.GetInt32() : 0),
            rust_geometry_calls = panels.Sum(panel => panel.TryGetProperty("rust_geometry_calls", out JsonElement calls) ? calls.GetInt32() : 0),
            native_geometry_calls = panels.Sum(panel => panel.TryGetProperty("native_geometry_calls", out JsonElement calls) ? calls.GetInt32() : 0),
            tick_service_calls = panels.Sum(panel => panel.TryGetProperty("tick_service_calls", out JsonElement calls) ? calls.GetInt32() : 0),
            ocr_service_calls = panels.Sum(panel => panel.TryGetProperty("ocr_service_calls", out JsonElement calls) ? calls.GetInt32() : 0),
            input_identity_verified = identitiesVerified, elapsed_milliseconds = total.Elapsed.TotalMilliseconds,
            sealed_payload_reads = 0, truth_reads = 0, other_project_body_reads = 0, accuracy_or_historical_gain_claimed = false,
        };
        EnsureAggregateOnly(JsonSerializer.SerializeToElement(aggregate, Program.Json));
        bool completed = failure is null && identitiesVerified && finished == planned && failed == 0;
        return BoundNextProjectReport(new
        {
            schema = routing ? "graphreader.real-dev-next-project-routing-calibration.v1" : NextProjectSchema,
            profile_kind = routing ? "changed_rust_routing" : "baseline", scope = "development_only_sensitive_ignored_output", status = completed ? "completed" : "failed",
            failure, selection = "ordinal_12_in_existing_120_dev_sorted_anonymized_case_id", zero_based_ordinal = NextProjectOrdinal,
            frozen_dev_count = Denominator, excluded_independent_count = 51, assignment_sha256 = CachedAxisAssignmentHash, inventory_sha256 = CachedAxisInventoryHash,
            case_id = caseId, source_image_sha256 = sourceHash, single_source_identity_digest = sourceDigest,
            active_input_locator = new
            {
                zero_based_ordinal = NextProjectOrdinal, anonymized_case_id = caseId,
                assignment_sha256 = CachedAxisAssignmentHash, inventory_sha256 = CachedAxisInventoryHash,
                project_sha256 = projectHash, embedded_image_sha256 = sourceHash, single_source_identity_digest = sourceDigest,
                panels = panels.Where(panel => panel.TryGetProperty("panel_image_sha256", out _)).Select(panel => new
                {
                    index = panel.GetProperty("index"), image_sha256 = panel.GetProperty("panel_image_sha256"),
                    width = panel.GetProperty("width"), height = panel.GetProperty("height"), crop = panel.GetProperty("crop"),
                }).ToArray(),
            },
            source_digest_is_new_single_case_not_old_twelve_case_comparison = true,
            metadata_inventory_access = "Directory entries and attributes across the frozen inventory; no other project bodies or images are read",
            selected_project_body_hash_passes_when_complete = 4, embedded_image_extractions = sourceCompleted,
            stages, reasons, timings, aggregate, panels, runtime_files = candidate?.Files,
            runtime_profile_sha256 = routing ? NextRoutingRuntimePinsHash : NextProjectRuntimePinsHash,
            budget.MaximumLogicalCores, model_status = "Existing reviewed CPU asset, not production-approved",
            private_source_images_requested = sourceAttempted, normal_geometry_calls_per_panel_maximum = 1,
            no_next_case_after_observed_failure = true, manual_geometry_supplied = false, candidate_selected = false,
            apply_called = false, export_called = false, image_writes = false,
        }, completed);
    }

    private static async Task<JsonElement> RunNextPanelAsync(int index, byte[] image, int width, int height, PdfRectD crop,
        int sourceWidth, int sourceHeight, string sourceHash, string app, string assets, string rust, MethodInfo binder,
        Dictionary<string, string> files, bool routing, CancellationToken token)
    {
        var stages = NextPanelStages.ToDictionary(name => name, _ => "unattempted", StringComparer.Ordinal);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal); var timings = new Dictionary<string, double>(StringComparer.Ordinal);
        string stage = "geometry", status = "FAILED"; bool operationalFailure = false;
        string hash = Program.Hash(image); string? tickError = null, failure = null;
        RustAxisProposalResult? geometryReceipt = null; AxisTickMeasurementResult? tickReceipt = null;
        CalibrationOcrResponse? response = null; CalibrationTickProposalResult? fit = null; CalibrationWorkingFrame? frame = null;
        CalibrationTickAnchors? originalAnchors = null; object? supportIdentity = null;
        int geometryCalls = 0, tickCalls = 0, ocrCalls = 0;
        TraceRustAxis? diagnostic = routing ? new TraceRustAxis(rust) { Width = width, Height = height } : null;
        var primary = new TraceAxis(diagnostic is null ? new RustAxisProposalService(rust) : diagnostic);
        var native = new TraceAxis(new OpenCvAxisProposalService(app));
        var clock = Stopwatch.StartNew();
        void Finish() { stages[stage] = "completed"; timings[stage] = clock.Elapsed.TotalMilliseconds; clock.Restart(); }
        try
        {
            token.ThrowIfCancellationRequested();
            if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216)
            { status = RejectNextUnsupported(stages, reasons); timings[stage] = clock.Elapsed.TotalMilliseconds; clock.Restart(); }
            else
            {
                stages[stage] = "attempted"; geometryCalls++;
                geometryReceipt = await new RustFirstAxisProposalService(primary, native).SuggestAsync(image, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                RustAxisGeometry? geometry = geometryReceipt.Geometry;
                if (geometryReceipt.AffineGeometry is not null && !NativeAffineTraceAccepted(geometryReceipt, width, height))
                    throw new InvalidDataException("INVALID_AFFINE_GEOMETRY");
                if (geometryReceipt.Provider == "opencv-review" && !OpenCvAxisProposalService.Valid(geometryReceipt, width, height))
                    throw new InvalidDataException("INVALID_NATIVE_GEOMETRY");
                Finish();
                if (geometry is null && geometryReceipt.AffineGeometry is null)
                { status = "NO_GEOMETRY"; stages["geometry"] = "failed"; reasons["geometry"] = SafeReviewError(geometryReceipt.ErrorCode) ?? "NO_USABLE_FRAME"; }
                else
                {
                    byte[] workingImage = image; int workingWidth = width, workingHeight = height;
                    RustAffineTickSource? support = null;
                    string binding = hash;
                    if (geometryReceipt.AffineGeometry is { } affine)
                    {
                        stage = "rectification"; stages[stage] = "attempted";
                        var prepared = CalibrationWorkingFrame.Create(image, width, height, affine, hash, token);
                        frame = prepared.Frame; workingImage = prepared.Image; support = prepared.OriginalSupport;
                        geometry = frame.Geometry; workingWidth = frame.Width; workingHeight = frame.Height; binding = frame.Binding;
                        supportIdentity = new { gray_sha256 = Program.Hash(support.Pixels.ToArray()), support.Width, support.Height,
                            support.M11, support.M12, support.M13, support.M21, support.M22, support.M23, frame.OriginalSupportBinding };
                        Finish();
                    }
                    else reasons["rectification"] = "NOT_AFFINE";
                    if (geometry is null) throw new InvalidDataException("MISSING_WORKING_GEOMETRY");
                    stage = "tick_measurement";
                    if (geometryReceipt.Provider == "opencv-review")
                    {
                        stages[stage] = "attempted"; tickCalls++;
                        using var tickDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); tickDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                        var tickService = new RustAxisTickMeasurementService(rust);
                        tickReceipt = support is null
                            ? await tickService.MeasureAsync(workingImage, geometry, tickDeadline.Token).ConfigureAwait(false)
                            : await tickService.MeasureAffineManualPartialAsync(workingImage, geometry, support, tickDeadline.Token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        string workingHash = Program.Hash(workingImage);
                        if (support is not null && tickReceipt.Evidence is not null && tickReceipt.OriginalSupportBinding != frame!.OriginalSupportBinding)
                            throw new InvalidDataException("ORIGINAL_SUPPORT_BINDING_MISMATCH");
                        RustAxisGeometry? measured = (RustAxisGeometry?)binder.Invoke(null, [tickReceipt, geometry, workingHash, workingWidth, workingHeight]);
                        if (measured is not null) geometry = measured;
                        else tickError = SafeTickMeasurementError(tickReceipt.ErrorCode ?? "INVALID_TICK_EVIDENCE");
                        if (tickError is not null) reasons[stage] = tickError;
                        Finish();
                        if (tickError is not null) stages[stage] = "failed";
                    }
                    else reasons[stage] = "OBSERVED_RUST_TICKS_REUSED_NO_EXTRA_CALL";
                    stage = "ocr"; stages[stage] = "attempted"; ocrCalls++;
                    var request = new CalibrationOcrRequest("next-project-panel-" + index, binding, sourceHash, Program.Hash(workingImage),
                        workingWidth, workingHeight, new(geometry.Left, geometry.Top, geometry.Right, geometry.Bottom))
                    {
                        MeasuredXTicks = geometry.XTickPositions.Count == 0 ? null : geometry.XTickPositions.ToArray(),
                        MeasuredYTicks = geometry.YTickPositions.Count == 0 ? null : geometry.YTickPositions.ToArray(),
                    };
                    using var ocrDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); ocrDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                    await using var ocr = new CalibrationOcrWorkerClient(app, assets, () => CalibrationOcrDeviceSelection.Cpu);
                    response = await ocr.SuggestAsync(request, workingImage, ocrDeadline.Token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!CalibrationOcrWorkerClient.IsValidResponse(request, response)) throw new InvalidDataException("INVALID_OCR_RECEIPT");
                    Finish();
                    if (response.FailureCode is not null) { status = "OCR_FAILED"; stages["ocr"] = "failed"; reasons["ocr"] = response.FailureCode; }
                    else
                    {
                        CalibrationOcrRuntime runtime = response.Runtime ?? throw new InvalidDataException("MISSING_OCR_RUNTIME");
                        if (runtime.Provider != "Cpu" || runtime.RequestedProvider != "Cpu" || runtime.ModelSha256 != Program.ModelHash ||
                            runtime.ManifestSha256 != Program.ManifestHash || runtime.Files.Any(file => !files.TryGetValue(file.Name, out string? expected) || expected != file.Sha256))
                            throw new InvalidDataException("OCR_RUNTIME_MISMATCH");
                        if (frame is not null && response.Proposals.Any(row => row.Polygon.Any(point => !frame.ContainsOriginalEdge(frame.ToOriginal(point)))))
                            throw new InvalidDataException("ORIGINAL_POLYGON_OUT_OF_BOUNDS");
                        stage = "association"; stages[stage] = "attempted";
                        fit = CalibrationTickProposal.CreateReviewDraft(geometry, response);
                        CalibrationTickAnchors? anchors = fit.Anchors ?? fit.PrintedXReviewDraft?.Anchors;
                        originalAnchors = anchors is null ? null : frame?.ToOriginal(anchors) ?? anchors;
                        if (originalAnchors is not null && frame is not null && (!frame.ContainsOriginal(originalAnchors.FirstSessionY0) ||
                            !frame.ContainsOriginal(originalAnchors.FirstSessionYMaximum) || !frame.ContainsOriginal(originalAnchors.FinalSessionY0)))
                            throw new InvalidDataException("ORIGINAL_ANCHOR_OUT_OF_BOUNDS");
                        if (frame is null) ValidatePanelMapping(response.Proposals, fit, geometry, crop, width, height, sourceWidth, sourceHeight);
                        status = fit.IsComplete ? "COMPLETE_REVIEW_DRAFT" : fit.PrintedXReviewDraft is not null ? "PRINTED_X_REVIEW_DRAFT" : "INCOMPLETE_REVIEW_DRAFT";
                        Finish();
                    }
                }
            }
            stage = "preservation"; stages[stage] = "attempted";
            if (Program.Hash(image) != hash) throw new InvalidDataException("PANEL_BYTES_CHANGED");
            token.ThrowIfCancellationRequested(); Finish();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            operationalFailure = true; status = "PANEL_FAILED"; stages[stage] = "failed"; timings[stage] = clock.Elapsed.TotalMilliseconds;
            failure = error is OperationCanceledException ? "CANCELLED_OR_STAGE_DEADLINE" : TraceExceptionCategory(error); reasons[stage] = failure;
        }
        return JsonSerializer.SerializeToElement(new
        {
            index, status, operational_failure = operationalFailure, failure, width, height, crop,
            panel_image_sha256 = hash, stages, reasons, timings, geometry_calls = geometryCalls,
            rust_geometry_calls = primary.Calls, native_geometry_calls = native.Calls, tick_service_calls = tickCalls, ocr_service_calls = ocrCalls,
            rust_decision = diagnostic?.Last?.Trace,
            geometry_receipt = geometryReceipt, original_support_identity = supportIdentity, tick_receipt = tickReceipt, tick_error = tickError,
            raw_derived_ocr_receipt = response, review_fit = fit, original_panel_anchors = originalAnchors,
            derived_polygons_are_raw_worker_receipt_not_original_public_geometry = frame is not null,
            applied = false, exported = false,
        }, Program.Json);
    }

    internal static object NextProjectCalibrationSelfTest()
    {
        var projects = Enumerable.Range(0, Denominator).Select(index => new FrozenRealCorpusProject("C:/owned/" + index,
            index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), "owned-study", FrozenRealCorpusInventory.RealDev)).Reverse().ToArray();
        var metadata = new FrozenDevTrainingMetadata(CachedAxisAssignmentHash, CachedAxisInventoryHash, [], false, false);
        if (SelectNextProject(metadata, projects).AnonymizedCaseId != "012") throw new InvalidDataException();
        int refused = 0;
        try { SelectNextProject(metadata, projects[..^1]); } catch (InvalidDataException) { refused++; }
        try { SelectNextProject(metadata with { AssignmentSha256 = "changed" }, projects); } catch (InvalidDataException) { refused++; }
        FrozenRealCorpusProject[] wrong = projects.ToArray();
        wrong[0] = new("C:/owned/wrong", "reserved", "owned-study", FrozenRealCorpusInventory.RealSealed);
        try { SelectNextProject(metadata, wrong); } catch (InvalidDataException) { refused++; }
        var stopped = NextPanelStages.ToDictionary(name => name, _ => "unattempted", StringComparer.Ordinal);
        stopped["geometry"] = "completed"; stopped["preservation"] = "completed";
        var failed = NextPanelStages.ToDictionary(name => name, _ => "unattempted", StringComparer.Ordinal);
        failed["geometry"] = "failed";
        var counts = CountNextPanelStages(3,
        [JsonSerializer.SerializeToElement(new { stages = stopped }), JsonSerializer.SerializeToElement(new { stages = failed })]);
        if (refused != 3 || counts["geometry"] != new NextStageCount(2, 1, 1, 1) ||
            counts["ocr"] != new NextStageCount(0, 0, 0, 3) || counts["preservation"] != new NextStageCount(1, 1, 0, 2))
            throw new InvalidDataException();
        var unsupported = NextPanelStages.ToDictionary(name => name, _ => "unattempted", StringComparer.Ordinal);
        var unsupportedReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        if (RejectNextUnsupported(unsupported, unsupportedReasons) != "UNSUPPORTED_DIMENSIONS" || unsupported["geometry"] != "failed" ||
            unsupportedReasons["geometry"] != "UNSUPPORTED_DIMENSIONS" || unsupported["ocr"] != "unattempted") throw new InvalidDataException();
        var projectStages = NextProjectStages.ToDictionary(name => name, _ => "completed", StringComparer.Ordinal);
        object aggregate = new { planned_panels = 3, finished_panels = 2, panel_stage_counts = counts };
        object[] largePanels = [new { index = 0, status = "NO_GEOMETRY", stages = failed,
            reasons = new { geometry = "NO_AXIS" }, timings = new { geometry = 2.5 }, raw_derived_ocr_receipt = new string('x', Program.MaximumOutputBytes) }];
        NextProjectOutcome bounded = BoundNextProjectReport(new
        {
            status = "completed", stages = projectStages, reasons = new { }, timings = new { runtime = 1.25 }, aggregate,
            active_input_locator = new { zero_based_ordinal = NextProjectOrdinal, panels = new string('y', Program.MaximumOutputBytes) }, panels = largePanels,
        }, true);
        JsonElement boundedJson = JsonSerializer.SerializeToElement(bounded.Report, Program.Json);
        if (bounded.Completed || JsonSerializer.SerializeToUtf8Bytes(bounded.Report, Program.Json).Length > Program.MaximumOutputBytes ||
            boundedJson.GetProperty("report_failure").GetString() != "REPORT_LIMIT_EXCEEDED" ||
            !JsonElement.DeepEquals(boundedJson.GetProperty("aggregate"), JsonSerializer.SerializeToElement(aggregate, Program.Json)) ||
            !JsonElement.DeepEquals(boundedJson.GetProperty("stages"), JsonSerializer.SerializeToElement(projectStages, Program.Json)) ||
            boundedJson.GetProperty("panels")[0].GetProperty("reasons").GetProperty("geometry").GetString() != "NO_AXIS" ||
            boundedJson.GetProperty("panels")[0].GetProperty("timings").GetProperty("geometry").GetDouble() != 2.5)
            throw new InvalidDataException();
        return new { schema = "graphreader.next-project-calibration-self-test.v1", ordinal_selection = true, selection_refusals = refused,
            planned_panels = 3, returned_panels = 2, stage_counts = counts,
            unsupported_geometry_rejection_accounted = true, oversized_report_accounting_retained = true,
            private_reads = 0, worker_calls = 0, ocr_calls = 0, truth_reads = 0 };
    }
}
