// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Input;
using GraphReader.App.Appearance;
using GraphReader.App.Controls;
using GraphReader.App.Integration;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Localization;
using GraphReader.App.Models;
using GraphReader.App.Services;
using GraphReader.Domain;
using GraphPoint = GraphReader.App.Models.GraphPoint;

namespace GraphReader.App.ViewModels;

public enum ManualEditorMode
{
    Select,
    Calibration,
    AddPoint,
    MovePoint,
    AddPhaseDivider,
    MovePhaseDivider,
}

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly string[] RequiredAutomaticDetectionStages =
        ["axis", "ocr", "markers", "legends", "phases"];
    private static readonly HashSet<char> InvalidExportFileNameCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> ReservedExportDeviceNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "COM¹", "COM²", "COM³", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6",
            "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
        ],
        StringComparer.OrdinalIgnoreCase);
    private const int MaximumExportStemLength = 229;
    private readonly IWorkspaceService _workspaceService;
    private readonly ILocalizationService? _localizationService;
    private readonly IManualWorkspaceService? _manualWorkspaceService;
    private readonly IWorkspaceDialogService? _dialogService;
    private readonly string? _recentProjectsPath;
    private readonly Func<WorkspaceTabViewModel, IReadOnlyList<PipelineWarningPresentation>> _pipelineWarningsProvider;
    private readonly bool _isWorkflowAvailable;
    private readonly CancellationTokenSource _shutdown = new();
    private WorkspaceTabViewModel? _selectedTab;
    private WorkflowStage _currentStage = WorkflowStage.Review;
    private bool _isBusy;
    private string _statusMessageKey = "Workflow.Review";
    private string? _statusMessageOverride;
    private bool _isPhaseOverlayVisible = true;
    private ApplicationTheme _appearanceMode = ApplicationTheme.System;
    private string? _selectedPointId;
    private string? _selectedSeriesId;
    private int _nextManualPointId = 1;
    private int _nextSplitSeriesId = 1;
    private CancellationTokenSource? _activeOperation;
    private bool _disposed;
    private ManualEditorMode _editorMode;
    private readonly List<Point> _calibrationAnchors = [];
    private string _newSeriesName = string.Empty;
    private MarkerShape _newSeriesShape = MarkerShape.Circle;
    private MarkerFill _newSeriesFill = MarkerFill.Filled;
    private SemanticRole _newSeriesRole = SemanticRole.Intervention;
    private string _phaseCode = "b";
    private string _phaseLabel = string.Empty;
    private string? _selectedDividerId;
    private string? _selectedSharedBaselineSeriesId;
    private double _manualYMaximum = 100;
    private double _manualXMaximum = 20;
    private int _manualFirstVisibleSession = 1;
    private string _manualFirstVisibleSessionText = "1";
    private readonly Dictionary<string, TabEditHistory> _editHistories = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reviewReadyTabIds = new(StringComparer.Ordinal);
    private WorkspaceSurfaceState _surfaceState;
    private WorkspaceSurfaceState _surfaceBeforeOperation;
    private string _statusKeyBeforeOperation = "Workflow.Review";
    private string? _statusOverrideBeforeOperation;
    private ReviewIssueViewModel? _selectedReviewIssue;
    private FilteredMarkerCandidateViewModel? _selectedFilteredMarker;
    private DataPreviewRowViewModel? _selectedDataPreviewRow;
    private bool _isDataPreviewExpanded;
    private ExportSummaryViewModel? _exportSummary;
    private WorkspaceOperationStatus? _lastOperationStatus;
    private WorkspaceOperationStatus? _operationStatusBeforeOperation;
    private bool _isRestoringHistory;

    public MainWindowViewModel(IWorkspaceService workspaceService)
        : this(workspaceService, null)
    {
    }

    public MainWindowViewModel(
        IWorkspaceService workspaceService,
        ILocalizationService? localizationService,
        string? startupErrorMessageKey = null,
        IWorkspaceDialogService? dialogService = null,
        string? recentProjectsPath = null,
        Func<WorkspaceTabViewModel, IReadOnlyList<PipelineWarningPresentation>>? pipelineWarningsProvider = null)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _localizationService = localizationService;
        _manualWorkspaceService = workspaceService as IManualWorkspaceService;
        _dialogService = dialogService;
        _recentProjectsPath = recentProjectsPath;
        _pipelineWarningsProvider = pipelineWarningsProvider ?? ResolvePipelineWarnings;
        _newSeriesName = GetLocalizedString(LocalizationKeys.ManualDefaultIntervention);
        _phaseLabel = GetLocalizedString(LocalizationKeys.ManualDefaultIntervention);
        MarkerShapeChoices =
        [
            Choice(MarkerShape.Circle, LocalizationKeys.MarkerShapeCircle),
            Choice(MarkerShape.Square, LocalizationKeys.MarkerShapeSquare),
            Choice(MarkerShape.TriangleUp, LocalizationKeys.MarkerShapeTriangleUp),
            Choice(MarkerShape.TriangleDown, LocalizationKeys.MarkerShapeTriangleDown),
            Choice(MarkerShape.Diamond, LocalizationKeys.MarkerShapeDiamond),
            Choice(MarkerShape.Star, LocalizationKeys.MarkerShapeStar),
            Choice(MarkerShape.Asterisk, LocalizationKeys.MarkerShapeAsterisk),
            Choice(MarkerShape.Cross, LocalizationKeys.MarkerShapeCross),
            Choice(MarkerShape.Other, LocalizationKeys.MarkerShapeOther),
        ];
        MarkerFillChoices =
        [
            Choice(MarkerFill.Filled, LocalizationKeys.MarkerFillFilled),
            Choice(MarkerFill.Open, LocalizationKeys.MarkerFillOpen),
            Choice(MarkerFill.Unknown, LocalizationKeys.MarkerFillUnknown),
        ];
        SeriesRoleChoices =
        [
            Choice(SemanticRole.Baseline, LocalizationKeys.SemanticRoleBaseline),
            Choice(SemanticRole.Intervention, LocalizationKeys.SemanticRoleIntervention),
            Choice(SemanticRole.Maintenance, LocalizationKeys.SemanticRoleMaintenance),
            Choice(SemanticRole.Generalization, LocalizationKeys.SemanticRoleGeneralization),
            Choice(SemanticRole.Unknown, LocalizationKeys.SemanticRoleUnknown),
        ];
        BaselineRelationChoices = [];
        ProbeRelationChoices = [];
        ReviewIssues = [];
        FilteredMarkers = [];
        DataPreviewRows = [];
        RecentProjects = new ObservableCollection<string>(LoadRecentProjectPaths(recentProjectsPath));
        _isWorkflowAvailable = string.IsNullOrWhiteSpace(startupErrorMessageKey);
        if (!_isWorkflowAvailable)
        {
            _statusMessageKey = startupErrorMessageKey!;
        }
        Tabs = new ObservableCollection<WorkspaceTabViewModel>(_workspaceService.CreateWorkspace());
        foreach (WorkspaceTabViewModel tab in Tabs)
        {
            EnsureManualOverlay(tab);
            ConfigureSeriesSelection(tab);
            EnsureEditHistory(tab);
            if (tab.Points.Count > 0 || tab.Calibration is not null)
            {
                _reviewReadyTabIds.Add(tab.TabId);
            }
        }
        _selectedTab = Tabs.FirstOrDefault();
        _selectedPointId = _selectedTab?.Points.FirstOrDefault()?.PointId;
        Magnifier = new MagnifierViewModel();
        Magnifier.IsCrosshairVisible = _selectedTab is not null;
        _surfaceState = _selectedTab is null
            ? WorkspaceSurfaceState.Empty
            : _reviewReadyTabIds.Contains(_selectedTab.TabId)
                ? WorkspaceSurfaceState.Reviewing
                : WorkspaceSurfaceState.Ready;

        ImportCommand = new AsyncRelayCommand(
            ImportImagesFromDialogAsync,
            () => !IsBusy && _isWorkflowAvailable);
        EnhanceCommand = _manualWorkspaceService is null
            ? CreateWorkflowCommand(WorkflowStage.Prepare, "Workflow.Enhance")
            : new AsyncRelayCommand(
                EnhanceSelectedTabAsync,
                () => !IsBusy && _isWorkflowAvailable && SelectedTab is not null &&
                    CanRunAutomaticStage(WorkflowStage.Prepare));
        AutoDetectCommand = CreateWorkflowCommand(WorkflowStage.Detect, "Workflow.AutoDetect");
        ReviewCommand = CreateWorkflowCommand(WorkflowStage.Review, "Workflow.Review");
        ExportCommand = new AsyncRelayCommand(
            ExportFromDialogAsync,
            () => !IsBusy && _isWorkflowAvailable && (_manualWorkspaceService is null || SelectedTab is not null));
        CancelCommand = new RelayCommand(_ => CancelActiveOperation(), _ => IsBusy);
        AddPointCommand = new RelayCommand(
            parameter => AddPoint((string)parameter!),
            parameter => parameter is string seriesId && FindSeries(seriesId) is not null);
        DeletePointCommand = new RelayCommand(
            parameter => DeletePoint((string)parameter!),
            parameter => parameter is string pointId && FindPoint(pointId) is not null);
        PromoteFilteredMarkerCommand = new RelayCommand(_ => PromoteFilteredMarker(), _ => CanPromoteFilteredMarker());
        AcceptReviewPointCommand = new RelayCommand(_ => AcceptReviewPoint(), _ => CanAcceptReviewPoint());
        MovePointCommand = new RelayCommand(
            parameter =>
            {
                MovePointRequest request = (MovePointRequest)parameter!;
                MovePoint(request.PointId, request.PixelX, request.PixelY);
            },
            parameter => parameter is MovePointRequest request && FindPoint(request.PointId) is not null);
        MergeSeriesCommand = new RelayCommand(
            parameter =>
            {
                SeriesPairRequest request = (SeriesPairRequest)parameter!;
                MergeSeries(request.SourceSeriesId, request.TargetSeriesId);
            },
            parameter => parameter is SeriesPairRequest request &&
                request.SourceSeriesId != request.TargetSeriesId &&
                FindSeries(request.SourceSeriesId) is not null &&
                FindSeries(request.TargetSeriesId) is not null);
        SplitSeriesCommand = new RelayCommand(
            parameter =>
            {
                SplitSeriesRequest request = (SplitSeriesRequest)parameter!;
                SplitSeries(request.SourceSeriesId, request.PointIds);
            },
            parameter => parameter is SplitSeriesRequest request &&
                request.PointIds.Count > 0 &&
                FindSeries(request.SourceSeriesId) is not null);
        ReassignPointCommand = new RelayCommand(
            parameter => ExecuteReassignCommand(parameter),
            CanExecuteReassignCommand);
        TogglePhaseOverlayCommand = new RelayCommand(
            _ => IsPhaseOverlayVisible = !IsPhaseOverlayVisible);
        ZoomInCommand = new RelayCommand(_ => ChangeZoom(1.25), _ => SelectedTab is not null);
        ZoomOutCommand = new RelayCommand(_ => ChangeZoom(0.8), _ => SelectedTab is not null);
        FitZoomCommand = new RelayCommand(_ => SetZoom(1), _ => SelectedTab is not null);
        ResetViewCommand = new RelayCommand(_ => SetZoom(1), _ => SelectedTab is not null);
        ShowOriginalPreviewCommand = new RelayCommand(
            _ => SetEnhancementPreviewMode(EnhancementPreviewMode.Original),
            _ => SelectedTab is not null);
        ShowEnhancedPreviewCommand = new RelayCommand(
            _ => SetEnhancementPreviewMode(EnhancementPreviewMode.Enhanced),
            _ => SelectedTab?.HasEnhancedPreview == true);
        ShowComparisonPreviewCommand = new RelayCommand(
            _ => SetEnhancementPreviewMode(EnhancementPreviewMode.Comparison),
            _ => SelectedTab?.HasEnhancedPreview == true);
        NextTabCommand = new RelayCommand(_ => SelectRelativeTab(1), _ => Tabs.Count > 1);
        PreviousTabCommand = new RelayCommand(_ => SelectRelativeTab(-1), _ => Tabs.Count > 1);
        OpenProjectCommand = new AsyncRelayCommand(OpenProjectFromDialogAsync, () => !IsBusy && _manualWorkspaceService is not null);
        OpenRecentProjectCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is string path)
                {
                    _ = OpenProjectPathAsync(path, CancellationToken.None);
                }
            },
            parameter => parameter is string path && File.Exists(path) && !IsBusy && _manualWorkspaceService is not null);
        SaveProjectCommand = new AsyncRelayCommand(
            cancellationToken => SaveProjectFromDialogAsync(saveAs: false, cancellationToken),
            () => !IsBusy && _manualWorkspaceService is not null && Tabs.Count > 0);
        SaveProjectAsCommand = new AsyncRelayCommand(
            cancellationToken => SaveProjectFromDialogAsync(saveAs: true, cancellationToken),
            () => !IsBusy && _manualWorkspaceService is not null && Tabs.Count > 0);
        RecoverProjectCommand = new AsyncRelayCommand(
            RecoverProjectFromAutosaveAsync,
            () => !IsBusy && _manualWorkspaceService is not null);
        CloseTabCommand = new RelayCommand(
            parameter => CloseTab(parameter as WorkspaceTabViewModel ?? SelectedTab),
            parameter => parameter is WorkspaceTabViewModel || SelectedTab is not null);
        StartCalibrationCommand = new RelayCommand(_ => BeginCalibration(), _ => CanStartCalibration());
        SelectEditorModeCommand = new RelayCommand(
            _ => EditorMode = ManualEditorMode.Select,
            _ => SelectedTab is not null);
        CreateSeriesCommand = new RelayCommand(_ => CreateSeries(), _ => SelectedTab is not null && _manualWorkspaceService is not null);
        EditSeriesCommand = new RelayCommand(_ => EditSelectedSeries(), _ => CanEditSelectedSeries());
        ApplySeriesRelationsCommand = new RelayCommand(_ => ApplySeriesRelations(), _ => CanApplySeriesRelations());
        BeginAddPointCommand = new RelayCommand(_ => EditorMode = ManualEditorMode.AddPoint, _ => CanEditPoint());
        BeginAddFilledPointCommand = new RelayCommand(
            _ => BeginAddPointForFill(MarkerFill.Filled),
            _ => HasSeriesWithFill(MarkerFill.Filled));
        BeginAddOpenPointCommand = new RelayCommand(
            _ => BeginAddPointForFill(MarkerFill.Open),
            _ => HasSeriesWithFill(MarkerFill.Open));
        BeginMovePointCommand = new RelayCommand(_ => EditorMode = ManualEditorMode.MovePoint, _ => CanMoveSelectedPoint());
        BeginAddPhaseDividerCommand = new RelayCommand(_ => EditorMode = ManualEditorMode.AddPhaseDivider, _ => SelectedTab is not null && _manualWorkspaceService is not null);
        BeginMovePhaseDividerCommand = new RelayCommand(_ => EditorMode = ManualEditorMode.MovePhaseDivider, _ => SelectedDividerId is not null && _manualWorkspaceService is not null);
        DeletePhaseDividerCommand = new RelayCommand(_ => DeleteSelectedPhaseDivider(), _ => SelectedDividerId is not null && _manualWorkspaceService is not null);
        LabelPhaseDividerCommand = new RelayCommand(_ => LabelSelectedPhaseDivider(), _ => SelectedDividerId is not null && _manualWorkspaceService is not null);
        UndoCommand = new RelayCommand(_ => UndoSelectedTab(), _ => CanUndo && !IsBusy);
        RedoCommand = new RelayCommand(_ => RedoSelectedTab(), _ => CanRedo && !IsBusy);
        OpenExportPreviewCommand = new RelayCommand(
            _ => OpenExportPreview(),
            _ => SelectedTab is not null && !IsBusy);
        ConfirmExportCommand = new AsyncRelayCommand(
            ExportFromDialogAsync,
            () => ExportSummary?.CanExport == true && !IsBusy);
        SelectReviewIssueCommand = new RelayCommand(
            parameter => SelectReviewIssue(parameter as ReviewIssueViewModel),
            parameter => parameter is ReviewIssueViewModel);
        SelectDataPreviewRowCommand = new RelayCommand(
            parameter => SelectDataPreviewRow(parameter as DataPreviewRowViewModel),
            parameter => parameter is DataPreviewRowViewModel);

        BuildIdentity identity = BuildIdentity.Current();
        VersionText = identity.Version;
        ShortCommit = identity.ShortCommit;
        RuntimeModeText = workspaceService is IRuntimeWorkspaceService runtime
            ? runtime.RuntimeEnvironment.ToString()
            : GetLocalizedString(LocalizationKeys.PreviewUnknown);
        AvailableAutomaticStagesText = workspaceService is IRuntimeWorkspaceService available
            ? JoinStages(available.AutomaticStages, static state => state is AutomaticStageState.Available or AutomaticStageState.Approved or AutomaticStageState.Experimental)
            : GetLocalizedString(LocalizationKeys.PreviewNone);
        MissingAutomaticStagesText = workspaceService is IRuntimeWorkspaceService missing
            ? JoinStages(missing.AutomaticStages, static state => state == AutomaticStageState.Unavailable)
            : GetLocalizedString(LocalizationKeys.PreviewUnknown);
        AutomaticStageStatus? enhancementStatus = (workspaceService as IRuntimeWorkspaceService)?.AutomaticStages
            .FirstOrDefault(status => string.Equals(status.Stage, "enhancement", StringComparison.Ordinal));
        EnhancementAvailabilityText = enhancementStatus?.State == AutomaticStageState.Experimental
            ? GetLocalizedString(LocalizationKeys.WorkflowEnhanceExperimental)
            : CanRunAutomaticStage(WorkflowStage.Prepare)
                ? string.Empty
                : GetLocalizedString(LocalizationKeys.WorkflowEnhanceUnavailable);
        RefreshWorkspaceProjection();
    }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; }

    public WorkspaceTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!ReferenceEquals(_selectedTab, value))
            {
                // A pending canvas action belongs to its original panel.
                // Cancel it before publishing the new selection to bindings.
                bool canceledEdit = EditorMode != ManualEditorMode.Select;
                _calibrationAnchors.Clear();
                EditorMode = ManualEditorMode.Select;
                if (canceledEdit && !IsBusy)
                {
                    SetStatus(GetLocalizedString(LocalizationKeys.StatusCancelled));
                }
            }

            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(SeriesCards));
                OnPropertyChanged(nameof(HasEnhancedPreview));
                OnPropertyChanged(nameof(CurrentEnhancementPreviewMode));
                SelectedPointId = value?.Points.FirstOrDefault()?.PointId;
                SelectedSeriesId = value?.SeriesCards.FirstOrDefault()?.SeriesId;
                SelectedDividerId = value?.PhaseDividers.FirstOrDefault()?.DividerId;
                Magnifier.IsCrosshairVisible = value is not null;
                Magnifier.PixelPosition = default;
                Magnifier.CrosshairPosition = new Point(0.5, 0.5);
                Magnifier.GraphPosition = null;
                Magnifier.NearestDetectionName = null;
                Magnifier.NearestDetectionConfidence = null;
                if (value is not null)
                {
                    EnsureEditHistory(value);
                }
                if (SurfaceState is not WorkspaceSurfaceState.Analyzing and not WorkspaceSurfaceState.ExportPreview)
                {
                    SurfaceState = value is null
                        ? WorkspaceSurfaceState.Empty
                        : _reviewReadyTabIds.Contains(value.TabId)
                            ? WorkspaceSurfaceState.Reviewing
                            : WorkspaceSurfaceState.Ready;
                }
                RefreshWorkspaceProjection();
                NotifyHistoryStateChanged();
                RelayCommand.RaiseCanExecuteChanged();
                AsyncRelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public MagnifierViewModel Magnifier { get; }

    public WorkspaceSurfaceState SurfaceState
    {
        get => _surfaceState;
        private set => SetProperty(ref _surfaceState, value);
    }

    public WorkspaceOperationStatus? LastOperationStatus
    {
        get => _lastOperationStatus;
        private set => SetProperty(ref _lastOperationStatus, value);
    }

    public ObservableCollection<ReviewIssueViewModel> ReviewIssues { get; }

    public ObservableCollection<FilteredMarkerCandidateViewModel> FilteredMarkers { get; }

    public FilteredMarkerCandidateViewModel? SelectedFilteredMarker
    {
        get => _selectedFilteredMarker;
        set
        {
            if (!SetProperty(ref _selectedFilteredMarker, value)) return;
            RelayCommand.RaiseCanExecuteChanged();
            if (value is null) return;
            SetSelectedReviewIssue(null, synchronizeWorkspace: false);
            SelectedPointId = null;
            HandleCanvasNavigation(new Point(value.PixelX, value.PixelY));
        }
    }

    public ReviewIssueViewModel? SelectedReviewIssue
    {
        get => _selectedReviewIssue;
        set => SetSelectedReviewIssue(value, synchronizeWorkspace: true);
    }

    public int ReviewIssueCount => ReviewIssues.Count;

    public int BlockingReviewIssueCount => ReviewIssues.Count(static issue => issue.IsBlocking);

    public bool HasReviewIssues => ReviewIssues.Count > 0;

    public ObservableCollection<DataPreviewRowViewModel> DataPreviewRows { get; }

    public ObservableCollection<string> RecentProjects { get; }

    public bool HasRecentProjects => RecentProjects.Count > 0;

    public DataPreviewRowViewModel? SelectedDataPreviewRow
    {
        get => _selectedDataPreviewRow;
        set => SetSelectedDataPreviewRow(value, synchronizeWorkspace: true);
    }

    public bool IsDataPreviewExpanded
    {
        get => _isDataPreviewExpanded;
        set => SetProperty(ref _isDataPreviewExpanded, value);
    }

    public ExportSummaryViewModel? ExportSummary
    {
        get => _exportSummary;
        private set
        {
            if (ReferenceEquals(_exportSummary, value))
            {
                return;
            }
            if (_exportSummary is not null)
            {
                _exportSummary.PropertyChanged -= HandleExportSummaryPropertyChanged;
            }
            if (SetProperty(ref _exportSummary, value))
            {
                if (_exportSummary is not null)
                {
                    _exportSummary.PropertyChanged += HandleExportSummaryPropertyChanged;
                }
                AsyncRelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanUndo => SelectedTab is not null && GetEditHistory(SelectedTab).CanUndo;

    public bool CanRedo => SelectedTab is not null && GetEditHistory(SelectedTab).CanRedo;

    public string? UndoDescription => SelectedTab is null ? null : GetEditHistory(SelectedTab).UndoDescription;

    public string? RedoDescription => SelectedTab is null ? null : GetEditHistory(SelectedTab).RedoDescription;

    public ObservableCollection<SeriesCardViewModel> SeriesCards =>
        SelectedTab?.SeriesCards ?? EmptySeriesCards;

    public WorkflowStage CurrentStage
    {
        get => _currentStage;
        private set => SetProperty(ref _currentStage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                AsyncRelayCommand.RaiseCanExecuteChanged();
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessageKey
    {
        get => _statusMessageKey;
        private set
        {
            if (SetProperty(ref _statusMessageKey, value))
            {
                _statusMessageOverride = null;
                OnPropertyChanged(nameof(StatusMessage));
            }
        }
    }

    public string StatusMessage => _statusMessageOverride ?? GetLocalizedString(StatusMessageKey);

    public string VersionText { get; }

    public string ShortCommit { get; }

    public string RuntimeModeText { get; }

    public string AvailableAutomaticStagesText { get; }

    public string MissingAutomaticStagesText { get; }

    public string EnhancementAvailabilityText { get; }

    public string AutoDetectionAvailabilityText =>
        CanRunAutomaticStage(WorkflowStage.Detect)
                ? string.Empty
                : GetLocalizedString(LocalizationKeys.WorkflowAutoDetectUnavailable);

    public bool HasEnhancedPreview => SelectedTab?.HasEnhancedPreview == true;

    public EnhancementPreviewMode CurrentEnhancementPreviewMode =>
        SelectedTab?.EnhancementPreviewMode ?? EnhancementPreviewMode.Original;

    public IReadOnlyList<LocalizedChoice<MarkerShape>> MarkerShapeChoices { get; }

    public IReadOnlyList<LocalizedChoice<MarkerFill>> MarkerFillChoices { get; }

    public IReadOnlyList<LocalizedChoice<SemanticRole>> SeriesRoleChoices { get; }

    public bool IsPhaseOverlayVisible
    {
        get => _isPhaseOverlayVisible;
        set => SetProperty(ref _isPhaseOverlayVisible, value);
    }

    public ApplicationTheme AppearanceMode
    {
        get => _appearanceMode;
        set => SetProperty(ref _appearanceMode, value);
    }

    public string? SelectedPointId
    {
        get => _selectedPointId;
        set
        {
            if (SetProperty(ref _selectedPointId, value))
            {
                DataPreviewRowViewModel? row = value is null
                    ? null
                    : DataPreviewRows.FirstOrDefault(candidate => string.Equals(
                        candidate.PointId,
                        value,
                        StringComparison.Ordinal));
                if (!ReferenceEquals(SelectedDataPreviewRow, row))
                {
                    SetSelectedDataPreviewRow(row, synchronizeWorkspace: false);
                }
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? SelectedSeriesId
    {
        get => _selectedSeriesId;
        set
        {
            if (SetProperty(ref _selectedSeriesId, value))
            {
                LoadSelectedSeriesEditor();
                RefreshSeriesRelationChoices();
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<SeriesRelationChoiceViewModel> BaselineRelationChoices { get; }

    public ObservableCollection<SeriesRelationChoiceViewModel> ProbeRelationChoices { get; }

    public string? SelectedSharedBaselineSeriesId
    {
        get => _selectedSharedBaselineSeriesId;
        set => SetProperty(ref _selectedSharedBaselineSeriesId, value);
    }

    public string? SelectedDividerId
    {
        get => _selectedDividerId;
        set
        {
            if (SetProperty(ref _selectedDividerId, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ManualEditorMode EditorMode
    {
        get => _editorMode;
        private set
        {
            if (SetProperty(ref _editorMode, value))
            {
                OnPropertyChanged(nameof(EditorInstruction));
            }
        }
    }

    public string EditorInstruction => EditorMode switch
    {
        ManualEditorMode.Calibration => FormatLocalizedString(
            LocalizationKeys.ManualInstructionCalibration,
            _calibrationAnchors.Count + 1),
        ManualEditorMode.AddPoint => GetLocalizedString(LocalizationKeys.ManualInstructionAddPoint),
        ManualEditorMode.MovePoint => GetLocalizedString(LocalizationKeys.ManualInstructionMovePoint),
        ManualEditorMode.AddPhaseDivider => GetLocalizedString(LocalizationKeys.ManualInstructionAddDivider),
        ManualEditorMode.MovePhaseDivider => GetLocalizedString(LocalizationKeys.ManualInstructionMoveDivider),
        _ => GetLocalizedString(LocalizationKeys.ManualInstructionSelect),
    };

    public double ManualYMaximum
    {
        get => _manualYMaximum;
        set
        {
            if (SetProperty(ref _manualYMaximum, value))
            {
                UpdateCalibrationPromptIfActive();
            }
        }
    }

    public int ManualFirstVisibleSession
    {
        get => _manualFirstVisibleSession;
        set
        {
            bool valueChanged = SetProperty(ref _manualFirstVisibleSession, value);
            bool textChanged = SetProperty(
                ref _manualFirstVisibleSessionText,
                value.ToString(CultureInfo.InvariantCulture),
                nameof(ManualFirstVisibleSessionText));
            if (valueChanged || textChanged)
            {
                OnPropertyChanged(nameof(IsManualFirstVisibleSessionInputValid));
                UpdateCalibrationPromptIfActive();
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ManualFirstVisibleSessionText
    {
        get => _manualFirstVisibleSessionText;
        set
        {
            string text = value ?? string.Empty;
            if (!SetProperty(ref _manualFirstVisibleSessionText, text))
            {
                return;
            }

            if (int.TryParse(
                text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed))
            {
                SetProperty(ref _manualFirstVisibleSession, parsed, nameof(ManualFirstVisibleSession));
            }

            OnPropertyChanged(nameof(IsManualFirstVisibleSessionInputValid));
            UpdateCalibrationPromptIfActive();
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsManualFirstVisibleSessionInputValid =>
        TryParsePositiveFirstVisibleSession(out int firstVisibleSession) &&
        double.IsFinite(ManualXMaximum) &&
        ManualXMaximum > firstVisibleSession;

    public double ManualXMaximum
    {
        get => _manualXMaximum;
        set
        {
            if (SetProperty(ref _manualXMaximum, value))
            {
                OnPropertyChanged(nameof(IsManualFirstVisibleSessionInputValid));
                UpdateCalibrationPromptIfActive();
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string NewSeriesName
    {
        get => _newSeriesName;
        set => SetProperty(ref _newSeriesName, value);
    }

    public MarkerShape NewSeriesShape
    {
        get => _newSeriesShape;
        set => SetProperty(ref _newSeriesShape, value);
    }

    public MarkerFill NewSeriesFill
    {
        get => _newSeriesFill;
        set => SetProperty(ref _newSeriesFill, value);
    }

    public SemanticRole NewSeriesRole
    {
        get => _newSeriesRole;
        set => SetProperty(ref _newSeriesRole, value);
    }

    public string PhaseCode
    {
        get => _phaseCode;
        set => SetProperty(ref _phaseCode, value);
    }

    public string PhaseLabel
    {
        get => _phaseLabel;
        set => SetProperty(ref _phaseLabel, value);
    }

    public ICommand ImportCommand { get; }

    public ICommand EnhanceCommand { get; }

    public ICommand AutoDetectCommand { get; }

    public ICommand ReviewCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand AddPointCommand { get; }

    public ICommand DeletePointCommand { get; }

    public ICommand PromoteFilteredMarkerCommand { get; }

    public ICommand AcceptReviewPointCommand { get; }

    public ICommand MovePointCommand { get; }

    public ICommand MergeSeriesCommand { get; }

    public ICommand SplitSeriesCommand { get; }

    public ICommand ReassignPointCommand { get; }

    public ICommand TogglePhaseOverlayCommand { get; }

    public ICommand ZoomInCommand { get; }

    public ICommand ZoomOutCommand { get; }

    public ICommand FitZoomCommand { get; }

    public ICommand ResetViewCommand { get; }

    public ICommand ShowOriginalPreviewCommand { get; }

    public ICommand ShowEnhancedPreviewCommand { get; }

    public ICommand ShowComparisonPreviewCommand { get; }

    public ICommand NextTabCommand { get; }

    public ICommand PreviousTabCommand { get; }

    public ICommand OpenProjectCommand { get; }

    public ICommand OpenRecentProjectCommand { get; }

    public ICommand SaveProjectCommand { get; }

    public ICommand SaveProjectAsCommand { get; }

    public ICommand RecoverProjectCommand { get; }

    public ICommand CloseTabCommand { get; }

    public ICommand StartCalibrationCommand { get; }

    public ICommand SelectEditorModeCommand { get; }

    public ICommand CreateSeriesCommand { get; }

    public ICommand EditSeriesCommand { get; }

    public ICommand ApplySeriesRelationsCommand { get; }

    public ICommand BeginAddPointCommand { get; }

    public ICommand BeginAddFilledPointCommand { get; }

    public ICommand BeginAddOpenPointCommand { get; }

    public ICommand BeginMovePointCommand { get; }

    public ICommand BeginAddPhaseDividerCommand { get; }

    public ICommand BeginMovePhaseDividerCommand { get; }

    public ICommand DeletePhaseDividerCommand { get; }

    public ICommand LabelPhaseDividerCommand { get; }

    public ICommand UndoCommand { get; }

    public ICommand RedoCommand { get; }

    public ICommand OpenExportPreviewCommand { get; }

    public ICommand ConfirmExportCommand { get; }

    public ICommand SelectReviewIssueCommand { get; }

    public ICommand SelectDataPreviewRowCommand { get; }

    private static ObservableCollection<SeriesCardViewModel> EmptySeriesCards { get; } = [];

    public Task ImportPathsAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] distinctPaths = paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return distinctPaths.Length == 0
            ? Task.CompletedTask
            : ImportPathsCoreAsync(distinctPaths, cancellationToken);
    }

    private bool CanPromoteFilteredMarker() =>
        !IsBusy && _manualWorkspaceService is not null && SelectedTab?.Calibration is not null &&
        SelectedFilteredMarker is { } candidate && FilteredMarkers.Contains(candidate) &&
        SelectedSeriesId is { } seriesId && FindSeries(seriesId) is not null;

    private void PromoteFilteredMarker()
    {
        if (!CanPromoteFilteredMarker()) return;
        WorkspaceTabViewModel tab = SelectedTab!;
        string seriesId = SelectedSeriesId!;
        string markerId = SelectedFilteredMarker!.MarkerId;
        WorkspaceTabSnapshot before = CaptureTabSnapshot(tab);
        try
        {
            GraphPoint point = _manualWorkspaceService!.PromoteFilteredMarker(tab.TabId, markerId, seriesId);
            SelectedPointId = point.PointId;
            RecordManualEdit(tab, LocalizationKeys.EditAddPoint, before);
            QueueAutosave(SnapshotTrigger.PointEdited, tab.TabId, point.PointId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            SetOperationFailure(exception, LocalizationKeys.ManualEditRejected, "MANUAL_MARKER_PROMOTION_REJECTED", "review-inputs");
        }
    }

    private bool CanAcceptReviewPoint()
    {
        if (IsBusy || _manualWorkspaceService is null || SelectedTab is not { } tab ||
            SelectedReviewIssue is not { Kind: ReviewIssueKind.Point, EntityId: { } pointId } issue ||
            !string.Equals(issue.TabId, tab.TabId, StringComparison.Ordinal) ||
            !string.Equals(issue.IssueId, $"point-unreviewed-{pointId}", StringComparison.Ordinal) ||
            !Guid.TryParse(tab.PanelId, out Guid panelId) || !Guid.TryParse(pointId, out Guid id))
            return false;
        return _manualWorkspaceService.CurrentProject.Panels
            .SingleOrDefault(panel => panel.PanelId.Value == panelId)?.Points
            .Any(point => point.PointId.Value == id && point.ReviewStatus == ReviewStatus.Unreviewed) == true &&
            FindPoint(pointId) is not null;
    }

    private void AcceptReviewPoint()
    {
        if (!CanAcceptReviewPoint()) return;
        WorkspaceTabViewModel tab = SelectedTab!;
        string pointId = SelectedReviewIssue!.EntityId!;
        IManualWorkspaceService service = _manualWorkspaceService!;
        bool wasDirty = tab.IsDirty;
        service.SetPointReviewStatus(tab.TabId, pointId, ReviewStatus.Accepted);
        GetEditHistory(tab).Record(GetLocalizedString(LocalizationKeys.EditAcceptPoint),
            () =>
            {
                service.SetPointReviewStatus(tab.TabId, pointId, ReviewStatus.Unreviewed);
                tab.IsDirty = wasDirty;
            },
            () =>
            {
                service.SetPointReviewStatus(tab.TabId, pointId, ReviewStatus.Accepted);
                tab.IsDirty = true;
            });
        tab.IsDirty = true;
        RefreshWorkspaceProjection();
        NotifyHistoryStateChanged();
        if (SelectedReviewIssue is { } next) SetSelectedReviewIssue(next, synchronizeWorkspace: true);
        QueueAutosave(SnapshotTrigger.PointEdited, tab.TabId, pointId);
    }

    public void AddPoint(string seriesId)
    {
        WorkspaceTabViewModel historyTab = RequireSelectedTab();
        WorkspaceTabSnapshot before = CaptureTabSnapshot(historyTab);
        if (_manualWorkspaceService is not null && SelectedTab is { } manualTab)
        {
            GraphPoint added = _manualWorkspaceService.AddPoint(
                manualTab.TabId,
                seriesId,
                Magnifier.PixelPosition.X,
                Magnifier.PixelPosition.Y);
            SelectedPointId = added.PointId;
            SelectedSeriesId = seriesId;
            RecordManualEdit(manualTab, LocalizationKeys.EditAddPoint, before);
            QueueAutosave(SnapshotTrigger.PointEdited, manualTab.TabId, added.PointId);
            return;
        }

        SeriesCardViewModel series = RequireSeries(seriesId);
        WorkspaceTabViewModel tab = RequireSelectedTab();
        GraphPoint point = new(
            $"manual-point-{_nextManualPointId++:D4}",
            seriesId,
            Magnifier.PixelPosition.X,
            Magnifier.PixelPosition.Y,
            Magnifier.GraphPosition?.X ?? 0,
            Magnifier.GraphPosition?.Y ?? 0,
            "unknown");
        tab.Points.Add(point);
        SelectedPointId = point.PointId;
        series.NotifyCountChanged();
        RecordManualEdit(tab, LocalizationKeys.EditAddPoint, before);
        RefreshWorkspaceProjection();
    }

    public void DeletePoint(string pointId)
    {
        WorkspaceTabViewModel historyTab = RequireSelectedTab();
        WorkspaceTabSnapshot before = CaptureTabSnapshot(historyTab);
        if (_manualWorkspaceService is not null && SelectedTab is { } manualTab)
        {
            GraphPoint manualPoint = RequirePoint(pointId);
            _manualWorkspaceService.DeletePoint(manualTab.TabId, pointId);
            if (SelectedPointId == pointId)
            {
                SelectedPointId = manualTab.Points.FirstOrDefault()?.PointId;
            }

            FindSeries(manualPoint.SeriesId)?.NotifyCountChanged();
            RecordManualEdit(manualTab, LocalizationKeys.EditDeletePoint, before);
            QueueAutosave(SnapshotTrigger.PointEdited, manualTab.TabId, pointId);
            return;
        }

        WorkspaceTabViewModel tab = RequireSelectedTab();
        GraphPoint point = RequirePoint(pointId);
        string seriesId = point.SeriesId;
        tab.Points.Remove(point);
        if (SelectedPointId == pointId)
        {
            SelectedPointId = tab.Points.FirstOrDefault()?.PointId;
        }

        FindSeries(seriesId)?.NotifyCountChanged();
        RecordManualEdit(tab, LocalizationKeys.EditDeletePoint, before);
        RefreshWorkspaceProjection();
    }

    public void MovePoint(string pointId, double pixelX, double pixelY)
    {
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelX));
        }

        WorkspaceTabViewModel historyTab = RequireSelectedTab();
        WorkspaceTabSnapshot before = CaptureTabSnapshot(historyTab);
        if (_manualWorkspaceService is not null && SelectedTab is { } manualTab)
        {
            _manualWorkspaceService.MovePoint(manualTab.TabId, pointId, pixelX, pixelY);
            SelectedPointId = pointId;
            RecordManualEdit(manualTab, LocalizationKeys.EditMovePoint, before);
            QueueAutosave(SnapshotTrigger.PointEdited, manualTab.TabId, pointId);
            return;
        }

        GraphPoint point = RequirePoint(pointId);
        point.PixelX = pixelX;
        point.PixelY = pixelY;
        SelectedPointId = point.PointId;
        FindSeries(point.SeriesId)?.NotifyCountChanged();
        RecordManualEdit(historyTab, LocalizationKeys.EditMovePoint, before);
        RefreshWorkspaceProjection();
    }

    public void MergeSeries(string sourceSeriesId, string targetSeriesId)
    {
        if (sourceSeriesId == targetSeriesId)
        {
            throw new ArgumentException("Source and target series must differ.", nameof(targetSeriesId));
        }

        WorkspaceTabViewModel tab = RequireSelectedTab();
        WorkspaceTabSnapshot before = CaptureTabSnapshot(tab);
        SeriesCardViewModel source = RequireSeries(sourceSeriesId);
        SeriesCardViewModel target = RequireSeries(targetSeriesId);
        foreach (GraphPoint point in tab.Points.Where(point => point.SeriesId == sourceSeriesId))
        {
            point.SeriesId = targetSeriesId;
        }

        source.NotifyCountChanged();
        target.NotifyCountChanged();
        tab.SeriesCards.Remove(source);
        RecordManualEdit(tab, LocalizationKeys.EditMergeSeries, before);
        RefreshWorkspaceProjection();
    }

    public void SplitSeries(string sourceSeriesId, IReadOnlyCollection<string> pointIds)
    {
        ArgumentNullException.ThrowIfNull(pointIds);
        if (pointIds.Count == 0)
        {
            throw new ArgumentException("At least one point is required.", nameof(pointIds));
        }

        WorkspaceTabViewModel tab = RequireSelectedTab();
        WorkspaceTabSnapshot before = CaptureTabSnapshot(tab);
        SeriesCardViewModel source = RequireSeries(sourceSeriesId);
        HashSet<string> selectedIds = pointIds.ToHashSet(StringComparer.Ordinal);
        GraphPoint[] selectedPoints = tab.Points
            .Where(point => selectedIds.Contains(point.PointId) && point.SeriesId == sourceSeriesId)
            .ToArray();
        if (selectedPoints.Length != selectedIds.Count)
        {
            throw new ArgumentException("Every split point must belong to the source series.", nameof(pointIds));
        }

        string createdSeriesId = $"series-split-{_nextSplitSeriesId++:D2}";
        SeriesCardViewModel created = new(
            createdSeriesId,
            "◆",
            GetLocalizedString(LocalizationKeys.SeriesSplitSymbolName),
            GetLocalizedString(LocalizationKeys.SeriesSplitLabel),
            source.Confidence,
            tab.Points);
        tab.SeriesCards.Add(created);
        foreach (GraphPoint point in selectedPoints)
        {
            point.SeriesId = createdSeriesId;
        }

        source.NotifyCountChanged();
        created.NotifyCountChanged();
        ConfigureSeriesSelection(created);
        RecordManualEdit(tab, LocalizationKeys.EditSplitSeries, before);
        RefreshWorkspaceProjection();
    }

    private string GetLocalizedString(string key) =>
        _localizationService?.GetString(key) ?? key;

    private LocalizedChoice<T> Choice<T>(T value, string key)
        where T : struct, Enum =>
        new(value, GetLocalizedString(key));

    private string FormatLocalizedString(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, GetLocalizedString(key), arguments);

    public void ReassignPoint(string pointId, string targetSeriesId)
    {
        GraphPoint existingPoint = RequirePoint(pointId);
        if (string.Equals(existingPoint.SeriesId, targetSeriesId, StringComparison.Ordinal))
        {
            return;
        }
        WorkspaceTabViewModel historyTab = RequireSelectedTab();
        WorkspaceTabSnapshot before = CaptureTabSnapshot(historyTab);
        if (_manualWorkspaceService is not null && SelectedTab is { } manualTab)
        {
            _manualWorkspaceService.ReassignPoint(manualTab.TabId, pointId, targetSeriesId);
            SelectedPointId = pointId;
            SelectedSeriesId = targetSeriesId;
            RecordManualEdit(manualTab, LocalizationKeys.EditReassignPoint, before);
            QueueAutosave(SnapshotTrigger.PointEdited, manualTab.TabId, pointId);
            return;
        }

        GraphPoint point = RequirePoint(pointId);
        SeriesCardViewModel target = RequireSeries(targetSeriesId);
        SelectedPointId = pointId;
        string sourceSeriesId = point.SeriesId;
        if (sourceSeriesId == targetSeriesId)
        {
            target.NotifyCountChanged();
            return;
        }

        point.SeriesId = targetSeriesId;
        FindSeries(sourceSeriesId)?.NotifyCountChanged();
        target.NotifyCountChanged();
        RecordManualEdit(historyTab, LocalizationKeys.EditReassignPoint, before);
        RefreshWorkspaceProjection();
    }

    public async Task HandleCanvasPointAsync(Point point, CancellationToken cancellationToken = default)
    {
        if (SelectedTab is not { } tab || _manualWorkspaceService is null)
        {
            return;
        }

        if (EditorMode == ManualEditorMode.Calibration && !IsManualFirstVisibleSessionInputValid)
        {
            SetStatus(GetLocalizedString(LocalizationKeys.ManualFirstVisibleSessionInvalid));
            return;
        }

        HandleCanvasNavigation(point);
        WorkspaceTabSnapshot? before = EditorMode == ManualEditorMode.Select
            ? null
            : CaptureTabSnapshot(tab);

        try
        {
            switch (EditorMode)
            {
                case ManualEditorMode.Calibration:
                _calibrationAnchors.Add(point);
                OnPropertyChanged(nameof(EditorInstruction));
                if (_calibrationAnchors.Count == 3)
                {
                    _manualWorkspaceService.Calibrate(
                        tab.TabId,
                        new ManualCalibrationRequest(
                            new GraphReader.Axis.PixelPoint(_calibrationAnchors[0].X, _calibrationAnchors[0].Y),
                            new GraphReader.Axis.PixelPoint(_calibrationAnchors[1].X, _calibrationAnchors[1].Y),
                            new GraphReader.Axis.PixelPoint(_calibrationAnchors[2].X, _calibrationAnchors[2].Y),
                            ManualYMaximum,
                            ManualXMaximum,
                            ManualFirstVisibleSession));
                    EditorMode = ManualEditorMode.Select;
                    SetStatus(FormatLocalizedString(
                        LocalizationKeys.ManualCalibrationSaved,
                        ManualFirstVisibleSession,
                        ManualXMaximum));
                    RecordManualEdit(tab, LocalizationKeys.EditCalibrate, before!);
                    await TryAutosaveAsync(SnapshotTrigger.CalibrationChanged, tab.TabId, null, cancellationToken);
                }
                    break;
                case ManualEditorMode.AddPoint:
                if (SelectedSeriesId is null)
                {
                    SetStatus(GetLocalizedString(LocalizationKeys.ManualSelectSeriesFirst));
                    break;
                }

                GraphPoint added = _manualWorkspaceService.AddPoint(
                    tab.TabId,
                    SelectedSeriesId,
                    point.X,
                    point.Y);
                SelectedPointId = added.PointId;
                EditorMode = ManualEditorMode.Select;
                SetStatus(GetLocalizedString(LocalizationKeys.ManualPointAdded));
                RecordManualEdit(tab, LocalizationKeys.EditAddPoint, before!);
                await TryAutosaveAsync(SnapshotTrigger.PointEdited, tab.TabId, added.PointId, cancellationToken);
                    break;
                case ManualEditorMode.MovePoint:
                if (SelectedPointId is not null)
                {
                    _manualWorkspaceService.MovePoint(tab.TabId, SelectedPointId, point.X, point.Y);
                    EditorMode = ManualEditorMode.Select;
                    SetStatus(GetLocalizedString(LocalizationKeys.ManualPointMoved));
                    RecordManualEdit(tab, LocalizationKeys.EditMovePoint, before!);
                    await TryAutosaveAsync(SnapshotTrigger.PointEdited, tab.TabId, SelectedPointId, cancellationToken);
                }
                    break;
                case ManualEditorMode.AddPhaseDivider:
                EditablePhaseDivider divider = _manualWorkspaceService.AddPhaseDivider(
                    tab.TabId,
                    point.X,
                    PhaseCode,
                    PhaseLabel);
                SelectedDividerId = divider.DividerId;
                EditorMode = ManualEditorMode.Select;
                SetStatus(GetLocalizedString(LocalizationKeys.ManualDividerAdded));
                RecordManualEdit(tab, LocalizationKeys.EditAddPhaseDivider, before!);
                await TryAutosaveAsync(SnapshotTrigger.PhaseEdited, tab.TabId, divider.DividerId, cancellationToken);
                    break;
                case ManualEditorMode.MovePhaseDivider:
                if (SelectedDividerId is not null)
                {
                    _manualWorkspaceService.MovePhaseDivider(tab.TabId, SelectedDividerId, point.X);
                    EditorMode = ManualEditorMode.Select;
                    SetStatus(GetLocalizedString(LocalizationKeys.ManualDividerMoved));
                    RecordManualEdit(tab, LocalizationKeys.EditMovePhaseDivider, before!);
                    await TryAutosaveAsync(SnapshotTrigger.PhaseEdited, tab.TabId, SelectedDividerId, cancellationToken);
                }
                    break;
                default:
                    SelectNearest(tab, point);
                    break;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _calibrationAnchors.Clear();
            OnPropertyChanged(nameof(EditorInstruction));
            EditorMode = ManualEditorMode.Select;
            SetOperationFailure(
                exception,
                LocalizationKeys.ManualEditRejected,
                "MANUAL_EDIT_REJECTED",
                "review-input");
        }
    }

    public void HandleCanvasNavigation(Point point)
    {
        if (SelectedTab is not { } tab)
        {
            return;
        }

        Magnifier.PixelPosition = point;
        Magnifier.CrosshairPosition = new Point(
            tab.PixelWidth > 0 ? Math.Clamp(point.X / tab.PixelWidth, 0, 1) : 0,
            tab.PixelHeight > 0 ? Math.Clamp(point.Y / tab.PixelHeight, 0, 1) : 0);
        if (tab.Calibration is { } calibration)
        {
            Magnifier.GraphPosition = new Point(
                calibration.XTransform.PixelToGraph(point.X),
                calibration.YTransform.PixelToGraph(point.Y));
        }
        else
        {
            Magnifier.GraphPosition = null;
        }
    }

    private async Task ImportImagesFromDialogAsync(CancellationToken cancellationToken)
    {
        if (_manualWorkspaceService is null || _dialogService is null)
        {
            await RunWorkflowStageAsync(WorkflowStage.Import, "Workflow.Import", cancellationToken);
            return;
        }

        IReadOnlyList<string> paths = _dialogService.SelectImages();
        if (paths.Count == 0)
        {
            return;
        }

        await ImportPathsCoreAsync(paths, cancellationToken);
    }

    private async Task ImportPathsCoreAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (_manualWorkspaceService is null)
        {
            return;
        }

        await RunBusyAsync(async token =>
        {
            try
            {
                IReadOnlyList<WorkspaceTabViewModel> imported =
                    await _manualWorkspaceService.ImportImagesAsync(paths, token);
                AddImportedTabs(imported);

                SelectedTab = imported.Count > 0 ? imported[^1] : SelectedTab;
                CurrentStage = WorkflowStage.Import;
                if (SelectedTab is not null)
                {
                    _reviewReadyTabIds.Remove(SelectedTab.TabId);
                    SurfaceState = WorkspaceSurfaceState.Ready;
                }
                RefreshWorkspaceProjection();
                SetStatus(_manualWorkspaceService.LastImportErrors.Count == 0
                    ? FormatLocalizedString(LocalizationKeys.StatusImportedFormat, imported.Count)
                    : FormatLocalizedString(
                        LocalizationKeys.StatusImportFailuresFormat,
                        imported.Count,
                        _manualWorkspaceService.LastImportErrors.Count));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                AddImportedTabs(_manualWorkspaceService.CreateWorkspace());
                CurrentStage = WorkflowStage.Import;
                throw;
            }
        }, cancellationToken);
    }

    private void AddImportedTabs(IEnumerable<WorkspaceTabViewModel> imported)
    {
        var existingTabIds = Tabs.Select(static tab => tab.TabId).ToHashSet(StringComparer.Ordinal);
        WorkspaceTabViewModel? lastAdded = null;
        foreach (WorkspaceTabViewModel tab in imported)
        {
            if (!existingTabIds.Add(tab.TabId))
            {
                continue;
            }

            EnsureManualOverlay(tab);
            ConfigureSeriesSelection(tab);
            EnsureEditHistory(tab);
            Tabs.Add(tab);
            lastAdded = tab;
        }

        if (lastAdded is not null)
        {
            SelectedTab = lastAdded;
        }
    }

    private async Task EnhanceSelectedTabAsync(CancellationToken cancellationToken)
    {
        if (_manualWorkspaceService is null || SelectedTab is not { } tab)
        {
            return;
        }

        await RunBusyAsync(async token =>
        {
            WorkspaceEnhancementResult result = await _manualWorkspaceService
                .EnhanceAsync(tab.TabId, token);
            SetStatus(result.UserMessageKey is null
                ? result.Message
                : FormatLocalizedString(
                    result.UserMessageKey,
                    result.MessageArguments?.ToArray() ?? []));
            if (!result.Succeeded)
            {
                return;
            }

            CurrentStage = WorkflowStage.Prepare;
            Magnifier.IsEnhanced = true;
            OnPropertyChanged(nameof(HasEnhancedPreview));
            OnPropertyChanged(nameof(CurrentEnhancementPreviewMode));
            RelayCommand.RaiseCanExecuteChanged();
            QueueAutosave(SnapshotTrigger.PointEdited, tab.TabId, "enhancement");
        }, cancellationToken);
    }

    private void SetEnhancementPreviewMode(EnhancementPreviewMode mode)
    {
        if (_manualWorkspaceService is null || SelectedTab is not { } tab)
        {
            return;
        }

        _manualWorkspaceService.SetEnhancementPreviewMode(tab.TabId, mode);
        Magnifier.IsEnhanced = mode != EnhancementPreviewMode.Original;
        OnPropertyChanged(nameof(CurrentEnhancementPreviewMode));
        QueueAutosave(SnapshotTrigger.ExportSettingsChanged, tab.TabId, "enhancement-preview");
    }

    private async Task OpenProjectFromDialogAsync(CancellationToken cancellationToken)
    {
        if (_manualWorkspaceService is null || _dialogService?.SelectProjectToOpen() is not string path)
        {
            return;
        }

        await OpenProjectPathAsync(path, cancellationToken);
    }

    private async Task OpenProjectPathAsync(string path, CancellationToken cancellationToken)
    {
        if (_manualWorkspaceService is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await RunBusyAsync(async token =>
        {
            IReadOnlyList<WorkspaceTabViewModel> opened =
                await _manualWorkspaceService.OpenProjectAsync(path, token);
            Tabs.Clear();
            _editHistories.Clear();
            _reviewReadyTabIds.Clear();
            foreach (WorkspaceTabViewModel tab in opened)
            {
                EnsureManualOverlay(tab);
                ConfigureSeriesSelection(tab);
                EnsureEditHistory(tab);
                if (tab.Points.Count > 0 || tab.Calibration is not null)
                {
                    _reviewReadyTabIds.Add(tab.TabId);
                }
                Tabs.Add(tab);
            }

            SelectedTab = Tabs.FirstOrDefault();
            SurfaceState = SelectedTab is null
                ? WorkspaceSurfaceState.Empty
                : _reviewReadyTabIds.Contains(SelectedTab.TabId)
                    ? WorkspaceSurfaceState.Reviewing
                    : WorkspaceSurfaceState.Ready;
            RefreshWorkspaceProjection();
            RememberRecentProject(path);
            SetStatus(FormatLocalizedString(LocalizationKeys.StatusOpenedFormat, Path.GetFileName(path)));
        }, cancellationToken);
    }

    private async Task SaveProjectFromDialogAsync(bool saveAs, CancellationToken cancellationToken)
    {
        if (_manualWorkspaceService is null || _dialogService is null)
        {
            return;
        }

        string? path = saveAs || _manualWorkspaceService.CurrentProjectPath is null
            ? _dialogService.SelectProjectToSave(_manualWorkspaceService.CurrentProjectPath)
            : _manualWorkspaceService.CurrentProjectPath;
        if (path is null)
        {
            return;
        }

        await RunBusyAsync(async token =>
        {
            DomainResult<ProjectSaveReceipt> saved =
                await _manualWorkspaceService.SaveProjectAsync(path, token);
            if (!saved.IsSuccess)
            {
                throw new InvalidOperationException(string.Join(" | ", saved.Errors.Select(error => error.TechnicalMessage)));
            }

            foreach (WorkspaceTabViewModel tab in Tabs)
            {
                tab.IsDirty = false;
            }
            RememberRecentProject(saved.Value!.Path);
            SetStatus(FormatLocalizedString(LocalizationKeys.StatusSavedFormat, Path.GetFileName(saved.Value!.Path)));
        }, cancellationToken);
    }

    private async Task RecoverProjectFromAutosaveAsync(CancellationToken cancellationToken)
    {
        if (_manualWorkspaceService is null || _dialogService is null)
        {
            return;
        }

        string suggestedPath = _manualWorkspaceService.CurrentProjectPath is { } currentPath
            ? Path.Combine(
                Path.GetDirectoryName(currentPath) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(currentPath)}-recovered.garproj")
            : "graph-project-recovered.garproj";
        string? destinationPath = _dialogService.SelectProjectToSave(suggestedPath);
        if (destinationPath is null)
        {
            return;
        }

        await RunBusyAsync(async token =>
        {
            DomainResult<ProjectSaveReceipt> recovered = await _manualWorkspaceService
                .RecoverLatestToNewFileAsync(destinationPath, token);
            if (!recovered.IsSuccess || recovered.Value is null)
            {
                throw new InvalidOperationException(
                    string.Join(" | ", recovered.Errors.Select(error => error.TechnicalMessage)));
            }

            IReadOnlyList<WorkspaceTabViewModel> opened = await _manualWorkspaceService
                .OpenProjectAsync(recovered.Value.Path, token);
            Tabs.Clear();
            _editHistories.Clear();
            _reviewReadyTabIds.Clear();
            foreach (WorkspaceTabViewModel tab in opened)
            {
                EnsureManualOverlay(tab);
                ConfigureSeriesSelection(tab);
                EnsureEditHistory(tab);
                if (tab.Points.Count > 0 || tab.Calibration is not null)
                {
                    _reviewReadyTabIds.Add(tab.TabId);
                }
                Tabs.Add(tab);
            }

            SelectedTab = Tabs.FirstOrDefault();
            RememberRecentProject(recovered.Value.Path);
            SetStatus(FormatLocalizedString(
                LocalizationKeys.StatusRecoveredFormat,
                Path.GetFileName(recovered.Value.Path)));
        }, cancellationToken);
    }

    private async Task ExportFromDialogAsync(CancellationToken cancellationToken)
    {
        if (_manualWorkspaceService is null || _dialogService is null)
        {
            await RunWorkflowStageAsync(WorkflowStage.Export, "Workflow.Export", cancellationToken);
            return;
        }

        if (SelectedTab is not { } tab || _dialogService.SelectExportDirectory() is not string directory)
        {
            return;
        }

        await RunBusyAsync(async token =>
        {
            GraphReader.Export.ExportResult exported =
                await _manualWorkspaceService.ExportAsync(tab.TabId, directory, token);
            if (!exported.Succeeded)
            {
                SetOperationFailure(exported.Failures);
                return;
            }

            CurrentStage = WorkflowStage.Export;
            SurfaceState = WorkspaceSurfaceState.ExportPreview;
            ExportSummary = CreateExportSummary(
                directory,
                exported.MinimalArtifacts.Select(static artifact => artifact.FileName)
                    .Concat(exported.AuditArtifacts.Select(static artifact => artifact.FileName)));
            SetStatus(FormatLocalizedString(
                LocalizationKeys.StatusExportedFormat,
                exported.MinimalArtifacts.Count + exported.AuditArtifacts.Count));
        }, cancellationToken);
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        _activeOperation?.Dispose();
        _activeOperation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        IsBusy = true;
        try
        {
            await action(_activeOperation.Token);
        }
        catch (OperationCanceledException) when (_activeOperation.IsCancellationRequested)
        {
            SetStatus(GetLocalizedString(LocalizationKeys.StatusCancelled));
        }
        catch (Exception exception)
        {
            SetOperationFailure(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BeginCalibration()
    {
        if (!CanStartCalibration())
        {
            return;
        }

        _calibrationAnchors.Clear();
        EditorMode = ManualEditorMode.Calibration;
        UpdateCalibrationPromptIfActive();
    }

    private void UpdateCalibrationPromptIfActive()
    {
        if (EditorMode == ManualEditorMode.Calibration)
        {
            if (_calibrationAnchors.Count > 0)
            {
                _calibrationAnchors.Clear();
                OnPropertyChanged(nameof(EditorInstruction));
            }

            if (!IsManualFirstVisibleSessionInputValid)
            {
                SetStatus(GetLocalizedString(LocalizationKeys.ManualFirstVisibleSessionInvalid));
                return;
            }

            SetStatus(FormatLocalizedString(
                LocalizationKeys.ManualCalibrationPrompt,
                ManualFirstVisibleSession,
                ManualXMaximum,
                ManualYMaximum));
        }
    }

    private void CreateSeries()
    {
        if (SelectedTab is not { } tab || _manualWorkspaceService is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(NewSeriesName))
        {
            SetStatus(GetLocalizedString(LocalizationKeys.ManualSeriesNameRequired));
            return;
        }

        try
        {
            WorkspaceTabSnapshot before = CaptureTabSnapshot(tab);
            string symbol = SymbolFor(NewSeriesShape, NewSeriesFill);
            SeriesCardViewModel series = _manualWorkspaceService.AddSeries(
                tab.TabId,
                new ManualSeriesDefinition(NewSeriesName, symbol, NewSeriesShape, NewSeriesFill, NewSeriesRole));
            ConfigureSeriesSelection(series);
            SelectedSeriesId = series.SeriesId;
            RecordManualEdit(tab, LocalizationKeys.EditCreateSeries, before);
            SetStatus(FormatLocalizedString(LocalizationKeys.ManualSeriesCreatedFormat, series.Label));
            QueueAutosave(SnapshotTrigger.PointEdited, tab.TabId, series.SeriesId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            SetOperationFailure(
                exception,
                LocalizationKeys.ManualEditRejected,
                "MANUAL_EDIT_REJECTED",
                "review-input");
        }
    }

    private void EditSelectedSeries()
    {
        if (SelectedTab is not { } tab ||
            SelectedSeriesId is not string seriesId ||
            _manualWorkspaceService is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(NewSeriesName))
        {
            SetStatus(GetLocalizedString(LocalizationKeys.ManualSeriesNameRequired));
            return;
        }

        try
        {
            WorkspaceTabSnapshot before = CaptureTabSnapshot(tab);
            _manualWorkspaceService.UpdateSeries(
                tab.TabId,
                seriesId,
                new ManualSeriesDefinition(
                    NewSeriesName,
                    SymbolFor(NewSeriesShape, NewSeriesFill),
                    NewSeriesShape,
                    NewSeriesFill,
                    NewSeriesRole));
            RecordManualEdit(tab, LocalizationKeys.EditEditSeries, before);
            SetStatus(FormatLocalizedString(LocalizationKeys.ManualSeriesSelectedFormat, NewSeriesName));
            QueueAutosave(SnapshotTrigger.PointEdited, tab.TabId, seriesId);
            RefreshSeriesRelationChoices();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            SetOperationFailure(
                exception,
                LocalizationKeys.ManualEditRejected,
                "MANUAL_EDIT_REJECTED",
                "review-input");
        }
    }

    private bool CanEditSelectedSeries() =>
        _manualWorkspaceService is not null &&
        SelectedTab is not null &&
        SelectedSeriesId is not null;

    private void LoadSelectedSeriesEditor()
    {
        if (FindSeries(SelectedSeriesId ?? string.Empty) is not { } series)
        {
            return;
        }

        NewSeriesName = series.Label;
        NewSeriesShape = series.Shape;
        NewSeriesFill = series.Fill;
        NewSeriesRole = series.SemanticRole;
    }

    private bool HasSeriesWithFill(MarkerFill fill) =>
        _manualWorkspaceService is not null &&
        SelectedTab?.SeriesCards.Any(series => series.Fill == fill) == true;

    private void BeginAddPointForFill(MarkerFill fill)
    {
        SeriesCardViewModel? series = SelectedTab?.SeriesCards.FirstOrDefault(candidate =>
            candidate.Fill == fill && string.Equals(candidate.SeriesId, SelectedSeriesId, StringComparison.Ordinal))
            ?? SelectedTab?.SeriesCards.FirstOrDefault(candidate => candidate.Fill == fill);
        if (series is null)
        {
            SetStatus(GetLocalizedString(LocalizationKeys.ManualSelectSeriesFirst));
            return;
        }

        SelectedSeriesId = series.SeriesId;
        EditorMode = ManualEditorMode.AddPoint;
    }

    private void ApplySeriesRelations()
    {
        if (SelectedTab is not { } tab ||
            SelectedSeriesId is not string interventionSeriesId ||
            _manualWorkspaceService is null)
        {
            return;
        }

        try
        {
            WorkspaceTabSnapshot before = CaptureTabSnapshot(tab);
            string[] probeSeriesIds = ProbeRelationChoices
                .Where(static choice => choice.IsSelected && choice.SeriesId is not null)
                .Select(static choice => choice.SeriesId!)
                .ToArray();
            _manualWorkspaceService.SetSeriesRelations(
                tab.TabId,
                interventionSeriesId,
                SelectedSharedBaselineSeriesId,
                probeSeriesIds);
            RecordManualEdit(tab, LocalizationKeys.ManualApplySeriesRelations, before);
            SetStatus(GetLocalizedString(LocalizationKeys.ManualRelationsApplied));
            QueueAutosave(SnapshotTrigger.ExportSettingsChanged, tab.TabId, interventionSeriesId);
            RefreshSeriesRelationChoices();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            SetOperationFailure(
                exception,
                LocalizationKeys.ManualEditRejected,
                "MANUAL_EDIT_REJECTED",
                "review-input");
        }
    }

    private bool CanApplySeriesRelations() =>
        _manualWorkspaceService is not null &&
        SelectedTab is not null &&
        SelectedSeriesId is string seriesId &&
        FindSeries(seriesId)?.SemanticRole == SemanticRole.Intervention;

    private void RefreshSeriesRelationChoices()
    {
        BaselineRelationChoices.Clear();
        ProbeRelationChoices.Clear();
        _selectedSharedBaselineSeriesId = null;

        if (_manualWorkspaceService is null ||
            SelectedTab is not { } tab ||
            SelectedSeriesId is not string selectedSeriesId ||
            FindSeries(selectedSeriesId)?.SemanticRole != SemanticRole.Intervention)
        {
            OnPropertyChanged(nameof(SelectedSharedBaselineSeriesId));
            return;
        }

        SeriesRecord? persisted = _manualWorkspaceService.CurrentProject.Panels
            .Where(panel => string.Equals(panel.PanelId.Value.ToString("D"), tab.PanelId, StringComparison.Ordinal))
            .SelectMany(static panel => panel.Series)
            .FirstOrDefault(series => string.Equals(
                series.SeriesId.Value.ToString("D"),
                selectedSeriesId,
                StringComparison.Ordinal));

        BaselineRelationChoices.Add(new SeriesRelationChoiceViewModel(
            null,
            GetLocalizedString(LocalizationKeys.ManualNoSharedBaseline)));
        foreach (SeriesCardViewModel series in tab.SeriesCards.Where(static item => item.SemanticRole == SemanticRole.Baseline))
        {
            BaselineRelationChoices.Add(new SeriesRelationChoiceViewModel(series.SeriesId, series.Label));
        }

        _selectedSharedBaselineSeriesId = persisted?.SharedBaselineSeriesId?.Value.ToString("D");
        OnPropertyChanged(nameof(SelectedSharedBaselineSeriesId));
        HashSet<string> selectedProbeIds = persisted?.ApplicableProbeSeriesIds
            .Select(static id => id.Value.ToString("D"))
            .ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (SeriesCardViewModel series in tab.SeriesCards.Where(static item =>
                     item.SemanticRole is SemanticRole.Maintenance or SemanticRole.Generalization))
        {
            ProbeRelationChoices.Add(new SeriesRelationChoiceViewModel(
                series.SeriesId,
                series.Label,
                selectedProbeIds.Contains(series.SeriesId)));
        }
    }

    private void CloseTab(WorkspaceTabViewModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab))
        {
            return;
        }

        if (tab.IsDirty)
        {
            SetStatus(GetLocalizedString(LocalizationKeys.ProjectCloseDirtyBlocked));
            return;
        }

        int index = Tabs.IndexOf(tab);
        if (_manualWorkspaceService is not null)
        {
            _manualWorkspaceService.CloseTab(tab.TabId);
        }

        Tabs.Remove(tab);
        _editHistories.Remove(tab.TabId);
        _reviewReadyTabIds.Remove(tab.TabId);
        SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void DeleteSelectedPhaseDivider()
    {
        if (SelectedTab is not { } tab || SelectedDividerId is not string dividerId || _manualWorkspaceService is null)
        {
            return;
        }

        WorkspaceTabSnapshot before = CaptureTabSnapshot(tab);
        _manualWorkspaceService.DeletePhaseDivider(tab.TabId, dividerId);
        SelectedDividerId = tab.PhaseDividers.FirstOrDefault()?.DividerId;
        RecordManualEdit(tab, LocalizationKeys.EditDeletePhaseDivider, before);
        SetStatus(GetLocalizedString(LocalizationKeys.ManualDividerDeleted));
        QueueAutosave(SnapshotTrigger.PhaseEdited, tab.TabId, dividerId);
    }

    private void LabelSelectedPhaseDivider()
    {
        if (SelectedTab is not { } tab || SelectedDividerId is not string dividerId || _manualWorkspaceService is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(PhaseCode) || string.IsNullOrWhiteSpace(PhaseLabel))
        {
            SetStatus(GetLocalizedString(LocalizationKeys.ManualPhaseLabelRequired));
            return;
        }

        try
        {
            WorkspaceTabSnapshot before = CaptureTabSnapshot(tab);
            _manualWorkspaceService.LabelPhaseDivider(tab.TabId, dividerId, PhaseCode, PhaseLabel);
            RecordManualEdit(tab, LocalizationKeys.EditLabelPhaseDivider, before);
            SetStatus(GetLocalizedString(LocalizationKeys.ManualDividerLabeled));
            QueueAutosave(SnapshotTrigger.PhaseEdited, tab.TabId, dividerId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            SetOperationFailure(
                exception,
                LocalizationKeys.ManualEditRejected,
                "MANUAL_EDIT_REJECTED",
                "review-input");
        }
    }

    private void SelectNearest(WorkspaceTabViewModel tab, Point point)
    {
        GraphPoint? nearestPoint = tab.Points
            .OrderBy(candidate => DistanceSquared(candidate.PixelX, candidate.PixelY, point.X, point.Y))
            .FirstOrDefault();
        EditablePhaseDivider? nearestDivider = tab.PhaseDividers
            .OrderBy(divider => Math.Abs(divider.OriginalX - point.X))
            .FirstOrDefault();
        double pointDistance = nearestPoint is null
            ? double.MaxValue
            : Math.Sqrt(DistanceSquared(nearestPoint.PixelX, nearestPoint.PixelY, point.X, point.Y));
        double dividerDistance = nearestDivider is null ? double.MaxValue : Math.Abs(nearestDivider.OriginalX - point.X);
        if (pointDistance <= 12 && pointDistance <= dividerDistance)
        {
            SelectedPointId = nearestPoint!.PointId;
            SelectedSeriesId = nearestPoint.SeriesId;
            SetStatus(GetLocalizedString(LocalizationKeys.ManualPointSelected));
        }
        else if (dividerDistance <= 12)
        {
            SelectedDividerId = nearestDivider!.DividerId;
            PhaseCode = nearestDivider.Code;
            PhaseLabel = nearestDivider.Label;
            SetStatus(GetLocalizedString(LocalizationKeys.ManualDividerSelected));
        }
    }

    private async Task TryAutosaveAsync(
        SnapshotTrigger trigger,
        string? tabId,
        string? entityId,
        CancellationToken cancellationToken,
        bool markDirty = true)
    {
        if (_manualWorkspaceService is null)
        {
            return;
        }

        WorkspaceTabViewModel? editedTab = tabId is null
            ? null
            : Tabs.FirstOrDefault(tab => string.Equals(tab.TabId, tabId, StringComparison.Ordinal));
        if (markDirty && editedTab is not null)
        {
            editedTab.IsDirty = true;
        }

        DomainResult<ProjectSnapshotReceipt> result = await _manualWorkspaceService
            .AutosaveAsync(trigger, tabId, entityId, cancellationToken);
        if (!result.IsSuccess && result.Errors.All(static error =>
            error.Code is not "AUTOSAVE_PATHS_UNAVAILABLE" and not "AUTOSAVE_NOT_ELIGIBLE"))
        {
            SetOperationFailure(result.Errors);
        }
    }

    public async Task RunTimerAutosaveAsync(CancellationToken cancellationToken = default)
    {
        if (_manualWorkspaceService is null || IsBusy)
        {
            return;
        }

        DomainResult<ProjectSnapshotReceipt> result = await _manualWorkspaceService
            .TimerAutosaveAsync(DateTimeOffset.UtcNow, cancellationToken);
        if (!result.IsSuccess && result.Errors.All(static error =>
            error.Code is not "AUTOSAVE_NOT_ELIGIBLE" and not "AUTOSAVE_NOT_DUE" and not "AUTOSAVE_PATHS_UNAVAILABLE"))
        {
            SetOperationFailure(result.Errors);
        }
    }

    private async void QueueAutosave(
        SnapshotTrigger trigger,
        string? tabId,
        string? entityId,
        bool markDirty = true)
    {
        try
        {
            await TryAutosaveAsync(trigger, tabId, entityId, _shutdown.Token, markDirty);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetOperationFailure(
                exception,
                LocalizationKeys.ApplicationDataNotWritable,
                "AUTOSAVE_FAILED",
                "check-storage-permissions");
        }
    }

    private bool CanEditPoint() =>
        _manualWorkspaceService is not null && SelectedTab is not null && SelectedSeriesId is not null;

    private bool CanStartCalibration() =>
        SelectedTab is not null &&
        _manualWorkspaceService is not null &&
        IsManualFirstVisibleSessionInputValid;

    private bool TryParsePositiveFirstVisibleSession(out int value) =>
        int.TryParse(
            ManualFirstVisibleSessionText.Trim(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value) &&
        value > 0;

    private bool CanMoveSelectedPoint() =>
        _manualWorkspaceService is not null && SelectedTab is not null && SelectedPointId is not null;

    private void SetStatus(string message)
    {
        _statusMessageOverride = message;
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void SetOperationFailure(
        Exception exception,
        string fallbackUserMessageKey = LocalizationKeys.OperationFailed,
        string fallbackCode = "WORKSPACE_OPERATION_FAILED",
        string fallbackSuggestedAction = "review-and-retry")
    {
        ArgumentNullException.ThrowIfNull(exception);

        WorkspaceOperationStatus status;
        if (exception is ProductionWorkflowStageException productionFailure)
        {
            ProductionWorkflowFailure failure = productionFailure.Failure;
            status = new WorkspaceOperationStatus(
                failure.Code,
                ReviewIssueSeverity.Blocking,
                SafeUserMessageKey(failure.UserMessageKey),
                failure.TechnicalMessage,
                failure.Recoverable,
                failure.SuggestedAction);
        }
        else
        {
            status = new WorkspaceOperationStatus(
                fallbackCode,
                ReviewIssueSeverity.Blocking,
                fallbackUserMessageKey,
                exception.Message,
                Recoverable: true,
                fallbackSuggestedAction);
        }

        LastOperationStatus = status;
        StatusMessageKey = status.UserMessageKey;
    }

    private void SetOperationFailure(IReadOnlyList<DomainError> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            SetOperationFailure(new InvalidOperationException("The operation failed without structured domain details."));
            return;
        }

        DomainError primary = failures[0];
        SetStructuredOperationFailure(
            primary.Code,
            primary.Severity == DomainErrorSeverity.Error
                ? ReviewIssueSeverity.Blocking
                : ReviewIssueSeverity.Warning,
            primary.UserMessageKey,
            string.Join(" | ", failures.Select(static failure => failure.TechnicalMessage)),
            primary.Recoverable,
            primary.SuggestedAction);
    }

    private void SetOperationFailure(IReadOnlyList<GraphReader.Export.ExportFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            SetOperationFailure(new InvalidOperationException("The export failed without structured details."));
            return;
        }

        GraphReader.Export.ExportFailure primary = failures[0];
        SetStructuredOperationFailure(
            primary.Code,
            primary.Severity == GraphReader.Export.ExportFailureSeverity.Error
                ? ReviewIssueSeverity.Blocking
                : ReviewIssueSeverity.Warning,
            primary.UserMessageKey,
            string.Join(" | ", failures.Select(static failure => failure.TechnicalMessage)),
            primary.Recoverable,
            primary.SuggestedAction);
    }

    private void SetStructuredOperationFailure(
        string code,
        ReviewIssueSeverity severity,
        string userMessageKey,
        string technicalMessage,
        bool recoverable,
        string suggestedAction)
    {
        LastOperationStatus = new WorkspaceOperationStatus(
            code,
            severity,
            SafeUserMessageKey(userMessageKey),
            technicalMessage,
            recoverable,
            suggestedAction);
        StatusMessageKey = LastOperationStatus.UserMessageKey;
    }

    private static string SafeUserMessageKey(string? userMessageKey) =>
        userMessageKey is not null && LocalizationKeys.All.Contains(userMessageKey, StringComparer.Ordinal)
            ? userMessageKey
            : LocalizationKeys.OperationFailed;

    private PipelineWarningPresentation[] ResolvePipelineWarnings(WorkspaceTabViewModel tab)
    {
        string panelIdText = tab.PanelId ?? tab.TabId;
        return _workspaceService is IAutomaticWorkspaceService { LastAutomaticRun: { } run } &&
            Guid.TryParse(panelIdText, out Guid panelId) &&
            run.Review.Panels.Any(panel => panel.PanelId == panelId)
                ? run.Review.Warnings
                    .Select(static warning => new PipelineWarningPresentation(
                        LocalizationKeys.ReviewPipelineWarningInterpretation,
                        warning))
                    .ToArray()
                : [];
    }

    private static string SymbolFor(MarkerShape shape, MarkerFill fill) => (shape, fill) switch
    {
        (MarkerShape.Circle, MarkerFill.Open) => "○",
        (MarkerShape.Circle, MarkerFill.Unknown) => "◌",
        (MarkerShape.Circle, MarkerFill.Filled) => "●",
        (MarkerShape.Square, MarkerFill.Open) => "□",
        (MarkerShape.Square, MarkerFill.Unknown) => "▧",
        (MarkerShape.Square, MarkerFill.Filled) => "■",
        (MarkerShape.TriangleUp, MarkerFill.Open) => "△",
        (MarkerShape.TriangleUp, MarkerFill.Unknown) => "△",
        (MarkerShape.TriangleUp, MarkerFill.Filled) => "▲",
        (MarkerShape.TriangleDown, MarkerFill.Open or MarkerFill.Unknown) => "▽",
        (MarkerShape.TriangleDown, MarkerFill.Filled) => "▼",
        (MarkerShape.Diamond, MarkerFill.Open) => "◇",
        (MarkerShape.Diamond, MarkerFill.Unknown) => "◇",
        (MarkerShape.Diamond, MarkerFill.Filled) => "◆",
        (MarkerShape.Star or MarkerShape.Asterisk, _) => "✱",
        (MarkerShape.Cross, _) => "✕",
        _ => "?",
    };

    private string JoinStages(
        IEnumerable<AutomaticStageStatus> stages,
        Func<AutomaticStageState, bool> include)
    {
        string[] names = stages.Where(stage => include(stage.State)).Select(stage => stage.Stage).ToArray();
        return names.Length == 0
            ? GetLocalizedString(LocalizationKeys.PreviewNone)
            : string.Join(", ", names);
    }

    private static double DistanceSquared(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2;
        double dy = y1 - y2;
        return (dx * dx) + (dy * dy);
    }

    private static void EnsureManualOverlay(WorkspaceTabViewModel tab)
    {
        if (tab.OverlayContent is null && tab.PixelWidth > 0 && tab.PixelHeight > 0)
        {
            tab.SetOverlayContent(new ManualReviewOverlay(
                tab.Points,
                tab.SeriesCards,
                tab.PhaseDividers,
                tab.PixelWidth,
                tab.PixelHeight));
        }
    }

    private void ConfigureSeriesSelection(WorkspaceTabViewModel tab)
    {
        foreach (SeriesCardViewModel series in tab.SeriesCards)
        {
            ConfigureSeriesSelection(series);
        }
    }

    private void ConfigureSeriesSelection(SeriesCardViewModel series)
    {
        series.SetSelectAction(seriesId =>
        {
            SelectedSeriesId = seriesId;
            SetStatus(FormatLocalizedString(LocalizationKeys.ManualSeriesSelectedFormat, series.Label));
        });
    }

    private void ChangeZoom(double factor)
    {
        if (SelectedTab is { } tab)
        {
            SetZoom(tab.ZoomLevel * factor);
        }
    }

    private void SelectRelativeTab(int offset)
    {
        if (Tabs.Count < 2)
        {
            return;
        }

        int current = SelectedTab is null ? 0 : Math.Max(0, Tabs.IndexOf(SelectedTab));
        int next = (current + offset) % Tabs.Count;
        if (next < 0)
        {
            next += Tabs.Count;
        }

        SelectedTab = Tabs[next];
    }

    private void SetZoom(double value)
    {
        if (SelectedTab is { } tab)
        {
            tab.ZoomLevel = Math.Clamp(value, 0.25, 8);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _activeOperation?.Cancel();
        _activeOperation?.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
        _disposed = true;
    }

    private AsyncRelayCommand CreateWorkflowCommand(WorkflowStage stage, string statusMessageKey)
    {
        AsyncRelayCommand command = new(
            cancellationToken => RunWorkflowStageAsync(stage, statusMessageKey, cancellationToken),
            () => !IsBusy && _isWorkflowAvailable && CanRunAutomaticStage(stage));
        return command;
    }

    private bool CanRunAutomaticStage(WorkflowStage stage)
    {
        if (stage == WorkflowStage.Detect && SelectedTab is null)
        {
            return false;
        }
        if (stage == WorkflowStage.Review)
        {
            return SelectedTab is not null && _reviewReadyTabIds.Contains(SelectedTab.TabId);
        }
        if (_workspaceService is not IRuntimeWorkspaceService runtime)
        {
            return true;
        }

        static bool IsEnabled(AutomaticStageStatus status, bool allowExperimental = false) =>
            status.State is AutomaticStageState.Available or AutomaticStageState.Approved ||
            (allowExperimental && status.State == AutomaticStageState.Experimental);

        return stage switch
        {
            WorkflowStage.Prepare => runtime.AutomaticStages
                .Any(status => string.Equals(status.Stage, "enhancement", StringComparison.Ordinal) && IsEnabled(status, allowExperimental: true)),
            WorkflowStage.Detect => RequiredAutomaticDetectionStages
                .All(required => runtime.AutomaticStages.Any(status =>
                    string.Equals(status.Stage, required, StringComparison.Ordinal) && IsEnabled(status))),
            _ => true,
        };
    }

    private async Task RunWorkflowStageAsync(
        WorkflowStage stage,
        string statusMessageKey,
        CancellationToken commandCancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        WorkspaceTabViewModel? selectedBeforeOperation = SelectedTab;
        Dictionary<Guid, WorkflowReviewPanel> previousDetectionPanels =
            stage == WorkflowStage.Detect && _workspaceService is IAutomaticWorkspaceService { LastAutomaticRun: { } previousRun }
                ? previousRun.Review.Panels.ToDictionary(static panel => panel.PanelId)
                : [];
        _activeOperation?.Dispose();
        _activeOperation = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token,
            commandCancellationToken);
        _surfaceBeforeOperation = SurfaceState;
        _statusKeyBeforeOperation = StatusMessageKey;
        _statusOverrideBeforeOperation = _statusMessageOverride;
        _operationStatusBeforeOperation = LastOperationStatus;
        LastOperationStatus = null;
        StatusMessageKey = statusMessageKey;
        if (stage == WorkflowStage.Detect)
        {
            SurfaceState = WorkspaceSurfaceState.Analyzing;
        }
        IsBusy = true;
        try
        {
            await _workspaceService.RunStageAsync(stage, _activeOperation.Token);
            CurrentStage = stage;
            StatusMessageKey = statusMessageKey;
            if (stage is WorkflowStage.Detect or WorkflowStage.Review)
            {
                foreach (WorkspaceTabViewModel tab in Tabs)
                {
                    _reviewReadyTabIds.Add(tab.TabId);
                    if (stage == WorkflowStage.Detect)
                    {
                        GetEditHistory(tab).Clear();
                    }
                }
                SurfaceState = WorkspaceSurfaceState.Reviewing;
                RefreshWorkspaceProjection();
                NotifyHistoryStateChanged();
            }
            else if (stage == WorkflowStage.Import)
            {
                SurfaceState = Tabs.Count == 0 ? WorkspaceSurfaceState.Empty : WorkspaceSurfaceState.Ready;
            }
            else if (stage == WorkflowStage.Export)
            {
                SurfaceState = WorkspaceSurfaceState.ExportPreview;
            }
        }
        catch (OperationCanceledException) when (_activeOperation.IsCancellationRequested)
        {
            bool completedPanels = stage == WorkflowStage.Detect && MarkInterruptedDetectionCheckpoints(previousDetectionPanels);
            _statusMessageKey = _statusKeyBeforeOperation;
            _statusMessageOverride = _statusOverrideBeforeOperation;
            OnPropertyChanged(nameof(StatusMessageKey));
            OnPropertyChanged(nameof(StatusMessage));
            LastOperationStatus = _operationStatusBeforeOperation;
            RestoreInterruptedSurface(completedPanels, selectedBeforeOperation);
            RefreshWorkspaceProjection();
        }
        catch (Exception exception)
        {
            bool completedPanels = stage == WorkflowStage.Detect && MarkInterruptedDetectionCheckpoints(previousDetectionPanels);
            SetOperationFailure(
                exception,
                LocalizationKeys.ProductionWorkflowUnavailable,
                "WORKSPACE_STAGE_FAILED",
                "retry");
            if (stage == WorkflowStage.Detect &&
                exception is ProductionWorkflowStageException productionFailure &&
                productionFailure.Failure.Code == ProductionWorkflowFailureCodes.RecalibrationRequired)
            {
                string messageKey = SelectCalibrationFailurePanel(productionFailure)
                    ? LocalizationKeys.DetectionCalibrationRequired
                    : LocalizationKeys.DetectionCalibrationPanelUnknown;
                LastOperationStatus = LastOperationStatus! with
                {
                    UserMessageKey = messageKey,
                };
                StatusMessageKey = messageKey;
            }
            RestoreInterruptedSurface(completedPanels, selectedBeforeOperation);
            RefreshWorkspaceProjection();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool MarkInterruptedDetectionCheckpoints(Dictionary<Guid, WorkflowReviewPanel> previousPanels)
    {
        if (_workspaceService is not IAutomaticWorkspaceService { LastAutomaticRun: { } run } ||
            _manualWorkspaceService is null || run.Review.ProjectId != _manualWorkspaceService.CurrentProject.ProjectId.Value)
        {
            return false;
        }

        bool changed = false;
        foreach (WorkflowReviewPanel panel in run.Review.Panels)
        {
            if (previousPanels.TryGetValue(panel.PanelId, out WorkflowReviewPanel? previous) &&
                ReferenceEquals(previous, panel)) continue;
            WorkflowImportedPanel imported = panel.PreparedPanel.ImportedPanel;
            WorkspaceTabViewModel[] matches = Tabs.Where(tab =>
                Guid.TryParse(tab.PanelId, out Guid panelId) && panelId == imported.PanelId &&
                Guid.TryParse(tab.SourceId, out Guid sourceId) && sourceId == imported.SourceId &&
                string.Equals(tab.SourceSha256, imported.Original.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) continue;
            _reviewReadyTabIds.Add(matches[0].TabId);
            GetEditHistory(matches[0]).Clear();
            changed = true;
        }
        if (changed) NotifyHistoryStateChanged();
        return changed;
    }

    private void RestoreInterruptedSurface(bool completedPanels, WorkspaceTabViewModel? selectedBeforeOperation)
    {
        if (!completedPanels && ReferenceEquals(SelectedTab, selectedBeforeOperation))
        {
            SurfaceState = _surfaceBeforeOperation;
            return;
        }

        SurfaceState = SelectedTab is null
            ? WorkspaceSurfaceState.Empty
            : _reviewReadyTabIds.Contains(SelectedTab.TabId)
                ? WorkspaceSurfaceState.Reviewing
                : WorkspaceSurfaceState.Ready;
    }

    internal bool SelectCalibrationFailurePanel(ProductionWorkflowStageException failure)
    {
        // The evidence chain is panel-bound. Never navigate based on empty,
        // mixed, or image-mismatched evidence from a failed run.
        WorkflowVisionEnvelope[] evidence = failure.CompletedEvidence.ToArray();
        if (evidence.Length == 0 ||
            (_manualWorkspaceService is not null &&
                _manualWorkspaceService.CurrentProject.ProjectId.Value != evidence[0].ProjectId) ||
            evidence.Any(item => item.PanelId != evidence[0].PanelId ||
                item.RunId != evidence[0].RunId || item.ProjectId != evidence[0].ProjectId ||
                !string.Equals(item.InputSha256, evidence[0].InputSha256, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        WorkspaceTabViewModel[] matches = Tabs.Where(tab =>
            Guid.TryParse(tab.PanelId, out Guid panelId) && panelId == evidence[0].PanelId &&
            string.Equals(tab.SourceSha256, evidence[0].InputSha256, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1)
        {
            SelectedTab = matches[0];
            return true;
        }

        return false;
    }

    private void CancelActiveOperation() => _activeOperation?.Cancel();

    private void EnsureEditHistory(WorkspaceTabViewModel tab)
    {
        if (!_editHistories.ContainsKey(tab.TabId))
        {
            _editHistories.Add(tab.TabId, new TabEditHistory());
        }
    }

    private TabEditHistory GetEditHistory(WorkspaceTabViewModel tab)
    {
        EnsureEditHistory(tab);
        return _editHistories[tab.TabId];
    }

    private void RecordManualEdit(
        WorkspaceTabViewModel tab,
        string descriptionKey,
        WorkspaceTabSnapshot before)
    {
        if (_isRestoringHistory)
        {
            return;
        }

        WorkspaceTabSnapshot after = CaptureTabSnapshot(tab);
        GetEditHistory(tab).Record(
            GetLocalizedString(descriptionKey),
            () => RestoreTabSnapshot(tab, before),
            () => RestoreTabSnapshot(tab, after));
        _reviewReadyTabIds.Add(tab.TabId);
        if (ReferenceEquals(SelectedTab, tab) && SurfaceState == WorkspaceSurfaceState.Ready)
        {
            SurfaceState = WorkspaceSurfaceState.Reviewing;
        }
        tab.IsDirty = true;
        RefreshWorkspaceProjection();
        NotifyHistoryStateChanged();
    }

    private void UndoSelectedTab()
    {
        if (SelectedTab is not { } tab)
        {
            return;
        }

        _isRestoringHistory = true;
        try
        {
            GetEditHistory(tab).Undo();
        }
        finally
        {
            _isRestoringHistory = false;
        }
        RefreshWorkspaceProjection();
        NotifyHistoryStateChanged();
        QueueAutosave(SnapshotTrigger.PointEdited, tab.TabId, "undo", markDirty: false);
    }

    private void RedoSelectedTab()
    {
        if (SelectedTab is not { } tab)
        {
            return;
        }

        _isRestoringHistory = true;
        try
        {
            GetEditHistory(tab).Redo();
        }
        finally
        {
            _isRestoringHistory = false;
        }
        RefreshWorkspaceProjection();
        NotifyHistoryStateChanged();
        QueueAutosave(SnapshotTrigger.PointEdited, tab.TabId, "redo", markDirty: false);
    }

    private void NotifyHistoryStateChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private WorkspaceTabSnapshot CaptureTabSnapshot(WorkspaceTabViewModel tab) => new(
        tab.Calibration,
        tab.Points.Select(static point => new PointSnapshot(
            point.PointId,
            point.SeriesId,
            point.PixelX,
            point.PixelY,
            point.GraphX,
            point.GraphY,
            point.PhaseCode,
            point.PhaseId,
            point.ObservationIndex)).ToArray(),
        tab.SeriesCards.Select(static series => new SeriesSnapshot(
            series.SeriesId,
            series.Symbol,
            series.AccessibleName,
            series.Label,
            series.Confidence,
            series.Shape,
            series.Fill,
            series.SemanticRole,
            series.IsVisible)).ToArray(),
        tab.PhaseDividers.Select(static divider => new DividerSnapshot(
            divider.DividerId,
            divider.OriginalX,
            divider.Code,
            divider.Label)).ToArray(),
        CaptureSeriesRelations(tab),
        CapturePointReviewStatuses(tab),
        CapturePointMarkerIds(tab),
        tab.IsDirty);

    private Dictionary<string, string> CapturePointMarkerIds(WorkspaceTabViewModel tab)
    {
        if (_manualWorkspaceService is null || !Guid.TryParse(tab.PanelId, out Guid panelId))
            return new Dictionary<string, string>();
        return _manualWorkspaceService.CurrentProject.Panels.SingleOrDefault(panel => panel.PanelId.Value == panelId)?
            .Points.Where(static point => point.MarkerId.HasValue)
            .ToDictionary(point => point.PointId.Value.ToString("D"), point => point.MarkerId!.Value.Value.ToString("D"),
                StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>();
    }

    private Dictionary<string, ReviewStatus> CapturePointReviewStatuses(WorkspaceTabViewModel tab)
    {
        if (_manualWorkspaceService is null || !Guid.TryParse(tab.PanelId, out Guid panelId))
            return new Dictionary<string, ReviewStatus>();
        return _manualWorkspaceService.CurrentProject.Panels.SingleOrDefault(panel => panel.PanelId.Value == panelId)?
            .Points.ToDictionary(point => point.PointId.Value.ToString("D"), point => point.ReviewStatus,
                StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, ReviewStatus>();
    }

    private SeriesRelationSnapshot[] CaptureSeriesRelations(WorkspaceTabViewModel tab)
    {
        if (_manualWorkspaceService is null ||
            !Guid.TryParse(tab.PanelId, out Guid panelId))
        {
            return Array.Empty<SeriesRelationSnapshot>();
        }

        PanelRecord? panel = _manualWorkspaceService.CurrentProject.Panels.FirstOrDefault(
            candidate => candidate.PanelId.Value == panelId);
        return panel?.Series
            .Where(static series => series.SemanticRole == SemanticRole.Intervention)
            .OrderBy(static series => series.SeriesId.Value)
            .Select(static series => new SeriesRelationSnapshot(
                series.SeriesId.Value.ToString("D"),
                series.SharedBaselineSeriesId?.Value.ToString("D"),
                series.ApplicableProbeSeriesIds
                    .Select(static id => id.Value.ToString("D"))
                    .ToArray()))
            .ToArray()
            ?? Array.Empty<SeriesRelationSnapshot>();
    }

    private void RestoreTabSnapshot(WorkspaceTabViewModel tab, WorkspaceTabSnapshot snapshot)
    {
        tab.Calibration = snapshot.Calibration;
        tab.Points.Clear();
        foreach (PointSnapshot point in snapshot.Points)
        {
            tab.Points.Add(new GraphPoint(
                point.PointId,
                point.SeriesId,
                point.PixelX,
                point.PixelY,
                point.GraphX,
                point.GraphY,
                point.PhaseCode,
                point.PhaseId,
                point.ObservationIndex));
        }

        tab.SeriesCards.Clear();
        foreach (SeriesSnapshot series in snapshot.Series)
        {
            var restored = new SeriesCardViewModel(
                series.SeriesId,
                series.Symbol,
                series.AccessibleName,
                series.Label,
                series.Confidence,
                tab.Points,
                series.Shape,
                series.Fill,
                series.SemanticRole)
            {
                IsVisible = series.IsVisible,
            };
            ConfigureSeriesSelection(restored);
            tab.SeriesCards.Add(restored);
        }

        tab.PhaseDividers.Clear();
        foreach (DividerSnapshot divider in snapshot.Dividers)
        {
            tab.PhaseDividers.Add(new EditablePhaseDivider(
                divider.DividerId,
                divider.OriginalX,
                divider.Code,
                divider.Label));
        }

        tab.IsDirty = snapshot.IsDirty;
        SelectedPointId = tab.Points.FirstOrDefault()?.PointId;
        SelectedSeriesId = tab.SeriesCards.FirstOrDefault()?.SeriesId;
        SelectedDividerId = tab.PhaseDividers.FirstOrDefault()?.DividerId;
        foreach (SeriesCardViewModel series in tab.SeriesCards)
        {
            series.NotifyCountChanged();
        }
        if (_workspaceService is IWorkspaceEditStateSink stateSink)
        {
            stateSink.SynchronizeRestoredTab(tab.TabId, snapshot.ReviewStatuses, snapshot.PointMarkerIds);
        }
        if (_manualWorkspaceService is not null)
        {
            foreach (SeriesRelationSnapshot relation in snapshot.SeriesRelations)
            {
                _manualWorkspaceService.SetSeriesRelations(
                    tab.TabId,
                    relation.InterventionSeriesId,
                    relation.SharedBaselineSeriesId,
                    relation.ApplicableProbeSeriesIds);
            }
            RefreshSeriesRelationChoices();
        }
        if (tab.Points.Count == 0 && tab.Calibration is null)
        {
            _reviewReadyTabIds.Remove(tab.TabId);
            if (ReferenceEquals(SelectedTab, tab))
            {
                SurfaceState = WorkspaceSurfaceState.Ready;
            }
        }
        else
        {
            _reviewReadyTabIds.Add(tab.TabId);
            if (ReferenceEquals(SelectedTab, tab) && SurfaceState != WorkspaceSurfaceState.ExportPreview)
            {
                SurfaceState = WorkspaceSurfaceState.Reviewing;
            }
        }
    }

    private void RefreshWorkspaceProjection()
    {
        string? selectedPointId = SelectedPointId;
        SelectedFilteredMarker = null;
        FilteredMarkers.Clear();
        ReviewIssues.Clear();
        DataPreviewRows.Clear();
        if (SelectedTab is not { } tab)
        {
            SetSelectedReviewIssue(null, synchronizeWorkspace: false);
            SetSelectedDataPreviewRow(null, synchronizeWorkspace: false);
            ExportSummary = null;
            NotifyReviewStateChanged();
            return;
        }

        ProjectDocument? project = _manualWorkspaceService?.CurrentProject;
        foreach (FilteredMarkerCandidateViewModel marker in WorkspaceReviewProjectionService.ProjectFilteredMarkers(tab, project))
            FilteredMarkers.Add(marker);
        foreach (ReviewIssueViewModel issue in WorkspaceReviewProjectionService.ProjectIssues(
                     tab,
                     project,
                     GetLocalizedString,
                     _pipelineWarningsProvider(tab)))
        {
            ReviewIssues.Add(issue);
        }
        foreach (DataPreviewRowViewModel row in WorkspaceReviewProjectionService.ProjectRows(tab, project))
        {
            DataPreviewRows.Add(row);
        }

        SetSelectedReviewIssue(ReviewIssues.FirstOrDefault(), synchronizeWorkspace: false);
        SetSelectedDataPreviewRow(DataPreviewRows.FirstOrDefault(row => string.Equals(
            row.PointId,
            selectedPointId,
            StringComparison.Ordinal)), synchronizeWorkspace: false);
        if (SurfaceState == WorkspaceSurfaceState.ExportPreview)
        {
            bool warningsAcknowledged = ExportSummary?.WarningsAcknowledged == true;
            ExportSummaryViewModel refreshed = CreateExportSummary(
                ExportSummary?.OutputDirectory,
                ExportSummary?.OutputFileNames);
            refreshed.WarningsAcknowledged = warningsAcknowledged;
            ExportSummary = refreshed;
        }
        NotifyReviewStateChanged();
    }

    private void NotifyReviewStateChanged()
    {
        OnPropertyChanged(nameof(AutoDetectionAvailabilityText));
        OnPropertyChanged(nameof(ReviewIssueCount));
        OnPropertyChanged(nameof(BlockingReviewIssueCount));
        OnPropertyChanged(nameof(HasReviewIssues));
        RelayCommand.RaiseCanExecuteChanged();
        AsyncRelayCommand.RaiseCanExecuteChanged();
    }

    private void SelectReviewIssue(ReviewIssueViewModel? issue)
    {
        SetSelectedReviewIssue(issue, synchronizeWorkspace: true);
    }

    private void SetSelectedReviewIssue(
        ReviewIssueViewModel? issue,
        bool synchronizeWorkspace)
    {
        _ = SetProperty(ref _selectedReviewIssue, issue);
        RelayCommand.RaiseCanExecuteChanged();
        if (!synchronizeWorkspace || issue is null)
        {
            return;
        }

        SelectedFilteredMarker = null;

        if (issue.EntityId is not { } entityId)
        {
            return;
        }
        if (FindPoint(entityId) is { } point)
        {
            SelectedPointId = entityId;
            SelectedSeriesId = point.SeriesId;
            HandleCanvasNavigation(new Point(point.PixelX, point.PixelY));
        }
        else if (FindSeries(entityId) is not null)
        {
            SelectedSeriesId = entityId;
        }
        else if (SelectedTab?.PhaseDividers.Any(divider => divider.DividerId == entityId) == true)
        {
            SelectedDividerId = entityId;
        }
    }

    private void SelectDataPreviewRow(DataPreviewRowViewModel? row)
    {
        SetSelectedDataPreviewRow(row, synchronizeWorkspace: true);
    }

    private void SetSelectedDataPreviewRow(
        DataPreviewRowViewModel? row,
        bool synchronizeWorkspace)
    {
        bool selectionChanged = !ReferenceEquals(_selectedDataPreviewRow, row);
        if (selectionChanged)
        {
            if (_selectedDataPreviewRow is not null)
            {
                _selectedDataPreviewRow.IsSelected = false;
            }
            _selectedDataPreviewRow = row;
            if (_selectedDataPreviewRow is not null)
            {
                _selectedDataPreviewRow.IsSelected = true;
            }
            OnPropertyChanged(nameof(SelectedDataPreviewRow));
        }

        if (!synchronizeWorkspace || row is null)
        {
            return;
        }

        SelectedPointId = row.PointId;
        SelectedSeriesId = row.SeriesId;
        HandleCanvasNavigation(new Point(row.PixelX, row.PixelY));
    }

    private void OpenExportPreview()
    {
        RefreshWorkspaceProjection();
        SurfaceState = WorkspaceSurfaceState.ExportPreview;
        ExportSummary = CreateExportSummary(outputDirectory: null, outputFileNames: null);
    }

    private static void HandleExportSummaryPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        _ = sender;
        if (args.PropertyName is nameof(ExportSummaryViewModel.CanExport) or null or "")
        {
            AsyncRelayCommand.RaiseCanExecuteChanged();
        }
    }

    private ExportSummaryViewModel CreateExportSummary(
        string? outputDirectory,
        IEnumerable<string>? outputFileNames)
    {
        WorkspaceTabViewModel? tab = SelectedTab;
        PanelRecord? panel = tab is not null && Guid.TryParse(tab.PanelId, out Guid panelId)
            ? _manualWorkspaceService?.CurrentProject.Panels.FirstOrDefault(
                candidate => candidate.PanelId.Value == panelId)
            : null;
        int phaseCount = panel?.Phases.Count ??
            tab?.Points.Select(static point => point.PhaseCode)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
        ReviewIssueViewModel[] blockingIssues = ReviewIssues
            .Where(static issue => issue.IsBlocking)
            .ToArray();
        ReviewIssueViewModel[] warningIssues = ReviewIssues
            .Where(static issue => issue.Severity == ReviewIssueSeverity.Warning)
            .ToArray();
        string[] expectedFileNames = outputFileNames?.ToArray()
            ?? CreateExpectedExportFileNames(tab, panel);
        return new ExportSummaryViewModel(
            tab?.Points.Count ?? 0,
            tab?.SeriesCards.Count ?? 0,
            phaseCount,
            blockingIssues.Length,
            warningIssues.Length,
            outputDirectory,
            expectedFileNames,
            FormatLocalizedString(
                LocalizationKeys.ExportPreviewProvenanceFormat,
                Path.GetFileName(tab?.SourcePath) ?? GetLocalizedString(LocalizationKeys.PreviewUnknown)),
            GetLocalizedString(LocalizationKeys.ExportPreviewDestinationPending),
            blockingIssues,
            warningIssues,
            outputDirectory is null
                ? ExportDestinationStatus.PendingSelection
                : ExportDestinationStatus.Written);
    }

    private static string[] CreateExpectedExportFileNames(
        WorkspaceTabViewModel? tab,
        PanelRecord? panel)
    {
        if (tab is null)
        {
            return [];
        }

        string? participant = panel?.Participant ?? Path.GetFileNameWithoutExtension(tab.DisplayName);
        ExpectedExportSeries[] series = panel is not null
            ? panel.Series
                .Where(static item => item.SemanticRole == SemanticRole.Intervention)
                .Select(static item => new ExpectedExportSeries(
                    item.SeriesId.Value,
                    item.SeriesId.Value.ToString("D"),
                    item.DisplayName,
                    item.Symbol))
                .ToArray()
            : tab.SeriesCards
                .Where(static item => item.SemanticRole == SemanticRole.Intervention)
                .Select(static item => new ExpectedExportSeries(
                    Guid.TryParse(item.SeriesId, out Guid seriesId) ? seriesId : null,
                    item.SeriesId,
                    item.Label,
                    item.Symbol))
                .ToArray();

        string participantName = SanitizeExpectedExportComponent(participant, "participant");
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextSuffixByStem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fileNames = new List<string>(series.Length * 3);
        foreach (ExpectedExportSeries item in series
                     .OrderBy(static item => item.SeriesId.HasValue ? 0 : 1)
                     .ThenBy(static item => item.SeriesId)
                     .ThenBy(static item => item.StableId, StringComparer.Ordinal))
        {
            string displayName = string.IsNullOrEmpty(item.Symbol)
                ? item.DisplayName
                : item.DisplayName.Replace(item.Symbol, string.Empty, StringComparison.Ordinal);
            string fallback = item.SeriesId is Guid seriesId
                ? $"series-{seriesId:N}"
                : $"series-{SanitizeExpectedExportComponent(item.StableId, "unknown")}";
            string seriesName = SanitizeExpectedExportComponent(displayName, fallback);
            string stem = LimitExpectedExportStem($"{participantName}_{seriesName}", suffixLength: 0);
            int suffixNumber = nextSuffixByStem.TryGetValue(stem, out int previousSuffix)
                ? checked(previousSuffix + 1)
                : 1;

            while (true)
            {
                string duplicateSuffix = suffixNumber == 1 ? string.Empty : $"-{suffixNumber}";
                string uniqueStem = LimitExpectedExportStem(stem, duplicateSuffix.Length) + duplicateSuffix;
                string minimalCsv = uniqueStem + ".csv";
                string extendedAuditCsv = uniqueStem + ".audit.csv";
                string auditJson = uniqueStem + ".audit.json";
                if (!usedFileNames.Contains(minimalCsv) &&
                    !usedFileNames.Contains(extendedAuditCsv) &&
                    !usedFileNames.Contains(auditJson))
                {
                    usedFileNames.Add(minimalCsv);
                    usedFileNames.Add(extendedAuditCsv);
                    usedFileNames.Add(auditJson);
                    fileNames.Add(minimalCsv);
                    fileNames.Add(extendedAuditCsv);
                    fileNames.Add(auditJson);
                    nextSuffixByStem[stem] = suffixNumber;
                    break;
                }

                suffixNumber = checked(suffixNumber + 1);
            }
        }

        return fileNames.ToArray();
    }

    private static string SanitizeExpectedExportComponent(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var sanitized = new char[value.Length];
        int written = 0;
        bool lastWasReplacement = false;
        foreach (char character in value)
        {
            bool mustReplace = char.IsControl(character) ||
                InvalidExportFileNameCharacters.Contains(character);
            if (mustReplace)
            {
                if (!lastWasReplacement)
                {
                    sanitized[written++] = '-';
                    lastWasReplacement = true;
                }
                continue;
            }

            sanitized[written++] = character;
            lastWasReplacement = false;
        }

        string result = new string(sanitized, 0, written).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(result))
        {
            return fallback;
        }

        int periodIndex = result.IndexOf('.');
        string deviceCandidate = periodIndex >= 0 ? result[..periodIndex] : result;
        return ReservedExportDeviceNames.Contains(deviceCandidate.TrimEnd('.', ' '))
            ? $"_{result}"
            : result;
    }

    private static string LimitExpectedExportStem(string stem, int suffixLength)
    {
        int maximumLength = MaximumExportStemLength - suffixLength;
        if (stem.Length <= maximumLength)
        {
            return stem;
        }

        int length = maximumLength;
        if (char.IsHighSurrogate(stem[length - 1]) &&
            length < stem.Length &&
            char.IsLowSurrogate(stem[length]))
        {
            length--;
        }
        return stem[..length].TrimEnd('.', ' ');
    }

    private bool CanExecuteReassignCommand(object? parameter)
    {
        if (parameter is ReassignPointRequest request)
        {
            return FindPoint(request.PointId) is not null &&
                FindSeries(request.TargetSeriesId) is not null;
        }

        return parameter is string targetSeriesId &&
            SelectedPointId is string pointId &&
            FindPoint(pointId) is GraphPoint point &&
            point.SeriesId != targetSeriesId &&
            FindSeries(targetSeriesId) is not null;
    }

    private void ExecuteReassignCommand(object? parameter)
    {
        if (parameter is ReassignPointRequest request)
        {
            ReassignPoint(request.PointId, request.TargetSeriesId);
            return;
        }

        if (parameter is string targetSeriesId && SelectedPointId is string pointId)
        {
            ReassignPoint(pointId, targetSeriesId);
            return;
        }

        throw new ArgumentException("A point and target series are required.", nameof(parameter));
    }

    private static string[] LoadRecentProjectPaths(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        try
        {
            return File.ReadAllLines(path)
                .Select(static value => value.Trim())
                .Where(static value => !string.IsNullOrWhiteSpace(value) && File.Exists(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
        }
        catch (Exception exception) when (IsExpectedRecentProjectException(exception))
        {
            return [];
        }
    }

    private void RememberRecentProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (IsExpectedRecentProjectException(exception))
        {
            return;
        }

        string? existing = RecentProjects.FirstOrDefault(candidate =>
            string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentProjects.Remove(existing);
        }
        RecentProjects.Insert(0, fullPath);
        while (RecentProjects.Count > 8)
        {
            RecentProjects.RemoveAt(RecentProjects.Count - 1);
        }
        OnPropertyChanged(nameof(HasRecentProjects));
        RelayCommand.RaiseCanExecuteChanged();

        if (string.IsNullOrWhiteSpace(_recentProjectsPath))
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_recentProjectsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllLines(_recentProjectsPath, RecentProjects);
        }
        catch (Exception exception) when (IsExpectedRecentProjectException(exception))
        {
            // Recent-project persistence is fail-soft and cannot block scientific work.
        }
    }

    private static bool IsExpectedRecentProjectException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
        ArgumentException or NotSupportedException;

    private WorkspaceTabViewModel RequireSelectedTab() =>
        SelectedTab ?? throw new InvalidOperationException("No workspace tab is selected.");

    private GraphReader.App.Models.GraphPoint? FindPoint(string pointId) =>
        SelectedTab?.Points.FirstOrDefault(point => point.PointId == pointId);

    private GraphReader.App.Models.GraphPoint RequirePoint(string pointId) =>
        FindPoint(pointId) ?? throw new KeyNotFoundException($"Point '{pointId}' does not exist.");

    private SeriesCardViewModel? FindSeries(string seriesId) =>
        SelectedTab?.SeriesCards.FirstOrDefault(series => series.SeriesId == seriesId);

    private SeriesCardViewModel RequireSeries(string seriesId) =>
        FindSeries(seriesId) ?? throw new KeyNotFoundException($"Series '{seriesId}' does not exist.");

    private sealed record WorkspaceTabSnapshot(
        ManualCalibrationState? Calibration,
        IReadOnlyList<PointSnapshot> Points,
        IReadOnlyList<SeriesSnapshot> Series,
        IReadOnlyList<DividerSnapshot> Dividers,
        IReadOnlyList<SeriesRelationSnapshot> SeriesRelations,
        IReadOnlyDictionary<string, ReviewStatus> ReviewStatuses,
        IReadOnlyDictionary<string, string> PointMarkerIds,
        bool IsDirty);

    private sealed record SeriesRelationSnapshot(
        string InterventionSeriesId,
        string? SharedBaselineSeriesId,
        IReadOnlyList<string> ApplicableProbeSeriesIds);

    private sealed record PointSnapshot(
        string PointId,
        string SeriesId,
        double PixelX,
        double PixelY,
        double GraphX,
        double GraphY,
        string PhaseCode,
        string? PhaseId,
        int ObservationIndex);

    private sealed record SeriesSnapshot(
        string SeriesId,
        string Symbol,
        string AccessibleName,
        string Label,
        double Confidence,
        MarkerShape Shape,
        MarkerFill Fill,
        SemanticRole SemanticRole,
        bool IsVisible);

    private sealed record DividerSnapshot(
        string DividerId,
        double OriginalX,
        string Code,
        string Label);

    private sealed record ExpectedExportSeries(
        Guid? SeriesId,
        string StableId,
        string DisplayName,
        string Symbol);
}
