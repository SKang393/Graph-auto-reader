// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace GraphReader.CalibrationOcrProbe;

internal static class AxisDecisionSelfTest
{
    private sealed record Control(string Name, int Width, int Height, byte[] Pixels, bool ExpectedGeometry);
    private sealed record Capture(byte[] FirstLine, int ExitCode, AxisDecisionDiagnosticResult? Diagnostic);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly MethodInfo DiagnosticParser = typeof(AxisDecisionDiagnosticClient).GetMethod("Parse",
        BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("AXIS_SELF_TEST_PARSER_UNAVAILABLE");

    internal static async Task<object> RunAsync(string baselinePath, string diagnosticPath, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(baselinePath) || !Path.IsPathFullyQualified(diagnosticPath))
            throw new InvalidDataException("AXIS_SELF_TEST_INVALID_PATH");
        object parserChecks = AxisDecisionDiagnosticClient.SelfTest();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var outcomes = new List<object>();
        var wall = Stopwatch.StartNew();
        foreach (Control control in Controls())
        {
            Capture baseline = await CaptureAsync(baselinePath, false, control, deadline.Token).ConfigureAwait(false);
            Capture ordinary = await CaptureAsync(diagnosticPath, false, control, deadline.Token).ConfigureAwait(false);
            Capture diagnostic = await CaptureAsync(diagnosticPath, true, control, deadline.Token).ConfigureAwait(false);
            if (baseline.ExitCode != ordinary.ExitCode || baseline.ExitCode != diagnostic.ExitCode ||
                !baseline.FirstLine.AsSpan().SequenceEqual(ordinary.FirstLine) ||
                !baseline.FirstLine.AsSpan().SequenceEqual(diagnostic.FirstLine))
                throw new InvalidDataException("AXIS_SELF_TEST_ORDINARY_RESPONSE_CHANGED");
            AxisDecisionDiagnosticResult result = diagnostic.Diagnostic
                ?? throw new InvalidDataException("AXIS_SELF_TEST_TRACE_MISSING");
            if ((result.Proposal.Geometry is not null) != control.ExpectedGeometry)
                throw new InvalidDataException("AXIS_SELF_TEST_UNEXPECTED_CONTROL_RESULT");
            if (outcomes.Count == 0)
            {
                AxisDecisionDiagnosticResult transported = await AxisDecisionDiagnosticClient.RunAsync(
                    diagnosticPath, control.Width, control.Height, control.Pixels, deadline.Token).ConfigureAwait(false);
                if (JsonSerializer.Serialize(transported, Program.Json) != JsonSerializer.Serialize(result, Program.Json))
                    throw new InvalidDataException("AXIS_SELF_TEST_CLIENT_TRANSPORT_CHANGED_RESULT");
            }
            outcomes.Add(new { control = control.Name, ordinary_response_equal = true, exit_code = diagnostic.ExitCode,
                geometry_returned = result.Proposal.Geometry is not null, error = result.Proposal.ErrorCode,
                route = result.Trace.Route.ToString(), reason = result.Trace.Reason.ToString(),
                result.Trace.RowBands, result.Trace.ColBands, result.Trace.PairsChecked, result.Trace.SpanRejections,
                result.Trace.ConnectionRejections, result.Trace.XTickRejections, result.Trace.YTickRejections, result.Trace.QualifiedPairs });
        }
        return new { schema = "graphreader.axis-decision-owned-self-test.v1", source_payload_reads = 0,
            image_writes = false, controls = outcomes, control_count = outcomes.Count,
            worker_invocations = outcomes.Count * 3 + 1, client_transport_checked = true,
            parser_checks = parserChecks, wall_milliseconds = wall.Elapsed.TotalMilliseconds };
    }

    private static async Task<Capture> CaptureAsync(string path, bool diagnostics, Control control, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = path, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        } };
        if (diagnostics) process.StartInfo.ArgumentList.Add("--diagnostics");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            if (!process.Start()) throw new InvalidDataException("AXIS_SELF_TEST_START_FAILED");
            using var stop = deadline.Token.Register(static state => Stop((Process)state!), process);
            Task<byte[]> output = ReadBoundedAsync(process.StandardOutput.BaseStream, 4100, deadline.Token);
            Task<byte[]> stderr = ReadBoundedAsync(process.StandardError.BaseStream, 8192, deadline.Token);
            _ = output.ContinueWith(static (task, state) => { if (task.IsFaulted) Stop((Process)state!); },
                process, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _ = stderr.ContinueWith(static (task, state) => { if (task.IsFaulted) Stop((Process)state!); },
                process, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Task write = WriteAsync(process, control, deadline.Token);
            await Task.WhenAll(output, stderr, write, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            byte[] bytes = await output.ConfigureAwait(false);
            int newline = Array.IndexOf(bytes, (byte)'\n');
            if (newline < 0) throw new InvalidDataException("AXIS_SELF_TEST_MISSING_LINE");
            string[] lines = StrictUtf8.GetString(bytes).Split('\n');
            if (lines.Length != (diagnostics ? 3 : 2) || lines[^1].Length != 0)
                throw new InvalidDataException("AXIS_SELF_TEST_UNEXPECTED_OUTPUT");
            for (int i = 0; i < lines.Length - 1; i++)
            {
                if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];
                if (lines[i].Length > 2048 || lines[i].Contains('\r'))
                    throw new InvalidDataException("AXIS_SELF_TEST_INVALID_LINE");
            }
            AxisDecisionDiagnosticResult? parsed = diagnostics
                ? DiagnosticParser.Invoke(null, [lines[..2], process.ExitCode, control.Width, control.Height]) as AxisDecisionDiagnosticResult
                    ?? throw new InvalidDataException("AXIS_SELF_TEST_INVALID_TRACE")
                : null;
            return new(bytes[..(newline + 1)], process.ExitCode, parsed);
        }
        finally
        {
            Stop(process);
            try { if (!process.HasExited) process.WaitForExit(2000); }
            catch (InvalidOperationException) { }
        }
    }

    private static async Task WriteAsync(Process process, Control control, CancellationToken token)
    {
        var header = new byte[20];
        "GRA1"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), control.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), control.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), control.Pixels.Length);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(header, token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(control.Pixels, token).ConfigureAwait(false);
        }
        catch (IOException) { }
        finally { process.StandardInput.Close(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[1024];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("AXIS_SELF_TEST_OUTPUT_LIMIT");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static IEnumerable<Control> Controls()
    {
        static byte[] White(int side) => Enumerable.Repeat((byte)255, side * side).ToArray();
        static void Horizontal(byte[] image, int side, int y, int left, int right)
        { for (int x = left; x <= right; x++) image[y * side + x] = 0; }
        static void Vertical(byte[] image, int side, int x, int top, int bottom)
        { for (int y = top; y <= bottom; y++) image[y * side + x] = 0; }
        static byte[] Grid(bool full)
        {
            byte[] image = White(128);
            foreach (int y in full ? new[] { 20, 40, 60, 80, 100 } : [100]) Horizontal(image, 128, y, 20, 115);
            foreach (int x in full ? new[] { 20, 40, 60, 80, 100 } : [20]) Vertical(image, 128, x, 10, 100);
            foreach (int x in new[] { 30, 70, 110 }) Vertical(image, 128, x, 101, 104);
            foreach (int y in new[] { 20, 50, 80 }) Horizontal(image, 128, y, 16, 19);
            return image;
        }
        yield return new("single_axes", 128, 128, Grid(false), true);
        byte[] grid = Grid(true);
        yield return new("unique_outward_grid", 128, 128, grid, true);
        byte[] caption = White(512);
        Horizontal(caption, 512, 190, 100, 195); Vertical(caption, 512, 100, 100, 190);
        foreach (int x in new[] { 110, 140, 180 }) Vertical(caption, 512, x, 191, 194);
        foreach (int y in new[] { 110, 140, 180 }) Horizontal(caption, 512, y, 96, 99);
        for (int x = 0; x < 512; x += 2) caption[400 * 512 + x] = 0;
        yield return new("caption_extent", 512, 512, caption, true);
        byte[] two = White(256);
        for (int y = 0; y < 128; y++)
        {
            grid.AsSpan(y * 128, 128).CopyTo(two.AsSpan(y * 256, 128));
            grid.AsSpan(y * 128, 128).CopyTo(two.AsSpan((y + 128) * 256 + 128, 128));
        }
        yield return new("two_plots", 256, 256, two, false);
        yield return new("blank", 128, 128, White(128), false);
        byte[] thick = White(128);
        for (int y = 100; y <= 104; y++) Horizontal(thick, 128, y, 20, 115);
        Vertical(thick, 128, 20, 10, 104);
        yield return new("thick_band", 128, 128, thick, false);
        byte[] shortAxes = White(128);
        Horizontal(shortAxes, 128, 30, 20, 30); Vertical(shortAxes, 128, 20, 20, 30);
        yield return new("short_axes", 128, 128, shortAxes, false);
        byte[] disconnected = White(128);
        Horizontal(disconnected, 128, 100, 40, 115); Vertical(disconnected, 128, 20, 10, 70);
        yield return new("disconnected_axes", 128, 128, disconnected, false);
    }
}
