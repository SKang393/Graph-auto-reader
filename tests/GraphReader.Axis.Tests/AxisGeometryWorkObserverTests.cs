// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections;
using System.Text.Json;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class AxisGeometryWorkObserverTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ObserverPreservesSuccessfulGeometryAndCandidateSnapshots(bool bounded)
    {
        AxisGeometryRequest request = new AxisFixtureBuilder().CleanAxes().Build() with
            { MaximumExaminedPairs = bounded ? 128 : null };
        var detector = new AxisGeometryDetector();
        var originalCandidates = new CandidateObserver();
        var observedCandidates = new CandidateObserver();
        var work = new WorkObserver();
        AxisGeometryResult original = await detector.DetectAsync(request with { CandidateObserver = originalCandidates });
        AxisGeometryResult observed = await detector.DetectAsync(request with { CandidateObserver = observedCandidates, WorkObserver = work });
        Assert.AreEqual(JsonSerializer.Serialize(original with { Diagnostics = original.Diagnostics with { Elapsed = TimeSpan.Zero } }),
            JsonSerializer.Serialize(observed with { Diagnostics = observed.Diagnostics with { Elapsed = TimeSpan.Zero } }));
        Assert.AreEqual(JsonSerializer.Serialize(originalCandidates.Candidates), JsonSerializer.Serialize(observedCandidates.Candidates));
        AxisGeometryWorkSnapshot snapshot = work.OnlySnapshot();
        Assert.AreEqual(2, snapshot.InputLineCount);
        Assert.AreEqual(2, snapshot.AcceptedLineCount);
        Assert.AreEqual(1, snapshot.HorizontalFamilyCount);
        Assert.AreEqual(1, snapshot.VerticalFamilyCount);
        Assert.AreEqual(1, snapshot.HorizontalAxisFamilyCount);
        Assert.AreEqual(1, snapshot.VerticalAxisFamilyCount);
        Assert.AreEqual(bounded ? 1L : 0L, snapshot.EnvelopeComparisons);
        Assert.AreEqual(1L, snapshot.IntersectionsChecked);
        Assert.AreEqual(1L, snapshot.IntersectionsInside);
        Assert.AreEqual(1L, snapshot.ExactPairRequests);
        Assert.AreEqual(1L, snapshot.ExactPairAttempts);
        Assert.AreEqual(1L, snapshot.ScoredPairs);
        Assert.AreEqual(1L, snapshot.AdmittedPairs);
        Assert.IsTrue(snapshot.EnumerationCompleted);
        Assert.IsFalse(snapshot.Cancelled);
        Assert.IsNull(snapshot.FailureReason);
        AssertInvariants(snapshot);
    }

    [TestMethod]
    public async Task ExactBudgetReportsOverflowRequestWithoutAttemptingItsContact()
    {
        var fixture = new AxisFixtureBuilder(1800, 800).Line(100, 700, 1700, 700);
        for (int index = 0; index < 129; index++) fixture.Line(100 + 10 * index, 700, 100 + 10 * index, 100);
        var work = new WorkObserver();
        var candidates = new CandidateObserver();
        AxisGeometryDetectionException failure = await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(async () =>
            await new AxisGeometryDetector().DetectAsync(fixture.Build() with
                { MaximumExaminedPairs = 128, WorkObserver = work, CandidateObserver = candidates }));
        AxisGeometryWorkSnapshot snapshot = work.OnlySnapshot();
        Assert.AreEqual(AxisGeometryFailureReason.PairBudgetExceeded, failure.Reason);
        Assert.AreEqual(failure.Reason, snapshot.FailureReason);
        Assert.AreEqual(AxisGeometryBudgetKind.ExactPairs, snapshot.BudgetKind);
        Assert.AreEqual(129L, snapshot.ExactPairRequests);
        Assert.AreEqual(128L, snapshot.ExactPairAttempts);
        Assert.AreEqual(128L, snapshot.ScoredPairs);
        Assert.AreEqual(128L, snapshot.AdmittedPairs);
        Assert.IsFalse(snapshot.EnumerationCompleted);
        Assert.IsNull(candidates.Candidates);
        AssertInvariants(snapshot);
    }

    [TestMethod]
    public async Task EnvelopeBudgetRetainsIndexPrefixWorkWithoutPublishingCandidates()
    {
        var fixture = new AxisFixtureBuilder(1000, 800).CleanAxes(100, 100, 700, 650);
        for (int index = 0; index < 4; index++)
        {
            fixture.Line(760, 40 + 30 * index, 780, 40 + 30 * index);
            fixture.Line(820 + 12 * index, 500, 820 + 12 * index, 520);
        }
        var work = new WorkObserver();
        var candidates = new CandidateObserver();
        await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(async () =>
            await new AxisGeometryDetector().DetectAsync(fixture.Build() with
                { MaximumExaminedPairs = 4, WorkObserver = work, CandidateObserver = candidates }));
        AxisGeometryWorkSnapshot snapshot = work.OnlySnapshot();
        Assert.AreEqual(AxisGeometryFailureReason.PairBudgetExceeded, snapshot.FailureReason);
        Assert.AreEqual(AxisGeometryBudgetKind.EnvelopeComparisons, snapshot.BudgetKind);
        Assert.AreEqual(17L, snapshot.EnvelopeComparisons);
        Assert.IsFalse(snapshot.EnumerationCompleted);
        Assert.AreEqual(snapshot.ExactPairRequests, snapshot.ExactPairAttempts);
        Assert.IsNull(candidates.Candidates);
        AssertInvariants(snapshot);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RawAndFrameClippedZeroExtentsHaveSeparateCounters(bool clipped)
    {
        AxisGeometryRequest request = clipped
            ? new AxisFixtureBuilder().Line(100, 100, 700, 40).Line(100, 100, 100, 0).Build()
            : new AxisFixtureBuilder().Line(100, 300, 500, 300).Line(500, 300, 500, 50).Build();
        var work = new WorkObserver();
        await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(async () =>
            await new AxisGeometryDetector().DetectAsync(request with { MaximumExaminedPairs = 128, WorkObserver = work }));
        AxisGeometryWorkSnapshot snapshot = work.OnlySnapshot();
        Assert.AreEqual(clipped ? 0L : 1L, snapshot.RawNonpositiveExtents);
        Assert.AreEqual(clipped ? 1L : 0L, snapshot.ClippedNonpositiveExtents);
        Assert.AreEqual(0L, snapshot.ExactPairRequests);
        Assert.AreEqual(0L, snapshot.ExactPairAttempts);
        Assert.AreEqual(AxisGeometryFailureReason.NoIntersectingPair, snapshot.FailureReason);
        Assert.IsTrue(snapshot.EnumerationCompleted, "An exhaustive empty search differs from a failed prefix.");
        AssertInvariants(snapshot);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ContactRejectionsCountOnlyPredicatesActuallyExecuted(bool horizontal)
    {
        AxisGeometryRequest request = horizontal
            ? new AxisFixtureBuilder().Line(100, 300, 300, 300).Line(500, 300, 500, 50).Build()
            : new AxisFixtureBuilder().Line(100, 300, 700, 300).Line(100, 50, 100, 200).Build();
        var work = new WorkObserver();
        await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(async () =>
            await new AxisGeometryDetector().DetectAsync(request with { WorkObserver = work }));
        AxisGeometryWorkSnapshot snapshot = work.OnlySnapshot();
        Assert.AreEqual(1L, snapshot.ExactPairAttempts);
        Assert.AreEqual(horizontal ? 1L : 0L, snapshot.HorizontalContactRejections);
        Assert.AreEqual(horizontal ? 0L : 1L, snapshot.VerticalContactRejections);
        Assert.AreEqual(0L, snapshot.ScoredPairs);
        Assert.IsTrue(snapshot.EnumerationCompleted);
        AssertInvariants(snapshot);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationPublishesOneIncompleteSnapshot(bool duringInput)
    {
        using var cancellation = new CancellationTokenSource();
        AxisGeometryRequest request = new AxisFixtureBuilder().CleanAxes().Line(200, 350, 700, 350).Build();
        if (duringInput) request = request with { LineCandidates = new CancellingLines(request.LineCandidates, cancellation) };
        else cancellation.Cancel();
        var work = new WorkObserver();
        var candidates = new CandidateObserver();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await new AxisGeometryDetector().DetectAsync(request with { WorkObserver = work, CandidateObserver = candidates }, cancellation.Token));
        AxisGeometryWorkSnapshot snapshot = work.OnlySnapshot();
        Assert.IsTrue(snapshot.Cancelled);
        Assert.IsFalse(snapshot.EnumerationCompleted);
        Assert.AreEqual(duringInput ? 2 : 0, snapshot.AcceptedLineCount);
        Assert.AreEqual(0L, snapshot.ExactPairAttempts);
        Assert.IsNull(candidates.Candidates);
        AssertInvariants(snapshot);
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("failure")]
    [DataRow("cancel")]
    public async Task ThrowingObserverCannotReplaceResultOrOriginalFailure(string outcome)
    {
        var observer = new WorkObserver { Throw = true };
        AxisGeometryRequest request = (outcome == "failure" ? new AxisFixtureBuilder() : new AxisFixtureBuilder().CleanAxes()).Build()
            with { WorkObserver = observer };
        var detector = new AxisGeometryDetector();
        if (outcome == "cancel")
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await detector.DetectAsync(request, new(canceled: true)));
        else if (outcome == "failure")
            await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(async () => await detector.DetectAsync(request));
        else Assert.AreEqual(new PixelPoint(100, 300), (await detector.DetectAsync(request)).PlotPolygon.BottomLeft);
        Assert.AreEqual(1, observer.Calls);
    }

    private static void AssertInvariants(AxisGeometryWorkSnapshot snapshot)
    {
        Assert.IsTrue(snapshot.AcceptedLineCount <= snapshot.InputLineCount);
        Assert.IsTrue(snapshot.IntersectionsInside <= snapshot.IntersectionsChecked);
        Assert.IsTrue(snapshot.ExactPairAttempts <= snapshot.ExactPairRequests);
        Assert.AreEqual(snapshot.ExactPairAttempts,
            snapshot.HorizontalContactRejections + snapshot.VerticalContactRejections + snapshot.ScoredPairs);
        Assert.IsTrue(snapshot.AdmittedPairs <= snapshot.ScoredPairs);
        Assert.IsTrue(snapshot.Cancelled || snapshot.EnumerationCompleted || snapshot.FailureReason is not null);
    }

    private sealed class WorkObserver : IAxisGeometryWorkObserver
    {
        internal int Calls { get; private set; }
        internal bool Throw { get; init; }
        private AxisGeometryWorkSnapshot? snapshot;
        public void OnWorkObserved(AxisGeometryWorkSnapshot value)
        {
            Calls++;
            snapshot = value;
            if (Throw) throw new InvalidOperationException("Owned observer failure.");
        }
        internal AxisGeometryWorkSnapshot OnlySnapshot()
        {
            Assert.AreEqual(1, Calls);
            Assert.IsNotNull(snapshot);
            return snapshot;
        }
    }

    private sealed class CandidateObserver : IAxisGeometryCandidateObserver
    {
        internal IReadOnlyList<AxisGeometryCandidateSnapshot>? Candidates { get; private set; }
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) => Candidates = candidates;
    }

    private sealed class CancellingLines(IReadOnlyList<GeometryLineCandidate> lines, CancellationTokenSource cancellation)
        : IReadOnlyList<GeometryLineCandidate>
    {
        public int Count => lines.Count;
        public GeometryLineCandidate this[int index]
        {
            get { if (index == 1) cancellation.Cancel(); return lines[index]; }
        }
        public IEnumerator<GeometryLineCandidate> GetEnumerator() => lines.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
