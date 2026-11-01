// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class DenseTickTextRegionRecoveryTests
{
    private static readonly OcrRectangle Plot = new(40, 10, 150, 90);
    private static readonly double[] ExpectedLeftEdges = [51d, 71d, 91d, 111d];

    [TestMethod]
    public void RepeatedMeasuredStrokesCreateBoundedStableCropsWithoutValues()
    {
        OcrRegion source = Source();
        OcrXAxisTickStroke[] strokes = [Stroke("a", 60), Stroke("b", 80), Stroke("c", 100), Stroke("d", 120)];
        IReadOnlyList<DenseTickCrop> crops = DenseTickTextRegionRecovery.Select([source], strokes, Plot, default);
        Assert.HasCount(4, crops);
        CollectionAssert.AreEqual(ExpectedLeftEdges,
            crops.Select(static crop => crop.Region.Polygon.Bounds.Left).ToArray());
        Assert.IsTrue(crops.All(crop => crop.SourceRegionId == source.RegionId &&
            crop.Region.Context?.ExplicitRoleHint == OcrTextRole.XTick));
        CollectionAssert.AreEqual(crops.Select(static crop => crop.Region.Polygon.Bounds).ToArray(),
            DenseTickTextRegionRecovery.Select([source], strokes.Reverse().ToArray(), Plot, default)
                .Select(static crop => crop.Region.Polygon.Bounds).ToArray());
    }

    [TestMethod]
    public void DuplicateAndUnsupportedStrokesCannotCreateMeasuredSpacing()
    {
        OcrXAxisTickStroke[] strokes = [Stroke("a", 60), Stroke("a2", 63), Stroke("b", 80),
            Stroke("b2", 83), Stroke("c", 100) with { SupportingCandidateIds = [] }, Stroke("d", 120)];
        Assert.IsEmpty(DenseTickTextRegionRecovery.Select([Source()], strokes, Plot, default));
    }

    [TestMethod]
    public void RoundingOverlapIsTrimmedInsteadOfDiscardingAnObservedTick()
    {
        OcrXAxisTickStroke[] strokes = [Stroke("a", 60), Stroke("b", 78), Stroke("c", 96), Stroke("d", 113)];
        IReadOnlyList<DenseTickCrop> crops = DenseTickTextRegionRecovery.Select([Source()], strokes, Plot, default);
        Assert.HasCount(4, crops);
        Assert.IsTrue(crops.Zip(crops.Skip(1), static (left, right) =>
            left.Region.Polygon.Bounds.Right <= right.Region.Polygon.Bounds.Left).All(static valid => valid));
    }

    [TestMethod]
    public void ZeroLengthStrokesCannotSupplyTickGeometry()
    {
        OcrXAxisTickStroke[] strokes = [Stroke("a", 60), Stroke("b", 80), Stroke("c", 100), Stroke("d", 120)];
        OcrXAxisTickStroke[] points = strokes.Select(static stroke => stroke with
        {
            LineStart = stroke.Center,
            LineEnd = stroke.Center,
        }).ToArray();
        Assert.IsEmpty(DenseTickTextRegionRecovery.Select([Source()], points, Plot, default));
    }

    [TestMethod]
    public void NonNumericOrMultipleWideRegionsLeaveBaselineUntouched()
    {
        OcrXAxisTickStroke[] strokes = [Stroke("a", 60), Stroke("b", 80), Stroke("c", 100), Stroke("d", 120)];
        Assert.IsEmpty(DenseTickTextRegionRecovery.Select([Source() with { Text = "horizontal title" }], strokes, Plot, default));
        Assert.IsEmpty(DenseTickTextRegionRecovery.Select([Source(), Source() with { RegionId = "second" }], strokes, Plot, default));
        Assert.IsEmpty(DenseTickTextRegionRecovery.Select([Source() with { ReviewStatus = OcrReviewStatus.Accepted }], strokes, Plot, default));
    }

    [TestMethod]
    public void CancellationIsHonored()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            DenseTickTextRegionRecovery.Select([Source()], [], Plot, cancellation.Token));
    }

    private static OcrRegion Source() => new("wide", OcrPolygon.FromRectangle(new OcrRectangle(50, 105, 100, 10)),
        "1234567890", [], OcrTextRole.XTick, .9, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);

    private static OcrXAxisTickStroke Stroke(string id, double x) =>
        new(id, new(x, 100), new(x, 98), new(x, 103), .9, [id]);
}
