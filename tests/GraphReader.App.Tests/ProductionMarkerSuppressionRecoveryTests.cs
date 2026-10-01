// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionMarkerSuppressionRecoveryTests
{
    private static readonly MarkerPolygon Plot = MarkerPolygon.FromRectangle(new(0, 0, 100, 100));

    [TestMethod]
    [DataRow(0.49, false)]
    [DataRow(0.5, true)]
    [DataRow(0.99, true)]
    public void OnlyARejectedBlockerAllowsAnotherClassification(double artifactProbability, bool expected)
    {
        MarkerCenter blocker = Center("blocker", 21, 20), candidate = Center("candidate", 20, 20);
        IReadOnlyList<MarkerCenter> found = Find([candidate], [blocker], [Classify(blocker, artifactProbability)]);
        Assert.AreEqual(expected ? 1 : 0, found.Count);
        if (expected) Assert.AreSame(candidate, found.Single());
    }

    [TestMethod]
    public void ASecondSurvivingOrUnclassifiedBlockerPreventsRecovery()
    {
        MarkerCenter first = Center("first", 17, 20), second = Center("second", 23, 20);
        MarkerCenter candidate = Center("candidate", 20, 20);
        Assert.IsEmpty(Find([candidate], [first, second], [Classify(first, 0.99), Classify(second, 0.01)]));
        Assert.IsEmpty(Find([candidate], [first, second], [Classify(first, 0.99)]));
    }

    [TestMethod]
    public void InitialCentersAndUnrelatedProposalsAreNeverReclassified()
    {
        MarkerCenter blocker = Center("blocker", 20, 20);
        Assert.IsEmpty(Find([Center("same-geometry", 20, 20), Center("not-suppressed", 70, 70)],
            [blocker], [Classify(blocker, 0.99)]));
    }

    [TestMethod]
    public void EarlierRecoveryRemainsImmutableEvenWithLowerConfidence()
    {
        MarkerCenter blocker = Center("blocker", 21, 20), candidate = Center("candidate", 20, 20);
        ClassifiedMarker existing = Classify(Center("earlier-recovery", 18, 20, confidence: 0.1));
        Assert.IsEmpty(Find([candidate], [blocker], [Classify(blocker, 0.99)], [existing]));
        Assert.AreEqual(new MarkerPoint(18, 20), existing.Marker.Center);
    }

    [TestMethod]
    [DataRow(true, OcrReviewStatus.Unreviewed, false)]
    [DataRow(false, OcrReviewStatus.Unreviewed, true)]
    [DataRow(true, OcrReviewStatus.Rejected, true)]
    public void TextEligibilityMatchesTheExistingExclusion(bool masked, OcrReviewStatus review, bool expected)
    {
        MarkerCenter blocker = Center("blocker", 21, 20), candidate = Center("candidate", 20, 20);
        OcrRegion region = new("text", OcrPolygon.FromRectangle(new(15, 15, 10, 10)), "Probe", [],
            OcrTextRole.Annotation, 0.95, OcrSourceImage.Original, review);
        IReadOnlyList<OcrMask> masks = ProductionTextMarkerExclusion.SelectMasks([region],
            masked ? [new(region.RegionId, region.Polygon, region.Confidence)] : [], CancellationToken.None);
        IReadOnlyList<MarkerCenter> found = ProductionMarkerSuppressionRecovery.Find(
            new([candidate], 5, 1.25), [blocker], [Classify(blocker, 0.99)], [], Plot,
            [region], masks, [], 0.5, CancellationToken.None);
        Assert.AreEqual(expected ? 1 : 0, found.Count);
    }

    [TestMethod]
    [DataRow(20.0, 25.0, false)]
    [DataRow(29.999, 25.0, false)]
    [DataRow(30.0, 25.0, true)]
    [DataRow(25.0, 30.0, true)]
    [DataRow(101.0, 25.0, false)]
    public void LegendFrameAndPlotBoundsRemainInForce(double x, double y, bool expected)
    {
        MarkerCenter blocker = Center("blocker", x - 1, y), candidate = Center("candidate", x, y);
        IReadOnlyList<MarkerCenter> found = ProductionMarkerSuppressionRecovery.Find(
            new([candidate], 5, 1.25), [blocker], [Classify(blocker, 0.99)], [], Plot,
            [], [], [new(20, 20, 10, 10)], 0.5, CancellationToken.None);
        Assert.AreEqual(expected ? 1 : 0, found.Count);
    }

    [TestMethod]
    public void RejectedRecoveryCandidatesCannotSuppressAcceptedNeighbors()
    {
        ClassifiedMarker rejected = Classify(Center("rejected", 20, 20, confidence: 0.99), 0.5);
        ClassifiedMarker alternative = Classify(Center("alternative", 22, 20, confidence: 0.8));
        ClassifiedMarker separate = Classify(Center("separate", 40, 20, confidence: 0.9));
        IReadOnlyList<ClassifiedMarker> added = ProductionMarkerSuppressionRecovery.SelectNew(
            new([], 5, 1.25), [], [alternative, rejected, separate], 0.5, CancellationToken.None);
        Assert.HasCount(2, added);
        Assert.AreEqual("separate", added[0].Marker.MarkerId);
        Assert.AreEqual("alternative", added[1].Marker.MarkerId);
    }

    [TestMethod]
    [DataRow(3.0, 4.999, false)]
    [DataRow(3.0, 5.0, true)]
    [DataRow(6.0, 7.499, false)]
    [DataRow(6.0, 7.5, true)]
    public void OriginalSpacingAndExistingPriorityArePreserved(double radius, double distance, bool expected)
    {
        ClassifiedMarker existing = Classify(Center("existing", 10, 20, radius, 0.1));
        ClassifiedMarker candidate = Classify(Center("candidate", 10 + distance, 20, confidence: 0.99));
        IReadOnlyList<ClassifiedMarker> added = ProductionMarkerSuppressionRecovery.SelectNew(
            new([], 5, 1.25), [existing], [candidate], 0.5, CancellationToken.None);
        Assert.AreEqual(expected ? 1 : 0, added.Count);
        Assert.AreEqual(0.1, existing.Marker.CenterConfidence);
    }

    [TestMethod]
    public void EmptyWorkStillHonorsCancellation()
    {
        var canceled = new CancellationToken(canceled: true);
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionMarkerSuppressionRecovery.Find(
            new([], 5, 1.25), [], [], [], Plot, [], [], [], 0.5, canceled));
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionMarkerSuppressionRecovery.SelectNew(
            new([], 5, 1.25), [], [], 0.5, canceled));
    }

    private static IReadOnlyList<MarkerCenter> Find(IReadOnlyList<MarkerCenter> proposals,
        IReadOnlyList<MarkerCenter> initial, IReadOnlyList<ClassifiedMarker> classified,
        IReadOnlyList<ClassifiedMarker>? existing = null) => ProductionMarkerSuppressionRecovery.Find(
            new(proposals, 5, 1.25), initial, classified, existing ?? [], Plot, [], [], [], 0.5, CancellationToken.None);

    private static MarkerCenter Center(string id, double x, double y, double radius = 3, double confidence = 0.8) =>
        new(id, new(x, y), radius, 0, confidence, MarkerSourceImage.Original);

    private static ClassifiedMarker Classify(MarkerCenter marker, double artifactProbability = 0.01) =>
        new(marker, MarkerShape.Cross, MarkerFill.Filled, "+", "Cross", artifactProbability, 0.9, 0.9, []);
}
