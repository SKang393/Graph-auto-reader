// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json.Serialization;
using GraphReader.App.Integration.Workflow;
using GraphReader.Imaging;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>Single-use, in-memory capture. Never writes a diagnostic file.</summary>
internal sealed class ComposedOcrObservationBuffer
{
    private OcrDetectionObservation? _pending;
    private bool _invalid;

    internal void Capture(OcrDetectionObservation observation)
    {
        if (_invalid || _pending is not null || observation.SuppliedRegions)
        {
            _invalid = true;
            throw new InvalidDataException("COMPOSED_OCR_OBSERVATION_INVALID");
        }
        _pending = observation;
    }

    internal IReadOnlyList<OcrDetectedRegion> Take(string projectId, string panelId,
        string inputSha256, int width, int height)
    {
        OcrDetectionObservation? value = _pending;
        _pending = null;
        if (_invalid || value is null || value.ProjectId != projectId || value.PanelId != panelId ||
            value.InputSha256 != inputSha256 || value.Width != width || value.Height != height)
        {
            _invalid = true;
            throw new InvalidDataException("COMPOSED_OCR_OBSERVATION_MISMATCH");
        }
        return value.RawDetectorRegions;
    }
}

/// <summary>Case-level values stay in-process and are excluded from serialization.</summary>
internal sealed record ComposedOcrSourcePredictions(
    int PanelCount,
    int RecognitionFailedRegionCount,
    [property: JsonIgnore] IReadOnlyList<OriginalDbOcrAggregatePrediction> RawDetectorRegions,
    [property: JsonIgnore] IReadOnlyList<OriginalDbOcrAggregatePrediction> AssembledRegions);

internal static class ComposedOcrInMemorySourceEvaluator
{
    /// <summary>
    /// Receives image bytes only. Uses actual import and axis geometry, the same
    /// detector image and phase context as the native workflow, and one OCR call.
    /// The observation buffer captures that call's raw boundary without a rerun.
    /// </summary>
    internal static async Task<ComposedOcrSourcePredictions> EvaluateAsync(
        byte[] encodedSource, string sourceSha256, ProductionOcrAdapter ocr,
        ComposedOcrObservationBuffer observations, ProductionAxisGeometryAdapter axis,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encodedSource);
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(axis);
        cancellationToken.ThrowIfCancellationRequested();
        if (ocr.IsApproved || axis.IsApproved || ocr.ConfigurationScope != "unapproved_frozen_candidate" ||
            (!ocr.AdapterId.StartsWith("graphreader-ocr:" + ProductionOcrAdapter.TickLaneCandidateCompositionVersion + ":", StringComparison.Ordinal) &&
             !ocr.AdapterId.StartsWith("graphreader-ocr:" + ProductionOcrAdapter.SourceScaleCandidateCompositionVersion + ":", StringComparison.Ordinal)))
            throw new InvalidDataException("COMPOSED_OCR_CANDIDATE_SCOPE_INVALID");
        string stage = "import";
        try
        {
            var image = new WorkflowInMemoryImageSource(sourceSha256, encodedSource);
            Guid projectId = ProductionWorkflowPanelStore.CreateStableId("composed-ocr-project-v1", image.Sha256);
            Guid sourceId = ProductionWorkflowPanelStore.CreateStableId("composed-ocr-source-v1", image.Sha256);
            var source = new WorkflowSourceRequest(sourceId, WorkflowSourceKind.Image, "source.png")
            {
                InMemoryImageSource = image,
            };
            var store = new ProductionWorkflowPanelStore();
            var importer = new ProductionWorkflowImportStage(store, new ImageImportService());
            WorkflowImportSnapshot imported = await importer.ImportAsync(
                new WorkflowImportRequest(projectId, [source], enhancementEnabled: false), cancellationToken)
                .ConfigureAwait(false);
            if (imported.Panels.Count == 0)
                throw new InvalidDataException("COMPOSED_OCR_IMPORT_EMPTY");
            var raw = new List<OriginalDbOcrAggregatePrediction>();
            var assembled = new List<OriginalDbOcrAggregatePrediction>();
            int failures = 0;
            foreach (WorkflowImportedPanel panel in imported.Panels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProductionPanelEvidence evidence = store.Get(panel.PanelId);
                var provenance = evidence.RasterPanelSource
                    ?? throw new InvalidDataException("COMPOSED_OCR_SOURCE_MAPPING_MISSING");
                var prepared = new WorkflowPreparedPanel(panel, panel.Original, enhanced: null);
                Guid runId = ProductionWorkflowPanelStore.CreateStableId(
                    "composed-ocr-run-v1", projectId.ToString("D"), panel.PanelId.ToString("D"));
                var request = new ProductionWorkflowDetectionRequest(prepared, panel.Original,
                    WorkflowImageVariant.Original, runId, projectId, evidence.CopyOriginalBytes());
                stage = "axis";
                ProductionDecodedRaster raster = new ProductionRasterFrameDecoder().Decode(request, cancellationToken);
                ProductionAxisGeometryEvidence geometry = await axis
                    .DetectForLocalSyntheticCandidateEvaluationAsync(request, cancellationToken).ConfigureAwait(false);
                OcrRectangle plot = OriginalDbOcrInMemorySourceEvaluator.PlotBounds(geometry.Geometry.PlotPolygon.Points
                    .Select(point => new OcrPoint(point.X, point.Y)).ToArray());
                IReadOnlyList<double> dividers = ProductionAutomaticDetectionAdapter.CreateOcrPhaseDividerXs(
                    geometry.Geometry.CoordinateSpace, geometry.Geometry.PhaseDividers, plot);
                OcrDetectorImage detectorImage = raster.CreateOcrDetectorImage(geometry.Geometry, cancellationToken);
                stage = "recognition";
                ProductionOcrEvidence recognized = await ocr.RecognizeForCandidateEvaluationAsync(
                    request, raster, plot, detectorImage, cancellationToken, dividers).ConfigureAwait(false);
                stage = "coverage";
                IReadOnlyList<OcrDetectedRegion> observed = observations.Take(projectId.ToString("D"),
                    panel.PanelId.ToString("D"), request.Image.Sha256, raster.Width, raster.Height);
                stage = "mapping";
                OriginalDbOcrAggregateBox Map(OcrPolygon polygon)
                {
                    OriginalDbOcrAggregateBox box = OriginalDbOcrInMemorySourceEvaluator.MapBox(
                        polygon, provenance.PanelToSourceMatrix);
                    if (box.Right > provenance.Source.Image.Width || box.Bottom > provenance.Source.Image.Height)
                        throw new InvalidDataException("COMPOSED_OCR_SOURCE_BOUNDS_INVALID");
                    return box;
                }
                raw.AddRange(observed.Select(region => new OriginalDbOcrAggregatePrediction(Map(region.Polygon), null, null)));
                assembled.AddRange(recognized.Result.Regions.Select(region => new OriginalDbOcrAggregatePrediction(
                    Map(region.Polygon), region.Text, MapRole(region.Role))));
                failures = checked(failures + (recognized.Result.RegionFailures?.Count ?? 0));
            }
            return new(imported.Panels.Count, failures, raw.AsReadOnly(), assembled.AsReadOnly());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Do not expose source paths, labels, identities or inner exceptions.
            throw new InvalidDataException("COMPOSED_OCR_SOURCE_EVALUATION_FAILED:" + stage);
        }
    }

    private static GraphReader.Domain.OcrRole MapRole(OcrTextRole role) => role switch
    {
        OcrTextRole.YTick => GraphReader.Domain.OcrRole.YTick,
        OcrTextRole.XTick => GraphReader.Domain.OcrRole.XTick,
        OcrTextRole.AxisTitle => GraphReader.Domain.OcrRole.AxisTitle,
        OcrTextRole.PhaseHeading => GraphReader.Domain.OcrRole.PhaseHeading,
        OcrTextRole.LegendText => GraphReader.Domain.OcrRole.LegendText,
        OcrTextRole.Participant => GraphReader.Domain.OcrRole.Participant,
        OcrTextRole.Annotation => GraphReader.Domain.OcrRole.Annotation,
        OcrTextRole.Other => GraphReader.Domain.OcrRole.Other,
        _ => throw new InvalidDataException("COMPOSED_OCR_ROLE_INVALID"),
    };
}
