// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.CalibrationOcr;
using GraphReader.Ocr;

namespace GraphReader.App.Services;

/// <summary>One persistent, local, review-only OCR worker; no image is written to disk.</summary>
public sealed class CalibrationOcrWorkerClient : ICalibrationOcrSuggestionService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    private static readonly string[] RuntimeFileNames = ["GraphReader.App", "GraphReader.Inference", "GraphReader.Ocr", "onnxruntime.dll"];
    private readonly string _executablePath;
    private readonly string _assetRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Process? _worker;
    private bool _disposed;

    public CalibrationOcrWorkerClient(string executablePath, string assetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        if (!Path.IsPathFullyQualified(executablePath) || !Path.IsPathFullyQualified(assetRoot))
            throw new ArgumentException("OCR worker and asset paths must be absolute.");
        _executablePath = Path.GetFullPath(executablePath);
        _assetRoot = Path.GetFullPath(assetRoot);
    }

    public Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request,
        ReadOnlyMemory<byte> encodedOriginalPanel, CancellationToken cancellationToken) =>
        Task.Run(() => SuggestCoreAsync(request, encodedOriginalPanel, cancellationToken), CancellationToken.None);

    private async Task<CalibrationOcrResponse> SuggestCoreAsync(CalibrationOcrRequest request,
        ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidRequest(request, image)) return CalibrationOcrResponse.Failure(request, CalibrationOcrReasonCodes.InvalidInput);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        bool entered = false, keepWorker = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            Process process = StartOrGetWorker();
            using var stop = deadline.Token.Register(static state => ((CalibrationOcrWorkerClient)state!).StopWorker(), this);
            Task<CalibrationOcrResponse> reading = Task.Run(() => CalibrationOcrWire.ReadResponseAsync(process.StandardOutput.BaseStream, deadline.Token), CancellationToken.None);
            try { await CalibrationOcrWire.WriteRequestAsync(process.StandardInput.BaseStream, request, image, deadline.Token).ConfigureAwait(false); }
            catch (IOException) { /* The worker may have already returned a startup failure. */ }
            CalibrationOcrResponse response = await reading.ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (response.Request is null && IsKnownFailure(response.FailureCode))
                return CalibrationOcrResponse.Failure(request, response.FailureCode!);
            if (!IsValidResponse(request, response)) return CalibrationOcrResponse.Failure(request, CalibrationOcrReasonCodes.InvalidFrame);
            keepWorker = !process.HasExited;
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return CalibrationOcrResponse.Failure(request, CalibrationOcrReasonCodes.Timeout); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CalibrationOcrResponse.Failure(request, CalibrationOcrReasonCodes.Unavailable);
        }
        finally
        {
            if (entered)
            {
                if (!keepWorker) StopWorker();
                _gate.Release();
            }
        }
    }

    private Process StartOrGetWorker()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { HasExited: false }) return _worker;
            _worker?.Dispose();
            var start = new ProcessStartInfo(_executablePath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("--calibration-ocr-worker");
            start.ArgumentList.Add(_assetRoot);
            var process = new Process { StartInfo = start };
            try { if (!process.Start()) throw new InvalidOperationException(); }
            catch { process.Dispose(); throw; }
            _worker = process;
            // Drain and discard stderr in fixed-size chunks so a noisy worker cannot block.
            _ = Task.Run(async () =>
            {
                try
                {
                    byte[] buffer = new byte[4096];
                    while (await process.StandardError.BaseStream.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException) { }
            });
            return process;
        }
    }

    private void StopWorker()
    {
        lock (_sync)
        {
            Process? process = _worker;
            _worker = null;
            if (process is null) return;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) _disposed = true;
        StopWorker();
        return ValueTask.CompletedTask;
    }

    private static bool ValidRequest(CalibrationOcrRequest? request, ReadOnlyMemory<byte> image) =>
        request is not null && request.ProtocolVersion == 1 && !string.IsNullOrWhiteSpace(request.RequestId) &&
        IsSha(request.Binding) && IsSha(request.SourceFileSha256) && IsSha(request.PanelImageSha256) &&
        image.Length is > 0 and <= CalibrationOcrWire.MaximumImageBytes &&
        string.Equals(Convert.ToHexString(SHA256.HashData(image.Span)), request.PanelImageSha256, StringComparison.OrdinalIgnoreCase) &&
        request.Width > 0 && request.Height > 0 && request.Plot is { } plot &&
        double.IsFinite(plot.Left) && double.IsFinite(plot.Top) && double.IsFinite(plot.Right) && double.IsFinite(plot.Bottom) &&
        plot.Left >= 0 && plot.Top >= 0 && plot.Right < request.Width && plot.Bottom < request.Height &&
        plot.Left < plot.Right && plot.Top < plot.Bottom;

    internal static bool IsValidResponse(CalibrationOcrRequest request, CalibrationOcrResponse? response)
    {
        if (response is null || response.Request != request || response.Proposals is null) return false;
        if (response.FailureCode is not null)
            return IsKnownFailure(response.FailureCode) && response.Proposals.Count == 0 && response.XSequence is null &&
                response.YSequence is null && response.Timings is null && response.Runtime is null;
        if (response.XSequence is null || response.YSequence is null || response.Timings is null || response.Runtime is null ||
            response.Proposals.Count > 128) return false;
        foreach (CalibrationOcrProposal proposal in response.Proposals)
        {
            if (proposal is null || string.IsNullOrWhiteSpace(proposal.RegionId) || proposal.Text is null ||
                !proposal.RequiresReview || proposal.SuggestedAxis is not ("X" or "Y") ||
                proposal.Role is not ("XTick" or "YTick" or "AxisTitle" or "PhaseHeading" or "LegendText" or "Participant" or "Annotation" or "Other") ||
                !double.IsFinite(proposal.Confidence) || proposal.Confidence is < 0 or > 1 ||
                (proposal.NumericValue is double value && !double.IsFinite(value)) || proposal.Polygon is null ||
                proposal.Polygon.Count is < 3 or > 4 || proposal.Polygon.Any(point => point is null ||
                    !double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 ||
                    point.X > request.Width || point.Y > request.Height)) return false;
            bool literal = proposal.FailureCode is null && GraphNumericParser.IsLiteralGraphNumber(proposal.Text);
            if (literal != proposal.NumericLiteral) return false;
            if (!literal)
            {
                if (proposal.NumericValue is not null || proposal.NormalizedText is not null) return false;
            }
            else
            {
                NumericParseResult parsed = GraphNumericParser.Parse(proposal.Text);
                if (!parsed.IsSuccess || parsed.Value is null || proposal.NumericValue != parsed.Value ||
                    string.IsNullOrWhiteSpace(proposal.NormalizedText) || proposal.NormalizedText != parsed.NormalizedText) return false;
            }
        }
        CalibrationOcrRuntime runtime = response.Runtime;
        if (runtime.ModelSha256 != CalibrationOcrSession.ModelSha256 || runtime.ManifestSha256 != CalibrationOcrSession.ManifestSha256 || runtime.Provider != "Cpu" ||
            runtime.IntraOperationThreads != 1 || runtime.InterOperationThreads != 1 || runtime.WorkerCount != 1 ||
            runtime.SessionCreationCount != 1 || runtime.PersistentCache || runtime.CpuBudget is not { } budget ||
            !budget.Verified || budget.PriorityClass != "BelowNormal" || budget.LogicalProcessors == 0 || budget.Rate == 0 || budget.Rate != 5000 / budget.LogicalProcessors ||
            !double.IsFinite(budget.MaximumLogicalCores) || budget.MaximumLogicalCores is <= 0 or > 0.5 ||
            runtime.Files is null || runtime.Files.Count != 4 || runtime.Files.Any(file => file is null || !IsSha(file.Sha256)) ||
            !runtime.Files.Select(file => file.Name).OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(RuntimeFileNames, StringComparer.Ordinal))
            return false;
        return ValidSequence(response.XSequence, response.Proposals.Count) && ValidSequence(response.YSequence, response.Proposals.Count) &&
            new[] { response.Timings.DecodeMilliseconds, response.Timings.ComponentMilliseconds, response.Timings.CropMilliseconds,
                response.Timings.InitializationMilliseconds, response.Timings.RecognitionMilliseconds, response.Timings.TotalMilliseconds }
                .All(value => double.IsFinite(value) && value >= 0);
    }

    private static bool ValidSequence(CalibrationOcrSequenceCheck value, int maximum) =>
        value.CandidateCount >= 0 && value.CandidateCount <= maximum && value.ResolvedCount >= 0 &&
        value.ResolvedCount <= value.CandidateCount && value.RejectedCount >= 0 && value.RejectedCount <= value.CandidateCount &&
        value.Reasons is not null && value.Reasons.All(reason => !string.IsNullOrWhiteSpace(reason));

    private static bool IsSha(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool IsKnownFailure(string? value) => value is CalibrationOcrReasonCodes.Unavailable or
        CalibrationOcrReasonCodes.InvalidInput or CalibrationOcrReasonCodes.AssetMismatch or CalibrationOcrReasonCodes.ResourceUnavailable or
        CalibrationOcrReasonCodes.Timeout or CalibrationOcrReasonCodes.InvalidFrame or CalibrationOcrReasonCodes.Cancelled or
        CalibrationOcrReasonCodes.RecognitionFailed or CalibrationOcrReasonCodes.TooManyCandidates;
}
