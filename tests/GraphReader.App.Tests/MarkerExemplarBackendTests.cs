// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.MarkerExemplar;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class MarkerExemplarBackendTests
{
    [TestMethod]
    public void ConnectedMarkersMatchAtOriginalPixelsAndPreserveFractionalSeedOffset()
    {
        byte[] pixels = Pixels();
        byte[] before = pixels.ToArray();
        MarkerExemplarRequest request = Request([1]);
        IReadOnlyList<MarkerExemplarCandidate> result = MarkerExemplarMatcher.Find(request, pixels, CancellationToken.None);
        Assert.HasCount(2, result);
        Assert.IsTrue(result.Any(candidate => candidate.Center == new MarkerExemplarPoint(20.25, 30.5)));
        Assert.IsTrue(result.Any(candidate => candidate.Center == new MarkerExemplarPoint(80.25, 30.5)));
        Assert.IsTrue(result.All(candidate => candidate.Similarity >= .9 && candidate.Similarity <= 1));
        CollectionAssert.AreEqual(before, pixels);
    }

    [TestMethod]
    public void PlotBoundsExcludeIdenticalGlyphOutsideConfirmedPlot()
    {
        MarkerExemplarRequest request = Request([1]) with { Plot = new(30, 10, 64, 40) };
        IReadOnlyList<MarkerExemplarCandidate> result = MarkerExemplarMatcher.Find(request, Pixels(), CancellationToken.None);
        Assert.HasCount(1, result);
        Assert.AreEqual(new MarkerExemplarPoint(80.25, 30.5), result[0].Center);
    }

    [TestMethod]
    public void FullImageContextFindsAllInclusivePlotCornersButExcludesOutsideCenters()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 128 * 96).ToArray();
        (int X, int Y)[] centers = [(30, 20), (90, 20), (30, 70), (90, 70),
            (29, 45), (91, 45), (60, 19), (60, 71)];
        foreach (var center in centers)
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                    if (Math.Abs(dx) + Math.Abs(dy) <= 3) pixels[(center.Y + dy) * 128 + center.X + dx] = 0;
        MarkerExemplarRequest request = Request([1]) with
        {
            Width = 128, Height = 96, Plot = new(30, 20, 61, 51),
            Exemplar = new(25, 15, 9, 9), SelectedPoint = new(30, 20),
        };
        Assert.IsTrue(MarkerExemplarValidation.ValidMetadata(request));
        IReadOnlyList<MarkerExemplarCandidate> result = MarkerExemplarMatcher.Find(request, pixels, CancellationToken.None);
        Assert.HasCount(3, result, "Template context may cross plot edges, while match centers may not.");
        foreach (var expected in new[] { new MarkerExemplarPoint(90, 20), new(30, 70), new(90, 70) })
            Assert.IsTrue(result.Any(candidate => candidate.Center == expected));
        Assert.IsTrue(result.All(candidate => MarkerExemplarGeometry.ContainsCenter(request.Plot, candidate.Center)));
    }

    [TestMethod]
    public void FractionalOddTemplateUsesNonzeroSearchOriginAndInclusiveLastPixelCutoff()
    {
        MarkerExemplarRequest request = Request([1]) with { Plot = new(20, 30, 62, 2) };
        IReadOnlyList<MarkerExemplarCandidate> result = MarkerExemplarMatcher.Find(request, Pixels(), CancellationToken.None);
        Assert.HasCount(2, result);
        Assert.IsTrue(result.Any(candidate => candidate.Center == new MarkerExemplarPoint(20.25, 30.5)));
        Assert.IsTrue(result.Any(candidate => candidate.Center == new MarkerExemplarPoint(80.25, 30.5)));
        MarkerExemplarRequest cutoff = request with { Plot = new(20, 30, 61, 2) };
        IReadOnlyList<MarkerExemplarCandidate> restricted = MarkerExemplarMatcher.Find(cutoff, Pixels(), CancellationToken.None);
        Assert.HasCount(1, restricted);
        Assert.AreEqual(new MarkerExemplarPoint(20.25, 30.5), restricted[0].Center,
            "A fractional center at 80.25 is beyond the confirmed last pixel 80.");
        var response = new MarkerExemplarResponse(cutoff, [new("outside", new(80.25, 30.5), .99)],
            new(1, 1, 2), new(new(2, 2500, .5, true), 1, MarkerExemplarValidation.Algorithm), null);
        Assert.IsFalse(MarkerExemplarWorkerClient.IsValidResponse(cutoff, response));
    }

    [TestMethod]
    public void SmallPlotCanUseFullImageTemplateButImageClippingStillFails()
    {
        MarkerExemplarRequest request = Request([1]) with { Plot = new(50, 30, 2, 2) };
        Assert.IsTrue(MarkerExemplarValidation.ValidMetadata(request));
        Assert.IsEmpty(MarkerExemplarMatcher.Find(request, Pixels(), CancellationToken.None), "Only the seed fits this small plot.");
        Assert.IsFalse(MarkerExemplarValidation.ValidMetadata(request with { Exemplar = new(-1, 26, 9, 9) }));
        Assert.IsFalse(MarkerExemplarValidation.ValidMetadata(request with
        {
            Plot = new(90, 30, 6, 2), Exemplar = new(90, 26, 9, 9), SelectedPoint = new(94, 30.5),
        }));
    }

    [TestMethod]
    public void ConstantTemplateAndCancellationCannotProduceProposals()
    {
        MarkerExemplarRequest request = Request([1]);
        MarkerExemplarException failure = Assert.ThrowsExactly<MarkerExemplarException>(() =>
            MarkerExemplarMatcher.Find(request, Enumerable.Repeat((byte)255, 96 * 64).ToArray(), CancellationToken.None));
        Assert.AreEqual(MarkerExemplarReasonCodes.UninformativeExemplar, failure.Reason);
        Assert.ThrowsExactly<OperationCanceledException>(() => MarkerExemplarMatcher.Find(request, Pixels(), new(canceled: true)));
    }

    [TestMethod]
    public void RequestValidationRejectsMismatchedHashGeometryAndResourceBounds()
    {
        byte[] bytes = [1, 2, 3];
        MarkerExemplarRequest request = Request(bytes);
        Assert.IsTrue(MarkerExemplarWorkerClient.IsValidRequest(request, bytes));
        MarkerExemplarRequest[] invalid =
        [
            request with { PanelImageSha256 = new string('0', 64) },
            request with { Width = 4097 },
            request with { Width = 4096, Height = 4096 },
            request with { Exemplar = new(-1, 26, 9, 9) },
            request with { Exemplar = new(40, 10, 66, 66) },
            request with { Exemplar = new(40, 10, 9, 8) },
            request with { Plot = new(0, 0, 20, 20) },
            request with { SelectedPoint = new(double.NaN, 30) },
            request with { SelectedSeriesId = "" },
            request with { Binding = "stale" },
        ];
        foreach (MarkerExemplarRequest bad in invalid) Assert.IsFalse(MarkerExemplarWorkerClient.IsValidRequest(bad, bytes));
    }

    [TestMethod]
    public void ResponseValidationRejectsStaleIdentityUnsafeRuntimeAndInvalidMatches()
    {
        MarkerExemplarRequest request = Request([1]);
        var good = new MarkerExemplarResponse(request, [new("one", new(80.25, 30.5), .99)],
            new(10, 20, 31), new(new(12, 416, .4992, true), 1, MarkerExemplarValidation.Algorithm), null);
        Assert.IsTrue(MarkerExemplarWorkerClient.IsValidResponse(request, good));
        MarkerExemplarResponse[] invalid =
        [
            good with { Request = request with { SelectedPointId = "changed" } },
            good with { Request = request with { Plot = new(30, 10, 64, 40) } },
            good with { Runtime = good.Runtime! with { OpenCvThreads = 2 } },
            good with { Runtime = good.Runtime! with { CpuBudget = new(12, 10000, 12, false) } },
            good with { Candidates = [new("one", new(0, 0), .99)] },
            good with { Candidates = [new("one", new(80, 30), double.NaN)] },
            good with { Candidates = [new("one", request.SelectedPoint, .99)] },
            good with { Candidates = [good.Candidates[0], good.Candidates[0]] },
            good with { FailureCode = MarkerExemplarReasonCodes.MatchingFailed },
        ];
        foreach (MarkerExemplarResponse bad in invalid) Assert.IsFalse(MarkerExemplarWorkerClient.IsValidResponse(request, bad));
        Assert.IsTrue(MarkerExemplarWorkerClient.IsValidResponse(request,
            MarkerExemplarResponse.Failure(request, MarkerExemplarReasonCodes.Timeout)));
        Assert.IsFalse(MarkerExemplarWorkerClient.IsValidResponse(request, MarkerExemplarResponse.Failure(request, "unknown")));
    }

    [TestMethod]
    public async Task BoundedWireRoundTripRejectsOversizeAndTruncatedFrames()
    {
        byte[] bytes = [1, 2, 3];
        MarkerExemplarRequest request = Request(bytes);
        using var pipe = new MemoryStream();
        await MarkerExemplarWire.WriteRequestAsync(pipe, request, bytes, CancellationToken.None);
        pipe.Position = 0;
        var decoded = await MarkerExemplarWire.ReadRequestAsync(pipe, CancellationToken.None);
        Assert.AreEqual(request, decoded.Request);
        CollectionAssert.AreEqual(bytes, decoded.Image);
        using var truncated = new MemoryStream(pipe.ToArray()[..^1]);
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(async () => await MarkerExemplarWire.ReadRequestAsync(truncated, CancellationToken.None));
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, int.MaxValue);
        using var oversized = new MemoryStream(prefix);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await MarkerExemplarWire.ReadRequestAsync(oversized, CancellationToken.None));
    }

    [TestMethod]
    public void DecodeDimensionMismatchFailsBeforeMatching() => StaTestHost.Run(() =>
    {
        byte[] bytes = Encode();
        MarkerExemplarRequest request = Request(bytes) with { Height = 65 };
        MarkerExemplarException failure = Assert.ThrowsExactly<MarkerExemplarException>(() =>
            MarkerExemplarWorker.Process(request, bytes, new(12, 416, .4992, true), CancellationToken.None));
        Assert.AreEqual(MarkerExemplarReasonCodes.InvalidInput, failure.Reason);
    });

    [TestMethod]
    public async Task ProductionModeCannotReadImageOrLoadNativeRuntime()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        Assert.AreEqual(2, await MarkerExemplarWorker.RunAsync(WorkflowRuntimeEnvironment.Production, input, output));
        output.Position = 0;
        MarkerExemplarResponse response = await MarkerExemplarWire.ReadResponseAsync(output, CancellationToken.None);
        Assert.AreEqual(MarkerExemplarReasonCodes.Unavailable, response.FailureCode);
        Assert.AreEqual(0L, input.Position);
    }

    [TestMethod]
    public async Task PreCancelledClientCannotLaunchChild()
    {
        await using var client = new MarkerExemplarWorkerClient(Path.Combine(Path.GetTempPath(), "nonexistent-marker-worker.exe"));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => client.FindAsync(Request([1]), new byte[] { 1 }, new(canceled: true)));
    }

    private static MarkerExemplarRequest Request(byte[] bytes) => new("owned-request", new string('a', 64), new string('b', 64),
        Convert.ToHexStringLower(SHA256.HashData(bytes)), "panel", 96, 64, new(46, 26, 9, 9), new(2, 10, 92, 40),
        "point", "series", new(50.25, 30.5));

    private static byte[] Pixels()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 96 * 64).ToArray();
        for (int x = 0; x < 96; x++) pixels[30 * 96 + x] = 0;
        foreach (int center in new[] { 20, 50, 80 })
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                    if (Math.Abs(dx) + Math.Abs(dy) <= 3) pixels[(30 + dy) * 96 + center + dx] = 0;
        return pixels;
    }

    private static byte[] Encode()
    {
        var bitmap = BitmapSource.Create(96, 64, 96, 96, PixelFormats.Gray8, null, Pixels(), 96);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
