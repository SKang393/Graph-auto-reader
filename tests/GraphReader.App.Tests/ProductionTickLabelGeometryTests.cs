// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionTickLabelGeometryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NarrowGlyphCentersAreAlignedToOriginalTickPixels(bool paddedStride)
    {
        OcrRegion[] labels = [XLabel("x"), YLabel("y")];
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
            Axis(), Image([50], [40], paddedStride), labels, CancellationToken.None);
        Assert.AreEqual(50, result.Positions["x"]);
        Assert.AreEqual(40, result.Positions["y"]);
        Assert.AreEqual(44, labels[0].Polygon.Bounds.Center.X);
        Assert.AreEqual(37, labels[1].Polygon.Bounds.Center.Y);
        Assert.AreEqual("1", labels[0].Text);
        Assert.AreEqual("20", labels[1].Text);
        Assert.AreEqual(0.95, labels[0].Confidence);
        Assert.HasCount(2, result.Warnings);
    }

    [TestMethod]
    public void MissingTicksDoNotManufacturePositionEvidence()
    {
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
            Axis(), Image([], []), [XLabel("x"), YLabel("y")], CancellationToken.None);
        Assert.IsEmpty(result.Positions);
        Assert.IsEmpty(result.Warnings);
    }

    [TestMethod]
    public void SeveralVisibleTicksRemainAmbiguous()
    {
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
            Axis(), Image([40, 50], []), [XLabel("x")], CancellationToken.None);
        Assert.IsEmpty(result.Positions);
        Assert.AreEqual("tick_label_multiple_visible_ticks:x", result.Warnings.Single());
    }

    [TestMethod]
    public void SeveralLabelsCannotClaimTheSameTick()
    {
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
            Axis(), Image([50], []), [XLabel("first"), XLabel("second")], CancellationToken.None);
        Assert.IsEmpty(result.Positions);
        Assert.HasCount(2, result.Warnings);
        Assert.IsTrue(result.Warnings.All(static warning => warning.StartsWith("tick_label_shared_visible_tick:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RejectedAndNonTickTextDoNotContributePositionEvidence()
    {
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(Axis(), Image([50], []),
            [XLabel("rejected") with { ReviewStatus = OcrReviewStatus.Rejected },
             XLabel("annotation") with { Role = OcrTextRole.Annotation }], CancellationToken.None);
        Assert.IsEmpty(result.Positions);
    }

    [TestMethod]
    public void TextInTheSamplingBandDoesNotBecomeATick()
    {
        OcrRegion overlapping = XLabel("text") with { Polygon = OcrPolygon.FromRectangle(new(42, 72, 4, 12)) };
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
            Axis(), Image([50], []), [overlapping], CancellationToken.None);
        Assert.IsEmpty(result.Positions);
    }

    [TestMethod]
    public void SlantedAxisIsSampledInItsOriginalCoordinates()
    {
        AxisGeometryResult axis = Axis() with
        {
            XAxis = new(new(new(20, 70), new(80, 73)), 0.95, 0, 1, ["slanted"]),
        };
        byte[] pixels = Enumerable.Repeat((byte)255, 100 * 100).ToArray();
        for (int y = 72; y <= 77; y++) pixels[y * 100 + 50] = 0;
        var image = new OcrImage(100, 100, 100, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(axis, image, [XLabel("x")], CancellationToken.None);
        Assert.AreEqual(50, result.Positions["x"]);
    }

    [TestMethod]
    public void WrongCoordinateSpaceAndCancellationAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ProductionTickLabelGeometry.Resolve(Axis(),
            Image([50], []) with { SourceImage = OcrSourceImage.Enhanced }, [XLabel("x")], CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => ProductionTickLabelGeometry.Resolve(Axis(),
            Image([50], []) with { OriginalToImage = new(2, 2, 0, 0) }, [XLabel("x")], CancellationToken.None));
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionTickLabelGeometry.Resolve(
            Axis(), Image([50], []), [XLabel("x")], new CancellationToken(canceled: true)));
    }

    private static OcrRegion XLabel(string id) => new(id,
        OcrPolygon.FromRectangle(new(42, 78, 4, 12)), "1", [], OcrTextRole.XTick,
        0.95, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);

    private static OcrRegion YLabel(string id) => new(id,
        OcrPolygon.FromRectangle(new(4, 31, 10, 12)), "20", [], OcrTextRole.YTick,
        0.95, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);

    private static AxisGeometryResult Axis() => new(AxisGeometryCoordinateSpaces.OriginalPixels,
        new(new(20, 70), new(80, 70), new(80, 10), new(20, 10)),
        new(new(new(20, 70), new(80, 70)), 0.95, 0, 1, ["x"]),
        new(new(new(20, 10), new(20, 70)), 0.95, 0, 1, ["y"]), [], [], [], 0.95,
        new(0, 0, 1, false, []), new(2, 2, 0, 1, 1, 0, 0, 0, TimeSpan.Zero, []));

    private static OcrImage Image(int[] xTicks, int[] yTicks, bool paddedStride = false)
    {
        int stride = paddedStride ? 104 : 100;
        byte[] pixels = Enumerable.Repeat((byte)255, stride * 100).ToArray();
        for (int x = 20; x <= 80; x++) pixels[70 * stride + x] = 0;
        for (int y = 10; y <= 70; y++) pixels[y * stride + 20] = 0;
        foreach (int x in xTicks) for (int y = 70; y <= 75; y++) pixels[y * stride + x] = 0;
        foreach (int y in yTicks) for (int x = 14; x <= 20; x++) pixels[y * stride + x] = 0;
        return new(100, 100, stride, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
    }
}
