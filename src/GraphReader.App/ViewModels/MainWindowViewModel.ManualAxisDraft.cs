// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Windows;
using System.Windows.Input;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Func<bool>? _manualAxisDraftIsCurrent;
    public ICommand BeginManualAxisDraftCommand { get; private set; } = null!;
    public bool IsManualAxisDraft => _axisSuggestion?.IsUserDefined == true;
    public bool IsCollectingManualAxisDraft => _manualAxisDraftIsCurrent is not null;

    private void BeginManualAxisDraft()
    {
        if (IsBusy || SelectedTab is not { } tab || _manualWorkspaceService is null ||
            _workspaceService is not IManualAxisDraftWorkspace { CanCreateManualAxisDraft: true } ||
            _workspaceService is not ICalibrationLabelSuggestionWorkspace { CanReadCalibrationLabels: true }) return;
        ClearAxisSuggestion();
        IManualWorkspaceService manual = _manualWorkspaceService;
        var project = manual.CurrentProject;
        var image = tab.ImageSource;
        string? panelId = tab.PanelId, hash = tab.SourceSha256;
        int width = tab.PixelWidth, height = tab.PixelHeight;
        var calibration = tab.Calibration;
        _manualAxisDraftIsCurrent = () => ReferenceEquals(SelectedTab, tab) &&
            ReferenceEquals(manual.CurrentProject, project) && ReferenceEquals(tab.ImageSource, image) &&
            tab.PanelId == panelId && tab.SourceSha256 == hash && tab.PixelWidth == width && tab.PixelHeight == height &&
            ReferenceEquals(tab.Calibration, calibration);
        _calibrationAnchors.Clear();
        EditorMode = ManualEditorMode.Calibration;
        OnPropertyChanged(nameof(IsCollectingManualAxisDraft));
        OnPropertyChanged(nameof(EditorInstruction));
        SetStatus(GetLocalizedString(LocalizationKeys.ManualAxisDraftPrompt));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void HandleManualAxisDraftPoint(WorkspaceTabViewModel tab, Point point)
    {
        if (IsBusy) return;
        if (_manualAxisDraftIsCurrent?.Invoke() != true || !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            point.X < 0 || point.Y < 0 || point.X > tab.PixelWidth - 1 || point.Y > tab.PixelHeight - 1)
        {
            ClearAxisSuggestion();
            SetStatus(GetLocalizedString(LocalizationKeys.ManualAxisDraftInvalid));
            return;
        }
        HandleCanvasNavigation(point);
        _calibrationAnchors.Add(point);
        OnPropertyChanged(nameof(EditorInstruction));
        if (_calibrationAnchors.Count != 3) return;
        Point[] anchors = _calibrationAnchors.ToArray();
        WorkspaceAxisSuggestion? draft = (_workspaceService as IManualAxisDraftWorkspace)?.CreateManualAxisDraft(tab.TabId,
            anchors.Select(anchor => new PixelPoint(anchor.X, anchor.Y)).ToArray());
        if (draft is not { IsUserDefined: true } || _manualAxisDraftIsCurrent?.Invoke() != true ||
            _workspaceService is not IAxisSuggestionWorkspace axes || !axes.IsAxisSuggestionCurrent(draft))
        {
            ClearAxisSuggestion();
            SetStatus(GetLocalizedString(LocalizationKeys.ManualAxisDraftInvalid));
            return;
        }
        ClearManualAxisDraftSelection();
        _axisSuggestion = draft;
        _suggestedAnchors = anchors;
        EditorMode = ManualEditorMode.Select;
        AxisSuggestionReviewed = false;
        UpdateAxisSuggestionOverlay();
        OnPropertyChanged(nameof(HasAxisSuggestion));
        OnPropertyChanged(nameof(IsManualAxisDraft));
        OnPropertyChanged(nameof(AxisSuggestionSummary));
        OnPropertyChanged(nameof(CalibrationLabelStatusText));
        SetStatus(GetLocalizedString(LocalizationKeys.ManualAxisDraftReady));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void ClearManualAxisDraftSelection()
    {
        if (!IsCollectingManualAxisDraft) return;
        _manualAxisDraftIsCurrent = null;
        _calibrationAnchors.Clear();
        OnPropertyChanged(nameof(IsCollectingManualAxisDraft));
        OnPropertyChanged(nameof(EditorInstruction));
        RelayCommand.RaiseCanExecuteChanged();
    }
}
