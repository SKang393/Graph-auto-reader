// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class SparseTickRecoveryTests
{
    private static readonly OcrRectangle Plot = new(40, 30, 180, 90);
    private static readonly OcrRectangle[] ZeroAnchorLabels =
    [
        new(70, 126, 5, 8),
        new(192, 126, 5, 8),
    ];
    private static readonly OcrXAxisTickStroke[] ZeroAnchorStrokes =
    [
        Stroke("x-1", 72),
        Stroke("x-2", 194),
    ];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OneReadLabelAndTwoVisibleTicksRecoverOnlyTheMissingPixelCrop(bool vertical)
    {
        var fixture = Fixture(vertical);
        byte[] original = fixture.Image.Pixels.ToArray();
        var result = await TickLaneTextRegionRecovery.FindAsync(
            fixture.Image, [fixture.Anchor], [fixture.Reading], Plot);
        Assert.HasCount(1, result);
        Assert.AreEqual(fixture.Missing, result[0].Polygon.Bounds);
        Assert.AreEqual(0d, result[0].OrientationDegrees);
        Assert.IsNull(result[0].Context);
        CollectionAssert.AreEqual(original, fixture.Image.Pixels.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SparseRecoveryRequiresDistinctAxisConnectedStrokesAndLiteralOriginalAnchor(bool vertical)
    {
        foreach (string invalid in new[] { "missing-tick", "missing-anchor-tick", "detached-ticks", "two-ticks", "no-axis" })
        {
            var fixture = Fixture(vertical, invalid);
            Assert.IsEmpty(await TickLaneTextRegionRecovery.FindAsync(
                fixture.Image, [fixture.Anchor], [fixture.Reading], Plot), invalid);
        }
        var valid = Fixture(vertical);
        foreach (OcrRegion reading in new[]
        {
            valid.Reading with { Text = "O" },
            valid.Reading with { ReviewStatus = OcrReviewStatus.Rejected },
            valid.Reading with { SourceImage = OcrSourceImage.Enhanced },
            valid.Reading with { RegionId = "unbound" },
            valid.Reading with { Polygon = OcrPolygon.FromRectangle(valid.Missing) },
        })
        {
            Assert.IsEmpty(await TickLaneTextRegionRecovery.FindAsync(valid.Image, [valid.Anchor], [reading], Plot));
        }
    }

    [TestMethod]
    public async Task ZeroAnchorXRecoveryUsesOnlyOneMeasuredOriginalPixelAxisStrokePerCrop()
    {
        var fixture = ZeroAnchorFixture();
        byte[] original = fixture.Image.Pixels.ToArray();

        IReadOnlyList<OcrDetectedRegion> recovered = await TickLaneTextRegionRecovery.FindAsync(
            fixture.Image, [], [], Plot, fixture.Strokes, CancellationToken.None);

        Assert.HasCount(2, recovered);
        Assert.IsTrue(recovered.Any(static region => region.RegionId.StartsWith(
            "tick-lane-axis-seed:x-1:", StringComparison.Ordinal)));
        Assert.IsTrue(recovered.Any(static region => region.RegionId.StartsWith(
            "tick-lane-axis-seed:x-2:", StringComparison.Ordinal)));
        Assert.IsTrue(recovered.All(static region => region.Context is null && region.OrientationDegrees == 0d));
        CollectionAssert.AreEqual(original, fixture.Image.Pixels.ToArray());

        IReadOnlyList<OcrDetectedRegion> ambiguous = await TickLaneTextRegionRecovery.FindAsync(
            fixture.Image, [], [], Plot, fixture.Strokes.Append(fixture.Strokes[0] with { TickId = "ambiguous" }).ToArray(), CancellationToken.None);
        Assert.HasCount(1, ambiguous);
        Assert.IsTrue(ambiguous.Single().RegionId.StartsWith("tick-lane-axis-seed:x-2:", StringComparison.Ordinal));

        var detached = ZeroAnchorFixture(includeAxis: false);
        Assert.IsEmpty(await TickLaneTextRegionRecovery.FindAsync(
            detached.Image, [], [], Plot, detached.Strokes, CancellationToken.None));
    }

    [TestMethod]
    public async Task ExistingFiveArgumentCancellationOverloadRemainsAvailable()
    {
        var fixture = ZeroAnchorFixture();
        IReadOnlyList<OcrDetectedRegion> recovered = await TickLaneTextRegionRecovery.FindAsync(
            fixture.Image, [], [], Plot, default);
        Assert.IsEmpty(recovered);
    }

    [TestMethod]
    public async Task NullOptionalStrokeIsIgnoredWithoutPreventingValidatedSeedRecovery()
    {
        var fixture = ZeroAnchorFixture();
        IReadOnlyList<OcrXAxisTickStroke> strokes = [null!, .. fixture.Strokes];
        IReadOnlyList<OcrDetectedRegion> recovered = await TickLaneTextRegionRecovery.FindAsync(
            fixture.Image, [], [], Plot, strokes, CancellationToken.None);
        Assert.HasCount(2, recovered);
    }

    [TestMethod]
    [DataRow("invalid-plot")]
    [DataRow("no-strokes")]
    [DataRow("unusable-strokes")]
    public void ZeroAnchorDiagnosticsIdentifyWhenBoundsWereNotClassified(string reason)
    {
        var fixture = ZeroAnchorFixture();
        OcrDetectedRegion[] candidates = [OcrTestFixtures.Region("candidate", 70, 126, 5, 8)];
        OcrRectangle plot = reason == "invalid-plot" ? new(0, 0, 0, 0) : Plot;
        IReadOnlyList<OcrXAxisTickStroke> strokes = reason switch
        {
            "no-strokes" => [],
            "unusable-strokes" => [new("off-axis", new(72, 80), new(72, 79), new(72, 84), .9, ["off-axis"])],
            _ => fixture.Strokes,
        };
        OcrTickLaneSelectionDiagnostic? observation = null;

        IReadOnlyList<OcrDetectedRegion> result = TickLaneTextRegionRecovery.SelectZeroAnchorXTickCandidates(
            fixture.Image, candidates, [], [], plot, strokes, value => observation = value);

        Assert.IsEmpty(result);
        Assert.IsNotNull(observation);
        Assert.AreEqual(1, observation.ComponentCount);
        Assert.AreEqual(0, observation.UsableStrokeCount);
        Assert.AreEqual(0, observation.RejectedOutsideSeedBounds);
        Assert.IsNull(observation.BoundsRejections, "Null denotes that classification did not run, not zero failures.");
    }

    [TestMethod]
    public void ZeroAnchorDiagnosticsCountEachIndependentBoundsRejectionWithoutChangingSelection()
    {
        var fixture = ZeroAnchorFixture();
        OcrDetectedRegion[] candidates =
        [
            OcrTestFixtures.Region("above", 70, 119, 5, 8),
            OcrTestFixtures.Region("left", 20, 126, 5, 8),
            OcrTestFixtures.Region("right", 240, 126, 5, 8),
            OcrTestFixtures.Region("short", 90, 126, 5, 2),
            OcrTestFixtures.Region("narrow", 100, 126, 1, 8),
            OcrTestFixtures.Region("wide", 110, 126, 45, 8),
            OcrTestFixtures.Region("far", 160, 153, 5, 8),
            new OcrDetectedRegion("invalid", new OcrPolygon([new(70, 126), new(70, 130), new(70, 134)]), 0, .9),
            OcrTestFixtures.Region("multiple", 20, 119, 1, 2),
            OcrTestFixtures.Region("valid-1", 70, 126, 5, 8),
            OcrTestFixtures.Region("valid-2", 192, 126, 5, 8),
        ];
        OcrTickLaneSelectionDiagnostic? observation = null;

        IReadOnlyList<OcrDetectedRegion> withDiagnostics = TickLaneTextRegionRecovery.SelectZeroAnchorXTickCandidates(
            fixture.Image, candidates, [], [], Plot, fixture.Strokes, value => observation = value);
        IReadOnlyList<OcrDetectedRegion> withoutDiagnostics = TickLaneTextRegionRecovery.SelectZeroAnchorXTickCandidates(
            fixture.Image, candidates, [], [], Plot, fixture.Strokes, diagnosticObserver: null);

        Assert.IsNotNull(observation);
        Assert.AreEqual(candidates.Length, observation.ComponentCount);
        Assert.AreEqual(9, observation.RejectedOutsideSeedBounds);
        Assert.AreEqual(new OcrTickLaneSeedBoundsRejections(
            InvalidBounds: 1,
            NotBelowPlot: 2,
            LeftOfLane: 2,
            RightOfLane: 1,
            TooShort: 2,
            TooNarrow: 3,
            TooWide: 1,
            TooFarBelowPlot: 1), observation.BoundsRejections);
        Assert.HasCount(2, withDiagnostics);
        CollectionAssert.AreEqual(withoutDiagnostics.ToArray(), withDiagnostics.ToArray());
        CollectionAssert.AreEquivalent(withDiagnostics.Select(static region => region.RegionId).ToArray(),
            observation.ProposedCrops.Select(static crop => crop.RegionId).ToArray());
    }

    private static (OcrImage Image, IReadOnlyList<OcrXAxisTickStroke> Strokes) ZeroAnchorFixture(bool includeAxis = true)
    {
        const int width = 260;
        const int height = 160;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        foreach (OcrRectangle box in ZeroAnchorLabels)
            for (int y = (int)box.Top; y < box.Bottom; y++)
                for (int x = (int)box.Left; x < box.Right; x++) pixels[y * width + x] = 0;
        if (includeAxis)
        {
            for (int x = 40; x <= 220; x++) pixels[120 * width + x] = 0;
            for (int y = 119; y <= 124; y++)
            {
                pixels[(y * width) + 72] = 0;
                pixels[(y * width) + 194] = 0;
            }
        }

        return (new OcrImage(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity), ZeroAnchorStrokes);
    }

    private static OcrXAxisTickStroke Stroke(string id, double x) => new(
        id, new OcrPoint(x, 120), new OcrPoint(x, 119), new OcrPoint(x, 124), 0.9, [id]);

    private static (OcrImage Image, OcrDetectedRegion Anchor, OcrRegion Reading, OcrRectangle Missing)
        Fixture(bool vertical, string? invalid = null)
    {
        const int width = 260;
        const int height = 160;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        OcrDetectedRegion anchor = vertical ? OcrTestFixtures.Region("read", 24, 40, 8, 8) :
            OcrTestFixtures.Region("read", 192, 126, 5, 8);
        OcrRectangle missing = vertical ? new(24, 100, 8, 8) : new(70, 126, 5, 8);
        foreach (OcrRectangle box in new[] { anchor.Polygon.Bounds, missing })
            for (int y = (int)box.Top; y < box.Bottom; y++)
                for (int x = (int)box.Left; x < box.Right; x++) pixels[y * width + x] = 0;
        if (invalid != "no-axis")
        {
            for (int x = 40; x <= 220; x++) pixels[120 * width + x] = 0;
            for (int y = 30; y <= 120; y++) pixels[y * width + 40] = 0;
        }
        if (invalid != "missing-anchor-tick") Tick(vertical ? 44 : 194);
        if (invalid != "missing-tick") Tick(vertical ? 104 : 72);
        if (invalid == "two-ticks") Tick(vertical ? 108 : 76);
        var image = new OcrImage(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
        var reading = new OcrRegion(anchor.RegionId, anchor.Polygon, "2", [],
            vertical ? OcrTextRole.YTick : OcrTextRole.XTick, 0.95,
            OcrSourceImage.Original, OcrReviewStatus.Unreviewed);
        return (image, anchor, reading, missing);

        void Tick(int along)
        {
            for (int cross = vertical ? 36 : 119; cross <= (vertical ? 41 : 124); cross++)
            {
                if (invalid == "detached-ticks" && (vertical ? cross >= 39 : cross <= 121)) continue;
                if (invalid == "no-axis" && (vertical ? cross == 40 : cross == 120)) continue;
                pixels[(vertical ? along : cross) * width + (vertical ? cross : along)] = 0;
            }
        }
    }
}
