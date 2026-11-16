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
        CancellationToken cancellationToken = default) =>
        StartAsync(workerPath, width, height, pixels, geometry, channels, "--measure-ticks", cancellationToken);

    /// <summary>For an explicitly marked frame, retains ticks only from independently observed full spans.</summary>
    public static Task<RustAxisTickMeasurementResult> RunManualPartialAsync(string workerPath, int width, int height,
        ReadOnlyMemory<byte> pixels, RustAxisGeometry geometry, int channels = 1,
        CancellationToken cancellationToken = default) =>
        StartAsync(workerPath, width, height, pixels, geometry, channels, "--measure-ticks-manual-partial", cancellationToken);

    /// <summary>Measures one exact derived Gray8 box with bounded original-pixel support.
    /// Protocol refusal never downgrades to a different measurement mode.</summary>
    public static Task<RustAxisTickMeasurementResult> RunAffineAsync(string workerPath, int width, int height,
        ReadOnlyMemory<byte> pixels, RustAxisGeometry geometry, RustAffineTickSource source,
        bool manualPartial = false, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<RustAxisTickMeasurementResult>(cancellationToken);
        if (!TryAffineSnapshot(width, height, pixels, geometry, source, out RequestSnapshot? request))
            return Task.FromResult(Failure("INVALID_INPUT"));
        string mode = manualPartial ? "--measure-affine-ticks-manual-partial" : "--measure-affine-ticks";
        return Task.Run(() => RunCoreAsync(workerPath, request!, mode, cancellationToken), CancellationToken.None);
    }

    private static bool TryAffineSnapshot(int width, int height, ReadOnlyMemory<byte> pixels,
        RustAxisGeometry? geometry, RustAffineTickSource? source, out RequestSnapshot? request)
    {
        request = null;
        if (source is null || source.Width is < 32 or > 4096 || source.Height is < 32 or > 4096 ||
            (long)source.Width * source.Height > MaximumPixels || (long)source.Width * source.Height != source.Pixels.Length ||
            !double.IsFinite(source.M11) || !double.IsFinite(source.M12) || !double.IsFinite(source.M13) ||
            !double.IsFinite(source.M21) || !double.IsFinite(source.M22) || !double.IsFinite(source.M23)) return false;
        double determinant = source.M11 * source.M22 - source.M12 * source.M21;
        if (!double.IsFinite(determinant) || determinant <= 0 ||
            !double.IsFinite(source.M22 / determinant) || !double.IsFinite(source.M12 / determinant) ||
            !double.IsFinite(source.M21 / determinant) || !double.IsFinite(source.M11 / determinant) ||
            !TrySnapshot(width, height, 1, pixels, geometry, out RequestSnapshot? derived)) return false;
        if (!ValidSourceGeometry(source, derived!, determinant)) return false;
        request = derived! with { Source = new(source.Width, source.Height, source.Pixels.ToArray(),
            source.M11, source.M12, source.M13, source.M21, source.M22, source.M23) };
        return true;
    }

    private static bool ValidSourceGeometry(RustAffineTickSource source, RequestSnapshot box, double determinant)
    {
        // Numerical validation only. Never apply this allowance to sampling or support windows.
        const double roundoff = 1e-7;
        PixelPoint Inverse(double x, double y) => new(
            (source.M22 * (x - source.M13) - source.M12 * (y - source.M23)) / determinant,
            (source.M11 * (y - source.M23) - source.M21 * (x - source.M13)) / determinant);
        bool Inside(PixelPoint point) => point.IsFinite && point.X >= -roundoff && point.Y >= -roundoff &&
            point.X <= source.Width - 1 + roundoff && point.Y <= source.Height - 1 + roundoff;
        PixelPoint origin = Inverse(box.Left, box.Bottom), xEnd = Inverse(box.Right, box.Bottom),
            yEnd = Inverse(box.Left, box.Top), fourth = Inverse(box.Right, box.Top);
        if (!Inside(origin) || !Inside(xEnd) || !Inside(yEnd) || !Inside(fourth) ||
            xEnd.X <= origin.X || yEnd.Y >= origin.Y) return false;
        static double Length(PixelPoint a, PixelPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        double xLength = Length(origin, xEnd), yLength = Length(origin, yEnd);
        return xLength >= box.Right - box.Left - .5 - roundoff && xLength <= box.Right - box.Left + .5 + roundoff &&
            yLength >= box.Bottom - box.Top - .5 - roundoff && yLength <= box.Bottom - box.Top + .5 + roundoff;
    }

    private static Task<RustAxisTickMeasurementResult> StartAsync(string workerPath, int width, int height,
        ReadOnlyMemory<byte> pixels, RustAxisGeometry geometry, int channels, string mode,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<RustAxisTickMeasurementResult>(cancellationToken);
        if (!TrySnapshot(width, height, channels, pixels, geometry, out RequestSnapshot? request))
            return Task.FromResult(Failure("INVALID_INPUT"));
        return Task.Run(() => RunCoreAsync(workerPath, request!, mode, cancellationToken), CancellationToken.None);
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
        RequestSnapshot request, string mode, CancellationToken cancellationToken)
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
        process.StartInfo.ArgumentList.Add(mode);

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
            byte[] header = CreateHeader(request);
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.WriteAsync(request.Pixels, deadline.Token).ConfigureAwait(false);
                if (request.Source is { } source)
                    await process.StandardInput.BaseStream.WriteAsync(source.Pixels, deadline.Token).ConfigureAwait(false);
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
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private static byte[] CreateHeader(RequestSnapshot request)
    {
        byte[] header = new byte[request.Source is null ? 36 : 96];
        if (request.Source is null) "GRT1"u8.CopyTo(header); else "GRA1"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked((uint)request.Width));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), checked((uint)request.Height));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), checked((uint)request.Channels));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), checked((uint)request.Pixels.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), checked((uint)request.Left));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), checked((uint)request.Bottom));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), checked((uint)request.Right));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), checked((uint)request.Top));
        if (request.Source is { } source)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), checked((uint)source.Width));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), checked((uint)source.Height));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), checked((uint)source.Pixels.Length));
            BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(48), source.M11);
            BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(56), source.M12);
            BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(64), source.M13);
            BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(72), source.M21);
            BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(80), source.M22);
            BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(88), source.M23);
        }
        return header;
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

    private sealed record AffineSourceSnapshot(int Width, int Height, byte[] Pixels,
        double M11, double M12, double M13, double M21, double M22, double M23);
    private sealed record RequestSnapshot(int Width, int Height, int Channels, int Left, int Bottom,
        int Right, int Top, byte[] Pixels, AffineSourceSnapshot? Source = null);
}
