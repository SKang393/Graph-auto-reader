// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GraphReader.Axis;

public sealed record RustAxisCandidateReviewResult(IReadOnlyList<RustAxisGeometry> Candidates, string? ErrorCode);

/// <summary>Reads a complete bounded list of observed geometry for explicit review.</summary>
public static class RustAxisCandidateReview
{
    private const int MaximumOutputCharacters = 64 * 1024;
    private const int MaximumErrorCharacters = 8192;

    public static Task<RustAxisCandidateReviewResult> RunAsync(string workerPath, int width, int height,
        ReadOnlyMemory<byte> pixels, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<RustAxisCandidateReviewResult>(cancellationToken);
        if (width is < 32 or > 4096 || height is < 32 or > 4096 ||
            (long)width * height > 16_777_216 || pixels.Length != (long)width * height)
            return Task.FromResult(Failure("INVALID_INPUT"));
        byte[] snapshot = pixels.ToArray();
        return Task.Run(() => RunCoreAsync(workerPath, width, height, snapshot, cancellationToken), CancellationToken.None);
    }

    private static async Task<RustAxisCandidateReviewResult> RunCoreAsync(string workerPath, int width, int height,
        ReadOnlyMemory<byte> pixels, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workerPath) || width is < 32 or > 4096 || height is < 32 or > 4096 ||
            (long)width * height > 16_777_216 || pixels.Length != (long)width * height)
            return Failure("INVALID_INPUT");
        if (!OperatingSystem.IsWindows()) return Failure("UNSUPPORTED_PLATFORM");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = new Process { StartInfo = new ProcessStartInfo(workerPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        }};
        process.StartInfo.ArgumentList.Add("--review-candidates");
        try
        {
            try { if (!process.Start()) return Failure("WORKER_START_FAILED"); }
            catch (System.ComponentModel.Win32Exception) { return Failure("WORKER_START_FAILED"); }
            using var stop = deadline.Token.Register(static state =>
            {
                try { ((Process)state!).Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }, process);
            Task<string?> output = ReadBoundedAsync(process.StandardOutput.BaseStream, MaximumOutputCharacters, requireAscii: true, process, deadline.Token);
            Task<string?> error = ReadBoundedAsync(process.StandardError.BaseStream, MaximumErrorCharacters, requireAscii: false, process, deadline.Token);
            byte[] header = new byte[20]; "GRA1"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), height);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 1);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), pixels.Length);
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.WriteAsync(pixels, deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException) { process.StandardInput.Close(); }
            string? response = await output.ConfigureAwait(false);
            string? stderr = await error.ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return response is null || stderr is null ? Failure("INVALID_RESPONSE") : Parse(response, process.ExitCode, width, height);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("WORKER_TIMEOUT"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { cancellationToken.ThrowIfCancellationRequested(); return Failure("WORKER_START_OR_IO_FAILED"); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); _ = process.WaitForExit(1000); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    internal static RustAxisCandidateReviewResult Parse(string response, int exitCode, int width, int height)
    {
        if (response.Length == 0 || response.Length > MaximumOutputCharacters || response[^1] != '\n') return Failure("INVALID_RESPONSE");
        string[] lines = response.Split('\n');
        if (lines[^1].Length != 0) return Failure("INVALID_RESPONSE");
        string[] first = lines[0].TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length == 2 && first[0] == "ERR" && exitCode == 1 && lines.Length == 2 &&
            first[1] is "NO_AXIS" or "NO_INTERSECTION" or "AMBIGUOUS_AXIS" or "CPU_BUDGET_UNAVAILABLE" or "INVALID_INPUT" or "INVALID_PROTOCOL")
            return Failure(first[1]);
        if (exitCode != 0 || first.Length != 2 || first[0] != "REVIEW1" ||
            !int.TryParse(first[1], NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
            count is < 1 or > 128 || lines.Length != count + 2) return Failure("INVALID_RESPONSE");
        var boxes = new RustAxisGeometry[count];
        var unique = new HashSet<(int, int, int, int)>();
        for (int i = 0; i < count; i++)
        {
            string[] parts = lines[i + 1].TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 7 || parts[0] != "BOX" ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int left) ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int bottom) ||
                !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int right) ||
                !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out int top) ||
                left < 0 || top < 0 || right >= width || bottom >= height || left >= right || top >= bottom ||
                !TryTicks(parts[5], left, right, out int[] xTicks) || !TryTicks(parts[6], top, bottom, out int[] yTicks) ||
                !unique.Add((left, bottom, right, top))) return Failure("INVALID_RESPONSE");
            boxes[i] = new RustAxisGeometry(left, bottom, right, top, 0, xTicks[0], xTicks[^1])
            { XTickPositions = Array.AsReadOnly(xTicks), YTickPositions = Array.AsReadOnly(yTicks) };
        }
        return new(Array.AsReadOnly(boxes), null);
    }

    private static bool TryTicks(string text, int minimum, int maximum, out int[] ticks)
    {
        ticks = [];
        string[] fields = text.Split(',');
        if (fields.Length is < 3 or > 128) return false;
        var parsed = new int[fields.Length];
        for (int i = 0; i < parsed.Length; i++)
            if (!int.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]) ||
                parsed[i] < minimum || parsed[i] > maximum || (i > 0 && parsed[i] <= parsed[i - 1])) return false;
        ticks = parsed;
        return true;
    }

    private static async Task<string?> ReadBoundedAsync(Stream reader, int maximum, bool requireAscii, Process process, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[1024];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return Encoding.UTF8.GetString(output.ToArray());
            bool invalidByte = false;
            if (requireAscii)
                for (int index = 0; index < count; index++)
                    if (buffer[index] >= 128) { invalidByte = true; break; }
            if (output.Length + count > maximum || invalidByte)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                return null;
            }
            output.Write(buffer, 0, count);
        }
    }

    private static RustAxisCandidateReviewResult Failure(string error) => new(Array.Empty<RustAxisGeometry>(), error);
}
