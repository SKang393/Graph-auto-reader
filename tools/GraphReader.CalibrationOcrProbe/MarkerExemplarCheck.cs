// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;

namespace GraphReader.CalibrationOcrProbe;

internal static class MarkerExemplarCheck
{
    internal static async Task<object> RunAsync(string appExe, CancellationToken cancellationToken,
        bool nearPixelLimit = false, bool transparentBackground = false)
    {
        if (!Path.IsPathFullyQualified(appExe) || !File.Exists(appExe))
            throw new InvalidDataException("An existing absolute App executable is required.");
        int width = nearPixelLimit ? 2560 : 1280, height = nearPixelLimit ? 1600 : 800;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int x = 200; x <= 1080; x++) pixels[400 * width + x] = 0;
        foreach (int center in new[] { 320, 640, 960 })
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                    if (dx * dx + dy * dy <= 9) pixels[(400 + dy) * width + center + dx] = 0;
        for (int y = 247; y <= 253; y++)
            for (int x = 1037; x <= 1043; x++)
                if (x == 1037 || x == 1043 || y == 247 || y == 253) pixels[y * width + x] = 0;
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        if (transparentBackground)
        {
            byte[] bgra = new byte[checked(pixels.Length * 4)];
            for (int i = 0; i < pixels.Length; i++)
                bgra[i * 4 + 3] = pixels[i] == 0 ? (byte)255 : (byte)0;
            image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        byte[] bytes = stream.ToArray();
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var request = new MarkerExemplarRequest("owned-marker-check", hash, hash, hash, "owned-panel",
            width, height, new(314, 394, 13, 13), new(80, 80, width - 160, height - 160), "owned-seed", "owned-series", new(320, 400));
        var wall = Stopwatch.StartNew();
        await using var client = new MarkerExemplarWorkerClient(appExe);
        MarkerExemplarResponse response = await client.FindAsync(request, bytes, cancellationToken);
        wall.Stop();
        if (response.FailureCode is not null || response.Request != request || response.Runtime is null || response.Timings is null)
            throw new InvalidDataException(response.FailureCode ?? "Marker worker returned an invalid response.");
        var expected = new[] { new MarkerExemplarPoint(640, 400), new MarkerExemplarPoint(960, 400) };
        var errors = expected.Select(point => new { expected = point,
            nearest_error_pixels = response.Candidates.Count == 0 ? (double?)null : response.Candidates.Min(candidate =>
                Math.Sqrt(Math.Pow(candidate.Center.X - point.X, 2) + Math.Pow(candidate.Center.Y - point.Y, 2))) }).ToArray();

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<MarkerExemplarResponse> interrupted = client.FindAsync(
            request with { RequestId = "owned-marker-cancel" }, bytes, cancel.Token);
        var observation = Stopwatch.StartNew();
        int? workerId = null;
        while (observation.Elapsed < TimeSpan.FromSeconds(5))
        {
            workerId = client.ActiveWorkerId;
            if (workerId is not null) break;
            if (interrupted.IsCompleted)
                throw new InvalidDataException("Cancellation cannot be verified: request finished before its child was observed.");
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
        if (workerId is null) throw new InvalidDataException("Cancellation cannot be verified: no owned child was observed.");
        // Hold the actual process handle before cancellation, rather than trusting a reused PID or fast task completion.
        using Process ownedChild = Process.GetProcessById(workerId.Value);
        _ = ownedChild.Handle;
        if (ownedChild.HasExited || interrupted.IsCompleted)
            throw new InvalidDataException("Cancellation cannot be verified: the observed child had already finished.");
        using var exitDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        exitDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        var cancellationWall = Stopwatch.StartNew();
        cancel.Cancel();
        bool cancellationObserved = false;
        try { _ = await interrupted.WaitAsync(exitDeadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested && interrupted.IsCanceled) { cancellationObserved = true; }
        if (!cancellationObserved) throw new InvalidDataException("The marker request did not report cancellation.");
        await ownedChild.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
        cancellationWall.Stop();
        if (!ownedChild.HasExited || client.ActiveWorkerId is not null)
            throw new InvalidDataException("The canceled owned child remains active.");

        var restartWall = Stopwatch.StartNew();
        MarkerExemplarResponse restarted = await client.FindAsync(
            request with { RequestId = "owned-marker-restart" }, bytes, cancellationToken).ConfigureAwait(false);
        restartWall.Stop();
        if (restarted.FailureCode is not null || restarted.Request?.RequestId != "owned-marker-restart" ||
            restarted.Runtime is null || restarted.Timings is null)
            throw new InvalidDataException(restarted.FailureCode ?? "Restarted worker returned an invalid response.");
        return new { schema = "graphreader.marker-exemplar-owned-check.v1", review_only = true,
            production_approved = false, transparent_background = transparentBackground,
            input_sha256 = hash, request.Width, request.Height, request.Exemplar, request.Plot,
            seed = request.SelectedPoint, wall_milliseconds = wall.Elapsed.TotalMilliseconds,
            response.Timings, response.Runtime, candidate_count = response.Candidates.Count,
            response.Candidates, control_errors = errors,
            lifecycle = new { cancelled_owned_process_id = workerId.Value, cancellation_observed = cancellationObserved,
                cancelled_child_exited = ownedChild.HasExited, cancellation_milliseconds = cancellationWall.Elapsed.TotalMilliseconds,
                restart_wall_milliseconds = restartWall.Elapsed.TotalMilliseconds,
                restart_candidate_count = restarted.Candidates.Count, restart_timings = restarted.Timings,
                restart_runtime = restarted.Runtime },
            note = "Owned control execution only; suggestions were not applied and no accuracy claim is made." };
    }
}
