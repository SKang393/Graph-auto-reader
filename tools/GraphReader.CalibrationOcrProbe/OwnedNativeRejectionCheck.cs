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

internal static class OwnedNativeRejectionCheck
{
    private static readonly string[] CaseNames = ["blank", "horizontal_only", "clean_plot", "bottom_touching_divider", "two_panels"];

    internal static async Task<object> RunAsync(string app, CpuBudgetJob budget, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(app) || !File.Exists(app))
            throw new ArgumentException("Absolute existing source App executable required.", nameof(app));
        if (budget.MaximumLogicalCores is <= 0 or > 0.5 || !budget.SilentBreakawayVerified)
            throw new InvalidOperationException("Independent parent CPU budget required.");
        string appHash = HashFile(app);
        string appDll = Path.ChangeExtension(app, ".dll");
        string appDllHash = HashFile(appDll);
        var service = new OpenCvAxisProposalService(app);
        var cases = new List<object>(CaseNames.Length);
        var violations = new List<string>();
        int geometryCount = 0, rejectedCount = 0, exceptionCount = 0, canceledCount = 0;
        var total = Stopwatch.StartNew();
        foreach (string name in CaseNames)
        {
            token.ThrowIfCancellationRequested();
            (int width, int height, byte[] pixels) = Fixture(name);
            string pixelsHash = Convert.ToHexStringLower(SHA256.HashData(pixels));
            byte[] encoded = Encode(width, height, pixels);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            RustAxisProposalResult? result = null;
            string? exceptionType = null;
            string terminal;
            var timer = Stopwatch.StartNew();
            try
            {
                result = await service.SuggestAsync(encoded, deadline.Token).ConfigureAwait(false);
                if (result.Geometry is null) { terminal = "rejected"; rejectedCount++; }
                else { terminal = "geometry_returned"; geometryCount++; }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                terminal = "case_cancelled";
                canceledCount++;
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                terminal = "exception";
                exceptionType = error.GetType().Name;
                exceptionCount++;
            }
            timer.Stop();
            if (result is null) violations.Add(name + ":" + terminal);
            else
            {
                if (result.Provider != "opencv-review") violations.Add(name + ":provider");
                if (name == "clean_plot")
                {
                    if (result.ErrorCode is not null || result.Geometry is not
                        { Left: 100, Bottom: 300, Right: 700, Top: 50 }) violations.Add(name + ":geometry");
                }
                else
                {
                    string expected = name switch
                    {
                        "blank" => "native_no_line_evidence",
                        "horizontal_only" => "native_missing_orientation",
                        _ => "native_multiple_ranked_candidates",
                    };
                    if (result.Geometry is not null || result.ErrorCode != "OPENCV_AXIS_AMBIGUOUS" ||
                        result.ReviewReasons.Count != 1 || result.ReviewReasons[0] != expected)
                        violations.Add(name + ":typed_rejection");
                }
                if (result.Geometry is { } geometry && (geometry.XTickPositions.Count != 0 ||
                    geometry.YTickPositions.Count != 0 || geometry.FirstTickX is not null || geometry.LastTickX is not null))
                    violations.Add(name + ":invented_ticks");
            }
            if (pixelsHash != Convert.ToHexStringLower(SHA256.HashData(pixels))) violations.Add(name + ":pixels_changed");
            cases.Add(new
            {
                name, width, height,
                pixels_sha256 = pixelsHash,
                encoded_sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded)),
                terminal, exception_type = exceptionType,
                milliseconds = timer.Elapsed.TotalMilliseconds,
                result,
            });
        }
        if (appHash != HashFile(app) || appDllHash != HashFile(appDll)) violations.Add("runtime_changed");
        return new
        {
            schema = "graphreader.owned-native-rejection-check.v1",
            scope = "owned_synthetic",
            actual_native_worker_calls = true,
            review_only = true,
            accuracy_claim = false,
            calibration_applied = false,
            planned_cases = CaseNames.Length,
            attempted_cases = cases.Count,
            terminal_cases = geometryCount + rejectedCount + exceptionCount + canceledCount,
            geometry_count = geometryCount, rejected_count = rejectedCount,
            exception_count = exceptionCount, cancelled_count = canceledCount,
            elapsed_milliseconds = total.Elapsed.TotalMilliseconds,
            budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores,
            budget.SilentBreakawayVerified,
            scheduling_note = "Parent and existing native workers independently cap CPU; no child scheduling measurement is added here.",
            app_sha256 = appHash, app_dll_sha256 = appDllHash,
            safety_passed = violations.Count == 0,
            violations, cases,
        };
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static (int Width, int Height, byte[] Pixels) Fixture(string name)
    {
        if (name == "two_panels") return OwnedGrayAxisCheck.Fixture(name);
        const int width = 800, height = 400;
        byte[] pixels = Enumerable.Repeat(byte.MaxValue, width * height).ToArray();
        if (name != "blank")
            for (int x = 100; x <= 700; x++) pixels[300 * width + x] = 0;
        if (name is "clean_plot" or "bottom_touching_divider")
            for (int y = 50; y <= 300; y++)
            {
                pixels[y * width + 100] = 0;
                if (name == "bottom_touching_divider") pixels[y * width + 400] = 0;
            }
        return (width, height, pixels);
    }

    private static byte[] Encode(int width, int height, byte[] pixels)
    {
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
