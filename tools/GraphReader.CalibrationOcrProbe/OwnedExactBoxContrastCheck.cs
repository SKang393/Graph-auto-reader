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

internal static class OwnedExactBoxContrastCheck
{
    private sealed record ContrastFixture(string Name, int Width, int Height, byte[] Pixels,
        int? Gray8First = null, int? Gray8Second = null, int[]? ExpectedPlotBoundsReportOnly = null);

    internal static async Task<object> RunAsync(string app, string rust, CpuBudgetJob budget, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(app) || !File.Exists(app) || !Path.IsPathFullyQualified(rust) || !File.Exists(rust))
            throw new ArgumentException("Absolute existing executables required.");
        var native = new OpenCvAxisProposalService(app);
        var measurement = new RustAxisTickMeasurementService(rust);
        var cases = new List<object>();
        foreach (ContrastFixture fixture in Fixtures())
        {
            token.ThrowIfCancellationRequested();
            var bitmap = BitmapSource.Create(fixture.Width, fixture.Height, 96, 96, PixelFormats.Gray8, null,
                fixture.Pixels, fixture.Width);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); byte[] encoded = stream.ToArray();
            var timer = Stopwatch.StartNew();
            RustAxisProposalResult? result = null; AxisTickMeasurementResult? ticks = null;
            string? nativeException = null, tickException = null; double? tickMilliseconds = null;
            try { result = await native.SuggestAsync(encoded, token).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { nativeException = e.GetType().Name; }
            double nativeMilliseconds = timer.Elapsed.TotalMilliseconds;
            object? properties = null;
            if (result?.Geometry is { } g)
            {
                properties = new { origin = fixture.Pixels[g.Bottom * fixture.Width + g.Left],
                    x_end = fixture.Pixels[g.Bottom * fixture.Width + g.Right],
                    y_end = fixture.Pixels[g.Top * fixture.Width + g.Left],
                    horizontal = Summary(Enumerable.Range(g.Left, g.Right - g.Left + 1)
                        .Select(x => fixture.Pixels[g.Bottom * fixture.Width + x])),
                    vertical = Summary(Enumerable.Range(g.Top, g.Bottom - g.Top + 1)
                        .Select(y => fixture.Pixels[y * fixture.Width + g.Left])) };
                timer.Restart();
                try { ticks = await measurement.MeasureAsync(encoded, g, token).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { tickException = e.GetType().Name; }
                tickMilliseconds = timer.Elapsed.TotalMilliseconds;
            }
            cases.Add(new { name = fixture.Name, width = fixture.Width, height = fixture.Height,
                image_sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded)), native = result,
                native_exception = nativeException, native_milliseconds = nativeMilliseconds, properties, measurement = ticks,
                measurement_exception = tickException, measurement_milliseconds = tickMilliseconds,
                measurement_attempted = result?.Geometry is not null,
                phase_gray8_values = fixture.Gray8First is { } first && fixture.Gray8Second is { } second
                    ? new[] { first, second } : null,
                expected_plot_bounds_report_only = fixture.ExpectedPlotBoundsReportOnly });
        }
        return new { schema = "graphreader.owned-exact-box-contrast.v1", scope = "owned_synthetic", no_ocr = true,
            budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores,
            app_sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(app))),
            rust_sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(rust))), cases };
    }

    private static object Summary(IEnumerable<byte> values)
    {
        byte[] samples = values.ToArray();
        return new { count = samples.Length, minimum = samples.Min(), maximum = samples.Max(), mean = samples.Average(x => (double)x),
            below_128 = samples.Count(x => x < 128) };
    }

    private static IEnumerable<ContrastFixture> Fixtures()
    {
        foreach (string name in new[] { "black_axes_marker", "gray_axes_marker", "faint_axes_no_ticks", "blank", "disconnected" })
        {
            var (width, height, pixels) = OwnedGrayAxisCheck.Fixture(
                name is "blank" or "disconnected" ? "black_axes_marker" : name);
            if (name == "blank") Array.Fill(pixels, (byte)255);
            if (name == "disconnected")
                for (int x = 18; x <= 22; x++)
                for (int y = 98; y <= 102; y++) pixels[y * width + x] = 255;
            yield return new(name, width, height, pixels);
        }

        const int phaseWidth = 640, phaseHeight = 480;
        foreach ((int first, int second) in new[] { (0, 255), (63, 191), (127, 127), (191, 63) })
        {
            byte[] pixels = Enumerable.Repeat((byte)255, phaseWidth * phaseHeight).ToArray();
            for (int x = 99; x <= 540; x++)
            {
                pixels[379 * phaseWidth + x] = (byte)first;
                pixels[380 * phaseWidth + x] = (byte)second;
            }
            for (int y = 80; y <= 380; y++)
            {
                int firstIndex = y * phaseWidth + 99, secondIndex = y * phaseWidth + 100;
                pixels[firstIndex] = Math.Min(pixels[firstIndex], (byte)first);
                pixels[secondIndex] = Math.Min(pixels[secondIndex], (byte)second);
            }
            foreach (int x in new[] { 140, 340, 540 })
            for (int y = 380; y <= 385; y++)
            {
                int firstIndex = y * phaseWidth + x, secondIndex = y * phaseWidth + x + 1;
                pixels[firstIndex] = Math.Min(pixels[firstIndex], (byte)first);
                pixels[secondIndex] = Math.Min(pixels[secondIndex], (byte)second);
            }
            foreach (int y in new[] { 100, 230, 360 })
            for (int x = 94; x <= 100; x++)
            {
                int firstIndex = y * phaseWidth + x, secondIndex = (y + 1) * phaseWidth + x;
                pixels[firstIndex] = Math.Min(pixels[firstIndex], (byte)first);
                pixels[secondIndex] = Math.Min(pixels[secondIndex], (byte)second);
            }
            yield return new($"antialiased_phase_{first}_{second}", phaseWidth, phaseHeight, pixels,
                first, second, [99, 380, 540, 80]);
        }
    }
}
