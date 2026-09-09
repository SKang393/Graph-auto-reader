// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Export;
using GraphReader.Imaging;
using GraphReader.Pdf;

namespace GraphReader.App.Integration.Workflow;

/// <summary>
/// Candidate execution is a local validation boundary. It can execute only an
/// explicitly unapproved adapter and does not change production availability.
/// </summary>
internal interface IProductionCandidateWorkflowDetectionAdapter
{
    string AdapterId { get; }

    bool IsApproved { get; }

    Task<WorkflowDetectionBatch> DetectForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        CancellationToken cancellationToken);
}

internal interface IProductionCandidateAxisGeometryAdapter : IProductionAxisGeometryAdapter
{
    Task<ProductionAxisGeometryEvidence> DetectForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        CancellationToken cancellationToken);
}

internal interface IProductionCandidateOcrAdapter : IProductionOcrAdapter
{
    Task<ProductionOcrEvidence> RecognizeForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster originalRaster,
        Ocr.OcrRectangle plotBounds,
        Ocr.OcrDetectorImage detectorImage,
        CancellationToken cancellationToken);
}

internal interface IProductionCandidateDetectionMaskComposer : IProductionDetectionMaskComposer
{
    Task<ProductionDetectionMaskEvidence> ComposeForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster,
        ProductionAxisGeometryEvidence axisEvidence,
        ProductionOcrEvidence ocrEvidence,
        CancellationToken cancellationToken);
}

internal interface IProductionCandidateMarkerCenterAdapter : IProductionMarkerCenterAdapter
{
    Task<ProductionMarkerCenterEvidence> DetectForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        Markers.Detection.MarkerImageFrame originalImage,
        Markers.Detection.MarkerPolygon plotPolygon,
        CancellationToken cancellationToken);
}

internal static class ProductionCandidateWorkflowComposition
{
    internal static WorkflowOrchestrator Create(
        ProductionWorkflowPanelStore panelStore,
        IImageImportService imageImportService,
        ProductionAutomaticDetectionAdapter candidateAdapter,
        IExportService exportService,
        IPdfImportService? pdfImportService = null,
        ProductionRasterPanelizer? rasterPanelizer = null)
    {
        ArgumentNullException.ThrowIfNull(panelStore);
        ArgumentNullException.ThrowIfNull(imageImportService);
        ArgumentNullException.ThrowIfNull(candidateAdapter);
        ArgumentNullException.ThrowIfNull(exportService);
        if (candidateAdapter.IsApproved)
        {
            throw new ArgumentException(
                "Candidate workflow composition requires an explicitly unapproved adapter.",
                nameof(candidateAdapter));
        }

        var importer = rasterPanelizer is null
            ? new ProductionWorkflowImportStage(panelStore, imageImportService, pdfImportService)
            : new ProductionWorkflowImportStage(
                panelStore,
                imageImportService,
                pdfImportService,
                rasterPanelizer);
        return new WorkflowOrchestrator(new WorkflowServiceSet(
            importer,
            new ProductionWorkflowPrepareStage(panelStore),
            new ProductionCandidateWorkflowDetectionStage(panelStore, candidateAdapter),
            new ProductionWorkflowExportStage(panelStore, exportService)));
    }
}
