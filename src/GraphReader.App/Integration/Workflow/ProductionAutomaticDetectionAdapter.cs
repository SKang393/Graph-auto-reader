// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.Text.Json;
using GraphReader.Axis;
using GraphReader.Domain;
using GraphReader.Export;
using GraphReader.Legends;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Markers.Grouping;
using GraphReader.Ocr;
using GraphReader.Phases;
using DomainCalibrationAnchor = GraphReader.Domain.CalibrationAnchor;
using DomainCalibrationAnchorKind = GraphReader.Domain.CalibrationAnchorKind;
using DomainCalibrationStatus = GraphReader.Domain.CalibrationStatus;
using DomainMarkerFill = GraphReader.Domain.MarkerFill;
using DomainMarkerShape = GraphReader.Domain.MarkerShape;
using DomainPhaseNormalizedType = GraphReader.Domain.PhaseNormalizedType;
using DomainPhaseSource = GraphReader.Domain.PhaseSource;
using DomainPixelPoint = GraphReader.Domain.PixelPoint;
using DomainReviewStatus = GraphReader.Domain.ReviewStatus;
using DomainSemanticRole = GraphReader.Domain.SemanticRole;
using DomainSourceImageKind = GraphReader.Domain.SourceImageKind;
using MarkerFill = GraphReader.Markers.Classification.MarkerFill;
using MarkerShape = GraphReader.Markers.Classification.MarkerShape;
using PhaseNormalizedType = GraphReader.Phases.PhaseNormalizedType;

namespace GraphReader.App.Integration.Workflow;

/// <summary>
/// Executes the approved original-image production chain and materializes the
/// exact review and export projection consumed by the WPF workspace. The
/// adapter is not composed unless every required component reports approval.
/// </summary>
public sealed class ProductionAutomaticDetectionAdapter :
    IProductionWorkflowDetectionAdapter,
    IProductionCandidateWorkflowDetectionAdapter
{
    private const double ArtifactRejectionThreshold = 0.5;
    private readonly ProductionWorkflowPanelStore panelStore;
    private readonly IProductionRasterFrameDecoder rasterDecoder;
    private readonly IProductionAxisGeometryAdapter axisAdapter;
    private readonly IProductionOcrAdapter ocrAdapter;
    private readonly IProductionDetectionMaskComposer maskComposer;
    private readonly IProductionMarkerCenterAdapter markerCenterAdapter;
    private readonly IProductionMarkerClassificationAdapter markerClassificationAdapter;
    private readonly IProductionLegendReasoningAdapter legendAdapter;
    private readonly IProductionPhaseReasoningAdapter phaseAdapter;
    private readonly IMarkerConnectionGraphBuilder connectionBuilder;
    private readonly IMarkerSeriesGrouper seriesGrouper;

    public ProductionAutomaticDetectionAdapter(
        ProductionWorkflowPanelStore panelStore,
        IProductionRasterFrameDecoder rasterDecoder,
        IProductionAxisGeometryAdapter axisAdapter,
        IProductionOcrAdapter ocrAdapter,
        IProductionDetectionMaskComposer maskComposer,
        IProductionMarkerCenterAdapter markerCenterAdapter,
        IProductionMarkerClassificationAdapter markerClassificationAdapter,
        IProductionLegendReasoningAdapter legendAdapter,
        IProductionPhaseReasoningAdapter phaseAdapter,
        IMarkerConnectionGraphBuilder? connectionBuilder = null,
        IMarkerSeriesGrouper? seriesGrouper = null)
    {
        this.panelStore = panelStore ?? throw new ArgumentNullException(nameof(panelStore));
        this.rasterDecoder = rasterDecoder ?? throw new ArgumentNullException(nameof(rasterDecoder));
        this.axisAdapter = axisAdapter ?? throw new ArgumentNullException(nameof(axisAdapter));
        this.ocrAdapter = ocrAdapter ?? throw new ArgumentNullException(nameof(ocrAdapter));
        this.maskComposer = maskComposer ?? throw new ArgumentNullException(nameof(maskComposer));
        this.markerCenterAdapter = markerCenterAdapter ??
            throw new ArgumentNullException(nameof(markerCenterAdapter));
        this.markerClassificationAdapter = markerClassificationAdapter ??
            throw new ArgumentNullException(nameof(markerClassificationAdapter));
        this.legendAdapter = legendAdapter ?? throw new ArgumentNullException(nameof(legendAdapter));
        this.phaseAdapter = phaseAdapter ?? throw new ArgumentNullException(nameof(phaseAdapter));
        this.connectionBuilder = connectionBuilder ?? new MarkerConnectionGraphBuilder();
        this.seriesGrouper = seriesGrouper ?? new MarkerSeriesGrouper();
    }

    public string AdapterId => string.Join(
        ':',
        "graphreader-production-detection-v1",
        axisAdapter.AdapterId,
        ocrAdapter.AdapterId,
        markerCenterAdapter.Model.Sha256[..12].ToLowerInvariant(),
        markerClassificationAdapter.Model.Sha256[..12].ToLowerInvariant());

    public bool IsApproved =>
        axisAdapter.IsApproved &&
        ocrAdapter.IsApproved &&
        maskComposer.IsApproved &&
        markerCenterAdapter.IsApproved &&
        markerClassificationAdapter.IsApproved &&
        legendAdapter.IsApproved &&
        phaseAdapter.IsApproved;

    public async Task<WorkflowDetectionBatch> DetectAsync(
        ProductionWorkflowDetectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsApproved)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
                "Errors.ModelNotFound",
                $"Production adapter '{AdapterId}' has an unapproved component.",
                "Install every checksum-resolved approved production component or continue in manual mode.");
        }

        return await DetectCoreAsync(request, candidateEvaluation: false, cancellationToken)
            .ConfigureAwait(false);
    }

    Task<WorkflowDetectionBatch> IProductionCandidateWorkflowDetectionAdapter.DetectForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        CancellationToken cancellationToken) =>
        DetectForCandidateEvaluationAsync(request, cancellationToken);

    internal Task<WorkflowDetectionBatch> DetectForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsApproved)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Candidate evaluation requires an explicitly unapproved automatic adapter.",
                "Use normal production execution for an approved adapter.");
        }
        if (axisAdapter is not IProductionCandidateAxisGeometryAdapter candidateAxis ||
            candidateAxis.IsApproved ||
            ocrAdapter is not IProductionCandidateOcrAdapter candidateOcr ||
            candidateOcr.IsApproved ||
            maskComposer is not IProductionCandidateDetectionMaskComposer candidateMasks ||
            candidateMasks.IsApproved ||
            markerCenterAdapter is not IProductionCandidateMarkerCenterAdapter candidateCenters ||
            candidateCenters.IsApproved ||
            !markerClassificationAdapter.IsApproved ||
            !legendAdapter.IsApproved ||
            !phaseAdapter.IsApproved)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
                "Errors.ModelNotFound",
                "Candidate composition must contain explicit unapproved axis, OCR, mask, and marker-center adapters plus approved fixed downstream reasoners.",
                "Recreate the candidate composition from its frozen model and runtime binding.");
        }

        return DetectCoreAsync(request, candidateEvaluation: true, cancellationToken);
    }

    private async Task<WorkflowDetectionBatch> DetectCoreAsync(
        ProductionWorkflowDetectionRequest request,
        bool candidateEvaluation,
        CancellationToken cancellationToken)
    {
        if (request.ImageVariant != WorkflowImageVariant.Original)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "The production chain starts from the immutable original image. Enhanced consensus is a separately approved optional path.",
                "Run the approved CPU workflow with enhancement disabled.");
        }

        var chain = new ProductionDetectionEvidenceChain(request);
        try
        {
            ProductionDecodedRaster raster = rasterDecoder.Decode(request, cancellationToken);
            ProductionAxisGeometryEvidence axis = await (candidateEvaluation
                    ? ((IProductionCandidateAxisGeometryAdapter)axisAdapter)
                        .DetectForCandidateEvaluationAsync(request, cancellationToken)
                    : axisAdapter.DetectAsync(request, cancellationToken))
                .ConfigureAwait(false);
            chain.Append(axis.Envelope);

            OcrRectangle plotBounds = ToOcrBounds(axis.Geometry.PlotPolygon);
            OcrDetectorImage detectorImage = raster.CreateOcrDetectorImage(
                axis.Geometry,
                cancellationToken);
            ProductionOcrEvidence ocr = await (candidateEvaluation
                    ? ((IProductionCandidateOcrAdapter)ocrAdapter)
                        .RecognizeForCandidateEvaluationAsync(
                            request,
                            raster,
                            plotBounds,
                            detectorImage,
                            cancellationToken)
                    : ocrAdapter.RecognizeAsync(
                        request,
                        raster,
                        plotBounds,
                        detectorImage,
                        cancellationToken))
                .ConfigureAwait(false);
            foreach (ProductionOcrModelEvidence evidence in ocr.ModelEvidence
                .OrderBy(static evidence => evidence.Task, StringComparer.Ordinal))
            {
                chain.Append(evidence.Envelope);
            }

            ProductionDetectionMaskEvidence masks = await (candidateEvaluation
                    ? ((IProductionCandidateDetectionMaskComposer)maskComposer)
                        .ComposeForCandidateEvaluationAsync(
                            request,
                            raster,
                            axis,
                            ocr,
                            cancellationToken)
                    : maskComposer.ComposeAsync(
                        request,
                        raster,
                        axis,
                        ocr,
                        cancellationToken))
                .ConfigureAwait(false);
            chain.Append(masks.ArtifactEnvelope);
            MarkerImageFrame markerFrame = masks.CreateMarkerFrame(raster);
            MarkerPolygon markerPlot = ToMarkerPolygon(axis.Geometry.PlotPolygon);
            ProductionMarkerCenterEvidence centers = await (candidateEvaluation
                    ? ((IProductionCandidateMarkerCenterAdapter)markerCenterAdapter)
                        .DetectForCandidateEvaluationAsync(
                            request,
                            markerFrame,
                            markerPlot,
                            cancellationToken)
                    : markerCenterAdapter.DetectAsync(
                        request,
                        markerFrame,
                        markerPlot,
                        enhancedImage: null,
                        enhancedTransforms: null,
                        cancellationToken))
                .ConfigureAwait(false);
            chain.Append(centers.Envelope);

            ProductionMarkerClassificationEvidence classification =
                await markerClassificationAdapter
                    .ClassifyAsync(request, markerFrame, centers.Markers, cancellationToken)
                    .ConfigureAwait(false);
            chain.Append(classification.Envelope);

            ClassifiedMarker[] canonicalMarkers = CanonicalizeMarkers(
                request,
                classification.Markers,
                markerCenterAdapter.Model.Sha256);
            ClassifiedMarker[] acceptedMarkers = canonicalMarkers
                .Where(static marker => marker.ArtifactProbability < ArtifactRejectionThreshold)
                .ToArray();
            SessionFirstCalibrationResult calibration = FitCalibration(
                axis.Geometry,
                ocr.Result,
                acceptedMarkers,
                cancellationToken);
            RequireCompleteCalibration(calibration, chain);

            MarkerGroupingState grouping = await GroupMarkersAsync(
                    request,
                    markerFrame,
                    axis.Geometry,
                    ocr.Result,
                    acceptedMarkers,
                    calibration,
                    cancellationToken)
                .ConfigureAwait(false);

            ProductionLegendReasoningEvidence legend = await legendAdapter
                .ResolveAsync(
                    request,
                    CreateLegendRequest(request, axis.Geometry, ocr.Result, grouping),
                    cancellationToken)
                .ConfigureAwait(false);
            chain.Append(legend.Envelope);

            ProductionPhaseReasoningEvidence phases = await phaseAdapter
                .ResolveAsync(
                    request,
                    CreatePhaseRequest(request, axis.Geometry, ocr.Result, grouping, legend.Payload),
                    cancellationToken)
                .ConfigureAwait(false);
            chain.Append(phases.Envelope);

            DetectionProjection projection = BuildProjection(
                request,
                calibration,
                ocr.Result,
                canonicalMarkers,
                acceptedMarkers,
                grouping,
                legend.Payload,
                phases.Payload,
                chain.Snapshot,
                classification.Envelope.Model?.Version ?? throw Failure(
                    ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                    "Errors.DetectionEvidenceRejected",
                    "Marker classification evidence omitted its model version.",
                    "Reject the classifier output and verify the production model manifest."));
            WorkflowVisionEnvelope outputEnvelope = WithWarnings(
                classification.Envelope,
                classification.Envelope.Warnings
                    .Concat(masks.Warnings)
                    .Concat(projection.Warnings)
                    .Distinct(StringComparer.Ordinal));
            var batch = new WorkflowDetectionBatch(
                outputEnvelope,
                WorkflowImageVariant.Original,
                projection.Candidates)
            {
                PendingExportEvidence = request.DeferExportEvidenceCommit
                    ? projection.ExportEvidence
                    : null,
            };
            if (!request.DeferExportEvidenceCommit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                panelStore.SetExportEvidence(request.Panel.ImportedPanel.PanelId, projection.ExportEvidence);
            }

            return batch;
        }
        catch (ProductionWorkflowStageException exception)
        {
            throw RetainEvidence(chain, exception);
        }
    }

    private async Task<MarkerGroupingState> GroupMarkersAsync(
        ProductionWorkflowDetectionRequest request,
        MarkerImageFrame frame,
        AxisGeometryResult axis,
        OcrResult ocr,
        IReadOnlyList<ClassifiedMarker> markers,
        SessionFirstCalibrationResult calibration,
        CancellationToken cancellationToken)
    {
        MarkerGroupingEvidence[] groupingEvidence = markers
            .Select(marker =>
            {
                SessionXEvidence? x = FindXEvidence(calibration.Lattice, marker.Marker.Center.X);
                return new MarkerGroupingEvidence(
                    marker,
                    x?.Ordinal,
                    x?.PrintedX ?? x?.EstimatedX,
                    PreliminaryPhaseRegion(axis, marker.Marker.Center.X));
            })
            .ToArray();
        IReadOnlyList<MarkerConnection> connections = await connectionBuilder
            .BuildAsync(new MarkerConnectionRequest(frame, groupingEvidence), cancellationToken)
            .ConfigureAwait(false);
        MarkerLegendEvidence[] legendEvidence = ocr.Regions
            .Where(static region => region.Role == OcrTextRole.LegendText)
            .SelectMany(region => markers
                .Where(marker => IsNearLegendText(marker, region))
                .Select(marker => new MarkerLegendEvidence(
                    marker.Shape,
                    marker.Fill,
                    region.Text,
                    MarkerTextEvidenceSource.Legend,
                    Math.Min(region.Confidence, marker.Confidence))))
            .ToArray();
        MarkerGroupingResult grouped = await seriesGrouper
            .GroupAsync(
                new MarkerGroupingRequest(
                    request.ProjectId.ToString("D"),
                    request.Panel.ImportedPanel.PanelId.ToString("D"),
                    groupingEvidence,
                    connections,
                    legendEvidence),
                cancellationToken)
            .ConfigureAwait(false);
        if (!grouped.Succeeded || grouped.State is null)
        {
            MarkerGroupingFailure? failure = grouped.Failure;
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                failure?.UserMessageKey ?? "Errors.DetectionEvidenceRejected",
                failure?.TechnicalMessage ?? "Marker grouping failed without a diagnostic.",
                failure?.SuggestedAction ?? "Retain classified markers and continue manual series review.");
        }

        return grouped.State;
    }

    private static DetectionProjection BuildProjection(
        ProductionWorkflowDetectionRequest request,
        SessionFirstCalibrationResult calibration,
        OcrResult ocr,
        IReadOnlyList<ClassifiedMarker> allMarkers,
        IReadOnlyList<ClassifiedMarker> acceptedMarkers,
        MarkerGroupingState grouping,
        LegendReasoningPayload legend,
        PhaseReasoningPayload phases,
        IReadOnlyList<WorkflowVisionEnvelope> provenance,
        string markerModelVersion)
    {
        Dictionary<string, MarkerSeries> seriesByMarker = grouping.Series
            .SelectMany(series => series.MarkerIds.Select(markerId => (markerId, series)))
            .ToDictionary(static item => item.markerId, static item => item.series, StringComparer.Ordinal);
        Dictionary<string, PhasePointAssignment> phaseByPoint = phases.Assignments
            .ToDictionary(static assignment => assignment.PointId, StringComparer.Ordinal);
        HashSet<string> excluded = legend.ExcludedArtifactMarkerIds.ToHashSet(StringComparer.Ordinal);
        ClassifiedMarker[] projectedMarkers = acceptedMarkers
            .Where(marker => !excluded.Contains(marker.Marker.MarkerId))
            .ToArray();
        if (projectedMarkers.Any(marker =>
                !seriesByMarker.ContainsKey(marker.Marker.MarkerId) ||
                !phaseByPoint.ContainsKey(marker.Marker.MarkerId)))
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Every accepted marker must have one exact series and phase assignment before review projection.",
                "Retain marker evidence and resolve grouping or phase assignment manually.");
        }

        LinearAxisTransform yTransform = calibration.YTransform.Transform ??
            throw new InvalidOperationException("Validated calibration lost its y transform.");
        LinearAxisTransform? xTransform = calibration.XTransform?.Transform;
        var pointRows = new List<ProjectedPoint>(projectedMarkers.Length);
        foreach (ClassifiedMarker marker in projectedMarkers)
        {
            SessionXEvidence? xEvidence = FindXEvidence(calibration.Lattice, marker.Marker.Center.X);
            if (xEvidence is null)
            {
                throw Failure(
                    ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                    "Errors.DetectionEvidenceRejected",
                    $"Marker '{marker.Marker.MarkerId}' has no session-lattice assignment.",
                    "Retain the marker and complete x-axis calibration manually.");
            }

            MarkerSeries series = seriesByMarker[marker.Marker.MarkerId];
            PhasePointAssignment phase = phaseByPoint[marker.Marker.MarkerId];
            Guid markerId = Guid.Parse(marker.Marker.MarkerId);
            Guid pointId = ProductionWorkflowPanelStore.CreateStableId(
                request.Panel.ImportedPanel.PanelId.ToString("D"),
                "point",
                marker.Marker.MarkerId);
            double? graphX = xEvidence.PrintedX ?? xEvidence.EstimatedX;
            graphX ??= xTransform?.PixelToGraph(marker.Marker.Center.X);
            PointXSource xSource = xEvidence.PrintedX.HasValue
                ? PointXSource.Printed
                : xEvidence.EstimatedX.HasValue
                    ? PointXSource.Estimated
                    : PointXSource.ObservationOrder;
            pointRows.Add(new ProjectedPoint(
                marker,
                pointId,
                markerId,
                Guid.Parse(series.SeriesId),
                Guid.Parse(phase.PhaseId),
                xEvidence.Ordinal,
                xEvidence.PrintedX,
                xEvidence.EstimatedX,
                xSource,
                xEvidence.Confidence,
                graphX,
                yTransform.PixelToGraph(marker.Marker.Center.Y),
                series,
                markerModelVersion));
        }

        CalibrationRecord domainCalibration = ToDomainCalibration(request, calibration);
        PhaseRecord[] domainPhases = phases.Phases.Select(ToDomainPhase).ToArray();
        Dictionary<string, LegendSeriesResolution> legendSeries = legend.Series
            .ToDictionary(static series => series.SeriesId, StringComparer.Ordinal);
        SeriesRecord[] domainSeries = grouping.Series.Select(series =>
        {
            _ = legendSeries.TryGetValue(series.SeriesId, out LegendSeriesResolution? resolved);
            DomainSemanticRole role = ToDomainRole(series.SemanticRole, resolved?.Semantic.Hint);
            return new SeriesRecord(
                SeriesId.FromGuid(Guid.Parse(series.SeriesId)),
                series.Symbol,
                ToDomainShape(series.Shape),
                ToDomainFill(series.Fill),
                resolved?.Name ?? series.DisplayName,
                role,
                resolved?.Source == LegendEvidenceSource.DetectedLegend ? resolved.Name : series.LegendText,
                pointRows.Where(point => point.SeriesId == Guid.Parse(series.SeriesId))
                    .Select(point => PointId.FromGuid(point.PointId))
                    .ToArray(),
                Math.Min(series.Confidence, resolved?.Confidence ?? 1),
                ParseSeriesId(series.SharedBaselineSeriesId),
                series.ApplicableProbeSeriesIds.Select(value => SeriesId.FromGuid(Guid.Parse(value))).ToArray(),
                UserConfirmedName: false);
        }).ToArray();
        PointRecord[] domainPoints = pointRows.Select(point => new PointRecord(
            PointId.FromGuid(point.PointId),
            MarkerId.FromGuid(point.MarkerId),
            SeriesId.FromGuid(point.SeriesId),
            PhaseId.FromGuid(point.PhaseId),
            new DomainPixelPoint(point.Marker.Marker.Center.X, point.Marker.Marker.Center.Y),
            point.GraphX,
            point.GraphY,
            point.ObservationIndex,
            point.PrintedX,
            point.EstimatedX,
            point.XSource,
            point.XConfidence,
            calibration.YTransform.Confidence,
            Math.Min(point.Marker.Marker.CenterConfidence, point.Marker.Confidence),
            "markers",
            point.MarkerModelVersion,
            DomainReviewStatus.Unreviewed,
            ModificationHistory: [])).ToArray();
        OcrEvidence[] domainOcr = ocr.Regions.Select(region => new OcrEvidence(
            OcrRegionId.FromGuid(StableOcrId(request, region.RegionId)),
            region.Polygon.Points.Select(point => new DomainPixelPoint(point.X, point.Y)).ToArray(),
            region.Text,
            region.Alternatives.Select(alternative => new OcrAlternative(
                alternative.Text,
                alternative.Confidence)).ToArray(),
            ToDomainOcrRole(region.Role),
            region.Confidence,
            region.SourceImage == OcrSourceImage.Original
                ? DomainSourceImageKind.Original
                : DomainSourceImageKind.Enhanced,
            ToDomainReviewStatus(region.ReviewStatus))).ToArray();
        MarkerRecord[] domainMarkers = allMarkers.Select(marker =>
        {
            Guid markerId = Guid.Parse(marker.Marker.MarkerId);
            MarkerSeries? candidateSeries = seriesByMarker.GetValueOrDefault(marker.Marker.MarkerId);
            bool rejected = marker.ArtifactProbability >= ArtifactRejectionThreshold ||
                excluded.Contains(marker.Marker.MarkerId);
            return new MarkerRecord(
                MarkerId.FromGuid(markerId),
                new DomainPixelPoint(marker.Marker.Center.X, marker.Marker.Center.Y),
                marker.Marker.Radius,
                ToDomainShape(marker.Shape),
                ToDomainFill(marker.Fill),
                marker.Symbol,
                marker.ArtifactProbability,
                marker.Marker.CenterConfidence,
                marker.ShapeConfidence,
                marker.FillConfidence,
                marker.Embedding.Select(static value => (double)value).ToArray(),
                candidateSeries is null ? null : SeriesId.FromGuid(Guid.Parse(candidateSeries.SeriesId)),
                ToDomainSource(marker.Marker.SourceImage),
                rejected ? DomainReviewStatus.Rejected : DomainReviewStatus.Unreviewed);
        }).ToArray();
        TransformRecord[] transforms = request.Transforms.Select(transform => ToDomainTransform(
            request,
            transform)).ToArray();
        string? participant = legend.Participants
            .OrderByDescending(static item => item.Confidence)
            .ThenBy(static item => item.Name, StringComparer.Ordinal)
            .Select(static item => item.Name)
            .FirstOrDefault();
        var projection = new ProductionPanelProjectionEvidence(
            domainCalibration,
            domainPhases,
            domainSeries,
            domainPoints,
            transforms,
            domainOcr,
            domainMarkers,
            participant);
        var exportCalibration = new ExportCalibration(
            ExportCalibrationStatus.Valid,
            hasYCalibration: true,
            hasPrintedSessionCalibration: calibration.XTransform?.IsValid == true,
            hasAbsoluteSessionOrigin: calibration.Lattice.HasAbsoluteSessionOrigin,
            firstObservedSession: calibration.Lattice.Assignments
                .OrderBy(static item => item.Ordinal)
                .Select(static item => item.PrintedX ?? item.EstimatedX)
                .FirstOrDefault(),
            calibration.Confidence,
            calibration.Reasons);
        var export = new ProductionPanelExportEvidence(
            exportCalibration,
            domainPhases.Select(ToExportPhase),
            domainSeries.Select(ToExportSeries),
            domainSeries.Where(static series => series.SemanticRole == DomainSemanticRole.Intervention)
                .Select(series => new ExportSeriesRelation(
                    series.SeriesId.Value,
                    series.SharedBaselineSeriesId?.Value,
                    series.ApplicableProbeSeriesIds.Select(static id => id.Value))),
            domainPoints.Select(point => new ProductionPointExportEvidence(
                point.PointId.Value,
                point.MarkerId?.Value,
                point.ObservationIndex,
                point.PrintedXValue,
                point.EstimatedXValue,
                ToExportXSource(point.XSource),
                point.XConfidence,
                point.YConfidence)),
            provenance,
            participant,
            calibration.XTransform?.IsValid == true
                ? ExportMode.PrintedSession
                : ExportMode.ObservationOrder,
            ExportAuditMode.ExtendedCsvAndJson,
            ExportSessionOriginPolicy.Default,
            projection);
        WorkflowDetectionCandidate[] candidates = pointRows.Select(point =>
        {
            PhaseRecord phase = domainPhases.Single(item => item.PhaseId.Value == point.PhaseId);
            return new WorkflowDetectionCandidate(
                point.PointId.ToString("D"),
                $"markers:{point.Marker.Marker.MarkerId}",
                point.Marker.Marker.Center.X,
                point.Marker.Marker.Center.Y,
                Math.Min(point.Marker.Marker.CenterConfidence, point.Marker.Confidence),
                WorkflowImageVariant.Original,
                point.Marker.Symbol,
                point.Marker.Shape.ToString(),
                point.Marker.Fill.ToString(),
                point.SeriesId.ToString("D"),
                phase.PhaseId.Value.ToString("D"),
                point.GraphX,
                point.GraphY,
                "markers",
                point.MarkerModelVersion);
        }).ToArray();
        string[] warnings = provenance.SelectMany(static envelope => envelope.Warnings)
            .Concat(calibration.Reasons)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new DetectionProjection(export, candidates, warnings);
    }

    private static SessionFirstCalibrationResult FitCalibration(
        AxisGeometryResult axis,
        OcrResult ocr,
        IReadOnlyList<ClassifiedMarker> markers,
        CancellationToken cancellationToken)
    {
        NumericTickEvidence[] yTicks = ParseTicks(ocr, OcrTextRole.YTick)
            .Select(item => new NumericTickEvidence(
                item.Region.RegionId,
                item.Region.Polygon.Bounds.Center.Y,
                item.Value,
                item.Confidence))
            .ToArray();
        PrintedXTickEvidence[] xTicks = ParseTicks(ocr, OcrTextRole.XTick)
            .Select(item => new PrintedXTickEvidence(
                item.Region.RegionId,
                item.Region.Polygon.Bounds.Center.X,
                item.Value,
                item.Confidence))
            .ToArray();
        UnlabeledXTickEvidence[] unlabeled = axis.Ticks
            .Where(static tick => tick.Axis == TickAxis.XAxis)
            .Select(tick => new UnlabeledXTickEvidence(
                tick.TickId,
                tick.Center.X,
                tick.Confidence))
            .ToArray();
        MarkerColumnEvidence[] markerColumns = markers
            .Select(marker => new MarkerColumnEvidence(
                marker.Marker.Center.X,
                marker.Marker.CenterConfidence))
            .ToArray();
        double? yMaximum = yTicks.Select(static item => item.Value)
            .Where(static value => value > 0)
            .Cast<double?>()
            .DefaultIfEmpty()
            .Max();
        double? xMaximum = xTicks.Select(static item => item.PrintedValue)
            .Cast<double?>()
            .DefaultIfEmpty()
            .Max();
        return RobustCalibration.FitSessionFirst(
            new SessionFirstCalibrationRequest
            {
                YTicks = yTicks,
                PrintedXTicks = xTicks,
                Lattice = new SessionLatticeRequest
                {
                    PrintedTicks = xTicks,
                    UnlabeledTicks = unlabeled,
                    MarkerColumns = markerColumns,
                    RequireFirstObservedSessionOne = true,
                },
                YMaximum = yMaximum,
                XMaximum = xMaximum,
            },
            cancellationToken);
    }

    private static IEnumerable<ParsedTick> ParseTicks(OcrResult ocr, OcrTextRole role)
    {
        foreach (OcrRegion region in ocr.Regions.Where(region => region.Role == role))
        {
            NumericParseResult parsed = GraphNumericParser.Parse(region.Text);
            if (parsed.IsSuccess && parsed.Value is { } value)
            {
                yield return new ParsedTick(
                    region,
                    value,
                    Math.Min(region.Confidence, parsed.Confidence));
            }
        }
    }

    private static void RequireCompleteCalibration(
        SessionFirstCalibrationResult calibration,
        ProductionDetectionEvidenceChain chain)
    {
        bool complete = calibration.Validity == CalibrationValidity.Valid &&
            calibration.YTransform.IsValid &&
            calibration.XTransform?.IsValid == true &&
            calibration.Lattice.HasAbsoluteSessionOrigin &&
            calibration.Anchors.Count == 3 &&
            calibration.Anchors.All(static anchor => anchor.GraphX.HasValue);
        if (!complete)
        {
            throw chain.Reject(new ProductionWorkflowFailure(
                ProductionWorkflowFailureCodes.RecalibrationRequired,
                "Errors.CalibrationInvalid",
                $"Automatic calibration is incomplete: {string.Join(" | ", calibration.Reasons)}",
                Recoverable: true,
                "Retain detected evidence and complete the three required calibration anchors manually."));
        }
    }

    private static LegendReasoningRequest CreateLegendRequest(
        ProductionWorkflowDetectionRequest request,
        AxisGeometryResult axis,
        OcrResult ocr,
        MarkerGroupingState grouping)
    {
        LegendRectangle plot = ToLegendBounds(axis.PlotPolygon);
        LegendTextRegion[] text = ocr.Regions.Select(region => new LegendTextRegion(
            region.RegionId,
            ToLegendBounds(region.Polygon.Bounds),
            region.Text,
            region.Role,
            region.Confidence,
            region.ReviewStatus)).ToArray();
        Dictionary<string, ClassifiedMarker> markers = grouping.Markers.ToDictionary(
            static item => item.Marker.Marker.MarkerId,
            static item => item.Marker,
            StringComparer.Ordinal);
        LegendGlyphCandidate[] glyphs = markers.Values
            .Where(marker => ocr.Regions.Any(region => IsNearLegendText(marker, region)))
            .Select(marker => new LegendGlyphCandidate(
                marker.Marker.MarkerId,
                new LegendRectangle(
                    marker.Marker.Center.X - marker.Marker.Radius,
                    marker.Marker.Center.Y - marker.Marker.Radius,
                    marker.Marker.Radius * 2,
                    marker.Marker.Radius * 2),
                marker.Shape,
                marker.Fill,
                marker.Embedding,
                marker.Confidence))
            .ToArray();
        LegendSeriesCandidate[] series = grouping.Series.Select(item => new LegendSeriesCandidate(
            item.SeriesId,
            item.Shape,
            item.Fill,
            item.Symbol,
            MarkerSymbolMap.Describe(item.Shape, item.Fill).AccessibleName,
            item.MarkerIds,
            MeanEmbedding(item.MarkerIds.Select(markerId => markers[markerId].Embedding)),
            item.DisplayName)).ToArray();
        LegendPlotMarker[] plotMarkers = grouping.Series.SelectMany(seriesItem =>
            seriesItem.MarkerIds.Select(markerId =>
            {
                ClassifiedMarker marker = markers[markerId];
                return new LegendPlotMarker(
                    markerId,
                    seriesItem.SeriesId,
                    new LegendPoint(marker.Marker.Center.X, marker.Marker.Center.Y),
                    marker.Shape,
                    marker.Fill);
            })).ToArray();
        return new LegendReasoningRequest(
            request.ProjectId.ToString("D"),
            request.Panel.ImportedPanel.PanelId.ToString("D"),
            request.Image.Sha256,
            new LegendRectangle(0, 0, request.Image.Width, request.Image.Height),
            plot,
            text,
            glyphs,
            series,
            plotMarkers);
    }

    private static PhaseReasoningRequest CreatePhaseRequest(
        ProductionWorkflowDetectionRequest request,
        AxisGeometryResult axis,
        OcrResult ocr,
        MarkerGroupingState grouping,
        LegendReasoningPayload legend)
    {
        PhaseRectangle bounds = ToPhaseBounds(axis.PlotPolygon);
        PhaseDividerSegment[] segments = axis.PhaseDividers
            .Where(static divider => divider.Style != DividerStyle.Unknown)
            .Select(divider => new PhaseDividerSegment(
                StableGuid(request, "phase-segment", divider.DividerId),
                request.Panel.ImportedPanel.PanelId.ToString("D"),
                new PhasePoint(divider.Line.Start.X, divider.Line.Start.Y),
                new PhasePoint(divider.Line.End.X, divider.Line.End.Y),
                Thickness: 1,
                divider.Style switch
                {
                    DividerStyle.Dashed => PhaseDividerStyle.Dashed,
                    DividerStyle.Dotted => PhaseDividerStyle.Dotted,
                    _ => PhaseDividerStyle.Solid,
                },
                divider.Confidence)).ToArray();
        PhaseHeadingEvidence[] headings = ocr.Regions
            .Where(static region => region.Role == OcrTextRole.PhaseHeading)
            .Select(region => new PhaseHeadingEvidence(
                StableGuid(request, "phase-heading", region.RegionId),
                request.Panel.ImportedPanel.PanelId.ToString("D"),
                ToPhaseBounds(region.Polygon.Bounds),
                region.Text,
                region.Confidence,
                region.ReviewStatus == OcrReviewStatus.Rejected)).ToArray();
        Dictionary<string, LegendSeriesResolution> legendSeries = legend.Series
            .ToDictionary(static item => item.SeriesId, StringComparer.Ordinal);
        PhasePointEvidence[] points = grouping.Series.SelectMany(series =>
            series.MarkerIds.Select(markerId =>
            {
                ClassifiedMarker marker = grouping.Markers.Single(item => string.Equals(
                    item.Marker.Marker.MarkerId,
                    markerId,
                    StringComparison.Ordinal)).Marker;
                return new PhasePointEvidence(
                    markerId,
                    series.SeriesId,
                    request.Panel.ImportedPanel.PanelId.ToString("D"),
                    new PhasePoint(marker.Marker.Center.X, marker.Marker.Center.Y));
            })).ToArray();
        PhaseSeriesEvidence[] seriesEvidence = grouping.Series.Select(series =>
        {
            _ = legendSeries.TryGetValue(series.SeriesId, out LegendSeriesResolution? resolved);
            return new PhaseSeriesEvidence(
                series.SeriesId,
                ToPhaseRole(series.SemanticRole, resolved?.Semantic.Hint),
                series.MarkerIds,
                series.ApplicableProbeSeriesIds);
        }).ToArray();
        return new PhaseReasoningRequest(
            request.ProjectId.ToString("D"),
            request.Panel.ImportedPanel.PanelId.ToString("D"),
            request.Image.Sha256,
            bounds,
            segments,
            headings,
            points,
            seriesEvidence);
    }

    private static ClassifiedMarker[] CanonicalizeMarkers(
        ProductionWorkflowDetectionRequest request,
        IEnumerable<ClassifiedMarker> markers,
        string centerModelSha256) => markers.Select(marker =>
    {
        MarkerCenter source = marker.Marker;
        Guid stableId = ProductionWorkflowPanelStore.CreateStableId(
            request.Panel.ImportedPanel.PanelId.ToString("D"),
            request.Image.Sha256,
            centerModelSha256,
            source.Center.X.ToString("R", CultureInfo.InvariantCulture),
            source.Center.Y.ToString("R", CultureInfo.InvariantCulture),
            source.Radius.ToString("R", CultureInfo.InvariantCulture));
        var center = source with { MarkerId = stableId.ToString("D") };
        return new ClassifiedMarker(
            center,
            marker.Shape,
            marker.Fill,
            marker.Symbol,
            marker.AccessibleName,
            marker.ArtifactProbability,
            marker.ShapeConfidence,
            marker.FillConfidence,
            marker.Embedding);
    }).ToArray();

    private static CalibrationRecord ToDomainCalibration(
        ProductionWorkflowDetectionRequest request,
        SessionFirstCalibrationResult calibration)
    {
        DomainCalibrationAnchor[] anchors = calibration.Anchors.Select(anchor =>
            new DomainCalibrationAnchor(
                anchor.Kind switch
                {
                    GraphReader.Axis.CalibrationAnchorKind.Session1Y0 =>
                        DomainCalibrationAnchorKind.Session1Y0,
                    GraphReader.Axis.CalibrationAnchorKind.Session1YMaximum =>
                        DomainCalibrationAnchorKind.Session1Ymax,
                    GraphReader.Axis.CalibrationAnchorKind.SessionMaximumY0 =>
                        DomainCalibrationAnchorKind.SessionmaxY0,
                    _ => throw new ArgumentOutOfRangeException(nameof(anchor)),
                },
                new DomainPixelPoint(anchor.Screen.X, anchor.Screen.Y),
                new GraphReader.Domain.GraphPoint(anchor.GraphX!.Value, anchor.GraphY),
                anchor.Confidence,
                EvidenceRegionId: null)).ToArray();
        return new CalibrationRecord(
            CalibrationId.FromGuid(ProductionWorkflowPanelStore.CreateStableId(
                request.Panel.ImportedPanel.PanelId.ToString("D"),
                "calibration",
                request.Image.Sha256)),
            DomainCalibrationStatus.Valid,
            anchors,
            new SessionLatticeRecord(
                calibration.Lattice.Session1PixelX!.Value,
                calibration.Lattice.PitchPixels!.Value,
                calibration.Lattice.Assignments.Select(static item => item.PrintedX)
                    .Where(static value => value.HasValue).Min(),
                calibration.Lattice.Assignments.Select(static item => item.PrintedX)
                    .Where(static value => value.HasValue).Max(),
                calibration.Lattice.Confidence,
                calibration.Lattice.Source.ToString()),
            UserConfirmed: false,
            calibration.Confidence,
            calibration.Reasons);
    }

    private static TransformRecord ToDomainTransform(
        ProductionWorkflowDetectionRequest request,
        WorkflowTransformProvenance transform) => new(
            TransformId.FromGuid(ProductionWorkflowPanelStore.CreateStableId(
                request.Panel.ImportedPanel.PanelId.ToString("D"),
                "transform",
                transform.TransformId)),
            TransformKind.Affine,
            ToDomainCoordinateSpace(transform.InputCoordinateSpace),
            ToDomainCoordinateSpace(transform.OutputCoordinateSpace),
            transform.InputToOutputMatrix,
            transform.OutputToInputMatrix,
            JsonSerializer.SerializeToElement(new { transform_id = transform.TransformId }),
            transform.Lossy);

    private static CoordinateSpace ToDomainCoordinateSpace(string value) => value switch
    {
        "original_pixels" => CoordinateSpace.OriginalPixels,
        "enhanced_pixels" => CoordinateSpace.EnhancedPixels,
        "page_pixels" => CoordinateSpace.PagePixels,
        "panel_pixels" => CoordinateSpace.PanelPixels,
        "deskewed_pixels" => CoordinateSpace.DeskewedPixels,
        "model_tensor" => CoordinateSpace.ModelTensor,
        "graph_units" => CoordinateSpace.GraphUnits,
        _ => throw Failure(
            ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
            "Errors.DetectionEvidenceRejected",
            $"Unknown transform coordinate space '{value}'.",
            "Reject the transform and rerun from immutable source evidence."),
    };

    private static PhaseRecord ToDomainPhase(PhaseRegion phase) => new(
        PhaseId.FromGuid(Guid.Parse(phase.PhaseId)),
        phase.Order,
        phase.Code,
        phase.NormalizedType switch
        {
            PhaseNormalizedType.Baseline => DomainPhaseNormalizedType.Baseline,
            PhaseNormalizedType.Intervention => DomainPhaseNormalizedType.Intervention,
            PhaseNormalizedType.Maintenance => DomainPhaseNormalizedType.Maintenance,
            PhaseNormalizedType.Generalization => DomainPhaseNormalizedType.Generalization,
            _ => DomainPhaseNormalizedType.Unknown,
        },
        phase.LabelText,
        phase.OriginalXMinimum,
        phase.OriginalXMaximum,
        ParsePhaseId(phase.BoundaryLeftId),
        ParsePhaseId(phase.BoundaryRightId),
        phase.Confidence,
        phase.Source switch
        {
            PhaseEvidenceSource.Ocr => DomainPhaseSource.Ocr,
            PhaseEvidenceSource.Manual => DomainPhaseSource.Manual,
            PhaseEvidenceSource.CrossPanel => DomainPhaseSource.CrossPanel,
            _ => DomainPhaseSource.ProfilePrior,
        },
        UserConfirmed: phase.Source == PhaseEvidenceSource.Manual);

    private static ExportPhase ToExportPhase(PhaseRecord phase) => new(
        phase.PhaseId.Value,
        phase.Order,
        phase.Code,
        phase.NormalizedType switch
        {
            DomainPhaseNormalizedType.Baseline => ExportPhaseType.Baseline,
            DomainPhaseNormalizedType.Intervention => ExportPhaseType.Intervention,
            DomainPhaseNormalizedType.Maintenance => ExportPhaseType.Maintenance,
            DomainPhaseNormalizedType.Generalization => ExportPhaseType.Generalization,
            _ => ExportPhaseType.Unknown,
        },
        phase.LabelText,
        phase.ScreenXMin,
        phase.ScreenXMax,
        phase.Confidence);

    private static ExportSeries ToExportSeries(SeriesRecord series) => new(
        series.SeriesId.Value,
        series.Symbol,
        series.DisplayName,
        series.SemanticRole switch
        {
            DomainSemanticRole.Baseline => ExportSeriesRole.Baseline,
            DomainSemanticRole.Intervention => ExportSeriesRole.Intervention,
            DomainSemanticRole.Maintenance => ExportSeriesRole.Maintenance,
            DomainSemanticRole.Generalization => ExportSeriesRole.Generalization,
            _ => ExportSeriesRole.Unknown,
        },
        series.PointIds.Select(static id => id.Value),
        series.Confidence,
        series.LegendText);

    private static ExportXValueSource ToExportXSource(PointXSource source) => source switch
    {
        PointXSource.Printed => ExportXValueSource.Printed,
        PointXSource.Estimated => ExportXValueSource.Estimated,
        PointXSource.ObservationOrder => ExportXValueSource.ObservationOrder,
        _ => ExportXValueSource.Unknown,
    };

    private static DomainSemanticRole ToDomainRole(
        MarkerSeriesRole role,
        LegendSemanticHint? hint) => hint switch
    {
        LegendSemanticHint.Generalization => DomainSemanticRole.Generalization,
        LegendSemanticHint.Maintenance => DomainSemanticRole.Maintenance,
        _ => role switch
        {
            MarkerSeriesRole.Baseline => DomainSemanticRole.Baseline,
            MarkerSeriesRole.Intervention => DomainSemanticRole.Intervention,
            MarkerSeriesRole.Maintenance => DomainSemanticRole.Maintenance,
            MarkerSeriesRole.Generalization => DomainSemanticRole.Generalization,
            _ => DomainSemanticRole.Unknown,
        },
    };

    private static PhaseNormalizedType ToPhaseRole(
        MarkerSeriesRole role,
        LegendSemanticHint? hint) => hint switch
    {
        LegendSemanticHint.Generalization => PhaseNormalizedType.Generalization,
        LegendSemanticHint.Maintenance => PhaseNormalizedType.Maintenance,
        _ => role switch
        {
            MarkerSeriesRole.Baseline => PhaseNormalizedType.Baseline,
            MarkerSeriesRole.Intervention => PhaseNormalizedType.Intervention,
            MarkerSeriesRole.Maintenance => PhaseNormalizedType.Maintenance,
            MarkerSeriesRole.Generalization => PhaseNormalizedType.Generalization,
            _ => PhaseNormalizedType.Unknown,
        },
    };

    private static DomainMarkerShape ToDomainShape(MarkerShape shape) => shape switch
    {
        MarkerShape.Circle => DomainMarkerShape.Circle,
        MarkerShape.Square => DomainMarkerShape.Square,
        MarkerShape.TriangleUp => DomainMarkerShape.TriangleUp,
        MarkerShape.TriangleDown => DomainMarkerShape.TriangleDown,
        MarkerShape.Diamond => DomainMarkerShape.Diamond,
        MarkerShape.Star => DomainMarkerShape.Star,
        MarkerShape.Asterisk => DomainMarkerShape.Asterisk,
        MarkerShape.Cross => DomainMarkerShape.Cross,
        _ => DomainMarkerShape.Other,
    };

    private static DomainMarkerFill ToDomainFill(MarkerFill fill) => fill switch
    {
        MarkerFill.Filled => DomainMarkerFill.Filled,
        MarkerFill.Open => DomainMarkerFill.Open,
        _ => DomainMarkerFill.Unknown,
    };

    private static OcrRole ToDomainOcrRole(OcrTextRole role) => role switch
    {
        OcrTextRole.YTick => OcrRole.YTick,
        OcrTextRole.XTick => OcrRole.XTick,
        OcrTextRole.AxisTitle => OcrRole.AxisTitle,
        OcrTextRole.PhaseHeading => OcrRole.PhaseHeading,
        OcrTextRole.LegendText => OcrRole.LegendText,
        OcrTextRole.Participant => OcrRole.Participant,
        OcrTextRole.Annotation => OcrRole.Annotation,
        _ => OcrRole.Other,
    };

    private static DomainReviewStatus ToDomainReviewStatus(OcrReviewStatus status) => status switch
    {
        OcrReviewStatus.Accepted => DomainReviewStatus.Accepted,
        OcrReviewStatus.Corrected => DomainReviewStatus.Corrected,
        OcrReviewStatus.Rejected => DomainReviewStatus.Rejected,
        _ => DomainReviewStatus.Unreviewed,
    };

    private static DomainSourceImageKind ToDomainSource(MarkerSourceImage source) => source switch
    {
        MarkerSourceImage.Enhanced => DomainSourceImageKind.Enhanced,
        MarkerSourceImage.Consensus => DomainSourceImageKind.Consensus,
        _ => DomainSourceImageKind.Original,
    };

    private static SeriesId? ParseSeriesId(string? value) =>
        Guid.TryParse(value, out Guid parsed) ? SeriesId.FromGuid(parsed) : null;

    private static PhaseId? ParsePhaseId(string? value) =>
        Guid.TryParse(value, out Guid parsed) ? PhaseId.FromGuid(parsed) : null;

    private static Guid StableOcrId(
        ProductionWorkflowDetectionRequest request,
        string regionId) => Guid.TryParse(regionId, out Guid parsed)
            ? parsed
            : ProductionWorkflowPanelStore.CreateStableId(
                request.Panel.ImportedPanel.PanelId.ToString("D"),
                "ocr",
                regionId);

    private static string StableGuid(
        ProductionWorkflowDetectionRequest request,
        string kind,
        string value) => ProductionWorkflowPanelStore.CreateStableId(
            request.Panel.ImportedPanel.PanelId.ToString("D"),
            kind,
            value).ToString("D");

    private static SessionXEvidence? FindXEvidence(SessionLatticeResult lattice, double pixelX) =>
        lattice.Assignments
            .OrderBy(item => Math.Abs(item.PixelX - pixelX))
            .ThenBy(static item => item.Ordinal)
            .FirstOrDefault();

    private static string PreliminaryPhaseRegion(AxisGeometryResult axis, double pixelX)
    {
        int order = axis.PhaseDividers.Count(divider => divider.Line.Midpoint.X < pixelX) + 1;
        return $"region-{order.ToString(CultureInfo.InvariantCulture)}";
    }

    private static bool IsNearLegendText(ClassifiedMarker marker, OcrRegion region)
    {
        if (region.Role != OcrTextRole.LegendText)
        {
            return false;
        }

        OcrRectangle bounds = region.Polygon.Bounds;
        double horizontalGap = bounds.Left - marker.Marker.Center.X;
        double verticalGap = Math.Abs(bounds.Center.Y - marker.Marker.Center.Y);
        return horizontalGap is >= 0 and <= 90 && verticalGap <= 14;
    }

    private static float[] MeanEmbedding(IEnumerable<IReadOnlyList<float>> values)
    {
        IReadOnlyList<float>[] vectors = values.ToArray();
        if (vectors.Length == 0)
        {
            return [];
        }

        int length = vectors[0].Count;
        if (vectors.Any(vector => vector.Count != length))
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Marker embeddings in one series have different lengths.",
                "Reject the grouping result and verify classifier output contracts.");
        }

        var mean = new float[length];
        foreach (IReadOnlyList<float> vector in vectors)
        {
            for (int index = 0; index < length; index++)
            {
                mean[index] += vector[index] / vectors.Length;
            }
        }

        return mean;
    }

    private static OcrRectangle ToOcrBounds(PlotPolygon polygon)
    {
        double left = polygon.Points.Min(static point => point.X);
        double top = polygon.Points.Min(static point => point.Y);
        double right = polygon.Points.Max(static point => point.X);
        double bottom = polygon.Points.Max(static point => point.Y);
        return new OcrRectangle(left, top, right - left, bottom - top);
    }

    private static MarkerPolygon ToMarkerPolygon(PlotPolygon polygon) => new(
        polygon.Points.Select(static point => new MarkerPoint(point.X, point.Y)).ToArray());

    private static LegendRectangle ToLegendBounds(PlotPolygon polygon)
    {
        OcrRectangle bounds = ToOcrBounds(polygon);
        return ToLegendBounds(bounds);
    }

    private static LegendRectangle ToLegendBounds(OcrRectangle bounds) =>
        new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    private static PhaseRectangle ToPhaseBounds(PlotPolygon polygon)
    {
        OcrRectangle bounds = ToOcrBounds(polygon);
        return ToPhaseBounds(bounds);
    }

    private static PhaseRectangle ToPhaseBounds(OcrRectangle bounds) =>
        new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    private static WorkflowVisionEnvelope WithWarnings(
        WorkflowVisionEnvelope source,
        IEnumerable<string> warnings) => new(
            source.ContractVersion,
            source.RunId,
            source.ProjectId,
            source.PanelId,
            source.Stage,
            source.StageVersion,
            source.InputSha256,
            source.Model,
            source.Timing,
            source.Confidence,
            warnings,
            source.Transforms,
            source.CoordinateSpace);

    private static ProductionWorkflowStageException RetainEvidence(
        ProductionDetectionEvidenceChain chain,
        ProductionWorkflowStageException exception)
    {
        WorkflowVisionEnvelope[] evidence = chain.Snapshot
            .Concat(exception.CompletedEvidence)
            .GroupBy(static item => string.Join(
                '|',
                item.Stage,
                item.StageVersion,
                item.Model?.ModelId,
                item.Model?.Version,
                item.Model?.Sha256), StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
        return new ProductionWorkflowStageException(exception.Failure, evidence);
    }

    private static ProductionWorkflowStageException Failure(
        string code,
        string userMessageKey,
        string technicalMessage,
        string suggestedAction) => new(new ProductionWorkflowFailure(
            code,
            userMessageKey,
            technicalMessage,
            Recoverable: true,
            suggestedAction));

    private sealed record ParsedTick(OcrRegion Region, double Value, double Confidence);

    private sealed record ProjectedPoint(
        ClassifiedMarker Marker,
        Guid PointId,
        Guid MarkerId,
        Guid SeriesId,
        Guid PhaseId,
        int ObservationIndex,
        double? PrintedX,
        double? EstimatedX,
        PointXSource XSource,
        double XConfidence,
        double? GraphX,
        double GraphY,
        MarkerSeries Series,
        string MarkerModelVersion);

    private sealed record DetectionProjection(
        ProductionPanelExportEvidence ExportEvidence,
        IReadOnlyList<WorkflowDetectionCandidate> Candidates,
        IReadOnlyList<string> Warnings);
}
