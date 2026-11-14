// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;

namespace GraphReader.CalibrationOcrProbe;

internal static class MarkerShapeControlCheck
{
    private static readonly int[] XPositions = [100, 200, 300, 400, 500];
    private static readonly int[] HorizontalY = [200, 200, 200, 200, 200];
    private static readonly int[] SlopedY = [200, 170, 220, 160, 210];

    internal static async Task<object> RunAsync(string appExe, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(appExe) || !File.Exists(appExe))
            throw new InvalidDataException("An existing absolute App executable is required.");
        await using var worker = new MarkerExemplarWorkerClient(appExe);
        object horizontal = await RunControlAsync(worker, "horizontal", HorizontalY, cancellationToken);
        object changingSlope = await RunControlAsync(worker, "changing-slope", SlopedY, cancellationToken);
        object hollow = await RunControlAsync(worker, "hollow-changing-slope", SlopedY, cancellationToken);
        object markerFree = await RunControlAsync(worker, "marker-free", HorizontalY, cancellationToken);
        return new { review_only = true, production_approved = false, horizontal, changing_slope = changingSlope,
            hollow_changing_slope = hollow, marker_free = markerFree,
            note = "Owned controls only; similarity is not probability, proposals are not applied, and no accuracy claim is made." };
    }

    private static async Task<object> RunControlAsync(MarkerExemplarWorkerClient worker, string name,
        int[] ys, CancellationToken cancellationToken)
    {
        const int width = 640, height = 400;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        bool markerFree = name == "marker-free", hollow = name == "hollow-changing-slope";
        if (markerFree)
        {
            DrawLine(pixels, width, 180, 120, 400, 120);
            DrawLine(pixels, width, 250, 80, 250, 160);
            DrawLine(pixels, width, 400, 120, 400, 190);
            DrawLine(pixels, width, 350, 260, 520, 310);
        }
        else
        {
            for (int index = 0; index < XPositions.Length - 1; index++)
                DrawLine(pixels, width, XPositions[index], ys[index], XPositions[index + 1], ys[index + 1]);
        }
        for (int index = 0; index < (markerFree ? 1 : XPositions.Length); index++)
        {
            if (hollow)
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                        if (dx * dx + dy * dy <= 9) pixels[(ys[index] + dy) * width + XPositions[index] + dx] = 255;
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                    if (dx * dx + dy * dy <= 9 && (!hollow || dx * dx + dy * dy >= 4))
                        pixels[(ys[index] + dy) * width + XPositions[index] + dx] = 0;
        }
        if (hollow)
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                    if (dx * dx + dy * dy <= 9) pixels[(100 + dy) * width + 550 + dx] = 0;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        byte[] image = stream.ToArray();
        string hash = Convert.ToHexStringLower(SHA256.HashData(image));
        var request = new MarkerExemplarRequest("shape-control-" + name, hash, hash, hash, "owned-panel",
            width, height, new(XPositions[0] - 6, ys[0] - 6, 13, 13), new(50, 50, 540, 300),
            "owned-seed", "owned-series", new(XPositions[0], ys[0]));
        var wall = Stopwatch.StartNew();
        MarkerExemplarResponse response = await worker.FindAsync(request, image, cancellationToken);
        wall.Stop();
        if (response.FailureCode is not null || response.Request != request || response.Runtime is null || response.Timings is null)
            return new { control = name, input_sha256 = hash, failure = response.FailureCode ?? "INVALID_RESPONSE",
                wall_milliseconds = wall.Elapsed.TotalMilliseconds };
        MarkerExemplarPoint[] expected = Enumerable.Range(1, markerFree ? 0 : XPositions.Length - 1)
            .Select(index => new MarkerExemplarPoint(XPositions[index], ys[index])).ToArray();
        bool Nearby(MarkerExemplarPoint a, MarkerExemplarPoint b) =>
            Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)) <= 1;
        int matched = expected.Count(point => response.Candidates.Any(candidate => Nearby(point, candidate.Center)));
        int falseProposals = response.Candidates.Count(candidate => !expected.Any(point => Nearby(point, candidate.Center)));
        return new { control = name, input_sha256 = hash, expected_count = expected.Length,
            matched_within_one_pixel = matched, missed_count = expected.Length - matched,
            false_proposals = falseProposals, proposal_count = response.Candidates.Count,
            response.Candidates, response.Timings, response.Runtime, wall_milliseconds = wall.Elapsed.TotalMilliseconds };
    }

    private static void DrawLine(byte[] pixels, int width, int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        while (true)
        {
            pixels[y0 * width + x0] = 0;
            if (x0 == x1 && y0 == y1) break;
            int twice = 2 * error;
            if (twice >= dy) { error += dy; x0 += sx; }
            if (twice <= dx) { error += dx; y0 += sy; }
        }
    }
}
