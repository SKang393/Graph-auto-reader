// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class InsidePlotTextRegionAssemblerTests
{
    private static readonly OcrRectangle PlotBounds = new(30, 15, 110, 70);
    private static readonly string[] ExpectedMemberIds = ["suffix", "word"];

    [TestMethod]
    public void AlignedFragmentsInsidePlotFormDeterministicUnionWithStableMembership()
    {
        var context = new OcrRegionContext(NearAnnotationArrow: true);
        var evidence = new OcrRegionEvidence(1, 0.2, 0.9, 0.1, false, ["fixture"]);
        OcrDetectedRegion word = OcrTestFixtures.Region(
            "word", 40, 30, 12, 10, confidence: 0.94, context: context) with
        {
            Evidence = evidence,
        };
        OcrDetectedRegion suffix = OcrTestFixtures.Region(
            "suffix", 54, 30, 6, 10, confidence: 0.72, context: context) with
        {
            Evidence = evidence,
        };
        OcrDetectedRegion singleton = OcrTestFixtures.Region("singleton", 115, 60, 8, 8);

        IReadOnlyList<InsidePlotTextRegionAssemblyGroup> first =
            InsidePlotTextRegionAssembler.AssembleWithMembership(
                [word, suffix, singleton], PlotBounds, [100]);
        IReadOnlyList<InsidePlotTextRegionAssemblyGroup> reordered =
            InsidePlotTextRegionAssembler.AssembleWithMembership(
                [singleton, suffix, word], PlotBounds, [100]);

        InsidePlotTextRegionAssemblyGroup merged = first.Single(
            static group => group.MemberRegionIds.Count == 2);
        Assert.AreEqual(new OcrRectangle(40, 30, 20, 10), merged.Region.Polygon.Bounds);
        Assert.AreEqual(0.72, merged.Region.DetectionConfidence);
        Assert.AreSame(context, merged.Region.Context);
        Assert.IsNull(merged.Region.Evidence);
        StringAssert.StartsWith(merged.Region.RegionId, "inside-plot:");
        CollectionAssert.AreEqual(ExpectedMemberIds, merged.MemberRegionIds.ToArray());
        CollectionAssert.AreEqual(
            first.Select(static group => group.Region.RegionId).ToArray(),
            reordered.Select(static group => group.Region.RegionId).ToArray());
        CollectionAssert.AreEqual(
            first.Select(static group => string.Join("|", group.MemberRegionIds)).ToArray(),
            reordered.Select(static group => string.Join("|", group.MemberRegionIds)).ToArray());
        Assert.AreSame(
            singleton,
            first.Single(static group => group.MemberRegionIds.Count == 1).Region);
        Assert.AreEqual(3, first.Sum(static group => group.MemberRegionIds.Count));
    }

    [TestMethod]
    public void OutsideCrossDividerMisalignedAndVerticalPairsRemainSeparate()
    {
        Assert.HasCount(2, InsidePlotTextRegionAssembler.Assemble(
            [OcrTestFixtures.Region("outside", 20, 30, 12, 10),
             OcrTestFixtures.Region("inside", 34, 30, 6, 10)],
            PlotBounds,
            []));
        Assert.HasCount(2, InsidePlotTextRegionAssembler.Assemble(
            [OcrTestFixtures.Region("left", 60, 30, 12, 10),
             OcrTestFixtures.Region("right", 74, 30, 6, 10)],
            PlotBounds,
            [73]));
        Assert.HasCount(2, InsidePlotTextRegionAssembler.Assemble(
            [OcrTestFixtures.Region("upper", 60, 30, 12, 10),
             OcrTestFixtures.Region("offset", 74, 32, 6, 10)],
            PlotBounds,
            []));
        Assert.HasCount(2, InsidePlotTextRegionAssembler.Assemble(
            [OcrTestFixtures.Region("numeric-1", 60, 30, 6, 8),
             OcrTestFixtures.Region("numeric-0", 60, 40, 6, 8)],
            PlotBounds,
            []));
    }

    [TestMethod]
    public void CancellationStopsAssembly()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            InsidePlotTextRegionAssembler.Assemble(
                [OcrTestFixtures.Region("first", 40, 30, 10, 8)],
                PlotBounds,
                [],
                cancellation.Token));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void CaptionBracketSeparatesAnAdjacentLabelWithoutChangingPixels(int scale)
    {
        var (image, plot, regions) = BracketFixture(scale, completeCaption: false, hooks: true);
        byte[] before = image.Pixels.ToArray();
        Assert.HasCount(1, InsidePlotTextRegionAssembler.Assemble(regions, plot, []));
        var result = InsidePlotTextRegionAssembler.AssembleWithMembership(regions, plot, [], image);
        Assert.HasCount(2, result);
        Assert.AreSame(regions[0], result[0].Region);
        Assert.AreSame(regions[1], result[1].Region);
        CollectionAssert.AreEqual(before, image.Pixels.ToArray());
        var reversed = InsidePlotTextRegionAssembler.AssembleWithMembership(regions.Reverse().ToArray(), plot, [], image);
        CollectionAssert.AreEqual(result.Select(g => g.Region.RegionId).ToArray(),
            reversed.Select(g => g.Region.RegionId).ToArray());
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public void CompleteCaptionAndUncorroboratedUnderlinePreserveWordAssembly(bool completeCaption, bool hooks)
    {
        var (image, plot, regions) = BracketFixture(1, completeCaption, hooks);
        Assert.HasCount(1, InsidePlotTextRegionAssembler.Assemble(regions, plot, [], image));
    }

    [TestMethod]
    public void BracketDoesNotSeparateInsidePlotWordsAndRejectsEnhancedEvidence()
    {
        var (image, plot, regions) = BracketFixture(1, completeCaption: false, hooks: true);
        Assert.HasCount(1, InsidePlotTextRegionAssembler.Assemble(regions, plot with { Y = 10, Height = 100 }, [], image));
        Assert.ThrowsExactly<ArgumentException>(() => InsidePlotTextRegionAssembler.Assemble(
            regions, plot, [], image with { SourceImage = OcrSourceImage.Enhanced }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            InsidePlotTextRegionAssembler.Assemble(regions, plot, [], image, cancellation.Token));
    }

    private static (OcrImage Image, OcrRectangle Plot, OcrDetectedRegion[] Regions) BracketFixture(
        int scale, bool completeCaption, bool hooks)
    {
        int width = 200 * scale, height = 120 * scale;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        int left = 45 * scale, right = (completeCaption ? 133 : 95) * scale, top = 36 * scale;
        for (int y = top; y < top + scale; y++)
            for (int x = left; x <= right; x++) pixels[y * width + x] = 0;
        if (hooks)
            for (int y = top; y <= top + 6 * scale; y++)
            {
                pixels[y * width + left] = 0;
                pixels[y * width + right] = 0;
            }
        return (new OcrImage(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity),
            new OcrRectangle(30 * scale, 70 * scale, 160 * scale, 40 * scale),
            [OcrTestFixtures.Region("caption", 50 * scale, 20 * scale, 40 * scale, 12 * scale),
             OcrTestFixtures.Region("separate", 98 * scale, 20 * scale, 30 * scale, 12 * scale)]);
    }

    [TestMethod]
    public void HeaderWordsJoinOnlyAcrossAWordSpaceWithinOnePhase()
    {
        OcrDetectedRegion word = OcrTestFixtures.Region("word", 60, 2, 30, 10);
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 96, 2, 24, 10);
        var joined = InsidePlotTextRegionAssembler.AssembleWithMembership([word, suffix], PlotBounds, []);
        Assert.HasCount(1, joined);
        CollectionAssert.AreEqual(ExpectedMemberIds, joined[0].MemberRegionIds.ToArray());
        Assert.AreEqual(new OcrRectangle(60, 2, 60, 10), joined[0].Region.Polygon.Bounds);
        Assert.HasCount(2, InsidePlotTextRegionAssembler.Assemble([word, suffix], PlotBounds, [93]));
        Assert.HasCount(2, InsidePlotTextRegionAssembler.Assemble([
            OcrTestFixtures.Region("condition-code", 41, 2, 6, 10), word], PlotBounds, []));
        Assert.HasCount(2, InsidePlotTextRegionAssembler.Assemble([
            OcrTestFixtures.Region("overlapping-code", 70, 2, 6, 10), word], PlotBounds, []));
        Assert.HasCount(2, InsidePlotTextRegionAssembler.Assemble([
            OcrTestFixtures.Region("participant", 10, 2, 40, 10), word], PlotBounds, []));
        Assert.HasCount(2, InsidePlotTextRegionAssembler.Assemble([
            word, suffix with { Polygon = OcrPolygon.FromRectangle(new OcrRectangle(96, 5, 24, 10)) }], PlotBounds, []));
    }
}
