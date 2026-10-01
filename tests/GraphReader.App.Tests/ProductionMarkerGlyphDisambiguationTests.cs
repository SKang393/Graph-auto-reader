// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionMarkerGlyphDisambiguationTests
{
    private static readonly OcrRectangle Plot = new(20, 20, 60, 60);

    [TestMethod]
    [DataRow("O", "ocr_single_glyph_annotation_needs_review:")]
    [DataRow("××", "ocr_symbol_run_annotation_needs_review:")]
    public void ResolvesFlaggedGlyphsAndRetainsOriginalReadingsForAudit(string text, string warningPrefix)
    {
        OcrResult raw = Result(Region() with { Text = text }) with { Warnings = [warningPrefix + "glyph"] };
        ClassifiedMarker marker = Marker();
        string before = JsonSerializer.Serialize(raw), markerBefore = JsonSerializer.Serialize(marker);
        MarkerGlyphDisambiguationBatch result = Resolve(raw, [marker]);
        Assert.IsEmpty(result.Result.Regions);
        Assert.AreSame(raw.Regions.Single(), result.MarkerGlyphs.Single());
        Assert.AreEqual("ocr_glyph_interpreted_as_marker:glyph:point", result.Warnings.Single());
        Assert.AreEqual(before, JsonSerializer.Serialize(raw));
        Assert.AreEqual(markerBefore, JsonSerializer.Serialize(marker));
        Assert.AreEqual(OcrReviewStatus.Unreviewed, result.MarkerGlyphs.Single().ReviewStatus);
        Assert.AreEqual(raw.Confidence, result.Result.Confidence);
        Assert.AreSame(raw.Masks, result.Result.Masks);
        Assert.IsFalse(result.Result.Cache.CacheHit);
        Assert.AreNotEqual(raw.Cache.CacheKey, result.Result.Cache.CacheKey);
        Assert.AreEqual(raw.Cache.RecognitionCacheKey, result.Result.Cache.RecognitionCacheKey);
        StringAssert.EndsWith(result.Result.StageVersion, ProductionMarkerGlyphDisambiguation.Version);
    }

    [TestMethod]
    [DataRow(OcrReviewStatus.Accepted)]
    [DataRow(OcrReviewStatus.Corrected)]
    [DataRow(OcrReviewStatus.Rejected)]
    public void PreservesHumanReviewDecisions(OcrReviewStatus review) =>
        AssertUnchanged(Result(Region() with { ReviewStatus = review }), [Marker()]);

    [TestMethod]
    [DataRow(OcrTextRole.XTick)]
    [DataRow(OcrTextRole.YTick)]
    [DataRow(OcrTextRole.PhaseHeading)]
    [DataRow(OcrTextRole.LegendText)]
    [DataRow(OcrTextRole.Participant)]
    [DataRow(OcrTextRole.AxisTitle)]
    public void PreservesSemanticTextRoles(OcrTextRole role) =>
        AssertUnchanged(Result(Region() with { Role = role }), [Marker()]);

    [TestMethod]
    public void RequiresExistingAmbiguityWarningAndWithheldMask()
    {
        OcrResult raw = Result(Region());
        AssertUnchanged(raw with { Warnings = [] }, [Marker()]);
        AssertUnchanged(raw with { Warnings = ["ocr_single_glyph_annotation_needs_review:other"] }, [Marker()]);
        AssertUnchanged(raw with { Masks = [new OcrMask("glyph", raw.Regions.Single().Polygon, .9)] }, [Marker()]);
        AssertUnchanged(Result(Region() with { Text = " " }), [Marker()]);
        AssertUnchanged(Result(Region() with { SourceImage = OcrSourceImage.Enhanced }), [Marker()]);
    }

    [TestMethod]
    public void SmallFalsePointInsideMisreadWordDoesNotRemoveItsText()
    {
        OcrRegion wide = Region() with { Text = "H", Polygon = OcrPolygon.FromRectangle(new(34, 45, 33, 10)) };
        AssertUnchanged(Result(wide), [Marker(radius: 2.5)]);
    }

    [TestMethod]
    [DataRow(MarkerShape.Other, .01)]
    [DataRow(MarkerShape.Circle, .5)]
    [DataRow(MarkerShape.Circle, .9)]
    public void RequiresAnAcceptedRecognizedMarkerShape(MarkerShape shape, double artifact) =>
        AssertUnchanged(Result(Region()), [Marker(shape: shape, artifact: artifact)]);

    [TestMethod]
    public void RequiresActualPolygonContainmentAndInsidePlotPosition()
    {
        OcrRegion diamond = Region() with { Polygon = new OcrPolygon([new(50, 40), new(60, 50), new(50, 60), new(40, 50)]) };
        AssertUnchanged(Result(diamond), [Marker(x: 42, y: 42, radius: 15)]);
        AssertUnchanged(Result(Region()), [Marker(x: 70)]);
        OcrRegion outside = Region() with { Polygon = OcrPolygon.FromRectangle(new(84, 44, 12, 12)) };
        AssertUnchanged(Result(outside), [Marker(x: 90)]);
        AssertUnchanged(Result(Region()), [Marker(source: MarkerSourceImage.Enhanced)]);
        AssertUnchanged(Result(Region()), []);
    }

    [TestMethod]
    public void DerivedCacheBindsAssociationsAndIsIndependentOfMarkerOrder()
    {
        OcrResult raw = Result(Region());
        ClassifiedMarker[] markers = [Marker("one"), Marker("two", x: 51)];
        var first = Resolve(raw, markers);
        var reordered = Resolve(raw, markers.Reverse().ToArray());
        Assert.HasCount(1, first.MarkerGlyphs);
        Assert.HasCount(2, first.Warnings);
        Assert.AreEqual(first.Result.Cache.CacheKey, reordered.Result.Cache.CacheKey);
        CollectionAssert.AreEqual(first.Warnings.ToArray(), reordered.Warnings.ToArray());
        Assert.AreNotEqual(first.Result.Cache.CacheKey, Resolve(raw, [markers[0]]).Result.Cache.CacheKey);
        Assert.AreNotEqual(first.Result.Cache.CacheKey,
            Resolve(raw with { Cache = raw.Cache with { CacheKey = "another-source" } }, markers).Result.Cache.CacheKey);
    }

    [TestMethod]
    public void InvalidCoordinatesBindingsAndCancellationFailExplicitly()
    {
        OcrResult raw = Result(Region());
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(raw with { CoordinateSpace = "enhanced_pixels" }, [Marker()]));
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(Result(Region() with { CoordinateSpace = "enhanced_pixels" }), [Marker()]));
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(raw with { Masks = [new("unknown", Region().Polygon, .9)] }, [Marker()]));
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(raw, [Marker(), Marker()]));
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(raw, [Marker(radius: double.PositiveInfinity)]));
        Assert.ThrowsExactly<ArgumentException>(() => ProductionMarkerGlyphDisambiguation.Resolve(raw,
            Plot with { Width = double.PositiveInfinity }, [], .5, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => ProductionMarkerGlyphDisambiguation.Resolve(raw, Plot, [], double.NaN, CancellationToken.None));
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionMarkerGlyphDisambiguation.Resolve(raw, Plot,
            [], .5, new CancellationToken(canceled: true)));
    }

    private static MarkerGlyphDisambiguationBatch Resolve(OcrResult result, IReadOnlyList<ClassifiedMarker> markers) =>
        ProductionMarkerGlyphDisambiguation.Resolve(result, Plot, markers, .5, CancellationToken.None);

    private static void AssertUnchanged(OcrResult raw, IReadOnlyList<ClassifiedMarker> markers)
    {
        MarkerGlyphDisambiguationBatch result = Resolve(raw, markers);
        Assert.AreSame(raw, result.Result);
        Assert.IsEmpty(result.MarkerGlyphs);
        Assert.IsEmpty(result.Warnings);
    }

    private static OcrRegion Region() => new("glyph", OcrPolygon.FromRectangle(new(44, 44, 12, 12)), "O",
        [new("O", .9, OcrSourceImage.Original)], OcrTextRole.Annotation, .9, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);

    private static OcrResult Result(OcrRegion region) => new(OcrContract.Version, "run", "project", "panel", OcrContract.Stage,
        "ocr-test", new string('a', 64), OcrContract.CoordinateSpace, [region], [], new(1, 1, 1, 3), .9,
        ["ocr_single_glyph_annotation_needs_review:" + region.RegionId], new(true, "raw-cache", 1, 1, true, "raw-recognition"), null, []);

    private static ClassifiedMarker Marker(string id = "point", double x = 50, double y = 50, double radius = 5,
        MarkerShape shape = MarkerShape.Circle, double artifact = .01, MarkerSourceImage source = MarkerSourceImage.Original) =>
        new(new MarkerCenter(id, new(x, y), radius, .01, .95, source), shape, MarkerFill.Filled,
            "circle", "filled circle", artifact, .99, .99, Enumerable.Repeat(.1f, 12));
}
