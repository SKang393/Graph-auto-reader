// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;

using GraphReader.App.Integration.Workflow;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using GraphReader.Export;
using GraphReader.Imaging;
using GraphReader.Pdf;
using GraphReader.Phases;
using GraphReader.SuperResolution;

namespace GraphReader.App.Services;

/// <summary>
/// Real-data production workspace. Automatic detection remains fail-closed
/// until composition supplies a workflow backed by approved runtime assets.
/// </summary>
public sealed class ProductionWorkspaceService : ManualPreviewWorkspaceService, IAutomaticWorkspaceService
{
    private static readonly string[] RequiredDetectionStages =
    [
        "axis",
        "ocr",
        "markers",
        "legends",
        "phases",
    ];

    private readonly Func<WorkflowReviewState?, CancellationToken, Task<WorkflowRunResult>>? _automaticWorkflow;
    private readonly WorkflowOrchestrator? _workflowOrchestrator;
    private readonly ProductionWorkflowPanelStore _panelStore;

    public ProductionWorkspaceService(
        IApplicationPaths? applicationPaths = null,
        IImageImportService? imageImportService = null,
        ProjectFileStore? projectFileStore = null,
        IExportService? exportService = null,
        IPhaseManualEditor? phaseEditor = null,
        IReadOnlyList<AutomaticStageStatus>? automaticStages = null,
        Func<CancellationToken, Task<RealEsrganBackendResolution>>? enhancementResolver = null,
        Func<WorkflowReviewState?, CancellationToken, Task<WorkflowRunResult>>? automaticWorkflow = null,
        WorkflowOrchestrator? workflowOrchestrator = null,
        ProductionWorkflowPanelStore? panelStore = null,
        IPdfImportService? pdfImportService = null)
        : base(
            applicationPaths,
            imageImportService,
            projectFileStore,
            exportService,
            phaseEditor,
            WorkflowRuntimeEnvironment.Production,
            automaticStages,
            enhancementResolver,
            pdfImportService)
    {
        _automaticWorkflow = automaticWorkflow;
        _workflowOrchestrator = workflowOrchestrator;
        _panelStore = panelStore ?? new ProductionWorkflowPanelStore();
    }

    public WorkflowRunResult? LastAutomaticRun { get; private set; }

    public override async Task<ExportResult> ExportAsync(
        string tabId,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (WorkspaceTabViewModel tab, PanelRecord panel) = PreparePanelForExport(tabId);

        if (_panelStore.TryGet(panel.PanelId.Value, out ProductionPanelEvidence? retained) &&
            retained is not null &&
            retained.Panel.PanelId == panel.PanelId.Value &&
            retained.Panel.SourceId == panel.SourceId.Value &&
            retained.ExportEvidence is { } exportEvidence)
        {
            ValidateCurrentProductionExportEvidence(CurrentProject, panel, retained, exportEvidence);
            return await ExportPreparedPanelAsync(
                tab,
                panel,
                outputDirectory,
                exportEvidence.Mode,
                exportEvidence.AuditMode,
                exportEvidence.Calibration,
                exportEvidence.SessionOriginPolicy,
                cancellationToken).ConfigureAwait(false);
        }

        if (HasProductionProjectionAudit(panel.PanelId.Value))
        {
            throw new InvalidOperationException(
                "Production export evidence is missing or stale for this reviewed panel. Rerun production detection before exporting.");
        }

        // A panel without an accepted production projection remains a manual
        // workspace. Preserve the established observation-order export behavior.
        if (tab.Calibration is null || panel.Calibration is null)
        {
            throw new InvalidOperationException("Manual three-anchor calibration is required before export.");
        }

        if (panel.Calibration.Status != CalibrationStatus.Valid || !panel.Calibration.UserConfirmed)
        {
            throw new InvalidOperationException(
                "Production manual export requires a valid, explicitly confirmed three-anchor calibration. Recalibrate before exporting.");
        }

        return await ExportPreparedPanelAsync(
            tab,
            panel,
            outputDirectory,
            ExportMode.ObservationOrder,
            ExportAuditMode.ExtendedCsvAndJson,
            new ExportCalibration(
                ExportCalibrationStatus.Valid,
                hasYCalibration: true,
                hasPrintedSessionCalibration: false,
                hasAbsoluteSessionOrigin: true,
                firstObservedSession: null,
                panel.Calibration.Confidence),
            new ExportSessionOriginPolicy(
                RequireFirstObservedSessionOne: false,
                InvalidSessionOriginBehavior.Block),
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateCurrentProductionExportEvidence(
        ProjectDocument project,
        PanelRecord panel,
        ProductionPanelEvidence retained,
        ProductionPanelExportEvidence exportEvidence)
    {
        ProductionPanelProjectionEvidence projection = exportEvidence.ProjectionEvidence ??
            throw new InvalidOperationException(
                "Production export evidence has no retained projection snapshot. Rerun production detection before exporting.");
        CalibrationRecord currentCalibration = panel.Calibration ??
            throw new InvalidOperationException(
                "The reviewed panel has no current calibration. Recalibrate before production export.");
        if (!ProductionProjectionMatchesCurrentPanel(panel, projection) ||
            projection.Calibration.CalibrationId != currentCalibration.CalibrationId ||
            !Enum.IsDefined(exportEvidence.Mode))
        {
            throw new InvalidOperationException(
                "Production export evidence is stale relative to the current calibration or panel transforms. Rerun production detection before exporting.");
        }

        SourceReference source = project.Sources.SingleOrDefault(item => item.SourceId == panel.SourceId) ??
            throw new InvalidOperationException(
                "The reviewed panel has no matching persisted source identity. Rerun production import before exporting.");
        string? retainedSourceSha256 = retained.RasterPanelSource?.Source.Image.Sha256 ??
            retained.PdfPanelSource?.DocumentSha256 ??
            retained.Panel.Original.Sha256;
        if (retainedSourceSha256 is null ||
            !string.Equals(source.Sha256, retainedSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Production export source checksum no longer matches the persisted project. Reimport the source before exporting.");
        }

        if (panel.Crop.Width != retained.Panel.Original.Width ||
            panel.Crop.Height != retained.Panel.Original.Height)
        {
            throw new InvalidOperationException(
                "Production export panel dimensions no longer match the retained original image. Reimport the source before exporting.");
        }

        if (retained.RasterPanelSource is { } rasterSource &&
            !RasterSourceMatchesPanel(panel, retained, rasterSource))
        {
            throw new InvalidOperationException(
                "Production export image hash or dimensions no longer match the retained panel. Reimport the source before exporting.");
        }

        if (retained.PdfPanelSource is { } pdfSource &&
            !PdfPanelSourceBinding.MatchesProductionPanel(project, source, panel, retained, pdfSource))
        {
            throw new InvalidOperationException(
                "Production PDF export source panel binding is missing or stale. Reimport the PDF and rerun production detection before exporting.");
        }

        if (exportEvidence.Provenance.Count == 0 || exportEvidence.Provenance.Any(envelope =>
                envelope.ProjectId != project.ProjectId.Value ||
                envelope.PanelId != panel.PanelId.Value ||
                (!string.Equals(envelope.InputSha256, retained.Panel.Original.Sha256, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(envelope.InputSha256, retained.Enhanced?.Sha256, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException(
                "Production export provenance is missing or does not match the current project, panel, or retained image. Rerun production detection before exporting.");
        }

        if (exportEvidence.Mode == ExportMode.PrintedSession &&
            (!exportEvidence.Calibration.HasPrintedSessionCalibration ||
             currentCalibration.SessionLattice is null))
        {
            throw new InvalidOperationException(
                "Printed-session export requires current reviewed session calibration. Recalibrate or select observation-order export.");
        }

    }

    private static bool RasterSourceMatchesPanel(
        PanelRecord panel,
        ProductionPanelEvidence retained,
        RasterPanelSourceProvenance rasterSource)
    {
        if (rasterSource.PanelImageSha256 != retained.Panel.Original.Sha256 ||
            rasterSource.EncodedCropInSourcePixels.Width != retained.Panel.Original.Width ||
            rasterSource.EncodedCropInSourcePixels.Height != retained.Panel.Original.Height)
        {
            return false;
        }

        TransformRecord[] cropTransforms = panel.Transforms.Where(transform =>
                transform.Parameters.ValueKind == JsonValueKind.Object &&
                transform.Parameters.TryGetProperty("schema", out JsonElement schema) &&
                string.Equals(schema.GetString(), "graphreader.raster_source_crop.v1", StringComparison.Ordinal))
            .ToArray();
        if (cropTransforms.Length != 1)
        {
            return false;
        }

        TransformRecord crop = cropTransforms[0];
        JsonElement parameters = crop.Parameters;
        if (!parameters.TryGetProperty("source_sha256", out JsonElement sourceSha) ||
            !string.Equals(sourceSha.GetString(), rasterSource.Source.Image.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !parameters.TryGetProperty("source_width", out JsonElement sourceWidth) ||
            sourceWidth.GetInt32() != rasterSource.Source.Image.Width ||
            !parameters.TryGetProperty("source_height", out JsonElement sourceHeight) ||
            sourceHeight.GetInt32() != rasterSource.Source.Image.Height ||
            !parameters.TryGetProperty("panel_sha256", out JsonElement panelSha) ||
            !string.Equals(panelSha.GetString(), rasterSource.PanelImageSha256, StringComparison.OrdinalIgnoreCase) ||
            !crop.Matrix3x3.SequenceEqual(rasterSource.SourceToPanelMatrix) ||
            crop.InverseMatrix3x3 is null ||
            !crop.InverseMatrix3x3.SequenceEqual(rasterSource.PanelToSourceMatrix))
        {
            return false;
        }

        return CropMatches(parameters, "requested_crop", rasterSource.RequestedCropInSourcePixels) &&
            CropMatches(parameters, "encoded_crop", rasterSource.EncodedCropInSourcePixels);

        static bool CropMatches(JsonElement parameters, string name, PdfRectD expected)
        {
            if (!parameters.TryGetProperty(name, out JsonElement crop) ||
                crop.ValueKind != JsonValueKind.Object ||
                !crop.TryGetProperty("x", out JsonElement x) ||
                !crop.TryGetProperty("y", out JsonElement y) ||
                !crop.TryGetProperty("width", out JsonElement width) ||
                !crop.TryGetProperty("height", out JsonElement height))
            {
                return false;
            }

            return x.GetDouble() == expected.X && y.GetDouble() == expected.Y &&
                width.GetDouble() == expected.Width && height.GetDouble() == expected.Height;
        }
    }

    private bool HasProductionProjectionAudit(Guid panelId)
    {
        string panelIdText = panelId.ToString("D");
        return CurrentProject.Audit.Events.Any(auditEvent =>
            auditEvent.Details is { ValueKind: System.Text.Json.JsonValueKind.Object } details &&
            details.TryGetProperty("kind", out System.Text.Json.JsonElement kind) &&
            string.Equals(kind.GetString(), "production_review_projection", StringComparison.Ordinal) &&
            (ContainsPanel(details, "point_identities", panelIdText, "panel_id") ||
             ContainsPanel(details, "vision_provenance", panelIdText, "panel_id")));

        static bool ContainsPanel(
            System.Text.Json.JsonElement details,
            string propertyName,
            string panelId,
            string idProperty)
        {
            if (!details.TryGetProperty(propertyName, out System.Text.Json.JsonElement values) ||
                values.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return false;
            }

            return values.EnumerateArray().Any(value =>
                value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                value.TryGetProperty(idProperty, out System.Text.Json.JsonElement id) &&
                string.Equals(id.GetString(), panelId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task<WorkflowRunResult> RunAutomaticDetectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AutomaticStageStatus? blocked = RequiredDetectionStages
            .Select(required => AutomaticStages.SingleOrDefault(status =>
                string.Equals(status.Stage, required, StringComparison.Ordinal)))
            .FirstOrDefault(status => status is null ||
                status.State is not (AutomaticStageState.Available or AutomaticStageState.Approved));
        if (blocked is not null)
        {
            throw new InvalidOperationException(blocked.Explanation);
        }

        if (_automaticWorkflow is null && _workflowOrchestrator is null)
        {
            throw new InvalidOperationException(
                "Automatic detection is not composed with approved production adapters.");
        }

        WorkflowRunResult result;
        if (_automaticWorkflow is not null)
        {
            result = await _automaticWorkflow(
                LastAutomaticRun?.Review,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            bool enhancementEnabled = AutomaticStages.Any(status =>
                string.Equals(status.Stage, "enhancement", StringComparison.Ordinal) &&
                status.State == AutomaticStageState.Approved);
            var request = new WorkflowRunRequest(
                Guid.NewGuid(),
                CreateProductionWorkflowImportRequest(enhancementEnabled))
            {
                ManualCalibrations = CreateConfirmedManualCalibrationBindings(),
            };
            WorkflowRunResult? accumulated = LastAutomaticRun;
            var completedPanelIds = new HashSet<Guid>();
            result = await _workflowOrchestrator!.RunThroughReviewAsync(
                request,
                LastAutomaticRun?.Review,
                (checkpoint, _) =>
                {
                    ProductionReviewProjectionResult checkpointProjection =
                        ProjectProductionReview(checkpoint, _panelStore);
                    if (!checkpointProjection.Succeeded)
                    {
                        throw new ProductionWorkflowStageException(
                            checkpointProjection.Failure ?? new ProductionWorkflowFailure(
                                ProductionWorkflowFailureCodes.ReviewProjectionRejected,
                                "Errors.ProductionReviewProjectionRejected",
                                "A completed production batch panel could not be checkpointed.",
                                Recoverable: true,
                                "Keep prior completed panels and rerun automatic detection."));
                    }

                    WorkflowRunResult projectedCheckpoint = checkpointProjection.ProjectedRun ??
                        throw new InvalidOperationException(
                            "A successful production batch checkpoint returned no projected run.");
                    accumulated = MergeCheckpoint(accumulated, projectedCheckpoint);
                    LastAutomaticRun = accumulated;
                    completedPanelIds.Add(projectedCheckpoint.Review.Panels.Single().PanelId);
                    return Task.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);

            Guid[] resultPanelIds = result.Review.Panels.Select(static panel => panel.PanelId).ToArray();
            if (resultPanelIds.Length == 0 ||
                resultPanelIds.Any(panelId => !completedPanelIds.Contains(panelId)) ||
                accumulated is null)
            {
                throw new InvalidOperationException(
                    "The production batch completed without a checkpoint for every review panel.");
            }

            Dictionary<Guid, WorkflowReviewPanel> projectedByPanelId = accumulated.Review.Panels
                .ToDictionary(static panel => panel.PanelId);
            WorkflowReviewPanel[] projectedPanels = resultPanelIds
                .Select(panelId => projectedByPanelId[panelId])
                .ToArray();
            HashSet<Guid> currentPanelIds = resultPanelIds.ToHashSet();
            WorkflowCorrection[] corrections = accumulated.Review.CorrectionJournal
                .Where(correction => currentPanelIds.Contains(correction.PanelId))
                .ToArray();
            var completedReview = new WorkflowReviewState(
                result.Review.ProjectId,
                projectedPanels,
                corrections,
                result.Review.Warnings
                    .Concat(accumulated.Review.Warnings)
                    .Distinct(StringComparer.Ordinal));
            result = new WorkflowRunResult(result.RunId, completedReview, result.Steps);
            LastAutomaticRun = result;
            return result;
        }

        if (result is null)
        {
            throw new InvalidOperationException(
                "The production automatic workflow returned no result.");
        }
        ProductionReviewProjectionResult projection = ProjectProductionReview(result, _panelStore);
        if (!projection.Succeeded)
        {
            throw new ProductionWorkflowStageException(projection.Failure ?? new ProductionWorkflowFailure(
                ProductionWorkflowFailureCodes.ReviewProjectionRejected,
                "Errors.ProductionReviewProjectionRejected",
                "The production review projection failed without structured evidence.",
                Recoverable: true,
                "Keep the current manual review and rerun automatic detection."));
        }

        LastAutomaticRun = projection.ProjectedRun ?? throw new InvalidOperationException(
            "A successful production review projection returned no corrected workflow run.");
        return LastAutomaticRun;
    }

    private static WorkflowRunResult MergeCheckpoint(
        WorkflowRunResult? accumulated,
        WorkflowRunResult checkpoint)
    {
        if (accumulated is null)
        {
            return checkpoint;
        }

        if (accumulated.Review.ProjectId != checkpoint.Review.ProjectId)
        {
            throw new InvalidOperationException(
                "A production batch checkpoint belongs to a different project.");
        }

        WorkflowReviewPanel checkpointPanel = checkpoint.Review.Panels.Single();
        var panels = accumulated.Review.Panels
            .Where(panel => panel.PanelId != checkpointPanel.PanelId)
            .Append(checkpointPanel)
            .ToArray();
        WorkflowCorrection[] corrections = accumulated.Review.CorrectionJournal
            .Where(correction => correction.PanelId != checkpointPanel.PanelId)
            .Concat(checkpoint.Review.CorrectionJournal)
            .ToArray();
        var review = new WorkflowReviewState(
            checkpoint.Review.ProjectId,
            panels,
            corrections,
            accumulated.Review.Warnings
                .Concat(checkpoint.Review.Warnings)
                .Distinct(StringComparer.Ordinal));
        return new WorkflowRunResult(checkpoint.RunId, review, checkpoint.Steps);
    }

    public override async Task RunStageAsync(WorkflowStage stage, CancellationToken cancellationToken)
    {
        if (stage == WorkflowStage.Detect)
        {
            await RunAutomaticDetectionAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await base.RunStageAsync(stage, cancellationToken).ConfigureAwait(false);
    }
}
