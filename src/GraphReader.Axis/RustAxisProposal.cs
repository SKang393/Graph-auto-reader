// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace GraphReader.Axis;

/// <summary>Unapproved pixel geometry for review, never calibrated values.</summary>
public sealed record RustAxisGeometry(int Left, int Bottom, int Right, int Top, double Confidence,
    int? FirstTickX = null, int? LastTickX = null);
public sealed record RustAxisProposalResult(RustAxisGeometry? Geometry, string? ErrorCode);

public static class RustAxisProposal
{
    private const int MaxPixels = 16_777_216;
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(60);

    public static Task<RustAxisProposalResult> RunAsync(string workerPath, int width, int height,
        ReadOnlyMemory<byte> pixels, int channels = 1, CancellationToken cancellationToken = default) =>
        Task.Run(() => RunCoreAsync(workerPath, width, height, pixels, channels, cancellationToken), CancellationToken.None);

    private static async Task<RustAxisProposalResult> RunCoreAsync(string workerPath, int width, int height,
        ReadOnlyMemory<byte> pixels, int channels, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workerPath) || width is < 32 or > 4096 || height is < 32 or > 4096 ||
            channels is not (1 or 3) || (long)width * height > MaxPixels ||
            (long)width * height * channels != pixels.Length)
            return new(null, "INVALID_INPUT");
        if (!OperatingSystem.IsWindows()) return new(null, "UNSUPPORTED_PLATFORM");
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = workerPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        }};
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(WorkerTimeout);
        try
        {
            if (!process.Start()) return new(null, "WORKER_START_FAILED");
            using var stop = deadline.Token.Register(static state =>
            {
                try { ((Process)state!).Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }, process);
            byte[] header = new byte[20];
            "GRA1"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), height);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), channels);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), pixels.Length);
            // Read output concurrently: a budget failure can exit before consuming stdin.
            Task<string?> output = ReadBoundedLineAsync(process.StandardOutput, deadline.Token);
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.WriteAsync(pixels, deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException) { process.StandardInput.Close(); }
            string? line = await output.ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (line is null) return new(null, "INVALID_RESPONSE");
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 && fields[0] == "ERR" && fields[1] is
                "CPU_BUDGET_UNAVAILABLE" or "INVALID_INPUT" or "INVALID_PROTOCOL" or "NO_AXIS" or "AMBIGUOUS_AXIS" or "NO_INTERSECTION")
                return new(null, fields[1]);
            if (process.ExitCode != 0 || fields.Length != 8 || fields[0] != "OK" ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int left) ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int bottom) ||
                !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out int right) ||
                !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out int top) ||
                !double.TryParse(fields[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double confidence) ||
                left < 0 || right >= width || right <= left || top < 0 || bottom >= height || bottom <= top ||
                !double.IsFinite(confidence) || confidence is < 0 or > 1)
                return new(null, "INVALID_RESPONSE");
            int? firstTick = null, lastTick = null;
            if (fields[6] != "-" || fields[7] != "-")
            {
                if (!int.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out int first) ||
                    !int.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out int last) ||
                    first < left || last > right || first >= last)
                    return new(null, "INVALID_RESPONSE");
                firstTick = first;
                lastTick = last;
            }
            return new(new(left, bottom, right, top, confidence, firstTick, lastTick), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, "WORKER_TIMEOUT"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { cancellationToken.ThrowIfCancellationRequested(); return new(null, "WORKER_START_OR_IO_FAILED"); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var chars = new char[257];
        for (int i = 0; i < chars.Length; i++)
        {
            if (await reader.ReadAsync(chars.AsMemory(i, 1), token).ConfigureAwait(false) == 0) return null;
            if (chars[i] == '\n') return new string(chars, 0, i).TrimEnd('\r');
        }
        return null;
    }
}
