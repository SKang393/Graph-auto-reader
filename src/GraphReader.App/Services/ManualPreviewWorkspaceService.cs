// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Localization;
using GraphReader.App.Models;
using GraphReader.App.ViewModels;
using GraphReader.Axis;
using GraphReader.Domain;
using GraphReader.Export;
using GraphReader.Imaging;
using GraphReader.Pdf;
using GraphReader.Phases;
using GraphReader.SuperResolution;
using AxisPixelPoint = GraphReader.Axis.PixelPoint;
using AppGraphPoint = GraphReader.App.Models.GraphPoint;
using DomainCalibrationAnchor = GraphReader.Domain.CalibrationAnchor;
using DomainCalibrationAnchorKind = GraphReader.Domain.CalibrationAnchorKind;
using DomainCalibrationStatus = GraphReader.Domain.CalibrationStatus;
using DomainGraphPoint = GraphReader.Domain.GraphPoint;
using DomainPixelPoint = GraphReader.Domain.PixelPoint;
using DomainPhaseNormalizedType = GraphReader.Domain.PhaseNormalizedType;
using ExportPhaseType = GraphReader.Export.ExportPhaseType;
using ExportReviewStatus = GraphReader.Export.ExportReviewStatus;
using PhaseNormalizedType = GraphReader.Phases.PhaseNormalizedType;

namespace GraphReader.App.Services;

/// <summary>
/// Real-data manual workspace. It intentionally begins empty and never supplies
/// recorded detections when production models are absent.
/// </summary>
public class ManualPreviewWorkspaceService : IManualWorkspaceService, IWorkspaceEditStateSink
{
    private const string ProductionProjectionAuditKind = "production_review_projection";
    private const string ProductionAddAuditKind = "production_point_added";
    private const string ProductionMoveAuditKind = "production_point_moved";
    private const string ProductionDeleteAuditKind = "production_point_deleted";
    private const string ProductionReassignAuditKind = "production_point_reassigned";
    private const string ProductionPhaseAuditKind = "production_phase_corrected";
    private const string RasterCropTransformSchema = "graphreader.raster_source_crop.v1";

    private sealed record DeletedPointTombstone(
        string CorrectionId,
        string PointId,
        string? DetectionKey);

    private sealed record PdfWorkspacePanel(
        WorkflowImportedPanel Panel,
        ImmutableImageBytes OriginalBytes);

    private sealed record PdfWorkspaceImport(
        string DocumentSha256,
        IReadOnlyList<PdfWorkspacePanel> Panels);

    private sealed record RasterWorkspacePanel(
        WorkflowImportedPanel Panel,
        ImmutableImageBytes OriginalBytes,
        RasterPanelSourceProvenance SourceProvenance);

    private sealed record RasterWorkspaceImport(
        RasterSourceImageEvidence Source,
        IReadOnlyList<RasterWorkspacePanel> Panels);

    private readonly IApplicationPaths? _applicationPaths;
    private readonly IImageImportService _imageImportService;
    private readonly IPdfImportService? _pdfImportService;
    private readonly ProjectFileStore _projectFileStore;
    private readonly IExportService _exportService;
    private readonly IPhaseManualEditor _phaseEditor;
    private readonly WorkflowRuntimeEnvironment _runtimeEnvironment;
    private readonly IReadOnlyList<AutomaticStageStatus> _automaticStages;
    private readonly Func<CancellationToken, Task<RealEsrganBackendResolution>>? _enhancementResolver;
    private readonly List<WorkspaceTabViewModel> _tabs = [];
    private readonly Dictionary<string, PhaseManualOverrides> _phaseOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ManualPointXState> _pointXStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PointModification>> _pointModificationHistories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string?>> _productionDetectionKeysByTab = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, DeletedPointTombstone>> _deletedPointTombstonesByTab = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _enhancementByTab = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnhancementEnvelope> _enhancementEnvelopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImmutableImageBytes> _originalPanelBytesByTab = new(StringComparer.Ordinal);
    private IReadOnlyList<ImageImportError> _lastImportErrors = [];

    public ManualPreviewWorkspaceService(
        IApplicationPaths? applicationPaths = null,
        IImageImportService? imageImportService = null,
        ProjectFileStore? projectFileStore = null,
        IExportService? exportService = null,
        IPhaseManualEditor? phaseEditor = null,
        WorkflowRuntimeEnvironment runtimeEnvironment = WorkflowRuntimeEnvironment.ManualPreview,
        IReadOnlyList<AutomaticStageStatus>? automaticStages = null,
        Func<CancellationToken, Task<RealEsrganBackendResolution>>? enhancementResolver = null,
        IPdfImportService? pdfImportService = null)
    {
        if (runtimeEnvironment == WorkflowRuntimeEnvironment.RecordedFake)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runtimeEnvironment),
                "The real-data manual workspace cannot use the recorded-fake runtime identity.");
        }

        _applicationPaths = applicationPaths;
        _imageImportService = imageImportService ?? new ImageImportService();
        _pdfImportService = pdfImportService;
        _projectFileStore = projectFileStore ?? new ProjectFileStore();
        _exportService = exportService ?? new ExportService();
        _phaseEditor = phaseEditor ?? new PhaseManualEditor();
        _runtimeEnvironment = runtimeEnvironment;
        _automaticStages = (automaticStages ?? DefaultAutomaticStages).ToArray();
        _enhancementResolver = enhancementResolver;
        CurrentProject = ProjectDocument.Create(GetApplicationVersion(), DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<AutomaticStageStatus> DefaultAutomaticStages { get; } =
    [
        new("enhancement", AutomaticStageState.Unavailable, "Enhancement is unavailable. Install an approved Real-ESRGAN runtime and model to enable it."),
        new("axis", AutomaticStageState.Unavailable, "Automatic axis detection is unavailable. Use manual three-anchor calibration."),
        new("ocr", AutomaticStageState.Unavailable, "Install approved OCR models to enable text recognition."),
        new("markers", AutomaticStageState.Unavailable, "Install approved marker models to enable marker detection."),
        new("legends", AutomaticStageState.Unavailable, "Automatic legend reasoning is unavailable. Create and label series manually."),
        new("phases", AutomaticStageState.Unavailable, "Automatic phase detection is unavailable. Add and label dividers manually."),
    ];

    public WorkflowRuntimeEnvironment RuntimeEnvironment => _runtimeEnvironment;

    public IReadOnlyList<AutomaticStageStatus> AutomaticStages => _automaticStages;

    public bool UsesFakeGraphData => false;

    public ProjectDocument CurrentProject { get; private set; }

    public string? CurrentProjectPath { get; private set; }

    public IReadOnlyList<ImageImportError> LastImportErrors => _lastImportErrors;

    public IReadOnlyList<WorkspaceTabViewModel> CreateWorkspace() => _tabs.ToArray();

    public void SynchronizeRestoredTab(string tabId)
    {
        WorkspaceTabViewModel tab = RequireTab(tabId);
        _phaseOverrides[tab.TabId] = CreatePhaseOverrides(tab);
        HashSet<string> activePointIds = _tabs.SelectMany(static item => item.Points)
            .Select(static point => point.PointId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string removedPointId in _pointXStates.Keys.Where(id => !activePointIds.Contains(id)).ToArray())
        {
            _pointXStates.Remove(removedPointId);
            _pointModificationHistories.Remove(removedPointId);
        }
        foreach (AppGraphPoint point in tab.Points)
        {
            _pointXStates[point.PointId] = tab.Calibration is null
                ? new ManualPointXState(null, null, PointXSource.Unknown, 0, HasGraphX: false)
                : new ManualPointXState(null, point.GraphX, PointXSource.Estimated, 1, HasGraphX: true);
            if (!_pointModificationHistories.ContainsKey(point.PointId))
            {
                _pointModificationHistories[point.PointId] = [];
            }
        }
        SynchronizeProject(
            DomainEventKind.PointEdited,
            Guid.Parse(tab.PanelId!),
            entityId: null,
            "Session edit history restored a tab state");
    }

    public async Task<IReadOnlyList<WorkspaceTabViewModel>> ImportImagesAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] requestedPaths = paths.ToArray();
        if (requestedPaths.Length == 0)
        {
            return [];
        }

        var addedTabs = new List<WorkspaceTabViewModel>();
        var errors = new List<ImageImportError>();
        _lastImportErrors = [];
        string[] imagePaths = requestedPaths.Where(static path => !IsPdfPath(path)).ToArray();
        foreach (string imagePath in imagePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceId = SourceId.New();
            try
            {
                RasterWorkspaceImport imported = await LoadRasterPanelsAsync(
                        CurrentProject.ProjectId.Value,
                        sourceId,
                        imagePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                var source = new SourceReference(
                    sourceId,
                    SourceKind.Image,
                    Path.GetFileName(imported.Source.Image.Reference),
                    imported.Source.Image.Reference,
                    imported.Source.Image.Sha256,
                    ArticleMetadata: null);
                var seededPanels = new List<PanelRecord>(imported.Panels.Count);
                foreach (RasterWorkspacePanel panel in imported.Panels)
                {
                    WorkspaceTabViewModel tab = CreateEmptyRasterTab(source, panel);
                    RegisterImportedTab(tab, panel.OriginalBytes, addedTabs);
                    seededPanels.Add(CreateRasterPanelSeed(tab, panel.SourceProvenance));
                }

                CommitImportedSource(source, seededPanels);
                _lastImportErrors = errors.ToArray();
            }
            catch (ProductionWorkflowStageException exception)
            {
                errors.Add(ToImportError(imagePath, exception.Failure));
                _lastImportErrors = errors.ToArray();
            }
        }

        foreach (string pdfPath in requestedPaths.Where(IsPdfPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceId = SourceId.New();
            try
            {
                PdfWorkspaceImport imported = await LoadPdfPanelsAsync(
                        CurrentProject.ProjectId.Value,
                        sourceId,
                        pdfPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                var source = new SourceReference(
                    sourceId,
                    SourceKind.Pdf,
                    Path.GetFileName(pdfPath),
                    Path.GetFullPath(pdfPath),
                    imported.DocumentSha256,
                    ArticleMetadata: null);
                foreach (PdfWorkspacePanel panel in imported.Panels)
                {
                    WorkspaceTabViewModel tab = CreateEmptyPdfTab(source, panel);
                    RegisterImportedTab(tab, panel.OriginalBytes, addedTabs);
                }

                CommitImportedSource(source);
                _lastImportErrors = errors.ToArray();
            }
            catch (ProductionWorkflowStageException exception)
            {
                errors.Add(ToImportError(pdfPath, exception.Failure));
                _lastImportErrors = errors.ToArray();
            }
        }

        _lastImportErrors = errors.ToArray();
        return addedTabs;
    }

    private void CommitImportedSource(
        SourceReference source,
        IReadOnlyList<PanelRecord>? seededPanels = null)
    {
        CurrentProject = CurrentProject with
        {
            ModifiedUtc = DateTimeOffset.UtcNow,
            Sources = CurrentProject.Sources.Append(source).ToArray(),
            Panels = seededPanels is null
                ? CurrentProject.Panels
                : CurrentProject.Panels.Concat(seededPanels).ToArray(),
        };
        SynchronizeProject(
            DomainEventKind.DetectionAccepted,
            panelId: null,
            entityId: null,
            "Real image or PDF panel import");
    }

    public async Task<IReadOnlyList<WorkspaceTabViewModel>> OpenProjectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        DomainResult<ProjectDocument> loaded = await _projectFileStore.LoadAsync(path, cancellationToken)
            .ConfigureAwait(false);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            throw new InvalidOperationException(FormatErrors(loaded.Errors));
        }

        ProjectDocument project = loaded.Value;
        var importedLegacyRasterSources = new Dictionary<SourceId, ImportedImage>();
        var importedRasterPanelsById = new Dictionary<PanelId, RasterWorkspacePanel>();
        var importedPdfPanelsById = new Dictionary<PanelId, PdfWorkspacePanel>();
        var errors = new List<ImageImportError>();
        foreach (SourceReference source in project.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(source.LocalPath))
            {
                throw new InvalidOperationException($"Source '{source.DisplayName}' has no local path.");
            }

            if (source.Kind == SourceKind.Image)
            {
                PanelRecord[] sourcePanels = project.Panels
                    .Where(panel => panel.SourceId == source.SourceId)
                    .ToArray();
                TransformRecord?[] cropTransforms = sourcePanels
                    .Select(panel => panel.Transforms.SingleOrDefault(IsRasterCropTransform))
                    .ToArray();
                if (cropTransforms.Any(static transform => transform is not null))
                {
                    if (cropTransforms.Any(static transform => transform is null))
                    {
                        throw new InvalidOperationException(
                            $"Source '{source.DisplayName}' mixes legacy and source-bound raster panels.");
                    }
                    RetainedRasterPanelReference[] retainedPanels = sourcePanels
                        .Select(panel => ReadRetainedRasterPanelReference(panel, source))
                        .ToArray();
                    RasterWorkspaceImport imported = await RestoreRasterPanelsAsync(
                            project.ProjectId.Value,
                            source.SourceId,
                            source.LocalPath,
                            source.Sha256,
                            retainedPanels,
                            cancellationToken)
                        .ConfigureAwait(false);
                    foreach (RasterWorkspacePanel panel in imported.Panels)
                    {
                        importedRasterPanelsById[PanelId.FromGuid(panel.Panel.PanelId)] = panel;
                    }
                }
                else
                {
                    ImageImportResult imported = await _imageImportService
                        .ImportAsync(source.LocalPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (!imported.IsSuccess || imported.Image is null)
                    {
                        throw new InvalidOperationException(
                            imported.Error?.TechnicalMessage ?? $"Source '{source.DisplayName}' could not be read.");
                    }
                    if (!string.Equals(imported.Image.Sha256, source.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Source '{source.DisplayName}' no longer matches its saved SHA-256.");
                    }
                    importedLegacyRasterSources[source.SourceId] = imported.Image;
                }
                continue;
            }

            try
            {
                PdfWorkspaceImport imported = await LoadPdfPanelsAsync(
                        project.ProjectId.Value,
                        source.SourceId,
                        source.LocalPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        imported.DocumentSha256,
                        source.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Source '{source.DisplayName}' no longer matches its saved SHA-256.");
                }

                foreach (PdfWorkspacePanel panel in imported.Panels)
                {
                    importedPdfPanelsById[PanelId.FromGuid(panel.Panel.PanelId)] = panel;
                }
            }
            catch (ProductionWorkflowStageException exception)
            {
                errors.Add(ToImportError(source.LocalPath, exception.Failure));
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" | ", errors.Select(static error => error.TechnicalMessage)));
        }

        var stagedTabs = new List<WorkspaceTabViewModel>(project.Panels.Count);
        var stagedPhaseOverrides = new Dictionary<string, PhaseManualOverrides>(StringComparer.Ordinal);
        var stagedPointXStates = new Dictionary<string, ManualPointXState>(StringComparer.Ordinal);
        var stagedPointHistories = new Dictionary<string, List<PointModification>>(StringComparer.Ordinal);
        var stagedDetectionKeys = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        var stagedTombstones = new Dictionary<string, Dictionary<string, DeletedPointTombstone>>(StringComparer.Ordinal);
        var stagedEnhancement = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var stagedOriginalBytes = new Dictionary<string, ImmutableImageBytes>(StringComparer.Ordinal);
        foreach (PanelRecord panel in project.Panels)
        {
            SourceReference source = project.Sources.Single(item => item.SourceId == panel.SourceId);
            WorkspaceTabViewModel tab;
            ImmutableImageBytes originalBytes;
            if (source.Kind == SourceKind.Image)
            {
                if (importedRasterPanelsById.TryGetValue(panel.PanelId, out RasterWorkspacePanel? rasterPanel))
                {
                    VerifySavedRasterPanel(panel, rasterPanel);
                    originalBytes = rasterPanel.OriginalBytes;
                    tab = CreateTabFromProject(
                        panel,
                        source,
                        originalBytes,
                        rasterPanel.Panel.Original.Width,
                        rasterPanel.Panel.Original.Height,
                        rasterPanel.Panel.Original.Sha256);
                }
                else if (importedLegacyRasterSources.TryGetValue(panel.SourceId, out ImportedImage? rasterSource) &&
                         IsLegacyFullImagePanel(
                             project.Panels.Count(candidate => candidate.SourceId == panel.SourceId),
                             panel,
                             rasterSource))
                {
                    originalBytes = rasterSource.OriginalBytes;
                    tab = CreateTabFromProject(
                        panel,
                        source,
                        originalBytes,
                        rasterSource.Metadata.Width,
                        rasterSource.Metadata.Height,
                        rasterSource.Sha256);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Saved image panel '{panel.PanelId.Value:D}' was not reproduced by the current source bytes.");
                }
            }
            else
            {
                if (!importedPdfPanelsById.TryGetValue(panel.PanelId, out PdfWorkspacePanel? pdfPanel))
                {
                    throw new InvalidOperationException(
                        $"Saved PDF panel '{panel.PanelId.Value:D}' was not reproduced by the current source bytes.");
                }

                originalBytes = pdfPanel.OriginalBytes;
                tab = CreateTabFromProject(
                    panel,
                    source,
                    originalBytes,
                    pdfPanel.Panel.Original.Width,
                    pdfPanel.Panel.Original.Height,
                    pdfPanel.Panel.Original.Sha256);
            }

            ReindexAllSeries(tab);
            stagedTabs.Add(tab);
            stagedOriginalBytes[tab.TabId] = originalBytes;
            stagedPhaseOverrides[tab.TabId] = CreatePhaseOverrides(tab);
            stagedDetectionKeys[tab.TabId] = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            stagedTombstones[tab.TabId] = new Dictionary<string, DeletedPointTombstone>(StringComparer.OrdinalIgnoreCase);
            if (panel.Enhancement is JsonElement enhancement)
            {
                stagedEnhancement[tab.TabId] = enhancement.Clone();
            }
            foreach (PointRecord point in panel.Points)
            {
                string pointId = point.PointId.Value.ToString("D");
                stagedPointXStates[pointId] = new ManualPointXState(
                    point.PrintedXValue,
                    point.EstimatedXValue,
                    point.XSource,
                    point.XConfidence,
                    point.GraphX.HasValue);
                stagedPointHistories[pointId] = point.ModificationHistory
                    .Select(modification => MapModificationSourceToPanel(panel, modification))
                    .ToList();
            }
        }

        RestoreProductionCorrectionState(project, stagedTabs, stagedDetectionKeys, stagedTombstones);
        _tabs.Clear();
        _tabs.AddRange(stagedTabs);
        ReplaceContents(_phaseOverrides, stagedPhaseOverrides);
        ReplaceContents(_pointXStates, stagedPointXStates);
        ReplaceContents(_pointModificationHistories, stagedPointHistories);
        ReplaceContents(_productionDetectionKeysByTab, stagedDetectionKeys);
        ReplaceContents(_deletedPointTombstonesByTab, stagedTombstones);
        ReplaceContents(_enhancementByTab, stagedEnhancement);
        _enhancementEnvelopes.Clear();
        ReplaceContents(_originalPanelBytesByTab, stagedOriginalBytes);
        CurrentProject = project;
        CurrentProjectPath = Path.GetFullPath(path);
        _lastImportErrors = [];
        return CreateWorkspace();
    }

    public async Task<WorkspaceEnhancementResult> EnhanceAsync(
        string tabId,
        CancellationToken cancellationToken)
    {
        WorkspaceTabViewModel tab = RequireTab(tabId);
        if (_enhancementResolver is null)
        {
            return new WorkspaceEnhancementResult(
                false,
                "Official Real-ESRGAN enhancement is not configured. The original image remains unchanged.",
                UserMessageKey: LocalizationKeys.WorkflowEnhanceUnavailable);
        }

        if (_applicationPaths is null || string.IsNullOrWhiteSpace(tab.SourcePath) || tab.PanelId is null)
        {
            return new WorkspaceEnhancementResult(
                false,
                "Enhancement storage or source metadata is unavailable. The original image remains unchanged.",
                UserMessageKey: LocalizationKeys.EnhancementStorageUnavailable);
        }

        RealEsrganBackendResolution backend = await _enhancementResolver(cancellationToken).ConfigureAwait(false);
        if (!backend.IsAvailable || backend.Service is null || backend.Model is null)
        {
            string message = backend.Diagnostic?.TechnicalMessage ??
                "The configured Real-ESRGAN runtime or model is unavailable.";
            return new WorkspaceEnhancementResult(
                false,
                $"{message} The original image remains unchanged.",
                UserMessageKey: backend.Diagnostic?.UserMessageKey ?? LocalizationKeys.EnhancementRuntimeUnavailable);
        }

        if (!_originalPanelBytesByTab.TryGetValue(tab.TabId, out ImmutableImageBytes? originalPanelBytes))
        {
            return new WorkspaceEnhancementResult(
                false,
                "The immutable panel image is unavailable. The original image remains unchanged.",
                UserMessageKey: LocalizationKeys.EnhancementStorageUnavailable);
        }

        string enhancementSourcePath = await MaterializeEnhancementSourceAsync(
                tab,
                originalPanelBytes,
                cancellationToken)
            .ConfigureAwait(false);
        string derivativeRoot = Path.Combine(_applicationPaths.CacheRoot, "Enhancement", "Derivatives");
        Directory.CreateDirectory(derivativeRoot);
        string outputPath = Path.Combine(
            derivativeRoot,
            $"{Guid.Parse(tab.PanelId):N}-{Guid.NewGuid():N}-realesrgan-x2.png");
        var request = new EnhancementRequest(
            CurrentProject.ProjectId.Value,
            Guid.Parse(tab.PanelId),
            enhancementSourcePath,
            outputPath,
            new PixelDimensions(tab.PixelWidth, tab.PixelHeight),
            backend.Model,
            new EnhancementOptions(Scale: 2, ContinueWithoutEnhancement: true));
        EnhancementResult result = await backend.Service.EnhanceAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess || result.OutputPath is null || result.Envelope is null)
        {
            return new WorkspaceEnhancementResult(
                false,
                $"{result.Diagnostic.Message} The original image remains unchanged.",
                RuntimeMilliseconds: result.Envelope?.TimingMs.Total ?? 0,
                UserMessageKey: LocalizationKeys.EnhancementExecutionFailed);
        }

        byte[] enhancedBytes = await File.ReadAllBytesAsync(result.OutputPath, cancellationToken)
            .ConfigureAwait(false);
        tab.EnhancedImageSource = CreateBitmap(new ImmutableImageBytes(enhancedBytes));
        tab.EnhancementPreviewMode = EnhancementPreviewMode.Enhanced;
        _enhancementEnvelopes[tab.TabId] = result.Envelope;
        UpdateEnhancementProvenance(tab, result.Envelope);
        SynchronizeProject(
            DomainEventKind.DetectionAccepted,
            Guid.Parse(tab.PanelId),
            backend.Model.ModelId,
            "Official Real-ESRGAN x2 derivative recorded for local evaluation");
        return new WorkspaceEnhancementResult(
            true,
            $"Enhanced preview ready with {backend.Model.ModelId} at 2x. The original image is unchanged.",
            result.OutputPath,
            result.Envelope.TimingMs.Total,
            LocalizationKeys.StatusEnhancementReadyFormat,
            [backend.Model.ModelId]);
    }

    public void SetEnhancementPreviewMode(string tabId, EnhancementPreviewMode mode)
    {
        WorkspaceTabViewModel tab = RequireTab(tabId);
        tab.EnhancementPreviewMode = mode;
        if (_enhancementEnvelopes.TryGetValue(tab.TabId, out EnhancementEnvelope? envelope))
        {
            UpdateEnhancementProvenance(tab, envelope);
            SynchronizeProject(
                DomainEventKind.ExportSettingsChanged,
                Guid.Parse(tab.PanelId!),
                envelope.Model.ModelId,
                $"Enhancement preview mode changed to {tab.EnhancementPreviewMode}");
        }
    }

    public bool CloseTab(string tabId)
    {
        WorkspaceTabViewModel? tab = _tabs.SingleOrDefault(
            item => string.Equals(item.TabId, tabId, StringComparison.Ordinal));
        if (tab is null)
        {
            return false;
        }

        _tabs.Remove(tab);
        _phaseOverrides.Remove(tab.TabId);
        _productionDetectionKeysByTab.Remove(tab.TabId);
        _deletedPointTombstonesByTab.Remove(tab.TabId);
        _enhancementByTab.Remove(tab.TabId);
        _enhancementEnvelopes.Remove(tab.TabId);
        _originalPanelBytesByTab.Remove(tab.TabId);
        foreach (AppGraphPoint point in tab.Points)
        {
            _pointXStates.Remove(point.PointId);
            _pointModificationHistories.Remove(point.PointId);
        }

        return true;
    }

    public async Task<DomainResult<ProjectSaveReceipt>> SaveProjectAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        string? selectedPath = string.IsNullOrWhiteSpace(path) ? CurrentProjectPath : Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return DomainResult<ProjectSaveReceipt>.Failure(new DomainError(
                "PROJECT_SAVE_PATH_REQUIRED",
                DomainErrorSeverity.Warning,
                "Errors.ProjectSavePathInvalid",
                "Select a project file path before saving.",
                Recoverable: true,
                "select_project_path"));
        }

        SynchronizeProject(DomainEventKind.ExportSettingsChanged, panelId: null, entityId: null, "Manual project save");
        DomainResult<ProjectSaveReceipt> saved = await _projectFileStore
            .SaveAsync(CurrentProject, selectedPath, cancellationToken)
            .ConfigureAwait(false);
        if (saved.IsSuccess)
        {
            CurrentProjectPath = selectedPath;
        }

        return saved;
    }

    public ManualCalibrationState Calibrate(string tabId, ManualCalibrationRequest request)
    {
        WorkspaceTabViewModel tab = RequireTab(tabId);
        ValidateCalibrationRequest(request);
        LinearAxisTransform xTransform = FitTransform(
            request.Session1Y0.X,
            1,
            request.SessionMaximumY0.X,
            request.XMaximum);
        LinearAxisTransform yTransform = FitTransform(
            request.Session1Y0.Y,
            0,
            request.Session1YMaximum.Y,
            request.YMaximum);

        var state = new ManualCalibrationState(
            request.Session1Y0,
            request.Session1YMaximum,
            request.SessionMaximumY0,
            request.YMaximum,
            request.XMaximum,
            xTransform,
            yTransform,
            Confidence: 1);
        tab.Calibration = state;
        foreach (AppGraphPoint point in tab.Points)
        {
            UpdatePointCoordinates(tab, point);
            MarkPointXAsManualEstimate(tab, point);
        }

        SynchronizeProject(
            DomainEventKind.CalibrationChanged,
            Guid.Parse(tab.PanelId!),
            entityId: null,
            "Manual three-anchor calibration");
        return state;
    }

    public SeriesCardViewModel AddSeries(string tabId, ManualSeriesDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Symbol);
        WorkspaceTabViewModel tab = RequireTab(tabId);
        var series = new SeriesCardViewModel(
            Guid.NewGuid().ToString("D"),
            definition.Symbol,
            $"{definition.Fill} {definition.Shape}",
            definition.DisplayName,
            1,
            tab.Points,
            definition.Shape,
            definition.Fill,
            definition.SemanticRole);
        tab.SeriesCards.Add(series);
        SynchronizeProject(DomainEventKind.PointEdited, Guid.Parse(tab.PanelId!), series.SeriesId, "Manual series created");
        return series;
    }

    public void UpdateSeries(string tabId, string seriesId, ManualSeriesDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Symbol);
        WorkspaceTabViewModel tab = RequireTab(tabId);
        SeriesCardViewModel series = RequireSeries(tab, seriesId);
        series.Label = definition.DisplayName;
        series.Symbol = definition.Symbol;
        series.AccessibleName = $"{definition.Fill} {definition.Shape}";
        series.Shape = definition.Shape;
        series.Fill = definition.Fill;
        series.SemanticRole = definition.SemanticRole;
        SynchronizeProject(
            DomainEventKind.PointEdited,
            Guid.Parse(tab.PanelId!),
            series.SeriesId,
            "Manual series edited");
    }

    public void SetSeriesRelations(
        string tabId,
        string interventionSeriesId,
        string? sharedBaselineSeriesId,
        IEnumerable<string> applicableProbeSeriesIds)
    {
        ArgumentNullException.ThrowIfNull(applicableProbeSeriesIds);
        WorkspaceTabViewModel tab = RequireTab(tabId);
        SeriesCardViewModel intervention = RequireSeries(tab, interventionSeriesId);
        if (intervention.SemanticRole != SemanticRole.Intervention)
        {
            throw new ArgumentException("Series relations can be assigned only to an intervention series.", nameof(interventionSeriesId));
        }

        SeriesId? baselineId = null;
        if (sharedBaselineSeriesId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedBaselineSeriesId);
            if (string.Equals(sharedBaselineSeriesId, intervention.SeriesId, StringComparison.Ordinal))
            {
                throw new ArgumentException("An intervention series cannot link to itself as its shared baseline.", nameof(sharedBaselineSeriesId));
            }

            SeriesCardViewModel baseline = RequireSeries(tab, sharedBaselineSeriesId);
            if (baseline.SemanticRole != SemanticRole.Baseline)
            {
                throw new ArgumentException("A shared baseline link must reference a baseline series in the same tab.", nameof(sharedBaselineSeriesId));
            }

            baselineId = SeriesId.FromGuid(Guid.Parse(baseline.SeriesId));
        }

        string[] requestedProbeIds = applicableProbeSeriesIds.ToArray();
        var probeIds = new SeriesId[requestedProbeIds.Length];
        for (int index = 0; index < requestedProbeIds.Length; index++)
        {
            string probeSeriesId = requestedProbeIds[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(probeSeriesId);
            if (string.Equals(probeSeriesId, intervention.SeriesId, StringComparison.Ordinal))
            {
                throw new ArgumentException("An intervention series cannot link to itself as an applicable probe.", nameof(applicableProbeSeriesIds));
            }

            SeriesCardViewModel probe = RequireSeries(tab, probeSeriesId);
            if (probe.SemanticRole is not (SemanticRole.Maintenance or SemanticRole.Generalization))
            {
                throw new ArgumentException(
                    "An applicable probe link must reference a maintenance or generalization series in the same tab.",
                    nameof(applicableProbeSeriesIds));
            }

            probeIds[index] = SeriesId.FromGuid(Guid.Parse(probe.SeriesId));
        }

        PanelId panelId = PanelId.FromGuid(Guid.Parse(tab.PanelId!));
        SeriesId interventionId = SeriesId.FromGuid(Guid.Parse(intervention.SeriesId));
        CurrentProject = CurrentProject with
        {
            Panels = CurrentProject.Panels.Select(panel => panel.PanelId == panelId
                ? panel with
                {
                    Series = panel.Series.Select(series => series.SeriesId == interventionId
                        ? series with
                        {
                            SharedBaselineSeriesId = baselineId,
                            ApplicableProbeSeriesIds = probeIds,
                        }
                        : series).ToArray(),
                }
                : panel).ToArray(),
        };
        SynchronizeProject(
            DomainEventKind.ExportSettingsChanged,
            panelId.Value,
            intervention.SeriesId,
            "Manual intervention series relations changed");
    }

    public AppGraphPoint AddPoint(string tabId, string seriesId, double pixelX, double pixelY)
    {
        WorkspaceTabViewModel tab = RequireTab(tabId);
        _ = RequireSeries(tab, seriesId);
        EnsureFinitePoint(pixelX, pixelY);
        var point = new AppGraphPoint(
            Guid.NewGuid().ToString("D"),
            seriesId,
            pixelX,
            pixelY,
            0,
            0,
            "a",
            observationIndex: 1);
        UpdatePointCoordinates(tab, point);
        MarkPointXAsManualEstimate(tab, point);
        _pointModificationHistories[point.PointId] = [];
        tab.Points.Add(point);
        ReindexSeries(tab, seriesId);
        RequireSeries(tab, seriesId).NotifyCountChanged();
        SynchronizeProject(
            DomainEventKind.PointEdited,
            Guid.Parse(tab.PanelId!),
            point.PointId,
            "Manual point added",
            JsonSerializer.SerializeToElement(new
            {
                kind = ProductionAddAuditKind,
                correction_id = Guid.NewGuid().ToString("D"),
                point_id = point.PointId,
            }));
        return point;
    }

    public void MovePoint(string tabId, string pointId, double pixelX, double pixelY)
    {
        WorkspaceTabViewModel tab = RequireTab(tabId);
        EnsureFinitePoint(pixelX, pixelY);
        AppGraphPoint point = RequirePoint(tab, pointId);
        DomainPixelPoint previousPixel = new(point.PixelX, point.PixelY);
        DomainGraphPoint? previousGraph = GetPreviousGraph(tab, point);
        point.PixelX = pixelX;
        point.PixelY = pixelY;
        UpdatePointCoordinates(tab, point);
        MarkPointXAsManualEstimate(tab, point);
        AppendPointModification(point, previousPixel, previousGraph, "Manual point moved");
        ReindexSeries(tab, point.SeriesId);
        _ = GetProductionDetectionKeys(tab.TabId).TryGetValue(point.PointId, out string? detectionKey);
        PanelRecord panel = CurrentProject.Panels.Single(candidate =>
            candidate.PanelId.Value == Guid.Parse(tab.PanelId!));
        DomainPixelPoint persistedPixel = MapPointPanelToSource(
            panel,
            new DomainPixelPoint(point.PixelX, point.PixelY));
        string? cropTransformId = panel.Transforms
            .SingleOrDefault(IsRasterCropTransform)?
            .TransformId.Value.ToString("D");
        SynchronizeProject(
            DomainEventKind.PointEdited,
            Guid.Parse(tab.PanelId!),
            point.PointId,
            "Manual point moved",
            JsonSerializer.SerializeToElement(new
            {
                kind = ProductionMoveAuditKind,
                correction_id = Guid.NewGuid().ToString("D"),
                target_point_id = point.PointId,
                target_detection_key = detectionKey,
                original_pixel_x = persistedPixel.X,
                original_pixel_y = persistedPixel.Y,
                coordinate_space = "original_pixels",
                crop_transform_id = cropTransformId,
            }));
    }

    public void DeletePoint(string tabId, string pointId)
    {
        WorkspaceTabViewModel tab = RequireTab(tabId);
        AppGraphPoint point = RequirePoint(tab, pointId);
        Dictionary<string, string?> identities = GetProductionDetectionKeys(tab.TabId);
        _ = identities.TryGetValue(point.PointId, out string? detectionKey);
        var tombstone = new DeletedPointTombstone(
            Guid.NewGuid().ToString("D"),
            point.PointId,
            detectionKey);
        GetDeletedPointTombstones(tab.TabId)[point.PointId] = tombstone;
        string sourceSeriesId = point.SeriesId;
        tab.Points.Remove(point);
        _pointXStates.Remove(point.PointId);
        _pointModificationHistories.Remove(point.PointId);
        identities.Remove(point.PointId);
        ReindexSeries(tab, sourceSeriesId);
        RequireSeries(tab, point.SeriesId).NotifyCountChanged();
        SynchronizeProject(
            DomainEventKind.PointEdited,
            Guid.Parse(tab.PanelId!),
            point.PointId,
            "Manual point deleted",
            JsonSerializer.SerializeToElement(new
            {
                kind = ProductionDeleteAuditKind,
                correction_id = tombstone.CorrectionId,
                target_point_id = tombstone.PointId,
                target_detection_key = tombstone.DetectionKey,
            }));
    }

    public void ReassignPoint(string tabId, string pointId, string targetSeriesId)
    {
        WorkspaceTabViewModel tab = RequireTab(tabId);
        AppGraphPoint point = RequirePoint(tab, pointId);
        SeriesCardViewModel target = RequireSeries(tab, targetSeriesId);
        SeriesCardViewModel source = RequireSeries(tab, point.SeriesId);
        if (string.Equals(source.SeriesId, target.SeriesId, StringComparison.Ordinal))
        {
            return;
        }

        DomainPixelPoint previousPixel = new(point.PixelX, point.PixelY);
        DomainGraphPoint? previousGraph = GetPreviousGraph(tab, point);
        point.SeriesId = target.SeriesId;
        UpdatePointCoordinates(tab, point);
        AppendPointModification(
            point,
            previousPixel,
            previousGraph,
            $"Manual point reassigned from series '{source.SeriesId}' to '{target.SeriesId}'");
        ReindexSeries(tab, source.SeriesId);
        ReindexSeries(tab, target.SeriesId);
        source.NotifyCountChanged();
        target.NotifyCountChanged();
        _ = GetProductionDetectionKeys(tab.TabId).TryGetValue(point.PointId, out string? detectionKey);
        SynchronizeProject(
            DomainEventKind.PointEdited,
            Guid.Parse(tab.PanelId!),
            point.PointId,
            "Manual point reassigned",
            JsonSerializer.SerializeToElement(new
            {
                kind = ProductionReassignAuditKind,
                correction_id = Guid.NewGuid().ToString("D"),
                target_point_id = point.PointId,
                target_detection_key = detectionKey,
                series_id = target.SeriesId,
            }));
    }

    public EditablePhaseDivider AddPhaseDivider(string tabId, double originalX, string code, string label)
    {
        WorkspaceTabViewModel tab = RequireTab(tabId);
        ValidatePhaseLabel(code, label);
        string dividerId = Guid.NewGuid().ToString("D");
        PhaseManualOverrides current = GetOverrides(tab);
        PhaseEditResult edited = _phaseEditor.Apply(
            current,
            new AddPhaseDividerCommand(Guid.NewGuid().ToString("D"), dividerId, originalX, PhaseDividerStyle.Dashed),
            PlotBounds(tab),
            CancellationToken.None);
        RequirePhaseSuccess(edited);
        _phaseOverrides[tab.TabId] = edited.Overrides;
        var divider = new EditablePhaseDivider(dividerId, originalX, code.Trim(), label.Trim());
        tab.PhaseDividers.Add(divider);
        SortDividers(tab);
        UpdateAllPointPhases(tab);
        SynchronizeProject(
            DomainEventKind.PhaseEdited,
            Guid.Parse(tab.PanelId!),
            dividerId,
            "Manual phase divider added",
            CreatePhaseCorrectionDetails("add", dividerId));
        return divider;
    }

    public void MovePhaseDivider(string tabId, string dividerId, double originalX)
    {
        WorkspaceTabViewModel tab = RequireTab(tabId);
        EditablePhaseDivider divider = RequireDivider(tab, dividerId);
        PhaseEditResult edited = _phaseEditor.Apply(
            GetOverrides(tab),
            new MovePhaseDividerCommand(
                Guid.NewGuid().ToString("D"),
                divider.DividerId,
                originalX,
                PhaseDividerStyle.Dashed,
                divider.OriginalX),
            PlotBounds(tab),
            CancellationToken.None);
        RequirePhaseSuccess(edited);
        _phaseOverrides[tab.TabId] = edited.Overrides;
        divider.OriginalX = originalX;
        SortDividers(tab);
        UpdateAllPointPhases(tab);
        SynchronizeProject(
            DomainEventKind.PhaseEdited,
            Guid.Parse(tab.PanelId!),
            dividerId,
            "Manual phase divider moved",
            CreatePhaseCorrectionDetails("move", dividerId));
    }

    public void DeletePhaseDivider(string tabId, string dividerId)
    {
        WorkspaceTabViewModel tab = RequireTab(tabId);
        EditablePhaseDivider divider = RequireDivider(tab, dividerId);
        PhaseEditResult edited = _phaseEditor.Apply(
            GetOverrides(tab),
            new DeletePhaseDividerCommand(Guid.NewGuid().ToString("D"), divider.DividerId, divider.OriginalX),
            PlotBounds(tab),
            CancellationToken.None);
        RequirePhaseSuccess(edited);
        _phaseOverrides[tab.TabId] = edited.Overrides;
        tab.PhaseDividers.Remove(divider);
        UpdateAllPointPhases(tab);
        SynchronizeProject(
            DomainEventKind.PhaseEdited,
            Guid.Parse(tab.PanelId!),
            dividerId,
            "Manual phase divider deleted",
            CreatePhaseCorrectionDetails("delete", dividerId));
    }

    public void LabelPhaseDivider(string tabId, string dividerId, string code, string label)
    {
        WorkspaceTabViewModel tab = RequireTab(tabId);
        EditablePhaseDivider divider = RequireDivider(tab, dividerId);
        ValidatePhaseLabel(code, label);
        double right = tab.PhaseDividers
            .Where(item => item.OriginalX > divider.OriginalX)
            .Select(item => item.OriginalX)
            .DefaultIfEmpty(tab.PixelWidth)
            .Min();
        PhaseEditResult edited = _phaseEditor.Apply(
            GetOverrides(tab),
            new RelabelPhaseCommand(
                Guid.NewGuid().ToString("D"),
                divider.DividerId,
                code.Trim(),
                MapPhaseType(code),
                label.Trim(),
                divider.OriginalX,
                right),
            PlotBounds(tab),
            CancellationToken.None);
        RequirePhaseSuccess(edited);
        _phaseOverrides[tab.TabId] = edited.Overrides;
        divider.Code = code.Trim();
        divider.Label = label.Trim();
        UpdateAllPointPhases(tab);
        SynchronizeProject(
            DomainEventKind.PhaseEdited,
            Guid.Parse(tab.PanelId!),
            dividerId,
            "Manual phase divider labeled",
            CreatePhaseCorrectionDetails("label", dividerId));
    }

    public async Task<DomainResult<ProjectSnapshotReceipt>> AutosaveAsync(
        SnapshotTrigger trigger,
        string? tabId,
        string? entityId,
        CancellationToken cancellationToken)
    {
        if (_applicationPaths is null)
        {
            return DomainResult<ProjectSnapshotReceipt>.Failure(new DomainError(
                "AUTOSAVE_PATHS_UNAVAILABLE",
                DomainErrorSeverity.Warning,
                "Errors.ApplicationPathsUnavailable",
                "Application paths were not supplied to the manual runtime.",
                Recoverable: true,
                "restart_application"));
        }

        SynchronizeProject(DomainEventKind.PointEdited, panelId: null, entityId, "Manual workspace autosave");
        var snapshots = new ProjectSnapshotService(_applicationPaths.AutosaveRoot, _projectFileStore);
        PanelId? panelId = tabId is null ? null : PanelId.FromGuid(Guid.Parse(RequireTab(tabId).PanelId!));
        DomainResult<ProjectSnapshotReceipt> result = await snapshots.SaveEventSnapshotAsync(
            CurrentProject,
            trigger,
            DateTimeOffset.UtcNow,
            panelId,
            entityId,
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && result.Value is not null)
        {
            CurrentProject = result.Value.Snapshot;
        }

        return result;
    }

    public async Task<DomainResult<ProjectSnapshotReceipt>> TimerAutosaveAsync(
        DateTimeOffset occurredUtc,
        CancellationToken cancellationToken)
    {
        if (_applicationPaths is null)
        {
            return DomainResult<ProjectSnapshotReceipt>.Failure(new DomainError(
                "AUTOSAVE_PATHS_UNAVAILABLE",
                DomainErrorSeverity.Warning,
                "Errors.ApplicationPathsUnavailable",
                "Application paths were not supplied to the manual runtime.",
                Recoverable: true,
                "restart_application"));
        }

        var scheduler = new FiveMinuteAutosaveScheduler();
        AutosaveSchedule schedule = scheduler.CreateSchedule(CurrentProject, CurrentProject.ModifiedUtc);
        var snapshots = new ProjectSnapshotService(_applicationPaths.AutosaveRoot, _projectFileStore, scheduler);
        DomainResult<ProjectSnapshotReceipt> result = await snapshots.SaveTimerSnapshotAsync(
            CurrentProject,
            schedule,
            occurredUtc,
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && result.Value is not null)
        {
            CurrentProject = result.Value.Snapshot;
        }

        return result;
    }

    public Task<DomainResult<RecoveryDiscoveryReport>> DiscoverRecoveryAsync(CancellationToken cancellationToken)
    {
        if (_applicationPaths is null)
        {
            return Task.FromResult(DomainResult<RecoveryDiscoveryReport>.Failure(new DomainError(
                "RECOVERY_PATHS_UNAVAILABLE",
                DomainErrorSeverity.Warning,
                "Errors.ApplicationPathsUnavailable",
                "Application paths were not supplied to the manual runtime.",
                Recoverable: true,
                "restart_application")));
        }

        var recovery = new ProjectRecoveryService(_projectFileStore);
        return CurrentProjectPath is null
            ? recovery.DiscoverAsync(_applicationPaths.AutosaveRoot, CurrentProject, null, cancellationToken)
            : recovery.DiscoverForProjectPathAsync(_applicationPaths.AutosaveRoot, CurrentProjectPath, cancellationToken);
    }

    public async Task<DomainResult<ProjectSaveReceipt>> RecoverLatestToNewFileAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (_applicationPaths is null)
        {
            return DomainResult<ProjectSaveReceipt>.Failure(new DomainError(
                "RECOVERY_PATHS_UNAVAILABLE",
                DomainErrorSeverity.Warning,
                "Errors.ApplicationPathsUnavailable",
                "Application paths were not supplied to the manual runtime.",
                Recoverable: true,
                "restart_application"));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        DomainResult<RecoveryDiscoveryReport> discovery = await DiscoverRecoveryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!discovery.IsSuccess || discovery.Value is null)
        {
            return DomainResult<ProjectSaveReceipt>.Failure(discovery.Errors);
        }

        RecoveryCandidate? candidate = discovery.Value.Candidates
            .FirstOrDefault(static item => item.Recommendation == RecoveryRecommendation.RestoreRecommended);
        if (candidate is null && discovery.Value.Candidates.Count > 0)
        {
            candidate = discovery.Value.Candidates[0];
        }
        if (candidate is null)
        {
            return DomainResult<ProjectSaveReceipt>.Failure(new DomainError(
                "RECOVERY_CANDIDATE_NOT_FOUND",
                DomainErrorSeverity.Warning,
                "Errors.RecoveryCandidateNotFound",
                "No autosave for the current project is available to recover.",
                Recoverable: true,
                "continue_editing"));
        }

        var recovery = new ProjectRecoveryService(_projectFileStore);
        return await recovery.RecoverToNewFileAsync(
            candidate.AutosavePath,
            destinationPath,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExportResult> ExportAsync(
        string tabId,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        WorkspaceTabViewModel tab = RequireTab(tabId);
        if (tab.Calibration is null)
        {
            throw new InvalidOperationException("Manual three-anchor calibration is required before export.");
        }

        SynchronizeProject(DomainEventKind.ExportSettingsChanged, Guid.Parse(tab.PanelId!), entityId: null, "Manual CSV export");
        PanelRecord panel = CurrentProject.Panels.Single(item => item.PanelId.Value == Guid.Parse(tab.PanelId!));
        SeriesRecord[] interventions = panel.Series
            .Where(static series => series.SemanticRole == SemanticRole.Intervention)
            .ToArray();
        if (interventions.Length == 0)
        {
            throw new InvalidOperationException("Create at least one intervention series before export.");
        }

        var request = new ExportRequest(
            Guid.NewGuid(),
            CurrentProject.ProjectId.Value,
            panel.PanelId.Value,
            outputDirectory,
            panel.Participant ?? Path.GetFileNameWithoutExtension(tab.DisplayName),
            ExportMode.ObservationOrder,
            ExportAuditMode.ExtendedCsvAndJson,
            ExportOperation.WriteFiles,
            new ExportCalibration(
                ExportCalibrationStatus.Valid,
                hasYCalibration: true,
                hasPrintedSessionCalibration: false,
                hasAbsoluteSessionOrigin: true,
                firstObservedSession: null,
                panel.Calibration!.Confidence),
            new ExportSessionOriginPolicy(
                RequireFirstObservedSessionOne: false,
                InvalidSessionOriginBehavior.Block),
            panel.Phases.Select(ToExportPhase),
            panel.Series.Select(ToExportSeries),
            panel.Points.Select(ToExportPoint),
            interventions.Select(series => new ExportSeriesRelation(
                series.SeriesId.Value,
                series.SharedBaselineSeriesId?.Value,
                series.ApplicableProbeSeriesIds.Select(static id => id.Value))),
            interventions.Select(static series => series.SeriesId.Value));
        return await _exportService.ExportAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public virtual Task RunStageAsync(WorkflowStage stage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AutomaticStageStatus? unavailable = stage switch
        {
            WorkflowStage.Prepare => AutomaticStages.First(status => status.Stage == "enhancement"),
            WorkflowStage.Detect => AutomaticStages.First(status => status.Stage == "markers"),
            _ => null,
        };

        return unavailable is null
            ? Task.CompletedTask
            : Task.FromException(new InvalidOperationException(unavailable.Explanation));
    }

    protected WorkflowImportRequest CreateProductionWorkflowImportRequest(bool enhancementEnabled)
    {
        WorkflowSourceRequest[] sources = CurrentProject.Sources
            .Where(static source => !string.IsNullOrWhiteSpace(source.LocalPath))
            .Select(source =>
            {
                var request = new WorkflowSourceRequest(
                    source.SourceId.Value,
                    source.Kind switch
                    {
                        SourceKind.Image => WorkflowSourceKind.Image,
                        SourceKind.Pdf => WorkflowSourceKind.Pdf,
                        _ => throw new InvalidOperationException(
                            $"Source kind '{source.Kind}' is not supported by the production workflow."),
                    },
                    source.LocalPath!);
                if (source.Kind != SourceKind.Image)
                {
                    return request;
                }

                PanelRecord[] panels = CurrentProject.Panels
                    .Where(panel => panel.SourceId == source.SourceId)
                    .ToArray();
                RetainedRasterPanelReference[] retained;
                if (panels.Length > 0 && panels.All(panel => panel.Transforms.Any(IsRasterCropTransform)))
                {
                    retained = panels.Select(panel => ReadRetainedRasterPanelReference(panel, source)).ToArray();
                }
                else if (panels.Length == 1 &&
                    panels[0].Transforms.All(static transform => !IsRasterCropTransform(transform)) &&
                    _tabs.SingleOrDefault(tab => string.Equals(
                        tab.PanelId,
                        panels[0].PanelId.Value.ToString("D"),
                        StringComparison.OrdinalIgnoreCase)) is { } legacyTab &&
                    panels[0].Crop == new CropRectangle(0, 0, legacyTab.PixelWidth, legacyTab.PixelHeight) &&
                    legacyTab.SourceSha256 is { Length: > 0 } legacyPanelSha256 &&
                    string.Equals(legacyPanelSha256, source.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    var fullCrop = new PdfRectD(0, 0, legacyTab.PixelWidth, legacyTab.PixelHeight);
                    retained =
                    [
                        new RetainedRasterPanelReference(
                            panels[0].PanelId.Value,
                            panels[0].DisplayName,
                            legacyPanelSha256,
                            legacyTab.PixelWidth,
                            legacyTab.PixelHeight,
                            fullCrop,
                            fullCrop),
                    ];
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Image source '{source.DisplayName}' has no complete retained panel identity for automatic detection.");
                }

                return request with { RetainedRaster = new RetainedRasterSourceRequest(source.Sha256, retained) };
            })
            .ToArray();
        if (sources.Length == 0)
        {
            throw new InvalidOperationException("Import a real image or PDF before running automatic detection.");
        }

        return new WorkflowImportRequest(CurrentProject.ProjectId.Value, sources, enhancementEnabled);
    }

    private sealed record ProductionProjectionPlan(
        WorkflowReviewPanel ReviewPanel,
        WorkspaceTabViewModel Tab,
        ProductionPanelProjectionEvidence Projection,
        ProductionPanelExportEvidence ExportEvidence,
        ManualCalibrationState Calibration,
        EditablePhaseDivider[] Dividers,
        Dictionary<Guid, ProductionPointExportEvidence> PointEvidence,
        Dictionary<Guid, PhaseRecord> Phases,
        bool WasBlank,
        bool PreserveManualPhases,
        WorkflowCorrection[] Corrections,
        WorkflowReviewPanel CorrectedReviewPanel);

    private sealed record ProductionCorrectionReplay(
        WorkflowReviewPanel ReviewPanel,
        ProductionPanelExportEvidence ExportEvidence,
        ProductionPanelProjectionEvidence Projection);

    protected ProductionReviewProjectionResult ProjectProductionReview(
        WorkflowRunResult result,
        ProductionWorkflowPanelStore panelStore)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(panelStore);
        if (result.Review.ProjectId != CurrentProject.ProjectId.Value)
        {
            return RejectProductionProjection(
                $"Automatic review project '{result.Review.ProjectId:D}' does not match the open project '{CurrentProject.ProjectId.Value:D}'.");
        }

        if (result.Review.Panels.Count == 0)
        {
            return RejectProductionProjection("Automatic review returned no panels to project.");
        }

        var plans = new List<ProductionProjectionPlan>(result.Review.Panels.Count);
        var plannedTabIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (WorkflowReviewPanel incomingReviewPanel in result.Review.Panels)
        {
            WorkflowReviewPanel reviewPanel = incomingReviewPanel;
            WorkflowImportedPanel imported = reviewPanel.PreparedPanel.ImportedPanel;
            WorkspaceTabViewModel[] matchingTabs = _tabs.Where(candidate =>
                    string.Equals(candidate.PanelId, imported.PanelId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.SourceId, imported.SourceId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.SourceSha256, imported.Original.Sha256, StringComparison.OrdinalIgnoreCase) &&
                    candidate.PixelWidth == imported.Original.Width &&
                    candidate.PixelHeight == imported.Original.Height)
                .ToArray();
            if (matchingTabs.Length != 1)
            {
                return RejectProductionProjection(
                    $"Review panel '{reviewPanel.PanelId:D}' matched {matchingTabs.Length} open workspace tabs by source, checksum, and dimensions.");
            }

            WorkspaceTabViewModel tab = matchingTabs[0];
            if (!plannedTabIds.Add(tab.TabId))
            {
                return RejectProductionProjection(
                    $"More than one review panel targets workspace tab '{tab.TabId}'.");
            }

            if (!panelStore.TryGet(reviewPanel.PanelId, out ProductionPanelEvidence? retained) ||
                retained?.ExportEvidence is not { ProjectionEvidence: { } retainedProjection } retainedExportEvidence)
            {
                return RejectProductionProjection(
                    $"Review panel '{reviewPanel.PanelId:D}' has no retained exact production projection evidence.");
            }

            if (result.Review.CorrectionJournal.Count > 0)
            {
                WorkflowReviewPanel? exactReview = CreateExactValidationReviewPanel(
                    tab,
                    reviewPanel,
                    retainedProjection);
                if (exactReview is null)
                {
                    return RejectProductionProjection(
                        $"Review panel '{reviewPanel.PanelId:D}' could not be restored to exact automatic evidence before correction replay.");
                }

                reviewPanel = exactReview;
            }

            ProductionCorrectionReplay replay = ApplyDeletedPointTombstones(
                tab,
                reviewPanel,
                retainedExportEvidence,
                retainedProjection);
            reviewPanel = replay.ReviewPanel;
            ProductionPanelExportEvidence exportEvidence = replay.ExportEvidence;
            ProductionPanelProjectionEvidence projection = replay.Projection;

            if (projection.Calibration.Status != DomainCalibrationStatus.Valid ||
                projection.Phases.Count == 0 ||
                projection.Series.Count == 0)
            {
                return RejectProductionProjection(
                    $"Review panel '{reviewPanel.PanelId:D}' has incomplete calibration, phase, or series evidence.");
            }

            PanelRecord[] projectPanels = CurrentProject.Panels.Where(panel =>
                    string.Equals(panel.PanelId.Value.ToString("D"), tab.PanelId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (projectPanels.Length != 1)
            {
                return RejectProductionProjection(
                    $"Workspace tab '{tab.TabId}' matched {projectPanels.Length} project panels.");
            }

            ManualCalibrationState? calibration = FromDomainCalibration(projection.Calibration);
            if (calibration is null ||
                (tab.Calibration is not null && !CalibrationMatches(tab.Calibration, calibration)) ||
                !TryValidateProjection(reviewPanel, exportEvidence, projection) ||
                !ExistingSeriesAreCompatible(tab.SeriesCards, projection.Series))
            {
                return RejectProductionProjection(
                    $"Review panel '{reviewPanel.PanelId:D}' failed exact calibration, point, or series validation.");
            }

            EditablePhaseDivider?[] projectedDividerCandidates = projection.Phases
                .Where(static phase => phase.Order > 1)
                .Select(phase => phase.BoundaryLeftId is { } boundary
                    ? new EditablePhaseDivider(
                        boundary.Value.ToString("D"),
                        phase.ScreenXMin,
                        phase.Code,
                        phase.LabelText ?? phase.Code)
                    : null)
                .ToArray();
            if (projectedDividerCandidates.Any(static divider => divider is null))
            {
                return RejectProductionProjection(
                    $"Review panel '{reviewPanel.PanelId:D}' has a phase boundary without an exact divider identity.");
            }
            EditablePhaseDivider[] projectedDividers = projectedDividerCandidates
                .Select(static divider => divider!)
                .ToArray();
            string? phaseCorrectionId = GetLatestPhaseCorrectionId(tab);
            bool preserveManualPhases = phaseCorrectionId is not null;
            if (projectedDividers.Select(static divider => divider.DividerId).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    projectedDividers.Length ||
                (!preserveManualPhases &&
                    tab.PhaseDividers.Count > 0 &&
                    !PhaseDividersMatch(tab.PhaseDividers, projectedDividers)))
            {
                return RejectProductionProjection(
                    $"Review panel '{reviewPanel.PanelId:D}' failed exact phase-divider validation.");
            }

            if (tab.Points.Select(static point => point.PointId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tab.Points.Count)
            {
                return RejectProductionProjection(
                    $"Workspace tab '{tab.TabId}' contains duplicate point identities.");
            }

            WorkflowCorrection[] corrections = result.Review.CorrectionJournal
                .Where(correction => correction.PanelId == reviewPanel.PanelId)
                .Concat(BuildPersistedWorkflowCorrections(tab, reviewPanel, phaseCorrectionId))
                .GroupBy(static correction => correction.CorrectionId, StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToArray();
            WorkflowReviewPanel correctedReviewPanel = ManualCorrectionOverlay.Reapply(
                reviewPanel,
                previousReview: null,
                corrections);
            HashSet<string> correctedPointIds = correctedReviewPanel.Points
                .Select(static point => point.PointId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (tab.Points.Any(point => !correctedPointIds.Contains(point.PointId)))
            {
                return RejectProductionProjection(
                    $"Review panel '{reviewPanel.PanelId:D}' cannot represent every persisted manual point correction.");
            }

            plans.Add(new ProductionProjectionPlan(
                reviewPanel,
                tab,
                projection,
                exportEvidence,
                calibration,
                projectedDividers,
                exportEvidence.Points.ToDictionary(static point => point.PointId),
                projection.Phases.ToDictionary(static phase => phase.PhaseId.Value),
                projectPanels[0].Series.Count == 0 &&
                    projectPanels[0].Points.Count == 0 &&
                    projectPanels[0].Calibration is null,
                preserveManualPhases,
                corrections,
                correctedReviewPanel));
        }

        if (plans.Count != result.Review.Panels.Count)
        {
            return RejectProductionProjection(
                $"Only {plans.Count} of {result.Review.Panels.Count} review panels passed exact projection validation.");
        }

        int projected = 0;
        foreach (ProductionProjectionPlan plan in plans)
        {
            WorkflowReviewPanel reviewPanel = plan.ReviewPanel;
            WorkspaceTabViewModel tab = plan.Tab;
            ProductionPanelProjectionEvidence projection = plan.Projection;

            tab.Calibration = plan.Calibration;
            if (!plan.PreserveManualPhases && tab.PhaseDividers.Count == 0)
            {
                foreach (EditablePhaseDivider divider in plan.Dividers)
                {
                    tab.PhaseDividers.Add(divider);
                }
                _phaseOverrides[tab.TabId] = CreatePhaseOverrides(tab);
            }

            foreach (SeriesRecord series in projection.Series)
            {
                if (tab.SeriesCards.Any(candidate => string.Equals(
                        candidate.SeriesId,
                        series.SeriesId.Value.ToString("D"),
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                tab.SeriesCards.Add(new SeriesCardViewModel(
                    series.SeriesId.Value.ToString("D"),
                    series.Symbol,
                    $"{series.Fill} {series.Shape}",
                    series.DisplayName,
                    series.Confidence,
                    tab.Points,
                    series.Shape,
                    series.Fill,
                    series.SemanticRole));
            }

            foreach (WorkflowPoint workflowPoint in reviewPanel.Points)
            {
                Guid pointId = Guid.Parse(workflowPoint.PointId);
                Guid seriesId = Guid.Parse(workflowPoint.SeriesId!);
                Guid phaseId = Guid.Parse(workflowPoint.PhaseId!);
                ProductionPointExportEvidence metadata = plan.PointEvidence[pointId];
                PhaseRecord phase = plan.Phases[phaseId];

                AppGraphPoint? existing = tab.Points.SingleOrDefault(point =>
                    string.Equals(point.PointId, workflowPoint.PointId, StringComparison.OrdinalIgnoreCase));
                if (existing is not null &&
                    _pointModificationHistories.TryGetValue(existing.PointId, out List<PointModification>? history) &&
                    history.Count > 0)
                {
                    continue;
                }

                if (existing is null)
                {
                    existing = new AppGraphPoint(
                        workflowPoint.PointId,
                        seriesId.ToString("D"),
                        workflowPoint.OriginalPixelX,
                        workflowPoint.OriginalPixelY,
                        workflowPoint.GraphX.GetValueOrDefault(),
                        workflowPoint.GraphY.GetValueOrDefault(),
                        phase.Code,
                        phaseId.ToString("D"),
                        metadata.ObservationIndex);
                    tab.Points.Add(existing);
                    _pointModificationHistories[existing.PointId] = [];
                }
                else
                {
                    existing.SeriesId = seriesId.ToString("D");
                    existing.PixelX = workflowPoint.OriginalPixelX;
                    existing.PixelY = workflowPoint.OriginalPixelY;
                    existing.GraphX = workflowPoint.GraphX.GetValueOrDefault();
                    existing.GraphY = workflowPoint.GraphY.GetValueOrDefault();
                    existing.PhaseCode = phase.Code;
                    existing.PhaseId = phaseId.ToString("D");
                    existing.ObservationIndex = metadata.ObservationIndex;
                }

                _pointXStates[existing.PointId] = new ManualPointXState(
                    metadata.PrintedXValue,
                    metadata.EstimatedXValue,
                    metadata.XSource switch
                    {
                        ExportXValueSource.Printed => PointXSource.Printed,
                        ExportXValueSource.Estimated => PointXSource.Estimated,
                        ExportXValueSource.ObservationOrder => PointXSource.ObservationOrder,
                        _ => PointXSource.Unknown,
                    },
                    metadata.XConfidence,
                    workflowPoint.GraphX.HasValue);
                projected++;
            }

            foreach (SeriesCardViewModel series in tab.SeriesCards)
            {
                series.NotifyCountChanged();
            }

            Dictionary<string, string?> identities = GetProductionDetectionKeys(tab.TabId);
            identities.Clear();
            foreach (WorkflowPoint point in reviewPanel.Points)
            {
                identities[point.PointId] = point.DetectionKey;
            }
        }

        var exactProjectionByTab = plans.ToDictionary(
            static plan => plan.Tab.TabId,
            static plan => (Evidence: plan.Projection, WasBlank: plan.WasBlank),
            StringComparer.Ordinal);
        SynchronizeProject(
            DomainEventKind.DetectionAccepted,
            panelId: null,
            entityId: result.RunId.ToString("D"),
            "Approved production workflow results projected by source and checksum identity",
            CreateProductionProjectionAuditDetails(result, plans));
        CurrentProject = CurrentProject with
        {
            Panels = CurrentProject.Panels.Select(panel =>
            {
                string tabId = panel.PanelId.Value.ToString("D");
                if (!exactProjectionByTab.TryGetValue(
                        tabId,
                        out (ProductionPanelProjectionEvidence Evidence, bool WasBlank) state))
                {
                    return panel;
                }

                ProductionPanelProjectionEvidence exact = state.Evidence;
                ProductionPanelProjectionEvidence persisted = MapProjectionPanelToSource(panel, exact);
                PanelRecord updated = panel with
                {
                    Participant = persisted.Participant,
                    Transforms = MergeProjectionTransforms(panel, exact.Transforms),
                    Calibration = persisted.Calibration,
                    OcrRegions = persisted.OcrRegions,
                    Markers = persisted.Markers,
                    Points = MergeProjectedPoints(panel.Points, persisted.Points),
                };
                return state.WasBlank
                    ? updated with { Phases = persisted.Phases, Series = persisted.Series }
                    : updated;
            }).ToArray(),
        };

        WorkflowCorrection[] allCorrections = plans.SelectMany(static plan => plan.Corrections)
            .GroupBy(static correction => correction.CorrectionId, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
        var correctedPanels = new List<WorkflowReviewPanel>(plans.Count);
        foreach (ProductionProjectionPlan plan in plans)
        {
            PanelRecord currentPanel = CurrentProject.Panels.Single(panel => string.Equals(
                panel.PanelId.Value.ToString("D"),
                plan.Tab.PanelId,
                StringComparison.OrdinalIgnoreCase));
            WorkflowReviewPanel aligned = AlignReviewPanelToProject(plan.CorrectedReviewPanel, currentPanel);
            correctedPanels.Add(aligned);
            panelStore.SetExportEvidence(
                plan.ReviewPanel.PanelId,
                AlignExportEvidenceToProject(plan.ExportEvidence, currentPanel));
        }

        var correctedReview = new WorkflowReviewState(
            result.Review.ProjectId,
            correctedPanels,
            allCorrections,
            result.Review.Warnings);
        var correctedRun = new WorkflowRunResult(result.RunId, correctedReview, result.Steps);
        return ProductionReviewProjectionResult.Success(plans.Count, projected, correctedRun);
    }

    private WorkflowReviewPanel? CreateExactValidationReviewPanel(
        WorkspaceTabViewModel tab,
        WorkflowReviewPanel incoming,
        ProductionPanelProjectionEvidence projection)
    {
        Dictionary<Guid, WorkflowPoint> incomingById = incoming.Points
            .Where(point => Guid.TryParse(point.PointId, out _))
            .ToDictionary(point => Guid.Parse(point.PointId));
        Dictionary<Guid, SeriesRecord> seriesById = projection.Series.ToDictionary(static series => series.SeriesId.Value);
        Dictionary<string, string?> identities = GetProductionDetectionKeys(tab.TabId);
        Dictionary<string, DeletedPointTombstone> tombstones = GetDeletedPointTombstones(tab.TabId);
        var exactPoints = new List<WorkflowPoint>(projection.Points.Count);
        foreach (PointRecord point in projection.Points)
        {
            if (point.SeriesId is not { } seriesId ||
                point.PhaseId is not { } phaseId ||
                !seriesById.TryGetValue(seriesId.Value, out SeriesRecord? series))
            {
                return null;
            }

            _ = incomingById.TryGetValue(point.PointId.Value, out WorkflowPoint? template);
            string pointId = point.PointId.Value.ToString("D");
            _ = identities.TryGetValue(pointId, out string? detectionKey);
            if (detectionKey is null && tombstones.TryGetValue(pointId, out DeletedPointTombstone? tombstone))
            {
                detectionKey = tombstone.DetectionKey;
            }
            detectionKey ??= template?.DetectionKey;
            if (string.IsNullOrWhiteSpace(detectionKey))
            {
                return null;
            }

            exactPoints.Add(new WorkflowPoint(
                pointId,
                detectionKey,
                point.OriginalPixel.X,
                point.OriginalPixel.Y,
                point.PointConfidence,
                template?.SourceImage ?? WorkflowImageVariant.Original,
                WorkflowReviewStatus.Accepted,
                series.Symbol,
                series.Shape.ToString(),
                series.Fill.ToString(),
                seriesId.Value.ToString("D"),
                phaseId.Value.ToString("D"),
                point.GraphX,
                point.GraphY,
                point.SourceStage,
                point.ModelVersion,
                isManual: false));
        }

        return new WorkflowReviewPanel(incoming.PreparedPanel, exactPoints, incoming.DetectionProvenance);
    }

    private WorkflowCorrection[] BuildPersistedWorkflowCorrections(
        WorkspaceTabViewModel tab,
        WorkflowReviewPanel exactReviewPanel,
        string? phaseCorrectionId)
    {
        Guid workspacePanelId = Guid.Parse(tab.PanelId!);
        PanelRecord currentPanel = CurrentProject.Panels.Single(panel => panel.PanelId.Value == workspacePanelId);
        var corrections = new List<WorkflowCorrection>();
        foreach (AuditEvent auditEvent in CurrentProject.Audit.Events
                     .Where(auditEvent => auditEvent.PanelId?.Value == workspacePanelId)
                     .OrderBy(static auditEvent => auditEvent.OccurredUtc))
        {
            if (auditEvent.Details is not JsonElement details ||
                !TryReadString(details, "kind", out string? kind) ||
                !TryReadString(details, "correction_id", out string? correctionId))
            {
                continue;
            }

            switch (kind)
            {
                case ProductionAddAuditKind when TryReadString(details, "point_id", out string? addedPointId):
                    PointRecord? addedPoint = currentPanel.Points.SingleOrDefault(point => string.Equals(
                        point.PointId.Value.ToString("D"), addedPointId, StringComparison.OrdinalIgnoreCase));
                    if (addedPoint is not null && TryCreateManualWorkflowPoint(currentPanel, addedPoint, out WorkflowPoint? workflowPoint))
                    {
                        corrections.Add(new AddWorkflowPointCorrection(
                            correctionId!, exactReviewPanel.PanelId, workflowPoint!));
                    }
                    break;
                case ProductionMoveAuditKind
                    when TryReadString(details, "target_point_id", out string? movedPointId) &&
                        TryReadDouble(details, "original_pixel_x", out double movedX) &&
                        TryReadDouble(details, "original_pixel_y", out double movedY):
                    DomainPixelPoint replayPixel = MapPersistedAuditPointToPanel(currentPanel, details, movedX, movedY);
                    corrections.Add(new MoveWorkflowPointCorrection(
                        correctionId!,
                        exactReviewPanel.PanelId,
                        movedPointId!,
                        TryReadNullableString(details, "target_detection_key"),
                        replayPixel.X,
                        replayPixel.Y));
                    break;
                case ProductionDeleteAuditKind when TryReadString(details, "target_point_id", out string? deletedPointId):
                    corrections.Add(new DeleteWorkflowPointCorrection(
                        correctionId!,
                        exactReviewPanel.PanelId,
                        deletedPointId!,
                        TryReadNullableString(details, "target_detection_key")));
                    break;
                case ProductionReassignAuditKind
                    when TryReadString(details, "target_point_id", out string? reassignedPointId) &&
                        TryReadString(details, "series_id", out string? seriesId):
                    corrections.Add(new ReassignWorkflowPointCorrection(
                        correctionId!,
                        exactReviewPanel.PanelId,
                        reassignedPointId!,
                        TryReadNullableString(details, "target_detection_key"),
                        seriesId!));
                    break;
            }
        }

        if (phaseCorrectionId is null)
        {
            return corrections.ToArray();
        }

        WorkflowReviewPanel pointCorrected = ManualCorrectionOverlay.Reapply(
            exactReviewPanel,
            previousReview: null,
            corrections);
        foreach (WorkflowPoint point in pointCorrected.Points)
        {
            string? desiredPhaseId = ResolveManualPhaseId(tab, point);
            if (desiredPhaseId is null || string.Equals(desiredPhaseId, point.PhaseId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            corrections.Add(new AssignWorkflowPointPhaseCorrection(
                $"{phaseCorrectionId}:{point.PointId}",
                exactReviewPanel.PanelId,
                point.PointId,
                point.DetectionKey,
                desiredPhaseId));
        }

        return corrections.ToArray();
    }

    private static bool TryCreateManualWorkflowPoint(
        PanelRecord panel,
        PointRecord point,
        out WorkflowPoint? workflowPoint)
    {
        workflowPoint = null;
        if (point.SeriesId is not { } seriesId ||
            point.PhaseId is not { } phaseId ||
            panel.Series.SingleOrDefault(series => series.SeriesId == seriesId) is not { } series)
        {
            return false;
        }

        DomainPixelPoint workspacePixel = MapPointSourceToPanel(panel, point.OriginalPixel);

        workflowPoint = new WorkflowPoint(
            point.PointId.Value.ToString("D"),
            detectionKey: null,
            workspacePixel.X,
            workspacePixel.Y,
            point.PointConfidence,
            WorkflowImageVariant.Original,
            WorkflowReviewStatus.Corrected,
            series.Symbol,
            series.Shape.ToString(),
            series.Fill.ToString(),
            seriesId.Value.ToString("D"),
            phaseId.Value.ToString("D"),
            point.GraphX,
            point.GraphY,
            point.SourceStage,
            point.ModelVersion,
            isManual: true);
        return true;
    }

    private static string? ResolveManualPhaseId(WorkspaceTabViewModel tab, WorkflowPoint point)
    {
        SeriesCardViewModel? series = tab.SeriesCards.SingleOrDefault(candidate => string.Equals(
            candidate.SeriesId, point.SeriesId, StringComparison.OrdinalIgnoreCase));
        if (series is null)
        {
            return null;
        }

        if (series.SemanticRole is SemanticRole.Maintenance or SemanticRole.Generalization)
        {
            return series.SeriesId;
        }

        return tab.PhaseDividers
            .Where(divider => divider.OriginalX <= point.OriginalPixelX)
            .OrderByDescending(static divider => divider.OriginalX)
            .Select(static divider => divider.DividerId)
            .FirstOrDefault() ?? tab.PanelId;
    }

    private static WorkflowReviewPanel AlignReviewPanelToProject(
        WorkflowReviewPanel corrected,
        PanelRecord panel)
    {
        Dictionary<string, WorkflowPoint> correctedById = corrected.Points.ToDictionary(
            static point => point.PointId,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<SeriesId, SeriesRecord> seriesById = panel.Series.ToDictionary(static series => series.SeriesId);
        var aligned = new List<WorkflowPoint>(panel.Points.Count);
        foreach (PointRecord point in panel.Points)
        {
            string pointId = point.PointId.Value.ToString("D");
            if (!correctedById.TryGetValue(pointId, out WorkflowPoint? template) ||
                point.SeriesId is not { } seriesId ||
                point.PhaseId is not { } phaseId ||
                !seriesById.TryGetValue(seriesId, out SeriesRecord? series))
            {
                throw new InvalidOperationException(
                    $"Corrected workflow review is missing persisted project point '{pointId}'.");
            }

            DomainPixelPoint workspacePixel = MapPointSourceToPanel(panel, point.OriginalPixel);
            aligned.Add(template with
            {
                OriginalPixelX = workspacePixel.X,
                OriginalPixelY = workspacePixel.Y,
                GraphX = point.GraphX,
                GraphY = point.GraphY,
                SeriesId = seriesId.Value.ToString("D"),
                PhaseId = phaseId.Value.ToString("D"),
                Symbol = series.Symbol,
                Shape = series.Shape.ToString(),
                Fill = series.Fill.ToString(),
            });
        }

        return new WorkflowReviewPanel(corrected.PreparedPanel, aligned, corrected.DetectionProvenance);
    }

    private static ProductionPanelExportEvidence AlignExportEvidenceToProject(
        ProductionPanelExportEvidence source,
        PanelRecord panel)
    {
        PanelRecord workspacePanel = MapPanelRecordSourceToPanel(panel);
        var projection = new ProductionPanelProjectionEvidence(
            workspacePanel.Calibration ?? throw new InvalidOperationException("Corrected production panel lost calibration."),
            workspacePanel.Phases,
            workspacePanel.Series,
            workspacePanel.Points,
            workspacePanel.Transforms,
            workspacePanel.OcrRegions,
            workspacePanel.Markers,
            workspacePanel.Participant);
        return new ProductionPanelExportEvidence(
            source.Calibration,
            workspacePanel.Phases.Select(ToExportPhase),
            workspacePanel.Series.Select(ToExportSeries),
            workspacePanel.Series.Select(series => new ExportSeriesRelation(
                series.SeriesId.Value,
                series.SharedBaselineSeriesId?.Value,
                series.ApplicableProbeSeriesIds.Select(static id => id.Value))),
            workspacePanel.Points.Select(point => new ProductionPointExportEvidence(
                point.PointId.Value,
                point.MarkerId?.Value,
                point.ObservationIndex,
                point.PrintedXValue,
                point.EstimatedXValue,
                point.XSource switch
                {
                    PointXSource.Printed => ExportXValueSource.Printed,
                    PointXSource.Estimated => ExportXValueSource.Estimated,
                    PointXSource.ObservationOrder => ExportXValueSource.ObservationOrder,
                    _ => ExportXValueSource.Unknown,
                },
                point.XConfidence,
                point.YConfidence)),
            source.Provenance,
            workspacePanel.Participant,
            source.Mode,
            source.AuditMode,
            source.SessionOriginPolicy,
            projection);
    }

    private ProductionCorrectionReplay ApplyDeletedPointTombstones(
        WorkspaceTabViewModel tab,
        WorkflowReviewPanel reviewPanel,
        ProductionPanelExportEvidence exportEvidence,
        ProductionPanelProjectionEvidence projection)
    {
        DeletedPointTombstone[] tombstones = GetDeletedPointTombstones(tab.TabId).Values
            .OrderBy(static tombstone => tombstone.CorrectionId, StringComparer.Ordinal)
            .ToArray();
        if (tombstones.Length == 0)
        {
            return new ProductionCorrectionReplay(reviewPanel, exportEvidence, projection);
        }

        WorkflowCorrection[] corrections = tombstones.Select(tombstone =>
            (WorkflowCorrection)new DeleteWorkflowPointCorrection(
                tombstone.CorrectionId,
                reviewPanel.PanelId,
                tombstone.PointId,
                tombstone.DetectionKey)).ToArray();
        WorkflowReviewPanel correctedPanel = ManualCorrectionOverlay.Reapply(
            reviewPanel,
            previousReview: null,
            corrections);
        HashSet<Guid> retainedPointIds = correctedPanel.Points
            .Select(point => Guid.TryParse(point.PointId, out Guid pointId) ? pointId : Guid.Empty)
            .Where(static pointId => pointId != Guid.Empty)
            .ToHashSet();
        SeriesRecord[] retainedSeries = projection.Series.Select(series => series with
        {
            PointIds = series.PointIds.Where(pointId => retainedPointIds.Contains(pointId.Value)).ToArray(),
        }).ToArray();
        var correctedProjection = new ProductionPanelProjectionEvidence(
            projection.Calibration,
            projection.Phases,
            retainedSeries,
            projection.Points.Where(point => retainedPointIds.Contains(point.PointId.Value)),
            projection.Transforms,
            projection.OcrRegions,
            projection.Markers,
            projection.Participant);
        var correctedExport = new ProductionPanelExportEvidence(
            exportEvidence.Calibration,
            exportEvidence.Phases,
            exportEvidence.Series.Select(series => new ExportSeries(
                series.SeriesId,
                series.Symbol,
                series.DisplayName,
                series.SemanticRole,
                series.PointIds.Where(retainedPointIds.Contains),
                series.Confidence,
                series.LegendText)),
            exportEvidence.Relations,
            exportEvidence.Points.Where(point => retainedPointIds.Contains(point.PointId)),
            exportEvidence.Provenance,
            exportEvidence.Participant,
            exportEvidence.Mode,
            exportEvidence.AuditMode,
            exportEvidence.SessionOriginPolicy,
            correctedProjection);
        return new ProductionCorrectionReplay(correctedPanel, correctedExport, correctedProjection);
    }

    private Dictionary<string, string?> GetProductionDetectionKeys(string tabId)
    {
        if (!_productionDetectionKeysByTab.TryGetValue(tabId, out Dictionary<string, string?>? identities))
        {
            identities = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            _productionDetectionKeysByTab[tabId] = identities;
        }

        return identities;
    }

    private Dictionary<string, DeletedPointTombstone> GetDeletedPointTombstones(string tabId)
    {
        if (!_deletedPointTombstonesByTab.TryGetValue(
                tabId,
                out Dictionary<string, DeletedPointTombstone>? tombstones))
        {
            tombstones = new Dictionary<string, DeletedPointTombstone>(StringComparer.OrdinalIgnoreCase);
            _deletedPointTombstonesByTab[tabId] = tombstones;
        }

        return tombstones;
    }

    private string? GetLatestPhaseCorrectionId(WorkspaceTabViewModel tab)
    {
        Guid panelId = Guid.Parse(tab.PanelId!);
        return CurrentProject.Audit.Events
            .Where(auditEvent => auditEvent.PanelId?.Value == panelId)
            .OrderByDescending(static auditEvent => auditEvent.OccurredUtc)
            .Select(static auditEvent => auditEvent.Details)
            .Where(static details => details is not null)
            .Select(static details => details!.Value)
            .Where(details => TryReadString(details, "kind", out string? kind) &&
                string.Equals(kind, ProductionPhaseAuditKind, StringComparison.Ordinal))
            .Select(details => TryReadString(details, "correction_id", out string? correctionId)
                ? correctionId
                : null)
            .FirstOrDefault(static correctionId => correctionId is not null);
    }

    private static JsonElement CreatePhaseCorrectionDetails(string action, string dividerId) =>
        JsonSerializer.SerializeToElement(new
        {
            kind = ProductionPhaseAuditKind,
            correction_id = Guid.NewGuid().ToString("D"),
            action,
            divider_id = dividerId,
        });

    private static JsonElement CreateProductionProjectionAuditDetails(
        WorkflowRunResult result,
        IReadOnlyList<ProductionProjectionPlan> plans) =>
        JsonSerializer.SerializeToElement(new
        {
            kind = ProductionProjectionAuditKind,
            run_id = result.RunId.ToString("D"),
            point_identities = plans.SelectMany(plan => plan.ReviewPanel.Points.Select(point => new
            {
                panel_id = plan.Tab.PanelId,
                point_id = point.PointId,
                detection_key = point.DetectionKey,
            })).ToArray(),
            vision_provenance = plans
                .SelectMany(static plan => plan.ReviewPanel.DetectionProvenance)
                .OrderBy(static envelope => envelope.PanelId)
                .ThenBy(static envelope => envelope.Stage, StringComparer.Ordinal)
                .ThenBy(static envelope => envelope.Model?.ModelId, StringComparer.Ordinal)
                .Select(static envelope => new
                {
                    contract_version = envelope.ContractVersion,
                    run_id = envelope.RunId.ToString("D"),
                    project_id = envelope.ProjectId.ToString("D"),
                    panel_id = envelope.PanelId.ToString("D"),
                    stage = envelope.Stage,
                    stage_version = envelope.StageVersion,
                    input_sha256 = envelope.InputSha256,
                    coordinate_space = envelope.CoordinateSpace,
                    model = envelope.Model is null
                        ? null
                        : new
                        {
                            model_id = envelope.Model.ModelId,
                            version = envelope.Model.Version,
                            sha256 = envelope.Model.Sha256,
                            provider = envelope.Model.Provider,
                        },
                    timing = new
                    {
                        preprocess_milliseconds = envelope.Timing.PreprocessMilliseconds,
                        inference_milliseconds = envelope.Timing.InferenceMilliseconds,
                        postprocess_milliseconds = envelope.Timing.PostprocessMilliseconds,
                        total_milliseconds = envelope.Timing.TotalMilliseconds,
                    },
                    confidence = envelope.Confidence,
                    warnings = envelope.Warnings.ToArray(),
                    transforms = envelope.Transforms.Select(static transform => new
                    {
                        transform_id = transform.TransformId,
                        input_coordinate_space = transform.InputCoordinateSpace,
                        output_coordinate_space = transform.OutputCoordinateSpace,
                        input_to_output_matrix = transform.InputToOutputMatrix.ToArray(),
                        output_to_input_matrix = transform.OutputToInputMatrix?.ToArray(),
                        lossy = transform.Lossy,
                    }).ToArray(),
                }).ToArray(),
            workflow_steps = result.Steps.Select(static step => new
            {
                step = step.Step.ToString(),
                elapsed_milliseconds = step.Elapsed.TotalMilliseconds,
                item_count = step.ItemCount,
            }).ToArray(),
            workflow_warnings = result.Review.Warnings.ToArray(),
        });

    private static void RestoreProductionCorrectionState(
        ProjectDocument project,
        IReadOnlyList<WorkspaceTabViewModel> tabs,
        Dictionary<string, Dictionary<string, string?>> detectionKeysByTab,
        Dictionary<string, Dictionary<string, DeletedPointTombstone>> tombstonesByTab)
    {
        foreach (AuditEvent auditEvent in project.Audit.Events.OrderBy(static item => item.OccurredUtc))
        {
            if (auditEvent.Details is not JsonElement details ||
                details.ValueKind != JsonValueKind.Object ||
                !details.TryGetProperty("kind", out JsonElement kindElement) ||
                kindElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? kind = kindElement.GetString();
            if (string.Equals(kind, ProductionProjectionAuditKind, StringComparison.Ordinal) &&
                details.TryGetProperty("point_identities", out JsonElement identitiesElement) &&
                identitiesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement identity in identitiesElement.EnumerateArray())
                {
                    if (!TryReadString(identity, "panel_id", out string? panelId) ||
                        !TryReadString(identity, "point_id", out string? pointId))
                    {
                        continue;
                    }

                    WorkspaceTabViewModel? tab = tabs.SingleOrDefault(candidate => string.Equals(
                        candidate.PanelId,
                        panelId,
                        StringComparison.OrdinalIgnoreCase));
                    if (tab is null)
                    {
                        continue;
                    }

                    string? detectionKey = TryReadNullableString(identity, "detection_key");
                    detectionKeysByTab[tab.TabId][pointId!] = detectionKey;
                }
            }
            else if (string.Equals(kind, ProductionDeleteAuditKind, StringComparison.Ordinal) &&
                auditEvent.PanelId is { } deletedPanelId &&
                TryReadString(details, "correction_id", out string? correctionId) &&
                TryReadString(details, "target_point_id", out string? targetPointId))
            {
                WorkspaceTabViewModel? tab = tabs.SingleOrDefault(candidate => string.Equals(
                    candidate.PanelId,
                    deletedPanelId.Value.ToString("D"),
                    StringComparison.OrdinalIgnoreCase));
                if (tab is null)
                {
                    continue;
                }

                string? detectionKey = TryReadNullableString(details, "target_detection_key");
                tombstonesByTab[tab.TabId][targetPointId!] = new DeletedPointTombstone(
                    correctionId!,
                    targetPointId!,
                    detectionKey);
                detectionKeysByTab[tab.TabId].Remove(targetPointId!);
            }
        }
    }

    private static void ReplaceContents<TKey, TValue>(
        Dictionary<TKey, TValue> destination,
        IReadOnlyDictionary<TKey, TValue> source)
        where TKey : notnull
    {
        destination.Clear();
        foreach ((TKey key, TValue value) in source)
        {
            destination.Add(key, value);
        }
    }

    private static bool TryReadString(JsonElement value, string propertyName, out string? result)
    {
        result = null;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        result = property.GetString();
        return true;
    }

    private static string? TryReadNullableString(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryReadDouble(JsonElement value, string propertyName, out double result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out result) &&
            double.IsFinite(result);
    }

    private static bool TryReadPositiveInt(JsonElement value, string propertyName, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out result) &&
            result > 0;
    }

    private static bool TryReadRect(JsonElement value, string propertyName, out PdfRectD result)
    {
        result = default;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(propertyName, out JsonElement rectangle) ||
            !TryReadDouble(rectangle, "x", out double x) ||
            !TryReadDouble(rectangle, "y", out double y) ||
            !TryReadDouble(rectangle, "width", out double width) ||
            !TryReadDouble(rectangle, "height", out double height))
        {
            return false;
        }
        result = new PdfRectD(x, y, width, height);
        return result.IsValid;
    }

    private static ProductionReviewProjectionResult RejectProductionProjection(string technicalMessage) =>
        ProductionReviewProjectionResult.Rejected(new ProductionWorkflowFailure(
            ProductionWorkflowFailureCodes.ReviewProjectionRejected,
            "Errors.ProductionReviewProjectionRejected",
            technicalMessage,
            Recoverable: true,
            "Keep the current manual review and rerun after every panel has exact approved projection evidence."));

    private static bool TryValidateProjection(
        WorkflowReviewPanel reviewPanel,
        ProductionPanelExportEvidence exportEvidence,
        ProductionPanelProjectionEvidence projection)
    {
        ProductionPointExportEvidence[] pointEvidenceItems = exportEvidence.Points.ToArray();
        SeriesRecord[] seriesItems = projection.Series.ToArray();
        PointRecord[] projectedPointItems = projection.Points.ToArray();
        if (pointEvidenceItems.Select(static point => point.PointId).Distinct().Count() != pointEvidenceItems.Length ||
            seriesItems.Select(static item => item.SeriesId.Value).Distinct().Count() != seriesItems.Length ||
            projection.Phases.Select(static item => item.PhaseId.Value).Distinct().Count() != projection.Phases.Count ||
            projectedPointItems.Select(static item => item.PointId.Value).Distinct().Count() != projectedPointItems.Length)
        {
            return false;
        }

        Dictionary<Guid, ProductionPointExportEvidence> pointEvidence = pointEvidenceItems
            .ToDictionary(static point => point.PointId);
        Dictionary<Guid, SeriesRecord> series = seriesItems.ToDictionary(static item => item.SeriesId.Value);
        HashSet<Guid> phases = projection.Phases.Select(static item => item.PhaseId.Value).ToHashSet();
        Dictionary<Guid, PointRecord> projectedPoints = projectedPointItems.ToDictionary(static item => item.PointId.Value);
        HashSet<Guid> reviewPointIds = reviewPanel.Points
            .Select(point => Guid.TryParse(point.PointId, out Guid id) ? id : Guid.Empty)
            .ToHashSet();
        (Guid SeriesId, Guid PointId)[] pointMemberships = seriesItems
            .SelectMany(seriesItem => seriesItem.PointIds.Select(pointId =>
                (seriesItem.SeriesId.Value, pointId.Value)))
            .ToArray();
        if (reviewPointIds.Contains(Guid.Empty) ||
            reviewPointIds.Count != reviewPanel.Points.Count ||
            !reviewPointIds.SetEquals(pointEvidence.Keys) ||
            !reviewPointIds.SetEquals(projectedPoints.Keys) ||
            pointMemberships.Select(static membership => membership.PointId).Distinct().Count() != pointMemberships.Length ||
            !reviewPointIds.SetEquals(pointMemberships.Select(static membership => membership.PointId)))
        {
            return false;
        }

        foreach (SeriesRecord retainedSeries in seriesItems)
        {
            HashSet<Guid> declaredPointIds = retainedSeries.PointIds.Select(static id => id.Value).ToHashSet();
            HashSet<Guid> reviewSeriesPointIds = reviewPanel.Points
                .Where(point => Guid.TryParse(point.SeriesId, out Guid id) && id == retainedSeries.SeriesId.Value)
                .Select(point => Guid.TryParse(point.PointId, out Guid id) ? id : Guid.Empty)
                .ToHashSet();
            HashSet<Guid> exactSeriesPointIds = projectedPointItems
                .Where(point => point.SeriesId?.Value == retainedSeries.SeriesId.Value)
                .Select(static point => point.PointId.Value)
                .ToHashSet();
            if (!declaredPointIds.SetEquals(reviewSeriesPointIds) ||
                !declaredPointIds.SetEquals(exactSeriesPointIds))
            {
                return false;
            }
        }

        foreach (WorkflowPoint point in reviewPanel.Points)
        {
            if (!Guid.TryParse(point.PointId, out Guid pointId) ||
                !Guid.TryParse(point.SeriesId, out Guid seriesId) ||
                !Guid.TryParse(point.PhaseId, out Guid phaseId) ||
                point.GraphX is null ||
                point.GraphY is null ||
                !pointEvidence.TryGetValue(pointId, out ProductionPointExportEvidence? metadata) ||
                !projectedPoints.TryGetValue(pointId, out PointRecord? exactPoint) ||
                metadata.ObservationIndex < 1 ||
                !series.TryGetValue(seriesId, out SeriesRecord? retainedSeries) ||
                !phases.Contains(phaseId) ||
                !Enum.TryParse(point.Shape, ignoreCase: true, out MarkerShape shape) ||
                !Enum.TryParse(point.Fill, ignoreCase: true, out MarkerFill fill) ||
                shape != retainedSeries.Shape ||
                fill != retainedSeries.Fill ||
                exactPoint.SeriesId?.Value != seriesId ||
                exactPoint.PhaseId?.Value != phaseId ||
                exactPoint.OriginalPixel.X != point.OriginalPixelX ||
                exactPoint.OriginalPixel.Y != point.OriginalPixelY ||
                exactPoint.GraphX != point.GraphX ||
                exactPoint.GraphY != point.GraphY ||
                exactPoint.ObservationIndex != metadata.ObservationIndex)
            {
                return false;
            }
        }

        return true;
    }

    private static List<PointRecord> MergeProjectedPoints(
        IReadOnlyList<PointRecord> current,
        IReadOnlyList<PointRecord> exact)
    {
        Dictionary<PointId, PointRecord> currentById = current.ToDictionary(static point => point.PointId);
        var merged = new List<PointRecord>(exact.Count + current.Count);
        foreach (PointRecord point in exact)
        {
            if (currentById.TryGetValue(point.PointId, out PointRecord? existing) &&
                existing.ModificationHistory.Count > 0)
            {
                merged.Add(existing);
            }
            else
            {
                merged.Add(point);
            }
        }

        merged.AddRange(current.Where(point => exact.All(candidate => candidate.PointId != point.PointId)));
        return merged;
    }

    private static bool CalibrationMatches(ManualCalibrationState current, ManualCalibrationState exact) =>
        current.Session1Y0 == exact.Session1Y0 &&
        current.Session1YMaximum == exact.Session1YMaximum &&
        current.SessionMaximumY0 == exact.SessionMaximumY0 &&
        current.YMaximum == exact.YMaximum &&
        current.XMaximum == exact.XMaximum;

    private static bool PhaseDividersMatch(
        IEnumerable<EditablePhaseDivider> current,
        IEnumerable<EditablePhaseDivider> projected) =>
        current.OrderBy(static divider => divider.OriginalX).Select(static divider =>
                (divider.DividerId, divider.OriginalX, divider.Code, divider.Label))
            .SequenceEqual(projected.OrderBy(static divider => divider.OriginalX).Select(static divider =>
                (divider.DividerId, divider.OriginalX, divider.Code, divider.Label)));

    private static bool ExistingSeriesAreCompatible(
        IEnumerable<SeriesCardViewModel> current,
        IEnumerable<SeriesRecord> projected)
    {
        Dictionary<string, SeriesRecord> retained = projected.ToDictionary(
            static series => series.SeriesId.Value.ToString("D"),
            StringComparer.OrdinalIgnoreCase);
        foreach (SeriesCardViewModel series in current)
        {
            if (!retained.TryGetValue(series.SeriesId, out SeriesRecord? exact))
            {
                continue;
            }

            if (!string.Equals(series.Symbol, exact.Symbol, StringComparison.Ordinal) ||
                !string.Equals(series.Label, exact.DisplayName, StringComparison.Ordinal) ||
                series.Shape != exact.Shape ||
                series.Fill != exact.Fill ||
                series.SemanticRole != exact.SemanticRole)
            {
                return false;
            }
        }

        return true;
    }

    private static string GetApplicationVersion()
    {
        Version version = typeof(ManualPreviewWorkspaceService).Assembly.GetName().Version ?? new Version(0, 0, 19);
        return $"{Math.Max(0, version.Major)}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}";
    }

    private async Task<RasterWorkspaceImport> LoadRasterPanelsAsync(
        Guid projectId,
        SourceId sourceId,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var panelStore = new ProductionWorkflowPanelStore();
        var stage = new ProductionWorkflowImportStage(
            panelStore,
            _imageImportService,
            _pdfImportService);
        WorkflowImportSnapshot snapshot = await stage.ImportAsync(
                new WorkflowImportRequest(
                    projectId,
                    [new WorkflowSourceRequest(sourceId.Value, WorkflowSourceKind.Image, Path.GetFullPath(sourcePath))],
                    enhancementEnabled: false),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        RasterSourceImageEvidence? source = null;
        var panels = new List<RasterWorkspacePanel>(snapshot.Panels.Count);
        foreach (WorkflowImportedPanel panel in snapshot.Panels)
        {
            ProductionPanelEvidence evidence = panelStore.Get(panel.PanelId);
            RasterPanelSourceProvenance provenance = evidence.RasterPanelSource ??
                throw RasterFailure(
                    $"Image panel '{panel.PanelId:D}' did not retain full-source raster provenance.");
            if (evidence.SourceKind != WorkflowSourceKind.Image ||
                panel.SourceId != sourceId.Value ||
                !string.Equals(provenance.PanelImageSha256, panel.Original.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw RasterFailure(
                    $"Image panel '{panel.PanelId:D}' did not match its retained raster source evidence.");
            }

            if (source is null)
            {
                source = provenance.Source;
            }
            else if (!string.Equals(source.Image.Reference, provenance.Source.Image.Reference, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(source.Image.Sha256, provenance.Source.Image.Sha256, StringComparison.OrdinalIgnoreCase) ||
                     source.Image.Width != provenance.Source.Image.Width ||
                     source.Image.Height != provenance.Source.Image.Height)
            {
                throw RasterFailure("Image panels from one import did not share one immutable full-source identity.");
            }

            panels.Add(new RasterWorkspacePanel(
                panel,
                new ImmutableImageBytes(evidence.CopyOriginalBytes()),
                provenance));
        }

        if (source is null || panels.Count == 0)
        {
            throw RasterFailure("Image workspace import returned no detector-ready panels.");
        }

        return new RasterWorkspaceImport(source, panels);
    }

    private async Task<RasterWorkspaceImport> RestoreRasterPanelsAsync(
        Guid projectId,
        SourceId sourceId,
        string sourcePath,
        string expectedSourceSha256,
        RetainedRasterPanelReference[] retainedPanels,
        CancellationToken cancellationToken)
    {
        var panelStore = new ProductionWorkflowPanelStore();
        var stage = new ProductionWorkflowImportStage(panelStore, _imageImportService, _pdfImportService);
        WorkflowImportSnapshot snapshot = await stage.RestoreRasterPanelsAsync(
                projectId,
                new WorkflowSourceRequest(sourceId.Value, WorkflowSourceKind.Image, Path.GetFullPath(sourcePath)),
                expectedSourceSha256,
                retainedPanels,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        RasterSourceImageEvidence? source = null;
        var panels = new List<RasterWorkspacePanel>(snapshot.Panels.Count);
        foreach (WorkflowImportedPanel panel in snapshot.Panels)
        {
            ProductionPanelEvidence evidence = panelStore.Get(panel.PanelId);
            RasterPanelSourceProvenance provenance = evidence.RasterPanelSource ??
                throw RasterFailure($"Restored image panel '{panel.PanelId:D}' has no source provenance.");
            if (panel.SourceId != sourceId.Value ||
                !string.Equals(provenance.PanelImageSha256, panel.Original.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw RasterFailure($"Restored image panel '{panel.PanelId:D}' did not match its retained identity.");
            }

            source ??= provenance.Source;
            if (!string.Equals(source.Image.Sha256, provenance.Source.Image.Sha256, StringComparison.OrdinalIgnoreCase) ||
                source.Image.Width != provenance.Source.Image.Width ||
                source.Image.Height != provenance.Source.Image.Height)
            {
                throw RasterFailure("Restored image panels did not share one immutable source identity.");
            }
            panels.Add(new RasterWorkspacePanel(
                panel,
                new ImmutableImageBytes(evidence.CopyOriginalBytes()),
                provenance));
        }

        if (source is null || panels.Count != retainedPanels.Length)
        {
            throw RasterFailure("Saved image panel restoration returned an incomplete panel set.");
        }
        return new RasterWorkspaceImport(source, panels);
    }

    private async Task<PdfWorkspaceImport> LoadPdfPanelsAsync(
        Guid projectId,
        SourceId sourceId,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var panelStore = new ProductionWorkflowPanelStore();
        var stage = new ProductionWorkflowImportStage(
            panelStore,
            _imageImportService,
            _pdfImportService);
        WorkflowImportSnapshot snapshot = await stage.ImportAsync(
                new WorkflowImportRequest(
                    projectId,
                    [new WorkflowSourceRequest(sourceId.Value, WorkflowSourceKind.Pdf, Path.GetFullPath(sourcePath))],
                    enhancementEnabled: false),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var panels = new List<PdfWorkspacePanel>(snapshot.Panels.Count);
        var documentChecksums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WorkflowImportedPanel panel in snapshot.Panels)
        {
            ProductionPanelEvidence evidence = panelStore.Get(panel.PanelId);
            if (evidence.SourceKind != WorkflowSourceKind.Pdf ||
                string.IsNullOrWhiteSpace(evidence.SourceDocumentSha256))
            {
                throw PdfFailure(
                    "PDF workspace import did not retain the source-document checksum.");
            }

            documentChecksums.Add(evidence.SourceDocumentSha256);
            var originalBytes = new ImmutableImageBytes(evidence.CopyOriginalBytes());
            BitmapImage decoded;
            try
            {
                decoded = CreateBitmap(originalBytes);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw PdfFailure(
                    $"PDF panel '{panel.PanelId:D}' encoded bytes could not be decoded: {exception.Message}");
            }

            if (decoded.PixelWidth != panel.Original.Width ||
                decoded.PixelHeight != panel.Original.Height)
            {
                throw PdfFailure(
                    $"PDF panel '{panel.PanelId:D}' encoded dimensions do not match its retained evidence.");
            }

            panels.Add(new PdfWorkspacePanel(
                panel,
                originalBytes));
        }

        if (panels.Count == 0 || documentChecksums.Count != 1)
        {
            throw PdfFailure(
                "PDF workspace import requires at least one panel bound to exactly one source-document checksum.");
        }

        return new PdfWorkspaceImport(documentChecksums.Single(), panels);
    }

    private void RegisterImportedTab(
        WorkspaceTabViewModel tab,
        ImmutableImageBytes originalBytes,
        List<WorkspaceTabViewModel> addedTabs)
    {
        _tabs.Add(tab);
        addedTabs.Add(tab);
        _originalPanelBytesByTab[tab.TabId] = originalBytes;
        _phaseOverrides[tab.TabId] = new PhaseManualOverrides();
        _productionDetectionKeysByTab[tab.TabId] =
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _deletedPointTombstonesByTab[tab.TabId] =
            new Dictionary<string, DeletedPointTombstone>(StringComparer.OrdinalIgnoreCase);
    }

    private static ImageImportError ToImportError(
        string sourcePath,
        ProductionWorkflowFailure failure) =>
        new(
            string.Equals(
                failure.Code,
                ProductionWorkflowFailureCodes.PdfImportUnavailable,
                StringComparison.Ordinal)
                ? ImageImportErrorCode.UnsupportedFormat
                : ImageImportErrorCode.IoFailure,
            ImageErrorSeverity.Error,
            failure.UserMessageKey,
            failure.TechnicalMessage,
            failure.Recoverable,
            ImageSuggestedAction.Retry,
            sourcePath);

    private static ProductionWorkflowStageException PdfFailure(string technicalMessage) =>
        new(new ProductionWorkflowFailure(
            ProductionWorkflowFailureCodes.PdfImportFailed,
            "Errors.PdfImportFailed",
            technicalMessage,
            Recoverable: true,
            "Retry the PDF import or select a detector-ready graph image."));

    private static ProductionWorkflowStageException RasterFailure(string technicalMessage) =>
        new(new ProductionWorkflowFailure(
            ProductionWorkflowFailureCodes.ImageImportFailed,
            "Errors.ImageReadFailed",
            technicalMessage,
            Recoverable: true,
            "Retry the image import or keep the source as one full-image panel."));

    private static bool IsPdfPath(string path) =>
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    private PanelRecord CreateRasterPanelSeed(
        WorkspaceTabViewModel tab,
        RasterPanelSourceProvenance provenance)
    {
        PanelRecord seed = ToPanelRecord(tab);
        return seed with
        {
            Crop = ToCropRectangle(provenance.EncodedCropInSourcePixels),
            Transforms = [CreateRasterCropTransform(seed.PanelId, provenance)],
        };
    }

    private static WorkspaceTabViewModel CreateEmptyRasterTab(
        SourceReference source,
        RasterWorkspacePanel panel)
    {
        var points = new ObservableCollection<AppGraphPoint>();
        var series = new ObservableCollection<SeriesCardViewModel>();
        var dividers = new ObservableCollection<EditablePhaseDivider>();
        return new WorkspaceTabViewModel(
            panel.Panel.PanelId.ToString("D"),
            panel.Panel.DisplayName,
            points,
            series,
            CreateBitmap(panel.OriginalBytes),
            enhancedImageSource: null,
            phaseOverlayContent: null,
            panel.Panel.PanelId.ToString("D"),
            source.SourceId.Value.ToString("D"),
            source.LocalPath,
            panel.Panel.Original.Sha256,
            panel.Panel.Original.Width,
            panel.Panel.Original.Height,
            calibration: null,
            dividers);
    }

    private static WorkspaceTabViewModel CreateEmptyPdfTab(
        SourceReference source,
        PdfWorkspacePanel panel)
    {
        var points = new ObservableCollection<AppGraphPoint>();
        var series = new ObservableCollection<SeriesCardViewModel>();
        var dividers = new ObservableCollection<EditablePhaseDivider>();
        return new WorkspaceTabViewModel(
            panel.Panel.PanelId.ToString("D"),
            panel.Panel.DisplayName,
            points,
            series,
            CreateBitmap(panel.OriginalBytes),
            enhancedImageSource: null,
            phaseOverlayContent: null,
            panel.Panel.PanelId.ToString("D"),
            source.SourceId.Value.ToString("D"),
            source.LocalPath,
            panel.Panel.Original.Sha256,
            panel.Panel.Original.Width,
            panel.Panel.Original.Height,
            calibration: null,
            dividers,
            pageNumber: panel.Panel.PageNumber);
    }

    private static WorkspaceTabViewModel CreateTabFromProject(
        PanelRecord panel,
        SourceReference source,
        ImportedImage image) =>
        CreateTabFromProject(
            panel,
            source,
            image.OriginalBytes,
            image.Metadata.Width,
            image.Metadata.Height,
            image.Sha256);

    private static WorkspaceTabViewModel CreateTabFromProject(
        PanelRecord panel,
        SourceReference source,
        ImmutableImageBytes originalBytes,
        int pixelWidth,
        int pixelHeight,
        string panelImageSha256)
    {
        PanelRecord workspacePanel = MapPanelRecordSourceToPanel(panel);
        var points = new ObservableCollection<AppGraphPoint>(workspacePanel.Points.Select(point => new AppGraphPoint(
            point.PointId.Value.ToString("D"),
            point.SeriesId?.Value.ToString("D") ?? string.Empty,
            point.OriginalPixel.X,
            point.OriginalPixel.Y,
            point.GraphX ?? 0,
            point.GraphY ?? 0,
            workspacePanel.Phases.FirstOrDefault(phase => phase.PhaseId == point.PhaseId)?.Code ?? "unknown",
            point.PhaseId?.Value.ToString("D"),
            point.ObservationIndex)));
        var series = new ObservableCollection<SeriesCardViewModel>(workspacePanel.Series.Select(item =>
            new SeriesCardViewModel(
                item.SeriesId.Value.ToString("D"),
                item.Symbol,
                $"{item.Fill} {item.Shape}",
                item.DisplayName,
                item.Confidence,
                points,
                item.Shape,
                item.Fill,
                item.SemanticRole)));
        var dividers = new ObservableCollection<EditablePhaseDivider>(workspacePanel.Phases
            .Where(static phase => phase.Order > 1 && phase.BoundaryLeftId is not null)
            .Select(phase => new EditablePhaseDivider(
                phase.BoundaryLeftId!.Value.Value.ToString("D"),
                phase.ScreenXMin,
                phase.Code,
                phase.LabelText ?? phase.Code)));
        ManualCalibrationState? calibration = FromDomainCalibration(workspacePanel.Calibration);
        return new WorkspaceTabViewModel(
            panel.PanelId.Value.ToString("D"),
            panel.DisplayName,
            points,
            series,
            CreateBitmap(originalBytes),
            enhancedImageSource: null,
            phaseOverlayContent: null,
            panel.PanelId.Value.ToString("D"),
            panel.SourceId.Value.ToString("D"),
            source.LocalPath,
            panelImageSha256,
            pixelWidth,
            pixelHeight,
            calibration,
            dividers,
            pageNumber: panel.PageNumber);
    }

    private static BitmapImage CreateBitmap(ImmutableImageBytes originalBytes)
    {
        using Stream stream = originalBytes.OpenRead();
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private async Task<string> MaterializeEnhancementSourceAsync(
        WorkspaceTabViewModel tab,
        ImmutableImageBytes originalBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = originalBytes.Copy();
        string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actualSha256, tab.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Panel '{tab.PanelId}' immutable bytes no longer match its retained SHA-256.");
        }

        string sourceRoot = Path.Combine(_applicationPaths!.CacheRoot, "Enhancement", "Sources");
        Directory.CreateDirectory(sourceRoot);
        string extension = DetectImageExtension(bytes, tab.SourcePath);
        string targetPath = Path.Combine(sourceRoot, $"{Guid.Parse(tab.PanelId!):N}-{actualSha256}.{extension}");
        if (File.Exists(targetPath))
        {
            byte[] existing = await File.ReadAllBytesAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(bytes)))
            {
                throw new InvalidOperationException(
                    $"Enhancement source cache collision for panel '{tab.PanelId}'.");
            }
            return targetPath;
        }

        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temporaryPath, targetPath);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                byte[] existing = await File.ReadAllBytesAsync(targetPath, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(bytes)))
                {
                    throw new InvalidOperationException(
                        $"Enhancement source cache collision for panel '{tab.PanelId}'.");
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return targetPath;
    }

    private static string DetectImageExtension(ReadOnlySpan<byte> bytes, string? sourcePath)
    {
        if (bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 &&
            bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10)
        {
            return "png";
        }
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
        {
            return "jpg";
        }
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4d)
        {
            return "bmp";
        }
        if (bytes.Length >= 4 &&
            ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2a && bytes[3] == 0x00) ||
             (bytes[0] == 0x4d && bytes[1] == 0x4d && bytes[2] == 0x00 && bytes[3] == 0x2a)))
        {
            return "tif";
        }
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return "webp";
        }

        string extension = Path.GetExtension(sourcePath ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return extension is "png" or "jpg" or "jpeg" or "bmp" or "tif" or "tiff" or "webp"
            ? extension
            : "img";
    }

    private static ManualCalibrationState? FromDomainCalibration(CalibrationRecord? calibration)
    {
        if (calibration is null)
        {
            return null;
        }

        DomainCalibrationAnchor? y0 = calibration.Anchors.FirstOrDefault(
            static anchor => anchor.Kind == DomainCalibrationAnchorKind.Session1Y0);
        DomainCalibrationAnchor? yMax = calibration.Anchors.FirstOrDefault(
            static anchor => anchor.Kind == DomainCalibrationAnchorKind.Session1Ymax);
        DomainCalibrationAnchor? xMax = calibration.Anchors.FirstOrDefault(
            static anchor => anchor.Kind == DomainCalibrationAnchorKind.SessionmaxY0);
        if (y0 is null || yMax is null || xMax is null)
        {
            return null;
        }

        double xMaximum = xMax.Graph.X;
        double yMaximum = yMax.Graph.Y;
        var xTransform = FitTransform(y0.Screen.X, 1, xMax.Screen.X, xMaximum);
        var yTransform = FitTransform(y0.Screen.Y, 0, yMax.Screen.Y, yMaximum);
        return new ManualCalibrationState(
            new AxisPixelPoint(y0.Screen.X, y0.Screen.Y),
            new AxisPixelPoint(yMax.Screen.X, yMax.Screen.Y),
            new AxisPixelPoint(xMax.Screen.X, xMax.Screen.Y),
            yMaximum,
            xMaximum,
            xTransform,
            yTransform,
            calibration.Confidence);
    }

    private void SynchronizeProject(
        DomainEventKind eventKind,
        Guid? panelId,
        string? entityId,
        string note,
        JsonElement? details = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var auditEvent = new AuditEvent(
            AuditEventId.New(),
            now,
            eventKind,
            panelId is null ? null : PanelId.FromGuid(panelId.Value),
            entityId,
            note,
            Details: details);
        CurrentProject = CurrentProject with
        {
            ModifiedUtc = now,
            Panels = _tabs.Select(ToPanelRecord).ToArray(),
            Audit = CurrentProject.Audit with
            {
                Events = CurrentProject.Audit.Events.Append(auditEvent).ToArray(),
            },
        };
    }

    private PanelRecord ToPanelRecord(WorkspaceTabViewModel tab)
    {
        PanelId panelId = PanelId.FromGuid(Guid.Parse(tab.PanelId!));
        SourceId sourceId = SourceId.FromGuid(Guid.Parse(tab.SourceId!));
        PanelRecord? existingPanel = CurrentProject.Panels
            .FirstOrDefault(panel => panel.PanelId == panelId);
        PhaseRecord[] workspaceRegionPhases = BuildRegionPhases(tab);
        PhaseRecord[] workspaceSemanticProbePhases = BuildSemanticProbePhases(
            tab,
            workspaceRegionPhases.Length);
        PhaseRecord[] generatedPhases = workspaceRegionPhases
            .Concat(workspaceSemanticProbePhases)
            .Select(phase => MapPhasePanelToSource(existingPanel, phase))
            .ToArray();
        bool preservePhaseEvidence = existingPanel is not null &&
            PhaseLayoutMatches(existingPanel.Phases, generatedPhases);
        PhaseRecord[] phases = preservePhaseEvidence
            ? existingPanel!.Phases.ToArray()
            : generatedPhases;
        PhaseRecord[] workspacePhasesForPointResolution = preservePhaseEvidence
            ? phases.Select(phase => MapPhaseSourceToPanel(existingPanel, phase)).ToArray()
            : workspaceRegionPhases.Concat(workspaceSemanticProbePhases).ToArray();
        PhaseRecord[] workspaceRegionPhasesForPointResolution = workspacePhasesForPointResolution
            .Take(workspaceRegionPhases.Length)
            .ToArray();
        PhaseRecord[] workspaceSemanticProbePhasesForPointResolution = workspacePhasesForPointResolution
            .Skip(workspaceRegionPhases.Length)
            .ToArray();
        Dictionary<SeriesId, SeriesRecord> existingSeries = existingPanel?
            .Series.ToDictionary(static series => series.SeriesId)
            ?? new Dictionary<SeriesId, SeriesRecord>();
        Dictionary<PointId, PointRecord> existingPoints = existingPanel?
            .Points.ToDictionary(static point => point.PointId)
            ?? new Dictionary<PointId, PointRecord>();
        Dictionary<string, PhaseRecord> pointPhases = tab.Points.ToDictionary(
            static point => point.PointId,
            point => ResolvePointPhase(
                tab,
                point,
                workspaceRegionPhasesForPointResolution,
                workspaceSemanticProbePhasesForPointResolution));
        HashSet<SeriesId> validBaselineIds = tab.SeriesCards
            .Where(static item => item.SemanticRole == SemanticRole.Baseline)
            .Select(item => SeriesId.FromGuid(Guid.Parse(item.SeriesId)))
            .ToHashSet();
        HashSet<SeriesId> validProbeIds = tab.SeriesCards
            .Where(static item => item.SemanticRole is SemanticRole.Maintenance or SemanticRole.Generalization)
            .Select(item => SeriesId.FromGuid(Guid.Parse(item.SeriesId)))
            .ToHashSet();
        SeriesRecord[] series = tab.SeriesCards.Select(item =>
        {
            SeriesId seriesId = SeriesId.FromGuid(Guid.Parse(item.SeriesId));
            _ = existingSeries.TryGetValue(seriesId, out SeriesRecord? prior);
            bool retainedIdentity = prior is not null &&
                string.Equals(prior.Symbol, item.Symbol, StringComparison.Ordinal) &&
                prior.Shape == item.Shape &&
                prior.Fill == item.Fill &&
                string.Equals(prior.DisplayName, item.Label, StringComparison.Ordinal) &&
                prior.SemanticRole == item.SemanticRole;
            return new SeriesRecord(
                seriesId,
                item.Symbol,
                item.Shape,
                item.Fill,
                item.Label,
                item.SemanticRole,
                prior?.LegendText,
                tab.Points.Where(point => point.SeriesId == item.SeriesId)
                    .Select(point => PointId.FromGuid(Guid.Parse(point.PointId)))
                    .ToArray(),
                item.Confidence,
                item.SemanticRole == SemanticRole.Intervention &&
                    prior?.SharedBaselineSeriesId is { } baselineId &&
                    validBaselineIds.Contains(baselineId)
                        ? baselineId
                        : null,
                item.SemanticRole == SemanticRole.Intervention
                    ? prior?.ApplicableProbeSeriesIds.Where(validProbeIds.Contains).ToArray() ?? []
                    : [],
                UserConfirmedName: retainedIdentity
                    ? prior!.UserConfirmedName
                    : true);
        }).ToArray();
        PointRecord[] points = tab.Points.Select(point =>
        {
            PointId pointId = PointId.FromGuid(Guid.Parse(point.PointId));
            _ = existingPoints.TryGetValue(pointId, out PointRecord? prior);
            PhaseRecord phase = pointPhases[point.PointId];
            point.PhaseId = phase.PhaseId.Value.ToString("D");
            point.PhaseCode = phase.Code;
            ManualPointXState xState = GetPointXState(tab, point);
            DomainPixelPoint originalPixel = MapPointPanelToSource(
                existingPanel,
                new DomainPixelPoint(point.PixelX, point.PixelY));
            SeriesId seriesId = SeriesId.FromGuid(Guid.Parse(point.SeriesId));
            double? graphX = xState.HasGraphX ? point.GraphX : null;
            double? graphY = tab.Calibration is null ? null : point.GraphY;
            PointModification[] workspaceModificationHistory = GetPointModificationHistory(point.PointId)
                .Select(modification => MapModificationPanelToSource(existingPanel, modification))
                .ToArray();
            PointModification[] modificationHistory = workspaceModificationHistory.Length == 0 &&
                prior?.ModificationHistory.Count > 0
                    ? prior.ModificationHistory.ToArray()
                    : workspaceModificationHistory;
            bool manuallyCorrected = prior is null ||
                modificationHistory.Length > 0 ||
                prior.SeriesId != seriesId ||
                prior.PhaseId != phase.PhaseId ||
                prior.OriginalPixel != originalPixel ||
                prior.GraphX != graphX ||
                prior.GraphY != graphY ||
                prior.ObservationIndex != point.ObservationIndex ||
                prior.PrintedXValue != xState.PrintedXValue ||
                prior.EstimatedXValue != xState.EstimatedXValue ||
                prior.XSource != xState.Source;
            return new PointRecord(
                pointId,
                prior?.MarkerId,
                seriesId,
                phase.PhaseId,
                originalPixel,
                graphX,
                graphY,
                point.ObservationIndex,
                xState.PrintedXValue,
                xState.EstimatedXValue,
                xState.Source,
                xState.Confidence,
                manuallyCorrected
                    ? tab.Calibration?.Confidence ?? 0
                    : prior!.YConfidence,
                prior?.PointConfidence ?? 1,
                prior?.SourceStage ?? "manual",
                prior?.ModelVersion,
                manuallyCorrected
                    ? ReviewStatus.Corrected
                    : prior!.ReviewStatus,
                modificationHistory);
        }).ToArray();
        CalibrationRecord? generatedCalibration = MapCalibrationPanelToSource(
            existingPanel,
            ToDomainCalibration(tab.Calibration));
        CalibrationRecord? calibration = existingPanel?.Calibration is { } retainedCalibration &&
            tab.Calibration is { } tabCalibration &&
            FromDomainCalibration(MapCalibrationSourceToPanel(existingPanel, retainedCalibration)) is { } retainedTabCalibration &&
            CalibrationMatches(tabCalibration, retainedTabCalibration)
                ? retainedCalibration
                : generatedCalibration;
        return new PanelRecord(
            panelId,
            sourceId,
            tab.PageNumber,
            tab.DisplayName,
            existingPanel?.Participant,
            existingPanel?.Crop ?? new CropRectangle(0, 0, tab.PixelWidth, tab.PixelHeight),
            existingPanel?.Transforms ?? [],
            Enhancement: _enhancementByTab.TryGetValue(tab.TabId, out JsonElement enhancement)
                ? enhancement.Clone()
                : existingPanel?.Enhancement?.Clone(),
            calibration,
            existingPanel?.OcrRegions ?? [],
            existingPanel?.Markers ?? [],
            series,
            points,
            phases,
            new ExportSettingsRecord(
                "observation_order",
                IncludeAuditSidecar: true,
                series.Where(static item => item.SemanticRole == SemanticRole.Intervention)
                    .Select(static item => item.SeriesId)
                    .ToArray()),
            existingPanel?.Validation?.Clone());
    }

    private static bool PhaseLayoutMatches(
        IReadOnlyList<PhaseRecord> retained,
        PhaseRecord[] current)
    {
        if (retained.Count != current.Length)
        {
            return false;
        }

        for (int index = 0; index < current.Length; index++)
        {
            PhaseRecord left = retained[index];
            PhaseRecord right = current[index];
            if (left.Order != right.Order ||
                !string.Equals(left.Code, right.Code, StringComparison.Ordinal) ||
                left.NormalizedType != right.NormalizedType ||
                !string.Equals(left.LabelText, right.LabelText, StringComparison.Ordinal) ||
                left.ScreenXMin != right.ScreenXMin ||
                left.ScreenXMax != right.ScreenXMax ||
                left.BoundaryLeftId != right.BoundaryLeftId ||
                left.BoundaryRightId != right.BoundaryRightId)
            {
                return false;
            }
        }

        return true;
    }

    private static CalibrationRecord? ToDomainCalibration(ManualCalibrationState? calibration)
    {
        if (calibration is null)
        {
            return null;
        }

        return new CalibrationRecord(
            CalibrationId.New(),
            DomainCalibrationStatus.Valid,
            [
                new DomainCalibrationAnchor(
                    DomainCalibrationAnchorKind.Session1Y0,
                    new DomainPixelPoint(calibration.Session1Y0.X, calibration.Session1Y0.Y),
                    new DomainGraphPoint(1, 0),
                    calibration.Confidence,
                    EvidenceRegionId: null),
                new DomainCalibrationAnchor(
                    DomainCalibrationAnchorKind.Session1Ymax,
                    new DomainPixelPoint(calibration.Session1YMaximum.X, calibration.Session1YMaximum.Y),
                    new DomainGraphPoint(1, calibration.YMaximum),
                    calibration.Confidence,
                    EvidenceRegionId: null),
                new DomainCalibrationAnchor(
                    DomainCalibrationAnchorKind.SessionmaxY0,
                    new DomainPixelPoint(calibration.SessionMaximumY0.X, calibration.SessionMaximumY0.Y),
                    new DomainGraphPoint(calibration.XMaximum, 0),
                    calibration.Confidence,
                    EvidenceRegionId: null),
            ],
            new SessionLatticeRecord(
                calibration.Session1Y0.X,
                (calibration.SessionMaximumY0.X - calibration.Session1Y0.X) / (calibration.XMaximum - 1),
                PrintedMin: null,
                PrintedMax: null,
                calibration.Confidence,
                "manual_three_anchor"),
            UserConfirmed: true,
            calibration.Confidence,
            Reasons: []);
    }

    private static CropRectangle ToCropRectangle(PdfRectD crop) =>
        new(crop.X, crop.Y, crop.Width, crop.Height);

    private static TransformRecord CreateRasterCropTransform(
        PanelId panelId,
        RasterPanelSourceProvenance provenance) =>
        new(
            TransformId.FromGuid(ProductionWorkflowPanelStore.CreateStableId(
                "raster-source-crop-v1",
                panelId.Value.ToString("D"),
                provenance.Source.Image.Sha256,
                provenance.PanelImageSha256)),
            TransformKind.Crop,
            CoordinateSpace.OriginalPixels,
            CoordinateSpace.PanelPixels,
            provenance.SourceToPanelMatrix.ToArray(),
            provenance.PanelToSourceMatrix.ToArray(),
            JsonSerializer.SerializeToElement(new
            {
                schema = RasterCropTransformSchema,
                source_sha256 = provenance.Source.Image.Sha256,
                source_width = provenance.Source.Image.Width,
                source_height = provenance.Source.Image.Height,
                panel_sha256 = provenance.PanelImageSha256,
                requested_crop = new
                {
                    x = provenance.RequestedCropInSourcePixels.X,
                    y = provenance.RequestedCropInSourcePixels.Y,
                    width = provenance.RequestedCropInSourcePixels.Width,
                    height = provenance.RequestedCropInSourcePixels.Height,
                },
                encoded_crop = new
                {
                    x = provenance.EncodedCropInSourcePixels.X,
                    y = provenance.EncodedCropInSourcePixels.Y,
                    width = provenance.EncodedCropInSourcePixels.Width,
                    height = provenance.EncodedCropInSourcePixels.Height,
                },
            }),
            Lossy: false);

    private static void VerifySavedRasterPanel(
        PanelRecord saved,
        RasterWorkspacePanel imported)
    {
        RasterPanelSourceProvenance provenance = imported.SourceProvenance;
        RetainedRasterPanelReference retained = ReadRetainedRasterPanelReference(
            saved,
            provenance.Source.Image.Sha256);
        if (retained.PanelId != imported.Panel.PanelId ||
            retained.SourceWidth != provenance.Source.Image.Width ||
            retained.SourceHeight != provenance.Source.Image.Height ||
            retained.RequestedCropInSourcePixels != provenance.RequestedCropInSourcePixels ||
            retained.EncodedCropInSourcePixels != provenance.EncodedCropInSourcePixels ||
            imported.Panel.Original.Width != retained.EncodedCropInSourcePixels.Width ||
            imported.Panel.Original.Height != retained.EncodedCropInSourcePixels.Height ||
            !string.Equals(provenance.PanelImageSha256, imported.Panel.Original.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(retained.PanelImageSha256, imported.Panel.Original.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Saved image panel '{saved.PanelId.Value:D}' no longer matches its source crop, checksum, or dimensions.");
        }
    }

    private static RetainedRasterPanelReference ReadRetainedRasterPanelReference(
        PanelRecord panel,
        SourceReference source) =>
        ReadRetainedRasterPanelReference(panel, source.Sha256);

    private static RetainedRasterPanelReference ReadRetainedRasterPanelReference(
        PanelRecord panel,
        string expectedSourceSha256)
    {
        TransformRecord[] matches = panel.Transforms.Where(IsRasterCropTransform).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Saved image panel '{panel.PanelId.Value:D}' requires exactly one source-bound crop transform.");
        }
        TransformRecord transform = matches[0];
        JsonElement parameters = transform.Parameters;
        if (transform.Kind != TransformKind.Crop ||
            transform.SourceSpace != CoordinateSpace.OriginalPixels ||
            transform.TargetSpace != CoordinateSpace.PanelPixels ||
            transform.Lossy ||
            !TryReadString(parameters, "source_sha256", out string? sourceSha256) ||
            !string.Equals(sourceSha256, expectedSourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !TryReadString(parameters, "panel_sha256", out string? panelSha256) ||
            !TryReadPositiveInt(parameters, "source_width", out int sourceWidth) ||
            !TryReadPositiveInt(parameters, "source_height", out int sourceHeight) ||
            !TryReadRect(parameters, "requested_crop", out PdfRectD requestedCrop) ||
            !TryReadRect(parameters, "encoded_crop", out PdfRectD encodedCrop) ||
            panel.Crop != ToCropRectangle(encodedCrop) ||
            encodedCrop.X < 0 || encodedCrop.Y < 0 || encodedCrop.Right > sourceWidth || encodedCrop.Bottom > sourceHeight ||
            encodedCrop.X != Math.Floor(encodedCrop.X) || encodedCrop.Y != Math.Floor(encodedCrop.Y) ||
            encodedCrop.Width != Math.Floor(encodedCrop.Width) || encodedCrop.Height != Math.Floor(encodedCrop.Height) ||
            requestedCrop.X < encodedCrop.X || requestedCrop.Y < encodedCrop.Y ||
            requestedCrop.Right > encodedCrop.Right || requestedCrop.Bottom > encodedCrop.Bottom)
        {
            throw new InvalidOperationException(
                $"Saved image panel '{panel.PanelId.Value:D}' has invalid source-bound crop parameters.");
        }

        double[] expectedMatrix = [1, 0, -encodedCrop.X, 0, 1, -encodedCrop.Y, 0, 0, 1];
        double[] expectedInverse = [1, 0, encodedCrop.X, 0, 1, encodedCrop.Y, 0, 0, 1];
        TransformId expectedTransformId = TransformId.FromGuid(ProductionWorkflowPanelStore.CreateStableId(
            "raster-source-crop-v1",
            panel.PanelId.Value.ToString("D"),
            sourceSha256!,
            panelSha256!));
        if (transform.TransformId != expectedTransformId || transform.Lossy ||
            !transform.Matrix3x3.SequenceEqual(expectedMatrix) ||
            transform.InverseMatrix3x3 is null ||
            !transform.InverseMatrix3x3.SequenceEqual(expectedInverse))
        {
            throw new InvalidOperationException(
                $"Saved image panel '{panel.PanelId.Value:D}' has an invalid crop transform identity or matrix.");
        }

        return new RetainedRasterPanelReference(
            panel.PanelId.Value,
            panel.DisplayName,
            panelSha256!,
            sourceWidth,
            sourceHeight,
            requestedCrop,
            encodedCrop);
    }

    private static bool IsLegacyFullImagePanel(
        int panelsForSource,
        PanelRecord panel,
        ImportedImage image)
    {
        return panelsForSource == 1 &&
            panel.Crop == new CropRectangle(0, 0, image.Metadata.Width, image.Metadata.Height) &&
            panel.Transforms.All(static transform => !IsRasterCropTransform(transform));
    }

    private static bool IsRasterCropTransform(TransformRecord transform) =>
        TryReadString(transform.Parameters, "schema", out string? schema) &&
        string.Equals(schema, RasterCropTransformSchema, StringComparison.Ordinal);

    private static bool TryGetRasterCrop(PanelRecord? panel, out CropRectangle crop)
    {
        crop = default!;
        if (panel is null || !panel.Transforms.Any(IsRasterCropTransform))
        {
            return false;
        }

        crop = panel.Crop;
        return true;
    }

    private static DomainPixelPoint MapPointPanelToSource(
        PanelRecord? panel,
        DomainPixelPoint point) =>
        TryGetRasterCrop(panel, out CropRectangle crop)
            ? new DomainPixelPoint(point.X + crop.X, point.Y + crop.Y)
            : point;

    private static DomainPixelPoint MapPointSourceToPanel(
        PanelRecord? panel,
        DomainPixelPoint point) =>
        TryGetRasterCrop(panel, out CropRectangle crop)
            ? new DomainPixelPoint(point.X - crop.X, point.Y - crop.Y)
            : point;

    internal static DomainPixelPoint MapPersistedAuditPointToPanel(
        PanelRecord panel,
        JsonElement details,
        double x,
        double y)
    {
        var point = new DomainPixelPoint(x, y);
        if (!TryReadString(details, "coordinate_space", out string? coordinateSpace))
        {
            return point;
        }

        if (string.Equals(coordinateSpace, "panel_pixels", StringComparison.Ordinal))
        {
            return point;
        }

        if (!string.Equals(coordinateSpace, "original_pixels", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Persisted point correction uses unknown coordinate space '{coordinateSpace}'.");
        }

        string? declaredCropId = TryReadNullableString(details, "crop_transform_id");
        string? currentCropId = panel.Transforms.SingleOrDefault(IsRasterCropTransform)?
            .TransformId.Value.ToString("D");
        if (declaredCropId is not null &&
            !string.Equals(declaredCropId, currentCropId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Persisted point correction does not match the panel's retained crop transform.");
        }

        return MapPointSourceToPanel(panel, point);
    }

    private static PointModification MapModificationPanelToSource(
        PanelRecord? panel,
        PointModification modification) =>
        modification.PreviousPixel is null
            ? modification
            : modification with
            {
                PreviousPixel = MapPointPanelToSource(panel, modification.PreviousPixel),
            };

    private static PointModification MapModificationSourceToPanel(
        PanelRecord? panel,
        PointModification modification) =>
        modification.PreviousPixel is null
            ? modification
            : modification with
            {
                PreviousPixel = MapPointSourceToPanel(panel, modification.PreviousPixel),
            };

    private static PhaseRecord MapPhasePanelToSource(PanelRecord? panel, PhaseRecord phase) =>
        TryGetRasterCrop(panel, out CropRectangle crop)
            ? phase with
            {
                ScreenXMin = phase.ScreenXMin + crop.X,
                ScreenXMax = phase.ScreenXMax + crop.X,
            }
            : phase;

    private static PhaseRecord MapPhaseSourceToPanel(PanelRecord? panel, PhaseRecord phase) =>
        TryGetRasterCrop(panel, out CropRectangle crop)
            ? phase with
            {
                ScreenXMin = phase.ScreenXMin - crop.X,
                ScreenXMax = phase.ScreenXMax - crop.X,
            }
            : phase;

    private static CalibrationRecord? MapCalibrationPanelToSource(
        PanelRecord? panel,
        CalibrationRecord? calibration)
    {
        if (calibration is null || !TryGetRasterCrop(panel, out CropRectangle crop))
        {
            return calibration;
        }

        return calibration with
        {
            Anchors = calibration.Anchors.Select(anchor => anchor with
            {
                Screen = new DomainPixelPoint(anchor.Screen.X + crop.X, anchor.Screen.Y + crop.Y),
            }).ToArray(),
            SessionLattice = calibration.SessionLattice is null
                ? null
                : calibration.SessionLattice with
                {
                    Session1PixelX = calibration.SessionLattice.Session1PixelX + crop.X,
                },
        };
    }

    private static CalibrationRecord? MapCalibrationSourceToPanel(
        PanelRecord? panel,
        CalibrationRecord? calibration)
    {
        if (calibration is null || !TryGetRasterCrop(panel, out CropRectangle crop))
        {
            return calibration;
        }

        return calibration with
        {
            Anchors = calibration.Anchors.Select(anchor => anchor with
            {
                Screen = new DomainPixelPoint(anchor.Screen.X - crop.X, anchor.Screen.Y - crop.Y),
            }).ToArray(),
            SessionLattice = calibration.SessionLattice is null
                ? null
                : calibration.SessionLattice with
                {
                    Session1PixelX = calibration.SessionLattice.Session1PixelX - crop.X,
                },
        };
    }

    private static ProductionPanelProjectionEvidence MapProjectionPanelToSource(
        PanelRecord panel,
        ProductionPanelProjectionEvidence projection) =>
        new(
            MapCalibrationPanelToSource(panel, projection.Calibration)!,
            projection.Phases.Select(phase => MapPhasePanelToSource(panel, phase)),
            projection.Series,
            projection.Points.Select(point => point with
            {
                OriginalPixel = MapPointPanelToSource(panel, point.OriginalPixel),
                ModificationHistory = point.ModificationHistory
                    .Select(modification => MapModificationPanelToSource(panel, modification))
                    .ToArray(),
            }),
            MergeProjectionTransforms(panel, projection.Transforms),
            projection.OcrRegions.Select(ocr => ocr with
            {
                Polygon = ocr.Polygon.Select(point => MapPointPanelToSource(panel, point)).ToArray(),
            }),
            projection.Markers.Select(marker => marker with
            {
                Center = MapPointPanelToSource(panel, marker.Center),
            }),
            projection.Participant);

    private static PanelRecord MapPanelRecordSourceToPanel(PanelRecord panel)
    {
        if (!TryGetRasterCrop(panel, out _))
        {
            return panel;
        }

        return panel with
        {
            Calibration = MapCalibrationSourceToPanel(panel, panel.Calibration),
            Phases = panel.Phases.Select(phase => MapPhaseSourceToPanel(panel, phase)).ToArray(),
            Points = panel.Points.Select(point => point with
            {
                OriginalPixel = MapPointSourceToPanel(panel, point.OriginalPixel),
                ModificationHistory = point.ModificationHistory
                    .Select(modification => MapModificationSourceToPanel(panel, modification))
                    .ToArray(),
            }).ToArray(),
            OcrRegions = panel.OcrRegions.Select(ocr => ocr with
            {
                Polygon = ocr.Polygon.Select(point => MapPointSourceToPanel(panel, point)).ToArray(),
            }).ToArray(),
            Markers = panel.Markers.Select(marker => marker with
            {
                Center = MapPointSourceToPanel(panel, marker.Center),
            }).ToArray(),
            Transforms = LocalizeProjectionTransforms(panel),
        };
    }

    private static TransformRecord[] MergeProjectionTransforms(
        PanelRecord panel,
        IEnumerable<TransformRecord> projectionTransforms)
    {
        TransformRecord[] cropTransforms = panel.Transforms.Where(IsRasterCropTransform).ToArray();
        bool hasCrop = cropTransforms.Length > 0;
        TransformRecord[] prepared = projectionTransforms.Select(transform =>
            hasCrop && transform.SourceSpace == CoordinateSpace.OriginalPixels
                ? transform with { SourceSpace = CoordinateSpace.PanelPixels }
                : transform).ToArray();
        return cropTransforms.Concat(prepared)
            .GroupBy(static transform => transform.TransformId)
            .Select(static group => group.First())
            .ToArray();
    }

    private static TransformRecord[] LocalizeProjectionTransforms(PanelRecord panel) =>
        panel.Transforms
            .Where(static transform => !IsRasterCropTransform(transform))
            .Select(transform => transform.SourceSpace == CoordinateSpace.PanelPixels
                ? transform with { SourceSpace = CoordinateSpace.OriginalPixels }
                : transform)
            .ToArray();

    private void UpdateEnhancementProvenance(
        WorkspaceTabViewModel tab,
        EnhancementEnvelope envelope)
    {
        _enhancementByTab[tab.TabId] = JsonSerializer.SerializeToElement(new
        {
            selected_preview = tab.EnhancementPreviewMode.ToString().ToLowerInvariant(),
            original_immutable = true,
            enhancement = envelope,
        });
    }

    private static PhaseRecord[] BuildRegionPhases(WorkspaceTabViewModel tab)
    {
        EditablePhaseDivider[] dividers = tab.PhaseDividers.OrderBy(static item => item.OriginalX).ToArray();
        var phases = new List<PhaseRecord>(dividers.Length + 1);
        double left = 0;
        for (int index = 0; index <= dividers.Length; index++)
        {
            EditablePhaseDivider? preceding = index == 0 ? null : dividers[index - 1];
            EditablePhaseDivider? following = index == dividers.Length ? null : dividers[index];
            double right = following?.OriginalX ?? tab.PixelWidth;
            string code = preceding?.Code ?? "a";
            string label = preceding?.Label ?? "Baseline";
            var phaseId = PhaseId.FromGuid(Guid.Parse(preceding?.DividerId ?? tab.PanelId!));
            phases.Add(new PhaseRecord(
                phaseId,
                index + 1,
                code,
                MapDomainPhaseType(code),
                label,
                left,
                right,
                preceding is null ? null : PhaseId.FromGuid(Guid.Parse(preceding.DividerId)),
                following is null ? null : PhaseId.FromGuid(Guid.Parse(following.DividerId)),
                1,
                PhaseSource.Manual,
                UserConfirmed: true));
            left = right;
        }

        return phases.ToArray();
    }

    private static PhaseRecord[] BuildSemanticProbePhases(WorkspaceTabViewModel tab, int firstOrder)
    {
        return tab.SeriesCards
            .Where(series => ProbePhaseCode(series.SemanticRole) is not null)
            .Where(series => tab.Points.Any(point => string.Equals(
                point.SeriesId,
                series.SeriesId,
                StringComparison.Ordinal)))
            .Select((series, index) =>
            {
                string code = ProbePhaseCode(series.SemanticRole)!;
                return new PhaseRecord(
                    PhaseId.FromGuid(Guid.Parse(series.SeriesId)),
                    firstOrder + index + 1,
                    code,
                    MapDomainPhaseType(code),
                    series.Label,
                    0,
                    tab.PixelWidth,
                    BoundaryLeftId: null,
                    BoundaryRightId: null,
                    Confidence: 1,
                    PhaseSource.Manual,
                    UserConfirmed: true);
            })
            .ToArray();
    }

    private static PhaseRecord ResolvePointPhase(
        WorkspaceTabViewModel tab,
        AppGraphPoint point,
        IReadOnlyList<PhaseRecord> regionPhases,
        IReadOnlyList<PhaseRecord> semanticProbePhases)
    {
        SeriesCardViewModel series = RequireSeries(tab, point.SeriesId);
        if (ProbePhaseCode(series.SemanticRole) is not null)
        {
            PhaseId semanticPhaseId = PhaseId.FromGuid(Guid.Parse(series.SeriesId));
            return semanticProbePhases.Single(phase => phase.PhaseId == semanticPhaseId);
        }

        return regionPhases.Last(phase =>
            point.PixelX >= phase.ScreenXMin && point.PixelX <= phase.ScreenXMax);
    }

    private static void UpdatePointCoordinates(WorkspaceTabViewModel tab, AppGraphPoint point)
    {
        if (tab.Calibration is { } calibration)
        {
            point.GraphX = calibration.XTransform.PixelToGraph(point.PixelX);
            point.GraphY = calibration.YTransform.PixelToGraph(point.PixelY);
        }

        SeriesCardViewModel series = RequireSeries(tab, point.SeriesId);
        string? probePhaseCode = ProbePhaseCode(series.SemanticRole);
        if (probePhaseCode is not null)
        {
            // A confirmed probe role is point semantics, not a horizontal phase boundary.
            point.PhaseId = series.SeriesId;
            point.PhaseCode = probePhaseCode;
            return;
        }

        EditablePhaseDivider? preceding = tab.PhaseDividers
            .Where(divider => divider.OriginalX <= point.PixelX)
            .OrderByDescending(static divider => divider.OriginalX)
            .FirstOrDefault();
        point.PhaseId = preceding?.DividerId ?? tab.PanelId;
        point.PhaseCode = preceding?.Code ?? "a";
    }

    private static string? ProbePhaseCode(SemanticRole semanticRole) => semanticRole switch
    {
        SemanticRole.Maintenance => "m",
        SemanticRole.Generalization => "g",
        _ => null,
    };

    private static void UpdateAllPointPhases(WorkspaceTabViewModel tab)
    {
        foreach (AppGraphPoint point in tab.Points)
        {
            UpdatePointCoordinates(tab, point);
        }
    }

    private static void ReindexAllSeries(WorkspaceTabViewModel tab)
    {
        foreach (SeriesCardViewModel series in tab.SeriesCards)
        {
            ReindexSeries(tab, series.SeriesId);
        }
    }

    private static void ReindexSeries(WorkspaceTabViewModel tab, string seriesId)
    {
        AppGraphPoint[] ordered = tab.Points
            .Where(point => string.Equals(point.SeriesId, seriesId, StringComparison.Ordinal))
            .OrderBy(static point => point.PixelX)
            .ThenBy(static point => point.PixelY)
            .ThenBy(static point => point.PointId, StringComparer.Ordinal)
            .ToArray();
        for (int index = 0; index < ordered.Length; index++)
        {
            ordered[index].ObservationIndex = index + 1;
        }
    }

    private static DomainGraphPoint? GetPreviousGraph(WorkspaceTabViewModel tab, AppGraphPoint point) =>
        tab.Calibration is null ? null : new DomainGraphPoint(point.GraphX, point.GraphY);

    private void AppendPointModification(
        AppGraphPoint point,
        DomainPixelPoint previousPixel,
        DomainGraphPoint? previousGraph,
        string reason)
    {
        if (!_pointModificationHistories.TryGetValue(point.PointId, out List<PointModification>? history))
        {
            history = [];
            _pointModificationHistories[point.PointId] = history;
        }

        history.Add(new PointModification(
            AuditEventId.New(),
            DateTimeOffset.UtcNow,
            previousPixel,
            previousGraph,
            reason));
    }

    private PointModification[] GetPointModificationHistory(string pointId) =>
        _pointModificationHistories.TryGetValue(pointId, out List<PointModification>? history)
            ? history.ToArray()
            : [];

    private void MarkPointXAsManualEstimate(WorkspaceTabViewModel tab, AppGraphPoint point)
    {
        _pointXStates[point.PointId] = tab.Calibration is null
            ? new ManualPointXState(
                PrintedXValue: null,
                EstimatedXValue: null,
                Source: PointXSource.Unknown,
                Confidence: 0,
                HasGraphX: false)
            : new ManualPointXState(
                PrintedXValue: null,
                EstimatedXValue: point.GraphX,
                Source: PointXSource.Estimated,
                Confidence: 0,
                HasGraphX: true);
    }

    private ManualPointXState GetPointXState(WorkspaceTabViewModel tab, AppGraphPoint point)
    {
        if (_pointXStates.TryGetValue(point.PointId, out ManualPointXState? state))
        {
            return state;
        }

        return tab.Calibration is null
            ? new ManualPointXState(null, null, PointXSource.Unknown, 0, HasGraphX: false)
            : new ManualPointXState(null, point.GraphX, PointXSource.Estimated, 0, HasGraphX: true);
    }

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
            SemanticRole.Baseline => ExportSeriesRole.Baseline,
            SemanticRole.Intervention => ExportSeriesRole.Intervention,
            SemanticRole.Maintenance => ExportSeriesRole.Maintenance,
            SemanticRole.Generalization => ExportSeriesRole.Generalization,
            _ => ExportSeriesRole.Unknown,
        },
        series.PointIds.Select(static id => id.Value),
        series.Confidence,
        series.LegendText);

    private static ExportPoint ToExportPoint(PointRecord point) => new(
        point.PointId.Value,
        point.MarkerId?.Value,
        point.SeriesId?.Value,
        point.PhaseId?.Value,
        new ExportPixelPoint(point.OriginalPixel.X, point.OriginalPixel.Y),
        point.GraphX,
        point.GraphY,
        point.ObservationIndex,
        point.PrintedXValue,
        point.EstimatedXValue,
        point.XSource switch
        {
            PointXSource.Printed => ExportXValueSource.Printed,
            PointXSource.Estimated => ExportXValueSource.Estimated,
            PointXSource.ObservationOrder => ExportXValueSource.ObservationOrder,
            _ => ExportXValueSource.Unknown,
        },
        point.XConfidence,
        point.YConfidence,
        point.PointConfidence,
        point.ReviewStatus switch
        {
            ReviewStatus.Accepted => ExportReviewStatus.Accepted,
            ReviewStatus.Corrected => ExportReviewStatus.Corrected,
            ReviewStatus.Rejected => ExportReviewStatus.Rejected,
            _ => ExportReviewStatus.Unreviewed,
        },
        point.SourceStage,
        point.ModelVersion);

    private static LinearAxisTransform FitTransform(double pixel1, double graph1, double pixel2, double graph2)
    {
        double slope = (graph2 - graph1) / (pixel2 - pixel1);
        return new LinearAxisTransform(slope, graph1 - (slope * pixel1));
    }

    private static void ValidateCalibrationRequest(ManualCalibrationRequest request)
    {
        if (!request.Session1Y0.IsFinite || !request.Session1YMaximum.IsFinite ||
            !request.SessionMaximumY0.IsFinite ||
            request.YMaximum <= 0 || request.XMaximum <= 1 ||
            Math.Abs(request.Session1Y0.X - request.Session1YMaximum.X) > 2 ||
            Math.Abs(request.Session1Y0.Y - request.SessionMaximumY0.Y) > 2)
        {
            throw new ArgumentException("Manual calibration requires aligned (1,0), (1,yMax), and (xMax,0) anchors with positive maxima.", nameof(request));
        }
    }

    private static void EnsureFinitePoint(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Point coordinates must be finite original pixels.");
        }
    }

    private static void ValidatePhaseLabel(string code, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
    }

    private static PhaseNormalizedType MapPhaseType(string code) => code.Trim().ToLowerInvariant() switch
    {
        "a" or "a1" or "a2" => PhaseNormalizedType.Baseline,
        "b" or "b1" or "b2" => PhaseNormalizedType.Intervention,
        "m" or "m1" or "m2" => PhaseNormalizedType.Maintenance,
        "g" or "g1" or "g2" => PhaseNormalizedType.Generalization,
        _ => PhaseNormalizedType.Unknown,
    };

    private static DomainPhaseNormalizedType MapDomainPhaseType(string code) => MapPhaseType(code) switch
    {
        PhaseNormalizedType.Baseline => DomainPhaseNormalizedType.Baseline,
        PhaseNormalizedType.Intervention => DomainPhaseNormalizedType.Intervention,
        PhaseNormalizedType.Maintenance => DomainPhaseNormalizedType.Maintenance,
        PhaseNormalizedType.Generalization => DomainPhaseNormalizedType.Generalization,
        _ => DomainPhaseNormalizedType.Unknown,
    };

    private static PhaseRectangle PlotBounds(WorkspaceTabViewModel tab) =>
        new(0, 0, tab.PixelWidth, tab.PixelHeight);

    private static void RequirePhaseSuccess(PhaseEditResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Failure!.TechnicalMessage);
        }
    }

    private static void SortDividers(WorkspaceTabViewModel tab)
    {
        EditablePhaseDivider[] ordered = tab.PhaseDividers.OrderBy(static divider => divider.OriginalX).ToArray();
        tab.PhaseDividers.Clear();
        foreach (EditablePhaseDivider divider in ordered)
        {
            tab.PhaseDividers.Add(divider);
        }
    }

    private PhaseManualOverrides GetOverrides(WorkspaceTabViewModel tab) =>
        _phaseOverrides.TryGetValue(tab.TabId, out PhaseManualOverrides? overrides)
            ? overrides
            : new PhaseManualOverrides();

    private static PhaseManualOverrides CreatePhaseOverrides(WorkspaceTabViewModel tab) =>
        new(tab.PhaseDividers.Select(divider => new PhaseManualDivider(
            divider.DividerId,
            divider.OriginalX,
            PhaseDividerStyle.Dashed)));

    private WorkspaceTabViewModel RequireTab(string tabId) =>
        _tabs.SingleOrDefault(tab => string.Equals(tab.TabId, tabId, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Workspace tab '{tabId}' does not exist.");

    private static SeriesCardViewModel RequireSeries(WorkspaceTabViewModel tab, string seriesId) =>
        tab.SeriesCards.SingleOrDefault(series => string.Equals(series.SeriesId, seriesId, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Series '{seriesId}' does not exist.");

    private static AppGraphPoint RequirePoint(WorkspaceTabViewModel tab, string pointId) =>
        tab.Points.SingleOrDefault(point => string.Equals(point.PointId, pointId, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Point '{pointId}' does not exist.");

    private static EditablePhaseDivider RequireDivider(WorkspaceTabViewModel tab, string dividerId) =>
        tab.PhaseDividers.SingleOrDefault(divider => string.Equals(divider.DividerId, dividerId, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Phase divider '{dividerId}' does not exist.");

    private static string FormatErrors(IEnumerable<DomainError> errors) =>
        string.Join(" | ", errors.Select(static error => $"{error.Code}: {error.TechnicalMessage}"));

    private sealed record ManualPointXState(
        double? PrintedXValue,
        double? EstimatedXValue,
        PointXSource Source,
        double Confidence,
        bool HasGraphX);
}
