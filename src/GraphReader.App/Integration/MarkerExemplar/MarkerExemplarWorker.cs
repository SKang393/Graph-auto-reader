// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.CalibrationOcr;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;

namespace GraphReader.App.Integration.MarkerExemplar;

internal static class MarkerExemplarWorker
{
    internal static async Task<int> RunAsync(WorkflowRuntimeEnvironment environment, Stream input, Stream output,
        CancellationToken cancellationToken = default)
    {
        MarkerExemplarRequest? request = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var watchdog = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(12), Timeout.InfiniteTimeSpan);
        try
        {
            if (environment != WorkflowRuntimeEnvironment.ManualPreview)
                throw new MarkerExemplarException(MarkerExemplarReasonCodes.Unavailable);
            CpuBudgetJob job;
            try { job = CpuBudgetJob.Apply(); }
            catch (Exception) { throw new MarkerExemplarException(MarkerExemplarReasonCodes.ResourceUnavailable); }
            using var jobOwner = job;
            var item = await MarkerExemplarWire.ReadRequestAsync(input, deadline.Token).ConfigureAwait(false);
            request = item.Request;
            if (!MarkerExemplarValidation.ValidRequest(request, item.Image))
                throw new MarkerExemplarException(MarkerExemplarReasonCodes.InvalidInput);
            using var matchingWatchdog = new Timer(_ => Environment.Exit(3), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            MarkerExemplarResponse response = Process(request, item.Image, job.Budget, deadline.Token,
                () => matchingWatchdog.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan));
            matchingWatchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            await MarkerExemplarWire.WriteResponseAsync(output, response, deadline.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error)
        {
            string reason = error switch
            {
                MarkerExemplarException known => known.Reason,
                OperationCanceledException => MarkerExemplarReasonCodes.Timeout,
                JsonException or InvalidDataException or EndOfStreamException => MarkerExemplarReasonCodes.InvalidFrame,
                System.ComponentModel.Win32Exception or PlatformNotSupportedException => MarkerExemplarReasonCodes.ResourceUnavailable,
                _ => MarkerExemplarReasonCodes.MatchingFailed,
            };
            using var writeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await MarkerExemplarWire.WriteResponseAsync(output, MarkerExemplarResponse.Failure(request, reason), writeDeadline.Token).ConfigureAwait(false); }
            catch (Exception failure) when (failure is IOException or OperationCanceledException or ObjectDisposedException) { }
            return 2;
        }
    }

    internal static MarkerExemplarResponse Process(MarkerExemplarRequest request, byte[] encoded,
        CalibrationOcrCpuBudget budget, CancellationToken token, Action? matchingStarted = null)
    {
        var total = Stopwatch.StartNew();
        if (!MarkerExemplarValidation.ValidRequest(request, encoded))
            throw new MarkerExemplarException(MarkerExemplarReasonCodes.InvalidInput);
        token.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(encoded, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        BitmapFrame frame = decoder.Frames[0];
        // Read dimensions before conversion or pixel allocation.
        if (frame.PixelWidth != request.Width || frame.PixelHeight != request.Height)
            throw new MarkerExemplarException(MarkerExemplarReasonCodes.InvalidInput);
        var gray = new FormatConvertedBitmap(WhiteBackgroundRaster.Normalize(frame, token), PixelFormats.Gray8, null, 0);
        byte[] pixels = new byte[checked(request.Width * request.Height)];
        gray.CopyPixels(pixels, request.Width, 0);
        double decode = total.Elapsed.TotalMilliseconds;
        token.ThrowIfCancellationRequested();
        var matching = Stopwatch.StartNew();
        // Includes native library loading and all correlation/postprocessing, independent of startup/decode budget.
        matchingStarted?.Invoke();
        IReadOnlyList<MarkerExemplarCandidate> candidates = MarkerExemplarMatcher.Find(request, pixels, token);
        matching.Stop();
        return new(request, candidates, new(decode, matching.Elapsed.TotalMilliseconds, total.Elapsed.TotalMilliseconds),
            new(budget, OpenCvSharp.Cv2.GetNumThreads(), MarkerExemplarValidation.Algorithm), null);
    }
}
