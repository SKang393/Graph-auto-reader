// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

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
    [DataRow("Probe", 60d, 40d, true, OcrTextRole.Annotation)]
    [DataRow("50", 60d, 40d, true, OcrTextRole.Annotation)]
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
    }

    private static StubTextRecognizer Recognizer(string text) => new((crops, _) =>
        ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop => new OcrRecognition(
            crop.RegionId, crop.SourceImage, [new(text, 0.99, crop.SourceImage)], 0.1)).ToArray()));
}
