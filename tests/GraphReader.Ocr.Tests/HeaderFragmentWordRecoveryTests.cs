// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class HeaderFragmentWordRecoveryTests
{
    private static readonly OcrRectangle Plot = new(30, 50, 270, 70);

    [TestMethod]
    public void GroupsMeasuredInkWithoutSupplyingTextAndKeepsThinStems()
    {
        var (detected, recognized, components) = Fixture();
        components[1] = components[1] with { Polygon = OcrPolygon.FromRectangle(new(101, 20, 2, 12)) };
        detected[4] = detected[4] with { Polygon = components[1].Polygon };
        recognized[4] = recognized[4] with { Polygon = components[1].Polygon, Text = "𝒜" };
        var result = HeaderFragmentWordRecovery.SelectCandidates(components, detected, recognized, Plot, []);
        Assert.HasCount(1, result);
        Assert.HasCount(4, result[0].SourceRegionIds);
        Assert.AreEqual(new OcrRectangle(90, 20, 41, 12), result[0].Region.Polygon.Bounds);
        Assert.IsNull(result[0].Region.Context);
        Assert.IsNull(result[0].Region.Evidence);
        var reordered = HeaderFragmentWordRecovery.SelectCandidates(components.Reverse().ToArray(), detected.Reverse().ToArray(),
            recognized.Reverse().ToArray(), Plot, []);
        Assert.AreEqual(result[0].Region.RegionId, reordered[0].Region.RegionId);
    }

    [TestMethod]
    [DataRow(OcrReviewStatus.Accepted)]
    [DataRow(OcrReviewStatus.Corrected)]
    [DataRow(OcrReviewStatus.Rejected)]
    public void PreservesHumanDecisions(OcrReviewStatus status)
    {
        var (detected, recognized, components) = Fixture();
        recognized[^1] = recognized[^1] with { ReviewStatus = status };
        Assert.IsEmpty(HeaderFragmentWordRecovery.SelectCandidates(components, detected, recognized, Plot, []));
    }

    [TestMethod]
    [DataRow(OcrTextRole.Annotation)]
    [DataRow(OcrTextRole.LegendText)]
    [DataRow(OcrTextRole.Participant)]
    [DataRow(OcrTextRole.XTick)]
    [DataRow(OcrTextRole.YTick)]
    public void PreservesOtherSemanticRoles(OcrTextRole role)
    {
        var (detected, recognized, components) = Fixture();
        recognized[^1] = recognized[^1] with { Role = role };
        Assert.IsEmpty(HeaderFragmentWordRecovery.SelectCandidates(components, detected, recognized, Plot, []));
    }

    [TestMethod]
    public void RejectsPartialNeighborOverlapUnrecognizedDetectionsAndExplicitContext()
    {
        var (detected, recognized, components) = Fixture();
        var neighbor = OcrTestFixtures.Region("neighbor", 125, 17, 20, 10);
        var reading = new OcrRegion(neighbor.RegionId, neighbor.Polygon, "Note", [], OcrTextRole.Other, .9,
            OcrSourceImage.Original, OcrReviewStatus.Unreviewed);
        Assert.IsEmpty(HeaderFragmentWordRecovery.SelectCandidates(components, [.. detected, neighbor], [.. recognized, reading], Plot, []));
        Assert.IsEmpty(HeaderFragmentWordRecovery.SelectCandidates(components, [.. detected, neighbor], recognized, Plot, []));
        detected[^1] = detected[^1] with { Context = new OcrRegionContext(NumericExpected: true) };
        Assert.IsEmpty(HeaderFragmentWordRecovery.SelectCandidates(components, detected, recognized, Plot, []));
    }

    [TestMethod]
    public void RequiresMultipleRecoveredFragmentsAndCorroboratedWordGeometry()
    {
        var (detected, recognized, components) = Fixture();
        Assert.IsEmpty(HeaderFragmentWordRecovery.SelectCandidates(components, detected, recognized, Plot, [110]));
        Assert.IsEmpty(HeaderFragmentWordRecovery.SelectCandidates(components, detected, recognized[1..], Plot, []));
        for (int i = 3; i < detected.Length - 1; i++)
        {
            detected[i] = detected[i] with { RegionId = "primary-" + i };
            recognized[i] = recognized[i] with { RegionId = detected[i].RegionId };
        }
        Assert.IsEmpty(HeaderFragmentWordRecovery.SelectCandidates(components, detected, recognized, Plot, []));
    }

    [TestMethod]
    public void RetainsTheWholeGroupWhenADividerCrossesIt()
    {
        var (detected, recognized, components) = Fixture();
        // Three fragments on one side would otherwise form a plausible partial word.
        Assert.IsEmpty(HeaderFragmentWordRecovery.SelectCandidates(components, detected, recognized, Plot, [120]));
    }

    [TestMethod]
    public async Task RejectsInvalidGeometryDerivedPixelsAndCancellation()
    {
        var (detected, recognized, components) = Fixture();
        Assert.ThrowsExactly<ArgumentException>(() => HeaderFragmentWordRecovery.SelectCandidates(components, detected, recognized, Plot, [double.NaN]));
        Assert.ThrowsExactly<ArgumentException>(() => HeaderFragmentWordRecovery.SelectCandidates(
            [components[0] with { CoordinateSpace = "enhanced_pixels" }], detected, recognized, Plot, []));
        Assert.ThrowsExactly<OperationCanceledException>(() => HeaderFragmentWordRecovery.SelectCandidates(
            components, detected, recognized, Plot, [], new CancellationToken(true)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await HeaderFragmentWordRecovery.FindAsync(
            OcrTestFixtures.Image() with { SourceImage = OcrSourceImage.Enhanced }, detected, recognized, Plot, []));
    }

    [TestMethod]
    [DataRow("Crab", false, false)]
    [DataRow("150", false, false)]
    [DataRow("", false, false)]
    [DataRow("Crab", true, false)]
    [DataRow("Crab", false, true)]
    public async Task PipelineReadsWordsSeparatelyAndPreservesFragmentsOnFailure(string text, bool failure, bool throwBatch)
    {
        var (allDetected, recognized, components) = Fixture();
        OcrDetectedRegion[] headings = allDetected[..3];
        byte[] pixels = Enumerable.Repeat((byte)255, 320 * 130).ToArray();
        foreach (OcrRectangle b in headings.Concat(components).Select(static r => r.Polygon.Bounds))
        for (int y = (int)b.Top; y < b.Bottom; y++)
        for (int x = (int)b.Left; x < b.Right; x++) pixels[y * 320 + x] = 0;
        byte[] original = pixels.ToArray();
        OcrRequest request = OcrTestFixtures.Request(headings) with
        {
            OriginalImage = new(320, 130, 320, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity),
            PlotBounds = Plot,
            PhaseDividerXs = [],
        };
        var batches = new List<IReadOnlyList<OcrCrop>>();
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            batches.Add(crops);
            if (throwBatch && crops[0].RegionId.StartsWith("header-fragment-word:", StringComparison.Ordinal))
                throw new InvalidOperationException("Word batch fixture failure.");
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
            {
                bool word = crop.RegionId.StartsWith("header-fragment-word:", StringComparison.Ordinal);
                string value = word ? text : crop.RegionId.StartsWith("header-glyph:", StringComparison.Ordinal)
                    ? "r" : recognized.Single(r => r.RegionId == crop.RegionId).Text;
                return new OcrRecognition(crop.RegionId, crop.SourceImage, [new(value, .95, crop.SourceImage)], .1,
                    word && failure ? new OcrFailure("FIXTURE", "error", "Errors.ModelNotFound", "Fixture", true, "retry") : null);
            }).ToArray());
        });
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), recognizer, new InMemoryOcrResultCache(),
            new OcrPipelineOptions { EnableHeaderGlyphRecovery = true });
        // Null divider context exercises the unchanged fragment path first.
        OcrResult baseline = await pipeline.RecognizeAsync(request with { PhaseDividerXs = null });
        Assert.IsTrue(baseline.Succeeded, baseline.Failure?.TechnicalMessage);
        Assert.HasCount(4, baseline.Regions.Where(static r => r.RegionId.StartsWith("header-glyph:", StringComparison.Ordinal)).ToArray());
        int originalBatchCount = batches.Count;
        OcrResult result = await pipeline.RecognizeAsync(request);
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(1, batches[^1]);
        Assert.AreEqual(new OcrRectangle(90, 20, 41, 12), batches[^1][0].OriginalPolygon.Bounds);
        Assert.AreEqual(OcrSourceImage.Original, batches[^1][0].SourceImage);
        bool replaced = text.Length > 0 && !failure && !throwBatch;
        Assert.AreEqual(replaced ? 0 : 4, result.Regions.Count(static r => r.RegionId.StartsWith("header-glyph:", StringComparison.Ordinal)));
        Assert.AreEqual(replaced ? 4 : 0, result.Warnings.Count(static w => w.StartsWith("ocr_header_fragment_replaced:", StringComparison.Ordinal)));
        Assert.AreEqual(JsonSerializer.Serialize(baseline.Regions.Where(static r => !r.RegionId.StartsWith("header-glyph:", StringComparison.Ordinal))),
            JsonSerializer.Serialize(result.Regions.Where(static r => !r.RegionId.StartsWith("header-", StringComparison.Ordinal))));
        if (replaced)
        {
            OcrRegion word = result.Regions.Single(static r => r.RegionId.StartsWith("header-fragment-word:", StringComparison.Ordinal));
            Assert.AreEqual(text, word.Text);
            Assert.IsFalse(word.Role is OcrTextRole.XTick or OcrTextRole.YTick);
            Assert.AreEqual(OcrReviewStatus.Unreviewed, word.ReviewStatus);
        }
        Assert.IsTrue(batches.Count > originalBatchCount);
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
        CollectionAssert.AreEqual(original, pixels);
    }

    private static (OcrDetectedRegion[] Detected, OcrRegion[] Recognized, OcrDetectedRegion[] Components) Fixture()
    {
        OcrDetectedRegion[] components = Enumerable.Range(0, 4).Select(i => OcrTestFixtures.Region("component-" + i, 90 + 11 * i, 20, 8, 12)).ToArray();
        OcrDetectedRegion[] detected = [OcrTestFixtures.Region("first", 35, 20, 25, 12),
            OcrTestFixtures.Region("second", 170, 20, 25, 12), OcrTestFixtures.Region("third", 240, 20, 25, 12),
            .. components.Select(static c => c with { RegionId = "header-glyph:" + c.RegionId })];
        string[] labels = ["Baseline", "Intervention", "Maintenance", "C", "r", "a", "b"];
        OcrRegion[] recognized = detected.Select((d, i) => new OcrRegion(d.RegionId, d.Polygon, labels[i], [],
            i < 3 ? OcrTextRole.PhaseHeading : OcrTextRole.Other, .9, OcrSourceImage.Original, OcrReviewStatus.Unreviewed)).ToArray();
        return (detected, recognized, components);
    }
}
