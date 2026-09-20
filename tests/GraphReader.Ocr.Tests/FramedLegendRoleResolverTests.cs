// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class FramedLegendRoleResolverTests
{
    [TestMethod]
    [DataRow(1, false)]
    [DataRow(2, true)]
    public void FrameAndSeparateSymbolSupplyRoleWithoutChangingPixelsOrText(int scale, bool hollow)
    {
        var fixture = Fixture(scale, hollow: hollow);
        byte[] pixels = fixture.Image.Pixels.ToArray();
        FramedLegendRoleResolution result = FramedLegendRoleResolver.Resolve(
            fixture.Image, [fixture.Text], [fixture.Detection]);
        Assert.HasCount(1, result.Evidence);
        OcrRegion updated = result.Regions.Single();
        Assert.AreEqual(OcrTextRole.LegendText, updated.Role);
        Assert.AreEqual(0.7, updated.Confidence);
        Assert.AreEqual(fixture.Text with { Role = updated.Role, Confidence = updated.Confidence }, updated);
        Assert.IsTrue(result.Evidence[0].FrameBounds.Left < result.Evidence[0].GlyphBounds.Left);
        Assert.IsTrue(result.Evidence[0].GlyphBounds.Right < fixture.Text.Polygon.Bounds.Left);
        CollectionAssert.AreEqual(pixels, fixture.Image.Pixels.ToArray());
    }

    [TestMethod]
    [DataRow("no-frame")]
    [DataRow("missing-edge")]
    [DataRow("no-symbol")]
    [DataRow("two-symbols")]
    [DataRow("attached-symbol")]
    public void FrameAndUniqueDetachedSymbolMustBothBeVisible(string defect)
    {
        var fixture = Fixture(defect: defect);
        FramedLegendRoleResolution result = FramedLegendRoleResolver.Resolve(
            fixture.Image, [fixture.Text], [fixture.Detection]);
        Assert.IsEmpty(result.Evidence);
        Assert.AreEqual(fixture.Text, result.Regions.Single());
    }

    [TestMethod]
    public void NumericExplicitAndReviewedEvidenceIsNeverOverwritten()
    {
        var fixture = Fixture();
        OcrRegion[] protectedRegions =
        [
            fixture.Text with { Text = "20" },
            fixture.Text with { Role = OcrTextRole.YTick },
            fixture.Text with { Role = OcrTextRole.AxisTitle },
            fixture.Text with { ReviewStatus = OcrReviewStatus.Accepted },
            fixture.Text with { ReviewStatus = OcrReviewStatus.Corrected },
            fixture.Text with { ReviewStatus = OcrReviewStatus.Rejected },
        ];
        foreach (OcrRegion region in protectedRegions)
        {
            Assert.IsEmpty(FramedLegendRoleResolver.Resolve(fixture.Image, [region], [fixture.Detection]).Evidence);
        }
        OcrRegionContext[] contexts =
        [
            new(ExplicitRoleHint: OcrTextRole.Annotation), new(NearAnnotationArrow: true),
            new(NearPhaseDivider: true), new(NumericExpected: true), new(AxisTitleExpected: true),
            new(InParticipantBand: true),
        ];
        foreach (OcrRegionContext context in contexts)
        {
            Assert.IsEmpty(FramedLegendRoleResolver.Resolve(fixture.Image, [fixture.Text],
                [fixture.Detection with { Context = context }]).Evidence);
        }
        Assert.IsEmpty(FramedLegendRoleResolver.Resolve(fixture.Image, [fixture.Text],
            [fixture.Detection with { OrientationDegrees = 90 }]).Evidence);
    }

    [TestMethod]
    public void InvalidInputsAndCancellationCannotProduceLegendEvidence()
    {
        var fixture = Fixture();
        Assert.ThrowsExactly<ArgumentException>(() => FramedLegendRoleResolver.Resolve(
            fixture.Image with { SourceImage = OcrSourceImage.Enhanced }, [fixture.Text], [fixture.Detection]));
        Assert.ThrowsExactly<ArgumentException>(() => FramedLegendRoleResolver.Resolve(
            fixture.Image with { Stride = 1 }, [fixture.Text], [fixture.Detection]));
        Assert.ThrowsExactly<ArgumentException>(() => FramedLegendRoleResolver.Resolve(
            fixture.Image, [fixture.Text], Array.Empty<OcrDetectedRegion>()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => FramedLegendRoleResolver.Resolve(
            fixture.Image, [fixture.Text], [fixture.Detection], cancellation.Token));
    }

    [TestMethod]
    public async Task PipelineKeepsDefaultRoleAndSharesOnlyUnchangedRecognitionCache()
    {
        var fixture = Fixture();
        var request = OcrTestFixtures.Request([fixture.Detection]) with
        {
            OriginalImage = fixture.Image,
            PlotBounds = new OcrRectangle(25, 10, 160, 95),
        };
        var recognizer = new StubTextRecognizer((crops, _) =>
            ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                new OcrRecognition(crop.RegionId, crop.SourceImage,
                    [new OcrRecognitionAlternative("Unfamiliar series", 0.95, crop.SourceImage)], 0.1)).ToArray()));
        var cache = new InMemoryOcrResultCache();
        var options = new OcrPipelineOptions { CropPaddingPixels = 0 };
        var original = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache, options);
        var contextual = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache,
            options with { EnableFramedLegendRoleResolution = true });
        OcrResult before = await original.RecognizeAsync(request);
        OcrResult after = await contextual.RecognizeAsync(request);
        Assert.IsTrue(before.Succeeded, before.Failure?.TechnicalMessage);
        Assert.IsTrue(after.Succeeded, after.Failure?.TechnicalMessage);
        Assert.AreEqual(OcrTextRole.Annotation, before.Regions.Single().Role);
        Assert.AreEqual(OcrTextRole.LegendText, after.Regions.Single().Role);
        Assert.IsFalse(after.Cache.CacheHit);
        Assert.IsTrue(after.Cache.RecognitionCacheHit);
        Assert.AreEqual(1, recognizer.CallCount);
        CollectionAssert.Contains(after.Warnings.ToArray(), "ocr_role_needs_review:label:framed_legend_symbol_context");
    }

    private static (OcrImage Image, OcrDetectedRegion Detection, OcrRegion Text) Fixture(
        int scale = 1, bool hollow = false, string? defect = null)
    {
        int width = 200 * scale, height = 120 * scale, stride = width + 5;
        byte[] pixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
        void Rectangle(int left, int top, int right, int bottom, bool outline)
        {
            for (int y = top * scale; y < bottom * scale; y++)
            for (int x = left * scale; x < right * scale; x++)
            {
                if (!outline || x < (left + 1) * scale || x >= (right - 1) * scale ||
                    y < (top + 1) * scale || y >= (bottom - 1) * scale)
                    pixels[y * stride + x] = 0;
            }
        }
        if (defect != "no-frame") Rectangle(35, 20, 165, 70, true);
        if (defect == "missing-edge")
            for (int y = 21 * scale; y < 69 * scale; y++)
            for (int x = 35 * scale; x < 36 * scale; x++) pixels[y * stride + x] = 255;
        if (defect == "two-symbols")
        {
            Rectangle(41, 38, 47, 44, false);
            Rectangle(56, 38, 62, 44, false);
        }
        else if (defect != "no-symbol") Rectangle(45, 36, defect == "attached-symbol" ? 74 : 58, 48, hollow);
        for (int x = 72; x < 148; x += 9) Rectangle(x, 36, x + 3, 46, false);
        var image = new OcrImage(width, height, stride, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
        var detection = OcrTestFixtures.Region("label", 70 * scale, 35 * scale, 80 * scale, 12 * scale);
        var text = new OcrRegion(detection.RegionId, detection.Polygon, "Unfamiliar series",
            Array.Empty<OcrRecognitionAlternative>(), OcrTextRole.Annotation, 0.9,
            OcrSourceImage.Original, OcrReviewStatus.Unreviewed);
        return (image, detection, text);
    }
}
