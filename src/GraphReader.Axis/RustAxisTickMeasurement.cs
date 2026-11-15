// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GraphReader.Axis;

/// <summary>Observed tick pixel positions for one exact supplied plot box.</summary>
public sealed record RustAxisTickEvidence(int Left, int Bottom, int Right, int Top,
    IReadOnlyList<int> XTickPositions, IReadOnlyList<int> YTickPositions);

/// <summary>Transient tick measurements, without axis confidence or graph values.</summary>
public sealed record RustAxisTickMeasurementResult(RustAxisTickEvidence? Evidence, string? ErrorCode);

/// <summary>Measures ticks only inside caller-supplied plot geometry; it never selects or fits geometry.</summary>
public static class RustAxisTickMeasurement
{
    private const int MaximumPixels = 16_777_216;
    private const int MaximumOutputBytes = 2048;
    private const int MaximumErrorBytes = 8192;
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(60);

    public static Task<RustAxisTickMeasurementResult> RunAsync(string workerPath, int width, int height,
        ReadOnlyMemory<byte> pixels, RustAxisGeometry geometry, int channels = 1,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<RustAxisTickMeasurementResult>(cancellationToken);
        if (!TrySnapshot(width, height, channels, pixels, geometry, out RequestSnapshot? request))
            return Task.FromResult(Failure("INVALID_INPUT"));
        return Task.Run(() => RunCoreAsync(workerPath, request!, cancellationToken), CancellationToken.None);
    }

    private static bool TrySnapshot(int width, int height, int channels, ReadOnlyMemory<byte> pixels,
        RustAxisGeometry? geometry, out RequestSnapshot? request)
    {
        request = null;
        if (geometry is null || width is < 32 or > 4096 || height is < 32 or > 4096 ||
            channels is not (1 or 3) || (long)width * height > MaximumPixels ||
            (long)width * height * channels != pixels.Length || geometry.Left < 0 || geometry.Top < 0 ||
            geometry.Right >= width || geometry.Bottom >= height || geometry.Left >= geometry.Right ||
            geometry.Top >= geometry.Bottom)
            return false;

        request = new RequestSnapshot(width, height, channels, geometry.Left, geometry.Bottom,
            geometry.Right, geometry.Top, pixels.ToArray());
        return true;
    }

    private static async Task<RustAxisTickMeasurementResult> RunCoreAsync(string workerPath,
        RequestSnapshot request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workerPath)) return Failure("INVALID_INPUT");
        if (!OperatingSystem.IsWindows()) return Failure("UNSUPPORTED_PLATFORM");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(WorkerTimeout);
        using var process = new Process { StartInfo = new ProcessStartInfo(workerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }};
        process.StartInfo.ArgumentList.Add("--measure-ticks");

        try
        {
            try
            {
                if (!process.Start()) return Failure("WORKER_START_FAILED");
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                return Failure("WORKER_START_FAILED");
            }

            using var stop = deadline.Token.Register(static state =>
            {
                try { ((Process)state!).Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }, process);

            Task<byte[]?> outputTask = ReadBoundedOutputAsync(process.StandardOutput.BaseStream, MaximumOutputBytes,
                process, deadline.Token);
            Task<bool> errorTask = DrainBoundedErrorAsync(process.StandardError.BaseStream, MaximumErrorBytes,
                process, deadline.Token);
            byte[] header = new byte[36];
            "GRT1"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked((uint)request.Width));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), checked((uint)request.Height));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), checked((uint)request.Channels));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), checked((uint)request.Pixels.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), checked((uint)request.Left));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), checked((uint)request.Bottom));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), checked((uint)request.Right));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), checked((uint)request.Top));
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.WriteAsync(request.Pixels, deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                process.StandardInput.Close();
            }

            byte[]? output = await outputTask.ConfigureAwait(false);
            bool errorWithinBound = await errorTask.ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (output is null || !errorWithinBound) return Failure("INVALID_PROTOCOL");
            return ParseResponse(output, process.ExitCode, request.Left, request.Bottom,
                request.Right, request.Top);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("WORKER_TIMEOUT");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure("WORKER_START_OR_IO_FAILED");
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(1000);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static RustAxisTickMeasurementResult ParseResponse(byte[] response, int exitCode,
        int expectedLeft, int expectedBottom, int expectedRight, int expectedTop)
    {
        if (response.Length is < 2 or > MaximumOutputBytes || response[^1] != (byte)'\n' ||
            response.AsSpan(0, response.Length - 1).Contains((byte)'\n') ||
            response.AsSpan().Contains((byte)'\r') || response.Any(value => value >= 128))
            return Failure("INVALID_PROTOCOL");

        string line = Encoding.ASCII.GetString(response, 0, response.Length - 1);
        string[] fields = line.Split(' ');
        if (fields.Any(string.IsNullOrEmpty)) return Failure("INVALID_PROTOCOL");
        if (fields.Length == 2 && fields[0] == "ERR" && exitCode == 1 &&
            fields[1] is "INVALID_INPUT" or "INVALID_PROTOCOL" or "CPU_BUDGET_UNAVAILABLE" or "AXIS_BOX_NOT_OBSERVED")
            return Failure(fields[1]);

        if (exitCode != 0 || fields.Length != 7 || fields[0] != "TICKS1" ||
            !TryCoordinate(fields[1], out int left) || !TryCoordinate(fields[2], out int bottom) ||
            !TryCoordinate(fields[3], out int right) || !TryCoordinate(fields[4], out int top) ||
            left != expectedLeft || bottom != expectedBottom || right != expectedRight || top != expectedTop ||
            !TryTicks(fields[5], expectedLeft, expectedRight, out int[] xTicks) ||
            !TryTicks(fields[6], expectedTop, expectedBottom, out int[] yTicks))
            return Failure("INVALID_PROTOCOL");

        return new(new RustAxisTickEvidence(expectedLeft, expectedBottom, expectedRight, expectedTop,
            Array.AsReadOnly(xTicks), Array.AsReadOnly(yTicks)), null);
    }

    private static bool TryCoordinate(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryTicks(string text, int minimum, int maximum, out int[] positions)
    {
        positions = [];
        if (text == "-") return true;
        string[] fields = text.Split(',');
        if (fields.Length is < 3 or > 128) return false;
        var parsed = new int[fields.Length];
        for (int index = 0; index < fields.Length; index++)
        {
            if (!int.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[index]) ||
                parsed[index] < minimum || parsed[index] > maximum || index > 0 && parsed[index] <= parsed[index - 1])
                return false;
        }
        positions = parsed;
        return true;
    }

    private static async Task<byte[]?> ReadBoundedOutputAsync(Stream stream, int maximum, Process process,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[512];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            bool nonAscii = false;
            for (int index = 0; index < count; index++)
            {
                if (buffer[index] < 128) continue;
                nonAscii = true;
                break;
            }
            if (nonAscii || output.Length + count > maximum)
            {
                Kill(process);
                return null;
            }
            output.Write(buffer, 0, count);
        }
    }

    private static async Task<bool> DrainBoundedErrorAsync(Stream stream, int maximum, Process process,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        long total = 0;
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return true;
            total += count;
            if (total > maximum)
            {
                Kill(process);
                return false;
            }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static RustAxisTickMeasurementResult Failure(string errorCode) =>
        new(null, errorCode);

    private sealed record RequestSnapshot(int Width, int Height, int Channels, int Left, int Bottom,
        int Right, int Top, byte[] Pixels);
}
