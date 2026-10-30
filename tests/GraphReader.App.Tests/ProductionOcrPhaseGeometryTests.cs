// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionOcrPhaseGeometryTests
{
    private static readonly OcrRectangle Plot = new(10, 10, 80, 80);
    private static readonly double[] ExpectedDividers = [30, 70];

    [TestMethod]
    public void MeasuredMidpointsRemainInPanelCoordinatesAndAreSortedUniqueAndReadOnly()
    {
        IReadOnlyList<double> result = ProductionAutomaticDetectionAdapter.CreateOcrPhaseDividerXs(
            "original_pixels", [Divider(70), Divider(30), Divider(70)], Plot);
        CollectionAssert.AreEqual(ExpectedDividers, result.ToArray());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<double>)result)[0] = 5);
        Assert.IsEmpty(ProductionAutomaticDetectionAdapter.CreateOcrPhaseDividerXs("original_pixels", [], Plot));
    }

    [TestMethod]
    [DataRow(9d)]
    [DataRow(91d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void InvalidBoundaryCannotReachOcr(double x)
    {
        ProductionWorkflowStageException error = Assert.ThrowsExactly<ProductionWorkflowStageException>(() =>
            ProductionAutomaticDetectionAdapter.CreateOcrPhaseDividerXs("original_pixels", [Divider(x)], Plot));
        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionEvidenceRejected, error.Failure.Code);
    }

    [TestMethod]
    public void DerivedCoordinatesAndInvalidPlotCannotReachOcr()
    {
        Assert.ThrowsExactly<ProductionWorkflowStageException>(() =>
            ProductionAutomaticDetectionAdapter.CreateOcrPhaseDividerXs("enhanced_pixels", [Divider(50)], Plot));
        Assert.ThrowsExactly<ProductionWorkflowStageException>(() =>
            ProductionAutomaticDetectionAdapter.CreateOcrPhaseDividerXs("original_pixels", [], Plot with { Width = double.PositiveInfinity }));
        PhaseDividerGeometry badY = Divider(50) with
        {
            Line = new GeometryLineSegment(new PixelPoint(50, double.NaN), new PixelPoint(50, 90)),
        };
        Assert.ThrowsExactly<ProductionWorkflowStageException>(() =>
            ProductionAutomaticDetectionAdapter.CreateOcrPhaseDividerXs("original_pixels", [badY], Plot));
    }

    [TestMethod]
    public void OptionalUnusableAxisTicksAreOmittedInsteadOfInvalidatingOcr()
    {
        AxisTickGeometry usable = Tick("usable", 30, 0.9, ["axis-candidate"]);
        AxisTickGeometry unsupported = Tick("unsupported", 50, 0.9, []);
        AxisTickGeometry lowConfidence = Tick("low-confidence", 70, 0, ["axis-candidate"]);

        IReadOnlyList<OcrXAxisTickStroke> result = ProductionAutomaticDetectionAdapter.CreateOcrXAxisTickStrokes(
            Geometry(usable, unsupported, lowConfidence));

        Assert.HasCount(1, result);
        Assert.AreEqual("usable", result[0].TickId);
        Assert.AreEqual(30d, result[0].Center.X);
    }

    [TestMethod]
    public void SeededLabelMustReassociateWithItsEncodedUniqueOriginalTick()
    {
        AxisGeometryResult axis = Geometry(Tick("seed", 30, 0.9, ["candidate"]),
            Tick("other", 70, 0.9, ["candidate"]));
        const string regionId = "tick-lane-axis-seed:seed:crop";

        Assert.IsTrue(ProductionAutomaticDetectionAdapter.IsSeededXTickAssociated(axis, regionId,
            new Dictionary<string, double> { [regionId] = 30 }));
        Assert.IsFalse(ProductionAutomaticDetectionAdapter.IsSeededXTickAssociated(axis, regionId,
            new Dictionary<string, double> { [regionId] = 70 }));
        Assert.IsFalse(ProductionAutomaticDetectionAdapter.IsSeededXTickAssociated(
            Geometry(Tick("seed", 30, 0.9, ["candidate"]), Tick("seed", 30, 0.9, ["candidate-2"])),
            regionId, new Dictionary<string, double> { [regionId] = 30 }));
    }

    private static PhaseDividerGeometry Divider(double x) =>
        new("measured", new GeometryLineSegment(new PixelPoint(x, 10), new PixelPoint(x, 90)),
            DividerStyle.Solid, 0.9, 1, 1, []);

    private static AxisTickGeometry Tick(string id, double x, double confidence, IReadOnlyList<string> support) =>
        new(id, TickAxis.XAxis, new PixelPoint(x, 90),
            new GeometryLineSegment(new PixelPoint(x, 89), new PixelPoint(x, 94)), confidence, support);

    private static AxisGeometryResult Geometry(params AxisTickGeometry[] ticks) => new(
        AxisGeometryCoordinateSpaces.OriginalPixels,
        new(new PixelPoint(10, 10), new PixelPoint(90, 10), new PixelPoint(90, 90), new PixelPoint(10, 90)),
        new(new(new PixelPoint(10, 90), new PixelPoint(90, 90)), 0.9, 0, 1, ["axis-x"]),
        new(new(new PixelPoint(10, 10), new PixelPoint(10, 90)), 0.9, 0, 1, ["axis-y"]),
        ticks, [], [], 0.9, new(0, 0, 1, false, []), new(2, 2, 0, 1, 1, 0, 0, 0, TimeSpan.Zero, []));
}
