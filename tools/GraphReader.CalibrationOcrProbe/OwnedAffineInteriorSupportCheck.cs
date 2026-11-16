// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GraphReader.Axis;
using GraphReader.Imaging;
using ImagePoint = GraphReader.Imaging.PixelPoint;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private static readonly string[] InteriorSupportStages = ["render", "source_connectivity", "rectification", "gap_replay", "source_support", "preservation"];

    internal static AffineTickRefusalOutcome RunOwnedAffineInteriorSupport(CpuBudgetJob budget, CancellationToken token)
    {
        var total = Stopwatch.StartNew();
        long allocations = GC.GetTotalAllocatedBytes(), sourceBytes = 0, derivedBytes = 0;
        var cases = new List<object>(2);
        var counts = InteriorSupportStages.ToDictionary(name => name,
            _ => new Dictionary<string, int>(StringComparer.Ordinal) { ["completed"] = 0, ["failed"] = 0, ["unattempted"] = 0 }, StringComparer.Ordinal);
        int attempted = 0, completed = 0, failed = 0, unattempted = 0, expectationFailed = 0;
        bool preflight = budget.MaximumLogicalCores is > 0 and <= .5;
        string? imagingHash = null, preflightFailure = preflight ? null : "CPU_BUDGET";
        if (preflight)
        {
            try { imagingHash = Program.Hash(Program.ReadBounded(typeof(AffineRasterRectifier).Assembly.Location, 16_777_216)); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { preflight = false; preflightFailure = error.GetType().Name; }
        }
        foreach (bool disconnected in new[] { false, true })
        {
            string name = disconnected ? "disconnected" : "connected";
            var stages = InteriorSupportStages.ToDictionary(stage => stage, _ => "unattempted", StringComparer.Ordinal);
            var timings = new Dictionary<string, double>(StringComparer.Ordinal);
            if (!preflight || token.IsCancellationRequested)
            {
                unattempted++;
                foreach (string key in InteriorSupportStages) counts[key]["unattempted"]++;
                cases.Add(new { name, status = "unattempted", reason = preflight ? "CANCELLED" : preflightFailure, stages });
                continue;
            }
            attempted++;
            string stage = "render"; var timer = Stopwatch.StartNew();
            void Next(string next) { stages[stage] = "completed"; timings[stage] = timer.Elapsed.TotalMilliseconds; timer.Restart(); stage = next; }
            try
            {
                token.ThrowIfCancellationRequested();
                const int width = 320, height = 240;
                ImagePoint origin = new(55, 185), xEnd = new(265, 192), yEnd = new(55, 45);
                byte[] source = RenderInteriorSupport(disconnected, token);
                string sourceHash = Program.Hash(source); sourceBytes += source.Length;
                Next("source_connectivity");
                bool connected = StrokeConnected(source, width, height, new StrokePixel(55, 185), new StrokePixel(265, 192), token);
                bool yConnected = StrokeConnected(source, width, height, new StrokePixel(55, 185), new StrokePixel(55, 45), token);
                Next("rectification");
                AffineRasterRectificationResult raster = AffineRasterRectifier.Rectify(source, width, height, width, origin, xEnd, yEnd, token);
                byte[] pixels = raster.Pixels.ToArray(); derivedBytes += pixels.Length;
                string derivedHash = Program.Hash(pixels);
                Next("gap_replay");
                var box = new RustAxisGeometry(raster.Left, raster.Bottom, raster.Right, raster.Top, 0);
                byte originGray = pixels[box.Bottom * raster.Width + box.Left], xEndGray = pixels[box.Bottom * raster.Width + box.Right];
                SpanReplay replay = ReplayHorizontalSpan(pixels, raster.Width, raster.Height, box, token);
                Next("source_support");
                OriginalSupportProof? proof = null;
                if (replay.Window is { } window)
                {
                    proof = ObserveOriginalSupport(source, width, height, raster.TransformChain, window, box.Bottom, token);
                    Next("preservation");
                }
                else { stage = "preservation"; timer.Restart(); }
                bool unchanged = sourceHash == Program.Hash(source) && derivedHash == Program.Hash(pixels);
                token.ThrowIfCancellationRequested();
                stages[stage] = "completed"; timings[stage] = timer.Elapsed.TotalMilliseconds;
                bool expected = connected == !disconnected && yConnected && unchanged && originGray < 200 && xEndGray < 200 &&
                    !replay.Observed && proof is { SearchComplete: true, LeftOutsideWindow: 0, RightOutsideWindow: 0 } &&
                    proof.AnySupportConnection == !disconnected && (disconnected || !proof.Uncertain);
                completed++; if (!expected) expectationFailed++;
                cases.Add(new { name, status = expected ? "passed" : "expectation_failed", source_sha256 = sourceHash,
                    derived_sha256 = derivedHash, source_x_connected = connected, source_y_connected = yConnected,
                    original_and_derived_unchanged = unchanged, raster.Width, raster.Height, origin_gray = originGray, x_end_gray = xEndGray,
                    replay.Observed, replay.Kind, replay.GapLength, replay.Work, replay.MaxSpan,
                    unchanged_production_window = replay.Window is not null, proof, stages, timings, raster.PacingMilliseconds });
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failed++; stages[stage] = "failed"; timings[stage] = timer.Elapsed.TotalMilliseconds;
                cases.Add(new { name, status = error is OperationCanceledException ? "cancelled" : "failed",
                    error = error is OperationCanceledException ? "CANCELLED" : error.GetType().Name, stages, timings });
            }
            foreach (string key in InteriorSupportStages) counts[key][stages[key]]++;
        }
        bool success = preflight && completed == 2 && failed == 0 && expectationFailed == 0 && unattempted == 0;
        return new(new
        {
            schema = "graphreader.owned-affine-interior-support.v1", status = success ? "passed" : "refused_or_failed",
            declared = 2, attempted, completed, failed, expectation_failed = expectationFailed, unattempted, preflight_failure = preflightFailure,
            loaded_imaging_sha256 = imagingHash,
            fixture = "320x240 gray192 one-pixel X: x55..265, y185+floor((x-55)/30); Y x55 y45..185; negative removes X offsets15..23",
            frame = "original (55,185), X end (265,192), Y end (55,45); shallow shear",
            source_connectivity = "independent eight-connected whole-source search", support_window = "unchanged production-clipped X replay window",
            interpretation_limit = "Owned source support does not establish semantic axis identity or production acceptance.",
            stages = counts, cases, elapsed_milliseconds = total.Elapsed.TotalMilliseconds,
            memory = new { source_gray_bytes = sourceBytes, derived_gray_bytes = derivedBytes,
                cumulative_parent_allocated_bytes = GC.GetTotalAllocatedBytes() - allocations },
            budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores,
            private_reads = 0, sealed_reads = 0, worker_calls = 0, ocr_calls = 0, fit_calls = 0, images_written = false,
        }, success);
    }

    private static byte[] RenderInteriorSupport(bool disconnected, CancellationToken token)
    {
        byte[] source = Enumerable.Repeat((byte)255, 320 * 240).ToArray();
        for (int x = 55; x <= 265; x++)
        {
            token.ThrowIfCancellationRequested();
            if (!disconnected || x - 55 is < 15 or > 23) source[(185 + (x - 55) / 30) * 320 + x] = 192;
        }
        for (int y = 45; y <= 185; y++) source[y * 320 + 55] = 192;
        return source;
    }

    internal static async Task<AffineTickRefusalOutcome> RunOwnedAffineOriginalSupportAsync(
        string worker, string baselinePath, CpuBudgetJob budget, CancellationToken token)
    {
        const string workerHash = "08538da44ff7f680f951c7cbe08c806801b74d9c44cff982bf507ae0f995edca";
        const string baselineHash = "8069c2c91e7c71d7d0607f586a976308904e62addfeb497ed3d21e1cf6a457e3";
        string[] stageNames = ["render", "rectification", "baseline_parity", "worker", "preservation"];
        var counts = stageNames.ToDictionary(s => s, _ => new Dictionary<string, int>(StringComparer.Ordinal)
            { ["completed"] = 0, ["failed"] = 0, ["unattempted"] = 0 }, StringComparer.Ordinal);
        var cases = new List<object>(2);
        var total = Stopwatch.StartNew();
        int attempted = 0, completed = 0, failed = 0, unattempted = 0, expectationFailed = 0, workerCalls = 0;
        string? preflightFailure = null, imagingHash = null;
        JsonDocument? baseline = null;
        try
        {
            if (budget.MaximumLogicalCores is not (> 0 and <= .5) || !budget.SilentBreakawayVerified)
                throw new InvalidOperationException("CPU_BUDGET");
            if (!Path.IsPathFullyQualified(worker) || Program.Hash(Program.ReadBounded(worker, 64 * 1024 * 1024)) != workerHash)
                throw new InvalidOperationException("WORKER_IDENTITY");
            byte[] bytes = Program.ReadBounded(baselinePath, 1024 * 1024);
            if (Program.Hash(bytes) != baselineHash) throw new InvalidOperationException("BASELINE_IDENTITY");
            baseline = JsonDocument.Parse(bytes);
            JsonElement root = baseline.RootElement;
            if (root.GetProperty("schema").GetString() != "graphreader.owned-affine-interior-support.v1" ||
                root.GetProperty("status").GetString() != "passed" || root.GetProperty("declared").GetInt32() != 2 ||
                root.GetProperty("completed").GetInt32() != 2 || root.GetProperty("failed").GetInt32() != 0 ||
                root.GetProperty("expectation_failed").GetInt32() != 0 || root.GetProperty("unattempted").GetInt32() != 0 ||
                root.GetProperty("cases").GetArrayLength() != 2)
                throw new InvalidOperationException("BASELINE_CONTENT");
            imagingHash = Program.Hash(Program.ReadBounded(typeof(AffineRasterRectifier).Assembly.Location, 16_777_216));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { preflightFailure = error is InvalidOperationException ? error.Message : error.GetType().Name; }

        for (int index = 0; index < 2; index++)
        {
            bool disconnected = index == 1;
            string name = disconnected ? "disconnected" : "connected";
            var stages = stageNames.ToDictionary(s => s, _ => "unattempted", StringComparer.Ordinal);
            var timings = new Dictionary<string, double>(StringComparer.Ordinal);
            if (preflightFailure is not null || token.IsCancellationRequested)
            {
                unattempted++;
                foreach (string key in stageNames) counts[key]["unattempted"]++;
                cases.Add(new { name, status = "unattempted", reason = preflightFailure ?? "CANCELLED", stages });
                continue;
            }
            attempted++;
            string stage = "render";
            var timer = Stopwatch.StartNew();
            void Next(string next) { stages[stage] = "completed"; timings[stage] = timer.Elapsed.TotalMilliseconds; stage = next; timer.Restart(); }
            try
            {
                token.ThrowIfCancellationRequested();
                byte[] source = RenderInteriorSupport(disconnected, token);
                string sourceHash = Program.Hash(source);
                Next("rectification");
                AffineRasterRectificationResult raster = AffineRasterRectifier.Rectify(source, 320, 240, 320,
                    new ImagePoint(55, 185), new ImagePoint(265, 192), new ImagePoint(55, 45), token);
                byte[] pixels = raster.Pixels.ToArray();
                string derivedHash = Program.Hash(pixels);
                Next("baseline_parity");
                JsonElement saved = baseline!.RootElement.GetProperty("cases")[index];
                if (saved.GetProperty("name").GetString() != name || saved.GetProperty("status").GetString() != "passed" ||
                    saved.GetProperty("source_sha256").GetString() != sourceHash || saved.GetProperty("derived_sha256").GetString() != derivedHash ||
                    saved.GetProperty("width").GetInt32() != raster.Width || saved.GetProperty("height").GetInt32() != raster.Height)
                    throw new InvalidOperationException("BASELINE_PIXEL_MISMATCH");
                Next("worker");
                var m = raster.TransformChain.OriginalToDerived;
                var input = new RustAffineTickSource(320, 240, source, m.M11, m.M12, m.M13, m.M21, m.M22, m.M23);
                var box = new RustAxisGeometry(raster.Left, raster.Bottom, raster.Right, raster.Top, 0);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                workerCalls++;
                RustAxisTickMeasurementResult result = await RustAxisTickMeasurement.RunAffineAsync(worker, raster.Width,
                    raster.Height, pixels, box, input, cancellationToken: deadline.Token).ConfigureAwait(false);
                Next("preservation");
                bool unchanged = sourceHash == Program.Hash(source) && derivedHash == Program.Hash(pixels);
                RustAxisTickEvidence? evidence = result.Evidence;
                bool exact = evidence is not null && evidence.Left == box.Left && evidence.Bottom == box.Bottom &&
                    evidence.Right == box.Right && evidence.Top == box.Top && evidence.XTickPositions.Count == 0 && evidence.YTickPositions.Count == 0;
                bool expected = unchanged && (disconnected ? result.ErrorCode == "AXIS_BOX_NOT_OBSERVED" && evidence is null : result.ErrorCode is null && exact);
                stages[stage] = "completed"; timings[stage] = timer.Elapsed.TotalMilliseconds;
                completed++; if (!expected) expectationFailed++;
                cases.Add(new { name, status = expected ? "passed" : "expectation_failed", source_sha256 = sourceHash,
                    derived_sha256 = derivedHash, raster.Width, raster.Height, baseline_parity = true, result.ErrorCode,
                    evidence_present = evidence is not null, exact_box_and_empty_ticks = exact,
                    x_tick_count = evidence?.XTickPositions.Count, y_tick_count = evidence?.YTickPositions.Count,
                    original_and_derived_unchanged = unchanged, stages, timings });
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failed++; stages[stage] = "failed"; timings[stage] = timer.Elapsed.TotalMilliseconds;
                cases.Add(new { name, status = error is OperationCanceledException ? (token.IsCancellationRequested ? "cancelled" : "timeout") : "failed",
                    error = error is InvalidOperationException ? error.Message : error.GetType().Name, stages, timings });
            }
            foreach (string key in stageNames) counts[key][stages[key]]++;
        }
        baseline?.Dispose();
        bool success = preflightFailure is null && completed == 2 && failed == 0 && expectationFailed == 0 && unattempted == 0;
        return new(new { schema = "graphreader.owned-affine-original-support.v1", status = success ? "passed" : "refused_or_failed",
            declared = 2, attempted, completed, failed, expectation_failed = expectationFailed, unattempted, worker_calls = workerCalls,
            preflight_failure = preflightFailure, worker_sha256 = workerHash, baseline_sha256 = baselineHash,
            loaded_imaging_sha256 = imagingHash, stages = counts, cases, elapsed_milliseconds = total.Elapsed.TotalMilliseconds,
            budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores,
            interpretation_limit = "Owned supplied geometry; not automatic calibration.", private_reads = 0, sealed_reads = 0,
            ocr_calls = 0, fit_calls = 0, images_written = false }, success);
    }
}
