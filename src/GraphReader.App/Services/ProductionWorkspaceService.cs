// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.IO;
using System.Text.Json.Serialization;

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
    private const string ProductionExportReceiptKey = "graphreader_production_export_evidence";
    private const string ProductionExportReceiptSchema = "graphreader.production_export_evidence.v1";
    private static readonly JsonSerializerOptions ProductionEvidenceJsonOptions = CreateProductionEvidenceJsonOptions();
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
    private readonly HashSet<Guid> _failedRestorePanelIds = [];

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

    public override async Task<IReadOnlyList<WorkspaceTabViewModel>> OpenProjectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkspaceTabViewModel> tabs = await base.OpenProjectAsync(path, cancellationToken)
            .ConfigureAwait(false);
        _failedRestorePanelIds.Clear();
        foreach (WorkspaceTabViewModel tab in tabs)
        {
            if (Guid.TryParse(tab.PanelId, out Guid panelId))
            {
                _failedRestorePanelIds.Add(panelId);
            }
        }
        RestoreProductionExportEvidence(tabs);
        return tabs;
    }

    protected override void PrepareProjectForSave()
    {
        PanelRecord[] panels = CurrentProject.Panels.ToArray();
        PanelRecord[] refreshed = new PanelRecord[panels.Length];
        for (int index = 0; index < panels.Length; index++)
        {
            PanelRecord panel = panels[index];
            if (_failedRestorePanelIds.Contains(panel.PanelId.Value))
            {
                refreshed[index] = HasProductionProjectionAudit(panel.PanelId.Value)
                    ? InvalidateProductionExportReceipt(panel, "The reopened production receipt did not validate. Rerun production detection before exporting.")
                    : panel;
                continue;
            }

            if (!_panelStore.TryGet(panel.PanelId.Value, out ProductionPanelEvidence? retained) ||
                retained?.ExportEvidence is not { ProjectionEvidence: { } priorProjection } priorEvidence ||
                !ProductionProjectionMatchesCurrentPanel(panel, priorProjection))
            {
                refreshed[index] = HasProductionProjectionAudit(panel.PanelId.Value)
                    ? InvalidateProductionExportReceipt(panel, "Reviewed production evidence no longer matches the saved panel. Rerun production detection before exporting.")
                    : panel;
                continue;
            }

            try
            {
                ProductionPanelExportEvidence updatedEvidence = AlignExportEvidenceToProject(priorEvidence, panel);
                ValidateCurrentProductionExportEvidence(CurrentProject, panel, retained, updatedEvidence);
                SourceReference source = CurrentProject.Sources.Single(item => item.SourceId == panel.SourceId);
                refreshed[index] = panel with
                {
                    Validation = PreserveProductionExportReceipt(
                        panel.Validation,
                        CurrentProject.ProjectId.Value,
                        source,
                        panel,
                        updatedEvidence,
                        retained.Panel.Original.Sha256,
                        retained.Panel.Original.Width,
                        retained.Panel.Original.Height),
                };
                _panelStore.SetExportEvidence(panel.PanelId.Value, updatedEvidence);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                refreshed[index] = InvalidateProductionExportReceipt(
                    panel,
                    "Production export evidence could not be refreshed for the current reviewed state. Rerun production detection before exporting.");
            }
        }

        CurrentProject = CurrentProject with { Panels = refreshed };
    }

    public override async Task<ExportResult> ExportAsync(
        string tabId,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (WorkspaceTabViewModel tab, PanelRecord panel) = PreparePanelForExport(tabId);

        if (!_failedRestorePanelIds.Contains(panel.PanelId.Value) &&
            _panelStore.TryGet(panel.PanelId.Value, out ProductionPanelEvidence? retained) &&
            retained is not null &&
            retained.Panel.PanelId == panel.PanelId.Value &&
            retained.Panel.SourceId == panel.SourceId.Value &&
            retained.ExportEvidence is { } exportEvidence)
        {
            if (tab.Calibration is { FirstVisibleSession: > 1 })
            {
                ExportSessionOriginPolicy expectedPolicy = RequireManualOriginPolicy(panel, tab);
                if (exportEvidence.SessionOriginPolicy != expectedPolicy)
                    throw new InvalidOperationException("Production export origin confirmation no longer matches this calibration.");
            }
            ValidateCurrentProductionExportEvidence(CurrentProject, panel, retained, exportEvidence);
            ProductionPanelExportEvidence currentEvidence = AlignExportEvidenceToProject(exportEvidence, panel);
            return await ExportPreparedPanelAsync(
                tab,
                panel,
                outputDirectory,
                currentEvidence.Mode,
                currentEvidence.AuditMode,
                currentEvidence.Calibration,
                currentEvidence.SessionOriginPolicy,
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

        bool sessionOverride = tab.Calibration.FirstVisibleSession > 1;
        ExportSessionOriginPolicy manualOriginPolicy = sessionOverride
            ? RequireManualOriginPolicy(panel, tab)
            : new ExportSessionOriginPolicy(false, InvalidSessionOriginBehavior.Block);
        return await ExportPreparedPanelAsync(
            tab,
            panel,
            outputDirectory,
            sessionOverride ? ExportMode.PrintedSession : ExportMode.ObservationOrder,
            ExportAuditMode.ExtendedCsvAndJson,
            new ExportCalibration(
                ExportCalibrationStatus.Valid,
                hasYCalibration: true,
                hasPrintedSessionCalibration: sessionOverride,
                hasAbsoluteSessionOrigin: true,
                firstObservedSession: sessionOverride ? FirstObservedSession(panel) : null,
                panel.Calibration.Confidence),
            manualOriginPolicy,
            cancellationToken).ConfigureAwait(false);
    }

    private void RestoreProductionExportEvidence(IReadOnlyList<WorkspaceTabViewModel> tabs)
    {
        foreach (WorkspaceTabViewModel tab in tabs)
        {
            if (tab.PanelId is null)
            {
                continue;
            }

            PanelRecord? panel = CurrentProject.Panels.SingleOrDefault(candidate =>
                string.Equals(candidate.PanelId.Value.ToString("D"), tab.PanelId, StringComparison.OrdinalIgnoreCase));
            if (panel?.Validation is not { ValueKind: JsonValueKind.Object } validation ||
                !validation.TryGetProperty(ProductionExportReceiptKey, out JsonElement receipt) ||
                receipt.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            try
            {
                if (!TryReadProductionExportEvidence(receipt, panel, out ProductionPanelExportEvidence? exportEvidence))
                {
                    continue;
                }
                if (exportEvidence?.ProjectionEvidence is not { } projection)
                {
                    continue;
                }
                if (!TryCreateReopenedProductionPanelEvidence(tab.TabId, exportEvidence, out ProductionPanelEvidence? retained) || retained is null)
                {
                    continue;
                }
                if (!ReceiptMatchesImportedPanel(receipt, retained.Panel.Original))
                {
                    continue;
                }
                if (!ProductionProjectionEntitiesMatchCurrentPanel(panel, projection))
                {
                    continue;
                }

                ValidateCurrentProductionExportEvidence(CurrentProject, panel, retained, exportEvidence);
                _panelStore.Register(retained);
                _panelStore.SetExportEvidence(panel.PanelId.Value, exportEvidence);
                _failedRestorePanelIds.Remove(panel.PanelId.Value);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                               JsonException or KeyNotFoundException or InvalidDataException or
                                               NullReferenceException or FormatException or OverflowException or NotSupportedException)
            {
                // A malformed or stale receipt leaves the production audit intact.
            }
        }
    }

    private static PanelRecord InvalidateProductionExportReceipt(PanelRecord panel, string reason)
    {
        JsonObject validation = panel.Validation is { ValueKind: JsonValueKind.Object } existing
            ? JsonNode.Parse(existing.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        validation[ProductionExportReceiptKey] = new JsonObject
        {
            ["schema"] = ProductionExportReceiptSchema,
            ["status"] = "invalidated",
            ["reason"] = reason,
            ["required_action"] = "rerun_production_detection",
        };
        return panel with { Validation = JsonSerializer.SerializeToElement(validation, ProductionEvidenceJsonOptions) };
    }

    private bool TryReadProductionExportEvidence(
        JsonElement receipt,
        PanelRecord panel,
        out ProductionPanelExportEvidence? evidence)
    {
        evidence = null;
        if (!HasString(receipt, "schema", ProductionExportReceiptSchema)) return Reject("schema");
        if (!receipt.TryGetProperty("project_id", out JsonElement projectId) || !Guid.TryParse(projectId.GetString(), out Guid parsedProjectId)) return Reject("project_id");
        if (!receipt.TryGetProperty("panel_id", out JsonElement panelId) || !Guid.TryParse(panelId.GetString(), out Guid parsedPanelId) || parsedPanelId != panel.PanelId.Value) return Reject("panel_id");
        if (!receipt.TryGetProperty("source_id", out JsonElement sourceId) || !Guid.TryParse(sourceId.GetString(), out Guid parsedSourceId) || parsedSourceId != panel.SourceId.Value) return Reject("source_id");
        if (!receipt.TryGetProperty("source_sha256", out JsonElement sourceSha) || sourceSha.GetString() is not { Length: 64 } sourceHash) return Reject("source_sha256");
        if (!receipt.TryGetProperty("panel_sha256", out JsonElement panelSha) || panelSha.GetString() is not { Length: 64 } panelHash) return Reject("panel_sha256");
        if (!receipt.TryGetProperty("panel_width", out JsonElement width) || !width.TryGetInt32(out int panelWidth) || panelWidth <= 0) return Reject("panel_width");
        if (!receipt.TryGetProperty("panel_height", out JsonElement height) || !height.TryGetInt32(out int panelHeight) || panelHeight <= 0) return Reject("panel_height");
        if (!receipt.TryGetProperty("page_number", out JsonElement pageNumber) || !NullableIntEquals(pageNumber, panel.PageNumber)) return Reject("page_number");
        if (!receipt.TryGetProperty("crop", out JsonElement crop) || !CropMatches(crop, panel.Crop)) return Reject("crop");
        if (!receipt.TryGetProperty("transforms", out JsonElement savedTransforms) ||
            !JsonElement.DeepEquals(
                savedTransforms,
                JsonSerializer.SerializeToElement(panel.Transforms, ProductionEvidenceJsonOptions))) return Reject("transforms");
        if (!receipt.TryGetProperty("calibration_id", out JsonElement calibrationId) ||
            !GuidNullableEquals(calibrationId, panel.Calibration is { } currentCalibration ? currentCalibration.CalibrationId.Value : null)) return Reject("calibration_id");
        if (!receipt.TryGetProperty("export_evidence", out JsonElement rawEvidence) || rawEvidence.ValueKind != JsonValueKind.Object) return Reject("export_evidence");
        if (!receipt.TryGetProperty("export_evidence_sha256", out JsonElement evidenceSha) || evidenceSha.GetString() is not { Length: 64 } expectedEvidenceHash) return Reject("export_evidence_sha256");

        SourceReference? source = CurrentSourceFor(panel);
        if (source is null || parsedProjectId != CurrentProject.ProjectId.Value ||
            !string.Equals(source.Sha256, sourceHash, StringComparison.OrdinalIgnoreCase))
        {
            return Reject("project or source identity");
        }

        try
        {
            evidence = DeserializeProductionPanelExportEvidence(rawEvidence);
        }
        catch (JsonException)
        {
            return Reject("evidence deserialization");
        }

        if (evidence is null)
        {
            return Reject("evidence null");
        }

        byte[] evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(evidence, ProductionEvidenceJsonOptions);
        return string.Equals(
            Convert.ToHexStringLower(SHA256.HashData(evidenceBytes)),
            expectedEvidenceHash,
            StringComparison.OrdinalIgnoreCase) || Reject("evidence hash");

        SourceReference? CurrentSourceFor(PanelRecord currentPanel) =>
            CurrentProject.Sources.SingleOrDefault(item => item.SourceId == currentPanel.SourceId);

        static bool HasString(JsonElement element, string name, string expected) =>
            element.TryGetProperty(name, out JsonElement value) &&
            string.Equals(value.GetString(), expected, StringComparison.Ordinal);

        static bool NullableIntEquals(JsonElement element, int? expected) =>
            expected is null
                ? element.ValueKind == JsonValueKind.Null
                : element.TryGetInt32(out int actual) && actual == expected.Value;

        static bool GuidNullableEquals(JsonElement element, Guid? expected) =>
            expected is null
                ? element.ValueKind == JsonValueKind.Null
                : Guid.TryParse(element.GetString(), out Guid actual) && actual == expected.Value;

        static bool CropMatches(JsonElement element, CropRectangle expected) =>
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("x", out JsonElement x) && x.TryGetDouble(out double actualX) && actualX == expected.X &&
            element.TryGetProperty("y", out JsonElement y) && y.TryGetDouble(out double actualY) && actualY == expected.Y &&
            element.TryGetProperty("width", out JsonElement cropWidth) && cropWidth.TryGetDouble(out double actualWidth) && actualWidth == expected.Width &&
            element.TryGetProperty("height", out JsonElement cropHeight) && cropHeight.TryGetDouble(out double actualHeight) && actualHeight == expected.Height;

        bool Reject(string field) => throw new InvalidOperationException($"Receipt parser rejected field '{field}'.");
    }

    private static bool ReceiptMatchesImportedPanel(JsonElement receipt, WorkflowImageEvidence image) =>
        receipt.TryGetProperty("panel_sha256", out JsonElement sha) &&
        string.Equals(sha.GetString(), image.Sha256, StringComparison.OrdinalIgnoreCase) &&
        receipt.TryGetProperty("panel_width", out JsonElement width) && width.TryGetInt32(out int actualWidth) &&
        actualWidth == image.Width &&
        receipt.TryGetProperty("panel_height", out JsonElement height) && height.TryGetInt32(out int actualHeight) &&
        actualHeight == image.Height;

    internal static ProductionPanelExportEvidence DeserializeProductionPanelExportEvidence(JsonElement value)
    {
        JsonElement calibrationJson = RequiredObject(value, "calibration");
        ExportCalibration calibration = new(
            Required<ExportCalibrationStatus>(calibrationJson, "status"),
            Required<bool>(calibrationJson, "has_y_calibration"),
            Required<bool>(calibrationJson, "has_printed_session_calibration"),
            Required<bool>(calibrationJson, "has_absolute_session_origin"),
            RequiredNullableDouble(calibrationJson, "first_observed_session"),
            Required<double>(calibrationJson, "confidence"),
            RequiredArray<string>(calibrationJson, "reasons"));

        JsonElement projectionJson = RequiredObject(value, "projection_evidence");
        var projection = new ProductionPanelProjectionEvidence(
            Required<CalibrationRecord>(projectionJson, "calibration"),
            RequiredArray<PhaseRecord>(projectionJson, "phases"),
            RequiredArray<SeriesRecord>(projectionJson, "series"),
            RequiredArray<PointRecord>(projectionJson, "points"),
            RequiredArray<TransformRecord>(projectionJson, "transforms"),
            RequiredArray<OcrEvidence>(projectionJson, "ocr_regions"),
            RequiredArray<MarkerRecord>(projectionJson, "markers"),
            RequiredNullableString(projectionJson, "participant"));
        if (!ProductionProjectionEvidenceHasValidStructure(projection))
        {
            throw new JsonException("Production export receipt projection contains null or incomplete nested values.");
        }

        return new ProductionPanelExportEvidence(
            calibration,
            RequiredArray<ExportPhase>(value, "phases"),
            RequiredExportSeriesArray(value),
            RequiredExportRelationArray(value),
            RequiredArray<ProductionPointExportEvidence>(value, "points"),
            RequiredVisionArray(value),
            RequiredNullableString(value, "participant"),
            Required<ExportMode>(value, "mode"),
            Required<ExportAuditMode>(value, "audit_mode"),
            Required<ExportSessionOriginPolicy>(value, "session_origin_policy"),
            projection);

        static JsonElement RequiredObject(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out JsonElement result) && result.ValueKind == JsonValueKind.Object
                ? result
                : throw new JsonException($"Production export receipt field '{name}' is missing or invalid.");

        static T Required<T>(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out JsonElement result)
                ? result.Deserialize<T>(ProductionEvidenceJsonOptions) ?? throw new JsonException($"Field '{name}' is null.")
                : throw new JsonException($"Production export receipt field '{name}' is missing.");

        static T[] RequiredArray<T>(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out JsonElement result) || result.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException($"Production export receipt field '{name}' is missing or invalid.");
            }

            T[] items = result.Deserialize<T[]>(ProductionEvidenceJsonOptions) ??
                throw new JsonException($"Production export receipt field '{name}' is null.");
            if (items.Any(static item => item is null))
            {
                throw new JsonException($"Production export receipt field '{name}' contains a null item.");
            }

            return items;
        }

        static T[]? RequiredNullableArray<T>(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out JsonElement result) && result.ValueKind == JsonValueKind.Array
                ? result.Deserialize<T[]>(ProductionEvidenceJsonOptions)
                : null;

        static T? RequiredNullableValue<T>(JsonElement parent, string name) where T : class =>
            parent.TryGetProperty(name, out JsonElement result) && result.ValueKind != JsonValueKind.Null
                ? result.Deserialize<T>(ProductionEvidenceJsonOptions)
                : null;

        static ExportSeries[] RequiredExportSeriesArray(JsonElement parent) =>
            parent.TryGetProperty("series", out JsonElement values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(item => new ExportSeries(
                    Required<Guid>(item, "series_id"),
                    Required<string>(item, "symbol"),
                    Required<string>(item, "display_name"),
                    Required<ExportSeriesRole>(item, "semantic_role"),
                    RequiredArray<Guid>(item, "point_ids"),
                    Required<double>(item, "confidence"),
                    RequiredNullableString(item, "legend_text"))).ToArray()
                : throw new JsonException("Production export receipt series are missing or invalid.");

        static ExportSeriesRelation[] RequiredExportRelationArray(JsonElement parent) =>
            parent.TryGetProperty("relations", out JsonElement values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(item => new ExportSeriesRelation(
                    Required<Guid>(item, "intervention_series_id"),
                    RequiredNullableGuid(item, "shared_baseline_series_id"),
                    RequiredArray<Guid>(item, "applicable_probe_series_ids"))).ToArray()
                : throw new JsonException("Production export receipt relations are missing or invalid.");

        static WorkflowVisionEnvelope[] RequiredVisionArray(JsonElement parent) =>
            parent.TryGetProperty("provenance", out JsonElement values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(item => new WorkflowVisionEnvelope(
                    Required<int>(item, "contract_version"),
                    Required<Guid>(item, "run_id"),
                    Required<Guid>(item, "project_id"),
                    Required<Guid>(item, "panel_id"),
                    Required<string>(item, "stage"),
                    Required<string>(item, "stage_version"),
                    Required<string>(item, "input_sha256"),
                    RequiredNullableValue<WorkflowVisionModel>(item, "model"),
                    Required<WorkflowVisionTiming>(item, "timing"),
                    Required<double>(item, "confidence"),
                    RequiredArray<string>(item, "warnings"),
                    ReadVisionTransforms(item),
                    Required<string>(item, "coordinate_space"))).ToArray()
                : throw new JsonException("Production export receipt provenance is missing or invalid.");

        static WorkflowTransformProvenance[] ReadVisionTransforms(JsonElement parent) =>
            parent.TryGetProperty("transforms", out JsonElement values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(item => new WorkflowTransformProvenance(
                    Required<string>(item, "transform_id"),
                    Required<string>(item, "input_coordinate_space"),
                    Required<string>(item, "output_coordinate_space"),
                    RequiredArray<double>(item, "input_to_output_matrix"),
                    RequiredNullableArray<double>(item, "output_to_input_matrix"),
                    Required<bool>(item, "lossy"))).ToArray()
                : throw new JsonException("Vision provenance transforms are missing or invalid.");

        static double? RequiredNullableDouble(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out JsonElement result) && result.ValueKind != JsonValueKind.Null
                ? result.GetDouble()
                : null;

        static string? RequiredNullableString(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out JsonElement result) && result.ValueKind != JsonValueKind.Null
                ? result.GetString()
                : null;

        static Guid? RequiredNullableGuid(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out JsonElement result) && result.ValueKind != JsonValueKind.Null
                ? result.GetGuid()
                : null;
    }

    private static JsonSerializerOptions CreateProductionEvidenceJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
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
                    MarkCurrentProductionPanels(projectedCheckpoint.Review.Panels);
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
        MarkCurrentProductionPanels(LastAutomaticRun.Review.Panels);
        return LastAutomaticRun;
    }

    private void MarkCurrentProductionPanels(IEnumerable<WorkflowReviewPanel> panels)
    {
        foreach (WorkflowReviewPanel panel in panels)
        {
            _failedRestorePanelIds.Remove(panel.PanelId);
        }
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
