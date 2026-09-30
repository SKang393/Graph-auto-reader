// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class SingleGlyphTextMaskReviewTests
{
    [TestMethod]
    [DataRow("*")]
    [DataRow("A")]
    [DataRow("Δ")]
    [DataRow("L")]
    [DataRow("1")]
    [DataRow("e\u0301")]
    public async Task SingleUnreviewedAnnotationKeepsItsReadingWithoutErasingMarkerEvidence(string text)
    {
        OcrRequest request = OcrTestFixtures.Request([
            OcrTestFixtures.Region("glyph", 60, 40, 12, 10,
                context: new OcrRegionContext(NearAnnotationArrow: true))]);
        var recognizer = Recognizer(text);
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), recognizer,
            new InMemoryOcrResultCache());

        OcrResult result = await pipeline.RecognizeAsync(request);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        OcrRegion region = result.Regions.Single();
        Assert.AreEqual(text, region.Text);
        Assert.AreEqual(OcrTextRole.Annotation, region.Role);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, region.ReviewStatus);
        CollectionAssert.AreEqual(request.DetectedRegions![0].Polygon.Points.ToArray(), region.Polygon.Points.ToArray());
        Assert.IsEmpty(result.Masks);
        CollectionAssert.Contains(result.Warnings.ToArray(), "ocr_single_glyph_annotation_needs_review:glyph");
        OcrResult cached = await pipeline.RecognizeAsync(request);
        Assert.AreEqual(1, recognizer.CallCount);
        Assert.IsEmpty(cached.Masks);
        CollectionAssert.Contains(cached.Warnings.ToArray(), "ocr_single_glyph_annotation_needs_review:glyph");
    }

    [TestMethod]
    [DataRow("\u221e\u00b0\u3002\u221e\u3002\u00b0\u3002\u00b0")]
    [DataRow("\u25cb\u25cf\u25cb")]
    [DataRow("++")]
    [DataRow("...")]
    [DataRow("\u2192\u2190")]
    [DataRow("\U0001F537\U0001F536")]
    [DataRow("* + *")]
    [DataRow("\u00b0\u00b0")]
    public async Task SymbolRunsRetainTheirLiteralReadingAndRequireReviewBeforeMasking(string text)
    {
        OcrRequest request = OcrTestFixtures.Request([
            OcrTestFixtures.Region("symbols", 60, 40, 30, 10,
                context: new OcrRegionContext(NearAnnotationArrow: true))]);
        var recognizer = Recognizer(text);
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), recognizer, new InMemoryOcrResultCache());

        OcrResult result = await pipeline.RecognizeAsync(request);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        OcrRegion region = result.Regions.Single();
        Assert.AreEqual(text, region.Text);
        Assert.AreEqual(OcrTextRole.Annotation, region.Role);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, region.ReviewStatus);
        CollectionAssert.AreEqual(request.DetectedRegions![0].Polygon.Points.ToArray(), region.Polygon.Points.ToArray());
        Assert.IsEmpty(result.Masks);
        CollectionAssert.Contains(result.Warnings.ToArray(), "ocr_symbol_run_annotation_needs_review:symbols");
        OcrResult cached = await pipeline.RecognizeAsync(request);
        Assert.AreEqual(1, recognizer.CallCount);
        Assert.IsEmpty(cached.Masks);
        CollectionAssert.Contains(cached.Warnings.ToArray(), "ocr_symbol_run_annotation_needs_review:symbols");
    }

    [TestMethod]
    [DataRow("Probe", 60d, 40d, true, OcrTextRole.Annotation)]
    [DataRow("50", 60d, 40d, true, OcrTextRole.Annotation)]
    [DataRow("50%", 60d, 40d, true, OcrTextRole.Annotation)]
    [DataRow("10\u00b0", 60d, 40d, true, OcrTextRole.Annotation)]
    [DataRow("2+3", 60d, 40d, true, OcrTextRole.Annotation)]
    [DataRow("\uac00\ub098", 60d, 40d, true, OcrTextRole.Annotation)]
    [DataRow("\u221e\u00b0\u3002", 145d, 40d, true, OcrTextRole.Annotation)]
    [DataRow("A", 145d, 40d, true, OcrTextRole.Annotation)]
    [DataRow("1", 18d, 40d, false, OcrTextRole.YTick)]
    [DataRow("1", 60d, 88d, false, OcrTextRole.XTick)]
    public async Task WordsOutsideAnnotationsAndAxisDigitsKeepTheirMasks(
        string text, double x, double y, bool annotation, OcrTextRole expectedRole)
    {
        OcrRequest request = OcrTestFixtures.Request([
            OcrTestFixtures.Region("glyph", x, y, 8, 6,
                context: new OcrRegionContext(NumericExpected: !annotation, NearAnnotationArrow: annotation))]);
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), Recognizer(text), new InMemoryOcrResultCache());

        OcrResult result = await pipeline.RecognizeAsync(request);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.AreEqual(expectedRole, result.Regions.Single().Role);
        Assert.AreEqual(text, result.Regions.Single().Text);
        Assert.AreEqual("glyph", result.Masks.Single().RegionId);
        Assert.IsFalse(result.Warnings.Any(w => w.StartsWith("ocr_single_glyph_annotation_needs_review:", StringComparison.Ordinal)));
        Assert.IsFalse(result.Warnings.Any(w => w.StartsWith("ocr_symbol_run_annotation_needs_review:", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(OcrTextRole.LegendText)]
    [DataRow(OcrTextRole.PhaseHeading)]
    [DataRow(OcrTextRole.XTick)]
    [DataRow(OcrTextRole.YTick)]
    [DataRow(OcrTextRole.AxisTitle)]
    [DataRow(OcrTextRole.Participant)]
    public void OtherRolesDoNotBecomeAmbiguousSymbolAnnotations(OcrTextRole role)
    {
        OcrRegion region = SymbolRegion(role, OcrReviewStatus.Unreviewed);
        Assert.IsFalse(RequiresReview(region));
    }

    [TestMethod]
    [DataRow(OcrReviewStatus.Accepted)]
    [DataRow(OcrReviewStatus.Corrected)]
    [DataRow(OcrReviewStatus.Rejected)]
    public void ReviewedSymbolRunsRespectTheExistingDecision(OcrReviewStatus status)
    {
        foreach (OcrTextRole role in new[] { OcrTextRole.Annotation, OcrTextRole.Other })
            Assert.IsFalse(RequiresReview(SymbolRegion(role, status)));
    }

    private static bool RequiresReview(OcrRegion region)
    {
        // Exercise review-state boundaries without widening the production API.
        Type type = typeof(OcrPipeline).Assembly.GetType("GraphReader.Ocr.SingleGlyphTextMaskReview")!;
        MethodInfo method = type.GetMethod("RequiresReview", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, [region, new OcrRectangle(30, 15, 110, 70)])!;
    }

    private static OcrRegion SymbolRegion(OcrTextRole role, OcrReviewStatus status) => new(
        "symbols", OcrPolygon.FromRectangle(new(60, 40, 30, 10)), "\u221e\u00b0\u3002", [], role,
        0.99, OcrSourceImage.Original, status);

    private static StubTextRecognizer Recognizer(string text) => new((crops, _) =>
        ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop => new OcrRecognition(
            crop.RegionId, crop.SourceImage, [new(text, 0.99, crop.SourceImage)], 0.1)).ToArray()));
}
