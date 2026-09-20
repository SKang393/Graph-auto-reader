// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionTextMarkerExclusionTests
{
    private static readonly string[] ExpectedExcludedIds = ["inside"];
    private static readonly string[] ExpectedWarnings = ["marker_excluded_by_ocr_text:inside:text"];

    [TestMethod]
    public void RecognizedTextExcludesCentersWithoutExpandingToRotatedBoundingBox()
    {
        OcrRegion text = Region() with { Polygon = new OcrPolygon([
            new(10, 0), new(20, 10), new(10, 20), new(0, 10)]) };
        ClassifiedMarker[] markers = [Marker("inside", 10, 10), Marker("corner", 1, 1), Marker("near", 21, 10)];
        TextMarkerExclusionBatch result = ProductionTextMarkerExclusion.Find(markers, [text],
            [new OcrMask(text.RegionId, OcrPolygon.FromRectangle(new(0, 0, 20, 20)), 0.95)], CancellationToken.None);
        CollectionAssert.AreEquivalent(ExpectedExcludedIds, result.ExcludedMarkerIds.ToArray());
        CollectionAssert.AreEqual(ExpectedWarnings, result.Warnings.ToArray());
        Assert.AreEqual(0.01, markers[0].ArtifactProbability, "Classifier probability must not be rewritten.");
    }

    [TestMethod]
    [DataRow(OcrTextRole.LegendText)]
    [DataRow(OcrTextRole.Annotation)]
    [DataRow(OcrTextRole.Other)]
    public void TextExclusionDoesNotDependOnItsSemanticRole(OcrTextRole role)
    {
        TextMarkerExclusionBatch result = ProductionTextMarkerExclusion.Find(
            [Marker("inside", 10, 10)], [Region() with { Role = role }], Masks(), CancellationToken.None);
        Assert.IsTrue(result.ExcludedMarkerIds.Contains("inside"));
    }

    [TestMethod]
    public void RejectedAndEmptyTextDoNotExcludeMarkers()
    {
        TextMarkerExclusionBatch result = ProductionTextMarkerExclusion.Find(
            [Marker("inside", 10, 10)],
            [Region() with { ReviewStatus = OcrReviewStatus.Rejected }, Region() with { RegionId = "empty", Text = " " }],
            Masks(), CancellationToken.None);
        Assert.IsEmpty(result.ExcludedMarkerIds);
    }

    [TestMethod]
    public void NonOriginalGeometryAndCancellationAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ProductionTextMarkerExclusion.Find(
            [Marker("inside", 10, 10)], [Region() with { CoordinateSpace = "enhanced_pixels" }], Masks(), CancellationToken.None));
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionTextMarkerExclusion.Find(
            [Marker("inside", 10, 10)], [Region()], Masks(), new CancellationToken(canceled: true)));
    }

    [TestMethod]
    public void WithheldMaskPreservesTheDetectionAndReportsItsUncertainTextOverlap()
    {
        OcrRegion region = Region() with { Text = "G", Confidence = 0.49,
            Alternatives = [new("G", 0.31, OcrSourceImage.Original)] };
        ClassifiedMarker marker = Marker("inside", 10, 10);
        TextMarkerExclusionBatch result = ProductionTextMarkerExclusion.Find(
            [marker], [region], [], CancellationToken.None);
        Assert.IsEmpty(result.ExcludedMarkerIds);
        Assert.AreEqual("marker_ocr_overlap_needs_review:inside:text", result.Warnings.Single());
        Assert.AreEqual(0.01, marker.ArtifactProbability);
        Assert.AreEqual(0.99, marker.ShapeConfidence);
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("duplicate")]
    [DataRow("coordinates")]
    public void InvalidMaskBindingsFailClosed(string defect)
    {
        OcrMask mask = Masks().Single();
        OcrMask[] masks = defect switch
        {
            "unknown" => [mask with { RegionId = "unknown" }],
            "duplicate" => [mask, mask],
            _ => [mask with { CoordinateSpace = "enhanced_pixels" }],
        };
        Assert.ThrowsExactly<ArgumentException>(() => ProductionTextMarkerExclusion.Find(
            [Marker("inside", 10, 10)], [Region()], masks, CancellationToken.None));
    }

    private static OcrMask[] Masks() => [new(Region().RegionId, Region().Polygon, 0.95)];

    private static OcrRegion Region() => new("text", OcrPolygon.FromRectangle(new OcrRectangle(0, 0, 20, 20)),
        "Label", [], OcrTextRole.Annotation, 0.95, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);

    private static ClassifiedMarker Marker(string id, double x, double y) => new(
        new MarkerCenter(id, new MarkerPoint(x, y), 3, 1, 0.95, MarkerSourceImage.Original),
        MarkerShape.Circle, MarkerFill.Filled, "circle", "Filled circle", 0.01, 0.99, 0.99, Enumerable.Repeat(0.1f, 12));
}
