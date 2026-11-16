// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections;
using System.Text.Json;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class AxisGeometryIncompleteReviewTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BudgetRefusesAutomaticResultAndPublishesOnlyAdmittedUnrankedGeometry(bool reverse)
    {
        AxisGeometryRequest request = ContactingPairs();
        if (reverse) request = request with { LineCandidates = request.LineCandidates.Reverse().ToArray() };
        var complete = new RankedObserver();
        await new AxisGeometryDetector().DetectAsync(request with { CandidateObserver = complete });
        Assert.IsNotNull(complete.Candidates);
        Assert.HasCount(129, complete.Candidates);
        PlotPolygon[] full = complete.Candidates.Select(candidate => new PlotPolygon(candidate.Intersection,
            candidate.BottomRight, candidate.TopRight, candidate.TopLeft)).ToArray();
        var ranked = new RankedObserver();
        var incomplete = new IncompleteObserver();
        var work = new WorkObserver();
        AxisGeometryDetectionException failure = await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(async () =>
            await new AxisGeometryDetector().DetectAsync(request with
                { MaximumExaminedPairs = 128, CandidateObserver = ranked, IncompleteReviewObserver = incomplete, WorkObserver = work }));
        Assert.AreEqual(AxisGeometryFailureReason.PairBudgetExceeded, failure.Reason);
        Assert.AreEqual(AxisGeometryBudgetKind.ExactPairs, failure.BudgetKind);
        Assert.IsNull(ranked.Candidates);
        Assert.AreEqual(1, incomplete.Calls);
        Assert.IsNotNull(incomplete.Snapshot);
        Assert.IsFalse(incomplete.Snapshot.EnumerationCompleted);
        Assert.AreEqual(failure.Reason, incomplete.Snapshot.FailureReason);
        Assert.AreEqual(failure.BudgetKind, incomplete.Snapshot.BudgetKind);
        Assert.HasCount(128, incomplete.Snapshot.Candidates);
        Assert.IsNotNull(work.Snapshot);
        Assert.AreEqual(128L, work.Snapshot.AdmittedPairs);
        Assert.AreEqual(128L, work.Snapshot.ExactPairAttempts);
        Assert.IsFalse(work.Snapshot.EnumerationCompleted);
        for (int index = 0; index < incomplete.Snapshot.Candidates.Count; index++)
        {
            PlotPolygon polygon = incomplete.Snapshot.Candidates[index].PlotPolygon;
            Assert.IsTrue(full.Contains(polygon), "Only geometry already admitted by unchanged enumeration may be observed.");
            Assert.AreEqual(100d + 10 * (reverse ? 128 - index : index), polygon.BottomLeft.X, 1e-9,
                "The prefix preserves admission order, rather than claiming a complete ranking.");
        }
        var list = (IList<AxisGeometryIncompleteReviewCandidate>)incomplete.Snapshot.Candidates;
        Assert.IsTrue(list.IsReadOnly);
        Assert.ThrowsExactly<NotSupportedException>(() => list[0] = list[1]);
    }

    [TestMethod]
    public async Task SuccessfulDetectionAndRankedObserverAreUnchanged()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder().CleanAxes().Build() with { MaximumExaminedPairs = 128 };
        var expectedRanked = new RankedObserver();
        AxisGeometryResult expected = await new AxisGeometryDetector().DetectAsync(request with { CandidateObserver = expectedRanked });
        var ranked = new RankedObserver(); var incomplete = new IncompleteObserver();
        AxisGeometryResult actual = await new AxisGeometryDetector().DetectAsync(request with
            { CandidateObserver = ranked, IncompleteReviewObserver = incomplete });
        Assert.AreEqual(expected.PlotPolygon, actual.PlotPolygon);
        Assert.AreEqual(expected.Confidence, actual.Confidence);
        Assert.AreEqual(JsonSerializer.Serialize(expected.Uncertainty), JsonSerializer.Serialize(actual.Uncertainty));
        Assert.AreEqual(JsonSerializer.Serialize(expectedRanked.Candidates), JsonSerializer.Serialize(ranked.Candidates));
        Assert.AreEqual(0, incomplete.Calls);
    }

    [TestMethod]
    [DataRow("invalid")]
    [DataRow("cancelled")]
    [DataRow("cancelled_during_input")]
    [DataRow("empty_budget_prefix")]
    public async Task InvalidCancelledAndEmptyPrefixSearchesNeverPublish(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        AxisGeometryRequest request = ContactingPairs();
        if (kind == "invalid") request = request with { ImageWidth = 0 };
        if (kind == "cancelled") cancellation.Cancel();
        if (kind == "cancelled_during_input") request = request with { LineCandidates = new CancellingLines(request.LineCandidates, cancellation) };
        var incomplete = new IncompleteObserver(); var ranked = new RankedObserver();
        request = request with { MaximumExaminedPairs = kind == "empty_budget_prefix" ? 1 : 128,
            IncompleteReviewObserver = incomplete, CandidateObserver = ranked };
        if (kind is "cancelled" or "cancelled_during_input")
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await new AxisGeometryDetector().DetectAsync(request, cancellation.Token));
        else if (kind == "invalid")
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () => await new AxisGeometryDetector().DetectAsync(request));
        else
        {
            AxisGeometryDetectionException failure = await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(async () =>
                await new AxisGeometryDetector().DetectAsync(request));
            Assert.AreEqual(AxisGeometryBudgetKind.EnvelopeComparisons, failure.BudgetKind);
        }
        Assert.AreEqual(0, incomplete.Calls);
        Assert.IsNull(ranked.Candidates);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ObserverErrorsCannotReplaceOriginalBudgetRefusal(bool localCancellation)
    {
        AxisGeometryRequest request = ContactingPairs() with { MaximumExaminedPairs = 128 };
        AxisGeometryDetectionException expected = await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(async () =>
            await new AxisGeometryDetector().DetectAsync(request));
        var incomplete = new IncompleteObserver { AfterDelivery = () =>
            { if (localCancellation) throw new OperationCanceledException(); throw new InvalidOperationException("Owned observer failure"); } };
        AxisGeometryDetectionException actual = await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(async () =>
            await new AxisGeometryDetector().DetectAsync(request with { IncompleteReviewObserver = incomplete }));
        Assert.AreEqual(expected.Message, actual.Message);
        Assert.AreEqual(expected.Reason, actual.Reason);
        Assert.AreEqual(expected.BudgetKind, actual.BudgetKind);
        Assert.AreEqual(1, incomplete.Calls);
    }

    [TestMethod]
    public async Task RequestCancellationByObserverPropagatesRatherThanBecomingBudgetRefusal()
    {
        using var cancellation = new CancellationTokenSource();
        var incomplete = new IncompleteObserver { AfterDelivery = cancellation.Cancel };
        OperationCanceledException failure = await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await new AxisGeometryDetector().DetectAsync(ContactingPairs() with
                { MaximumExaminedPairs = 128, IncompleteReviewObserver = incomplete }, cancellation.Token));
        Assert.AreEqual(cancellation.Token, failure.CancellationToken);
        Assert.AreEqual(1, incomplete.Calls, "The observer itself cancelled only after delivery; consumers must discard on cancellation.");
    }

    private static AxisGeometryRequest ContactingPairs()
    {
        var fixture = new AxisFixtureBuilder(1800, 800).Line(100, 700, 1700, 700);
        for (int index = 0; index < 129; index++) fixture.Line(100 + 10 * index, 700, 100 + 10 * index, 100);
        return fixture.Build();
    }

    private sealed class IncompleteObserver : IAxisGeometryIncompleteReviewObserver
    {
        internal AxisGeometryIncompleteReviewSnapshot? Snapshot { get; private set; }
        internal int Calls { get; private set; }
        internal Action? AfterDelivery { get; init; }
        public void OnSearchInterrupted(AxisGeometryIncompleteReviewSnapshot snapshot) { Snapshot = snapshot; Calls++; AfterDelivery?.Invoke(); }
    }
    private sealed class RankedObserver : IAxisGeometryCandidateObserver
    {
        internal IReadOnlyList<AxisGeometryCandidateSnapshot>? Candidates { get; private set; }
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) => Candidates = candidates;
    }
    private sealed class WorkObserver : IAxisGeometryWorkObserver
    {
        internal AxisGeometryWorkSnapshot? Snapshot { get; private set; }
        public void OnWorkObserved(AxisGeometryWorkSnapshot snapshot) => Snapshot = snapshot;
    }
    private sealed class CancellingLines(IReadOnlyList<GeometryLineCandidate> lines, CancellationTokenSource cancellation) : IReadOnlyList<GeometryLineCandidate>
    {
        public int Count => lines.Count;
        public GeometryLineCandidate this[int index] { get { if (index == 1) cancellation.Cancel(); return lines[index]; } }
        public IEnumerator<GeometryLineCandidate> GetEnumerator() => lines.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
