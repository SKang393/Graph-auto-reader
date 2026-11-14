// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GraphReader.App.Integration.MarkerExemplar;

namespace GraphReader.App.Services;

/// <summary>One isolated child per image-match request; never writes image or cache files.</summary>
public sealed class MarkerExemplarWorkerClient : IMarkerExemplarSuggestionService
{
    private readonly string executablePath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private Process? worker;
    private bool disposed;

    internal int? ActiveWorkerId
    {
        get { lock (sync) return worker is { HasExited: false } ? worker.Id : null; }
    }

    public MarkerExemplarWorkerClient(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath)) throw new ArgumentException("Worker path must be absolute.", nameof(executablePath));
        this.executablePath = Path.GetFullPath(executablePath);
    }

    public Task<MarkerExemplarResponse> FindAsync(MarkerExemplarRequest request,
        ReadOnlyMemory<byte> encodedOriginalPanel, CancellationToken cancellationToken) =>
        Task.Run(() => FindCoreAsync(request, encodedOriginalPanel, cancellationToken), CancellationToken.None);

    private async Task<MarkerExemplarResponse> FindCoreAsync(MarkerExemplarRequest request,
        ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidRequest(request, image)) return MarkerExemplarResponse.Failure(request, MarkerExemplarReasonCodes.InvalidInput);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        bool entered = false;
        try
        {
            await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            Process process = StartWorker();
            using var stop = deadline.Token.Register(KillWorker);
            Task<MarkerExemplarResponse> reading = MarkerExemplarWire.ReadResponseAsync(process.StandardOutput.BaseStream, deadline.Token);
            try { await MarkerExemplarWire.WriteRequestAsync(process.StandardInput.BaseStream, request, image, deadline.Token).ConfigureAwait(false); }
            catch (IOException) { /* A startup failure may already be available on stdout. */ }
            MarkerExemplarResponse response;
            try { response = await reading.ConfigureAwait(false); }
            catch (EndOfStreamException)
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                return MarkerExemplarResponse.Failure(request,
                    process.ExitCode == 3 ? MarkerExemplarReasonCodes.Timeout : MarkerExemplarReasonCodes.Unavailable);
            }
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (response.Request is null && MarkerExemplarValidation.KnownFailure(response.FailureCode))
                return MarkerExemplarResponse.Failure(request, response.FailureCode!);
            if (!IsValidResponse(request, response) || process.ExitCode != 0 && response.FailureCode is null)
                return MarkerExemplarResponse.Failure(request, MarkerExemplarReasonCodes.InvalidFrame);
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return MarkerExemplarResponse.Failure(request, MarkerExemplarReasonCodes.Timeout); }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkerExemplarResponse.Failure(request, MarkerExemplarReasonCodes.Unavailable);
        }
        finally
        {
            if (entered) { StopWorker(); gate.Release(); }
        }
    }

    private Process StartWorker()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var start = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("--marker-exemplar-worker");
            var process = new Process { StartInfo = start };
            try { if (!process.Start()) throw new InvalidOperationException(); }
            catch { process.Dispose(); throw; }
            worker = process;
            _ = Task.Run(async () =>
            {
                try
                {
                    byte[] buffer = new byte[4096];
                    while (await process.StandardError.BaseStream.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
                }
                catch (Exception error) when (error is IOException or InvalidOperationException) { }
            }, CancellationToken.None);
            return process;
        }
    }

    private void StopWorker()
    {
        Process? process;
        lock (sync)
        {
            process = worker;
            worker = null;
        }
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(1000);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    private void KillWorker()
    {
        // Cancellation can originate on the UI thread. Reaping occurs in FindCoreAsync's finally.
        lock (sync)
        {
            try { if (worker is { HasExited: false }) worker.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync) disposed = true;
        GC.SuppressFinalize(this);
        return new ValueTask(Task.Run(StopWorker, CancellationToken.None));
    }

    internal static bool IsValidRequest(MarkerExemplarRequest? request, ReadOnlyMemory<byte> image) =>
        MarkerExemplarValidation.ValidRequest(request, image);
    internal static bool IsValidResponse(MarkerExemplarRequest request, MarkerExemplarResponse? response) =>
        MarkerExemplarValidation.ValidResponse(request, response);
}
