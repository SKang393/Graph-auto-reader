// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
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

}
