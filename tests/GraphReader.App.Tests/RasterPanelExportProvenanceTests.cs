// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.App.Integration.Workflow;
using GraphReader.Export;
using GraphReader.Pdf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class RasterPanelExportProvenanceTests
{
    [TestMethod]
    public void SourceAndCropAreImmutableAndRejectInvalidProvenance()
    {
        byte[] bytes = [1, 2, 3];
        var source = new RasterSourceImageEvidence(Image(bytes, 300, 400), new ImmutableByteBuffer(bytes));
        bytes[0] = 99;
        byte[] copy = source.CopyBytes();
        copy[1] = 99;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, source.CopyBytes());
        var crop = new RasterPanelSourceProvenance(source, Hash([4]),
            new PdfRectD(100.2, 200.2, 49.5, 79.5), new PdfRectD(100, 200, 50, 80));
        Assert.AreEqual(new PdfPointD(120, 230), crop.MapPanelPixelToSource(new PdfPointD(20, 30)));
        Assert.AreEqual(new PdfPointD(20, 30), crop.MapSourcePixelToPanel(new PdfPointD(120, 230)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => crop.MapPanelPixelToSource(new PdfPointD(51, 30)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => crop.MapSourcePixelToPanel(new PdfPointD(99, 230)));
        Assert.ThrowsExactly<ArgumentException>(() => new RasterSourceImageEvidence(source.Image, new ImmutableByteBuffer([9])));
        Assert.ThrowsExactly<ArgumentException>(() => new RasterPanelSourceProvenance(source, Hash([4]),
            new PdfRectD(100, 200, 50, 80), new PdfRectD(100.5, 200, 50, 80)));
        Assert.ThrowsExactly<ArgumentException>(() => new RasterPanelSourceProvenance(source, Hash([4]),
            new PdfRectD(100, 200, 50, 80), new PdfRectD(100, 200, 49, 80)));
        Assert.ThrowsExactly<ArgumentException>(() => new RasterPanelSourceProvenance(source, Hash([4]),
            new PdfRectD(290, 200, 50, 80), new PdfRectD(290, 200, 50, 80)));
    }

    [TestMethod]
    public async Task ExportTranslatesPointsAndPhasesToFullSourceWithoutChangingScientificValues()
    {
        Fixture fixture = CreateFixture(withCrop: true);
        var sink = new CapturingExportService();
        WorkflowExportResult result = await new ProductionWorkflowExportStage(fixture.Store, sink)
            .ExportAsync(fixture.Review, new WorkflowExportRequest(Guid.NewGuid(), "unused"), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        ExportRequest request = sink.Request!;
        Assert.AreEqual(new ExportPixelPoint(120, 230), request.Points.Single().OriginalPixel);
        Assert.AreEqual(1d, request.Points.Single().GraphX);
        Assert.AreEqual(42d, request.Points.Single().GraphY);
        Assert.AreEqual(100d, request.Phases.Single().OriginalXMinimum);
        Assert.AreEqual(150d, request.Phases.Single().OriginalXMaximum);
        Assert.AreEqual(20d, fixture.Review.Panels.Single().Points.Single().OriginalPixelX);
        Assert.AreEqual(0d, fixture.Store.Get(fixture.PanelId).ExportEvidence!.Phases.Single().OriginalXMinimum);
    }

    [TestMethod]
    public async Task LegacyImageExportKeepsItsOriginalCoordinates()
    {
        Fixture fixture = CreateFixture(withCrop: false);
        var sink = new CapturingExportService();
        WorkflowExportResult result = await new ProductionWorkflowExportStage(fixture.Store, sink)
            .ExportAsync(fixture.Review, new WorkflowExportRequest(Guid.NewGuid(), "unused"), CancellationToken.None);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(new ExportPixelPoint(20, 30), sink.Request!.Points.Single().OriginalPixel);
        Assert.AreEqual(0d, sink.Request.Phases.Single().OriginalXMinimum);
    }

    [TestMethod]
    public async Task OutOfCropPointBlocksExportBeforeWriting()
    {
        Fixture fixture = CreateFixture(withCrop: true, pointX: 51);
        var sink = new CapturingExportService();
        WorkflowExportResult result = await new ProductionWorkflowExportStage(fixture.Store, sink)
            .ExportAsync(fixture.Review, new WorkflowExportRequest(Guid.NewGuid(), "unused"), CancellationToken.None);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ProductionWorkflowFailureCodes.RecalibrationRequired, result.FailureCode);
        Assert.IsNull(sink.Request);
    }

    [TestMethod]
    public void StoreRetainsCropAcrossUpdatesAndRejectsConflictingOriginalSource()
    {
        Fixture fixture = CreateFixture(withCrop: true);
        ProductionPanelEvidence original = fixture.Store.Get(fixture.PanelId);
        RasterPanelSourceProvenance crop = original.RasterPanelSource!;
        fixture.Store.SetPreparation(fixture.PanelId, null, null, [], []);
        fixture.Store.SetExportEvidence(fixture.PanelId, original.ExportEvidence!);
        Assert.AreSame(crop, fixture.Store.Get(fixture.PanelId).RasterPanelSource);
        var changedSource = new RasterSourceImageEvidence(Image([7, 8, 9], 300, 400), new ImmutableByteBuffer([7, 8, 9]));
        var changedCrop = new RasterPanelSourceProvenance(changedSource, crop.PanelImageSha256,
            crop.RequestedCropInSourcePixels, crop.EncodedCropInSourcePixels);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Register(new ProductionPanelEvidence(
            original.Panel, WorkflowSourceKind.Image, original.CopyOriginalBytes(), rasterPanelSource: changedCrop)));
        var movedSource = new RasterSourceImageEvidence(new WorkflowImageEvidence("memory:moved.png",
            crop.Source.Image.Sha256, 300, 400, WorkflowImageVariant.Original),
            new ImmutableByteBuffer(crop.Source.CopyBytes()));
        var movedCrop = new RasterPanelSourceProvenance(movedSource, crop.PanelImageSha256,
            crop.RequestedCropInSourcePixels, crop.EncodedCropInSourcePixels);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Register(new ProductionPanelEvidence(
            original.Panel, WorkflowSourceKind.Image, original.CopyOriginalBytes(), rasterPanelSource: movedCrop)));
    }

    [TestMethod]
    public async Task ReviewWithDifferentRasterDimensionsCannotReuseStoredExportEvidence()
    {
        Fixture fixture = CreateFixture(withCrop: true);
        WorkflowReviewPanel original = fixture.Review.Panels.Single();
        WorkflowImportedPanel imported = original.PreparedPanel.ImportedPanel;
        WorkflowImageEvidence wrongImage = Image([4, 5, 6], 60, 80);
        var wrongPanel = new WorkflowImportedPanel(imported.PanelId, imported.SourceId, imported.DisplayName, wrongImage);
        var wrongReview = new WorkflowReviewState(fixture.Review.ProjectId,
            [new WorkflowReviewPanel(new WorkflowPreparedPanel(wrongPanel, wrongImage, null), original.Points)]);
        var sink = new CapturingExportService();
        WorkflowExportResult result = await new ProductionWorkflowExportStage(fixture.Store, sink)
            .ExportAsync(wrongReview, new WorkflowExportRequest(Guid.NewGuid(), "unused"), CancellationToken.None);
        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(sink.Request);
    }

    private static Fixture CreateFixture(bool withCrop, double pointX = 20)
    {
        Guid projectId = Guid.NewGuid(), panelId = Guid.NewGuid(), pointId = Guid.NewGuid(),
            seriesId = Guid.NewGuid(), phaseId = Guid.NewGuid();
        byte[] bytes = [4, 5, 6];
        WorkflowImageEvidence image = Image(bytes, 50, 80);
        var imported = new WorkflowImportedPanel(panelId, Guid.NewGuid(), "synthetic.png", image);
        var envelope = new WorkflowVisionEnvelope(1, Guid.NewGuid(), projectId, panelId, "markers",
            "fixture", image.Sha256, null, new WorkflowVisionTiming(0, 0, 0, 0), 1);
        var exportEvidence = new ProductionPanelExportEvidence(
            new ExportCalibration(ExportCalibrationStatus.Valid, true, true, true, 1, 1),
            [new ExportPhase(phaseId, 1, "b", ExportPhaseType.Intervention, null, 0, 50, 1)],
            [new ExportSeries(seriesId, "●", "Series", ExportSeriesRole.Intervention, [pointId], 1)], [],
            [new ProductionPointExportEvidence(pointId, null, 1, 1, null, ExportXValueSource.Printed, 1, 1)],
            [envelope]);
        RasterPanelSourceProvenance? crop = withCrop
            ? new RasterPanelSourceProvenance(
                new RasterSourceImageEvidence(Image([1, 2, 3], 300, 400), new ImmutableByteBuffer([1, 2, 3])),
                image.Sha256, new PdfRectD(100, 200, 50, 80), new PdfRectD(100, 200, 50, 80))
            : null;
        var store = new ProductionWorkflowPanelStore();
        store.Register(new ProductionPanelEvidence(imported, WorkflowSourceKind.Image, bytes,
            exportEvidence: exportEvidence, rasterPanelSource: crop));
        var point = new WorkflowPoint(pointId.ToString(), "fixture-point", pointX, 30, 1,
            WorkflowImageVariant.Original, WorkflowReviewStatus.Accepted, "●", "circle", "filled",
            seriesId.ToString(), phaseId.ToString(), 1, 42, "markers", null, false);
        var review = new WorkflowReviewState(projectId,
            [new WorkflowReviewPanel(new WorkflowPreparedPanel(imported, image, null), [point], [envelope])]);
        return new Fixture(store, review, panelId);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static WorkflowImageEvidence Image(byte[] bytes, int width, int height) =>
        new("memory:source.png", Hash(bytes), width, height, WorkflowImageVariant.Original);
    private sealed record Fixture(ProductionWorkflowPanelStore Store, WorkflowReviewState Review, Guid PanelId);

    private sealed class CapturingExportService : IExportService
    {
        public ExportRequest? Request { get; private set; }
        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new ExportResult(request.RunId, request.ProjectId, request.PanelId,
                request.Mode, new ExportPreview([], false), [], [], new ExportDeterminism("fixture", Hash([])),
                new ExportTiming(0, 0, 0, 0, 0)));
        }
    }
}
