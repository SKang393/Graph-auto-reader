// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using GraphReader.Axis;
using GraphReader.Imaging;
using ImagePoint = GraphReader.Imaging.PixelPoint;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private sealed record StrokeFrame(string Name, double Angle, double Phase);
    private sealed record StrokeCase(string Name, StrokeFrame Frame, int Width, byte Gray, bool Disconnected);
    private readonly record struct StrokePixel(int X, int Y);
    private static readonly StrokeFrame[] StrokeFrames = [new("identity", 0, 0), new("positive_half", 2, .5), new("negative_half", -2, .5)];
    private static readonly int[] StrokeWidths = [1, 3];
    private static readonly byte[] StrokeGrays = [128, 192];
    private const int StrokeCanvasWidth = 320, StrokeCanvasHeight = 240;
    internal static AffineTickRefusalOutcome RunOwnedAffineStrokeSampling(CpuBudgetJob budget, CancellationToken token)
    {
        var cases = new List<StrokeCase>(13);
        foreach (StrokeFrame frame in StrokeFrames) foreach (int width in StrokeWidths) foreach (byte gray in StrokeGrays)
            cases.Add(new(frame.Name + "_s" + width + "_g" + gray, frame, width, gray, false));
        cases.Add(new("disconnected_positive_half_s1_g128", StrokeFrames[1], 1, 128, true));
        var results = new List<object>(cases.Count); int failed = 0, completed = 0, sourceInvalid = 0;
        int exactFrameDisconnections = 0, mappedPlantedDisconnections = 0;
        long sourceBytes = 0, derivedBytes = 0, allocated = GC.GetTotalAllocatedBytes();
        var total = Stopwatch.StartNew();
        foreach (StrokeCase item in cases)
        {
            if (token.IsCancellationRequested) break;
            string stage = "render"; var clock = Stopwatch.StartNew();
            try
            {
                token.ThrowIfCancellationRequested();
                double angle = item.Frame.Angle * Math.PI / 180;
                ImagePoint origin = new(55 + item.Frame.Phase, 185 + item.Frame.Phase);
                ImagePoint xEnd = new(origin.X + 210 * Math.Cos(angle), origin.Y + 210 * Math.Sin(angle));
                ImagePoint yEnd = new(origin.X + 140 * Math.Sin(angle), origin.Y - 140 * Math.Cos(angle));
                StrokePixel o = StrokeRound(origin), x = StrokeRound(xEnd), y = StrokeRound(yEnd);
                byte[] source = Enumerable.Repeat((byte)255, StrokeCanvasWidth * StrokeCanvasHeight).ToArray();
                StrokePaintLine(source, o, x, item.Width, item.Gray, item.Disconnected, token);
                StrokePaintLine(source, o, y, item.Width, item.Gray, false, token);
                string sourceHash = Program.Hash(source); sourceBytes += source.Length;
                bool sourceX = StrokeConnected(source, StrokeCanvasWidth, StrokeCanvasHeight, o, x, token);
                bool sourceY = StrokeConnected(source, StrokeCanvasWidth, StrokeCanvasHeight, o, y, token);
                if (sourceX != !item.Disconnected || !sourceY) { sourceInvalid++; throw new InvalidDataException(); }
                double renderMilliseconds = clock.Elapsed.TotalMilliseconds;

                stage = "rectification";
                AffineRasterRectificationResult derived = AffineRasterRectifier.Rectify(source, StrokeCanvasWidth, StrokeCanvasHeight,
                    StrokeCanvasWidth, origin, xEnd, yEnd, token);
                byte[] pixels = derived.Pixels.ToArray(); derivedBytes += pixels.Length;
                bool unchanged = sourceHash == Program.Hash(source);
                bool identityParity = item.Frame.Angle != 0 || item.Frame.Phase != 0 ||
                    derived.Width == StrokeCanvasWidth && derived.Height == StrokeCanvasHeight && source.AsSpan().SequenceEqual(pixels);
                if (!unchanged || !identityParity) throw new InvalidDataException();
                double rectificationMilliseconds = clock.Elapsed.TotalMilliseconds - renderMilliseconds;
                stage = "observation";
                StrokePixel exactO = new(derived.Left, derived.Bottom), exactX = new(derived.Right, derived.Bottom), exactY = new(derived.Left, derived.Top);
                ImagePoint mappedO = derived.TransformChain.ToDerived(new(o.X, o.Y));
                ImagePoint mappedX = derived.TransformChain.ToDerived(new(x.X, x.Y));
                ImagePoint mappedY = derived.TransformChain.ToDerived(new(y.X, y.Y));
                bool exactXConnected = StrokeConnected(pixels, derived.Width, derived.Height, exactO, exactX, token);
                bool exactYConnected = StrokeConnected(pixels, derived.Width, derived.Height, exactO, exactY, token);
                bool mappedXConnected = StrokeConnected(pixels, derived.Width, derived.Height, StrokeRound(mappedO), StrokeRound(mappedX), token);
                bool mappedYConnected = StrokeConnected(pixels, derived.Width, derived.Height, StrokeRound(mappedO), StrokeRound(mappedY), token);
                if (item.Disconnected && mappedXConnected) throw new InvalidDataException();
                if (!item.Disconnected && (!exactXConnected || !exactYConnected)) exactFrameDisconnections++;
                if (!item.Disconnected && (!mappedXConnected || !mappedYConnected)) mappedPlantedDisconnections++;
                bool originInk = pixels[exactO.Y * derived.Width + exactO.X] < 200;
                SpanReplay? replay = originInk ? ReplayHorizontalSpan(pixels, derived.Width, derived.Height,
                    new RustAxisGeometry(derived.Left, derived.Bottom, derived.Right, derived.Top, 0), token) : null;
                results.Add(new
                {
                    item.Name, status = "completed", item.Frame.Angle, item.Frame.Phase,
                    digital_width = item.Width, source_ink_gray = item.Gray, expected_disconnected_x = item.Disconnected,
                    source_sha256 = sourceHash, derived_sha256 = Program.Hash(pixels), original_unchanged = unchanged,
                    source_x_connected = sourceX, source_y_connected = sourceY,
                    source_anchor_to_planted_pixel_offsets = new { origin = StrokeOffset(origin, new(o.X, o.Y)), x_end = StrokeOffset(xEnd, new(x.X, x.Y)), y_end = StrokeOffset(yEnd, new(y.X, y.Y)) },
                    derived_frame_to_mapped_planted_offsets = new { origin = StrokeOffset(new(exactO.X, exactO.Y), mappedO), x_end = StrokeOffset(new(exactX.X, exactX.Y), mappedX), y_end = StrokeOffset(new(exactY.X, exactY.Y), mappedY) },
                    identity_pixel_parity = item.Frame.Angle == 0 && item.Frame.Phase == 0 ? (bool?)identityParity : null,
                    derived.Width, derived.Height, derived.PacingMilliseconds,
                    exact_frame_anchor_ink = new { origin = originInk, x_end = pixels[exactX.Y * derived.Width + exactX.X] < 200, y_end = pixels[exactY.Y * derived.Width + exactY.X] < 200 },
                    exact_frame_x_connected = exactXConnected, exact_frame_y_connected = exactYConnected,
                    mapped_planted_x_connected = mappedXConnected, mapped_planted_y_connected = mappedYConnected,
                    exact_x_row = StrokeProfile(pixels, derived.Width, exactO, exactX),
                    exact_y_column = StrokeProfile(pixels, derived.Width, exactY, exactO),
                    exact_x_span_replay = replay is null ? null : new { replay.Observed, replay.Kind, replay.Work, replay.GapLength },
                    exact_x_span_not_reached_reason = originInk ? null : "ORIGIN_NOT_INK",
                    render_and_source_proof_milliseconds = renderMilliseconds,
                    rectification_milliseconds = rectificationMilliseconds,
                    observation_milliseconds = clock.Elapsed.TotalMilliseconds - renderMilliseconds - rectificationMilliseconds,
                    elapsed_milliseconds = clock.Elapsed.TotalMilliseconds,
                });
                completed++;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failed++;
                results.Add(new { item.Name, status = error is OperationCanceledException ? "cancelled" : "failed", stage,
                    category = error.GetType().Name, elapsed_milliseconds = clock.Elapsed.TotalMilliseconds });
            }
        }
        total.Stop();
        bool safe = cases.Count == 13 && completed == cases.Count && failed == 0 && sourceInvalid == 0;
        object report = new
        {
            schema = "graphreader.owned-affine-stroke-sampling.v1", safety_passed = safe,
            declared = cases.Count, attempted = results.Count, completed, failed, unattempted = cases.Count - results.Count,
            positive_controls = 12, disconnected_controls = 1, source_fixture_failures = sourceInvalid,
            positive_exact_frame_disconnections = exactFrameDisconnections, positive_mapped_planted_disconnections = mappedPlantedDisconnections,
            measured_disconnections_are_observations_not_acceptance_failures = true,
            renderer = "8-connected Bresenham centerline at nearest original pixel centers; width3 is one-pixel Chebyshev dilation",
            annotation_rounding = "AwayFromZero; continuous frame and planted-pixel offsets are reported separately",
            connectivity = "8-neighbor whole-owned-raster component at the named exact or rounded-mapped endpoints, all pixels<200",
            black_controls = "Prior11nominal-geometric black controls already used rectification and actual worker; not rerun and no pixel-parity claim with this digital renderer",
            timing_scope = "render/source proof/actual rectifier/owned observations; no worker latency",
            imaging_assembly_sha256 = Program.Hash(Program.ReadBounded(typeof(AffineRasterRectifier).Assembly.Location, 16_777_216)),
            cases = results, wall_seconds = total.Elapsed.TotalSeconds,
            memory = new { source_gray_bytes = sourceBytes, derived_gray_bytes = derivedBytes, cumulative_parent_allocated_bytes = GC.GetTotalAllocatedBytes() - allocated },
            budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores,
            private_reads = 0, worker_calls = 0, ocr_calls = 0, fit_calls = 0, images_written = false,
        };
        return new(report, safe);
    }

    private static StrokePixel StrokeRound(ImagePoint point) => new((int)Math.Round(point.X, MidpointRounding.AwayFromZero), (int)Math.Round(point.Y, MidpointRounding.AwayFromZero));
    private static object StrokeOffset(ImagePoint frame, ImagePoint planted) => new { dx = planted.X - frame.X, dy = planted.Y - frame.Y,
        distance = Math.Sqrt(Math.Pow(planted.X - frame.X, 2) + Math.Pow(planted.Y - frame.Y, 2)) };

    private static void StrokePaintLine(byte[] pixels, StrokePixel start, StrokePixel end, int width, byte gray, bool gap, CancellationToken token)
    {
        int x = start.X, y = start.Y, dx = Math.Abs(end.X - x), sx = x < end.X ? 1 : -1;
        int dy = -Math.Abs(end.Y - y), sy = y < end.Y ? 1 : -1, error = dx + dy, step = 0, radius = width / 2;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!gap || step is < 100 or > 108)
                for (int row = y - radius; row <= y + radius; row++) for (int column = x - radius; column <= x + radius; column++)
                    if (row >= 0 && column >= 0 && row < StrokeCanvasHeight && column < StrokeCanvasWidth) pixels[row * StrokeCanvasWidth + column] = gray;
            if (x == end.X && y == end.Y) break;
            int twice = 2 * error;
            if (twice >= dy) { error += dy; x += sx; }
            if (twice <= dx) { error += dx; y += sy; }
            step++;
        }
    }

    private static bool StrokeConnected(byte[] pixels, int width, int height, StrokePixel start, StrokePixel end, CancellationToken token)
    {
        bool Inside(StrokePixel point) => point.X >= 0 && point.Y >= 0 && point.X < width && point.Y < height;
        if (!Inside(start) || !Inside(end) || pixels[start.Y * width + start.X] >= 200 || pixels[end.Y * width + end.X] >= 200) return false;
        bool[] visited = new bool[pixels.Length]; var queue = new Queue<int>();
        int first = start.Y * width + start.X, target = end.Y * width + end.X; visited[first] = true; queue.Enqueue(first);
        while (queue.TryDequeue(out int current))
        {
            token.ThrowIfCancellationRequested(); if (current == target) return true;
            int x = current % width, y = current / width;
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                int next = ny * width + nx;
                if (!visited[next] && pixels[next] < 200) { visited[next] = true; queue.Enqueue(next); }
            }
        }
        return false;
    }

    private static object StrokeProfile(byte[] pixels, int width, StrokePixel start, StrokePixel end)
    {
        int count = Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y)), dark = 0, run = 0, longest = 0;
        for (int step = 0; step <= count; step++)
        {
            int x = start.X + Math.Sign(end.X - start.X) * step, y = start.Y + Math.Sign(end.Y - start.Y) * step;
            if (pixels[y * width + x] < 200) { dark++; run = 0; } else { run++; longest = Math.Max(longest, run); }
        }
        return new { samples = count + 1, dark_samples = dark, white_samples = count + 1 - dark, longest_white_run = longest };
    }
}
