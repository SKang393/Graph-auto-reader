// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class ProductionRoleInferenceTests
{
    private static readonly OcrRectangle Plot = new(50, 30, 110, 60);

    [TestMethod]
    public async Task PlainDetectorParticipantCueIsResolvedBySharedPipeline()
    {
        var detector = new StubTextRegionDetector(
            [OcrTestFixtures.Region("participant", 4, 55, 40, 10)]);
        OcrResult result = await RecognizeAsync(HorizontalText(4, 55), "Participant 01", detector);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(1, result.Regions);
        Assert.AreEqual(OcrTextRole.Participant, result.Regions[0].Role);
        Assert.AreEqual("Participant 01", result.Regions[0].Text);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, result.Regions[0].ReviewStatus);
    }

    [TestMethod]
    public async Task PlainDetectorAmbiguousPeripheralTextGetsReviewWarning()
    {
        var detector = new StubTextRegionDetector(
            [OcrTestFixtures.Region("ambiguous", 4, 55, 40, 10)]);
        OcrResult result = await RecognizeAsync(HorizontalText(4, 55), "Morgan", detector);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.AreEqual(OcrTextRole.Other, result.Regions.Single().Role);
        Assert.IsTrue(result.Warnings.Contains(
            "ocr_role_needs_review:ambiguous:ambiguous_peripheral_text"));
    }

    [TestMethod]
    public async Task ActualDetectorAndPipelineLeaveUnlabeledAbovePlotNameForReview()
    {
        OcrImage image = HorizontalText(90, 10);

        OcrResult result = await RecognizeAsync(image, "Morgan");

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(1, result.Regions);
        Assert.AreEqual(OcrTextRole.Other, result.Regions[0].Role);
        Assert.IsTrue(result.Warnings.Any(warning =>
            warning.Contains(result.Regions[0].RegionId, StringComparison.Ordinal) &&
            warning.Contains("review", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ActualDetectorAndPipelineInferLegendFromGlyphAndTextGeometry()
    {
        OcrImage image = HorizontalText(176, 42, includeLeadingGlyph: true);

        OcrResult result = await RecognizeAsync(image, "Treatment");

        AssertRole(result, OcrTextRole.LegendText);
    }

    [TestMethod]
    public async Task ActualDetectorAndPipelineInferAnnotationInsidePlot()
    {
        OcrImage image = HorizontalText(88, 58);

        OcrResult result = await RecognizeAsync(image, "Generalization");

        AssertRole(result, OcrTextRole.Annotation);
    }

    [TestMethod]
    public async Task ActualDetectorAndPipelineInferPhaseHeadingAbovePlot()
    {
        OcrImage image = HorizontalText(86, 10);

        OcrResult result = await RecognizeAsync(image, "Intervention");

        AssertRole(result, OcrTextRole.PhaseHeading);
    }

    [TestMethod]
    public async Task ActualDetectorAndPipelineInferRotatedYAxisTitle()
    {
        OcrImage image = VerticalText(20, 42);
        var detector = new ConnectedComponentTextRegionDetector(
            new ConnectedComponentTextRegionDetectorOptions { ForegroundThreshold = 128 });

        IReadOnlyList<OcrDetectedRegion> detected = await detector.DetectAsync(image, CancellationToken.None);

        OcrResult result = await RecognizeAsync(image, "Percentage Correct", detector);

        AssertRole(result, OcrTextRole.AxisTitle);
        Assert.IsTrue(detected.Any(region =>
            GraphTextRoleClassifier.GetOrientation(region.OrientationDegrees) is
                OcrOrientation.RotatedClockwise or OcrOrientation.RotatedCounterClockwise));
    }

    [TestMethod]
    public async Task ActualDetectorGroupsSeparateVerticalGlyphsIntoOneRotatedYAxisTitle()
    {
        OcrImage image = SeparateGlyphVerticalText(20, 34);
        var detector = new ConnectedComponentTextRegionDetector(
            new ConnectedComponentTextRegionDetectorOptions { ForegroundThreshold = 128 });

        IReadOnlyList<OcrDetectedRegion> detected = await detector.DetectAsync(image, CancellationToken.None);
        OcrResult result = await RecognizeAsync(image, "Percentage Correct", detector);

        Assert.HasCount(1, detected);
        Assert.AreEqual(4, detected[0].Evidence?.ComponentCount);
        Assert.IsTrue(GraphTextRoleClassifier.GetOrientation(detected[0].OrientationDegrees) is
            OcrOrientation.RotatedClockwise or OcrOrientation.RotatedCounterClockwise);
        Assert.HasCount(1, result.Regions);
        Assert.AreEqual(OcrTextRole.AxisTitle, result.Regions[0].Role);
    }

    [TestMethod]
    public async Task UnknownAbovePlotPhaseTextRequiresReviewInsteadOfConfidentParticipantMetadata()
    {
        OcrImage image = HorizontalText(86, 10);

        OcrResult result = await RecognizeAsync(image, "Training");

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(1, result.Regions);
        OcrRegion region = result.Regions[0];
        Assert.IsFalse(
            region.Role == OcrTextRole.Participant && region.Confidence >= 0.70,
            "Ambiguous above-plot text became confident participant metadata.");
        Assert.IsTrue(result.Warnings.Any(warning =>
            warning.Contains(region.RegionId, StringComparison.Ordinal) &&
            warning.Contains("review", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<OcrResult> RecognizeAsync(
        OcrImage image,
        string text,
        ITextRegionDetector? detector = null)
    {
        var recognizer = new StubTextRecognizer((crops, _) =>
            ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                new OcrRecognition(
                    crop.RegionId,
                    crop.SourceImage,
                    [new OcrRecognitionAlternative(text, 0.95, crop.SourceImage)],
                    0.1)).ToArray()));
        var pipeline = new OcrPipeline(
            detector ?? new ConnectedComponentTextRegionDetector(
                new ConnectedComponentTextRegionDetectorOptions { ForegroundThreshold = 128 }),
            recognizer,
            new MemoryOcrResultCache(),
            batchSize: 8);
        var request = new OcrRequest(
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            image,
            Plot);
        return await pipeline.RecognizeAsync(request, CancellationToken.None);
    }

    private static void AssertRole(OcrResult result, OcrTextRole role)
    {
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.IsTrue(
            result.Regions.Any(region => region.Role == role),
            $"No production detector region was classified as {role}. Actual: " +
            string.Join(",", result.Regions.Select(static region => region.Role)));
    }

    private static OcrImage HorizontalText(int x, int y, bool includeLeadingGlyph = false)
    {
        byte[] pixels = BlankPixels();
        if (includeLeadingGlyph)
        {
            FillRectangle(pixels, x, y, 5, 5);
            x += 10;
        }

        for (var glyph = 0; glyph < 4; glyph++)
        {
            FillRectangle(pixels, x + (glyph * 5), y, 2, 6);
        }

        return Image(pixels);
    }

    private static OcrImage VerticalText(int x, int y)
    {
        byte[] pixels = BlankPixels();
        var previousOffset = 0;
        for (var row = 0; row < 18; row++)
        {
            var period = row % 8;
            var offset = period <= 4 ? period : 8 - period;
            FillRectangle(
                pixels,
                x + Math.Min(previousOffset, offset),
                y + row,
                Math.Abs(offset - previousOffset) + 1,
                1);
            previousOffset = offset;
        }

        return Image(pixels);
    }

    private static OcrImage SeparateGlyphVerticalText(int x, int y)
    {
        byte[] pixels = BlankPixels();
        for (var glyph = 0; glyph < 4; glyph++)
        {
            var glyphTop = y + (glyph * 9);
            FillRectangle(pixels, x, glyphTop, 7, 1);
            FillRectangle(pixels, x + 6, glyphTop, 1, 4);
        }

        return Image(pixels);
    }

    private static byte[] BlankPixels() => Enumerable.Repeat((byte)255, 240 * 120).ToArray();

    private static OcrImage Image(byte[] pixels) =>
        new(240, 120, 240, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);

    private static void FillRectangle(byte[] pixels, int x, int y, int width, int height)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                pixels[(row * 240) + column] = 0;
            }
        }
    }
}
