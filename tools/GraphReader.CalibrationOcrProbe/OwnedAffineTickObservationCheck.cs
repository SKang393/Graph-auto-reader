// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal static IReadOnlyDictionary<string, string> OwnedAffineObservationPins => AffineCalibrationRuntimeLibraries;
}

internal static class OwnedAffineTickObservationCheck
{
    private sealed record Case(string Name, int Stroke, double Angle, double Shear, double Phase);
    private readonly record struct Point(double X, double Y);
    private static readonly Case[] Cases = [new("identity_3", 3, 0, 0, 0),
        new("positive_3_integer", 3, 2, 0, 0), new("positive_3_half", 3, 2, 0, .5),
        new("negative_3_integer", 3, -2, 0, 0), new("negative_3_half", 3, -2, 0, .5),
        new("shear_3_integer", 3, 0, .035, 0), new("shear_3_half", 3, 0, .035, .5),
        new("identity_1", 1, 0, 0, 0), new("identity_2", 2, 0, 0, 0),
        new("positive_1_half", 1, 2, 0, .5), new("positive_2_half", 2, 2, 0, .5)];
    private static readonly int[] XTicks = [100, 160, 220];
    private static readonly int[] YTicks = [75, 115, 155];
    private const int Width = 320, Height = 240;

    internal static object SelfTest()
    {
        if (Cases.Length != 11 || Cases.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != 11 ||
            Cases.Count(item => item.Stroke == 3 && Math.Abs(item.Angle) == 2) != 4 ||
            Cases.Count(item => item.Stroke == 3 && item.Shear != 0) != 2) throw new InvalidDataException();
        byte[] pixels = Render(Cases[0]);
        if (pixels[185 * Width + 55] != 0 || pixels[193 * Width + 100] != 0 || pixels[75 * Width + 47] != 0 || pixels[20 * Width + 20] != 255) throw new InvalidDataException();
        return new { schema = "graphreader.owned-affine-tick-observation-self-test.v1", declared_cases = Cases.Length,
            fixture_geometry_checked = true, private_reads = 0, worker_calls = 0 };
    }

    internal static async Task<object> RunAsync(string app, string rust, CpuBudgetJob budget, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified || !Path.IsPathFullyQualified(app) || !Path.IsPathFullyQualified(rust) ||
            Program.Hash(File.ReadAllBytes(app)) != "9c3057f6598b0e5d963719119c9f45b5372f67e2806f82d98debefd8f3ef97a6" ||
            Program.Hash(File.ReadAllBytes(rust)) != "2cc6f263a00544243291f2605031c4df7e147175bd345480bd0a44f92fde0b7a") throw new InvalidDataException();
        string sourceDir = Path.GetDirectoryName(app)!, probeDir = Path.GetDirectoryName(typeof(OwnedAffineTickObservationCheck).Assembly.Location)!;
        var pins = RealDevCalibrationCheck.OwnedAffineObservationPins;
        if (pins.Count != 20) throw new InvalidDataException();
        foreach (var (name, expected) in pins)
            if (Program.Hash(File.ReadAllBytes(Path.Combine(sourceDir, name))) != expected || Program.Hash(File.ReadAllBytes(Path.Combine(probeDir, name))) != expected) throw new InvalidDataException();
        var results = new List<object>();
        foreach (Case item in Cases)
        {
            if (token.IsCancellationRequested) break;
            string stage = "render"; var timer = Stopwatch.StartNew();
            try
            {
                byte[] pixels = Render(item); byte[] original = Encode(pixels, Width, Height);
                string hash = Convert.ToHexStringLower(SHA256.HashData(original));
                Point origin = Transform(item, new(55, 185)), xEnd = Transform(item, new(265, 185)), yEnd = Transform(item, new(55, 45));
                var affine = new RustAffineAxisGeometry(new(origin.X, origin.Y), new(xEnd.X, xEnd.Y), new(yEnd.X, yEnd.Y), 0);
                object source = Observe(pixels, Width, Height, origin, xEnd, yEnd);
                stage = "rectification";
                var (frame, derived, _) = CalibrationWorkingFrame.Create(original, Width, Height, affine, hash, token);
                byte[] gray = Decode(derived, frame.Width, frame.Height);
                var box = frame.Geometry;
                object rectified = Observe(gray, frame.Width, frame.Height, new(box.Left, box.Bottom), new(box.Right, box.Bottom), new(box.Left, box.Top));
                var expectedX = XTicks.Select(x => { Point p = Transform(item, new(x, 185)); return frame.ToDerived(new(p.X, p.Y)).X; }).ToArray();
                var expectedY = YTicks.Select(y => { Point p = Transform(item, new(55, y)); return frame.ToDerived(new(p.X, p.Y)).Y; }).ToArray();
                stage = "tick_measurement";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
                AxisTickMeasurementResult measured = await new RustAxisTickMeasurementService(rust).MeasureAsync(derived, box, deadline.Token).ConfigureAwait(false);
                results.Add(new { item.Name, nominal_geometric_stroke_width = item.Stroke, item.Angle, item.Shear, item.Phase, status = measured.ErrorCode is null ? "returned" : "refused",
                    error_code = measured.ErrorCode, source_sha256 = hash, original_unchanged = hash == Convert.ToHexStringLower(SHA256.HashData(original)),
                    source, rectified, expected_x = expectedX, expected_y = expectedY,
                    observed_x = measured.Evidence?.XTickPositions, observed_y = measured.Evidence?.YTickPositions,
                    x_count = CountEvidence(measured.Evidence?.XTickPositions, expectedX), y_count = CountEvidence(measured.Evidence?.YTickPositions, expectedY),
                    elapsed_milliseconds = timer.Elapsed.TotalMilliseconds });
            }
            catch (OperationCanceledException) { results.Add(new { item.Name, status = token.IsCancellationRequested ? "cancelled" : "timed_out", stage, elapsed_milliseconds = timer.Elapsed.TotalMilliseconds }); if (token.IsCancellationRequested) break; }
            catch (Exception error) when (error is not OutOfMemoryException) { results.Add(new { item.Name, status = "failed", stage, category = error.GetType().Name, elapsed_milliseconds = timer.Elapsed.TotalMilliseconds }); }
        }
        return new { schema = "graphreader.owned-affine-tick-observation.v1", stroke_width_semantics = "nominal geometric distance diameter; effective raster coverage varies with phase and orientation", declared = Cases.Length, attempted = results.Count,
            unattempted = Cases.Length - results.Count, cases = results, private_reads = 0, ocr_calls = 0, model_calls = 0,
            images_written = false, budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores };
    }

    private static object CountEvidence(IReadOnlyList<int>? observed, double[] expected)
    {
        if (observed is null) return new { expected_count = expected.Length, observed_count = (int?)null, count_shortfall = (int?)null, count_excess = (int?)null, nearest_residuals = (double[]?)null };
        return new { expected_count = expected.Length, observed_count = (int?)observed.Count,
            count_shortfall = (int?)Math.Max(0, expected.Length - observed.Count), count_excess = (int?)Math.Max(0, observed.Count - expected.Length),
            nearest_residuals = observed.Count == 0 ? null : expected.Select(value => observed.Min(tick => Math.Abs(tick - value))).ToArray() };
    }

    private static Point Transform(Case item, Point p)
    {
        double x = p.X - 160 + item.Shear * (p.Y - 120), y = p.Y - 120;
        double angle = item.Angle * Math.PI / 180, c = Math.Cos(angle), s = Math.Sin(angle);
        return new(160 + x * c - y * s + item.Phase, 120 + x * s + y * c + item.Phase);
    }

    private static byte[] Render(Case item)
    {
        var segments = new List<(Point A, Point B)> { (new(55, 185), new(265, 185)), (new(55, 185), new(55, 45)) };
        segments.AddRange(XTicks.Select(x => (new Point(x, 185), new Point(x, 193))));
        segments.AddRange(YTicks.Select(y => (new Point(55, y), new Point(47, y))));
        var transformed = segments.Select(s => (A: Transform(item, s.A), B: Transform(item, s.B))).ToArray();
        byte[] pixels = Enumerable.Repeat((byte)255, Width * Height).ToArray();
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            if (transformed.Any(s => Distance(x, y, s.A, s.B) <= item.Stroke / 2d)) pixels[y * Width + x] = 0;
        return pixels;
    }

    private static double Distance(double x, double y, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        return Math.Sqrt(Math.Pow(x - a.X - t * dx, 2) + Math.Pow(y - a.Y - t * dy, 2));
    }

    private static object Observe(byte[] pixels, int width, int height, Point origin, Point xEnd, Point yEnd) => new {
        origin_gray = Sample(pixels, width, height, origin), x_end_gray = Sample(pixels, width, height, xEnd), y_end_gray = Sample(pixels, width, height, yEnd),
        horizontal = Span(pixels, width, height, origin, xEnd), vertical = Span(pixels, width, height, origin, yEnd) };
    private static byte Sample(byte[] pixels, int width, int height, Point point)
    {
        int x = (int)Math.Round(point.X), y = (int)Math.Round(point.Y);
        if (x < 0 || x >= width || y < 0 || y >= height) throw new InvalidDataException();
        return pixels[y * width + x];
    }
    private static object Span(byte[] pixels, int width, int height, Point a, Point b)
    {
        int steps = (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y))), dark = 0, gap = 0, longest = 0;
        for (int i = 0; i <= steps; i++) { double t = (double)i / steps; bool ink = Sample(pixels, width, height, new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t)) < 200; if (ink) { dark++; gap = 0; } else { gap++; longest = Math.Max(longest, gap); } }
        return new { samples = steps + 1, dark, longest_white_run = longest };
    }
    private static byte[] Encode(byte[] pixels, int width, int height)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width)));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static byte[] Decode(byte[] image, int width, int height)
    {
        using var stream = new MemoryStream(image, writable: false); var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0); byte[] pixels = new byte[checked(width * height)]; gray.CopyPixels(pixels, width, 0); return pixels;
    }
}
