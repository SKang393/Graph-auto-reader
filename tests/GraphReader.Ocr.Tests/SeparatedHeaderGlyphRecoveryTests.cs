// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class SeparatedHeaderGlyphRecoveryTests
{
    private static readonly OcrRectangle Plot = new(40, 90, 180, 40);

    [TestMethod]
    [DataRow(false, "A Experiment")]
    [DataRow(true, "Experiment A")]
    [DataRow(false, "𝒜 Experiment")]
    public async Task SeparatesEitherBoundaryFromMeasuredInkWithoutSupplyingCharacters(bool right, string text)
    {
        var (image, source, reading, glyph, word) = Fixture(right, text);
        byte[] original = image.Pixels.ToArray();
        var result = await SeparatedHeaderGlyphRecovery.FindAsync(image, [source], [reading], Plot);
        Assert.HasCount(1, result);
        Assert.AreEqual(source.RegionId, result[0].SourceRegionId);
        Assert.AreEqual(glyph.Polygon.Bounds, result[0].Glyph.Polygon.Bounds);
        Assert.AreEqual(word.Polygon.Bounds, result[0].Word.Polygon.Bounds);
        Assert.IsNull(result[0].Glyph.Context);
        Assert.IsNull(result[0].Word.Context);
        Assert.IsNull(result[0].Glyph.Evidence);
        Assert.IsNull(result[0].Word.Evidence);
        var reordered = SeparatedHeaderGlyphRecovery.SelectCandidates(image, [word, glyph], [source],
            [reading with { Text = right ? "Unrelated Z" : "Z Unrelated" }], Plot);
        Assert.HasCount(1, reordered);
        Assert.AreEqual(result[0].Glyph.Polygon.Bounds, reordered[0].Glyph.Polygon.Bounds);
        Assert.AreEqual(result[0].Word.Polygon.Bounds, reordered[0].Word.Polygon.Bounds);
        CollectionAssert.AreEqual(original, image.Pixels.ToArray());
    }

    [TestMethod]
    [DataRow(OcrReviewStatus.Accepted)]
    [DataRow(OcrReviewStatus.Corrected)]
    [DataRow(OcrReviewStatus.Rejected)]
    public void PreservesHumanReviewDecisions(OcrReviewStatus review)
    {
        var (image, source, reading, glyph, word) = Fixture();
        Assert.IsEmpty(SeparatedHeaderGlyphRecovery.SelectCandidates(image, [glyph, word], [source],
            [reading with { ReviewStatus = review }], Plot));
    }

    [TestMethod]
    public void ProtectsBindingRolesContextAndOriginalCoordinates()
    {
        var (image, source, reading, glyph, word) = Fixture();
        OcrRegion[] invalid = [reading with { RegionId = "unbound" }, reading with { Polygon = glyph.Polygon },
            reading with { CoordinateSpace = "enhanced_pixels" }, reading with { SourceImage = OcrSourceImage.Enhanced },
            reading with { Role = OcrTextRole.XTick }, reading with { Role = OcrTextRole.YTick },
            reading with { Role = OcrTextRole.LegendText }, reading with { Role = OcrTextRole.Annotation },
            reading with { Role = OcrTextRole.AxisTitle }];
        foreach (var item in invalid)
            Assert.IsEmpty(SeparatedHeaderGlyphRecovery.SelectCandidates(image, [glyph, word], [source], [item], Plot));
        OcrRegionContext[] contexts = [new(NumericExpected: true), new(AxisTitleExpected: true),
            new(NearLegendGlyph: true), new(NearAnnotationArrow: true), new(InParticipantBand: true),
            new(ExplicitRoleHint: OcrTextRole.PhaseHeading)];
        foreach (var context in contexts)
            Assert.IsEmpty(SeparatedHeaderGlyphRecovery.SelectCandidates(image, [glyph, word],
                [source with { Context = context }], [reading], Plot));
        Assert.IsEmpty(SeparatedHeaderGlyphRecovery.SelectCandidates(image, [glyph, word],
            [source with { OrientationDegrees = 90 }], [reading], Plot));
        Assert.IsEmpty(SeparatedHeaderGlyphRecovery.SelectCandidates(image, [glyph, word],
            [source with { CoordinateSpace = "enhanced_pixels" }], [reading], Plot));
    }

    [TestMethod]
    [DataRow("1 Experiment")]
    [DataRow("Experiment 1")]
    [DataRow("AB Experiment")]
    [DataRow("Experiment")]
    [DataRow(" ")]
    public void RequiresAnAlreadyRecognizedSingleLetterBoundaryToken(string text)
    {
        var (image, source, reading, glyph, word) = Fixture(text: text);
        Assert.IsEmpty(SeparatedHeaderGlyphRecovery.SelectCandidates(image, [glyph, word], [source], [reading], Plot));
    }

    [TestMethod]
    public void RejectsTightGapsCompetingEvidenceAndAmbiguousBoundaries()
    {
        var (image, source, reading, glyph, word) = Fixture();
        var touching = word with { Polygon = OcrPolygon.FromRectangle(new(85, 50, 71, 12)) };
        Assert.IsEmpty(SeparatedHeaderGlyphRecovery.SelectCandidates(image, [glyph, touching], [source], [reading], Plot));
        Assert.IsEmpty(SeparatedHeaderGlyphRecovery.SelectCandidates(image, [glyph, word], [source, glyph], [reading], Plot));
        var protectedGlyph = reading with { RegionId = "protected", Polygon = glyph.Polygon, ReviewStatus = OcrReviewStatus.Accepted };
        Assert.IsEmpty(SeparatedHeaderGlyphRecovery.SelectCandidates(image, [glyph, word], [source], [reading, protectedGlyph], Plot));
        var otherGlyph = OcrTestFixtures.Region("other-end", 172, 51, 10, 10);
        var wide = source with { Polygon = OcrPolygon.FromRectangle(new(70, 50, 112, 12)) };
        Assert.IsEmpty(SeparatedHeaderGlyphRecovery.SelectCandidates(image, [glyph, word, otherGlyph], [wide],
            [reading with { Polygon = wide.Polygon, Text = "A Experiment B" }], Plot));
        Assert.IsEmpty(SeparatedHeaderGlyphRecovery.SelectCandidates(image, [glyph, word], [source], [reading],
            Plot with { Y = 55 }));
    }

    [TestMethod]
    public async Task InvalidGeometryDerivedImagesAndCancellationFailExplicitly()
    {
        var (image, source, reading, glyph, word) = Fixture();
        Assert.ThrowsExactly<ArgumentException>(() => SeparatedHeaderGlyphRecovery.SelectCandidates(image,
            [glyph, word], [source], [reading], Plot with { Width = double.PositiveInfinity }));
        Assert.ThrowsExactly<ArgumentException>(() => SeparatedHeaderGlyphRecovery.SelectCandidates(image,
            [glyph with { CoordinateSpace = "enhanced_pixels" }, word], [source], [reading], Plot));
        Assert.ThrowsExactly<OperationCanceledException>(() => SeparatedHeaderGlyphRecovery.SelectCandidates(image,
            [], [], [], Plot, new CancellationToken(canceled: true)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await SeparatedHeaderGlyphRecovery.FindAsync(
            image with { SourceImage = OcrSourceImage.Enhanced }, [source], [reading], Plot));
    }

    [TestMethod]
    [DataRow("B", "Experiment", false, false, false)]
    [DataRow("8", "20", false, false, false)]
    [DataRow("", "Experiment", false, false, false)]
    [DataRow("B", "", false, false, false)]
    [DataRow("B", "Experiment", true, false, false)]
    [DataRow("B", "Experiment", false, true, false)]
    [DataRow("B", "Experiment", false, false, true)]
    public async Task ReplacesOnlyACompleteSuccessfulPairInSeparateBatches(
        string glyphText, string wordText, bool failGlyph, bool failWord, bool throwWord)
    {
        var (image, source, reading, glyph, word) = Fixture();
        byte[] original = image.Pixels.ToArray();
        OcrRequest request = OcrTestFixtures.Request([source]) with { OriginalImage = image, PlotBounds = Plot, PhaseDividerXs = [] };
        var batches = new List<IReadOnlyList<OcrCrop>>();
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            batches.Add(crops);
            bool isGlyph = crops[0].RegionId.StartsWith("header-separated-glyph:", StringComparison.Ordinal);
            bool isWord = crops[0].RegionId.StartsWith("header-separated-word:", StringComparison.Ordinal);
            if (isWord && throwWord) throw new InvalidOperationException("Separated word fixture failure.");
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop => new OcrRecognition(
                crop.RegionId, crop.SourceImage, [new(isGlyph ? glyphText : isWord ? wordText : reading.Text, .95, crop.SourceImage)], .1,
                (isGlyph && failGlyph) || (isWord && failWord)
                    ? new OcrFailure("FIXTURE", "error", "Errors.ModelNotFound", "Fixture", true, "retry") : null)).ToArray());
        });
        var detector = new StubTextRegionDetector([]);
        var cache = new InMemoryOcrResultCache();
        OcrResult baseline = await new OcrPipeline(detector, recognizer, cache).RecognizeAsync(request);
        var pipeline = new OcrPipeline(detector, recognizer, cache, new OcrPipelineOptions { EnableHeaderGlyphRecovery = true });
        OcrResult result = await pipeline.RecognizeAsync(request);
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(3, batches);
        Assert.HasCount(1, batches[1]);
        Assert.HasCount(1, batches[2]);
        Assert.AreEqual(glyph.Polygon.Bounds, batches[1].Single().OriginalPolygon.Bounds);
        Assert.AreEqual(word.Polygon.Bounds, batches[2].Single().OriginalPolygon.Bounds);
        bool replaced = glyphText.Length > 0 && wordText.Length > 0 && !failGlyph && !failWord && !throwWord;
        Assert.AreEqual(replaced, !result.Regions.Any(r => r.RegionId == source.RegionId));
        Assert.AreEqual(replaced, result.Warnings.Any(static w => w.StartsWith("ocr_header_composite_replaced:", StringComparison.Ordinal)));
        if (replaced)
        {
            Assert.HasCount(2, result.Regions);
            CollectionAssert.AreEquivalent(new[] { glyphText, wordText }, result.Regions.Select(static r => r.Text).ToArray());
            Assert.IsTrue(result.Regions.All(static r => r.ReviewStatus == OcrReviewStatus.Unreviewed &&
                r.SourceImage == OcrSourceImage.Original && r.Role is not (OcrTextRole.XTick or OcrTextRole.YTick)));
        }
        else Assert.AreEqual(JsonSerializer.Serialize(baseline.Regions), JsonSerializer.Serialize(result.Regions));
        int calls = recognizer.CallCount;
        OcrResult repeated = await pipeline.RecognizeAsync(request);
        if (failGlyph || failWord || throwWord)
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

    private static (OcrImage Image, OcrDetectedRegion Source, OcrRegion Reading, OcrDetectedRegion Glyph, OcrDetectedRegion Word)
        Fixture(bool right = false, string? text = null)
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 240 * 140).ToArray();
        var glyph = OcrTestFixtures.Region("glyph", right ? 146 : 70, 51, 10, 10);
        var word = OcrTestFixtures.Region("word", right ? 70 : 96, 50, 60, 12);
        OcrRectangle gb = glyph.Polygon.Bounds, wb = word.Polygon.Bounds;
        for (int y = (int)gb.Top; y < gb.Bottom; y++)
        for (int x = (int)gb.Left; x < gb.Right; x++) pixels[y * 240 + x] = 0;
        // The detector excludes one component wider than 15% of the image.
        // Represent the word with separate glyph components, as real ink does.
        for (int letter = 0; letter < 3; letter++)
        for (int y = (int)wb.Top; y < wb.Bottom; y++)
        for (int x = (int)wb.Left + letter * 21; x < wb.Left + letter * 21 + 18; x++) pixels[y * 240 + x] = 0;
        var source = OcrTestFixtures.Region("composite", 70, 50, 86, 12);
        var reading = new OcrRegion(source.RegionId, source.Polygon, text ?? (right ? "Experiment A" : "A Experiment"), [],
            OcrTextRole.Other, .9, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);
        return (new(240, 140, 240, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity), source, reading, glyph, word);
    }
}
