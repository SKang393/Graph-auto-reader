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
        TextMarkerExclusionBatch result = Find(markers, [text],
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
        TextMarkerExclusionBatch result = Find(
            [Marker("inside", 10, 10)], [Region() with { Role = role }], Masks(), CancellationToken.None);
        Assert.IsTrue(result.ExcludedMarkerIds.Contains("inside"));
    }

    [TestMethod]
    public void RejectedAndEmptyTextDoNotExcludeMarkers()
    {
        TextMarkerExclusionBatch result = Find(
            [Marker("inside", 10, 10)],
            [Region() with { ReviewStatus = OcrReviewStatus.Rejected }, Region() with { RegionId = "empty", Text = " " }],
            Masks(), CancellationToken.None);
        Assert.IsEmpty(result.ExcludedMarkerIds);
    }

    [TestMethod]
    public void NonOriginalGeometryAndCancellationAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Find(
            [Marker("inside", 10, 10)], [Region() with { CoordinateSpace = "enhanced_pixels" }], Masks(), CancellationToken.None));
        Assert.ThrowsExactly<OperationCanceledException>(() => Find(
            [Marker("inside", 10, 10)], [Region()], Masks(), new CancellationToken(canceled: true)));
    }

    [TestMethod]
    public void WithheldMaskPreservesTheDetectionAndReportsItsUncertainTextOverlap()
    {
        OcrRegion region = Region() with { Text = "G", Confidence = 0.49,
            Alternatives = [new("G", 0.31, OcrSourceImage.Original)] };
        ClassifiedMarker marker = Marker("inside", 10, 10);
        TextMarkerExclusionBatch result = Find(
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
        Assert.ThrowsExactly<ArgumentException>(() => Find(
            [Marker("inside", 10, 10)], [Region()], masks, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("filled", true)]
    [DataRow("open", false)]
    [DataRow("stem", false)]
    [DataRow("dot", false)]
    [DataRow("word", false)]
    [DataRow("plain-letters", false)]
    [DataRow("reviewed", false)]
    [DataRow("legend", false)]
    [DataRow("no-peer", false)]
    [DataRow("wrong-size", false)]
    [DataRow("masked-peer", false)]
    [DataRow("legend-peer", false)]
    [DataRow("second-text", false)]
    public void MixedAnnotationRetainsOnlyIndependentlyCorroboratedSolidInk(string example, bool retained)
    {
        const int width = 160, height = 80, stride = 172;
        var pixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int d = (x - 30) * (x - 30) + (y - 40) * (y - 40);
                bool target = example switch
                {
                    "open" => d is >= 18 and <= 36,
                    "stem" => Math.Abs(x - 30) <= 1 && Math.Abs(y - 40) <= 6,
                    "dot" => d <= 4,
                    _ => d <= 36 || (y == 40 && x >= 30 && x <= 52),
                };
                int peerRadius = example == "wrong-size" ? 3 : 6;
                if (target || (x - 110) * (x - 110) + (y - 34) * (y - 34) <= peerRadius * peerRadius)
                    pixels[y * stride + x] = 0;
            }
        var original = new OcrImage(width, height, stride, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
        var plot = new MarkerPolygon([new(3, 3), new(155, 3), new(155, 75), new(3, 75)]);
        OcrRegion annotation = Region() with
        {
            Polygon = OcrPolygon.FromRectangle(new(20, 31, 60, 18)),
            Text = example switch { "word" => "Dose ○", "plain-letters" => "OOO", _ => "● O △" },
            Role = example == "legend" ? OcrTextRole.LegendText : OcrTextRole.Annotation,
            ReviewStatus = example == "reviewed" ? OcrReviewStatus.Accepted : OcrReviewStatus.Unreviewed,
        };
        List<OcrRegion> regions = [annotation];
        if (example is "masked-peer" or "second-text")
            regions.Add(Region() with { RegionId = "word", Polygon = example == "masked-peer"
                ? OcrPolygon.FromRectangle(new(95, 22, 30, 24)) : annotation.Polygon });
        var masks = regions.Select(r => new OcrMask(r.RegionId, r.Polygon, r.Confidence)).ToArray();
        ClassifiedMarker[] markers = example == "no-peer" ? [Marker("target", 29, 41)] :
            [Marker("target", 29, 41), Marker("peer", 110, 34)];
        OcrRectangle[] frames = example == "legend-peer" ? [new(92, 17, 42, 36)] : [];
        var result = ProductionTextMarkerExclusion.Find(markers, regions, masks, original, plot, frames, 0.5, default);
        Assert.AreEqual(retained, !result.ExcludedMarkerIds.Contains("target"));
        if (retained) CollectionAssert.Contains(result.Warnings.ToArray(), "marker_ocr_symbol_run_disk_needs_review:target:text");
        Assert.HasCount(regions.Count, masks);
    }

    private static TextMarkerExclusionBatch Find(IReadOnlyList<ClassifiedMarker> markers, IReadOnlyList<OcrRegion> regions,
        IReadOnlyList<OcrMask> masks, CancellationToken cancellationToken) =>
        ProductionTextMarkerExclusion.Find(markers, regions, masks,
            new OcrImage(32, 32, 32, new byte[32 * 32], OcrSourceImage.Original, OcrFrameTransform.Identity),
            new MarkerPolygon([new(0, 0), new(31, 0), new(31, 31), new(0, 31)]), [], 0.5, cancellationToken);

    private static OcrMask[] Masks() => [new(Region().RegionId, Region().Polygon, 0.95)];

    private static OcrRegion Region() => new("text", OcrPolygon.FromRectangle(new OcrRectangle(0, 0, 20, 20)),
        "Label", [], OcrTextRole.Annotation, 0.95, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);

    private static ClassifiedMarker Marker(string id, double x, double y) => new(
        new MarkerCenter(id, new MarkerPoint(x, y), 3, 1, 0.95, MarkerSourceImage.Original),
        MarkerShape.Circle, MarkerFill.Filled, "circle", "Filled circle", 0.01, 0.99, 0.99, Enumerable.Repeat(0.1f, 12));
}
