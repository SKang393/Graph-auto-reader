// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class BracketCaptionGlyphRecoveryTests
{
    private static readonly OcrRectangle Plot = new(20, 85, 200, 45);

    [TestMethod]
    [DataRow(155, 50)]
    [DataRow(40, 30)]
    public async Task ReadsTheMeasuredGlyphOnEitherSideWithoutSupplyingItsText(int glyphX, int bracketLeft)
    {
        var (image, caption, reading, glyph) = Fixture(glyphX: glyphX, bracketLeft: bracketLeft);
        byte[] original = image.Pixels.ToArray();
        var result = await BracketCaptionGlyphRecovery.FindAsync(image, [caption], [reading], Plot);
        Assert.HasCount(1, result);
        Assert.AreEqual(glyph.Polygon.Bounds, result[0].Polygon.Bounds);
        Assert.IsTrue(result[0].RegionId.StartsWith("bracket-caption-glyph:", StringComparison.Ordinal));
        Assert.IsNull(result[0].Context);
        Assert.IsNull(result[0].Evidence);
        Assert.AreEqual(0d, result[0].OrientationDegrees);
        var renamed = await BracketCaptionGlyphRecovery.FindAsync(image, [caption],
            [reading with { Text = "Arbitrary caption" }], Plot);
        Assert.AreEqual(result[0], renamed[0]);
        CollectionAssert.AreEqual(original, image.Pixels.ToArray());
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("underline")]
    [DataRow("one-hook")]
    [DataRow("caption-only")]
    public void RequiresAnActualBracketSpanningBothCaptionAndGlyph(string defect)
    {
        var (image, caption, reading, glyph) = Fixture(defect);
        Assert.IsEmpty(BracketCaptionGlyphRecovery.SelectCandidates(image, [glyph], [caption], [reading], Plot));
    }

    [TestMethod]
    public void AmbiguousOrCoveredComponentsAreNotAdded()
    {
        var (image, caption, reading, glyph) = Fixture();
        var second = OcrTestFixtures.Region("second", 171, 46, 6, 11);
        Assert.IsEmpty(BracketCaptionGlyphRecovery.SelectCandidates(image, [glyph, second], [caption], [reading], Plot));
        Assert.IsEmpty(BracketCaptionGlyphRecovery.SelectCandidates(image, [glyph], [caption, glyph], [reading], Plot));
        var unboundReading = reading with { RegionId = "human-glyph", Polygon = glyph.Polygon, ReviewStatus = OcrReviewStatus.Accepted };
        Assert.IsEmpty(BracketCaptionGlyphRecovery.SelectCandidates(image, [glyph], [caption], [reading, unboundReading], Plot));
    }

    [TestMethod]
    public void DuplicateCaptionEvidenceDoesNotDuplicateAGlyph()
    {
        var (image, caption, reading, glyph) = Fixture();
        var second = caption with { RegionId = "second" };
        var result = BracketCaptionGlyphRecovery.SelectCandidates(image, [glyph], [second, caption],
            [reading with { RegionId = second.RegionId }, reading], Plot);
        Assert.HasCount(1, result);
        var reordered = BracketCaptionGlyphRecovery.SelectCandidates(image, [glyph], [caption, second],
            [reading, reading with { RegionId = second.RegionId }], Plot);
        Assert.AreEqual(result[0], reordered[0]);
    }

    [TestMethod]
    [DataRow(OcrReviewStatus.Accepted)]
    [DataRow(OcrReviewStatus.Corrected)]
    [DataRow(OcrReviewStatus.Rejected)]
    public void PreservesHumanReviewDecisions(OcrReviewStatus review)
    {
        var (image, caption, reading, glyph) = Fixture();
        Assert.IsEmpty(BracketCaptionGlyphRecovery.SelectCandidates(image, [glyph], [caption],
            [reading with { ReviewStatus = review }], Plot));
    }

    [TestMethod]
    public void RequiresBoundOriginalHorizontalUnprotectedCaptionGeometry()
    {
        var (image, caption, reading, glyph) = Fixture();
        OcrRegion[] invalidReadings = [reading with { RegionId = "unbound" }, reading with { Polygon = glyph.Polygon },
            reading with { SourceImage = OcrSourceImage.Enhanced }, reading with { CoordinateSpace = "enhanced_pixels" },
            reading with { Text = "20" }, reading with { Text = " " }, reading with { Role = OcrTextRole.YTick }];
        foreach (var invalid in invalidReadings)
            Assert.IsEmpty(BracketCaptionGlyphRecovery.SelectCandidates(image, [glyph], [caption], [invalid], Plot));
        OcrRegionContext[] protectedContexts = [new(NumericExpected: true), new(AxisTitleExpected: true),
            new(NearLegendGlyph: true), new(NearAnnotationArrow: true), new(InParticipantBand: true),
            new(ExplicitRoleHint: OcrTextRole.Annotation)];
        foreach (var context in protectedContexts)
            Assert.IsEmpty(BracketCaptionGlyphRecovery.SelectCandidates(image, [glyph],
                [caption with { Context = context }], [reading], Plot));
        Assert.IsEmpty(BracketCaptionGlyphRecovery.SelectCandidates(image, [glyph],
            [caption with { OrientationDegrees = 90 }], [reading], Plot));
        Assert.IsEmpty(BracketCaptionGlyphRecovery.SelectCandidates(image, [glyph],
            [caption with { CoordinateSpace = "enhanced_pixels" }], [reading], Plot));
    }

    [TestMethod]
    [DataRow(140, 46, 10, 11)]
    [DataRow(155, 25, 10, 11)]
    [DataRow(155, 46, 2, 11)]
    [DataRow(155, 46, 25, 11)]
    [DataRow(155, 46, 10, 5)]
    [DataRow(5, 46, 10, 11)]
    public void RejectsAdjacentWordFragmentsAndComponentsOutsideTheGlyphEnvelope(int x, int y, int width, int height)
    {
        var (image, caption, reading, _) = Fixture();
        var component = OcrTestFixtures.Region("other", x, y, width, height);
        Assert.IsEmpty(BracketCaptionGlyphRecovery.SelectCandidates(image, [component], [caption], [reading], Plot));
    }

    [TestMethod]
    public async Task InvalidCoordinatesDerivedPixelsAndCancellationFailExplicitly()
    {
        var (image, caption, reading, glyph) = Fixture();
        Assert.ThrowsExactly<ArgumentException>(() => BracketCaptionGlyphRecovery.SelectCandidates(image, [glyph],
            [caption], [reading], Plot with { Width = double.PositiveInfinity }));
        Assert.ThrowsExactly<ArgumentException>(() => BracketCaptionGlyphRecovery.SelectCandidates(image,
            [glyph with { CoordinateSpace = "enhanced_pixels" }], [caption], [reading], Plot));
        Assert.ThrowsExactly<OperationCanceledException>(() => BracketCaptionGlyphRecovery.SelectCandidates(image,
            [], [], [], Plot, new CancellationToken(canceled: true)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await BracketCaptionGlyphRecovery.FindAsync(
            image with { SourceImage = OcrSourceImage.Enhanced }, [caption], [reading], Plot));
    }

    [TestMethod]
    [DataRow("B", false, false)]
    [DataRow("8", false, false)]
    [DataRow("", false, false)]
    [DataRow("B", true, false)]
    [DataRow("B", false, true)]
    public async Task PipelineUsesASeparateBatchAndPreservesBaselineOnFailure(string text, bool failure, bool throwBatch)
    {
        var (image, caption, reading, glyph) = Fixture();
        byte[] original = image.Pixels.ToArray();
        OcrRequest request = OcrTestFixtures.Request([caption]) with { OriginalImage = image, PlotBounds = Plot, PhaseDividerXs = [] };
        var batches = new List<IReadOnlyList<OcrCrop>>();
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            batches.Add(crops);
            bool bracket = crops[0].RegionId.StartsWith("bracket-caption-glyph:", StringComparison.Ordinal);
            if (bracket && throwBatch) throw new InvalidOperationException("Bracket glyph fixture failure.");
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop => new OcrRecognition(
                crop.RegionId, crop.SourceImage, [new(bracket ? text : reading.Text, .95, crop.SourceImage)], .1,
                bracket && failure ? new OcrFailure("FIXTURE", "error", "Errors.ModelNotFound", "Fixture", true, "retry") : null)).ToArray());
        });
        var detector = new StubTextRegionDetector([]);
        var cache = new InMemoryOcrResultCache();
        OcrResult baseline = await new OcrPipeline(detector, recognizer, cache).RecognizeAsync(request);
        var pipeline = new OcrPipeline(detector, recognizer, cache, new OcrPipelineOptions { EnableHeaderGlyphRecovery = true });
        OcrResult result = await pipeline.RecognizeAsync(request);
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(2, batches);
        Assert.HasCount(1, batches[1]);
        OcrCrop crop = batches[1].Single();
        Assert.AreEqual(glyph.Polygon.Bounds, crop.OriginalPolygon.Bounds);
        Assert.AreEqual(OcrSourceImage.Original, crop.SourceImage);
        Assert.AreEqual(JsonSerializer.Serialize(baseline.Regions),
            JsonSerializer.Serialize(result.Regions.Where(r => r.RegionId != crop.RegionId)));
        bool added = text.Length > 0 && !failure && !throwBatch;
        Assert.AreEqual(added, result.Regions.Any(r => r.RegionId == crop.RegionId));
        Assert.AreEqual(added, result.Warnings.Any(static w => w.EndsWith(":original_pixel_bracket_caption_glyph_recovery", StringComparison.Ordinal)));
        if (added)
        {
            OcrRegion recovered = result.Regions.Single(r => r.RegionId == crop.RegionId);
            Assert.AreEqual(text, recovered.Text);
            Assert.IsFalse(recovered.Role is OcrTextRole.XTick or OcrTextRole.YTick);
            Assert.AreEqual(OcrReviewStatus.Unreviewed, recovered.ReviewStatus);
        }
        int calls = recognizer.CallCount;
        OcrResult repeated = await pipeline.RecognizeAsync(request);
        if (failure || throwBatch)
        {
            Assert.IsFalse(repeated.Cache.CacheHit);
            Assert.IsTrue(recognizer.CallCount > calls);
        }
        else
        {
            Assert.IsTrue(repeated.Cache.CacheHit);
            Assert.AreEqual(calls, recognizer.CallCount);
        }
        Assert.AreNotEqual(baseline.Cache.CacheKey, result.Cache.CacheKey);
        CollectionAssert.AreEqual(original, image.Pixels.ToArray());
    }

    private static (OcrImage Image, OcrDetectedRegion Caption, OcrRegion Reading, OcrDetectedRegion Glyph) Fixture(
        string? defect = null, int glyphX = 155, int bracketLeft = 50)
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 240 * 140).ToArray();
        void Fill(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) pixels[y * 240 + x] = 0;
        }
        Fill(70, 45, 135, 57);
        Fill(glyphX, 46, glyphX + 10, 57);
        int right = defect == "caption-only" ? 145 : 180;
        if (defect != "missing") Fill(bracketLeft, 60, right + 1, 61);
        if (defect is not ("missing" or "underline"))
        {
            Fill(bracketLeft, 60, bracketLeft + 1, 67);
            if (defect != "one-hook") Fill(right, 60, right + 1, 67);
        }
        var caption = OcrTestFixtures.Region("caption", 70, 45, 65, 12);
        var reading = new OcrRegion(caption.RegionId, caption.Polygon, "Follow-up", [], OcrTextRole.Annotation, .9,
            OcrSourceImage.Original, OcrReviewStatus.Unreviewed);
        return (new OcrImage(240, 140, 240, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity), caption, reading,
            OcrTestFixtures.Region("glyph", glyphX, 46, 10, 11));
    }
}
