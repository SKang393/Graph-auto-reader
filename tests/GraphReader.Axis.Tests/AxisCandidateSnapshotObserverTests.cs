// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class AxisCandidateSnapshotObserverTests
{
    [TestMethod]
    public async Task ObserverReceivesCompleteImmutableBestFirstCandidatesWithoutChangingResult()
    {
        AxisGeometryRequest request = MultiCandidateRequest();
        AxisGeometryResult baseline = await new AxisGeometryDetector().DetectAsync(request);
        var observer = new CapturingObserver();

        AxisGeometryResult observed = await new AxisGeometryDetector().DetectAsync(
            request with { CandidateObserver = observer });

        AssertGeometryEquivalent(baseline, observed);
        IReadOnlyList<AxisGeometryCandidateSnapshot> candidates =
            observer.Candidates ?? throw new AssertFailedException("Observer was not called.");
        Assert.IsTrue(candidates.Count >= 2, "Fixture should expose competing axis-pair candidates.");

        for (var index = 0; index < candidates.Count; index++)
        {
            AxisGeometryCandidateSnapshot candidate = candidates[index];
            Assert.AreEqual(index, candidate.Rank);
            Assert.IsTrue(double.IsFinite(candidate.Score));
            Assert.IsTrue(candidate.XSpan > 0d);
            Assert.IsTrue(candidate.YSpan > 0d);
            Assert.IsTrue(candidate.XCoverageFraction is >= 0d and <= 1d);
            Assert.IsTrue(candidate.YCoverageFraction is >= 0d and <= 1d);
            Assert.IsTrue(double.IsFinite(candidate.XRootMeanSquareError));
            Assert.IsTrue(double.IsFinite(candidate.YRootMeanSquareError));
            Assert.IsNotEmpty(candidate.XSupportingCandidateIds);
            Assert.IsNotEmpty(candidate.YSupportingCandidateIds);
            if (index > 0)
            {
                Assert.IsTrue(candidates[index - 1].Score >= candidate.Score);
            }
        }

        AxisGeometryCandidateSnapshot first = candidates[0];
        Assert.AreEqual(baseline.PlotPolygon.BottomLeft, first.Intersection);
        Assert.AreEqual(baseline.PlotPolygon.BottomRight, first.BottomRight);
        Assert.AreEqual(baseline.PlotPolygon.TopRight, first.TopRight);
        Assert.AreEqual(baseline.PlotPolygon.TopLeft, first.TopLeft);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<AxisGeometryCandidateSnapshot>)candidates)[0] = candidates[1]);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<string>)first.XSupportingCandidateIds)[0] = "changed");
    }

    [TestMethod]
    public async Task ObserverExceptionDoesNotChangeGeometryOrEscapeDetection()
    {
        AxisGeometryRequest request = MultiCandidateRequest();
        AxisGeometryResult baseline = await new AxisGeometryDetector().DetectAsync(request);

        AxisGeometryResult observed = await new AxisGeometryDetector().DetectAsync(
            request with { CandidateObserver = new ThrowingObserver() });

        AssertGeometryEquivalent(baseline, observed);
    }

    [TestMethod]
    public async Task ObserverCancellationWithoutRequestCancellationDoesNotChangeResult()
    {
        AxisGeometryRequest request = MultiCandidateRequest();
        AxisGeometryResult baseline = await new AxisGeometryDetector().DetectAsync(request);

        AxisGeometryResult observed = await new AxisGeometryDetector().DetectAsync(
            request with { CandidateObserver = new OperationCanceledObserver() });

        AssertGeometryEquivalent(baseline, observed);
    }

    [TestMethod]
    public async Task ReorderingLineCandidatesDoesNotChangeBestFirstCandidateSnapshots()
    {
        AxisGeometryRequest request = MultiCandidateRequest();
        var forwardObserver = new CapturingObserver();
        var reverseObserver = new CapturingObserver();

        AxisGeometryResult forward = await new AxisGeometryDetector().DetectAsync(
            request with { CandidateObserver = forwardObserver });
        AxisGeometryResult reversed = await new AxisGeometryDetector().DetectAsync(
            request with
            {
                LineCandidates = request.LineCandidates.Reverse().ToArray(),
                CandidateObserver = reverseObserver,
            });

        AssertGeometryEquivalent(forward, reversed);
        string forwardSnapshots = JsonSerializer.Serialize(
            forwardObserver.Candidates ?? throw new AssertFailedException("Forward observer was not called."));
        string reverseSnapshots = JsonSerializer.Serialize(
            reverseObserver.Candidates ?? throw new AssertFailedException("Reverse observer was not called."));
        Assert.AreEqual(forwardSnapshots, reverseSnapshots);
    }

    [TestMethod]
    public async Task ObserverCancellationPropagatesWhenRequestTokenIsCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        AxisGeometryRequest request = MultiCandidateRequest() with
        {
            CandidateObserver = new CancelRequestThenThrowingObserver(cancellation),
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await new AxisGeometryDetector().DetectAsync(request, cancellation.Token));
        Assert.IsTrue(cancellation.IsCancellationRequested);
    }

    [TestMethod]
    public async Task DetectionFailureRemainsUnchangedAndDoesNotNotifyObserver()
    {
        var observer = new CapturingObserver();
        var request = new AxisGeometryRequest(800, 400, Array.Empty<GeometryLineCandidate>())
        {
            CandidateObserver = observer,
        };

        AxisGeometryDetectionException failure = await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(
            async () => await new AxisGeometryDetector().DetectAsync(request));

        Assert.AreEqual("AXIS_GEOMETRY_NOT_FOUND", failure.Code);
        Assert.IsNull(observer.Candidates);
    }

    private static AxisGeometryRequest MultiCandidateRequest() => new AxisFixtureBuilder()
        .CleanAxes()
        .Line(180, 310, 680, 310, id: "alternative-x-axis")
        .Line(180, 310, 180, 70, id: "alternative-y-axis")
        .Build();

    private static void AssertGeometryEquivalent(AxisGeometryResult expected, AxisGeometryResult actual)
    {
        static string Normalize(AxisGeometryResult result) => JsonSerializer.Serialize(
            result with { Diagnostics = result.Diagnostics with { Elapsed = TimeSpan.Zero } });

        Assert.AreEqual(Normalize(expected), Normalize(actual));
    }

    private sealed class CapturingObserver : IAxisGeometryCandidateObserver
    {
        public IReadOnlyList<AxisGeometryCandidateSnapshot>? Candidates { get; private set; }

        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) =>
            Candidates = candidates;
    }

    private sealed class ThrowingObserver : IAxisGeometryCandidateObserver
    {
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) =>
            throw new InvalidOperationException("Synthetic observer failure.");
    }

    private sealed class OperationCanceledObserver : IAxisGeometryCandidateObserver
    {
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) =>
            throw new OperationCanceledException("Synthetic observer cancellation.");
    }

    private sealed class CancelRequestThenThrowingObserver(CancellationTokenSource cancellation)
        : IAxisGeometryCandidateObserver
    {
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }
}
