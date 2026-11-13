// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;

namespace GraphReader.CalibrationOcrProbe;

internal static class SelfHostedWorkerCheck
{
    internal static async Task<object> RunAsync(Program.ProbeRequest source, string requestHash,
        string executablePath, string assetRoot, CancellationToken cancellationToken)
    {
        if (source.Scope != "owned-synthetic-review-only" || source.SourceCropXywh is not { Length: 4 } crop ||
            source.PlotLtrb is not { Length: 4 } plot || !Path.IsPathFullyQualified(source.ImagePath) ||
            !Path.IsPathFullyQualified(executablePath) || !Path.IsPathFullyQualified(assetRoot) ||
            !File.Exists(executablePath) || !Directory.Exists(assetRoot))
            throw new InvalidDataException("Owned request or absolute worker paths are invalid.");
        byte[] original = Program.ReadBounded(source.ImagePath, 64 * 1024 * 1024);
        Program.RequireHash(original, source.ImageSha256);
        byte[] panel;
        using (var input = new MemoryStream(original, writable: false))
        {
            BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) throw new InvalidDataException("Expected one original image frame.");
            BitmapFrame frame = decoder.Frames[0];
            if (frame.PixelWidth > 4096 || frame.PixelHeight > 4096 || crop[0] < 0 || crop[1] < 0 ||
                crop[2] is < 32 or > 4096 || crop[3] is < 32 or > 4096 ||
                (long)crop[0] + crop[2] > frame.PixelWidth || (long)crop[1] + crop[3] > frame.PixelHeight ||
                plot.Any(value => !double.IsFinite(value)) || plot[0] < 0 || plot[1] < 0 ||
                plot[2] >= crop[2] || plot[3] >= crop[3] || plot[0] >= plot[2] || plot[1] >= plot[3])
                throw new InvalidDataException("Owned image crop or plot is outside its source.");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(frame, new Int32Rect(crop[0], crop[1], crop[2], crop[3]))));
            using var encoded = new MemoryStream();
            encoder.Save(encoded);
            panel = encoded.ToArray();
        }
        var request = new CalibrationOcrRequest(Guid.NewGuid().ToString("N"), requestHash, source.ImageSha256,
            Program.Hash(panel), crop[2], crop[3], new(plot[0], plot[1], plot[2], plot[3]));
        string fullExe = Path.GetFullPath(executablePath);
        HashSet<int> baseline = ProcessIds(fullExe);
        var client = new CalibrationOcrWorkerClient(fullExe, Path.GetFullPath(assetRoot));
        CalibrationOcrResponse cold, warm, restarted;
        int coldPid = 0, restartPid = 0;
        bool cancelled = false;
        Process? coldProcess = null, restartProcess = null;
        try
        {
            cold = await client.SuggestAsync(request, panel, cancellationToken);
            RequireSuccess(cold, request, false);
            coldPid = FindOnlyNewProcess(fullExe, baseline);
            coldProcess = Process.GetProcessById(coldPid);
            CalibrationOcrRequest warmRequest = request with { RequestId = Guid.NewGuid().ToString("N") };
            warm = await client.SuggestAsync(warmRequest, panel, cancellationToken);
            RequireSuccess(warm, warmRequest, true);
            if (FindOnlyNewProcess(fullExe, baseline) != coldPid) throw new InvalidDataException("Warm request did not reuse the child.");
            using (var cancelledRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cancelledRequest.CancelAfter(TimeSpan.FromMilliseconds(50));
                try { _ = await client.SuggestAsync(request with { RequestId = Guid.NewGuid().ToString("N") }, panel, cancelledRequest.Token); }
                catch (OperationCanceledException) when (cancelledRequest.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { cancelled = true; }
            }
            if (!cancelled) throw new InvalidDataException("Third request completed before cancellation; cancellation was not verified.");
            await WaitForExitAsync(coldProcess, cancellationToken);
            CalibrationOcrRequest restartedRequest = request with { RequestId = Guid.NewGuid().ToString("N") };
            restarted = await client.SuggestAsync(restartedRequest, panel, cancellationToken);
            RequireSuccess(restarted, restartedRequest, false);
            restartPid = FindOnlyNewProcess(fullExe, baseline);
            if (restartPid == coldPid) throw new InvalidDataException("Restart did not create a distinct child process.");
            restartProcess = Process.GetProcessById(restartPid);
        }
        finally { await client.DisposeAsync(); }
        try
        {
            await WaitForExitAsync(restartProcess!, cancellationToken);
            return new
            {
                schema = "graphreader.calibration-ocr-client-lifecycle.v1", review_only = true, production_approved = false,
                request_sha256 = requestHash, source_sha256 = source.ImageSha256, panel_sha256 = request.PanelImageSha256,
                source_crop_xywh = crop, plot_ltrb_in_crop = plot, coordinate_space = "original_panel_pixels",
                cold_pid = coldPid, warm_pid = coldPid, cancelled_pid = coldPid, restart_pid = restartPid,
                cancelled, cancelled_process_exited = coldProcess!.HasExited, disposed_process_exited = restartProcess!.HasExited,
                cold = Summarize(cold), warm = Summarize(warm), restart = Summarize(restarted),
                note = "Observed labels are diagnostic suggestions requiring review; no accuracy or approval claim."
            };
        }
        finally { coldProcess?.Dispose(); restartProcess?.Dispose(); }
    }

    private static void RequireSuccess(CalibrationOcrResponse response, CalibrationOcrRequest request, bool reused)
    {
        if (response.FailureCode is not null || response.Request != request || response.Timings is null ||
            response.Timings.ReusedSession != reused || response.Runtime is null ||
            response.Runtime.ModelSha256 != Program.ModelHash || response.Runtime.ManifestSha256 != Program.ManifestHash ||
            response.Runtime.Provider != "Cpu" || response.Runtime.SessionCreationCount != 1 ||
            !response.Runtime.CpuBudget.Verified || response.Runtime.CpuBudget.MaximumLogicalCores > .5)
            throw new InvalidDataException("Worker response failed the pinned identity, budget or session check.");
    }

    private static object Summarize(CalibrationOcrResponse response) => new
    {
        response.Timings, memory = response.Runtime!.Memory, cpu_budget = response.Runtime.CpuBudget,
        response.Runtime.SessionCreationCount, candidate_count = response.Proposals.Count,
        rows = response.Proposals.Select(row => new { row.Text, row.Role, row.SuggestedAxis, row.FailureCode, row.RequiresReview }).ToArray(),
        response.XSequence, response.YSequence,
    };

    private static HashSet<int> ProcessIds(string fullExe)
    {
        var result = new HashSet<int>();
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fullExe)))
        {
            using (process)
            {
                try
                {
                    string? file = process.MainModule?.FileName;
                    if (file is not null && string.Equals(Path.GetFullPath(file), fullExe, StringComparison.OrdinalIgnoreCase)) result.Add(process.Id);
                }
                catch (InvalidOperationException) { /* Process exited during enumeration. */ }
            }
        }
        return result;
    }

    private static int FindOnlyNewProcess(string fullExe, HashSet<int> baseline)
    {
        int[] found = ProcessIds(fullExe).Where(pid => !baseline.Contains(pid)).ToArray();
        if (found.Length != 1) throw new InvalidDataException("Could not uniquely identify the owned child process.");
        return found[0];
    }

    private static async Task WaitForExitAsync(Process process, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(deadline.Token);
        if (!process.HasExited) throw new InvalidDataException("Owned worker did not exit.");
    }
}
