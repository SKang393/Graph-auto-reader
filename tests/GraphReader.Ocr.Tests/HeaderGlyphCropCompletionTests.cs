// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class HeaderGlyphCropCompletionTests
{
    private static readonly OcrRectangle Plot = new(30, 50, 220, 70);

    [TestMethod]
    [DataRow(-90.0)]
    [DataRow(90.0)]
    [DataRow(270.0)]
    public void RestoresQuarterTurnHeadingFragmentsFromHorizontalOriginalInk(double orientation)
    {
        var (detected, recognized, component) = Fixture();
        detected[^1] = detected[^1] with { OrientationDegrees = orientation };
        recognized[^1] = recognized[^1] with { Text = "A", Role = OcrTextRole.PhaseHeading };
        var result = HeaderGlyphCropCompletion.SelectCandidates([component], detected, recognized, Plot);
        Assert.HasCount(1, result);
        Assert.AreEqual(component.Polygon, result[0].Region.Polygon);
        Assert.AreEqual(0.0, result[0].Region.OrientationDegrees);
        Assert.AreEqual(orientation, detected[^1].OrientationDegrees);
        Assert.AreEqual("A", recognized[^1].Text);
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(10.0)]
    [DataRow(180.0)]
    public void PreservesExistingHorizontalCropOrientation(double orientation)
    {
        var (detected, recognized, component) = Fixture();
        detected[^1] = detected[^1] with { OrientationDegrees = orientation };
        var result = HeaderGlyphCropCompletion.SelectCandidates([component], detected, recognized, Plot);
        Assert.HasCount(1, result);
        Assert.AreEqual(orientation, result[0].Region.OrientationDegrees);
    }

    [TestMethod]
    [DataRow(OcrTextRole.Other, OcrSourceImage.Original, OcrReviewStatus.Unreviewed, -90.0, 0.0)]
    [DataRow(OcrTextRole.PhaseHeading, OcrSourceImage.Enhanced, OcrReviewStatus.Unreviewed, -90.0, 0.0)]
    [DataRow(OcrTextRole.PhaseHeading, OcrSourceImage.Original, OcrReviewStatus.Accepted, -90.0, 0.0)]
    [DataRow(OcrTextRole.PhaseHeading, OcrSourceImage.Original, OcrReviewStatus.Corrected, -90.0, 0.0)]
    [DataRow(OcrTextRole.PhaseHeading, OcrSourceImage.Original, OcrReviewStatus.Rejected, -90.0, 0.0)]
    [DataRow(OcrTextRole.PhaseHeading, OcrSourceImage.Original, OcrReviewStatus.Unreviewed, 45.0, 0.0)]
    [DataRow(OcrTextRole.PhaseHeading, OcrSourceImage.Original, OcrReviewStatus.Unreviewed, -90.0, 90.0)]
    [DataRow(OcrTextRole.PhaseHeading, OcrSourceImage.Original, OcrReviewStatus.Unreviewed, -90.0, 45.0)]
    public void OrientationRepairRequiresAnUnreviewedOriginalHeadingAndHorizontalComponent(
        OcrTextRole role, OcrSourceImage sourceImage, OcrReviewStatus reviewStatus,
        double fragmentOrientation, double componentOrientation)
    {
        var (detected, recognized, component) = Fixture();
        detected[^1] = detected[^1] with { OrientationDegrees = fragmentOrientation };
        recognized[^1] = recognized[^1] with
        {
            Text = "A", Role = role, SourceImage = sourceImage, ReviewStatus = reviewStatus,
        };
        component = component with { OrientationDegrees = componentOrientation };
        Assert.IsEmpty(HeaderGlyphCropCompletion.SelectCandidates([component], detected, recognized, Plot));
    }

    [TestMethod]
    public void OrientationRepairKeepsCorroborationCollisionAndProtectedContextGuards()
    {
        var (detected, recognized, component) = Fixture();
        detected[^1] = detected[^1] with { OrientationDegrees = -90 };
        recognized[^1] = recognized[^1] with { Text = "A", Role = OcrTextRole.PhaseHeading };
        Assert.IsEmpty(HeaderGlyphCropCompletion.SelectCandidates([component], detected,
            [recognized[0], recognized[^1]], Plot));
        Assert.IsEmpty(HeaderGlyphCropCompletion.SelectCandidates([component],
            [.. detected, OcrTestFixtures.Region("neighbor", 100, 20, 4, 6)], recognized, Plot));
        Assert.IsEmpty(HeaderGlyphCropCompletion.SelectCandidates(
            [component, component with { RegionId = "ambiguous" }], detected, recognized, Plot));
        OcrDetectedRegion source = detected[^1];
        foreach (OcrRegionContext context in new[]
        {
            new OcrRegionContext(NumericExpected: true), new OcrRegionContext(AxisTitleExpected: true),
            new OcrRegionContext(NearLegendGlyph: true), new OcrRegionContext(NearAnnotationArrow: true),
            new OcrRegionContext(InParticipantBand: true),
            new OcrRegionContext(ExplicitRoleHint: OcrTextRole.PhaseHeading),
        })
        {
            detected[^1] = source with { Context = context };
            Assert.IsEmpty(HeaderGlyphCropCompletion.SelectCandidates([component], detected, recognized, Plot));
        }
    }

    [TestMethod]
    [DataRow("A", OcrTextRole.PhaseHeading)]
    [DataRow("3", OcrTextRole.Other)]
    [DataRow("𝒜", OcrTextRole.PhaseHeading)]
    public void UsesTheCompleteMeasuredComponentWithoutSupplyingText(string text, OcrTextRole role)
    {
        var (detected, recognized, component) = Fixture();
        recognized[^1] = recognized[^1] with { Text = text, Role = role };
        var result = HeaderGlyphCropCompletion.SelectCandidates([component], detected, recognized, Plot);
        Assert.HasCount(1, result);
        Assert.AreEqual("fragment", result[0].SourceRegionId);
        Assert.AreEqual(component.Polygon, result[0].Region.Polygon);
        Assert.IsNull(result[0].Region.Context);
        Assert.IsNull(result[0].Region.Evidence);
        Assert.AreEqual(.7, result[0].Region.DetectionConfidence);
        var reordered = HeaderGlyphCropCompletion.SelectCandidates([component], detected.Reverse().ToArray(),
            recognized.Reverse().ToArray(), Plot);
        Assert.AreEqual(result[0], reordered[0]);
        Assert.AreEqual(new OcrRectangle(102, 22, 6, 6), detected[^1].Polygon.Bounds);
    }

    [TestMethod]
    [DataRow(OcrReviewStatus.Accepted)]
    [DataRow(OcrReviewStatus.Corrected)]
    [DataRow(OcrReviewStatus.Rejected)]
    public void PreservesEveryHumanReviewDecision(OcrReviewStatus status)
    {
        var (detected, recognized, component) = Fixture();
        recognized[^1] = recognized[^1] with { ReviewStatus = status };
        Assert.IsEmpty(HeaderGlyphCropCompletion.SelectCandidates([component], detected, recognized, Plot));
    }

    [TestMethod]
    [DataRow(OcrTextRole.XTick)]
    [DataRow(OcrTextRole.YTick)]
    [DataRow(OcrTextRole.Participant)]
    [DataRow(OcrTextRole.LegendText)]
    [DataRow(OcrTextRole.Annotation)]
    public void ProtectsTicksAndOtherBoundContexts(OcrTextRole role)
    {
        var (detected, recognized, component) = Fixture();
        recognized[^1] = recognized[^1] with { Role = role };
        Assert.IsEmpty(HeaderGlyphCropCompletion.SelectCandidates([component], detected, recognized, Plot));
    }

    [TestMethod]
    public void RejectsUnboundWordsRotatedTextAndExplicitNumericContext()
    {
        var (detected, recognized, component) = Fixture();
        OcrRegion original = recognized[^1];
        foreach (OcrRegion replacement in new[]
        {
            original with { RegionId = "unbound" }, original with { Text = "AB" },
            original with { Polygon = component.Polygon },
        })
        {
            recognized[^1] = replacement;
            Assert.IsEmpty(HeaderGlyphCropCompletion.SelectCandidates([component], detected, recognized, Plot));
        }
        recognized[^1] = original;
        OcrDetectedRegion source = detected[^1];
        foreach (OcrDetectedRegion replacement in new[]
        {
            source with { OrientationDegrees = 90 },
            source with { Context = new OcrRegionContext(NumericExpected: true) },
            source with { Context = new OcrRegionContext(ExplicitRoleHint: OcrTextRole.PhaseHeading) },
        })
        {
            detected[^1] = replacement;
            Assert.IsEmpty(HeaderGlyphCropCompletion.SelectCandidates([component], detected, recognized, Plot));
        }
    }

    [TestMethod]
    [DataRow(102, 22, 6, 6)]
    [DataRow(103, 20, 2, 12)]
    [DataRow(99, 0, 12, 32)]
    [DataRow(90, 20, 40, 12)]
    [DataRow(100, 18, 10, 40)]
    public void RequiresAnExpandingCompactComponentWithinTheHeadingBand(int x, int y, int width, int height)
    {
        var (detected, recognized, _) = Fixture();
        var component = OcrTestFixtures.Region("component", x, y, width, height);
        Assert.IsEmpty(HeaderGlyphCropCompletion.SelectCandidates([component], detected, recognized, Plot));
    }

    [TestMethod]
    public void AmbiguousComponentsOtherDetectionsAndInsufficientHeadingsArePreserved()
    {
        var (detected, recognized, component) = Fixture();
        Assert.IsEmpty(HeaderGlyphCropCompletion.SelectCandidates(
            [component, component with { RegionId = "ambiguous" }], detected, recognized, Plot));
        Assert.IsEmpty(HeaderGlyphCropCompletion.SelectCandidates([component],
            [.. detected, OcrTestFixtures.Region("neighbor", 100, 20, 4, 6)], recognized, Plot));
        Assert.IsEmpty(HeaderGlyphCropCompletion.SelectCandidates([component], detected,
            [recognized[0], recognized[^1]], Plot));
    }

    [TestMethod]
    public async Task InvalidCoordinatesDerivedPixelsAndCancellationFailExplicitly()
    {
        var (detected, recognized, component) = Fixture();
        Assert.ThrowsExactly<ArgumentException>(() => HeaderGlyphCropCompletion.SelectCandidates(
            [component], detected, recognized, Plot with { Height = double.PositiveInfinity }));
        Assert.ThrowsExactly<ArgumentException>(() => HeaderGlyphCropCompletion.SelectCandidates(
            [component with { CoordinateSpace = "enhanced_pixels" }], detected, recognized, Plot));
        Assert.ThrowsExactly<OperationCanceledException>(() => HeaderGlyphCropCompletion.SelectCandidates(
            [], detected, recognized, Plot, new CancellationToken(canceled: true)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await HeaderGlyphCropCompletion.FindAsync(
            OcrTestFixtures.Image() with { SourceImage = OcrSourceImage.Enhanced }, detected, recognized, Plot));
    }

    [TestMethod]
    [DataRow("B", false, 0.0)]
    [DataRow("8", false, 0.0)]
    [DataRow("", false, 0.0)]
    [DataRow("B", true, 0.0)]
    [DataRow("A", false, -90.0)]
    [DataRow("8", false, 90.0)]
    [DataRow("", false, -90.0)]
    [DataRow("A", true, -90.0)]
    public async Task PipelineUsesASeparateOriginalCropAndReplacesOnlySuccessfulReadings(
        string completedText, bool fail, double orientation)
    {
        var (detected, recognized, component) = Fixture();
        detected[^1] = detected[^1] with { OrientationDegrees = orientation };
        if (orientation != 0)
            recognized[^1] = recognized[^1] with { Text = "A", Role = OcrTextRole.PhaseHeading };
        byte[] pixels = Enumerable.Repeat((byte)255, 260 * 130).ToArray();
        foreach (OcrRectangle rectangle in detected.Take(3).Select(static region => region.Polygon.Bounds).Append(component.Polygon.Bounds))
        for (int y = (int)rectangle.Top; y < rectangle.Bottom; y++)
        for (int x = (int)rectangle.Left; x < rectangle.Right; x++) pixels[y * 260 + x] = 0;
        byte[] before = pixels.ToArray();
        OcrRequest request = OcrTestFixtures.Request(detected) with
        {
            OriginalImage = new OcrImage(260, 130, 260, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity),
            PlotBounds = Plot,
        };
        var textById = recognized.ToDictionary(static region => region.RegionId, static region => region.Text);
        var batches = new List<IReadOnlyList<OcrCrop>>();
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            batches.Add(crops);
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
            {
                bool completed = crop.RegionId.StartsWith("header-glyph-complete:", StringComparison.Ordinal);
                return new OcrRecognition(crop.RegionId, crop.SourceImage,
                    [new OcrRecognitionAlternative(completed ? completedText : textById[crop.RegionId], .95, crop.SourceImage)], .1,
                    completed && fail ? new OcrFailure("FIXTURE_FAILURE", "error", "Errors.ModelNotFound",
                        "Fixture completion failed despite a partial alternative.", true, "retry") : null);
            }).ToArray());
        });
        var cache = new InMemoryOcrResultCache();
        var detector = new StubTextRegionDetector([]);
        OcrResult baseline = await new OcrPipeline(detector, recognizer, cache).RecognizeAsync(request);
        var pipeline = new OcrPipeline(detector, recognizer, cache, new OcrPipelineOptions { EnableHeaderGlyphRecovery = true });
        OcrResult result = await pipeline.RecognizeAsync(request);
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(2, batches);
        Assert.HasCount(1, batches[1]);
        OcrCrop completedCrop = batches[1].Single();
        Assert.AreEqual(component.Polygon.Bounds, completedCrop.OriginalPolygon.Bounds);
        Assert.AreEqual(OcrSourceImage.Original, completedCrop.SourceImage);
        Assert.AreEqual(JsonSerializer.Serialize(baseline.Regions.Where(static region => region.RegionId != "fragment")),
            JsonSerializer.Serialize(result.Regions.Where(region => region.RegionId != "fragment" && region.RegionId != completedCrop.RegionId)));
        bool replaced = completedText.Length > 0 && !fail;
        Assert.AreEqual(!replaced, result.Regions.Any(static region => region.RegionId == "fragment"));
        Assert.AreEqual(replaced, result.Warnings.Any(static warning => warning.StartsWith("ocr_truncated_glyph_replaced:", StringComparison.Ordinal)));
        if (replaced)
        {
            OcrRegion completed = result.Regions.Single(region => region.RegionId == completedCrop.RegionId);
            Assert.AreEqual(completedText, completed.Text);
            Assert.AreEqual(component.Polygon.Bounds, completed.Polygon.Bounds);
            Assert.IsFalse(completed.Role is OcrTextRole.XTick or OcrTextRole.YTick);
            Assert.AreEqual(OcrReviewStatus.Unreviewed, completed.ReviewStatus);
            Assert.IsTrue((await pipeline.RecognizeAsync(request)).Cache.CacheHit);
            Assert.AreEqual(2, recognizer.CallCount);
        }
        else Assert.AreEqual(recognized[^1].Text, result.Regions.Single(static region => region.RegionId == "fragment").Text);
        Assert.AreNotEqual(baseline.Cache.CacheKey, result.Cache.CacheKey);
        CollectionAssert.AreEqual(before, pixels);
    }

    private static (OcrDetectedRegion[] Detected, OcrRegion[] Recognized, OcrDetectedRegion Component) Fixture()
    {
        OcrDetectedRegion[] detected = [OcrTestFixtures.Region("one", 55, 20, 25, 12),
            OcrTestFixtures.Region("two", 140, 20, 25, 12), OcrTestFixtures.Region("three", 210, 20, 30, 12),
            OcrTestFixtures.Region("fragment", 102, 22, 6, 6)];
        string[] labels = ["Baseline", "Intervention", "Maintenance", "3"];
        OcrRegion[] recognized = detected.Select((region, index) => new OcrRegion(region.RegionId,
            region.Polygon, labels[index], [], index == 3 ? OcrTextRole.Other : OcrTextRole.PhaseHeading, .9,
            OcrSourceImage.Original, OcrReviewStatus.Unreviewed)).ToArray();
        return (detected, recognized, OcrTestFixtures.Region("component", 100, 20, 10, 12, confidence: .7));
    }
}
