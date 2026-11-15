// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App;
using GraphReader.App.Integration.NativeAxis;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class NativeAxisCandidateReviewTests
{
    private static readonly int[] DividerLefts = [100, 400];
    private static readonly int[] PanelLefts = [50, 450];

    [TestMethod]
    public void CandidateResponseRejectsContradictionsAndInvalidGeometry()
    {
        var candidate = new AxisReviewCandidate(0, new RustAxisGeometry(10, 90, 90, 10, 0));
        var valid = new AxisCandidateReviewResult([candidate], null);
        Assert.IsTrue(OpenCvAxisProposalService.ValidCandidates(valid, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.ValidCandidates(valid with { ErrorCode = "OPENCV_AXIS_AMBIGUOUS" }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.ValidCandidates(valid with { Candidates = [] }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.ValidCandidates(valid with { Candidates = [candidate with { Index = 1 }] }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.ValidCandidates(valid with { Candidates = [candidate with { Geometry = candidate.Geometry with { Right = 100 } }] }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.ValidCandidates(valid with { Candidates = [candidate with { Geometry = candidate.Geometry with { Confidence = .5 } }] }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.ValidCandidates(valid with { Candidates = Enumerable.Repeat(candidate, 129).ToArray() }, 100, 100));
        Assert.IsTrue(OpenCvAxisProposalService.ValidCandidates(new([], "OPENCV_AXIS_AMBIGUOUS"), 100, 100));
    }

    [TestMethod]
    public async Task CandidateProtocolRejectsOversizeAndTruncatedFrames()
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, NativeAxisCandidateReview.MaximumResponseBytes + 1);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OpenCvAxisProposalService.ReadCandidateResponseAsync(new MemoryStream(header), CancellationToken.None));
        BinaryPrimitives.WriteInt32LittleEndian(header, 5);
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => OpenCvAxisProposalService.ReadCandidateResponseAsync(new MemoryStream(header), CancellationToken.None));
        byte[] malformed = Encoding.UTF8.GetBytes("{\"Candidates\":[{\"Geometry\":{}}],\"ErrorCode\":null}");
        BinaryPrimitives.WriteInt32LittleEndian(header, malformed.Length);
        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(malformed);
        stream.Position = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OpenCvAxisProposalService.ReadCandidateResponseAsync(stream, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("{\"Candidates\":[],\"ErrorCode\":\"OPENCV_AXIS_AMBIGUOUS\",\"Unknown\":0}")]
    [DataRow("{\"Candidates\":[],\"Candidates\":[],\"ErrorCode\":\"OPENCV_AXIS_AMBIGUOUS\"}")]
    public async Task CandidateProtocolRejectsUnknownOrDuplicateProperties(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        using var stream = new MemoryStream(); stream.Write(prefix); stream.Write(bytes); stream.Position = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OpenCvAxisProposalService.ReadCandidateResponseAsync(stream, CancellationToken.None));
    }

    [TestMethod]
    public async Task MaximumCandidateResponseRoundTripsWithinProtocolLimit()
    {
        AxisReviewCandidate[] candidates = Enumerable.Range(0, 128)
            .Select(index => new AxisReviewCandidate(index, new RustAxisGeometry(10, 90, 90, 10, 0))).ToArray();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new AxisCandidateReviewResult(candidates, null));
        Assert.IsLessThanOrEqualTo(NativeAxisCandidateReview.MaximumResponseBytes, bytes.Length);
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        using var stream = new MemoryStream(); stream.Write(prefix); stream.Write(bytes); stream.Position = 0;
        AxisCandidateReviewResult? result = await OpenCvAxisProposalService.ReadCandidateResponseAsync(stream, CancellationToken.None);
        Assert.IsTrue(OpenCvAxisProposalService.ValidCandidates(result, 100, 100));
        Assert.HasCount(128, result!.Candidates);
        for (int i = 0; i < candidates.Length; i++)
        {
            AxisReviewCandidate candidate = result.Candidates[i];
            Assert.AreEqual(i, candidate.Index);
            Assert.AreEqual(10, candidate.Geometry.Left);
            Assert.AreEqual(90, candidate.Geometry.Bottom);
            Assert.AreEqual(90, candidate.Geometry.Right);
            Assert.AreEqual(10, candidate.Geometry.Top);
            Assert.AreEqual(0d, candidate.Geometry.Confidence);
            Assert.IsNull(candidate.Geometry.FirstTickX);
            Assert.IsNull(candidate.Geometry.LastTickX);
            Assert.IsEmpty(candidate.Geometry.XTickPositions);
            Assert.IsEmpty(candidate.Geometry.YTickPositions);
        }
    }

    [TestMethod]
    public async Task ExactContactBudgetFailureReturnsNoPartialCandidates()
    {
        var lines = new List<GeometryLineCandidate>
        {
            new("baseline", new(new(100, 700), new(1700, 700)), LineCandidateSource.RecordedFixture),
        };
        for (int index = 0; index < 129; index++)
            lines.Add(new($"vertical-{index}", new(new(100 + 10 * index, 700), new(100 + 10 * index, 100)), LineCandidateSource.RecordedFixture));
        AxisCandidateReviewResult within = await NativeAxisCandidateReview.ProcessLinesAsync(lines.Take(129).ToArray(), 1800, 800, CancellationToken.None);
        Assert.IsNull(within.ErrorCode);
        Assert.HasCount(128, within.Candidates);
        AxisCandidateReviewResult exceeded = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 1800, 800, CancellationToken.None);
        Assert.AreEqual("OPENCV_AXIS_AMBIGUOUS", exceeded.ErrorCode);
        Assert.IsEmpty(exceeded.Candidates);
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeAxisCandidateReview.ProcessLinesAsync(
            new CancellingLines(lines, cancelled), 1800, 800, cancelled.Token));
    }

    [TestMethod]
    public async Task EnvelopeBudgetFailureReturnsNoPartialCandidates()
    {
        var lines = new List<GeometryLineCandidate>();
        for (int index = 0; index < 129; index++)
        {
            lines.Add(new($"horizontal-{index}", new(new(200, 200 + 20 * index), new(3900, 570 + 20 * index)), LineCandidateSource.RecordedFixture));
            lines.Add(new($"vertical-{index}", new(new(2600 + 10 * index, 20), new(2600 + 10 * index, 100)), LineCandidateSource.RecordedFixture));
        }
        AxisGeometryDetectionException failure = await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(async () =>
            await new AxisGeometryDetector().DetectAsync(new AxisGeometryRequest(4096, 4096, lines) { MaximumExaminedPairs = 128 }));
        Assert.AreEqual(AxisGeometryBudgetKind.EnvelopeComparisons, failure.BudgetKind);
        AxisCandidateReviewResult result = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 4096, 4096, CancellationToken.None);
        Assert.AreEqual("OPENCV_AXIS_AMBIGUOUS", result.ErrorCode);
        Assert.IsEmpty(result.Candidates);
    }

    [TestMethod]
    public async Task CandidateWorkerRejectsProductionBeforeReadingAndClientHonorsCancellation()
    {
        using var input = new MemoryStream([1, 2, 3]);
        using var output = new MemoryStream();
        Assert.AreEqual(2, await NativeAxisCandidateReview.RunAsync(WorkflowRuntimeEnvironment.Production, input, output));
        Assert.AreEqual(0L, input.Position);
        Assert.AreEqual(0L, output.Length);
        var service = new OpenCvAxisProposalService(Path.GetFullPath("missing-worker.exe"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => service.ReviewCandidatesAsync([1], cancelled.Token));
        _ = StartupArguments.Parse(["--native-axis-candidates-worker"]);
        Assert.ThrowsExactly<ArgumentException>(() => StartupArguments.Parse(["--native-axis-candidates-worker", "--portable-smoke"]));
    }

    [TestMethod]
    public void DividerReturnsAllReviewCandidatesWhileNormalProposalRemainsAmbiguous() => StaTestHost.Run(() =>
    {
        const int width = 800, height = 400;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int x = 100; x <= 700; x++) pixels[300 * width + x] = 0;
        for (int y = 50; y <= 300; y++) { pixels[y * width + 100] = 0; pixels[y * width + 400] = 0; }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width)));
        using var stream = new MemoryStream(); encoder.Save(stream);
        byte[] encoded = stream.ToArray();
        AxisCandidateReviewResult candidates = Pump(NativeAxisCandidateReview.ProcessAsync(encoded, CancellationToken.None));
        Assert.IsNull(candidates.ErrorCode);
        Assert.HasCount(2, candidates.Candidates);
        CollectionAssert.AreEquivalent(DividerLefts, candidates.Candidates.Select(candidate => candidate.Geometry.Left).ToArray());
        Assert.IsTrue(candidates.Candidates.All(candidate => candidate.Geometry.Confidence == 0 &&
            candidate.Geometry.XTickPositions.Count == 0 && candidate.Geometry.YTickPositions.Count == 0));
        RustAxisProposalResult normal = Pump(NativeAxisWorker.ProcessAsync(encoded, CancellationToken.None));
        Assert.IsNull(normal.Geometry);
        Assert.AreEqual("OPENCV_AXIS_AMBIGUOUS", normal.ErrorCode);
    });

    [TestMethod]
    public void TwoPanelsRemainDistinctReviewChoices() => StaTestHost.Run(() =>
    {
        const int width = 800, height = 400;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int x = 50; x <= 350; x++) pixels[300 * width + x] = 0;
        for (int x = 450; x <= 750; x++) pixels[300 * width + x] = 0;
        for (int y = 50; y <= 300; y++) { pixels[y * width + 50] = 0; pixels[y * width + 450] = 0; }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width)));
        using var stream = new MemoryStream(); encoder.Save(stream);
        AxisCandidateReviewResult result = Pump(NativeAxisCandidateReview.ProcessAsync(stream.ToArray(), CancellationToken.None));
        Assert.IsNull(result.ErrorCode);
        Assert.HasCount(2, result.Candidates);
        CollectionAssert.AreEquivalent(PanelLefts, result.Candidates.Select(candidate => candidate.Geometry.Left).ToArray());
    });

    [TestMethod]
    public void CandidateGeometryRejectsRotationAndInvalidBounds()
    {
        PixelPoint[] rotated = [new(10, 90), new(90, 88), new(90, 10), new(10, 10)];
        Assert.IsFalse(NativeAxisCandidateReview.TryGeometry(rotated, 100, 100, out _));
        PixelPoint[] outOfBounds = [new(-1, 90), new(90, 90), new(90, 10), new(-1, 10)];
        Assert.IsFalse(NativeAxisCandidateReview.TryGeometry(outOfBounds, 100, 100, out _));
    }

    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private sealed class CancellingLines(IReadOnlyList<GeometryLineCandidate> inner, CancellationTokenSource cancellation)
        : IReadOnlyList<GeometryLineCandidate>
    {
        public int Count => inner.Count;
        public GeometryLineCandidate this[int index]
        {
            get { if (index == 20) cancellation.Cancel(); return inner[index]; }
        }
        public IEnumerator<GeometryLineCandidate> GetEnumerator() => inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
