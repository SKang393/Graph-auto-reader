// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.MarkerExemplar;
using GraphReader.App.Services;

namespace GraphReader.CalibrationOcrProbe;

internal static class OwnedAffineMarkerWorkerCheck
{
    private static readonly (int X, int Y)[] Rings = [(110, 150), (170, 140), (220, 130), (52, 80)];
    private static readonly MarkerExemplarPoint[] Expected = [new(170.25, 140.5), new(220.25, 130.5)];
    internal sealed record Outcome(object Report, bool Passed);
    internal static async Task<Outcome> RunAsync(string app, CpuBudgetJob budget, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified || !Path.IsPathFullyQualified(app) || !File.Exists(app)) throw new InvalidDataException();
        string appDir = Path.GetDirectoryName(app)!, probeDir = Path.GetDirectoryName(typeof(OwnedAffineMarkerWorkerCheck).Assembly.Location)!;
        string[] libraries = RealDevCalibrationCheck.OwnedAffineRuntimeNames.ToArray();
        if (libraries.Length != 20) throw new InvalidDataException();
        foreach (string library in libraries)
            if (Program.Hash(File.ReadAllBytes(Path.Combine(appDir, library))) != Program.Hash(File.ReadAllBytes(Path.Combine(probeDir, library)))) throw new InvalidDataException();
        var loaded = new[] { typeof(MarkerExemplarRequest).Assembly, typeof(GraphReader.Axis.RustAxisGeometry).Assembly,
            typeof(GraphReader.Imaging.CanvasCoordinateMapper).Assembly, typeof(GraphReader.Ocr.OcrImage).Assembly };
        foreach (var assembly in loaded)
            if (Program.Hash(File.ReadAllBytes(assembly.Location)) != Program.Hash(File.ReadAllBytes(Path.Combine(appDir, assembly.GetName().Name + ".dll")))) throw new InvalidDataException();

        byte[] pixels = Enumerable.Repeat((byte)255, 320 * 240).ToArray();
        foreach (var (x, y) in Rings)
            for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++)
                if (dx * dx + dy * dy is >= 9 and <= 16) pixels[(y + dy) * 320 + x + dx] = 0;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(320, 240, 96, 96, PixelFormats.Gray8, null, pixels, 320)));
        using var stream = new MemoryStream(); encoder.Save(stream); byte[] image = stream.ToArray();
        string hash = Convert.ToHexStringLower(SHA256.HashData(image));
        double angle = 2 * Math.PI / 180;
        var affine = new MarkerExemplarAffinePlot(new(50, 200), new(50 + 220 * Math.Cos(angle), 200 + 220 * Math.Sin(angle)),
            new(50 + 150 * Math.Sin(angle), 200 - 150 * Math.Cos(angle)));
        if (!MarkerExemplarGeometry.TryCreateAffineDomain(affine, 320, 240, out var domain, out var roi)) throw new InvalidDataException();
        var outside = new MarkerExemplarPoint(52.25, 80.5);
        if (!MarkerExemplarGeometry.ContainsCenter(roi, outside) || domain.Contains(outside) || Expected.Any(point => !domain.Contains(point))) throw new InvalidDataException();
        var request = new MarkerExemplarRequest("owned-affine-marker", hash, hash, hash, "owned-panel", 320, 240,
            new(103, 144, 13, 13), roi, "owned-seed", "owned-series", new(110.25, 150.5)) { AffinePlot = affine };
        var stopwatch = Stopwatch.StartNew();
        await using var client = new MarkerExemplarWorkerClient(app);
        MarkerExemplarResponse response = await client.FindAsync(request, image, token);
        stopwatch.Stop();
        bool echo = response.Request == request, valid = MarkerExemplarValidation.ValidResponse(request, response);
        bool exact = response.Candidates.Count == 2 && Expected.All(point => response.Candidates.Count(candidate => candidate.Center == point) == 1);
        bool outsideExcluded = response.Candidates.All(candidate => candidate.Center != outside);
        bool unchanged = hash == Convert.ToHexStringLower(SHA256.HashData(image));
        bool passed = response.FailureCode is null && echo && valid && exact && outsideExcluded && unchanged;
        object report = new { schema = "graphreader.owned-affine-marker-worker-check.v1", status = passed ? "passed" : "failed",
            worker_calls = 1, private_reads = 0, image_writes = false, apply_called = false, export_called = false,
            app_exe_sha256 = Program.Hash(File.ReadAllBytes(app)), app_dll_sha256 = Program.Hash(File.ReadAllBytes(Path.Combine(appDir, "GraphReader.App.dll"))),
            loaded_assemblies = loaded.Select(assembly => new { name = assembly.GetName().Name, sha256 = Program.Hash(File.ReadAllBytes(assembly.Location)) }).ToArray(),
            runtime_libraries_checked = libraries.Length, input_sha256 = hash, input_bytes = image.Length, input_unchanged = unchanged,
            response.FailureCode, echo_valid = echo, response_valid = valid, expected_centers_exact = exact, outside_excluded = outsideExcluded,
            candidates = response.Candidates, response.Runtime, response.Timings, elapsed_milliseconds = stopwatch.Elapsed.TotalMilliseconds,
            budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores };
        return new(report, passed);
    }
}
