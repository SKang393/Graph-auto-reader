// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using GraphReader.App.Controls;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.Domain;

namespace GraphReader.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private WorkspaceAxisSuggestion? _axisSuggestion;
    private Point[] _suggestedAnchors = [];
    private string _suggestedYMaximum = "";
    private string _suggestedFirstSession = "";
    private string _suggestedFinalSession = "";
    private bool _axisSuggestionReviewed;
    private int _axisSuggestionGeneration;
    private bool _suggestedUsesPrintedXCoordinates;
    public ICommand SuggestAxesCommand { get; private set; } = null!;
    public ICommand ApplyAxisSuggestionCommand { get; private set; } = null!;
    public ICommand DismissAxisSuggestionCommand { get; private set; } = null!;
    public bool HasAxisSuggestion => _axisSuggestion is not null;
    public bool HasAffineAxisSuggestion => _axisSuggestion is { Geometry: null, AffineGeometry: not null };
    public bool SuggestedUsesPrintedXCoordinates => _suggestedUsesPrintedXCoordinates;
    public string SuggestedFirstXLabel => GetLocalizedString(SuggestedUsesPrintedXCoordinates
        ? LocalizationKeys.ManualFirstPrintedX : LocalizationKeys.ManualFirstVisibleSession);
    public string SuggestedFinalXLabel => GetLocalizedString(SuggestedUsesPrintedXCoordinates
        ? LocalizationKeys.ManualFinalPrintedX : LocalizationKeys.ManualXMaximum);
    public string UseCalibrationLabelForFirstXText => GetLocalizedString(SuggestedUsesPrintedXCoordinates
        ? LocalizationKeys.ManualUseFirstPrintedX : LocalizationKeys.ManualCalibrationLabelsUseFirstSession);
    public string UseCalibrationLabelForFinalXText => GetLocalizedString(SuggestedUsesPrintedXCoordinates
        ? LocalizationKeys.ManualUseFinalPrintedX : LocalizationKeys.ManualCalibrationLabelsUseFinalSession);

    private void SetSuggestedPrintedXCoordinates(bool value)
    {
        if (!SetProperty(ref _suggestedUsesPrintedXCoordinates, value, nameof(SuggestedUsesPrintedXCoordinates))) return;
        AxisSuggestionReviewed = false;
        NotifySuggestedPrintedXLabelsChanged();
        OnPropertyChanged(nameof(HasPrintedXReviewDraft));
        OnPropertyChanged(nameof(AxisSuggestionSummary));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void NotifySuggestedPrintedXLabelsChanged()
    {
        OnPropertyChanged(nameof(SuggestedFirstXLabel));
        OnPropertyChanged(nameof(SuggestedFinalXLabel));
        OnPropertyChanged(nameof(UseCalibrationLabelForFirstXText));
        OnPropertyChanged(nameof(UseCalibrationLabelForFinalXText));
    }
    public bool HasAxisGeometryReviewWarning => _axisSuggestion is { Provider: "opencv-review" or "rust-review" };
    public string AxisGeometryReviewWarningText
    {
        get
        {
            if (!HasAxisGeometryReviewWarning) return string.Empty;
            bool partial = _axisSuggestion!.ReviewReasons.Any(reason => reason is "x_axis_partial" or "y_axis_partial");
            if (_axisSuggestion.ReviewReasons.Contains("native_incomplete_candidate_search", StringComparer.Ordinal))
                return GetLocalizedString(LocalizationKeys.ManualAxisCandidatesIncompleteWarning) + "\n" +
                    GetLocalizedString(partial ? LocalizationKeys.ManualAxisGeometryPartialWarning
                        : LocalizationKeys.ManualAxisGeometryReviewWarning);
            if (_axisSuggestion.ReviewReasons.Contains("native_multiple_ranked_candidates", StringComparer.Ordinal))
            {
                string warning = GetLocalizedString(LocalizationKeys.ManualAxisGeometryAmbiguousWarning);
                return partial ? warning + "\n" + GetLocalizedString(LocalizationKeys.ManualAxisGeometryPartialWarning) : warning;
            }
            return GetLocalizedString(partial ? LocalizationKeys.ManualAxisGeometryPartialWarning
                : LocalizationKeys.ManualAxisGeometryReviewWarning);
        }
    }
    public string SuggestedYMaximum
    {
        get => _suggestedYMaximum;
        set
        {
            if (SetProperty(ref _suggestedYMaximum, value))
            {
                ResetAxisSuggestionReview();
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SuggestedFirstSession
    {
        get => _suggestedFirstSession;
        set
        {
            if (SetProperty(ref _suggestedFirstSession, value))
            {
                ResetAxisSuggestionReview();
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SuggestedFinalSession
    {
        get => _suggestedFinalSession;
        set
        {
            if (SetProperty(ref _suggestedFinalSession, value))
            {
                ResetAxisSuggestionReview();
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool AxisSuggestionReviewed
    {
        get => _axisSuggestionReviewed;
        set
        {
            SetProperty(ref _axisSuggestionReviewed, value);
            RelayCommand.RaiseCanExecuteChanged();
        }
    }
    public string AxisSuggestionSummary => _suggestedAnchors.Length == 3
        ? FormatLocalizedString(SuggestedUsesPrintedXCoordinates ? LocalizationKeys.ManualPrintedXPositions : LocalizationKeys.ManualAxisPositions,
            _suggestedAnchors[0].X.ToString("0.##", CultureInfo.CurrentCulture), _suggestedAnchors[0].Y.ToString("0.##", CultureInfo.CurrentCulture),
            _suggestedAnchors[1].X.ToString("0.##", CultureInfo.CurrentCulture), _suggestedAnchors[1].Y.ToString("0.##", CultureInfo.CurrentCulture),
            _suggestedAnchors[2].X.ToString("0.##", CultureInfo.CurrentCulture), _suggestedAnchors[2].Y.ToString("0.##", CultureInfo.CurrentCulture)) : "";

    private void InitializeAxisSuggestionCommands()
    {
        InitializeAxisCandidateCommands();
        BeginManualAxisDraftCommand = new RelayCommand(_ => BeginManualAxisDraft(),
            _ => !IsBusy && SelectedTab is not null && _manualWorkspaceService is not null &&
                _workspaceService is IManualAxisDraftWorkspace { CanCreateManualAxisDraft: true } &&
                _workspaceService is ICalibrationLabelSuggestionWorkspace { CanReadCalibrationLabels: true });
        SuggestAxesCommand = new AsyncRelayCommand(SuggestAxesAsync,
            () => !IsBusy && SelectedTab is not null && _workspaceService is IAxisSuggestionWorkspace { CanSuggestAxes: true });
        SuggestCalibrationFromTicksCommand = new AsyncRelayCommand(SuggestCalibrationFromTicksAsync,
            () => !IsBusy && SelectedTab is not null &&
                _workspaceService is ICalibrationLabelSuggestionWorkspace { CanReadCalibrationLabels: true } &&
                (CanReadCalibrationLabelsCore() || _workspaceService is IAxisSuggestionWorkspace { CanSuggestAxes: true }));
        InitializeCalibrationLabelCommands();
        ApplyAxisSuggestionCommand = new AsyncRelayCommand(ApplyAxisSuggestionAsync,
            () => !IsBusy && AxisSuggestionReviewed && TryGetSuggestedValues(out _, out _, out _));
        DismissAxisSuggestionCommand = new RelayCommand(_ => ClearAxisSuggestion(),
            _ => (HasAxisSuggestion || IsCollectingManualAxisDraft || HasAxisCandidates) && !IsBusy);
    }

    public Task SuggestAxesAsync(CancellationToken cancellationToken) => RunBusyAsync(async token =>
    {
        if (SelectedTab is not { } tab || _workspaceService is not IAxisSuggestionWorkspace workspace)
        {
            return;
        }

        ClearAxisSuggestion();
        int generation = _axisSuggestionGeneration;
        WorkspaceAxisSuggestion? suggestion;
        try
        {
            suggestion = await workspace.SuggestAxesAsync(tab.TabId, token);
        }
        catch (ProductionWorkflowStageException) when (generation != _axisSuggestionGeneration ||
            !ReferenceEquals(SelectedTab, tab))
        {
            return;
        }
        token.ThrowIfCancellationRequested();
        if (generation != _axisSuggestionGeneration || !ReferenceEquals(SelectedTab, tab)) return;
        if (suggestion is null || !workspace.IsAxisSuggestionCurrent(suggestion))
        {
            SetStatus(GetLocalizedString(LocalizationKeys.ManualAxisUnavailable));
            return;
        }
        SetAxisSuggestion(suggestion);
    }, cancellationToken);

    private void SetAxisSuggestion(WorkspaceAxisSuggestion suggestion)
    {
        _axisSuggestion = suggestion;
        if (suggestion.AffineGeometry is { } frame && suggestion.Geometry is null)
        {
            _calibrationAnchors.Clear();
            _suggestedAnchors = [new(frame.Origin.X, frame.Origin.Y), new(frame.YEnd.X, frame.YEnd.Y), new(frame.XEnd.X, frame.XEnd.Y)];
            SetSuggestedPrintedXCoordinates(ManualUsesPrintedXCoordinates);
            SuggestedYMaximum = SuggestedFirstSession = SuggestedFinalSession = string.Empty;
            AxisSuggestionReviewed = false;
        }
        else if (suggestion.Geometry is { } geometry && suggestion.AffineGeometry is null)
        {
            int firstX = geometry.Left, finalX = geometry.Right;
            if (geometry is { FirstTickX: int firstTick, LastTickX: int lastTick })
            {
                firstX = firstTick;
                finalX = lastTick;
            }
            _suggestedAnchors = [new(firstX, geometry.Bottom), new(firstX, geometry.Top), new(finalX, geometry.Bottom)];
        }
        else
        {
            ClearAxisSuggestion();
            return;
        }
        EditorMode = ManualEditorMode.Select;
        UpdateAxisSuggestionOverlay();
        OnPropertyChanged(nameof(HasAxisSuggestion));
        OnPropertyChanged(nameof(HasAffineAxisSuggestion));
        OnPropertyChanged(nameof(IsManualAxisDraft));
        OnPropertyChanged(nameof(AxisSuggestionSummary));
        OnPropertyChanged(nameof(CalibrationLabelStatusText));
        OnPropertyChanged(nameof(HasAxisGeometryReviewWarning));
        OnPropertyChanged(nameof(AxisGeometryReviewWarningText));
        SetStatus(GetLocalizedString(HasAffineAxisSuggestion ? LocalizationKeys.ManualAffineAxisSuggestionNotice : LocalizationKeys.ManualAxisReview));
    }

    public Task ApplyAxisSuggestionAsync(CancellationToken cancellationToken) => RunBusyAsync(async token =>
    {
        token.ThrowIfCancellationRequested();
        if (!AxisSuggestionReviewed || !TryGetSuggestedValues(out double y, out double final, out double first) ||
            SelectedTab is not { } tab || _manualWorkspaceService is null)
        {
            return;
        }

        WorkspaceTabSnapshot before = CaptureTabSnapshot(tab);
        bool printedX = SuggestedUsesPrintedXCoordinates;
        _manualWorkspaceService.Calibrate(tab.TabId, new ManualCalibrationRequest(
            new(_suggestedAnchors[0].X, _suggestedAnchors[0].Y),
            new(_suggestedAnchors[1].X, _suggestedAnchors[1].Y),
            new(_suggestedAnchors[2].X, _suggestedAnchors[2].Y), y, final, first)
            { UsesPrintedXCoordinates = printedX, PreserveAffineGeometry = HasAffineAxisSuggestion });
        ClearMarkerExemplarProposals(updateSample: true);
        ManualUsesPrintedXCoordinates = printedX;
        ManualYMaximum = y;
        ManualXMaximum = final;
        ManualFirstVisibleSessionText = first.ToString(CultureInfo.InvariantCulture);
        ClearAxisSuggestion();
        EditorMode = ManualEditorMode.Select;
        SetStatus(FormatLocalizedString(printedX ? LocalizationKeys.ManualPrintedXCalibrationSaved : LocalizationKeys.ManualCalibrationSaved, first, final));
        RecordManualEdit(tab, LocalizationKeys.EditCalibrate, before);
        await TryAutosaveAsync(SnapshotTrigger.CalibrationChanged, tab.TabId, null, token);
    }, cancellationToken);

    private bool TryGetSuggestedValues(out double y, out double final, out double first)
    {
        y = final = 0;
        first = 0;
        return EditorMode != ManualEditorMode.Calibration && _axisSuggestion is { } suggestion && SelectedTab?.TabId == suggestion.TabId &&
            _workspaceService is IAxisSuggestionWorkspace workspace && workspace.IsAxisSuggestionCurrent(suggestion) &&
            _suggestedAnchors.Length == 3 &&
            double.TryParse(SuggestedYMaximum, NumberStyles.Float, CultureInfo.InvariantCulture, out y) && double.IsFinite(y) && y > 0 &&
            double.TryParse(SuggestedFirstSession, NumberStyles.Float, CultureInfo.InvariantCulture, out first) && double.IsFinite(first) &&
            (SuggestedUsesPrintedXCoordinates ||
                int.TryParse(SuggestedFirstSession, NumberStyles.Integer, CultureInfo.InvariantCulture, out int session) && session > 0) &&
            double.TryParse(SuggestedFinalSession, NumberStyles.Float, CultureInfo.InvariantCulture, out final) && double.IsFinite(final) && final > first;
    }

    private void UpdateAxisSuggestionOverlay()
    {
        if (SelectedTab is not { } tab)
        {
            return;
        }

        EnsureManualOverlay(tab);
        if (tab.OverlayContent is ManualReviewOverlay overlay)
        {
            overlay.SetAxisProposal(_suggestedAnchors);
        }
    }

    private void ClearAxisSuggestion()
    {
        ClearAxisCandidates();
        bool wasCollecting = IsCollectingManualAxisDraft;
        ClearManualAxisDraftSelection();
        if (wasCollecting && EditorMode == ManualEditorMode.Calibration) EditorMode = ManualEditorMode.Select;
        _axisSuggestionGeneration++;
        SetSuggestedPrintedXCoordinates(false);
        SetEstimatedCalibrationPositions(false);
        ClearCalibrationLabelResults(clearPickedValues: true);
        if (HasAxisSuggestion)
        {
            _calibrationAnchors.Clear();
            if (EditorMode == ManualEditorMode.Calibration)
            {
                EditorMode = ManualEditorMode.Select;
            }
        }

        if (SelectedTab?.OverlayContent is ManualReviewOverlay overlay)
        {
            overlay.SetAxisProposal([]);
        }

        _axisSuggestion = null;
        _suggestedAnchors = [];
        SuggestedYMaximum = SuggestedFirstSession = SuggestedFinalSession = "";
        AxisSuggestionReviewed = false;
        OnPropertyChanged(nameof(HasAxisSuggestion));
        OnPropertyChanged(nameof(HasAffineAxisSuggestion));
        OnPropertyChanged(nameof(IsManualAxisDraft));
        OnPropertyChanged(nameof(AxisSuggestionSummary));
        OnPropertyChanged(nameof(CalibrationLabelStatusText));
        OnPropertyChanged(nameof(HasAxisGeometryReviewWarning));
        OnPropertyChanged(nameof(AxisGeometryReviewWarningText));
    }
}
