// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Reflection;
using System.Buffers.Binary;
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
    private static readonly int[] PartialXTicks = [40, 70, 100];
    private static readonly int[] PartialYTicks = [20, 50, 80];
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
        MethodInfo encoder = typeof(RustAxisTickMeasurement).GetMethod("CreateHeader", BindingFlags.NonPublic | BindingFlags.Static)!;
        byte[] header = (byte[])encoder.Invoke(null, [snapshot])!;
        Assert.AreEqual(36, header.Length);
        Assert.AreEqual("GRT1", Encoding.ASCII.GetString(header, 0, 4));
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
    public async Task ManualPartialSharesPrestartCancellationAndRequestBounds()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        TaskCanceledException error = await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await RustAxisTickMeasurement.RunManualPartialAsync("unused", 64, 64, new byte[4096], Plot,
                cancellationToken: canceled.Token));
        Assert.AreEqual(canceled.Token, error.CancellationToken);
        RustAxisTickMeasurementResult bad = await RustAxisTickMeasurement.RunManualPartialAsync(
            "unused", 31, 64, new byte[31 * 64], Plot);
        Assert.AreEqual("INVALID_INPUT", bad.ErrorCode);
        Assert.IsNull(bad.Evidence);
        bad = await RustAxisTickMeasurement.RunManualPartialAsync("unused", 128, 128,
            new byte[128 * 128], Plot with { Right = 128 });
        Assert.AreEqual("INVALID_INPUT", bad.ErrorCode);
        Assert.IsNull(bad.Evidence);
    }

    [TestMethod]
    public async Task AffineRejectsInvalidSourceMatrixAndDerivedContextBeforeWorkerStart()
    {
        byte[] pixels = new byte[128 * 128];
        var source = new RustAffineTickSource(128, 128, pixels, 1, 0, 0, 0, 1, 0);
        RustAffineTickSource?[] invalid = [null, source with { Width = 31 }, source with { Height = 4097 },
            source with { Pixels = new byte[3] }, source with { M11 = double.NaN }, source with { M13 = double.PositiveInfinity },
            source with { M11 = 0 }, source with { M11 = -1 }, source with { M11 = 2 },
            source with { M13 = 200 }, source with { M11 = -1, M22 = -1, M13 = 127, M23 = 127 }];
        foreach (RustAffineTickSource? bad in invalid)
        {
            RustAxisTickMeasurementResult result = await RustAxisTickMeasurement.RunAffineAsync("unused", 128, 128, pixels, Plot, bad!);
            Assert.AreEqual("INVALID_INPUT", result.ErrorCode);
            Assert.IsNull(result.Evidence);
        }
        Assert.AreEqual("INVALID_INPUT", (await RustAxisTickMeasurement.RunAffineAsync("unused", 128, 128,
            new byte[3], Plot, source)).ErrorCode);
        Assert.AreEqual("INVALID_INPUT", (await RustAxisTickMeasurement.RunAffineAsync("unused", 128, 128,
            pixels, Plot with { Right = 128 }, source)).ErrorCode);
    }

    [TestMethod]
    public void AffineSnapshotCopiesBothRastersAndWritesExactLittleEndianHeader()
    {
        byte[] derived = new byte[128 * 128], original = new byte[128 * 128];
        original[0] = 192; derived[0] = 64;
        var source = new RustAffineTickSource(128, 128, original, 1, 0, .25, 0, 1, .5);
        object snapshot = AffineSnapshot(derived, source)!;
        derived[0] = 0; original[0] = 0;
        byte[] copied = (byte[])snapshot.GetType().GetProperty("Pixels")!.GetValue(snapshot)!;
        object originalSnapshot = snapshot.GetType().GetProperty("Source")!.GetValue(snapshot)!;
        byte[] sourceCopy = (byte[])originalSnapshot.GetType().GetProperty("Pixels")!.GetValue(originalSnapshot)!;
        Assert.AreEqual(64, copied[0]); Assert.AreEqual(192, sourceCopy[0]);
        MethodInfo encoder = typeof(RustAxisTickMeasurement).GetMethod("CreateHeader", BindingFlags.Static | BindingFlags.NonPublic)!;
        byte[] header = (byte[])encoder.Invoke(null, [snapshot])!;
        Assert.AreEqual(96, header.Length);
        Assert.AreEqual("GRA1", Encoding.ASCII.GetString(header, 0, 4));
        uint[] expected = [128, 128, 1, 16384, 10, 90, 110, 10, 128, 128, 16384];
        for (int index = 0; index < expected.Length; index++) Assert.AreEqual(expected[index], BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4 + index * 4)));
        double[] matrix = [1, 0, .25, 0, 1, .5];
        for (int index = 0; index < matrix.Length; index++) Assert.AreEqual(matrix[index], BinaryPrimitives.ReadDoubleLittleEndian(header.AsSpan(48 + index * 8)));
    }

    [TestMethod]
    public void AffineNumericalValidationAllowsOnlyDeclaredBorderAndRoundedLengthRoundoff()
    {
        byte[] pixels = new byte[128 * 128];
        var source = new RustAffineTickSource(128, 128, pixels, 1, 0, 10 + 1e-8, 0, 1, 10 + 1e-8);
        Assert.IsNotNull(AffineSnapshot(pixels, source));
        Assert.IsNull(AffineSnapshot(pixels, source with { M13 = 10 + 1e-6 }));
        var midpoint = source with { M11 = 100 / (100.5 + 5e-8), M13 = 10, M23 = 10 };
        Assert.IsNotNull(AffineSnapshot(pixels, midpoint));
        Assert.IsNull(AffineSnapshot(pixels, midpoint with { M11 = 100 / (100.5 + 5e-7) }));
    }

    [TestMethod]
    public async Task AffinePrestartCancellationPreservesTokenForBothModes()
    {
        byte[] pixels = new byte[128 * 128];
        var source = new RustAffineTickSource(128, 128, pixels, 1, 0, 0, 0, 1, 0);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        for (int mode = 0; mode < 2; mode++)
        {
            TaskCanceledException error = await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
                await RustAxisTickMeasurement.RunAffineAsync("unused", 128, 128, pixels, Plot, source, mode != 0, canceled.Token));
            Assert.AreEqual(canceled.Token, error.CancellationToken);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BuiltWorkerAffineWireKeepsStrictOrExplicitPartialSemantics(bool partial)
    {
        string? worker = Environment.GetEnvironmentVariable("GRAPHREADER_RUST_WORKER");
        if (string.IsNullOrWhiteSpace(worker)) { Assert.Inconclusive("Set GRAPHREADER_RUST_WORKER to the built Rust worker."); return; }
        byte[] pixels = PartialFixture();
        for (int y = 40; y <= 80; y++) pixels[y * 128 + 20] = 255;
        byte[] before = pixels.ToArray();
        var source = new RustAffineTickSource(128, 128, pixels, 1, 0, 0, 0, 1, 0);
        RustAxisTickMeasurementResult result = await RustAxisTickMeasurement.RunAffineAsync(worker, 128, 128, pixels,
            new(20, 100, 115, 10, 0), source, partial);
        if (partial)
        {
            Assert.IsNull(result.ErrorCode);
            Assert.IsNotNull(result.Evidence);
            CollectionAssert.AreEqual(PartialXTicks, result.Evidence.XTickPositions.ToArray());
            Assert.IsEmpty(result.Evidence.YTickPositions);
        }
        else { Assert.AreEqual("AXIS_BOX_NOT_OBSERVED", result.ErrorCode); Assert.IsNull(result.Evidence); }
        CollectionAssert.AreEqual(before, pixels);
    }

    [TestMethod]
    public async Task BuiltWorkerAffineIdentityPreservesFullyObservedStrictEvidence()
    {
        string? worker = Environment.GetEnvironmentVariable("GRAPHREADER_RUST_WORKER");
        if (string.IsNullOrWhiteSpace(worker)) { Assert.Inconclusive("Set GRAPHREADER_RUST_WORKER to the built Rust worker."); return; }
        byte[] pixels = PartialFixture();
        var box = new RustAxisGeometry(20, 100, 115, 10, 0);
        RustAxisTickMeasurementResult strict = await RustAxisTickMeasurement.RunAsync(worker, 128, 128, pixels, box);
        RustAxisTickMeasurementResult affine = await RustAxisTickMeasurement.RunAffineAsync(worker, 128, 128, pixels, box,
            new(128, 128, pixels, 1, 0, 0, 0, 1, 0));
        Assert.IsNull(strict.ErrorCode); Assert.IsNull(affine.ErrorCode);
        Assert.IsNotNull(strict.Evidence); Assert.IsNotNull(affine.Evidence);
        CollectionAssert.AreEqual(strict.Evidence.XTickPositions.ToArray(), affine.Evidence.XTickPositions.ToArray());
        CollectionAssert.AreEqual(strict.Evidence.YTickPositions.ToArray(), affine.Evidence.YTickPositions.ToArray());
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task BlockingWorkerPreservesCallerCancellationAcrossLegacyAndAffineModes(int mode)
    {
        string? worker = Environment.GetEnvironmentVariable("GRAPHREADER_RUST_BLOCKING_WORKER");
        if (string.IsNullOrWhiteSpace(worker)) { Assert.Inconclusive("Set GRAPHREADER_RUST_BLOCKING_WORKER to the existing cancellation fixture."); return; }
        byte[] pixels = PartialFixture();
        using var cancellation = new CancellationTokenSource();
        string ready = Path.Combine(Path.GetTempPath(), "graphreader-affine-worker-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("GRAPHREADER_BLOCKING_READY");
        Task<RustAxisTickMeasurementResult>? operation = null;
        try
        {
            Environment.SetEnvironmentVariable("GRAPHREADER_BLOCKING_READY", ready);
            var box = new RustAxisGeometry(20, 100, 115, 10, 0);
            operation = mode switch
            {
                0 => RustAxisTickMeasurement.RunAsync(worker, 128, 128, pixels, box, cancellationToken: cancellation.Token),
                1 => RustAxisTickMeasurement.RunManualPartialAsync(worker, 128, 128, pixels, box, cancellationToken: cancellation.Token),
                _ => RustAxisTickMeasurement.RunAffineAsync(worker, 128, 128, pixels, box,
                    new(128, 128, pixels, 1, 0, 0, 0, 1, 0), mode == 3, cancellation.Token),
            };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(ready)) await Task.Delay(10, deadline.Token);
            int pid = int.Parse(await File.ReadAllTextAsync(ready, deadline.Token), System.Globalization.CultureInfo.InvariantCulture);
            cancellation.Cancel();
            OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(async () => await operation);
            Assert.AreEqual(cancellation.Token, error.CancellationToken);
            try
            {
                using var child = System.Diagnostics.Process.GetProcessById(pid);
                await child.WaitForExitAsync(deadline.Token);
                Assert.IsTrue(child.HasExited);
            }
            catch (ArgumentException) { /* Already exited. */ }
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                if (operation is not null)
                    try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (OperationCanceledException) { /* Expected cancellation during fixture cleanup. */ }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GRAPHREADER_BLOCKING_READY", previous);
                File.Delete(ready);
                File.Delete(Path.ChangeExtension(ready, "tmp"));
            }
        }
    }

    private static object? AffineSnapshot(byte[] derived, RustAffineTickSource source)
    {
        MethodInfo method = typeof(RustAxisTickMeasurement).GetMethod("TryAffineSnapshot", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] args = [128, 128, new ReadOnlyMemory<byte>(derived), Plot, source, null];
        return (bool)method.Invoke(null, args)! ? args[^1] : null;
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

        RustAxisTickMeasurementResult partial = await RustAxisTickMeasurement.RunManualPartialAsync(worker,
            width, height, pixels, new RustAxisGeometry(8, 100, 115, 10, 0), channels);
        Assert.IsNull(partial.ErrorCode);
        Assert.IsNotNull(partial.Evidence);
        CollectionAssert.AreEqual(result.Evidence.XTickPositions.ToArray(), partial.Evidence.XTickPositions.ToArray());
        CollectionAssert.AreEqual(result.Evidence.YTickPositions.ToArray(), partial.Evidence.YTickPositions.ToArray());
    }

    [TestMethod]
    [DataRow(true, 1)]
    [DataRow(false, 1)]
    [DataRow(true, 3)]
    [DataRow(false, 3)]
    public async Task BuiltWorkerManualPartialUsesExplicitModeAndRetainsOnlyObservedAxis(bool xObserved, int channels)
    {
        string? worker = Environment.GetEnvironmentVariable("GRAPHREADER_RUST_WORKER");
        if (string.IsNullOrWhiteSpace(worker)) { Assert.Inconclusive("Set GRAPHREADER_RUST_WORKER to the built Rust worker."); return; }
        byte[] gray = PartialFixture();
        if (xObserved)
            for (int y = 40; y <= 80; y++) gray[y * 128 + 20] = 255;
        else
            for (int x = 40; x <= 80; x++) gray[100 * 128 + x] = 255;
        byte[] pixels = channels == 1 ? gray : ExpandGrayToRgb(gray);
        byte[] unchanged = pixels.ToArray();
        var box = new RustAxisGeometry(20, 100, 115, 10, 0);
        RustAxisTickMeasurementResult strict = await RustAxisTickMeasurement.RunAsync(worker, 128, 128, pixels, box, channels);
        Assert.AreEqual("AXIS_BOX_NOT_OBSERVED", strict.ErrorCode);
        Assert.IsNull(strict.Evidence);

        RustAxisTickMeasurementResult partial = await RustAxisTickMeasurement.RunManualPartialAsync(
            worker, 128, 128, pixels, box, channels);
        Assert.IsNull(partial.ErrorCode);
        Assert.IsNotNull(partial.Evidence);
        Assert.AreEqual((20, 100, 115, 10), (partial.Evidence.Left, partial.Evidence.Bottom,
            partial.Evidence.Right, partial.Evidence.Top));
        CollectionAssert.AreEqual(xObserved ? PartialXTicks : Array.Empty<int>(), partial.Evidence.XTickPositions.ToArray());
        CollectionAssert.AreEqual(xObserved ? Array.Empty<int>() : PartialYTicks, partial.Evidence.YTickPositions.ToArray());
        CollectionAssert.AreEqual(unchanged, pixels);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task BuiltWorkerManualPartialPreservesOriginAndBothSpanRefusals(bool missingOrigin)
    {
        string? worker = Environment.GetEnvironmentVariable("GRAPHREADER_RUST_WORKER");
        if (string.IsNullOrWhiteSpace(worker)) { Assert.Inconclusive("Set GRAPHREADER_RUST_WORKER to the built Rust worker."); return; }
        byte[] pixels = PartialFixture();
        if (missingOrigin) pixels[100 * 128 + 20] = 255;
        else
        {
            for (int y = 40; y <= 80; y++) pixels[y * 128 + 20] = 255;
            for (int x = 40; x <= 80; x++) pixels[100 * 128 + x] = 255;
        }
        RustAxisTickMeasurementResult result = await RustAxisTickMeasurement.RunManualPartialAsync(worker,
            128, 128, pixels, new RustAxisGeometry(20, 100, 115, 10, 0));
        Assert.AreEqual("AXIS_BOX_NOT_OBSERVED", result.ErrorCode);
        Assert.IsNull(result.Evidence);
    }

    private static byte[] PartialFixture()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 128 * 128).ToArray();
        for (int x = 20; x <= 115; x++) pixels[100 * 128 + x] = 0;
        for (int y = 10; y <= 100; y++) pixels[y * 128 + 20] = 0;
        foreach (int x in PartialXTicks)
            for (int y = 101; y < 105; y++) pixels[y * 128 + x] = 0;
        foreach (int y in PartialYTicks)
            for (int x = 16; x < 20; x++) pixels[y * 128 + x] = 0;
        return pixels;
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
