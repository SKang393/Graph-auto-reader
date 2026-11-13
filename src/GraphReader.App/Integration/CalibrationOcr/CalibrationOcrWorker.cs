// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;

namespace GraphReader.App.Integration.CalibrationOcr;

internal static class CalibrationOcrWorker
{
    internal static async Task<int> RunAsync(string assetRoot, WorkflowRuntimeEnvironment environment,
        Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        if (environment != WorkflowRuntimeEnvironment.ManualPreview ||
            string.IsNullOrWhiteSpace(assetRoot) || !Path.IsPathFullyQualified(assetRoot) || !Directory.Exists(assetRoot))
        {
            await CalibrationOcrWire.WriteResponseAsync(output,
                CalibrationOcrResponse.Failure(null, CalibrationOcrReasonCodes.Unavailable), cancellationToken).ConfigureAwait(false);
            return 2;
        }

        CpuBudgetJob job;
        try { job = CpuBudgetJob.Apply(); }
        catch (Exception)
        {
            await CalibrationOcrWire.WriteResponseAsync(output,
                CalibrationOcrResponse.Failure(null, CalibrationOcrReasonCodes.ResourceUnavailable), cancellationToken).ConfigureAwait(false);
            return 2;
        }

        using (job)
        {
            // The reader factory is lazy: no OCR or ONNX initialization precedes the verified Job.
            return await RunLoopAsync(input, output, new CalibrationOcrSession(assetRoot, job.Budget), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<int> RunLoopAsync(Stream input, Stream output, CalibrationOcrSession session,
        CancellationToken cancellationToken)
    {
        await using (session.ConfigureAwait(false))
        {
            while (true)
            {
                CalibrationOcrWorkItem? item = null;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Idle workers retain the session. The deadline starts on the first request byte.
                using var watchdog = new Timer(_ => Environment.Exit(3), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                try
                {
                    item = await CalibrationOcrWire.ReadRequestAsync(input, deadline.Token, () =>
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(170));
                        watchdog.Change(TimeSpan.FromSeconds(175), Timeout.InfiniteTimeSpan);
                    }).ConfigureAwait(false);
                    if (item is null) return 0;
                    CalibrationOcrResponse response;
                    try { response = await session.RecognizeAsync(item, deadline.Token).ConfigureAwait(false); }
                    catch (CalibrationOcrException error) when (error.Reason is
                        CalibrationOcrReasonCodes.InvalidInput or CalibrationOcrReasonCodes.TooManyCandidates)
                    {
                        response = CalibrationOcrResponse.Failure(item.Request, error.Reason);
                    }
                    await CalibrationOcrWire.WriteResponseAsync(output, response, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    string reason = cancellationToken.IsCancellationRequested
                        ? CalibrationOcrReasonCodes.Cancelled : CalibrationOcrReasonCodes.Timeout;
                    await FailureAsync(output, item?.Request, reason).ConfigureAwait(false);
                    return reason == CalibrationOcrReasonCodes.Timeout ? 3 : 1;
                }
                catch (Exception error)
                {
                    string reason = error switch
                    {
                        CalibrationOcrException known => known.Reason,
                        JsonException or EndOfStreamException or InvalidDataException => CalibrationOcrReasonCodes.InvalidFrame,
                        FileNotFoundException or DirectoryNotFoundException => CalibrationOcrReasonCodes.Unavailable,
                        _ => CalibrationOcrReasonCodes.RecognitionFailed,
                    };
                    await FailureAsync(output, item?.Request, reason).ConfigureAwait(false);
                    return 1;
                }
            }
        }
    }

    private static async Task FailureAsync(Stream output, CalibrationOcrRequest? request, string reason)
    {
        try
        {
            await CalibrationOcrWire.WriteResponseAsync(output, CalibrationOcrResponse.Failure(request, reason),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException) { } // Parent closed its pipe; the process can release its session and exit.
    }
}
