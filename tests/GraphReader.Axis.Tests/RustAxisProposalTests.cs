// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.Axis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class RustAxisProposalTests
{
    private static readonly int[] ParsedTicks = [20, 40, 60, 80, 100];
    private static readonly int[] FixtureTicks = [20, 40, 60, 80, 96];
    [TestMethod]
    public void ParserAcceptsLegacyAndValidTickListsButRejectsInvalidLists()
    {
        RustAxisProposalResult Parse(string text) => (RustAxisProposalResult)typeof(RustAxisProposal)
            .GetMethod("ParseResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [text, 0, 128, 128])!;
        Assert.IsNotNull(Parse("OK 20 100 115 10 0.7 - -").Geometry);
        RustAxisProposalResult fallback = Parse("OK 20 100 115 10 0.7 30 110 - -");
        Assert.IsNotNull(fallback.Geometry);
        Assert.AreEqual(30, fallback.Geometry.FirstTickX);
        Assert.AreEqual(110, fallback.Geometry.LastTickX);
        Assert.AreEqual(0, fallback.Geometry.XTickPositions.Count);
        RustAxisProposalResult valid = Parse("OK 20 100 115 10 0.7 20 100 20,40,60,80,100 20,40,60,80,100");
        Assert.IsNotNull(valid.Geometry);
        CollectionAssert.AreEqual(ParsedTicks, valid.Geometry.XTickPositions.ToArray());
        CollectionAssert.AreEqual(ParsedTicks, valid.Geometry.YTickPositions.ToArray());
        foreach (string line in new[] {
            "OK 20 100 115 10 0.7 20 100 20,40,40,100 -",
            "OK 20 100 115 10 0.7 20 100 20,40,101 -",
            "OK 20 100 115 10 0.7 20 100 20,40,100 9,40,100",
            "OK 20 100 115 10 0.7 - - 20,40,100 -",
            "OK 20 100 115 10 0.7 - - - " + string.Join(',', Enumerable.Range(0, 129)),
            new string('x', 2049) })
            Assert.AreEqual("INVALID_RESPONSE", Parse(line).ErrorCode);
    }
    [TestMethod]
    public async Task InvalidFrameFailsBeforeLaunchingWorker()
    {
        RustAxisProposalResult result = await RustAxisProposal.RunAsync("missing.exe", 64, 64, new byte[2]);
        Assert.AreEqual("INVALID_INPUT", result.ErrorCode);
        Assert.IsNull(result.Geometry);
    }

    [TestMethod]
    public async Task CancellationBeforeLaunchIsPreserved()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await RustAxisProposal.RunAsync("missing.exe", 64, 64, new byte[4096], cancellationToken: cancel.Token));
    }

    [TestMethod]
    public async Task MissingWorkerReturnsStructuredFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        RustAxisProposalResult result = await RustAxisProposal.RunAsync("missing-graphreader-worker.exe", 64, 64, new byte[4096]);
        Assert.AreEqual("WORKER_START_OR_IO_FAILED", result.ErrorCode);
    }

    [TestMethod]
    public async Task ActualWorkerReturnsPixelGeometryAndRejectsBlank()
    {
        string? worker = Environment.GetEnvironmentVariable("GRAPHREADER_RUST_WORKER");
        if (string.IsNullOrEmpty(worker)) { Assert.Inconclusive("Set GRAPHREADER_RUST_WORKER to the built worker."); return; }
        byte[] pixels = Enumerable.Repeat((byte)255, 64 * 64).ToArray();
        for (int x = 8; x <= 55; x++) pixels[52 * 64 + x] = 0;
        for (int y = 7; y <= 52; y++) pixels[y * 64 + 8] = 0;
        RustAxisProposalResult result = await RustAxisProposal.RunAsync(worker, 64, 64, pixels);
        Assert.IsNull(result.ErrorCode);
        Assert.IsNotNull(result.Geometry);
        Assert.AreEqual(new RustAxisGeometry(8, 52, 55, 7, 0.71875), result.Geometry);
        pixels = Enumerable.Repeat((byte)255, 128 * 128).ToArray();
        for (int x = 8; x <= 115; x++) pixels[100 * 128 + x] = 0;
        for (int y = 10; y <= 100; y++) pixels[y * 128 + 8] = 0;
        foreach (int x in new[] { 20, 40, 60, 80, 96 })
            for (int y = 101; y <= 104; y++) pixels[y * 128 + x] = 0;
        result = await RustAxisProposal.RunAsync(worker, 128, 128, pixels);
        Assert.IsNull(result.ErrorCode);
        Assert.IsNotNull(result.Geometry);
        Assert.AreEqual(8, result.Geometry.Left);
        Assert.AreEqual(115, result.Geometry.Right);
        Assert.AreEqual(20, result.Geometry.FirstTickX);
        Assert.AreEqual(96, result.Geometry.LastTickX);
        CollectionAssert.AreEqual(FixtureTicks, result.Geometry.XTickPositions.ToArray());
        pixels = new byte[64 * 64];
        Array.Fill(pixels, (byte)255);
        result = await RustAxisProposal.RunAsync(worker, 64, 64, pixels);
        Assert.AreEqual("NO_AXIS", result.ErrorCode);
        Assert.IsNull(result.Geometry);
    }

    [TestMethod]
    public async Task CancellingActiveWorkerStopsTheRequest()
    {
        string? worker = Environment.GetEnvironmentVariable("GRAPHREADER_RUST_BLOCKING_WORKER");
        if (string.IsNullOrEmpty(worker)) { Assert.Inconclusive("Set GRAPHREADER_RUST_BLOCKING_WORKER to the test-only blocking worker."); return; }
        using var cancel = new CancellationTokenSource();
        string ready = Path.Combine(Path.GetTempPath(), "graphreader-worker-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("GRAPHREADER_BLOCKING_READY");
        try
        {
            Environment.SetEnvironmentVariable("GRAPHREADER_BLOCKING_READY", ready);
            Task<RustAxisProposalResult> operation = RustAxisProposal.RunAsync(worker, 4096, 4096,
                new byte[4096 * 4096], cancellationToken: cancel.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(ready)) await Task.Delay(10, deadline.Token);
            int pid = int.Parse(await File.ReadAllTextAsync(ready), System.Globalization.CultureInfo.InvariantCulture);
            cancel.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await operation);
            try
            {
                using var child = System.Diagnostics.Process.GetProcessById(pid);
                await child.WaitForExitAsync(deadline.Token);
            }
            catch (ArgumentException) { /* Already exited. */ }
        }
        finally
        {
            cancel.Cancel();
            Environment.SetEnvironmentVariable("GRAPHREADER_BLOCKING_READY", previous);
            File.Delete(ready);
        }
    }
}
