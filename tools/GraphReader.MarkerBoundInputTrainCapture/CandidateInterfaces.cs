// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.RealAcceptance.Ocr;

// Pre-marker composition requires the interface, but this stub never loads,
// identifies, approves, invokes or produces results from any such model.
internal sealed class NoClassification : IProductionCandidateMarkerClassificationAdapter
{
    public string AdapterId => throw new InvalidOperationException("No classification stage exists in this diagnostic.");
    public bool IsApproved => false;
    public ModelIdentity Model => throw new InvalidOperationException("No classification model exists in this diagnostic.");
    public Task<ProductionMarkerClassificationEvidence> ClassifyAsync(ProductionWorkflowDetectionRequest request,
        MarkerImageFrame image, IReadOnlyList<MarkerCenter> markers, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Classification is unavailable in this diagnostic.");
    public Task<ProductionMarkerClassificationEvidence> ClassifyForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request, MarkerImageFrame image, IReadOnlyList<MarkerCenter> markers,
        IReadOnlyDictionary<string, MarkerRectangle> originalPixelContentBounds, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Classification is unavailable in this diagnostic.");
}

// Captures the exact raster and OCR evidence passed through the candidate
// pre-marker workflow. It delegates both candidate overloads unchanged.
internal sealed class ObservedOcr : IProductionCandidateOcrAdapter
{
    private readonly IProductionCandidateOcrAdapter inner;
    private readonly Dictionary<Guid, (ProductionDecodedRaster Raster, ProductionOcrEvidence Evidence)> captured = [];
    internal ObservedOcr(IProductionCandidateOcrAdapter inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (inner.IsApproved) throw new ArgumentException("Unapproved candidate OCR required.", nameof(inner));
    }
    public string AdapterId => inner.AdapterId;
    public bool IsApproved => false;
    public Task<ProductionOcrEvidence> RecognizeAsync(ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster, OcrRectangle bounds, OcrDetectorImage image, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Production execution is unavailable.");
    public async Task<ProductionOcrEvidence> RecognizeForCandidateEvaluationAsync(ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster, OcrRectangle bounds, OcrDetectorImage image, IReadOnlyList<double> phaseDividers,
        CancellationToken cancellationToken)
    {
        ProductionOcrEvidence result = await inner.RecognizeForCandidateEvaluationAsync(request, raster, bounds, image,
            phaseDividers, cancellationToken).ConfigureAwait(false);
        Retain(request, raster, result); return result;
    }
    public async Task<ProductionOcrEvidence> RecognizeForCandidateEvaluationAsync(ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster, OcrRectangle bounds, OcrDetectorImage image, IReadOnlyList<double> phaseDividers,
        IReadOnlyList<OcrXAxisTickStroke> strokes, CancellationToken cancellationToken)
    {
        ProductionOcrEvidence result = await inner.RecognizeForCandidateEvaluationAsync(request, raster, bounds, image,
            phaseDividers, strokes, cancellationToken).ConfigureAwait(false);
        Retain(request, raster, result); return result;
    }
    private void Retain(ProductionWorkflowDetectionRequest request, ProductionDecodedRaster raster, ProductionOcrEvidence result)
    {
        if (!captured.TryAdd(request.Panel.ImportedPanel.PanelId, (raster, result)))
            throw new InvalidOperationException("OCR observation repeated for a panel.");
    }
    internal (ProductionDecodedRaster Raster, ProductionOcrEvidence Evidence) ForPanel(Guid panelId) =>
        captured.TryGetValue(panelId, out var value) ? value : throw new InvalidOperationException("OCR observation was not produced.");
}

internal sealed class ObservedAxis : IProductionCandidateAxisGeometryAdapter
{
    private readonly IProductionCandidateAxisGeometryAdapter inner;
    private readonly Dictionary<Guid, ProductionAxisGeometryEvidence> captured = [];
    internal ObservedAxis(IProductionCandidateAxisGeometryAdapter inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (inner.IsApproved) throw new ArgumentException("Unapproved candidate axis required.", nameof(inner));
    }
    public string AdapterId => inner.AdapterId;
    public bool IsApproved => false;
    public Task<ProductionAxisGeometryEvidence> DetectAsync(ProductionWorkflowDetectionRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Production execution is unavailable.");
    public async Task<ProductionAxisGeometryEvidence> DetectForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request, CancellationToken cancellationToken)
    {
        ProductionAxisGeometryEvidence result = await inner.DetectForCandidateEvaluationAsync(request, cancellationToken).ConfigureAwait(false);
        if (!captured.TryAdd(request.Panel.ImportedPanel.PanelId, result))
            throw new InvalidOperationException("Axis observation repeated for a panel.");
        return result;
    }
    internal ProductionAxisGeometryEvidence ForPanel(Guid panelId) => captured.TryGetValue(panelId, out var value)
        ? value : throw new InvalidOperationException("Axis observation was not produced.");
}
