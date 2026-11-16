// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using GraphReader.App.Controls;
using GraphReader.App.Localization;
using GraphReader.App.Services;

namespace GraphReader.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private WorkspaceAxisCandidateReview? _axisCandidateReview;
    private AxisReviewCandidateViewModel? _selectedAxisCandidate;
    private int _axisCandidatesGeneration;
    private bool _axisCandidatesUnavailable;

    public ObservableCollection<AxisReviewCandidateViewModel> AxisCandidates { get; } = [];
    public ICommand FindAxisCandidatesCommand { get; private set; } = null!;
    public ICommand UseAxisCandidateCommand { get; private set; } = null!;
    public bool HasAxisCandidates => AxisCandidates.Count != 0;
    public string AxisCandidatesStatusText => _axisCandidatesUnavailable
        ? GetLocalizedString(LocalizationKeys.ManualAxisCandidatesUnavailable)
        : (_axisCandidateReview is { EnumerationCompleted: false }
            ? GetLocalizedString(LocalizationKeys.ManualAxisCandidatesIncompleteWarning) + "\n" : string.Empty) +
            GetLocalizedString(LocalizationKeys.ManualAxisCandidatesInstructions);

    public AxisReviewCandidateViewModel? SelectedAxisCandidate
    {
        get => _selectedAxisCandidate;
        set
        {
            if (IsBusy || value is not null && (!AxisCandidates.Contains(value) || !IsAxisCandidateReviewCurrent()))
            {
                OnPropertyChanged(nameof(SelectedAxisCandidate));
                return;
            }
            if (!SetProperty(ref _selectedAxisCandidate, value)) return;
            if (SelectedTab is { } tab)
            {
                EnsureManualOverlay(tab);
                if (tab.OverlayContent is ManualReviewOverlay overlay)
                {
                    Point[] anchors = value?.Candidate switch
                    {
                        { Geometry: { } geometry, AffineGeometry: null } =>
                            [new(geometry.Left, geometry.Bottom), new(geometry.Left, geometry.Top), new(geometry.Right, geometry.Bottom)],
                        { Geometry: null, AffineGeometry: { } affine } =>
                            [new(affine.Origin.X, affine.Origin.Y), new(affine.YEnd.X, affine.YEnd.Y), new(affine.XEnd.X, affine.XEnd.Y)],
                        _ => [],
                    };
                    overlay.SetAxisProposal(anchors);
                }
            }
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    private void InitializeAxisCandidateCommands()
    {
        FindAxisCandidatesCommand = new AsyncRelayCommand(FindAxisCandidatesAsync,
            () => !_disposed && !IsBusy && SelectedTab is not null &&
                _workspaceService is IAxisCandidateReviewWorkspace { CanReviewAxisCandidates: true });
        UseAxisCandidateCommand = new RelayCommand(_ => UseAxisCandidate(), _ => CanUseAxisCandidate());
    }

    public Task FindAxisCandidatesAsync(CancellationToken cancellationToken) => RunBusyAsync(async token =>
    {
        if (_disposed || SelectedTab is not { } tab ||
            _workspaceService is not IAxisCandidateReviewWorkspace { CanReviewAxisCandidates: true } workspace) return;
        ClearAxisSuggestion();
        _calibrationAnchors.Clear();
        EditorMode = ManualEditorMode.Select;
        int generation = _axisCandidatesGeneration;
        WorkspaceAxisCandidateReview? review = await workspace.ReviewAxisCandidatesAsync(tab.TabId, token);
        token.ThrowIfCancellationRequested();
        if (_disposed || generation != _axisCandidatesGeneration || !ReferenceEquals(SelectedTab, tab)) return;
        if (review is null || review.TabId != tab.TabId || !workspace.IsAxisCandidateReviewCurrent(review) || review.Candidates.Count == 0)
        {
            _axisCandidatesUnavailable = true;
            OnPropertyChanged(nameof(AxisCandidatesStatusText));
            SetStatus(AxisCandidatesStatusText);
            return;
        }
        _axisCandidateReview = review;
        foreach (AxisReviewCandidate candidate in review.Candidates)
            AxisCandidates.Add(new(candidate, AxisCandidateLabel(candidate)));
        OnPropertyChanged(nameof(HasAxisCandidates));
        OnPropertyChanged(nameof(AxisCandidatesStatusText));
        SetStatus(AxisCandidatesStatusText);
        RelayCommand.RaiseCanExecuteChanged();
    }, cancellationToken);

    private bool IsAxisCandidateReviewCurrent() => !_disposed && _axisCandidateReview is { } review &&
        SelectedTab?.TabId == review.TabId && _workspaceService is IAxisCandidateReviewWorkspace workspace &&
        workspace.IsAxisCandidateReviewCurrent(review);

    private bool CanUseAxisCandidate() => !IsBusy && IsAxisCandidateReviewCurrent() &&
        _selectedAxisCandidate is { } selected && AxisCandidates.Contains(selected) &&
        _axisCandidateReview!.Candidates.Any(candidate => ReferenceEquals(candidate, selected.Candidate));

    private void UseAxisCandidate()
    {
        if (!CanUseAxisCandidate() || _axisCandidateReview is not { } review ||
            _selectedAxisCandidate is not { } selected || _workspaceService is not IAxisCandidateReviewWorkspace workspace) return;
        WorkspaceAxisSuggestion? suggestion = workspace.SelectAxisCandidate(review, selected.Index);
        if (suggestion is null) return;
        ClearAxisSuggestion();
        SetAxisSuggestion(suggestion);
    }

    private string AxisCandidateLabel(AxisReviewCandidate candidate) => candidate switch
    {
        { Geometry: { } geometry, AffineGeometry: null } =>
            FormatLocalizedString(LocalizationKeys.ManualAxisCandidateLabel, candidate.Index + 1,
                geometry.Left, geometry.Top, geometry.Right, geometry.Bottom),
        { Geometry: null, AffineGeometry: { } affine } =>
            FormatLocalizedString(LocalizationKeys.ManualAxisCandidateAffineLabel, candidate.Index + 1,
                affine.Origin.X, affine.Origin.Y, affine.XEnd.X, affine.XEnd.Y, affine.YEnd.X, affine.YEnd.Y),
        _ => string.Empty,
    };

    private void NotifyAxisCandidateLabelsChanged()
    {
        foreach (AxisReviewCandidateViewModel candidate in AxisCandidates)
            candidate.UpdateLabel(AxisCandidateLabel(candidate.Candidate));
        OnPropertyChanged(nameof(AxisCandidatesStatusText));
    }

    private void ClearAxisCandidates()
    {
        _axisCandidatesGeneration++;
        bool hadPreview = _selectedAxisCandidate is not null;
        _axisCandidateReview = null;
        _selectedAxisCandidate = null;
        _axisCandidatesUnavailable = false;
        AxisCandidates.Clear();
        if (hadPreview && SelectedTab?.OverlayContent is ManualReviewOverlay overlay) overlay.SetAxisProposal([]);
        OnPropertyChanged(nameof(SelectedAxisCandidate));
        OnPropertyChanged(nameof(HasAxisCandidates));
        OnPropertyChanged(nameof(AxisCandidatesStatusText));
        RelayCommand.RaiseCanExecuteChanged();
    }
}

public sealed class AxisReviewCandidateViewModel : ObservableObject
{
    private string _displayText;
    internal AxisReviewCandidate Candidate { get; }
    internal AxisReviewCandidateViewModel(AxisReviewCandidate candidate, string displayText)
    {
        Candidate = candidate;
        _displayText = displayText;
    }
    public int Index => Candidate.Index;
    public string DisplayText => _displayText;
    internal void UpdateLabel(string text) => SetProperty(ref _displayText, text, nameof(DisplayText));
}
