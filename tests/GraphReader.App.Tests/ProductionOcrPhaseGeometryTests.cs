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

    private static PhaseDividerGeometry Divider(double x) =>
        new("measured", new GeometryLineSegment(new PixelPoint(x, 10), new PixelPoint(x, 90)),
            DividerStyle.Solid, 0.9, 1, 1, []);
}
