// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal enum AxisDecisionRoute { NONE, PRIMARY, CONTENT_EXTENT, GRID, CONNECTED_RUNS }
internal enum AxisDecisionReason
{
    OK, NO_AXIS, PEAK_AMBIGUOUS, THICK_ROW_BAND, THICK_COL_BAND, BAND_LIMIT, PAIR_LIMIT,
    NO_VALID_PAIR, MULTIPLE_PAIRS, OUTSIDE_BANDS, NO_SPAN, SHORT_SPAN, DISCONNECTED,
    INSUFFICIENT_TICKS, INVALID_PROTOCOL, INVALID_INPUT, CPU_BUDGET_UNAVAILABLE,
}
internal sealed record AxisDecisionTrace(AxisDecisionRoute Route, AxisDecisionReason Reason,
    uint RowBands, uint ColBands, uint PairsChecked, uint SpanRejections, uint ConnectionRejections,
    uint XTickRejections, uint YTickRejections, uint QualifiedPairs);
internal sealed record AxisDecisionDiagnosticResult(RustAxisProposalResult Proposal, AxisDecisionTrace Trace);

/// <summary>Diagnostic-only wire reader. The caller authenticates the worker before opening inputs.</summary>
internal static class AxisDecisionDiagnosticClient
{
    private const int MaximumLineLength = 2048;
    private const int MaximumStderrCharacters = 8192;
    private static readonly MethodInfo NormalParser = typeof(RustAxisProposal).GetMethod("ParseResponse",
        BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(string), typeof(int), typeof(int), typeof(int)], null)
        ?? throw new InvalidOperationException("AXIS_DIAGNOSTIC_PARSER_UNAVAILABLE");

    internal static async Task<AxisDecisionDiagnosticResult> RunAsync(string workerPath, int width, int height,
        ReadOnlyMemory<byte> gray, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(workerPath) || width is < 32 or > 4096 || height is < 32 or > 4096 ||
            (long)width * height > 16_777_216 || (long)width * height != gray.Length)
            throw new InvalidDataException("AXIS_DIAGNOSTIC_INVALID_INPUT");
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = workerPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        } };
        process.StartInfo.ArgumentList.Add("--diagnostics");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            if (!process.Start()) throw new InvalidDataException("AXIS_DIAGNOSTIC_START_FAILED");
            using var stop = deadline.Token.Register(static state => StopWorker((Process)state!), process);
            Task<string[]> output = ReadOutputAsync(process.StandardOutput, deadline.Token);
            Task stderr = DrainStderrAsync(process.StandardError, deadline.Token);
            // A malformed or excessive response must stop the owned child even while stdin is being written.
            _ = output.ContinueWith(static (task, state) => { if (task.IsFaulted) StopWorker((Process)state!); },
                process, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _ = stderr.ContinueWith(static (task, state) => { if (task.IsFaulted) StopWorker((Process)state!); },
                process, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Task write = WriteAsync(process, width, height, gray, deadline.Token);
            await Task.WhenAll(output, stderr, write, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return Parse(await output.ConfigureAwait(false), process.ExitCode, width, height);
        }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("AXIS_DIAGNOSTIC_TIMEOUT");
        }
        finally
        {
            StopWorker(process);
            try { if (!process.HasExited) process.WaitForExit(2000); }
            catch (InvalidOperationException) { }
        }
    }

    private static async Task WriteAsync(Process process, int width, int height, ReadOnlyMemory<byte> gray,
        CancellationToken token)
    {
        var header = new byte[20];
        "GRA1"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), gray.Length);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(header, token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(gray, token).ConfigureAwait(false);
        }
        catch (IOException) { } // Budget/protocol failures can exit before consuming the image.
        finally { process.StandardInput.Close(); }
    }

    private static async Task<string[]> ReadOutputAsync(StreamReader reader, CancellationToken token)
    {
        string first = await ReadLineAsync(reader, token).ConfigureAwait(false);
        string second = await ReadLineAsync(reader, token).ConfigureAwait(false);
        var tail = new char[1];
        if (await reader.ReadAsync(tail.AsMemory(), token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("AXIS_DIAGNOSTIC_EXTRA_OUTPUT");
        return [first, second];
    }

    private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n')
            {
                string line = text.Length > 0 && text[^1] == '\r'
                    ? text.ToString(0, text.Length - 1) : text.ToString();
                if (line.Length > MaximumLineLength) break;
                return line;
            }
            if (text.Length >= MaximumLineLength + 1) break;
            text.Append(character[0]);
        }
        throw new InvalidDataException("AXIS_DIAGNOSTIC_INVALID_LINE");
    }

    private static async Task DrainStderrAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        int total = 0, count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > MaximumStderrCharacters) throw new InvalidDataException("AXIS_DIAGNOSTIC_STDERR_LIMIT");
        }
    }

    private static AxisDecisionDiagnosticResult Parse(string[] lines, int exitCode, int width, int height)
    {
        if (lines.Length != 2 || lines.Any(line => line.Length > MaximumLineLength || line.Contains('\n') || line.Contains('\r')))
            throw new InvalidDataException("AXIS_DIAGNOSTIC_INVALID_RESPONSE");
        var proposal = NormalParser.Invoke(null, [lines[0], exitCode, width, height]) as RustAxisProposalResult
            ?? throw new InvalidDataException("AXIS_DIAGNOSTIC_INVALID_RESPONSE");
        if (proposal.ErrorCode == "INVALID_RESPONSE") throw new InvalidDataException("AXIS_DIAGNOSTIC_INVALID_RESPONSE");
        string[] fields = lines[1].Split(' ');
        if (fields.Length != 11 || fields[0] != "TRACE1" ||
            !Enum.TryParse(fields[1], out AxisDecisionRoute route) || !Enum.IsDefined(route) || route.ToString() != fields[1] ||
            !Enum.TryParse(fields[2], out AxisDecisionReason reason) || !Enum.IsDefined(reason) || reason.ToString() != fields[2])
            throw new InvalidDataException("AXIS_DIAGNOSTIC_INVALID_TRACE");
        var values = new uint[8];
        for (int i = 0; i < values.Length; i++)
            if (!uint.TryParse(fields[i + 3], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]) || values[i] > 16384)
                throw new InvalidDataException("AXIS_DIAGNOSTIC_INVALID_COUNTER");
        return new(proposal, new(route, reason, values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]));
    }

    private static void StopWorker(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    internal static object SelfTest()
    {
        const string normal = "OK 8 52 55 7 0.9 - - - -";
        const string trace = "TRACE1 PRIMARY OK 1 1 1 0 0 0 0 1";
        AxisDecisionDiagnosticResult result = Parse([normal, trace], 0, 64, 64);
        if (result.Proposal.Geometry is null || result.Trace.Route != AxisDecisionRoute.PRIMARY || result.Trace.QualifiedPairs != 1)
            throw new InvalidDataException("AXIS_DIAGNOSTIC_SELF_TEST_FAILED");
        result = Parse(["ERR NO_AXIS", "TRACE1 CONTENT_EXTENT NO_VALID_PAIR 0 0 0 0 0 0 0 0"], 1, 64, 64);
        if (result.Proposal.ErrorCode != "NO_AXIS") throw new InvalidDataException("AXIS_DIAGNOSTIC_SELF_TEST_FAILED");
        string[][] malformed =
        [
            [normal], [normal, trace, "extra"], ["unrecognized", trace], [normal, new string('x', 2049)],
            [normal, trace.Replace("PRIMARY", "UNKNOWN", StringComparison.Ordinal)],
            [normal, trace.Replace(" OK ", " UNKNOWN ", StringComparison.Ordinal)],
            [normal, "TRACE1 PRIMARY OK 1 1 1 0 0 0 0"],
            [normal, "TRACE1 PRIMARY OK 1 1 1 0 0 0 0 1 1"],
            [normal, "TRACE1 PRIMARY OK -1 1 1 0 0 0 0 1"],
            [normal, "TRACE1 PRIMARY OK 16385 1 1 0 0 0 0 1"],
            [normal, "TRACE1 PRIMARY OK NaN 1 1 0 0 0 0 1"],
            [normal, "TRACE1 1 OK 1 1 1 0 0 0 0 1"],
        ];
        int rejected = 0;
        foreach (string[] lines in malformed)
        {
            try { _ = Parse(lines, 0, 64, 64); }
            catch (InvalidDataException) { rejected++; }
        }
        if (rejected != malformed.Length) throw new InvalidDataException("AXIS_DIAGNOSTIC_SELF_TEST_FAILED");
        return new { valid_response_checks = 2, malformed_response_checks = rejected, private_payload_reads = 0 };
    }
}
