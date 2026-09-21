// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class ContainedTextFragmentResolverTests
{
    [TestMethod]
    public void CompleteReadingRetainsItsOriginalFieldsAndRemovesNestedFragments()
    {
        OcrRegion complete = Region("complete", "Follow-up", new(10, 10, 100, 20));
        OcrRegion fragment = Region("fragment", "oll o", new(30, 12, 30, 12));
        OcrRegion nested = Region("nested", "lo", new(40, 14, 10, 8));
        OcrRegion separate = Region("separate", "lo", new(120, 12, 10, 8));
        OcrRegion[] input = [nested, complete, fragment, separate];

        ContainedTextFragmentResolution result = ContainedTextFragmentResolver.Resolve(input);

        Assert.HasCount(2, result.Regions);
        Assert.AreSame(complete, result.Regions[0]);
        Assert.AreSame(separate, result.Regions[1]);
        Assert.HasCount(2, result.RemovedRegionIds);
        Assert.HasCount(4, input);
        Assert.AreSame(nested, input[0]);
    }

    [TestMethod]
    [DataRow("20", OcrTextRole.Other)]
    [DataRow("−2.5%", OcrTextRole.Annotation)]
    [DataRow("1,000", OcrTextRole.Other)]
    [DataRow(".5", OcrTextRole.Other)]
    [DataRow("lo", OcrTextRole.YTick)]
    [DataRow("lo", OcrTextRole.XTick)]
    public void NumericReadingsAndTickRolesAreNeverRemoved(string text, OcrTextRole role)
    {
        OcrRegion child = Region("child", text, new(30, 12, 20, 12)) with { Role = role };
        OcrRegion parent = Region("parent", "value " + text, new(10, 10, 100, 20));
        Assert.HasCount(2, ContainedTextFragmentResolver.Resolve([parent, child]).Regions);
    }

    [TestMethod]
    [DataRow("follow-up", 30)]
    [DataRow("not present", 30)]
    [DataRow("lo", 105)]
    [DataRow("Follow-up", 30)]
    public void CaseConflictsDifferentReadingsPartialOverlapAndEqualTextRemain(string text, double x)
    {
        OcrRegion parent = Region("parent", "Follow-up", new(10, 10, 100, 20));
        OcrRegion child = Region("child", text, new(x, 12, 20, 12));
        Assert.HasCount(2, ContainedTextFragmentResolver.Resolve([parent, child]).Regions);
    }

    [TestMethod]
    [DataRow(OcrReviewStatus.Accepted)]
    [DataRow(OcrReviewStatus.Corrected)]
    [DataRow(OcrReviewStatus.Rejected)]
    public void UserReviewDecisionsArePreserved(OcrReviewStatus reviewStatus)
    {
        OcrRegion parent = Region("parent", "Follow-up", new(10, 10, 100, 20));
        OcrRegion child = Region("child", "lo", new(30, 12, 20, 12)) with { ReviewStatus = reviewStatus };
        Assert.HasCount(2, ContainedTextFragmentResolver.Resolve([parent, child]).Regions);
    }

    [TestMethod]
    public void RejectedParentsAndDifferentImageEvidenceDoNotSuppressText()
    {
        OcrRegion parent = Region("parent", "Follow-up", new(10, 10, 100, 20));
        OcrRegion child = Region("child", "lo", new(30, 12, 20, 12));
        Assert.HasCount(2, ContainedTextFragmentResolver.Resolve(
            [parent with { ReviewStatus = OcrReviewStatus.Rejected }, child]).Regions);
        Assert.HasCount(2, ContainedTextFragmentResolver.Resolve(
            [parent with { SourceImage = OcrSourceImage.Enhanced }, child]).Regions);
        Assert.HasCount(2, ContainedTextFragmentResolver.Resolve(
            [parent with { CoordinateSpace = "enhanced_pixels" }, child]).Regions);
    }

    [TestMethod]
    public void NonRectangularPolygonsAndConflictingPrimaryReadingsRemain()
    {
        OcrRegion parent = Region("parent", "Follow-up", new(10, 10, 100, 20)) with
        {
            Polygon = new OcrPolygon([new(10, 20), new(60, 10), new(110, 20), new(60, 30)]),
        };
        OcrRegion child = Region("child", "lo", new(12, 11, 10, 8));
        Assert.HasCount(2, ContainedTextFragmentResolver.Resolve([parent, child]).Regions);
        parent = Region("parent", "Follow-up", new(10, 10, 100, 20));
        child = child with { Text = "10", Alternatives = [new("lo", 0.9, OcrSourceImage.Original)] };
        Assert.HasCount(2, ContainedTextFragmentResolver.Resolve([parent, child]).Regions);
    }

    [TestMethod]
    public void CancellationIsObservedEvenForEmptyInput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            ContainedTextFragmentResolver.Resolve([], cancellation.Token));
    }

    [TestMethod]
    public async Task PipelineKeepsBothRecognitionCropsButCachesOnlyCompleteRegionAndMask()
    {
        var observed = new List<OcrCrop>();
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            observed.AddRange(crops);
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop => new OcrRecognition(
                crop.RegionId, crop.SourceImage,
                [new(crop.RegionId == "parent" ? "Sessions" : "es", 0.96, crop.SourceImage)], 0.1)).ToArray());
        });
        OcrRequest request = OcrTestFixtures.Request([
            OcrTestFixtures.Region("parent", 50, 86, 70, 12, context: new(AxisTitleExpected: true)),
            OcrTestFixtures.Region("child", 65, 88, 10, 8, context: new(AxisTitleExpected: true))]);
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), recognizer, new InMemoryOcrResultCache());
        OcrResult result = await pipeline.RecognizeAsync(request);
        OcrResult cached = await pipeline.RecognizeAsync(request);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(2, observed);
        Assert.IsTrue(observed.All(static crop => crop.SourceImage == OcrSourceImage.Original));
        Assert.HasCount(1, result.Regions);
        Assert.AreEqual("Sessions", result.Regions[0].Text);
        Assert.HasCount(1, result.Masks);
        Assert.AreEqual("parent", result.Masks[0].RegionId);
        CollectionAssert.Contains(result.Warnings.ToArray(), "ocr_duplicate_text_fragment_removed:child");
        Assert.IsTrue(cached.Cache.CacheHit);
        Assert.HasCount(1, cached.Regions);
        Assert.HasCount(1, cached.Masks);
        Assert.AreEqual(1, recognizer.CallCount);
    }

    private static OcrRegion Region(string id, string text, OcrRectangle bounds) =>
        new(id, OcrPolygon.FromRectangle(bounds), text, [new(text, 0.95, OcrSourceImage.Original)],
            OcrTextRole.Other, 0.8, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);
}
