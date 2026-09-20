// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class TickLaneTextRegionRecoveryTests
{
    private static readonly OcrRectangle Plot = new(30, 15, 110, 70);

    [TestMethod]
    public void AlignedPixelComponentIsProposedWithoutInventingTextOrNumericContext()
    {
        var rows = XAnchors();
        OcrDetectedRegion missing = OcrTestFixtures.Region("missing", 60, 91, 5, 8, orientationDegrees: -90);
        OcrDetectedRegion[] components = [missing,
            OcrTestFixtures.Region("duplicate", 80, 91, 5, 8),
            OcrTestFixtures.Region("point", 60, 65, 5, 8),
            OcrTestFixtures.Region("axis", 30, 84, 100, 2),
            OcrTestFixtures.Region("footer", 60, 110, 35, 9),
            OcrTestFixtures.Region("speck", 63, 92, 1, 1)];
        var result = TickLaneTextRegionRecovery.SelectCandidates(components, rows.Detected, rows.Recognized, Plot);
        Assert.HasCount(1, result);
        Assert.AreEqual("tick-lane:missing", result[0].RegionId);
        Assert.AreEqual(missing.Polygon, result[0].Polygon);
        Assert.AreEqual(0d, result[0].OrientationDegrees);
        Assert.IsNull(result[0].Context);
        Assert.AreEqual(-90d, missing.OrientationDegrees);
        var reversed = TickLaneTextRegionRecovery.SelectCandidates(components.Reverse().ToArray(),
            rows.Detected.Reverse().ToArray(), rows.Recognized.Reverse().ToArray(), Plot);
        CollectionAssert.AreEqual(result.ToArray(), reversed.ToArray());
    }

    [TestMethod]
    public void MissingLiteralAnchorsRejectedLabelsAndMisalignedRowsCannotAuthorizeRecovery()
    {
        var rows = XAnchors();
        OcrDetectedRegion[] candidate = [OcrTestFixtures.Region("missing", 60, 91, 5, 8)];
        Assert.IsEmpty(TickLaneTextRegionRecovery.SelectCandidates(candidate, rows.Detected,
            rows.Recognized.Take(2).ToArray(), Plot));
        foreach (OcrRegion replacement in new[]
        {
            rows.Recognized[0] with { Text = "O" },
            rows.Recognized[0] with { ReviewStatus = OcrReviewStatus.Rejected },
            rows.Recognized[0] with { RegionId = "unbound" },
            rows.Recognized[0] with { Polygon = OcrPolygon.FromRectangle(new OcrRectangle(40, 110, 5, 8)) },
        })
        {
            Assert.IsEmpty(TickLaneTextRegionRecovery.SelectCandidates(candidate, rows.Detected,
                [replacement, .. rows.Recognized.Skip(1)], Plot));
        }
    }

    [TestMethod]
    public void YLaneUsesMeasuredRightAlignmentAndDoesNotAdmitPlotMarkersOrLongTitles()
    {
        OcrDetectedRegion[] detected = [OcrTestFixtures.Region("one", 23, 25, 5, 8),
            OcrTestFixtures.Region("two", 18, 45, 10, 8), OcrTestFixtures.Region("three", 23, 65, 5, 8)];
        OcrRegion[] recognized = detected.Select(region => Recognized(region, "10", OcrTextRole.YTick)).ToArray();
        var result = TickLaneTextRegionRecovery.SelectCandidates([
            OcrTestFixtures.Region("missing", 23, 35, 5, 8),
            OcrTestFixtures.Region("left-note", 2, 35, 5, 8),
            OcrTestFixtures.Region("wide-title", 1, 35, 27, 8),
            OcrTestFixtures.Region("inside", 50, 35, 5, 8)], detected, recognized, Plot);
        Assert.HasCount(1, result);
        Assert.AreEqual("tick-lane:missing", result[0].RegionId);
    }

    [TestMethod]
    public async Task InvalidCoordinatesEnhancedPixelsAndCancellationStopBeforeRecovery()
    {
        var rows = XAnchors();
        Assert.ThrowsExactly<ArgumentException>(() => TickLaneTextRegionRecovery.SelectCandidates(
            [], rows.Detected, rows.Recognized, Plot with { Width = double.PositiveInfinity }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => TickLaneTextRegionRecovery.SelectCandidates(
            [], rows.Detected, rows.Recognized, Plot, cancellation.Token));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await TickLaneTextRegionRecovery.FindAsync(
            OcrTestFixtures.Image() with { SourceImage = OcrSourceImage.Enhanced }, rows.Detected, rows.Recognized, Plot));
    }

    private static (OcrDetectedRegion[] Detected, OcrRegion[] Recognized) XAnchors()
    {
        OcrDetectedRegion[] detected = [OcrTestFixtures.Region("one", 40, 91, 5, 8),
            OcrTestFixtures.Region("two", 80, 91, 5, 8), OcrTestFixtures.Region("three", 120, 91, 5, 8)];
        // Recognition values intentionally do not form a sequence. This helper
        // locates pixels; only downstream calibration may validate values.
        OcrRegion[] recognized = [Recognized(detected[0], "9", OcrTextRole.XTick),
            Recognized(detected[1], "2", OcrTextRole.XTick), Recognized(detected[2], "15", OcrTextRole.XTick)];
        return (detected, recognized);
    }

    private static OcrRegion Recognized(OcrDetectedRegion region, string text, OcrTextRole role) =>
        new(region.RegionId, region.Polygon, text, [], role, 0.9,
            OcrSourceImage.Original, OcrReviewStatus.Unreviewed);
}
