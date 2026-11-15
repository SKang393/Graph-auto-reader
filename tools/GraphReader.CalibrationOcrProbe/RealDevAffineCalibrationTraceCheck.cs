// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Pdf;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string AffineCachedHash = "b77aaafc2c16e39e526373888c77a0e55b228a97821551d200e83471581d0993";
    private sealed record AffineTarget(int ProjectIndex, int Width, int Height, PdfRectD Crop);
    private static readonly string[] AffineStageNames = ["preparation", "geometry", "rectification", "tick_measurement", "ocr", "association"];
    private static Dictionary<string, string> NewAffineStages() => AffineStageNames
        .ToDictionary(name => name, _ => "unattempted", StringComparer.Ordinal);
    private static void BeginAffineStage(Dictionary<string, string> stages, ref string stage, string name)
    {
        if (stages.TryGetValue(stage, out string? value) && value == "attempted") stages[stage] = "completed";
        stage = name; stages[name] = "attempted";
    }

    private static AffineTarget ParseAffineTarget(byte[] bytes, string expectedHash = AffineCachedHash)
    {
        Program.RequireHash(bytes, expectedHash);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        if (root.GetProperty("schema").GetString() != NativeAffineTraceSchema ||
            root.GetProperty("selection").GetString() != "first_12_sorted_anonymized_case_id" ||
            root.GetProperty("sample_denominator").GetInt32() != TraceSampleCount || root.GetProperty("frozen_dev_denominator").GetInt32() != Denominator ||
            root.GetProperty("attempted").GetInt32() != TraceSampleCount || root.GetProperty("unattempted").GetInt32() != 0 ||
            root.GetProperty("sealed_payload_reads").GetInt32() != 0 || root.GetProperty("truth_scoring").GetBoolean() || root.GetProperty("image_writes").GetBoolean() ||
            root.GetProperty("assignment_sha256").GetString() != CachedAxisAssignmentHash ||
            root.GetProperty("selected_inventory_sha256").GetString() != CachedAxisInventoryHash || root.GetProperty("source_digest").GetString() != CachedAxisSourceDigest)
            throw new InvalidDataException();
        JsonElement projects = root.GetProperty("projects");
        if (projects.GetArrayLength() != TraceSampleCount) throw new InvalidDataException();
        var targets = new List<AffineTarget>(); int i = 0;
        foreach (JsonElement project in projects.EnumerateArray())
        {
            if (project.GetProperty("index").GetInt32() != i || project.GetProperty("status").GetString() != "completed") throw new InvalidDataException();
            foreach (JsonElement panel in project.GetProperty("panels").EnumerateArray())
            {
                if (panel.GetProperty("status").GetString() != "AFFINE_GEOMETRY_RETURNED") continue;
                JsonElement crop = panel.GetProperty("crop");
                targets.Add(new(i, panel.GetProperty("width").GetInt32(), panel.GetProperty("height").GetInt32(),
                    new PdfRectD(ExactInt(crop.GetProperty("x")), ExactInt(crop.GetProperty("y")), ExactInt(crop.GetProperty("width")), ExactInt(crop.GetProperty("height")))));
            }
            i++;
        }
        return targets.Count == 1 ? targets[0] : throw new InvalidDataException();
    }

    internal static async Task<object> RunAffineCalibrationTraceAsync(string cached, string corpus, string app, string assets, string rust, CpuBudgetJob budget, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified || !new[] { cached, corpus, app, assets, rust }.All(Path.IsPathFullyQualified)) throw new InvalidDataException();
        AffineTarget target = ParseAffineTarget(Program.ReadBounded(cached, 1_000_000));
        using Candidate candidate = Candidate.VerifyAffineCalibration(app, assets, rust, token);
        var reader = ResolveEmbeddedImageReader();
        var binder = typeof(ManualPreviewWorkspaceService).GetMethod("TryBindMeasuredTicks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            null, [typeof(AxisTickMeasurementResult), typeof(RustAxisGeometry), typeof(string), typeof(int), typeof(int)], null);
        if (binder is null || binder.ReturnType != typeof(RustAxisGeometry)) throw new InvalidDataException();
        var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpus, token);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        if (metadata.AssignmentSha256 != CachedAxisAssignmentHash || metadata.SelectedInventorySha256 != CachedAxisInventoryHash) throw new InvalidDataException();
        FrozenRealCorpusProject[] all = selector.EnumerateSelectedProjects(token).OrderBy(p => p.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
        if (all.Length != Denominator || all.Any(p => p.Split != FrozenRealCorpusInventory.RealDev)) throw new InvalidDataException();
        int sourceAttempted = 0, sourceCompleted = 0; string stage = "source_read"; object? output = null;
        var timings = new Dictionary<string, double>(StringComparer.Ordinal); var timer = Stopwatch.StartNew(); string? stageDetail = null;
        var stages = NewAffineStages();
        object? geometryEvidence = null, tickEvidence = null; int? invalidPolygonCount = null;
        void Begin(string name) => BeginAffineStage(stages, ref stage, name);
        ImageOnlyPayload? selected = null; using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            for (int i = 0; i < TraceSampleCount; i++)
            {
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); readDeadline.CancelAfter(TimeSpan.FromSeconds(45));
                sourceAttempted++; ImageOnlyPayload item = ReadLockedImageOnly(selector, all[i], digest, reader, readDeadline.Token); sourceCompleted++;
                if (i == target.ProjectIndex) selected = item;
            }
            stage = "source_digest";
            if (Convert.ToHexStringLower(digest.GetHashAndReset()) != CachedAxisSourceDigest || selected is null) throw new InvalidDataException();
            timings["source_read"] = timer.Elapsed.TotalMilliseconds; timer.Restart();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
            Begin("preparation"); var hash = Convert.ToHexStringLower(SHA256.HashData(selected.Image));
            IReadOnlyList<object>? panels = null;
            try { panels = await PrepareAsync(panelizer, selected.Image, hash, selected.Width, selected.Height, deadline.Token).ConfigureAwait(false); }
            catch (Exception error) when (IsGenuineNoPanel(error)) { }
            var prepared = panels is null ? [(selected.Image, selected.Width, selected.Height, new PdfRectD(0, 0, selected.Width, selected.Height))] :
                panels.Select(p => ReadPreparedPanel(p, selected.Width, selected.Height)).ToArray();
            var matches = prepared.Where(p => p.Item2 == target.Width && p.Item3 == target.Height && p.Item4 == target.Crop).ToArray();
            if (matches.Length != 1) throw new InvalidDataException();
            timings["preparation"] = timer.Elapsed.TotalMilliseconds; timer.Restart();
            var panel = matches[0]; Begin("geometry");
            var primary = new TraceRustAxis(rust) { Width = panel.Item2, Height = panel.Item3 };
            var secondary = new TraceAxis(new OpenCvAxisProposalService(app));
            var axis = new RustFirstAxisProposalService(primary, secondary);
            RustAxisProposalResult proposal = await axis.SuggestAsync(panel.Item1, deadline.Token).ConfigureAwait(false);
            geometryEvidence = new { rust_error = primary.Last?.Proposal.ErrorCode, native_error = secondary.Last?.ErrorCode,
                native_review_reasons = secondary.Last?.ReviewReasons, proposal.ErrorCode, proposal.Provider, proposal.ReviewReasons };
            stageDetail = proposal.ErrorCode switch { "NO_AXIS" or "AMBIGUOUS_AXIS" or "NO_INTERSECTION" or "OPENCV_AXIS_AMBIGUOUS" or "WORKER_TIMEOUT" => proposal.ErrorCode, null => null, _ => "OTHER_FAILURE" };
            if (!NativeAffineTraceAccepted(proposal, panel.Item2, panel.Item3)) throw new InvalidDataException();
            timings["geometry"] = timer.Elapsed.TotalMilliseconds; timer.Restart();
            Begin("rectification"); stageDetail = null;
            var (frame, image) = CalibrationWorkingFrame.Create(panel.Item1, panel.Item2, panel.Item3, proposal.AffineGeometry!, hash, deadline.Token);
            timings["rectification"] = timer.Elapsed.TotalMilliseconds; timer.Restart();
            Begin("tick_measurement");
            AxisTickMeasurementResult measurement = await new RustAxisTickMeasurementService(rust).MeasureAsync(image, frame.Geometry, deadline.Token).ConfigureAwait(false);
            stageDetail = SafeTickMeasurementError(measurement.ErrorCode);
            RustAxisGeometry? measured = (RustAxisGeometry?)binder.Invoke(null, [measurement, frame.Geometry, frame.DerivedImageSha256, frame.Width, frame.Height]);
            RustAxisGeometry geometry = measured ?? frame.Geometry;
            tickEvidence = new { error = measured is null ? SafeTickMeasurementError(measurement.ErrorCode ?? "INVALID_TICK_EVIDENCE") : null,
                bound = measured is not null, x_tick_count = geometry.XTickPositions.Count, y_tick_count = geometry.YTickPositions.Count };
            timings["tick_measurement"] = timer.Elapsed.TotalMilliseconds; timer.Restart();
            Begin("ocr"); stageDetail = null;
            var request = new CalibrationOcrRequest("affine-trace", frame.Binding, hash, frame.DerivedImageSha256, frame.Width, frame.Height,
                new(geometry.Left, geometry.Top, geometry.Right, geometry.Bottom)) { MeasuredXTicks = geometry.XTickPositions.Count > 0 ? geometry.XTickPositions.ToArray() : null, MeasuredYTicks = geometry.YTickPositions.Count > 0 ? geometry.YTickPositions.ToArray() : null };
            await using var ocr = new CalibrationOcrWorkerClient(app, assets, () => CalibrationOcrDeviceSelection.Cpu);
            CalibrationOcrResponse response = await ocr.SuggestAsync(request, image, deadline.Token).ConfigureAwait(false);
            stageDetail = response.FailureCode;
            if (!CalibrationOcrWorkerClient.IsValidResponse(request, response) || response.FailureCode is not null) throw new InvalidDataException();
            CalibrationOcrRuntime runtime = response.Runtime ?? throw new InvalidDataException();
            if (runtime.Provider != "Cpu" || runtime.RequestedProvider != "Cpu" || runtime.ModelSha256 != Program.ModelHash || runtime.ManifestSha256 != Program.ManifestHash || runtime.Files.Any(f => !candidate.Files.TryGetValue(f.Name, out string? expected) || f.Sha256 != expected)) throw new InvalidDataException();
            invalidPolygonCount = response.Proposals.Count(r => r.Polygon.Any(p => !frame.ContainsOriginalEdge(frame.ToOriginal(p))));
            if (invalidPolygonCount != 0) { stageDetail = "ORIGINAL_POLYGON_OUT_OF_BOUNDS"; throw new InvalidDataException(); }
            timings["ocr"] = timer.Elapsed.TotalMilliseconds; timer.Restart();
            Begin("association"); stageDetail = null; CalibrationTickProposalResult fit = CalibrationTickProposal.CreateReviewDraft(geometry, response);
            CalibrationTickAnchors? anchors = fit.IsComplete ? fit.Anchors : fit.PrintedXReviewDraft?.Anchors;
            var original = anchors is null ? null : frame.ToOriginal(anchors);
            if (original is not null && (!frame.ContainsOriginal(original.FirstSessionY0) || !frame.ContainsOriginal(original.FirstSessionYMaximum) || !frame.ContainsOriginal(original.FinalSessionY0))) throw new InvalidDataException();
            CalibrationOcrPoint AddCrop(CalibrationOcrPoint point) => new(point.X + target.Crop.X, point.Y + target.Crop.Y);
            var sourceAnchors = original is null ? null : new CalibrationTickAnchors(AddCrop(original.FirstSessionY0), AddCrop(original.FirstSessionYMaximum), AddCrop(original.FinalSessionY0));
            timings["association"] = timer.Elapsed.TotalMilliseconds;
            output = new { status = "completed", provider = proposal.Provider, proposal.ReviewReasons, rectified = true,
                tick_error = measured is null ? SafeTickMeasurementError(measurement.ErrorCode ?? "INVALID_TICK_EVIDENCE") : null,
                x_tick_count = geometry.XTickPositions.Count, y_tick_count = geometry.YTickPositions.Count,
                row_count = response.Proposals.Count, numeric_count = response.Proposals.Count(r => r.NumericValue is not null),
                paired_count = fit.PairedTicks.Count, fit.IsComplete, printed_x_review = fit.PrintedXReviewDraft is not null,
                fit.UsesEstimatedPositions, fit.Reasons, original_panel_anchors = original, original_source_anchors = sourceAnchors,
                original_polygon_count = response.Proposals.Count(r => r.Polygon.All(p => frame.ContainsOriginalEdge(frame.ToOriginal(p)))),
                rows = response.Proposals.Select(r => new { r.Text, r.NumericValue, r.Role, r.SuggestedAxis, r.FailureCode,
                    original_polygon = r.Polygon.Select(p => { var q = frame.ToOriginal(p); return new { x = q.X + target.Crop.X, y = q.Y + target.Crop.Y }; }).ToArray() }).ToArray(),
                pairs = fit.PairedTicks.Select(p => { var q = frame.ToOriginal(p.Axis == "X" ? new CalibrationOcrPoint(p.PixelPosition, geometry.Bottom) : new CalibrationOcrPoint(geometry.Left, p.PixelPosition)); return new { p.Axis, p.PixelPosition, p.Value, p.IsEstimated, source_x = q.X + target.Crop.X, source_y = q.Y + target.Crop.Y }; }).ToArray(),
                crop = target.Crop, timings };
            stages["association"] = "completed"; stage = "complete";
        }
        catch (OperationCanceledException) { if (stages.ContainsKey(stage)) stages[stage] = "failed"; timings[stage] = timer.Elapsed.TotalMilliseconds; output = new { status = "failed", stage, category = token.IsCancellationRequested ? "CANCELLED" : "PROJECT_TIMEOUT", timings }; }
        catch (Exception error) when (error is not OutOfMemoryException) { if (stages.ContainsKey(stage)) stages[stage] = "failed"; timings[stage] = timer.Elapsed.TotalMilliseconds; output = new { status = "failed", stage, category = TraceExceptionCategory(error), detail_code = stageDetail, timings }; }
        return new { schema = AffineCalibrationTraceSchema, scope = "development_only_sensitive_metadata", selected_panels = 1, cached_other_projects = 11,
            source_attempted = sourceAttempted, source_completed = sourceCompleted, source_failed = sourceAttempted - sourceCompleted,
            source_unattempted = TraceSampleCount - sourceAttempted, source_digest_verified = sourceCompleted == TraceSampleCount && stage != "source_digest",
            stages, stages_completed = stages.Values.Count(value => value == "completed"), stages_failed = stages.Values.Count(value => value == "failed"),
            stages_unattempted = stages.Values.Count(value => value == "unattempted"), invalid_original_polygon_count = invalidPolygonCount,
            geometry_evidence = geometryEvidence, tick_evidence = tickEvidence, geometry_ocr_pipeline = output,
            truth_scoring = false, sealed_payload_reads = 0, image_writes = false, apply_called = false, export_called = false };
    }

    internal static object AffineCalibrationTraceSelfTest()
    {
        try { _ = ParseAffineTarget("tampered"u8.ToArray()); throw new InvalidOperationException(); } catch (InvalidDataException) { }
        object[] projects = Enumerable.Range(0, TraceSampleCount).Select(i => (object)new { index = i, status = "completed", panels = i == 3 ? new object[] { new { status = "AFFINE_GEOMETRY_RETURNED", width = 64, height = 64, crop = new { x = 0, y = 0, width = 64, height = 64 } } } : Array.Empty<object>() }).ToArray();
        byte[] sample = JsonSerializer.SerializeToUtf8Bytes(new { schema = NativeAffineTraceSchema, selection = "first_12_sorted_anonymized_case_id", sample_denominator = TraceSampleCount, frozen_dev_denominator = Denominator, attempted = TraceSampleCount, unattempted = 0, sealed_payload_reads = 0, truth_scoring = false, image_writes = false, assignment_sha256 = CachedAxisAssignmentHash, selected_inventory_sha256 = CachedAxisInventoryHash, source_digest = CachedAxisSourceDigest, projects }, Program.Json);
        AffineTarget target = ParseAffineTarget(sample, Convert.ToHexStringLower(SHA256.HashData(sample)));
        if (target.ProjectIndex != 3 || target.Width != 64 || target.Height != 64 || target.Crop.Width != 64) throw new InvalidDataException();
        var stages = NewAffineStages(); string stage = "source_read";
        BeginAffineStage(stages, ref stage, "preparation"); BeginAffineStage(stages, ref stage, "geometry"); stages[stage] = "failed";
        if (stages.Values.Count(value => value == "completed") != 1 || stages.Values.Count(value => value == "failed") != 1 || stages.Values.Count(value => value == "unattempted") != 4) throw new InvalidDataException();
        try { RequireNativeRejectionPins("", AffineCalibrationDllHash, AffineCalibrationRustHash, AffineCalibrationRuntimeLibraries); throw new InvalidOperationException(); } catch (InvalidDataException) { }
        if (AffineCalibrationRuntimeLibraries.Count != 20 || AffineCalibrationRuntimeLibraries["GraphReader.App.dll"] != AffineCalibrationDllHash || AffineCalibrationRuntimeLibraries["OpenCvSharpExtern.dll"] != "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31") throw new InvalidDataException();
        return new { schema = "graphreader.real-dev-affine-calibration-trace-self-test.v1", tamper_refused = true, selection_checked = true, stage_accounting_checked = true, missing_pins_refused = true, private_reads = 0 };
    }
}
