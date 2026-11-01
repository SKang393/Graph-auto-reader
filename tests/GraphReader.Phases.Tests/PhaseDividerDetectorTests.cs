// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Phases.Tests;

[TestClass]
public sealed class PhaseDividerDetectorTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void DetectAggregatesSolidDashedAndDottedSegments()
    {
        PhaseDividerSegment[] segments =
        [
            PhaseTestFixture.Segment("solid", 80),
            PhaseTestFixture.Segment("dash-1", 180, 24, 92, PhaseDividerStyle.Dashed),
            PhaseTestFixture.Segment("dash-2", 181, 98, 166, PhaseDividerStyle.Dashed),
            PhaseTestFixture.Segment("dash-3", 180, 172, 256, PhaseDividerStyle.Dashed),
            PhaseTestFixture.Segment("dot-1", 320, 24, 54, PhaseDividerStyle.Dotted),
            PhaseTestFixture.Segment("dot-2", 319, 62, 92, PhaseDividerStyle.Dotted),
            PhaseTestFixture.Segment("dot-3", 320, 100, 130, PhaseDividerStyle.Dotted),
            PhaseTestFixture.Segment("dot-4", 321, 138, 168, PhaseDividerStyle.Dotted),
            PhaseTestFixture.Segment("dot-5", 320, 176, 206, PhaseDividerStyle.Dotted),
            PhaseTestFixture.Segment("dot-6", 319, 214, 256, PhaseDividerStyle.Dotted),
        ];

        IReadOnlyList<PhaseDivider> result = new PhaseDividerDetector().Detect(
            PhaseTestFixture.Request(segments: segments),
            CancellationToken.None);

        Assert.AreEqual(3, result.Count);
        CollectionAssert.AreEqual(
            new[] { PhaseDividerStyle.Solid, PhaseDividerStyle.Dashed, PhaseDividerStyle.Dotted },
            result.Select(item => item.Style).ToArray());
        Assert.IsTrue(result.All(item => item.Source == PhaseEvidenceSource.ProfilePrior));
    }

    [TestMethod]
    public void DetectExcludesAxesBordersAnnotationsAndShortNoise()
    {
        PhaseDividerSegment[] segments =
        [
            PhaseTestFixture.Segment("accepted", 240),
            PhaseTestFixture.Segment("axis", 80, kind: PhaseSegmentKind.YAxis),
            PhaseTestFixture.Segment("border", 140, kind: PhaseSegmentKind.PanelBorder),
            PhaseTestFixture.Segment("annotation", 300, kind: PhaseSegmentKind.AnnotationStroke),
            PhaseTestFixture.Segment("left-edge", 22),
            PhaseTestFixture.Segment("right-edge", 498),
            PhaseTestFixture.Segment("short", 400, 110, 150),
            PhaseTestFixture.Segment("diagonal", 440, horizontalDrift: 20),
        ];

        IReadOnlyList<PhaseDivider> result = new PhaseDividerDetector().Detect(
            PhaseTestFixture.Request(segments: segments),
            CancellationToken.None);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(240, result[0].OriginalX, 0.001);
        string[] expectedSegmentIds = ["accepted"];
        CollectionAssert.AreEqual(expectedSegmentIds, result[0].SegmentIds.ToArray());
    }

    [TestMethod]
    public void DetectPropagatesAlignedDividerFromPeerPanel()
    {
        var peer = new PhasePanelEvidence(
            PhaseTestFixture.PeerPanelId,
            PhaseTestFixture.PlotBounds,
            [PhaseTestFixture.Segment("peer-divider", 210, panelId: PhaseTestFixture.PeerPanelId)],
            Array.Empty<PhaseHeadingEvidence>(),
            shareDividersWithTarget: true);

        IReadOnlyList<PhaseDivider> result = new PhaseDividerDetector().Detect(
            PhaseTestFixture.Request(alignedPanels: [peer]),
            CancellationToken.None);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(210, result[0].OriginalX, 0.001);
        Assert.AreEqual(PhaseEvidenceSource.CrossPanel, result[0].Source);
        CollectionAssert.AreEquivalent(
            new[] { PhaseTestFixture.PeerPanelId },
            result[0].SourcePanelIds.ToArray());
    }

    [TestMethod]
    public void DetectMergesAlignedCurrentAndPeerEvidenceWithoutDuplicateDivider()
    {
        var peer = new PhasePanelEvidence(
            PhaseTestFixture.PeerPanelId,
            PhaseTestFixture.PlotBounds,
            [PhaseTestFixture.Segment("peer-divider", 201, panelId: PhaseTestFixture.PeerPanelId)],
            Array.Empty<PhaseHeadingEvidence>(),
            shareDividersWithTarget: true);

        IReadOnlyList<PhaseDivider> result = new PhaseDividerDetector().Detect(
            PhaseTestFixture.Request(
                segments: [PhaseTestFixture.Segment("local-divider", 200)],
                alignedPanels: [peer]),
            CancellationToken.None);

        Assert.AreEqual(1, result.Count);
        CollectionAssert.AreEquivalent(
            new[] { PhaseTestFixture.PanelId, PhaseTestFixture.PeerPanelId },
            result[0].SourcePanelIds.ToArray());
        Assert.AreEqual(PhaseEvidenceSource.ProfilePrior, result[0].Source);
    }

    [TestMethod]
    public void DetectDoesNotPropagateStaggeredMultipleBaselineOnset()
    {
        var staggeredPeer = new PhasePanelEvidence(
            PhaseTestFixture.PeerPanelId,
            PhaseTestFixture.PlotBounds,
            [PhaseTestFixture.Segment("peer-onset", 340, panelId: PhaseTestFixture.PeerPanelId)],
            Array.Empty<PhaseHeadingEvidence>());

        IReadOnlyList<PhaseDivider> result = new PhaseDividerDetector().Detect(
            PhaseTestFixture.Request(
                segments: [PhaseTestFixture.Segment("target-onset", 180)],
                alignedPanels: [staggeredPeer]),
            CancellationToken.None);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(180, result[0].OriginalX, 0.001);
        CollectionAssert.DoesNotContain(result.Select(item => item.OriginalX).ToArray(), 340d);
    }

    [TestMethod]
    public void DividerIdentityIgnoresMutableEvidenceMembershipAtSameCoordinate()
    {
        string craftedSegmentId = $"a|{PhaseTestFixture.PanelId}/b";
        IReadOnlyList<PhaseDivider> twoSegmentResult = new PhaseDividerDetector().Detect(
            PhaseTestFixture.Request(
                segments:
                [
                    PhaseTestFixture.Segment("a", 240, 24, 150, PhaseDividerStyle.Dashed),
                    PhaseTestFixture.Segment("b", 240, 150, 256, PhaseDividerStyle.Dashed),
                ]),
            CancellationToken.None);
        IReadOnlyList<PhaseDivider> craftedSingleSegmentResult = new PhaseDividerDetector().Detect(
            PhaseTestFixture.Request(
                segments: [PhaseTestFixture.Segment(craftedSegmentId, 240)]),
            CancellationToken.None);

        Assert.AreEqual(1, twoSegmentResult.Count);
        Assert.AreEqual(1, craftedSingleSegmentResult.Count);
        Assert.AreEqual(twoSegmentResult[0].DividerId, craftedSingleSegmentResult[0].DividerId);
        CollectionAssert.AreNotEquivalent(
            twoSegmentResult[0].SegmentIds.ToArray(),
            craftedSingleSegmentResult[0].SegmentIds.ToArray());
    }

    [TestMethod]
    public void SubpixelDistinctDividersReceiveUniqueIdentifiers()
    {
        var options = new PhaseReasoningOptions
        {
            DividerClusterTolerancePixels = 0.1,
        };
        IReadOnlyList<PhaseDivider> result = new PhaseDividerDetector().Detect(
            PhaseTestFixture.Request(
                segments:
                [
                    PhaseTestFixture.Segment("left-subpixel", 100.2),
                    PhaseTestFixture.Segment("right-subpixel", 100.4),
                ],
                options: options),
            CancellationToken.None);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(2, result.Select(item => item.DividerId).Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void DetectorDoesNotReturnDividerCentersCloserThanItsClusterTolerance()
    {
        var options = new PhaseReasoningOptions { DividerClusterTolerancePixels = 3 };
        IReadOnlyList<PhaseDivider> dividers = new PhaseDividerDetector().Detect(
            PhaseTestFixture.Request(segments:
            [
                PhaseTestFixture.Segment("first", 100),
                PhaseTestFixture.Segment("second", 102.5),
                PhaseTestFixture.Segment("third", 103.5),
            ], options: options), CancellationToken.None);

        Assert.IsTrue(dividers.Zip(dividers.Skip(1), (left, right) =>
            right.OriginalX - left.OriginalX >= options.DividerClusterTolerancePixels).All(static valid => valid),
            "The detector returned divider centers that its own phase service rejects as too close.");
    }

    [TestMethod]
    public void DetectHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new PhaseDividerDetector().Detect(
                PhaseTestFixture.Request(segments: [PhaseTestFixture.Segment("divider", 200)]),
                cancellation.Token));
    }

    [TestMethod]
    public void FixedSyntheticHeldOutSegmentAggregationGateMeetsPixelAndSessionError()
    {
        var errors = new List<double>();
        foreach (PhaseDividerHeldOutCase testCase in PhaseDividerHeldOutFixture.Cases)
        {
            IReadOnlyList<PhaseDivider> detected = new PhaseDividerDetector().Detect(
                PhaseTestFixture.Request(segments: testCase.Segments),
                CancellationToken.None);

            Assert.AreEqual(1, detected.Count, $"Expected one divider for {testCase.CaseId}.");
            errors.Add(Math.Abs(detected[0].OriginalX - testCase.ExpectedX));
        }

        double meanPixelError = errors.Average();
        double maximumSessionError = PhaseDividerHeldOutFixture.Cases
            .Zip(errors)
            .Max(pair => pair.Second / pair.First.SessionPitchPixels);
        Assert.IsLessThanOrEqualTo(1, meanPixelError, $"Mean pixel error was {meanPixelError:F3}.");
        Assert.IsLessThanOrEqualTo(0.10, maximumSessionError, $"Maximum session error was {maximumSessionError:F3}.");
        Assert.IsGreaterThanOrEqualTo(0.95, errors.Count(error => error <= 2) / (double)errors.Count);
        TestContext.WriteLine(
            $"Synthetic held-out cases={errors.Count}; mean pixel error={meanPixelError:F6}; " +
            $"maximum pixel error={errors.Max():F6}; maximum session error={maximumSessionError:F6}.");
    }
}
