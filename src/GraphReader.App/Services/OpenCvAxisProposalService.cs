// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.NativeAxis;
using GraphReader.Axis;

namespace GraphReader.App.Services;

/// <summary>Runs a bounded, review-only native geometry proposal in an isolated process.</summary>
public sealed class OpenCvAxisProposalService : IAxisProposalService
{
    private readonly string workerPath;
    public OpenCvAxisProposalService(string workerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        if (!Path.IsPathFullyQualified(workerPath)) throw new ArgumentException("An absolute worker path is required.", nameof(workerPath));
        this.workerPath = Path.GetFullPath(workerPath);
    }

    public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<RustAxisProposalResult>(cancellationToken);
        if (originalImage is null || originalImage.Length == 0 || originalImage.Length > NativeAxisWorker.MaximumImageBytes)
            return Task.FromResult(Failure("INVALID_INPUT"));
        byte[] snapshot = (byte[])originalImage.Clone();
        return Task.Run(() => RunAsync(snapshot, cancellationToken), CancellationToken.None);
    }

    private async Task<RustAxisProposalResult> RunAsync(byte[] originalImage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (originalImage is null || originalImage.Length == 0 || originalImage.Length > NativeAxisWorker.MaximumImageBytes) return Failure("INVALID_INPUT");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process { StartInfo = new ProcessStartInfo(workerPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("--native-axis-worker");
        try
        {
            using var input = new MemoryStream(originalImage, writable: false);
            BitmapFrame frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216) return Failure("INVALID_INPUT");
            if (!process.Start()) return Failure("OPENCV_AXIS_UNAVAILABLE");
            using var stop = deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
            Task drain = DrainAsync(process.StandardError.BaseStream);
            Task<RustAxisProposalResult?> read = ReadResponseAsync(process.StandardOutput.BaseStream, deadline.Token);
            byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, originalImage.Length);
            await process.StandardInput.BaseStream.WriteAsync(prefix, deadline.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(originalImage, deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            RustAxisProposalResult? result = await read.ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            byte[] extra = new byte[1];
            if (await process.StandardOutput.BaseStream.ReadAsync(extra, deadline.Token).ConfigureAwait(false) != 0 ||
                process.ExitCode != 0 || !Valid(result, width, height)) return Failure("OPENCV_AXIS_UNAVAILABLE");
            return result!;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("OPENCV_AXIS_TIMEOUT"); }
        catch (Exception)
        { cancellationToken.ThrowIfCancellationRequested(); return Failure("OPENCV_AXIS_UNAVAILABLE"); }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
                _ = process.WaitForExit(1000);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
    private static async Task DrainAsync(Stream stream) { byte[] buffer = new byte[1024]; try { while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0) { } } catch (IOException) { } }
    private static async Task<RustAxisProposalResult?> ReadResponseAsync(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > NativeAxisWorker.MaximumResponseBytes) throw new InvalidDataException();
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<RustAxisProposalResult>(bytes);
    }
    internal static bool Valid(RustAxisProposalResult? result, int width, int height)
    {
        if (result is null || result.Provider != "opencv-review" || result.ReviewReasons is null || result.ReviewReasons.Count > 32 ||
            result.ReviewReasons.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 128)) return false;
        if (result.Geometry is null) return result.ErrorCode is "OPENCV_AXIS_UNAVAILABLE" or "OPENCV_AXIS_AMBIGUOUS" or "INVALID_INPUT";
        RustAxisGeometry g = result.Geometry;
        return result.ErrorCode is null && g.Left >= 0 && g.Top >= 0 && g.Right < width && g.Bottom < height && g.Left < g.Right && g.Top < g.Bottom &&
            double.IsFinite(g.Confidence) && g.Confidence is >= 0 and <= 1 && g.FirstTickX is null && g.LastTickX is null &&
            g.XTickPositions is { Count: 0 } && g.YTickPositions is { Count: 0 };
    }
    private static RustAxisProposalResult Failure(string code) => new(null, code) { Provider = "opencv-review" };
}
