// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using GraphReader.App.Controls;
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
    public ICommand SuggestAxesCommand { get; private set; } = null!;
    public ICommand ApplyAxisSuggestionCommand { get; private set; } = null!;
    public ICommand DismissAxisSuggestionCommand { get; private set; } = null!;
    public bool HasAxisSuggestion => _axisSuggestion is not null;
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
        ? FormatLocalizedString(LocalizationKeys.ManualAxisPositions,
            _suggestedAnchors[0].X, _suggestedAnchors[0].Y, _suggestedAnchors[1].X,
            _suggestedAnchors[1].Y, _suggestedAnchors[2].X, _suggestedAnchors[2].Y) : "";

    private void InitializeAxisSuggestionCommands()
    {
        SuggestAxesCommand = new AsyncRelayCommand(SuggestAxesAsync,
            () => !IsBusy && SelectedTab is not null && _workspaceService is IAxisSuggestionWorkspace { CanSuggestAxes: true });
        InitializeCalibrationLabelCommands();
        ApplyAxisSuggestionCommand = new AsyncRelayCommand(ApplyAxisSuggestionAsync,
            () => !IsBusy && AxisSuggestionReviewed && TryGetSuggestedValues(out _, out _, out _));
        DismissAxisSuggestionCommand = new RelayCommand(_ => ClearAxisSuggestion(), _ => HasAxisSuggestion && !IsBusy);
    }

    public Task SuggestAxesAsync(CancellationToken cancellationToken) => RunBusyAsync(async token =>
    {
        if (SelectedTab is not { } tab || _workspaceService is not IAxisSuggestionWorkspace workspace)
        {
            return;
        }

        ClearAxisSuggestion();
        int generation = _axisSuggestionGeneration;
        WorkspaceAxisSuggestion? suggestion = await workspace.SuggestAxesAsync(tab.TabId, token);
        token.ThrowIfCancellationRequested();
        if (suggestion is null || generation != _axisSuggestionGeneration ||
            !ReferenceEquals(SelectedTab, tab) || !workspace.IsAxisSuggestionCurrent(suggestion))
        {
            SetStatus(GetLocalizedString(LocalizationKeys.ManualAxisUnavailable));
            return;
        }
        _axisSuggestion = suggestion;
        var geometry = suggestion.Geometry;
        int firstX = geometry.Left;
        int finalX = geometry.Right;
        if (geometry is { FirstTickX: int firstTick, LastTickX: int lastTick })
        {
            firstX = firstTick;
            finalX = lastTick;
        }
        _suggestedAnchors =
        [
            new(firstX, geometry.Bottom),
            new(firstX, geometry.Top),
            new(finalX, geometry.Bottom),
        ];
        EditorMode = ManualEditorMode.Select;
        UpdateAxisSuggestionOverlay();
        OnPropertyChanged(nameof(HasAxisSuggestion));
        OnPropertyChanged(nameof(AxisSuggestionSummary));
        OnPropertyChanged(nameof(CalibrationLabelStatusText));
        SetStatus(GetLocalizedString(LocalizationKeys.ManualAxisReview));
    }, cancellationToken);

    public Task ApplyAxisSuggestionAsync(CancellationToken cancellationToken) => RunBusyAsync(async token =>
    {
        token.ThrowIfCancellationRequested();
        if (!AxisSuggestionReviewed || !TryGetSuggestedValues(out double y, out double final, out int first) ||
            SelectedTab is not { } tab || _manualWorkspaceService is null)
        {
            return;
        }

        WorkspaceTabSnapshot before = CaptureTabSnapshot(tab);
        _manualWorkspaceService.Calibrate(tab.TabId, new ManualCalibrationRequest(
            new(_suggestedAnchors[0].X, _suggestedAnchors[0].Y),
            new(_suggestedAnchors[1].X, _suggestedAnchors[1].Y),
            new(_suggestedAnchors[2].X, _suggestedAnchors[2].Y), y, final, first));
        ManualYMaximum = y;
        ManualXMaximum = final;
        ManualFirstVisibleSessionText = first.ToString(CultureInfo.InvariantCulture);
        ClearAxisSuggestion();
        EditorMode = ManualEditorMode.Select;
        SetStatus(FormatLocalizedString(LocalizationKeys.ManualCalibrationSaved, first, final));
        RecordManualEdit(tab, LocalizationKeys.EditCalibrate, before);
        await TryAutosaveAsync(SnapshotTrigger.CalibrationChanged, tab.TabId, null, token);
    }, cancellationToken);

    private bool TryGetSuggestedValues(out double y, out double final, out int first)
    {
        y = final = 0;
        first = 0;
        return EditorMode != ManualEditorMode.Calibration && _axisSuggestion is { } suggestion && SelectedTab?.TabId == suggestion.TabId &&
            _workspaceService is IAxisSuggestionWorkspace workspace && workspace.IsAxisSuggestionCurrent(suggestion) &&
            _suggestedAnchors.Length == 3 &&
            double.TryParse(SuggestedYMaximum, NumberStyles.Float, CultureInfo.InvariantCulture, out y) && double.IsFinite(y) && y > 0 &&
            int.TryParse(SuggestedFirstSession, NumberStyles.Integer, CultureInfo.InvariantCulture, out first) && first > 0 &&
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
        _axisSuggestionGeneration++;
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
        OnPropertyChanged(nameof(AxisSuggestionSummary));
        OnPropertyChanged(nameof(CalibrationLabelStatusText));
    }
}
