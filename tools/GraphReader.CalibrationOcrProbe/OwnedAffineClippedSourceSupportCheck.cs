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
    private const string ClippedSupportImagingHash = "00602cf9b13c325b6d5123bc10619770df9d01962e4c02eb2976aa1fdc491ced";
    private static readonly string[] ClippedSupportStages =
        ["render", "baseline_binding", "rectification", "gap_replay", "source_support", "preservation"];
    private static readonly string[] ClippedSupportCases = ["positive_half_s1_g128", "negative_half_s1_g128"];

    internal static AffineTickRefusalOutcome RunOwnedAffineClippedSourceSupport(string baselinePath,
        CpuBudgetJob budget, CancellationToken token)
    {
        var total = Stopwatch.StartNew();
        long allocated = GC.GetTotalAllocatedBytes(), sourceBytes = 0, derivedBytes = 0;
        var results = new List<object>(2);
        var stageCounts = ClippedSupportStages.ToDictionary(name => name,
            _ => new Dictionary<string, int>(StringComparer.Ordinal) { ["completed"] = 0, ["failed"] = 0, ["unattempted"] = 0 },
            StringComparer.Ordinal);
        int attempted = 0, completed = 0, failed = 0, unattempted = 0, eligible = 0, feasibilityRefusals = 0;
        string? preflightFailure = null, loadedImagingHash = null;
        JsonElement baseline = default;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(baselinePath) || budget.MaximumLogicalCores > .5) throw new InvalidDataException();
            byte[] imaging = Program.ReadBounded(typeof(AffineRasterRectifier).Assembly.Location, 16_777_216);
            loadedImagingHash = Program.Hash(imaging);
            Program.RequireHash(imaging, ClippedSupportImagingHash);
            byte[] bytes = Program.ReadBounded(baselinePath, 131_072);
            Program.RequireHash(bytes, StrokeBaselineReportHash);
            using JsonDocument document = JsonDocument.Parse(bytes);
            baseline = document.RootElement.Clone();
            if (baseline.GetProperty("schema").GetString() != "graphreader.owned-affine-stroke-sampling.v1" ||
                !baseline.GetProperty("safety_passed").GetBoolean() || baseline.GetProperty("declared").GetInt32() != 13 ||
                baseline.GetProperty("completed").GetInt32() != 13 || baseline.GetProperty("failed").GetInt32() != 0 ||
                baseline.GetProperty("unattempted").GetInt32() != 0)
                throw new InvalidDataException();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            preflightFailure = error is OperationCanceledException ? "CANCELLED" : "BASELINE_OR_IMAGING_BINDING_FAILED";
        }

        for (int caseIndex = 0; caseIndex < ClippedSupportCases.Length; caseIndex++)
        {
            string name = ClippedSupportCases[caseIndex];
            var stages = ClippedSupportStages.ToDictionary(key => key, _ => "unattempted", StringComparer.Ordinal);
            if (preflightFailure is not null || token.IsCancellationRequested)
            {
                unattempted++;
                foreach (string key in ClippedSupportStages) stageCounts[key]["unattempted"]++;
                results.Add(new { name, status = "unattempted", reason = preflightFailure ?? "CANCELLED", stages });
                continue;
            }
            attempted++;
            string stage = "render"; var clock = Stopwatch.StartNew();
            try
            {
                // These are exactly the two existing thin-gray controls. No new
                // rendering, radius, clipping or threshold variant is explored.
                StrokeFrame frame = StrokeFrames[caseIndex + 1];
                double angle = frame.Angle * Math.PI / 180;
                ImagePoint origin = new(55 + frame.Phase, 185 + frame.Phase);
                ImagePoint xEnd = new(origin.X + 210 * Math.Cos(angle), origin.Y + 210 * Math.Sin(angle));
                ImagePoint yEnd = new(origin.X + 140 * Math.Sin(angle), origin.Y - 140 * Math.Cos(angle));
                byte[] source = Enumerable.Repeat((byte)255, StrokeCanvasWidth * StrokeCanvasHeight).ToArray();
                StrokePaintLine(source, StrokeRound(origin), StrokeRound(xEnd), 1, 128, false, token);
                StrokePaintLine(source, StrokeRound(origin), StrokeRound(yEnd), 1, 128, false, token);
                string sourceHash = Program.Hash(source); sourceBytes += source.Length;
                stages[stage] = "completed"; stage = "baseline_binding";
                JsonElement[] matches = baseline.GetProperty("cases").EnumerateArray()
                    .Where(row => row.GetProperty("name").GetString() == name).ToArray();
                if (matches.Length != 1) throw new InvalidDataException();
                JsonElement previous = matches[0];
                if (previous.GetProperty("status").GetString() != "completed" ||
                    previous.GetProperty("angle").GetDouble() != frame.Angle || previous.GetProperty("phase").GetDouble() != frame.Phase ||
                    previous.GetProperty("digital_width").GetInt32() != 1 || previous.GetProperty("source_ink_gray").GetInt32() != 128 ||
                    previous.GetProperty("expected_disconnected_x").GetBoolean() || !previous.GetProperty("original_unchanged").GetBoolean() ||
                    !previous.GetProperty("source_x_connected").GetBoolean() || !previous.GetProperty("source_y_connected").GetBoolean() ||
                    previous.GetProperty("source_sha256").GetString() != sourceHash)
                    throw new InvalidDataException();
                stages[stage] = "completed"; stage = "rectification";
                AffineRasterRectificationResult raster = AffineRasterRectifier.Rectify(source, StrokeCanvasWidth, StrokeCanvasHeight,
                    StrokeCanvasWidth, origin, xEnd, yEnd, token);
                byte[] pixels = raster.Pixels.ToArray(); derivedBytes += pixels.Length;
                string derivedHash = Program.Hash(pixels);
                if (raster.Width != previous.GetProperty("width").GetInt32() || raster.Height != previous.GetProperty("height").GetInt32() ||
                    derivedHash != previous.GetProperty("derived_sha256").GetString()) throw new InvalidDataException();
                stages[stage] = "completed"; stage = "gap_replay";
                var box = new RustAxisGeometry(raster.Left, raster.Bottom, raster.Right, raster.Top, 0);
                bool originInk = pixels[box.Bottom * raster.Width + box.Left] < 200;
                bool xEndInk = pixels[box.Bottom * raster.Width + box.Right] < 200;
                JsonElement anchorInk = previous.GetProperty("exact_frame_anchor_ink"), cachedReplay = previous.GetProperty("exact_x_span_replay");
                SpanReplay replay = ReplayHorizontalSpan(pixels, raster.Width, raster.Height, box, token);
                if (!originInk || !xEndInk || !anchorInk.GetProperty("origin").GetBoolean() || !anchorInk.GetProperty("x_end").GetBoolean() ||
                    replay.Observed || replay.Kind != "DISCONNECTED" || replay.Window is null ||
                    cachedReplay.GetProperty("observed").GetBoolean() != replay.Observed || cachedReplay.GetProperty("kind").GetString() != replay.Kind ||
                    cachedReplay.GetProperty("work").GetInt32() != replay.Work || cachedReplay.GetProperty("gap_length").GetInt32() != replay.GapLength)
                    throw new InvalidDataException();
                stages[stage] = "completed"; stage = "source_support";
                // Use the production-clipped replay window verbatim. In
                // particular, do not construct a perpendicular-relaxed window.
                OriginalSupportProof proof = ObserveOriginalSupport(source, StrokeCanvasWidth, StrokeCanvasHeight,
                    raster.TransformChain, replay.Window, box.Bottom, token);
                bool eligibleSupport = !proof.Uncertain && proof.SearchComplete && proof.AnySupportConnection == true;
                stages[stage] = "completed"; stage = "preservation";
                bool unchanged = sourceHash == Program.Hash(source) && derivedHash == Program.Hash(pixels);
                if (!unchanged) throw new InvalidDataException();
                token.ThrowIfCancellationRequested(); stages[stage] = "completed";
                if (eligibleSupport) eligible++; else feasibilityRefusals++;
                completed++;
                results.Add(new
                {
                    name, status = "completed", frame.Angle, frame.Phase, digital_width = 1, source_ink_gray = 128,
                    source_sha256 = sourceHash, derived_sha256 = derivedHash, original_unchanged = unchanged,
                    raster.Width, raster.Height, baseline_pixel_parity = true, origin_ink = originInk, x_end_ink = xEndInk,
                    replay.Kind, replay.Work, replay.GapLength, maximum_span = replay.MaxSpan, bridge_radius = replay.Window.Radius,
                    production_clipped_window_unchanged = true, eligible_source_support = eligibleSupport, proof,
                    stages, elapsed_milliseconds = clock.Elapsed.TotalMilliseconds, raster.PacingMilliseconds,
                });
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failed++; stages[stage] = "failed";
                results.Add(new { name, status = error is OperationCanceledException ? "cancelled" : "failed", stage,
                    error = error is OperationCanceledException ? "CANCELLED" : error.GetType().Name,
                    stages, elapsed_milliseconds = clock.Elapsed.TotalMilliseconds });
            }
            foreach (string key in ClippedSupportStages) stageCounts[key][stages[key]]++;
        }
        bool done = preflightFailure is null && completed == 2 && failed == 0 && unattempted == 0;
        return new(new
        {
            schema = "graphreader.owned-affine-clipped-source-support.v1", status = done ? "completed" : "refused_or_failed",
            declared = 2, attempted, completed, failed, unattempted, preflight_failure = preflightFailure,
            eligible_source_support_controls = eligible, feasibility_refusals = feasibilityRefusals,
            branch_closed_without_widening = done && eligible == 0,
            baseline_report_sha256 = StrokeBaselineReportHash, full_thirteen_case_baseline_reexecuted = false, bound_controls_recreated = 2,
            imaging_assembly_sha256 = loadedImagingHash, required_imaging_assembly_sha256 = ClippedSupportImagingHash,
            original_width = StrokeCanvasWidth, original_height = StrokeCanvasHeight, threshold_exclusive = 200,
            original_connectivity = "All positive-weight ink contributors enumerated; any 8-connected source path inside the unchanged production-clipped window",
            interpretation_limit = "Connectivity does not establish semantic axis identity, scientific correctness or production acceptance",
            stages = stageCounts, cases = results, wall_seconds = total.Elapsed.TotalSeconds,
            memory = new { source_gray_bytes = sourceBytes, derived_gray_bytes = derivedBytes,
                cumulative_parent_allocated_bytes = GC.GetTotalAllocatedBytes() - allocated },
            budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores,
            private_reads = 0, worker_calls = 0, ocr_calls = 0, fit_calls = 0, images_written = false,
        }, done);
    }
}
