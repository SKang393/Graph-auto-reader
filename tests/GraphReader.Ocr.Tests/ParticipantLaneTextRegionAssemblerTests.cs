// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class ParticipantLaneTextRegionAssemblerTests
{
    private static readonly OcrRectangle PlotBounds = new(30, 15, 110, 70);
    private static readonly string[] ExpectedMemberIds = ["suffix", "word"];

    [TestMethod]
    public void AlignedFragmentsInParticipantLaneFormDeterministicUnion()
    {
        OcrDetectedRegion word = OcrTestFixtures.Region("word", 2, 30, 12, 10);
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 16, 30, 6, 10);
        OcrDetectedRegion separateTick = OcrTestFixtures.Region("tick", 18, 55, 6, 8);

        IReadOnlyList<ParticipantLaneTextRegionAssemblyGroup> first =
            ParticipantLaneTextRegionAssembler.AssembleWithMembership(
                [word, suffix, separateTick],
                PlotBounds);
        IReadOnlyList<ParticipantLaneTextRegionAssemblyGroup> reordered =
            ParticipantLaneTextRegionAssembler.AssembleWithMembership(
                [separateTick, suffix, word],
                PlotBounds);

        ParticipantLaneTextRegionAssemblyGroup merged = first.Single(
            static group => group.MemberRegionIds.Count == 2);
        Assert.AreEqual(new OcrRectangle(2, 30, 20, 10), merged.Region.Polygon.Bounds);
        CollectionAssert.AreEqual(ExpectedMemberIds, merged.MemberRegionIds.ToArray());
        StringAssert.StartsWith(merged.Region.RegionId, "participant-lane:");
        Assert.IsNull(merged.Region.Context);
        CollectionAssert.AreEqual(
            first.Select(static group => group.Region.RegionId).ToArray(),
            reordered.Select(static group => group.Region.RegionId).ToArray());
        CollectionAssert.AreEqual(
            first.Select(static group => group.Region.Polygon.Bounds).ToArray(),
            reordered.Select(static group => group.Region.Polygon.Bounds).ToArray());
        CollectionAssert.AreEqual(
            first.Select(static group => string.Join("|", group.MemberRegionIds)).ToArray(),
            reordered.Select(static group => string.Join("|", group.MemberRegionIds)).ToArray());
        Assert.AreEqual(3, first.Sum(static group => group.MemberRegionIds.Count));
        Assert.HasCount(2, first);
    }

    [TestMethod]
    public void NeighboringNumericRowsRemainSeparateFromParticipantFragments()
    {
        OcrDetectedRegion upperTick = OcrTestFixtures.Region("upper-tick", 20, 20, 5, 8);
        OcrDetectedRegion word = OcrTestFixtures.Region("word", 2, 34, 12, 10);
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 16, 34, 6, 10);
        OcrDetectedRegion lowerTick = OcrTestFixtures.Region("lower-tick", 20, 49, 5, 8);

        IReadOnlyList<ParticipantLaneTextRegionAssemblyGroup> result =
            ParticipantLaneTextRegionAssembler.AssembleWithMembership(
                [upperTick, word, suffix, lowerTick],
                PlotBounds);

        Assert.HasCount(3, result);
        CollectionAssert.AreEquivalent(
            ExpectedMemberIds,
            result.Single(static group => group.MemberRegionIds.Count == 2)
                .MemberRegionIds.ToArray());
        Assert.IsTrue(result.Any(static group =>
            group.MemberRegionIds.SequenceEqual(["upper-tick"], StringComparer.Ordinal)));
        Assert.IsTrue(result.Any(static group =>
            group.MemberRegionIds.SequenceEqual(["lower-tick"], StringComparer.Ordinal)));
    }

    [TestMethod]
    public void HeightMismatchAndOutsideLanePairsRemainSeparate()
    {
        Assert.HasCount(2, ParticipantLaneTextRegionAssembler.Assemble(
            [OcrTestFixtures.Region("large", 2, 30, 14, 19),
             OcrTestFixtures.Region("small", 18, 37, 5, 5)],
            PlotBounds));
        Assert.HasCount(2, ParticipantLaneTextRegionAssembler.Assemble(
            [OcrTestFixtures.Region("inside-left", 34, 30, 12, 10),
             OcrTestFixtures.Region("inside-right", 48, 30, 6, 10)],
            PlotBounds));
        Assert.HasCount(2, ParticipantLaneTextRegionAssembler.Assemble(
            [OcrTestFixtures.Region("below-left", 2, 92, 12, 8),
             OcrTestFixtures.Region("below-right", 16, 92, 6, 8)],
            PlotBounds));
    }

    [TestMethod]
    public void HeaderFragmentsJoinWithoutAbsorbingOffsetPhaseLabelOrPlotText()
    {
        OcrDetectedRegion word = OcrTestFixtures.Region("word", 2, 1, 12, 8);
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 16, 1, 6, 8);
        OcrDetectedRegion phase = OcrTestFixtures.Region("phase", 24, 6, 5, 8);
        OcrDetectedRegion heading = OcrTestFixtures.Region("heading", 50, 1, 20, 8);
        var result = ParticipantLaneTextRegionAssembler.AssembleWithMembership(
            [heading, phase, suffix, word], PlotBounds);
        Assert.HasCount(3, result);
        var merged = result.Single(static group => group.MemberRegionIds.Count == 2);
        CollectionAssert.AreEqual(ExpectedMemberIds, merged.MemberRegionIds.ToArray());
        Assert.AreEqual(new OcrRectangle(2, 1, 20, 8), merged.Region.Polygon.Bounds);
        Assert.AreEqual(4, result.Sum(static group => group.MemberRegionIds.Count));
        Assert.IsNull(merged.Region.Context);
        var repeated = ParticipantLaneTextRegionAssembler.Assemble(
            result.Select(static group => group.Region).ToArray(), PlotBounds);
        CollectionAssert.AreEqual(result.Select(static group => group.Region.RegionId).ToArray(),
            repeated.Select(static region => region.RegionId).ToArray());
    }
}
