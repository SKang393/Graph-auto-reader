// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using GraphReader.Axis;
using OpenCvSharp;

namespace GraphReader.CalibrationOcrProbe;

internal static class OwnedAxisBackendCheck
{
    private sealed class Observer : IAxisGeometryCandidateObserver
    {
        internal int Count { get; private set; }
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) => Count = candidates.Count;
    }

    internal static async Task<object> RunAsync(string worker, CpuBudgetJob budget, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(worker) || !File.Exists(worker)) throw new ArgumentException("Absolute worker path required.");
        Cv2.SetNumThreads(1);
        var results = new List<object>();
        foreach (string name in new[] { "clean", "padded", "connected_corner", "empty_corner", "two_panels", "blank" })
        {
            token.ThrowIfCancellationRequested();
            (int width, int height, byte[] pixels, int offset) = Fixture(name);
            var frame = new GrayscaleLineCandidateFrame(width, height, width, pixels);
            var watch = Stopwatch.StartNew();
            RustAxisProposalResult rust = await RustAxisProposal.RunAsync(worker, width, height, pixels, cancellationToken: token);
            double rustMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            IReadOnlyList<GeometryLineCandidate> lines = await new OpenCvLineCandidateProvider().DetectLinesAsync(frame, token);
            var observer = new Observer();
            AxisGeometryResult? geometry = null;
            string? failure = null;
            try { geometry = await new AxisGeometryDetector().DetectAsync(new AxisGeometryRequest(width, height, lines) { CandidateObserver = observer }, token); }
            catch (InvalidOperationException) { failure = "geometry_unavailable"; }
            double cvMs = watch.Elapsed.TotalMilliseconds;
            double? cornerError = geometry is null || name is "blank" or "two_panels" ? null :
                Math.Sqrt(Math.Pow(geometry.PlotPolygon.BottomLeft.X - (30.5 + offset), 2) + Math.Pow(geometry.PlotPolygon.BottomLeft.Y - (130.5 + offset), 2));
            results.Add(new { name, width, height, image_sha256 = Convert.ToHexStringLower(SHA256.HashData(pixels)),
                rust, rust_ms = rustMs, line_candidates = lines.Count, ranked_pairs = observer.Count,
                opencv = geometry is null ? null : new { geometry.PlotPolygon, geometry.Confidence, geometry.Uncertainty, geometry.Diagnostics },
                opencv_failure = failure, opencv_ms = cvMs, corner_error_pixels = cornerError,
                note = "Geometry is a review proposal; ranked pairs and uncertainty are not uniqueness proof." });
        }
        return new { review_only = true, accuracy_claim = false, budget.LogicalProcessors, budget.Rate,
            budget.MaximumLogicalCores, opencv_threads = Cv2.GetNumThreads(), worker_sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(worker))), cases = results };
    }

    internal static (int Width, int Height, byte[] Pixels, int Offset) Fixture(string name)
    {
        int width = name == "padded" ? 768 : name == "two_panels" ? 512 : 256;
        int height = name == "padded" ? 768 : 160;
        int offset = name == "padded" ? 200 : 0;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        void Plot(int dx, int dy)
        {
            for (int x = 30; x <= 225; x++) for (int y = 130; y <= 131; y++) pixels[(y + dy) * width + x + dx] = 0;
            for (int y = 20; y <= 131; y++) for (int x = 30; x <= 31; x++) pixels[(y + dy) * width + x + dx] = 0;
        }
        if (name != "blank") Plot(offset, offset);
        if (name == "two_panels") Plot(256, 0);
        if (name is "connected_corner" or "empty_corner")
            for (int y = 114; y <= 146; y++) for (int x = 14; x <= 46; x++)
                if (Math.Pow(x - 30.5, 2) + Math.Pow(y - 130.5, 2) <= 225)
                    pixels[y * width + x] = name == "connected_corner" ? (byte)0 : (byte)255;
        return (width, height, pixels, offset);
    }
}
