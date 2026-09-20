// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class HeaderLayoutRoleResolverTests
{
    private static readonly string[] ExpectedNoteIds = ["note"];
    [TestMethod]
    [DataRow(1.0, 0.0)]
    [DataRow(2.0, 15.0)]
    public void DetachedNoteUsesLayoutWithoutChangingTextPixelsOrAlternatives(double scale, double offset)
    {
        var rows = Fixture(scale, offset);
        HeaderLayoutRoleResolution result = HeaderLayoutRoleResolver.Resolve(rows.Regions, rows.Detected, rows.Plot);
        CollectionAssert.AreEqual(ExpectedNoteIds, result.DetachedRegionIds.ToArray());
        OcrRegion note = result.Regions.Single(region => region.RegionId == "note");
        OcrRegion original = rows.Regions.Single(region => region.RegionId == "note");
        Assert.AreEqual(OcrTextRole.Annotation, note.Role);
        Assert.AreEqual(0.64, note.Confidence);
        Assert.AreEqual(original with { Role = note.Role, Confidence = note.Confidence }, note);
        CollectionAssert.AreEquivalent(rows.Regions.Where(region => region.RegionId != "note").ToArray(),
            result.Regions.Where(region => region.RegionId != "note").ToArray());
        HeaderLayoutRoleResolution reversed = HeaderLayoutRoleResolver.Resolve(
            rows.Regions.Reverse().ToArray(), rows.Detected.Reverse().ToArray(), rows.Plot);
        CollectionAssert.AreEqual(result.DetachedRegionIds.ToArray(), reversed.DetachedRegionIds.ToArray());
    }

    [TestMethod]
    public void LoneHeadingOrNearbySecondLineDoesNotSupplyDetachedNoteEvidence()
    {
        var rows = Fixture();
        HeaderLayoutRoleResolution lone = HeaderLayoutRoleResolver.Resolve(
            rows.Regions.Where(region => region.RegionId != "right").ToArray(), rows.Detected, rows.Plot);
        Assert.IsEmpty(lone.DetachedRegionIds);
        OcrPolygon close = OcrPolygon.FromRectangle(new OcrRectangle(45, 50, 35, 10));
        OcrRegion[] near = rows.Regions.Select(region => region.RegionId == "note"
            ? region with { Polygon = close } : region).ToArray();
        HeaderLayoutRoleResolution multiline = HeaderLayoutRoleResolver.Resolve(near, rows.Detected, rows.Plot);
        Assert.IsEmpty(multiline.DetachedRegionIds);
    }

    [TestMethod]
    public void ExplicitDividerAndHumanReviewedRolesArePreserved()
    {
        var rows = Fixture();
        foreach (OcrRegionContext context in new[]
        {
            new OcrRegionContext(ExplicitRoleHint: OcrTextRole.PhaseHeading),
            new OcrRegionContext(NearPhaseDivider: true),
        })
        {
            var detected = rows.Detected.Select(region => region.RegionId == "note"
                ? region with { Context = context } : region).ToArray();
            Assert.IsEmpty(HeaderLayoutRoleResolver.Resolve(rows.Regions, detected, rows.Plot).DetachedRegionIds);
        }
        foreach (OcrReviewStatus status in new[] { OcrReviewStatus.Accepted, OcrReviewStatus.Corrected, OcrReviewStatus.Rejected })
        {
            var reviewed = rows.Regions.Select(region => region.RegionId == "note"
                ? region with { ReviewStatus = status } : region).ToArray();
            Assert.IsEmpty(HeaderLayoutRoleResolver.Resolve(reviewed, rows.Detected, rows.Plot).DetachedRegionIds);
        }
    }

    [TestMethod]
    public void NumericRolesAndVerticalLabelsAreNeverReclassified()
    {
        var rows = Fixture();
        foreach (OcrTextRole role in new[] { OcrTextRole.XTick, OcrTextRole.YTick, OcrTextRole.AxisTitle })
        {
            var numeric = rows.Regions.Select(region => region.RegionId == "note"
                ? region with { Role = role, Text = "20" } : region).ToArray();
            Assert.IsEmpty(HeaderLayoutRoleResolver.Resolve(numeric, rows.Detected, rows.Plot).DetachedRegionIds);
        }
        var vertical = rows.Detected.Select(region => region.RegionId == "note"
            ? region with { OrientationDegrees = 90 } : region).ToArray();
        Assert.IsEmpty(HeaderLayoutRoleResolver.Resolve(rows.Regions, vertical, rows.Plot).DetachedRegionIds);
    }

    [TestMethod]
    public void RejectedOrOverlappingDetectionsDoNotCorroborateAHeadingRow()
    {
        var rows = Fixture();
        var rejected = rows.Regions.Select(region => region.RegionId == "right"
            ? region with { ReviewStatus = OcrReviewStatus.Rejected } : region).ToArray();
        Assert.IsEmpty(HeaderLayoutRoleResolver.Resolve(rejected, rows.Detected, rows.Plot).DetachedRegionIds);
        OcrPolygon samePosition = rows.Regions.Single(region => region.RegionId == "left").Polygon;
        var duplicates = rows.Regions.Select(region => region.RegionId == "right"
            ? region with { Polygon = samePosition } : region).ToArray();
        Assert.IsEmpty(HeaderLayoutRoleResolver.Resolve(duplicates, rows.Detected, rows.Plot).DetachedRegionIds);
    }

    [TestMethod]
    public void InvalidPlotAndCancellationFailBeforeResolution()
    {
        var rows = Fixture();
        Assert.ThrowsExactly<ArgumentException>(() => HeaderLayoutRoleResolver.Resolve(
            rows.Regions, rows.Detected, new OcrRectangle(double.NaN, 0, 10, 10)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => HeaderLayoutRoleResolver.Resolve(
            rows.Regions, rows.Detected, rows.Plot, cancellation.Token));
    }

    [TestMethod]
    public async Task PipelineSeparatesRoleCacheButReusesUnchangedRecognitionAndEmitsReviewWarning()
    {
        var rows = Fixture();
        OcrRequest request = OcrTestFixtures.Request(rows.Detected) with
        {
            PlotBounds = rows.Plot,
            OriginalImage = OcrTestFixtures.Image(width: 160, height: 190),
        };
        var recognizer = new StubTextRecognizer((crops, _) =>
            ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                new OcrRecognition(crop.RegionId, crop.SourceImage,
                    [new OcrRecognitionAlternative(crop.RegionId == "note" ? "Maintenance" : "A", 0.95, crop.SourceImage)],
                    0.1)).ToArray()));
        var cache = new InMemoryOcrResultCache();
        var options = new OcrPipelineOptions { CropPaddingPixels = 0 };
        var original = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache, options);
        var contextual = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache,
            options with { EnableHeaderLayoutRoleResolution = true });

        OcrResult before = await original.RecognizeAsync(request);
        OcrResult after = await contextual.RecognizeAsync(request);

        Assert.IsTrue(before.Succeeded, before.Failure?.TechnicalMessage);
        Assert.IsTrue(after.Succeeded, after.Failure?.TechnicalMessage);
        Assert.AreEqual(OcrTextRole.PhaseHeading, before.Regions.Single(region => region.RegionId == "note").Role);
        Assert.AreEqual(OcrTextRole.Annotation, after.Regions.Single(region => region.RegionId == "note").Role);
        Assert.IsFalse(after.Cache.CacheHit);
        Assert.IsTrue(after.Cache.RecognitionCacheHit);
        Assert.AreEqual(1, recognizer.CallCount);
        CollectionAssert.Contains(after.Warnings.ToArray(), "ocr_role_needs_review:note:detached_above_header_row");
    }

    private static (OcrRegion[] Regions, OcrDetectedRegion[] Detected, OcrRectangle Plot) Fixture(
        double scale = 1, double offset = 0)
    {
        OcrDetectedRegion Region(string id, double x, double y, double width) =>
            OcrTestFixtures.Region(id, offset + x * scale, offset + y * scale, width * scale, 10 * scale);
        OcrDetectedRegion[] detected = [Region("left", 20, 65, 20), Region("right", 85, 65, 25), Region("note", 45, 35, 35)];
        OcrRegion[] recognized = detected.Select(region => new OcrRegion(region.RegionId, region.Polygon,
            region.RegionId == "note" ? "Maintenance" : "A",
            Array.Empty<OcrRecognitionAlternative>(), OcrTextRole.PhaseHeading, 0.9,
            OcrSourceImage.Original, OcrReviewStatus.Unreviewed)).ToArray();
        return (recognized, detected, new OcrRectangle(offset + 10 * scale, offset + 90 * scale, 120 * scale, 80 * scale));
    }
}
