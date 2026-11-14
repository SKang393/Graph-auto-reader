// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using GraphReader.App.Localization;
using GraphReader.App.Services;

namespace GraphReader.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CalibrationTickProposalResult? _calibrationTickProposal;
    private string _calibrationTickProposalStatusText = string.Empty;
    private bool _hasEstimatedCalibrationPositions;

    public ICommand SuggestCalibrationFromTicksCommand { get; private set; } = null!;
    public ObservableCollection<string> CalibrationPairedTickRows { get; } = [];
    public bool HasCalibrationTickProposal => _calibrationTickProposal is not null;
    public bool HasCalibrationTickProposalStatus => _calibrationTickProposalStatusText.Length > 0;
    public string CalibrationTickProposalStatusText => _calibrationTickProposalStatusText;
    public bool HasEstimatedCalibrationPositions => _hasEstimatedCalibrationPositions;

    public async Task SuggestCalibrationFromTicksAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        ClearCalibrationTickProposal();
        if (SelectedTab is not { } expectedTab || _workspaceService is not IAxisSuggestionWorkspace axes ||
            _workspaceService is not ICalibrationLabelSuggestionWorkspace labels)
            return;

        await SuggestAxesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(SelectedTab, expectedTab) || _axisSuggestion is not { } suggestion ||
            suggestion.TabId != expectedTab.TabId || !axes.IsAxisSuggestionCurrent(suggestion))
            return;

        await ReadCalibrationLabelsAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(SelectedTab, expectedTab) || !ReferenceEquals(_axisSuggestion, suggestion) ||
            _calibrationLabelSuggestion is not { } labelsResult || !labels.IsCalibrationLabelSuggestionCurrent(labelsResult))
        {
            _calibrationTickProposalStatusText = GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsUnavailable);
            OnPropertyChanged(nameof(CalibrationTickProposalStatusText));
            OnPropertyChanged(nameof(HasCalibrationTickProposalStatus));
            return;
        }

        CalibrationTickProposalResult proposal = CalibrationTickProposal.CreateReviewDraft(suggestion.Geometry, labelsResult.Response);
        if (!axes.IsAxisSuggestionCurrent(suggestion) || !labels.IsCalibrationLabelSuggestionCurrent(labelsResult))
        {
            _calibrationTickProposalStatusText = GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsUnavailable);
            OnPropertyChanged(nameof(CalibrationTickProposalStatusText));
            OnPropertyChanged(nameof(HasCalibrationTickProposalStatus));
            return;
        }

        SetEstimatedCalibrationPositions(proposal.UsesEstimatedPositions);
        if (!proposal.IsComplete)
        {
            _calibrationTickProposal = proposal;
            PopulateCalibrationPairedTickRows(proposal);
            _calibrationTickProposalStatusText = GetLocalizedString(LocalizationKeys.ManualCalibrationTicksIncomplete);
            OnPropertyChanged(nameof(HasCalibrationTickProposal));
            OnPropertyChanged(nameof(CalibrationTickProposalStatusText));
            OnPropertyChanged(nameof(HasCalibrationTickProposalStatus));
            return;
        }

        CalibrationTickAnchors anchors = proposal.Anchors!;
        SuggestedYMaximum = proposal.YMaximum!.Value.ToString("R", CultureInfo.InvariantCulture);
        SuggestedFirstSession = proposal.FirstSession!.Value.ToString(CultureInfo.InvariantCulture);
        SuggestedFinalSession = proposal.FinalSession!.Value.ToString("R", CultureInfo.InvariantCulture);
        _suggestedAnchors =
        [
            new(anchors.FirstSessionY0.X, anchors.FirstSessionY0.Y),
            new(anchors.FirstSessionYMaximum.X, anchors.FirstSessionYMaximum.Y),
            new(anchors.FinalSessionY0.X, anchors.FinalSessionY0.Y),
        ];
        UpdateAxisSuggestionOverlay();
        AxisSuggestionReviewed = false;
        _calibrationTickProposal = proposal;
        PopulateCalibrationPairedTickRows(proposal);
        _calibrationTickProposalStatusText = FormatLocalizedString(proposal.UsesEstimatedPositions
                ? LocalizationKeys.ManualCalibrationTicksEstimatedComplete : LocalizationKeys.ManualCalibrationTicksComplete,
            proposal.PairedTicks.Count);
        OnPropertyChanged(nameof(HasCalibrationTickProposal));
        OnPropertyChanged(nameof(CalibrationTickProposalStatusText));
        OnPropertyChanged(nameof(HasCalibrationTickProposalStatus));
        OnPropertyChanged(nameof(AxisSuggestionSummary));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void PopulateCalibrationPairedTickRows(CalibrationTickProposalResult proposal)
    {
        CalibrationPairedTickRows.Clear();
        foreach (CalibrationPairedTick pair in proposal.PairedTicks)
        {
            string axis = GetLocalizedString(pair.Axis == "X"
                ? LocalizationKeys.ManualCalibrationTicksAxisX : LocalizationKeys.ManualCalibrationTicksAxisY);
            CalibrationPairedTickRows.Add(FormatLocalizedString(pair.IsEstimated
                    ? LocalizationKeys.ManualCalibrationTicksEstimatedRow : LocalizationKeys.ManualCalibrationTicksPairedRow,
                axis, pair.Value.ToString("R", CultureInfo.InvariantCulture), pair.PixelPosition));
        }
    }

    private void SetEstimatedCalibrationPositions(bool value)
    {
        if (_hasEstimatedCalibrationPositions == value) return;
        _hasEstimatedCalibrationPositions = value;
        OnPropertyChanged(nameof(HasEstimatedCalibrationPositions));
    }

    private void ClearCalibrationTickProposal()
    {
        // Editing a draft invalidates its tick proof, but its estimated-position
        // warning remains until the axis suggestion is dismissed or applied.
        bool hadProposal = _calibrationTickProposal is not null;
        bool hadStatus = _calibrationTickProposalStatusText.Length > 0;
        _calibrationTickProposal = null;
        _calibrationTickProposalStatusText = string.Empty;
        CalibrationPairedTickRows.Clear();
        if (hadProposal) OnPropertyChanged(nameof(HasCalibrationTickProposal));
        if (hadStatus) OnPropertyChanged(nameof(CalibrationTickProposalStatusText));
        if (hadStatus) OnPropertyChanged(nameof(HasCalibrationTickProposalStatus));
    }
}
