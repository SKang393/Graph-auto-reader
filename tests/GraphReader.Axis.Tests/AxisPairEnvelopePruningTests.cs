// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class AxisPairEnvelopePruningTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DetachedClutterRetainsTheCompleteUnlimitedCandidateSet(bool reverse)
    {
        AxisGeometryRequest request = DetachedClutter(12);
        if (reverse) request = request with { LineCandidates = request.LineCandidates.Reverse().ToArray() };
        var unlimited = new Observer();
        AxisGeometryResult expected = await new AxisGeometryDetector().DetectAsync(request with { CandidateObserver = unlimited });
        Assert.HasCount(1, unlimited.Candidates!);
        Assert.AreEqual(new PixelPoint(100, 650), expected.PlotPolygon.BottomLeft);
        Assert.AreEqual(new PixelPoint(700, 100), expected.PlotPolygon.TopRight);

        var bounded = new Observer();
        AxisGeometryResult actual = await new AxisGeometryDetector().DetectAsync(
            request with { MaximumExaminedPairs = 128, CandidateObserver = bounded });

        AssertParity(expected, actual, unlimited, bounded);
    }

    [TestMethod]
    [DataRow("divider", false)]
    [DataRow("divider", true)]
    [DataRow("grid", false)]
    [DataRow("grid", true)]
    [DataRow("slanted", false)]
    [DataRow("slanted", true)]
    [DataRow("offset_members", false)]
    [DataRow("offset_members", true)]
    public async Task ExistingContactCandidatesAndRankingRemainIdentical(string style, bool reverse)
    {
        AxisGeometryRequest request = style switch
        {
            "divider" => new AxisFixtureBuilder().CleanAxes().Line(400, 300, 400, 50).Build(),
            "grid" => new AxisFixtureBuilder().CleanAxes().VerticalGrid(250).VerticalGrid(400).VerticalGrid(550)
                .HorizontalGrid(100).HorizontalGrid(175).HorizontalGrid(250).Build(),
            "slanted" => new AxisFixtureBuilder().Line(100, 300, 700, 280).Line(100, 300, 90, 20).Build(),
            "offset_members" => new AxisFixtureBuilder().Line(100, 300, 400, 300).Line(400, 302, 700, 302)
                .Line(100, 300, 100, 50).Build(),
            _ => throw new AssertFailedException("Unknown owned control."),
        };
        if (reverse) request = request with { LineCandidates = request.LineCandidates.Reverse().ToArray() };
        var unlimited = new Observer();
        var bounded = new Observer();
        var detector = new AxisGeometryDetector();
        AxisGeometryResult expected = await detector.DetectAsync(request with { CandidateObserver = unlimited });
        AxisGeometryResult actual = await detector.DetectAsync(request with { CandidateObserver = bounded, MaximumExaminedPairs = 128 });
        AssertParity(expected, actual, unlimited, bounded);
    }

    [TestMethod]
    [DataRow(94d, true)]
    [DataRow(94.01d, true)]
    [DataRow(93.99d, false)]
    public async Task ExistingFiniteMemberContactToleranceIsNotTightened(double verticalX, bool contacts)
    {
        AxisGeometryRequest request = new AxisFixtureBuilder().Line(100, 300, 700, 300)
            .Line(verticalX, 300, verticalX, 50).Build();
        var detector = new AxisGeometryDetector();
        if (!contacts)
        {
            AxisGeometryDetectionException expected = await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(
                async () => await detector.DetectAsync(request));
            AxisGeometryDetectionException actual = await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(
                async () => await detector.DetectAsync(request with { MaximumExaminedPairs = 128 }));
            Assert.AreEqual(AxisGeometryFailureReason.NoIntersectingPair, expected.Reason);
            Assert.AreEqual(expected.Reason, actual.Reason);
            return;
        }
        var unlimited = new Observer();
        var bounded = new Observer();
        AxisGeometryResult expectedResult = await detector.DetectAsync(request with { CandidateObserver = unlimited });
        AxisGeometryResult actualResult = await detector.DetectAsync(request with { CandidateObserver = bounded, MaximumExaminedPairs = 128 });
        AssertParity(expectedResult, actualResult, unlimited, bounded);
    }

    [TestMethod]
    [DataRow(128)]
    [DataRow(129)]
    public async Task TrueContactSurvivorsStillRespectTheUnchangedExactLimit(int count)
    {
        var fixture = new AxisFixtureBuilder(1800, 800).Line(100, 700, 1700, 700, id: "shared-baseline");
        for (int index = 0; index < count; index++)
            fixture.Line(100 + 10 * index, 700, 100 + 10 * index, 100, id: $"contacting-v-{index}");
        AxisGeometryRequest request = fixture.Build();
        var unlimited = new Observer();
        var bounded = new Observer();
        var detector = new AxisGeometryDetector();
        AxisGeometryResult expected = await detector.DetectAsync(request with { CandidateObserver = unlimited });
        Assert.HasCount(count, unlimited.Candidates!);
        if (count > 128)
        {
            AxisGeometryDetectionException failure = await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(
                async () => await detector.DetectAsync(request with { MaximumExaminedPairs = 128, CandidateObserver = bounded }));
            Assert.AreEqual(AxisGeometryFailureReason.PairBudgetExceeded, failure.Reason);
            Assert.IsNull(bounded.Candidates, "A budget failure must not publish a partial ranked set.");
            return;
        }
        AxisGeometryResult actual = await detector.DetectAsync(request with { MaximumExaminedPairs = 128, CandidateObserver = bounded });
        AssertParity(expected, actual, unlimited, bounded);
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(4)]
    public async Task CheapEnvelopeComparisonLimitAlsoFailsClosed(int clutter)
    {
        AxisGeometryRequest request = DetachedClutter(clutter);
        var unlimited = new Observer();
        var bounded = new Observer();
        var detector = new AxisGeometryDetector();
        AxisGeometryResult expected = await detector.DetectAsync(request with { CandidateObserver = unlimited });
        Assert.HasCount(1, unlimited.Candidates!);
        if (clutter == 4) // 25 cheap comparisons exceed B squared = 16, despite only one exact survivor.
        {
            AxisGeometryDetectionException failure = await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(
                async () => await detector.DetectAsync(request with { MaximumExaminedPairs = 4, CandidateObserver = bounded }));
            Assert.AreEqual(AxisGeometryFailureReason.PairBudgetExceeded, failure.Reason);
            Assert.IsNull(bounded.Candidates);
            return;
        }
        AxisGeometryResult actual = await detector.DetectAsync(request with { MaximumExaminedPairs = 4, CandidateObserver = bounded });
        AssertParity(expected, actual, unlimited, bounded); // Exactly 16 cheap comparisons remain permitted.
    }

    [TestMethod]
    public async Task LargePositiveLimitUsesLongArithmeticAndCancellationStillWins()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder().CleanAxes().Build() with { MaximumExaminedPairs = int.MaxValue };
        AxisGeometryResult geometry = await new AxisGeometryDetector().DetectAsync(request);
        Assert.AreEqual(new PixelPoint(100, 300), geometry.PlotPolygon.BottomLeft);
        var observer = new Observer();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await new AxisGeometryDetector().DetectAsync(request with { CandidateObserver = observer }, new CancellationToken(canceled: true)));
        Assert.IsNull(observer.Candidates);
    }

    private static AxisGeometryRequest DetachedClutter(int count)
    {
        var fixture = new AxisFixtureBuilder(1000, 800).CleanAxes(100, 100, 700, 650);
        for (int index = 0; index < count; index++)
        {
            fixture.Line(760, 40 + 30 * index, 780, 40 + 30 * index, id: $"detached-h-{index}");
            fixture.Line(820 + 12 * index, 500, 820 + 12 * index, 520, id: $"detached-v-{index}");
        }
        return fixture.Build();
    }

    private static void AssertParity(AxisGeometryResult expected, AxisGeometryResult actual,
        Observer unlimited, Observer bounded)
    {
        Assert.IsNotNull(unlimited.Candidates);
        Assert.IsNotNull(bounded.Candidates);
        Assert.AreEqual(JsonSerializer.Serialize(unlimited.Candidates), JsonSerializer.Serialize(bounded.Candidates));
        Assert.AreEqual(
            JsonSerializer.Serialize(expected with { Diagnostics = expected.Diagnostics with { Elapsed = TimeSpan.Zero } }),
            JsonSerializer.Serialize(actual with { Diagnostics = actual.Diagnostics with { Elapsed = TimeSpan.Zero } }));
    }

    private sealed class Observer : IAxisGeometryCandidateObserver
    {
        internal IReadOnlyList<AxisGeometryCandidateSnapshot>? Candidates { get; private set; }
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) => Candidates = candidates;
    }
}
