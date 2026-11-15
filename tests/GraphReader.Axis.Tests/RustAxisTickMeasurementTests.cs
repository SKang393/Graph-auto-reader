// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Reflection;
using System.Text;
using GraphReader.Axis;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class RustAxisTickMeasurementTests
{
    private static readonly RustAxisGeometry Plot = new(10, 90, 110, 10, 0);
    private static readonly int[] ExpectedXTicks = [20, 40, 80, 110];
    private static readonly int[] ExpectedYTicks = [10, 50, 90];
    private static readonly int[] ExpectedWorkerXTicks = [20, 40, 60, 80, 96];
    private static readonly string[] EvidencePropertyNames =
        ["Left", "Bottom", "Right", "Top", "XTickPositions", "YTickPositions"];
    private static readonly MethodInfo Parser = typeof(RustAxisTickMeasurement).GetMethod("ParseResponse",
        BindingFlags.NonPublic | BindingFlags.Static)!;

    [TestMethod]
    public void ParserReturnsOnlyImmutableTickEvidenceForExactBox()
    {
        RustAxisTickMeasurementResult result = Parse("TICKS1 10 90 110 10 20,40,80,110 10,50,90\n");
        Assert.IsNull(result.ErrorCode);
        Assert.IsNotNull(result.Evidence);
        Assert.AreEqual((10, 90, 110, 10), (result.Evidence.Left, result.Evidence.Bottom,
            result.Evidence.Right, result.Evidence.Top));
        CollectionAssert.AreEqual(ExpectedXTicks, result.Evidence.XTickPositions.ToArray());
        CollectionAssert.AreEqual(ExpectedYTicks, result.Evidence.YTickPositions.ToArray());
        Assert.IsInstanceOfType<System.Collections.ObjectModel.ReadOnlyCollection<int>>(result.Evidence.XTickPositions);
        Assert.IsInstanceOfType<System.Collections.ObjectModel.ReadOnlyCollection<int>>(result.Evidence.YTickPositions);
        CollectionAssert.AreEqual(EvidencePropertyNames,
            typeof(RustAxisTickEvidence).GetProperties().Select(property => property.Name).ToArray());
    }

    [TestMethod]
    public void ParserAllowsAnAxisWithoutDetectedTicks()
    {
        RustAxisTickMeasurementResult result = Parse("TICKS1 10 90 110 10 - 10,50,90\n");
        Assert.IsNull(result.ErrorCode);
        Assert.IsNotNull(result.Evidence);
        Assert.IsEmpty(result.Evidence.XTickPositions);
        CollectionAssert.AreEqual(ExpectedYTicks, result.Evidence.YTickPositions.ToArray());
    }

    [TestMethod]
    [DataRow("INVALID_INPUT")]
    [DataRow("INVALID_PROTOCOL")]
    [DataRow("CPU_BUDGET_UNAVAILABLE")]
    [DataRow("AXIS_BOX_NOT_OBSERVED")]
    public void ParserPreservesOnlyDocumentedWorkerFailures(string code)
    {
        RustAxisTickMeasurementResult result = Parse($"ERR {code}\n", exitCode: 1);
        Assert.AreEqual(code, result.ErrorCode);
        Assert.IsNull(result.Evidence);
        Assert.AreEqual("INVALID_PROTOCOL", Parse($"ERR {code}\n", exitCode: 0).ErrorCode);
    }

    [TestMethod]
    public void ParserRejectsBoxMismatchAndMalformedWholeOutput()
    {
        string valid = "TICKS1 10 90 110 10 20,40,110 10,50,90\n";
        foreach (byte[] response in new[]
        {
            Encoding.ASCII.GetBytes(valid.Replace("TICKS1 10", "TICKS1 11", StringComparison.Ordinal)),
            Encoding.ASCII.GetBytes(valid + "\n"),
            Encoding.ASCII.GetBytes(valid[..^1] + " trailing\n"),
            Encoding.ASCII.GetBytes(valid.Replace(" ", "  ", StringComparison.Ordinal)),
            Encoding.ASCII.GetBytes(valid.Replace("\n", "\r\n", StringComparison.Ordinal)),
            Encoding.ASCII.GetBytes(valid[..^1]),
            [.. Encoding.ASCII.GetBytes(valid), (byte)0xff],
        })
        {
            RustAxisTickMeasurementResult result = Parse(response);
            Assert.AreEqual("INVALID_PROTOCOL", result.ErrorCode);
            Assert.IsNull(result.Evidence);
        }

        RustAxisTickMeasurementResult badExit = Parse(Encoding.ASCII.GetBytes(valid), exitCode: 1);
        Assert.AreEqual("INVALID_PROTOCOL", badExit.ErrorCode);
    }

    [TestMethod]
    public void ParserRejectsBadTicksAndOutputBeyondProtocolBound()
    {
        string[] lines =
        [
            "TICKS1 10 90 110 10 20,20,80 10,50,90\n",
            "TICKS1 10 90 110 10 20,15,80 10,50,90\n",
            "TICKS1 10 90 110 10 9,20,80 10,50,90\n",
            "TICKS1 10 90 110 10 20,40 10,50,90\n",
            "TICKS1 10 90 110 10 20,40,80 10,50,91\n",
            "TICKS1 10 90 110 10 20,40,80 10,50\n",
        ];
        foreach (string line in lines)
            Assert.AreEqual("INVALID_PROTOCOL", Parse(line).ErrorCode);

        Assert.AreEqual("INVALID_PROTOCOL", Parse(new byte[2049]).ErrorCode);
    }

    [TestMethod]
    public void RequestValidationRejectsInvalidDimensionsChannelsPayloadAndBoxBeforeStartingWorker()
    {
        byte[] pixels = new byte[64 * 64];
        Assert.AreEqual("INVALID_INPUT", Run(31, 64, pixels, Plot).ErrorCode);
        Assert.AreEqual("INVALID_INPUT", Run(64, 64, pixels, Plot, channels: 2).ErrorCode);
        Assert.AreEqual("INVALID_INPUT", Run(64, 64, new byte[4], Plot).ErrorCode);
        Assert.AreEqual("INVALID_INPUT", Run(64, 64, pixels, Plot with { Right = 64 }).ErrorCode);
        Assert.AreEqual("INVALID_INPUT", Run(64, 64, pixels, Plot with { Top = 90 }).ErrorCode);
    }

    [TestMethod]
    public void RequestSnapshotCopiesPixelsAndExactBoxBeforeWorkerAwait()
    {
        MethodInfo snapshotter = typeof(RustAxisTickMeasurement).GetMethod("TrySnapshot",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        byte[] original = [1, 2, 3, 4];
        object?[] arguments = [2, 2, 1, new ReadOnlyMemory<byte>(original), new RustAxisGeometry(0, 1, 1, 0, 0), null];
        Assert.IsFalse((bool)snapshotter.Invoke(null, arguments)!); // dimensions are intentionally too small

        byte[] pixels = Enumerable.Range(0, 32 * 32).Select(value => (byte)value).ToArray();
        object?[] validArguments = [32, 32, 1, new ReadOnlyMemory<byte>(pixels), new RustAxisGeometry(2, 30, 29, 2, .9), null];
        Assert.IsTrue((bool)snapshotter.Invoke(null, validArguments)!);
        object snapshot = validArguments[^1]!;
        pixels[0] ^= 0xff;
        Type type = snapshot.GetType();
        byte[] copied = (byte[])type.GetProperty("Pixels", BindingFlags.Instance | BindingFlags.Public)!.GetValue(snapshot)!;
        Assert.AreEqual(0, copied[0]);
        Assert.AreEqual(2, type.GetProperty("Left", BindingFlags.Instance | BindingFlags.Public)!.GetValue(snapshot));
        Assert.AreEqual(30, type.GetProperty("Bottom", BindingFlags.Instance | BindingFlags.Public)!.GetValue(snapshot));
        Assert.AreEqual(29, type.GetProperty("Right", BindingFlags.Instance | BindingFlags.Public)!.GetValue(snapshot));
        Assert.AreEqual(2, type.GetProperty("Top", BindingFlags.Instance | BindingFlags.Public)!.GetValue(snapshot));
    }

    [TestMethod]
    public async Task CancellationBeforeStartIsPreserved()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await RustAxisTickMeasurement.RunAsync("unused", 64, 64, new byte[4096], Plot,
                cancellationToken: canceled.Token));
    }

    [TestMethod]
    public async Task MissingWorkerReturnsStructuredStartFailure()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Worker process transport is Windows-only."); return; }
        RustAxisTickMeasurementResult result = await RustAxisTickMeasurement.RunAsync(
            Path.GetFullPath("missing-rust-axis-tick-worker.exe"), 64, 64, new byte[4096],
            new RustAxisGeometry(10, 50, 50, 10, 0));
        Assert.AreEqual("WORKER_START_FAILED", result.ErrorCode);
        Assert.IsNull(result.Evidence);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task BuiltWorkerMeasuresOnlyTicksInsideSuppliedOwnedPlot(int channels)
    {
        string? worker = Environment.GetEnvironmentVariable("GRAPHREADER_RUST_WORKER");
        if (string.IsNullOrWhiteSpace(worker)) { Assert.Inconclusive("Set GRAPHREADER_RUST_WORKER to the built Rust worker."); return; }

        const int width = 128, height = 128;
        byte[] gray = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int x = 8; x <= 115; x++) gray[100 * width + x] = 0;
        for (int y = 10; y <= 100; y++) gray[y * width + 8] = 0;
        foreach (int x in new[] { 20, 40, 60, 80, 96 })
            for (int y = 101; y <= 104; y++) gray[y * width + x] = 0;
        byte[] pixels = channels == 1 ? gray : ExpandGrayToRgb(gray);

        RustAxisTickMeasurementResult result = await RustAxisTickMeasurement.RunAsync(worker, width, height,
            pixels, new RustAxisGeometry(8, 100, 115, 10, 0), channels);
        Assert.IsNull(result.ErrorCode);
        Assert.IsNotNull(result.Evidence);
        Assert.AreEqual((8, 100, 115, 10), (result.Evidence.Left, result.Evidence.Bottom,
            result.Evidence.Right, result.Evidence.Top));
        CollectionAssert.AreEqual(ExpectedWorkerXTicks, result.Evidence.XTickPositions.ToArray());
        Assert.IsEmpty(result.Evidence.YTickPositions);
    }

    private static byte[] ExpandGrayToRgb(byte[] gray)
    {
        var rgb = new byte[checked(gray.Length * 3)];
        for (int index = 0; index < gray.Length; index++)
            rgb[index * 3] = rgb[index * 3 + 1] = rgb[index * 3 + 2] = gray[index];
        return rgb;
    }

    private static RustAxisTickMeasurementResult Parse(string line, int exitCode = 0) =>
        Parse(Encoding.ASCII.GetBytes(line), exitCode);

    private static RustAxisTickMeasurementResult Parse(byte[] response, int exitCode = 0) =>
        (RustAxisTickMeasurementResult)Parser.Invoke(null,
            [response, exitCode, Plot.Left, Plot.Bottom, Plot.Right, Plot.Top])!;

    private static RustAxisTickMeasurementResult Run(int width, int height, byte[] pixels,
        RustAxisGeometry geometry, int channels = 1) => RustAxisTickMeasurement.RunAsync("unused", width,
            height, pixels, geometry, channels).GetAwaiter().GetResult();
}
