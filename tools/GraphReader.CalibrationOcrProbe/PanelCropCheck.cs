// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Services;
using GraphReader.Imaging;

namespace GraphReader.CalibrationOcrProbe;

internal static class PanelCropCheck
{
    internal static Task<object> RunAsync(string outputRoot, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(outputRoot) || !Directory.Exists(outputRoot) ||
            Directory.EnumerateFileSystemEntries(outputRoot).Any())
            throw new InvalidDataException("An existing absolute empty output root is required.");
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { completion.TrySetResult(await RunOnDispatcherAsync(outputRoot, cancellationToken)); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Panel crop diagnostic" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task<object> RunOnDispatcherAsync(string outputRoot, CancellationToken token)
    {
        const int width = 2560, height = 2200, cropX = 128, cropY = 64, cropSize = 2048;
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) pixels[y * width + x] = (byte)((x * 17 + y * 31) & 255);
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        string imagePath = Path.Combine(outputRoot, "owned-crop-source.png");
        using (var file = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(file);
        string originalHash = await HashAsync(imagePath, token);
        using var workspace = new ManualPreviewWorkspaceService();
        var parent = (await workspace.ImportImagesAsync([imagePath], token)).Single();
        PanelCropContext context = workspace.GetPanelCropContext(parent.TabId) ?? throw new InvalidDataException("Crop context unavailable.");
        var rect = new MarkerExemplarRectangle(cropX, cropY, cropSize, cropSize);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await workspace.CreatePanelCropAsync(context, rect, canceled.Token); throw new InvalidDataException("Pre-canceled crop returned a child."); }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        }
        if (workspace.CreateWorkspace().Count != 1) throw new InvalidDataException("Canceled crop changed the workspace.");
        var clock = Stopwatch.StartNew();
        TimeSpan previousTick = clock.Elapsed, maxGap = TimeSpan.Zero;
        int ticks = 0;
        var heartbeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        heartbeat.Tick += (_, _) => { TimeSpan now = clock.Elapsed; maxGap = Max(maxGap, now - previousTick); previousTick = now; ticks++; };
        heartbeat.Start();
        using Process process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var child = await workspace.CreatePanelCropAsync(context, rect, token);
        wall.Stop();
        TimeSpan cpu = process.TotalProcessorTime - cpuBefore;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        heartbeat.Stop();
        maxGap = Max(maxGap, clock.Elapsed - previousTick);
        if (child.PixelWidth != cropSize || child.PixelHeight != cropSize || child.Calibration is not null ||
            child.Points.Count != 0 || child.SeriesCards.Count != 0 || !workspace.IsPanelCropContextCurrent(context))
            throw new InvalidDataException("Crop did not preserve parent state and produce a blank child.");
        var childContext = workspace.GetPanelCropContext(child.TabId) ?? throw new InvalidDataException("Child context unavailable.");
        if (childContext.OriginalImageIdentity is not ImmutableImageBytes childBytes)
            throw new InvalidDataException("Child original bytes unavailable.");
        string childHash = Convert.ToHexStringLower(SHA256.HashData(childBytes.Copy()));
        if (!string.Equals(childHash, child.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Child image hash differs from its binding.");
        var cropped = (BitmapSource)child.ImageSource!;
        foreach ((int x, int y) in new[] { (0, 0), (37, 83), (1024, 1024), (2047, 2047) })
        {
            byte[] actual = new byte[1];
            cropped.CopyPixels(new Int32Rect(x, y, 1, 1), actual, 1, 0);
            if (actual[0] != pixels[(cropY + y) * width + cropX + x])
                throw new InvalidDataException("Crop pixel did not map to its original source coordinate.");
        }
        if (await HashAsync(imagePath, token) != originalHash) throw new InvalidDataException("Original file changed.");
        string project = Path.Combine(outputRoot, "owned-crop.garproj");
        if (!(await workspace.SaveProjectAsync(project, token)).IsSuccess) throw new InvalidDataException("Project save failed.");
        using var reopened = new ManualPreviewWorkspaceService();
        var tabs = await reopened.OpenProjectAsync(project, token);
        var restored = tabs.Single(item => item.TabId == child.TabId);
        if (tabs.Count != 2 || restored.PixelWidth != cropSize || restored.PixelHeight != cropSize ||
            restored.Points.Count != 0 || restored.SeriesCards.Count != 0 || restored.Calibration is not null ||
            !string.Equals(restored.SourceSha256, childHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Reopened crop does not match the blank child.");
        long outputBytes = Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        if (outputBytes > 64L * 1024 * 1024) throw new InvalidDataException("Diagnostic output exceeded 64 MiB.");
        return new { scope = "owned-synthetic-crop-performance", no_hard_cpu_cap = true,
            logical_processors = Environment.ProcessorCount, source_sha256 = originalHash, child_sha256 = childHash, crop = rect,
            crop_wall_milliseconds = wall.Elapsed.TotalMilliseconds, process_cpu_milliseconds = cpu.TotalMilliseconds,
            dispatcher_heartbeat_max_gap_milliseconds = maxGap.TotalMilliseconds, dispatcher_heartbeat_count = ticks,
            parent_unchanged = true, selected_pixels_match = true, child_blank = true, reopened_tabs = tabs.Count,
            cancellation_before_create_no_child = true, output_bytes = outputBytes };
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
    }
}
