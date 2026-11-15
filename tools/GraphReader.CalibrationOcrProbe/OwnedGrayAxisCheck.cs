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

internal static class OwnedGrayAxisCheck
{
    private static readonly int[] XTicks = [30, 70, 110];
    private static readonly int[] YTicks = [20, 50, 80];
    private sealed record Observation(RustAxisProposalResult? Result, string? ExceptionType, double Milliseconds);

    internal static async Task<object> RunAsync(string app, string rust, CpuBudgetJob budget, CancellationToken token,
        bool expectAmbiguousRecovery = false)
    {
        if (!Path.IsPathFullyQualified(app) || !File.Exists(app) || !Path.IsPathFullyQualified(rust) || !File.Exists(rust))
            throw new ArgumentException("Absolute existing executables required.");
        var native = new OpenCvAxisProposalService(app);
        var combined = new RustFirstAxisProposalService(new RustAxisProposalService(rust), native);
        var results = new List<object>();
        var violations = new List<string>();
        foreach (string name in new[] { "black_axes_marker", "gray_axes_marker", "faint_axes_no_ticks", "two_panels" })
        {
            token.ThrowIfCancellationRequested();
            var (width, height, pixels) = Fixture(name);
            byte[] encoded = Encode(width, height, pixels);
            Observation direct = await ObserveAsync(native, encoded, token).ConfigureAwait(false);
            Observation composed = await ObserveAsync(combined, encoded, token).ConfigureAwait(false);
            if (direct.ExceptionType is not null || composed.ExceptionType is not null) violations.Add(name + ":exception");
            if (name == "black_axes_marker" && composed.Result is not
                { Provider: "rust", Geometry: { Left: 20, Bottom: 100, Right: 115, Top: 10 } }) violations.Add(name + ":rust_geometry");
            if (name == "gray_axes_marker")
            {
                if (!expectAmbiguousRecovery && composed.Result is not
                    { Provider: "rust", Geometry: null, ErrorCode: "AMBIGUOUS_AXIS" }) violations.Add(name + ":rust_ambiguity");
                if (expectAmbiguousRecovery && (composed.Result is not
                    { Provider: "opencv-review", Geometry: not null, ErrorCode: null } ||
                    !composed.Result.ReviewReasons.Contains("rust_axis_ambiguous", StringComparer.Ordinal)))
                    violations.Add(name + ":native_review_recovery");
            }
            if (name == "two_panels" && (direct.Result?.Geometry is not null || composed.Result?.Geometry is not null ||
                composed.Result?.ErrorCode != "AMBIGUOUS_AXIS")) violations.Add(name + ":ambiguity");
            if (direct.Result?.Geometry is { } nativeGeometry &&
                (nativeGeometry.XTickPositions.Count != 0 || nativeGeometry.YTickPositions.Count != 0)) violations.Add(name + ":native_ticks");
            results.Add(new { name, width, height, encoded_sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded)), direct, composed });
        }
        return new { schema = "graphreader.owned-gray-axis-check.v1", scope = "owned_synthetic", expect_ambiguous_recovery = expectAmbiguousRecovery, review_only = true,
            accuracy_claim = false, native_tick_values_inferred = false, safety_passed = violations.Count == 0,
            violations, budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores,
            app_sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(app))),
            rust_sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(rust))), cases = results };
    }

    private static async Task<Observation> ObserveAsync(IAxisProposalService service, byte[] image, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        try { return new(await service.SuggestAsync(image, token).ConfigureAwait(false), null, timer.Elapsed.TotalMilliseconds); }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        { return new(null, error.GetType().Name, timer.Elapsed.TotalMilliseconds); }
    }

    internal static (int Width, int Height, byte[] Pixels) Fixture(string name)
    {
        if (name == "two_panels")
        {
            var original = OwnedAxisBackendCheck.Fixture(name);
            return (original.Width, original.Height, original.Pixels);
        }
        const int width = 128, height = 128;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        byte ink = name switch { "black_axes_marker" => 0, "gray_axes_marker" => 192, "faint_axes_no_ticks" => 128, _ => throw new ArgumentException("Unknown fixture.") };
        for (int x = 20; x <= 115; x++) pixels[100 * width + x] = ink;
        for (int y = 10; y <= 100; y++) pixels[y * width + 20] = ink;
        if (name != "faint_axes_no_ticks")
        {
            foreach (int x in XTicks) for (int y = 101; y <= 104; y++) pixels[y * width + x] = ink;
            foreach (int y in YTicks) for (int x = 16; x < 20; x++) pixels[y * width + x] = ink;
            for (int y = 58; y <= 62; y++) for (int x = 58; x <= 62; x++) pixels[y * width + x] = 0;
        }
        return (width, height, pixels);
    }

    private static byte[] Encode(int width, int height, byte[] pixels)
    {
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
