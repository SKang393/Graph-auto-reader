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
            .Select(index => new AxisReviewCandidate(index, new RustAxisGeometry(index, 90, 500, 10, 0))).ToArray();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new AxisCandidateReviewResult(candidates, null));
        Assert.IsLessThanOrEqualTo(NativeAxisCandidateReview.MaximumResponseBytes, bytes.Length);
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        using var stream = new MemoryStream(); stream.Write(prefix); stream.Write(bytes); stream.Position = 0;
        AxisCandidateReviewResult? result = await OpenCvAxisProposalService.ReadCandidateResponseAsync(stream, CancellationToken.None);
        Assert.IsTrue(OpenCvAxisProposalService.ValidCandidates(result, 600, 100));
        Assert.HasCount(128, result!.Candidates);
        for (int i = 0; i < candidates.Length; i++)
        {
            AxisReviewCandidate candidate = result.Candidates[i];
            Assert.AreEqual(i, candidate.Index);
            Assert.AreEqual(i, candidate.Geometry.Left);
            Assert.AreEqual(90, candidate.Geometry.Bottom);
            Assert.AreEqual(500, candidate.Geometry.Right);
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
        NativeAxisCandidateReviewObservation? budget = null;
        AxisCandidateReviewResult exceeded = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 1800, 800,
            value => budget = value, CancellationToken.None);
        Assert.AreEqual("OPENCV_AXIS_AMBIGUOUS", exceeded.ErrorCode);
        Assert.IsEmpty(exceeded.Candidates);
        Assert.IsNotNull(budget);
        Assert.AreEqual("enumeration", budget.Stage);
        Assert.AreEqual("native_pair_budget_exceeded", budget.FailureDetail);
        Assert.IsFalse(budget.EnumerationCompleted);
        Assert.AreEqual(0, budget.RawSnapshotCount);
        Assert.AreEqual(0, budget.CheckedCount);
        Assert.IsNotNull(budget.Work);
        Assert.AreEqual(AxisGeometryBudgetKind.ExactPairs, budget.Work.BudgetKind);
        Assert.AreEqual(129L, budget.Work.ExactPairRequests);
        Assert.AreEqual(128L, budget.Work.ExactPairAttempts);
        Assert.AreEqual(128L, budget.Work.ScoredPairs);
        Assert.AreEqual(128L, budget.Work.AdmittedPairs);
        using var cancelled = new CancellationTokenSource();
        NativeAxisCandidateReviewObservation? cancellation = null;
        int cancellationNotifications = 0;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeAxisCandidateReview.ProcessLinesAsync(
            new CancellingLines(lines, cancelled), 1800, 800,
            value => { cancellation = value; cancellationNotifications++; }, cancelled.Token));
        Assert.AreEqual(1, cancellationNotifications);
        Assert.IsNotNull(cancellation);
        Assert.IsTrue(cancellation.Cancelled);
        Assert.IsFalse(cancellation.EnumerationCompleted);
        Assert.AreEqual("cancelled", cancellation.FailureDetail);
        Assert.IsNotNull(cancellation.Work);
        Assert.IsTrue(cancellation.Work.Cancelled);
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
        NativeAxisCandidateReviewObservation? observation = null;
        AxisCandidateReviewResult result = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 4096, 4096,
            value => observation = value, CancellationToken.None);
        Assert.AreEqual("OPENCV_AXIS_AMBIGUOUS", result.ErrorCode);
        Assert.IsEmpty(result.Candidates);
        Assert.IsNotNull(observation);
        Assert.IsFalse(observation.EnumerationCompleted);
        Assert.AreEqual("native_envelope_budget_exceeded", observation.FailureDetail);
        Assert.IsNotNull(observation.Work);
        Assert.AreEqual(AxisGeometryBudgetKind.EnvelopeComparisons, observation.Work.BudgetKind);
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
    public void DividerReturnsAllReviewCandidatesAndNormalTopRankedReviewProposal() => StaTestHost.Run(() =>
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
        NativeAxisCandidateReviewObservation? observation = null;
        AxisCandidateReviewResult observed = Pump(NativeAxisCandidateReview.ProcessAsync(encoded,
            value => observation = value, CancellationToken.None));
        Assert.AreEqual(JsonSerializer.Serialize(candidates), JsonSerializer.Serialize(observed));
        Assert.IsNotNull(observation);
        Assert.AreEqual("complete", observation.Stage);
        Assert.IsTrue(observation.EnumerationCompleted);
        Assert.AreEqual(2, observation.RawSnapshotCount);
        Assert.AreEqual(2, observation.EligibleCount);
        Assert.IsNull(candidates.ErrorCode);
        Assert.HasCount(2, candidates.Candidates);
        CollectionAssert.AreEquivalent(DividerLefts, candidates.Candidates.Select(candidate => candidate.Geometry.Left).ToArray());
        Assert.IsTrue(candidates.Candidates.All(candidate => candidate.Geometry.Confidence == 0 &&
            candidate.Geometry.XTickPositions.Count == 0 && candidate.Geometry.YTickPositions.Count == 0));
        RustAxisProposalResult normal = Pump(NativeAxisWorker.ProcessAsync(encoded, CancellationToken.None));
        Assert.IsNull(normal.ErrorCode);
        Assert.IsNotNull(normal.Geometry);
        Assert.AreEqual("opencv-review", normal.Provider);
        CollectionAssert.Contains(normal.ReviewReasons.ToArray(), "native_multiple_ranked_candidates");
        Assert.AreEqual(candidates.Candidates[0].Geometry.Left, normal.Geometry.Left);
        Assert.AreEqual(candidates.Candidates[0].Geometry.Right, normal.Geometry.Right);
        Assert.AreEqual(candidates.Candidates[0].Geometry.Top, normal.Geometry.Top);
        Assert.AreEqual(candidates.Candidates[0].Geometry.Bottom, normal.Geometry.Bottom);
        Assert.IsEmpty(normal.Geometry.XTickPositions);
        Assert.IsEmpty(normal.Geometry.YTickPositions);
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
        PixelPoint[] nonfinite = [new(double.NaN, 90), new(90, 90), new(90, 10), new(10, 10)];
        Assert.IsFalse(NativeAxisCandidateReview.TryGeometry(nonfinite, 100, 100, out _));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task CompleteMixedSnapshotRetainsEveryRepresentableChoice(bool reverse, bool divider)
    {
        List<GeometryLineCandidate> lines =
        [
            new("clean-x", new(new(100, 300), new(350, 300)), LineCandidateSource.RecordedFixture),
            new("clean-y", new(new(100, 300), new(100, 50)), LineCandidateSource.RecordedFixture),
            new("tilted-x", new(new(500, 300), new(750, 290)), LineCandidateSource.RecordedFixture),
            new("tilted-y", new(new(500, 300), new(490, 50)), LineCandidateSource.RecordedFixture),
        ];
        if (divider) lines.Add(new("clean-divider", new(new(200, 300), new(200, 50)), LineCandidateSource.RecordedFixture));
        if (reverse) lines.Reverse();
        var observer = new SnapshotObserver();
        _ = await new AxisGeometryDetector().DetectAsync(new AxisGeometryRequest(800, 400, lines)
            { CandidateObserver = observer, MaximumExaminedPairs = 128 });
        Assert.IsNotNull(observer.Snapshots);
        Assert.HasCount(divider ? 3 : 2, observer.Snapshots);
        RustAxisGeometry[] eligible = observer.Snapshots.Select(snapshot =>
            NativeAxisCandidateReview.TryGeometry(
                [snapshot.Intersection, snapshot.BottomRight, snapshot.TopRight, snapshot.TopLeft],
                800, 400, out RustAxisGeometry? geometry) ? geometry : null)
            .OfType<RustAxisGeometry>().ToArray();
        Assert.HasCount(divider ? 2 : 1, eligible);
        Assert.IsTrue(eligible.Contains(new RustAxisGeometry(100, 300, 350, 50, 0)));

        AxisCandidateReviewResult result = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 800, 400, CancellationToken.None);
        Assert.IsNull(result.ErrorCode);
        CollectionAssert.AreEqual(eligible, result.Candidates.Select(candidate => candidate.Geometry).ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(0, eligible.Length).ToArray(), result.Candidates.Select(candidate => candidate.Index).ToArray());
        Assert.IsTrue(OpenCvAxisProposalService.ValidCandidates(result, 800, 400));
        NativeAxisCandidateReviewObservation? observation = null;
        int notifications = 0;
        AxisCandidateReviewResult observed = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 800, 400,
            value => { observation = value; notifications++; }, CancellationToken.None);
        Assert.AreEqual(JsonSerializer.Serialize(result), JsonSerializer.Serialize(observed));
        Assert.AreEqual(1, notifications);
        Assert.IsNotNull(observation);
        Assert.AreEqual("complete", observation.Stage);
        Assert.IsTrue(observation.EnumerationCompleted);
        Assert.IsFalse(observation.Cancelled);
        Assert.AreEqual(observer.Snapshots.Count, observation.RawSnapshotCount);
        Assert.AreEqual(observation.RawSnapshotCount, observation.CheckedCount);
        Assert.AreEqual(eligible.Length, observation.EligibleCount);
        Assert.AreEqual(1, observation.RotatedCount);
        Assert.AreEqual(0, observation.RoundedExclusionCount);
        Assert.AreEqual(0, observation.DuplicateExclusionCount);
        Assert.IsNull(observation.FailureDetail);
        Assert.IsNull(observation.HardGeometryRejection);
        Assert.IsNotNull(observation.Work);
    }

    [TestMethod]
    public async Task CompleteAllRotatedSnapshotReturnsNoCandidates()
    {
        GeometryLineCandidate[] lines =
        [
            new("tilted-x", new(new(500, 300), new(750, 290)), LineCandidateSource.RecordedFixture),
            new("tilted-y", new(new(500, 300), new(490, 50)), LineCandidateSource.RecordedFixture),
        ];
        var observer = new SnapshotObserver();
        _ = await new AxisGeometryDetector().DetectAsync(new AxisGeometryRequest(800, 400, lines)
            { CandidateObserver = observer, MaximumExaminedPairs = 128 });
        Assert.IsNotNull(observer.Snapshots);
        Assert.HasCount(1, observer.Snapshots);
        AxisGeometryCandidateSnapshot snapshot = observer.Snapshots[0];
        Assert.AreEqual("native_rotated_geometry", NativeAxisWorker.GeometryRejection(
            [snapshot.Intersection, snapshot.BottomRight, snapshot.TopRight, snapshot.TopLeft], 1, 800, 400));
        NativeAxisCandidateReviewObservation? observation = null;
        AxisCandidateReviewResult result = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 800, 400,
            value => observation = value, CancellationToken.None);
        Assert.AreEqual("OPENCV_AXIS_AMBIGUOUS", result.ErrorCode);
        Assert.IsEmpty(result.Candidates);
        Assert.IsNotNull(observation);
        Assert.IsTrue(observation.EnumerationCompleted);
        Assert.AreEqual("geometry_eligibility", observation.Stage);
        Assert.AreEqual("native_no_eligible_candidates", observation.FailureDetail);
        Assert.AreEqual(1, observation.RawSnapshotCount);
        Assert.AreEqual(1, observation.CheckedCount);
        Assert.AreEqual(1, observation.RotatedCount);
        Assert.AreEqual(0, observation.RoundedExclusionCount);
        Assert.AreEqual(0, observation.EligibleCount);
        Assert.IsNull(observation.HardGeometryRejection);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RoundedExclusionRetainsAllSupportedCandidatesRegardlessOfRank(bool roundedFirst, bool reverse)
    {
        int cleanLeft = roundedFirst ? 400 : 100;
        int cleanBottom = roundedFirst ? 200 : 300;
        double tinyLeft = roundedFirst ? 100.5001 : 500.5001;
        int tinyBottom = roundedFirst ? 350 : 200;
        GeometryLineCandidate[] lines =
        [
            new("clean-x", new(new(cleanLeft, cleanBottom), new(cleanLeft + 250, cleanBottom)), LineCandidateSource.RecordedFixture),
            new("clean-y", new(new(cleanLeft, cleanBottom), new(cleanLeft, 50)), LineCandidateSource.RecordedFixture),
            new("clean-divider", new(new(cleanLeft + 100, cleanBottom), new(cleanLeft + 100, 50)), LineCandidateSource.RecordedFixture),
            new("tiny-x", new(new(tinyLeft, tinyBottom), new(tinyLeft + .9998, tinyBottom + .03)), LineCandidateSource.RecordedFixture),
            new("tiny-y", new(new(tinyLeft, tinyBottom), new(tinyLeft, 50)), LineCandidateSource.RecordedFixture),
        ];
        if (reverse) Array.Reverse(lines);
        var observer = new SnapshotObserver();
        _ = await new AxisGeometryDetector().DetectAsync(new AxisGeometryRequest(800, 400, lines)
            { CandidateObserver = observer, MaximumExaminedPairs = 128 });
        Assert.IsNotNull(observer.Snapshots);
        Assert.HasCount(3, observer.Snapshots);
        int roundedRank = roundedFirst ? 0 : 2;
        AxisGeometryCandidateSnapshot rounded = observer.Snapshots[roundedRank];
        PixelPoint[] roundedCorners = [rounded.Intersection, rounded.BottomRight, rounded.TopRight, rounded.TopLeft];
        Assert.IsNull(NativeAxisWorker.GeometryRejection(roundedCorners, 1, 800, 400));
        Assert.IsFalse(NativeAxisCandidateReview.TryGeometry(roundedCorners, 800, 400, out _));
        RustAxisGeometry[] eligible = observer.Snapshots.Select(snapshot =>
            NativeAxisCandidateReview.TryGeometry(
                [snapshot.Intersection, snapshot.BottomRight, snapshot.TopRight, snapshot.TopLeft],
                800, 400, out RustAxisGeometry? geometry) ? geometry : null)
            .OfType<RustAxisGeometry>().ToArray();
        Assert.HasCount(2, eligible);

        AxisCandidateReviewResult result = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 800, 400, CancellationToken.None);
        Assert.IsNull(result.ErrorCode);
        CollectionAssert.AreEqual(eligible, result.Candidates.Select(candidate => candidate.Geometry).ToArray());
        Assert.AreEqual(0, result.Candidates[0].Index);
        Assert.AreEqual(1, result.Candidates[1].Index);
        Assert.IsTrue(OpenCvAxisProposalService.ValidCandidates(result, 800, 400));
        NativeAxisCandidateReviewObservation? observation = null;
        int notifications = 0;
        AxisCandidateReviewResult observed = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 800, 400,
            value => { observation = value; notifications++; }, CancellationToken.None);
        Assert.AreEqual(JsonSerializer.Serialize(result), JsonSerializer.Serialize(observed));
        Assert.AreEqual(1, notifications);
        Assert.IsNotNull(observation);
        Assert.AreEqual("complete", observation.Stage);
        Assert.IsTrue(observation.EnumerationCompleted);
        Assert.AreEqual(3, observation.RawSnapshotCount);
        Assert.AreEqual(3, observation.CheckedCount);
        Assert.AreEqual(2, observation.EligibleCount);
        Assert.AreEqual(0, observation.RotatedCount);
        Assert.AreEqual(1, observation.RoundedExclusionCount);
        Assert.AreEqual(0, observation.DuplicateExclusionCount);
        Assert.IsNull(observation.HardGeometryRejection);
        Assert.IsNull(observation.FailureDetail);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompleteSubpixelHypothesesReturnFirstRankedDistinctIntegerBoxes(bool reverse)
    {
        GeometryLineCandidate[] lines =
        [
            new("baseline", new(new(100, 300), new(700, 300)), LineCandidateSource.RecordedFixture),
            new("upper-support", new(new(100, 296.1), new(101, 296.1)), LineCandidateSource.RecordedFixture),
            new("lower-support", new(new(100, 303.9), new(101, 303.9)), LineCandidateSource.RecordedFixture),
            new("left-axis", new(new(100, 300), new(100, 50)), LineCandidateSource.RecordedFixture),
            new("divider", new(new(400, 300), new(400, 50)), LineCandidateSource.RecordedFixture),
        ];
        if (reverse) Array.Reverse(lines);
        var observer = new SnapshotObserver();
        _ = await new AxisGeometryDetector().DetectAsync(new AxisGeometryRequest(800, 400, lines)
            { CandidateObserver = observer, MaximumExaminedPairs = 128 });
        Assert.IsNotNull(observer.Snapshots);
        Assert.HasCount(6, observer.Snapshots);
        RustAxisGeometry[] geometries = observer.Snapshots.Select(snapshot =>
        {
            Assert.IsTrue(NativeAxisCandidateReview.TryGeometry(
                [snapshot.Intersection, snapshot.BottomRight, snapshot.TopRight, snapshot.TopLeft],
                800, 400, out RustAxisGeometry? geometry));
            Assert.IsNotNull(geometry);
            return geometry;
        }).ToArray();
        RustAxisGeometry[] expected = geometries.GroupBy(geometry =>
            (geometry.Left, geometry.Bottom, geometry.Right, geometry.Top)).Select(group => group.First()).ToArray();
        Assert.HasCount(2, expected);
        CollectionAssert.AreEquivalent(DividerLefts, expected.Select(geometry => geometry.Left).ToArray());
        Assert.IsTrue(expected.All(geometry => geometry.Bottom == 300 && geometry.Right == 700 && geometry.Top == 50));
        var unfiltered = new AxisCandidateReviewResult(geometries.Select((geometry, index) => new AxisReviewCandidate(index, geometry)).ToArray(), null);
        Assert.IsFalse(OpenCvAxisProposalService.ValidCandidates(unfiltered, 800, 400));

        AxisCandidateReviewResult result = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 800, 400, CancellationToken.None);
        Assert.IsNull(result.ErrorCode);
        Assert.IsTrue(OpenCvAxisProposalService.ValidCandidates(result, 800, 400));
        CollectionAssert.AreEqual(expected, result.Candidates.Select(candidate => candidate.Geometry).ToArray());
        Assert.AreEqual(0, result.Candidates[0].Index);
        Assert.AreEqual(1, result.Candidates[1].Index);
        NativeAxisCandidateReviewObservation? observation = null;
        AxisCandidateReviewResult observed = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 800, 400,
            value => observation = value, CancellationToken.None);
        Assert.AreEqual(JsonSerializer.Serialize(result), JsonSerializer.Serialize(observed));
        Assert.IsNotNull(observation);
        Assert.AreEqual("complete", observation.Stage);
        Assert.IsTrue(observation.EnumerationCompleted);
        Assert.AreEqual(6, observation.RawSnapshotCount);
        Assert.AreEqual(6, observation.CheckedCount);
        Assert.AreEqual(2, observation.EligibleCount);
        Assert.AreEqual(0, observation.RotatedCount);
        Assert.AreEqual(0, observation.RoundedExclusionCount);
        Assert.AreEqual(4, observation.DuplicateExclusionCount);
        Assert.IsNull(observation.HardGeometryRejection);
        Assert.IsNull(observation.FailureDetail);
    }

    [TestMethod]
    public async Task CompleteAllRoundedSnapshotReturnsNoCandidates()
    {
        GeometryLineCandidate[] lines =
        [
            new("tiny-x", new(new(100.5001, 200), new(101.4999, 200.03)), LineCandidateSource.RecordedFixture),
            new("y", new(new(100.5001, 200), new(100.5001, 50)), LineCandidateSource.RecordedFixture),
        ];
        NativeAxisCandidateReviewObservation? observation = null;
        AxisCandidateReviewResult original = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 400, 400, CancellationToken.None);
        AxisCandidateReviewResult observed = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 400, 400,
            value => observation = value, CancellationToken.None);
        Assert.AreEqual(JsonSerializer.Serialize(original), JsonSerializer.Serialize(observed));
        Assert.AreEqual("OPENCV_AXIS_AMBIGUOUS", observed.ErrorCode);
        Assert.IsEmpty(observed.Candidates);
        Assert.IsNotNull(observation);
        Assert.IsTrue(observation.EnumerationCompleted);
        Assert.AreEqual("geometry_eligibility", observation.Stage);
        Assert.AreEqual(1, observation.RawSnapshotCount);
        Assert.AreEqual(1, observation.CheckedCount);
        Assert.AreEqual(0, observation.EligibleCount);
        Assert.AreEqual(0, observation.RotatedCount);
        Assert.AreEqual(1, observation.RoundedExclusionCount);
        Assert.IsNull(observation.HardGeometryRejection);
        Assert.AreEqual("native_no_eligible_candidates", observation.FailureDetail);
    }

    [TestMethod]
    public async Task EmptyEvidenceAndUnexpectedErrorsRemainDistinct()
    {
        NativeAxisCandidateReviewObservation? empty = null;
        AxisCandidateReviewResult result = await NativeAxisCandidateReview.ProcessLinesAsync([], 800, 400,
            value => empty = value, CancellationToken.None);
        Assert.AreEqual("OPENCV_AXIS_AMBIGUOUS", result.ErrorCode);
        Assert.IsNotNull(empty);
        Assert.AreEqual("enumeration", empty.Stage);
        Assert.AreEqual("native_no_line_evidence", empty.FailureDetail);
        Assert.IsFalse(empty.EnumerationCompleted);
        Assert.AreEqual(0, empty.RawSnapshotCount);
        NativeAxisCandidateReviewObservation? invalid = null;
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => NativeAxisCandidateReview.ProcessLinesAsync([], 0, 400,
            value => invalid = value, CancellationToken.None));
        Assert.IsNotNull(invalid);
        Assert.AreEqual("native_enumeration_failed", invalid.FailureDetail);
        Assert.IsFalse(invalid.Cancelled);
        Assert.IsFalse(invalid.EnumerationCompleted);
    }

    [TestMethod]
    public async Task ThrowingDiagnosticObserverCannotChangeResultOrCancellation()
    {
        GeometryLineCandidate[] lines =
        [
            new("x", new(new(100, 300), new(350, 300)), LineCandidateSource.RecordedFixture),
            new("y", new(new(100, 300), new(100, 50)), LineCandidateSource.RecordedFixture),
        ];
        AxisCandidateReviewResult original = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 800, 400, CancellationToken.None);
        AxisCandidateReviewResult observed = await NativeAxisCandidateReview.ProcessLinesAsync(lines, 800, 400,
            _ => throw new InvalidOperationException("Owned diagnostic failure"), CancellationToken.None);
        Assert.AreEqual(JsonSerializer.Serialize(original), JsonSerializer.Serialize(observed));
        AxisCandidateReviewResult failure = await NativeAxisCandidateReview.ProcessLinesAsync([], 800, 400, CancellationToken.None);
        AxisCandidateReviewResult observedFailure = await NativeAxisCandidateReview.ProcessLinesAsync([], 800, 400,
            _ => throw new OperationCanceledException("Owned diagnostic failure"), CancellationToken.None);
        Assert.AreEqual(JsonSerializer.Serialize(failure), JsonSerializer.Serialize(observedFailure));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeAxisCandidateReview.ProcessLinesAsync(lines, 800, 400,
            _ => throw new InvalidOperationException("Owned diagnostic failure"), cancelled.Token));
    }

    private sealed class SnapshotObserver : IAxisGeometryCandidateObserver
    {
        internal IReadOnlyList<AxisGeometryCandidateSnapshot>? Snapshots { get; private set; }
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) => Snapshots = candidates;
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
