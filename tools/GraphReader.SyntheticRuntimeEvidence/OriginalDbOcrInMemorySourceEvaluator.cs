// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using GraphReader.App.Integration.Workflow;
using GraphReader.Imaging;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>In-process output only. Never serialize this object into sealed evidence.</summary>
internal sealed record OriginalDbOcrSourcePredictions(
    int PanelCount,
    [property: JsonIgnore] IReadOnlyList<OriginalDbOcrAggregatePrediction> Predictions);

/// <summary>
/// Runs authenticated image bytes through actual import, axis and original-image OCR.
/// The caller owns model authentication, read admission and the non-persistent runtime.
/// No truth, crop, plot boundary or prediction is accepted as input.
/// </summary>
internal static class OriginalDbOcrInMemorySourceEvaluator
{
    internal static async Task<OriginalDbOcrSourcePredictions> EvaluateAsync(
        byte[] encodedSource,
        string sourceSha256,
        ProductionOcrAdapter ocr,
        LocalOnnxTextRegionDetector rawDetector,
        ProductionAxisGeometryAdapter axis,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encodedSource);
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(rawDetector);
        ArgumentNullException.ThrowIfNull(axis);
        cancellationToken.ThrowIfCancellationRequested();
        if (ocr.IsApproved || axis.IsApproved ||
            ocr.ConfigurationScope != "unapproved_frozen_candidate" ||
            !ocr.AdapterId.Contains(ProductionOcrAdapter.OriginalDbCandidateCompositionVersion, StringComparison.Ordinal))
            throw new InvalidDataException("SEALED_OCR_CANDIDATE_SCOPE_INVALID");
        string stage = "import";
        try
        {
            var image = new WorkflowInMemoryImageSource(sourceSha256, encodedSource);
            Guid projectId = ProductionWorkflowPanelStore.CreateStableId("sealed-ocr-project-v1", image.Sha256);
            Guid sourceId = ProductionWorkflowPanelStore.CreateStableId("sealed-ocr-source-v1", image.Sha256);
            var source = new WorkflowSourceRequest(sourceId, WorkflowSourceKind.Image, "sealed-source.png")
            {
                InMemoryImageSource = image,
            };
            var store = new ProductionWorkflowPanelStore();
            var importer = new ProductionWorkflowImportStage(store, new ImageImportService());
            WorkflowImportSnapshot imported = await importer.ImportAsync(
                new WorkflowImportRequest(projectId, [source], enhancementEnabled: false), cancellationToken)
                .ConfigureAwait(false);
            if (imported.Panels.Count == 0)
                throw new InvalidDataException("SEALED_OCR_IMPORT_EMPTY");
            var predictions = new List<OriginalDbOcrAggregatePrediction>();
            foreach (WorkflowImportedPanel panel in imported.Panels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProductionPanelEvidence evidence = store.Get(panel.PanelId);
                var provenance = evidence.RasterPanelSource
                    ?? throw new InvalidDataException("SEALED_OCR_SOURCE_MAPPING_MISSING");
                var prepared = new WorkflowPreparedPanel(panel, panel.Original, enhanced: null);
                Guid runId = ProductionWorkflowPanelStore.CreateStableId(
                    "sealed-ocr-run-v1", projectId.ToString("D"), panel.PanelId.ToString("D"));
                var request = new ProductionWorkflowDetectionRequest(prepared, panel.Original,
                    WorkflowImageVariant.Original, runId, projectId, evidence.CopyOriginalBytes());
                stage = "axis";
                ProductionDecodedRaster raster = new ProductionRasterFrameDecoder().Decode(request, cancellationToken);
                ProductionAxisGeometryEvidence geometry = await axis
                    .DetectForLocalSyntheticCandidateEvaluationAsync(request, cancellationToken).ConfigureAwait(false);
                OcrRectangle plot = PlotBounds(geometry.Geometry.PlotPolygon.Points
                    .Select(point => new OcrPoint(point.X, point.Y)).ToArray());
                OcrImage original = raster.CreateOcrImage();
                string graySha = Convert.ToHexStringLower(SHA256.HashData(original.Pixels.Span));
                string bgrSha = original.BgrPixels is { } bgr
                    ? Convert.ToHexStringLower(SHA256.HashData(bgr.Pixels.Span))
                    : throw new InvalidDataException("SEALED_OCR_ORIGINAL_BGR_MISSING");
                var detectorImage = new OcrDetectorImage(original, graySha, bgrSha);
                stage = "raw_detection";
                IReadOnlyList<OcrDetectedRegion> raw = await rawDetector.DetectAsync(original, cancellationToken)
                    .ConfigureAwait(false);
                stage = "recognition";
                ProductionOcrEvidence recognized = await ocr.RecognizeForCandidateEvaluationAsync(
                    request, raster, plot, detectorImage, cancellationToken).ConfigureAwait(false);
                stage = "coverage";
                OfficialHeadCandidateEvaluation.ValidateOcrCoverage(raw, recognized.Result);
                var byId = recognized.Result.Regions.ToDictionary(row => row.RegionId, StringComparer.Ordinal);
                // Full OCR matching preserves the application's recognition order. Raw geometry
                // reports maximum matching cardinality only, which is invariant to this permutation.
                IEnumerable<OcrDetectedRegion> ordered = InRecognitionOrder(raw, recognized.Result.Regions);
                stage = "mapping";
                foreach (OcrDetectedRegion region in ordered)
                {
                    var box = MapBox(region.Polygon, provenance.PanelToSourceMatrix);
                    if (box.Right > provenance.Source.Image.Width || box.Bottom > provenance.Source.Image.Height)
                        throw new InvalidDataException("SEALED_OCR_SOURCE_BOUNDS_INVALID");
                    predictions.Add(byId.TryGetValue(region.RegionId, out OcrRegion? text)
                        ? new OriginalDbOcrAggregatePrediction(box, text.Text, MapRole(text.Role))
                        : new OriginalDbOcrAggregatePrediction(box, null, null));
                }
            }
            return new OriginalDbOcrSourcePredictions(imported.Panels.Count, predictions.AsReadOnly());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Do not retain an inner exception that may contain case-level paths or text.
            throw new InvalidDataException("SEALED_OCR_SOURCE_EVALUATION_FAILED:" + stage);
        }
    }

    internal static IEnumerable<OcrDetectedRegion> InRecognitionOrder(
        IReadOnlyList<OcrDetectedRegion> raw, IReadOnlyList<OcrRegion> recognized)
    {
        var rawById = raw.ToDictionary(row => row.RegionId, StringComparer.Ordinal);
        var recognizedIds = recognized.Select(row => row.RegionId).ToHashSet(StringComparer.Ordinal);
        return recognized.Select(row => rawById[row.RegionId])
            .Concat(raw.Where(row => !recognizedIds.Contains(row.RegionId)));
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
        _ => throw new InvalidDataException("SEALED_OCR_ROLE_INVALID"),
    };

    internal static OriginalDbOcrAggregateBox MapBox(OcrPolygon polygon, IReadOnlyList<double> matrix)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        ArgumentNullException.ThrowIfNull(matrix);
        if (!polygon.Bounds.IsValid)
            throw new InvalidDataException("SEALED_OCR_POLYGON_INVALID");
        if (matrix.Count != 9 || matrix.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException("SEALED_OCR_TRANSFORM_INVALID");
        var points = polygon.Points.Select(point =>
        {
            double divisor = matrix[6] * point.X + matrix[7] * point.Y + matrix[8];
            if (!double.IsFinite(divisor) || Math.Abs(divisor) < 1e-12)
                throw new InvalidDataException("SEALED_OCR_TRANSFORM_SINGULAR");
            double x = (matrix[0] * point.X + matrix[1] * point.Y + matrix[2]) / divisor;
            double y = (matrix[3] * point.X + matrix[4] * point.Y + matrix[5]) / divisor;
            if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0)
                throw new InvalidDataException("SEALED_OCR_SOURCE_COORDINATE_INVALID");
            return new OcrPoint(x, y);
        }).ToArray();
        return new(points.Min(point => point.X), points.Min(point => point.Y),
            points.Max(point => point.X), points.Max(point => point.Y));
    }

    internal static OcrRectangle PlotBounds(IReadOnlyList<OcrPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3 || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            throw new InvalidDataException("SEALED_OCR_PLOT_INVALID");
        double left = points.Min(point => point.X);
        double top = points.Min(point => point.Y);
        var bounds = new OcrRectangle(left, top, points.Max(point => point.X) - left,
            points.Max(point => point.Y) - top);
        if (!bounds.IsValid || left < 0 || top < 0)
            throw new InvalidDataException("SEALED_OCR_PLOT_INVALID");
        return bounds;
    }
}
