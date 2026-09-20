// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class HeaderGlyphTextRegionRecoveryTests
{
    private static readonly OcrRectangle Plot = new(30, 50, 220, 70);

    [TestMethod]
    public void IsolatedGlyphUsesCorroboratedHeadingRowWithoutInventingItsText()
    {
        var rows = Headings();
        OcrDetectedRegion[] components = [OcrTestFixtures.Region("missing", 100, 21, 8, 9),
            OcrTestFixtures.Region("word-suffix", 173, 21, 6, 9),
            OcrTestFixtures.Region("duplicate", 58, 20, 10, 10),
            OcrTestFixtures.Region("plot-marker", 100, 60, 8, 9),
            OcrTestFixtures.Region("line", 99, 20, 1, 10),
            OcrTestFixtures.Region("note", 100, 1, 8, 9)];
        var result = HeaderGlyphTextRegionRecovery.SelectCandidates(components, rows.Detected, rows.Recognized, Plot);
        Assert.HasCount(1, result);
        Assert.AreEqual("header-glyph:missing", result[0].RegionId);
        Assert.IsNull(result[0].Context);
        Assert.AreEqual(components[0].Polygon, result[0].Polygon);
        var reordered = HeaderGlyphTextRegionRecovery.SelectCandidates(components.Reverse().ToArray(),
            rows.Detected.Reverse().ToArray(), rows.Recognized.Reverse().ToArray(), Plot);
        CollectionAssert.AreEqual(result.ToArray(), reordered.ToArray());
    }

    [TestMethod]
    public void RejectedUnboundOrInsufficientHeadingEvidenceCannotAuthorizeRecovery()
    {
        var rows = Headings();
        OcrDetectedRegion[] component = [OcrTestFixtures.Region("missing", 100, 21, 8, 9)];
        Assert.IsEmpty(HeaderGlyphTextRegionRecovery.SelectCandidates(component, rows.Detected,
            rows.Recognized.Take(2).ToArray(), Plot));
        foreach (OcrRegion replacement in new[]
        {
            rows.Recognized[0] with { ReviewStatus = OcrReviewStatus.Rejected },
            rows.Recognized[0] with { RegionId = "unbound" },
            rows.Recognized[0] with { Role = OcrTextRole.Other },
            rows.Recognized[0] with { Polygon = OcrPolygon.FromRectangle(new OcrRectangle(55, 2, 25, 10)) },
        })
        {
            Assert.IsEmpty(HeaderGlyphTextRegionRecovery.SelectCandidates(component, rows.Detected,
                [replacement, .. rows.Recognized.Skip(1)], Plot));
        }
    }

    [TestMethod]
    public async Task InvalidPlotDerivedPixelsAndCancellationAreRejected()
    {
        var rows = Headings();
        Assert.ThrowsExactly<ArgumentException>(() => HeaderGlyphTextRegionRecovery.SelectCandidates(
            [], rows.Detected, rows.Recognized, Plot with { Height = double.PositiveInfinity }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => HeaderGlyphTextRegionRecovery.SelectCandidates(
            [], rows.Detected, rows.Recognized, Plot, cancellation.Token));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await HeaderGlyphTextRegionRecovery.FindAsync(
            OcrTestFixtures.Image() with { SourceImage = OcrSourceImage.Enhanced }, rows.Detected, rows.Recognized, Plot));
    }

    [TestMethod]
    [DataRow("A")]
    [DataRow("8")]
    public async Task PipelineReadsOriginalCropPreservesBaselineAndSeparatesTheCache(string glyphText)
    {
        var rows = Headings();
        byte[] pixels = Enumerable.Repeat((byte)255, 260 * 130).ToArray();
        OcrRectangle missing = new(100, 21, 8, 9);
        foreach (OcrRectangle rectangle in rows.Detected.Select(static region => region.Polygon.Bounds).Append(missing))
        {
            for (int y = (int)rectangle.Top; y < rectangle.Bottom; y++)
            {
                for (int x = (int)rectangle.Left; x < rectangle.Right; x++)
                {
                    pixels[y * 260 + x] = 0;
                }
            }
        }
        OcrRequest request = OcrTestFixtures.Request(rows.Detected) with
        {
            OriginalImage = new OcrImage(260, 130, 260, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity),
            PlotBounds = Plot,
        };
        OcrCrop? recoveredCrop = null;
        var textById = rows.Recognized.ToDictionary(static region => region.RegionId, static region => region.Text);
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            recoveredCrop = crops.FirstOrDefault(crop => crop.RegionId.StartsWith("header-glyph:", StringComparison.Ordinal))
                ?? recoveredCrop;
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                new OcrRecognition(crop.RegionId, crop.SourceImage,
                    [new OcrRecognitionAlternative(textById.GetValueOrDefault(crop.RegionId, glyphText), 0.95, crop.SourceImage)],
                    0.1)).ToArray());
        });
        var cache = new InMemoryOcrResultCache();
        var detector = new StubTextRegionDetector([]);
        OcrResult baseline = await new OcrPipeline(detector, recognizer, cache).RecognizeAsync(request);
        var pipeline = new OcrPipeline(detector, recognizer, cache,
            new OcrPipelineOptions { EnableHeaderGlyphRecovery = true });
        OcrResult result = await pipeline.RecognizeAsync(request);
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(4, result.Regions);
        Assert.AreEqual(JsonSerializer.Serialize(baseline.Regions), JsonSerializer.Serialize(result.Regions.Take(3)));
        Assert.AreEqual(glyphText, result.Regions[^1].Text);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, result.Regions[^1].ReviewStatus);
        Assert.IsNotNull(recoveredCrop);
        Assert.AreEqual(missing, recoveredCrop.OriginalPolygon.Bounds);
        Assert.AreEqual(OcrSourceImage.Original, recoveredCrop.SourceImage);
        Assert.IsTrue(recoveredCrop.Pixels.ToArray().Any(static value => value == 0));
        Assert.Contains($"ocr_role_needs_review:{recoveredCrop.RegionId}:original_pixel_header_glyph_recovery", result.Warnings);
        Assert.AreNotEqual(baseline.Cache.CacheKey, result.Cache.CacheKey);
        Assert.AreEqual(2, recognizer.CallCount);
        Assert.IsTrue((await pipeline.RecognizeAsync(request)).Cache.CacheHit);
        Assert.AreEqual(2, recognizer.CallCount);
    }

    private static (OcrDetectedRegion[] Detected, OcrRegion[] Recognized) Headings()
    {
        OcrDetectedRegion[] detected = [OcrTestFixtures.Region("one", 55, 20, 25, 10),
            OcrTestFixtures.Region("two", 140, 20, 25, 10), OcrTestFixtures.Region("three", 210, 20, 30, 10)];
        string[] labels = ["Baseline", "Intervention", "Maintenance"];
        OcrRegion[] recognized = detected.Select((region, index) => new OcrRegion(region.RegionId,
            region.Polygon, labels[index], [], OcrTextRole.PhaseHeading, 0.9,
            OcrSourceImage.Original, OcrReviewStatus.Unreviewed)).ToArray();
        return (detected, recognized);
    }
}
