// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.ObjectModel;
using System.Globalization;
using GraphReader.App.Controls;
using System.Windows.Input;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.Domain;
using GraphPoint = GraphReader.App.Models.GraphPoint;

namespace GraphReader.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const int MinimumMarkerExemplarSize = 5;
    private const int MaximumMarkerExemplarSize = 65;
    private const int DefaultMarkerExemplarSize = 17;
    private string _markerExemplarExampleSizeText = DefaultMarkerExemplarSize.ToString(CultureInfo.InvariantCulture);
    private string _markerExemplarStatusOverride = string.Empty;
    private WorkspaceMarkerExemplarSuggestion? _markerExemplarSuggestion;
    private CancellationTokenSource? _markerExemplarCancellation;
    private int _markerExemplarGeneration;

    public ObservableCollection<MarkerExemplarCandidateViewModel> MarkerExemplarCandidates { get; } = [];
    public ICommand FindMarkerExemplarsCommand { get; private set; } = null!;
    public ICommand AcceptMarkerExemplarCandidateCommand { get; private set; } = null!;
    public ICommand AcceptAllMarkerExemplarCandidatesCommand { get; private set; } = null!;
    public ICommand RemoveMarkerExemplarCandidateCommand { get; private set; } = null!;

    public string MarkerExemplarExampleSizeText
    {
        get => _markerExemplarExampleSizeText;
        set
        {
            if (!SetProperty(ref _markerExemplarExampleSizeText, value)) return;
            InvalidateMarkerExemplarInputs();
            OnPropertyChanged(nameof(MarkerExemplarStatusText));
            RelayCommand.RaiseCanExecuteChanged();
            AsyncRelayCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanShowMarkerExemplar => SelectedTab?.Calibration is not null && _manualWorkspaceService is not null;
    public bool HasMarkerExemplarCandidates => MarkerExemplarCandidates.Count > 0;
    public bool HasMarkerExemplarStatus => CanShowMarkerExemplar && !string.IsNullOrEmpty(MarkerExemplarStatusText);
    public string AcceptAllMarkerExemplarsText => FormatLocalizedString(
        LocalizationKeys.ManualMarkerExemplarAcceptAll, GetMarkerExemplarBatchCandidates().Candidates.Count);
    public string MarkerExemplarStatusText
    {
        get
        {
            if (_markerExemplarStatusOverride.Length > 0) return _markerExemplarStatusOverride;
            if (!CanShowMarkerExemplar) return string.Empty;
            if (!TryGetMarkerExemplarSize(out _)) return GetLocalizedString(LocalizationKeys.ManualMarkerExemplarInvalidSize);
            if (_workspaceService is not IMarkerExemplarWorkspace { CanFindMarkerExamples: true })
                return FormatLocalizedString(LocalizationKeys.ManualMarkerExemplarFailure,
                    GetLocalizedString(LocalizationKeys.ManualMarkerExemplarFailureUnavailable));
            if (EditorMode == ManualEditorMode.Select && SelectedTab is { } tab && SelectedPointId is { } pointId &&
                tab.Points.SingleOrDefault(point => point.PointId == pointId) is { } seed &&
                !TryGetMarkerExemplarRectangle(tab, seed, ParseMarkerExemplarSize(), out _))
                return GetLocalizedString(LocalizationKeys.ManualMarkerExemplarUnavailable);
            return GetLocalizedString(LocalizationKeys.ManualMarkerExemplarReady);
        }
    }

    private void InitializeMarkerExemplarCommands()
    {
        FindMarkerExemplarsCommand = new AsyncRelayCommand(FindMarkerExemplarsAsync, CanFindMarkerExemplars);
        AcceptMarkerExemplarCandidateCommand = new RelayCommand(AcceptMarkerExemplarCandidate,
            CanAcceptMarkerExemplarCandidate);
        AcceptAllMarkerExemplarCandidatesCommand = new RelayCommand(_ => AcceptAllMarkerExemplarCandidates(),
            _ => GetMarkerExemplarBatchCandidates().Candidates.Count > 0);
        RemoveMarkerExemplarCandidateCommand = new RelayCommand(RemoveMarkerExemplarCandidate,
            parameter => parameter is MarkerExemplarCandidateViewModel row && MarkerExemplarCandidates.Contains(row));
    }

    private bool CanFindMarkerExemplars() => !IsBusy &&
        _workspaceService is IMarkerExemplarWorkspace { CanFindMarkerExamples: true } &&
        TryGetMarkerExemplarContext(out _, out _, out _, out _, out _);

    public async Task FindMarkerExemplarsAsync(CancellationToken cancellationToken) => await RunBusyAsync(async token =>
    {
        if (!TryGetMarkerExemplarContext(out IMarkerExemplarWorkspace workspace, out WorkspaceTabViewModel tab,
                out GraphPoint seed, out string seriesId, out int exampleSize))
        {
            SetMarkerExemplarStatus(GetLocalizedString(CanFindMarkerExemplarSizeOnly()
                ? LocalizationKeys.ManualMarkerExemplarUnavailable : LocalizationKeys.ManualMarkerExemplarInvalidSize));
            return;
        }

        EditorMode = ManualEditorMode.Select;
        ClearMarkerExemplarProposals(updateSample: true);
        int generation = _markerExemplarGeneration;
        string selectedPointId = seed.PointId;
        double selectedX = seed.PixelX, selectedY = seed.PixelY;
        var requestCancellation = new CancellationTokenSource();
        _markerExemplarCancellation = requestCancellation;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, requestCancellation.Token);
        SetMarkerExemplarStatus(GetLocalizedString(LocalizationKeys.ManualMarkerExemplarSearching));
        try
        {
            WorkspaceMarkerExemplarSuggestion? result = await workspace.FindMarkerExamplesAsync(
                tab.TabId, selectedPointId, seriesId, exampleSize, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (generation != _markerExemplarGeneration || !ReferenceEquals(SelectedTab, tab) ||
                SelectedPointId != selectedPointId || SelectedSeriesId != seriesId ||
                !TryGetMarkerExemplarSize(out int currentSize) || currentSize != exampleSize ||
                !tab.Points.Any(point => point.PointId == selectedPointId &&
                    point.PixelX == selectedX && point.PixelY == selectedY) || result is null ||
                result.TabId != tab.TabId || result.Request.SelectedPointId != selectedPointId ||
                result.Request.SelectedSeriesId != seriesId || result.Request.Exemplar.Width != exampleSize ||
                result.Request.Exemplar.Height != exampleSize || !workspace.IsMarkerExemplarSuggestionCurrent(result))
            {
                SetMarkerExemplarStatus(GetLocalizedString(LocalizationKeys.ManualMarkerExemplarUnavailable));
                return;
            }

            if (result.Response.FailureCode is { } failureCode)
            {
                SetMarkerExemplarStatus(FormatLocalizedString(LocalizationKeys.ManualMarkerExemplarFailure,
                    GetMarkerExemplarFailure(failureCode)));
                return;
            }

            _markerExemplarSuggestion = result;
            MarkerExemplarCandidates.Clear();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenCenters = new List<MarkerExemplarPoint>();
            foreach (MarkerExemplarCandidate candidate in result.Response.Candidates)
            {
                if (!seenIds.Add(candidate.CandidateId) || !IsValidMarkerCandidate(candidate, result.Request)) continue;
                bool duplicate = IsDuplicateMarkerPosition(candidate.Center, seriesId, tab) ||
                    seenCenters.Any(center => IsSameMarkerPosition(center, candidate.Center));
                seenCenters.Add(candidate.Center);
                MarkerExemplarCandidates.Add(new(candidate,
                    FormatLocalizedString(LocalizationKeys.ManualMarkerExemplarRow,
                        candidate.Center.X.ToString("0.##", CultureInfo.CurrentCulture),
                        candidate.Center.Y.ToString("0.##", CultureInfo.CurrentCulture),
                        candidate.Similarity.ToString("P0", CultureInfo.CurrentCulture)), duplicate));
            }
            SetMarkerExemplarStatus(MarkerExemplarCandidates.Count == 0
                ? GetLocalizedString(LocalizationKeys.ManualMarkerExemplarNoMatches)
                : FormatLocalizedString(LocalizationKeys.ManualMarkerExemplarCount, MarkerExemplarCandidates.Count));
            UpdateMarkerExemplarOverlay();
            OnPropertyChanged(nameof(HasMarkerExemplarCandidates));
            OnPropertyChanged(nameof(AcceptAllMarkerExemplarsText));
            RelayCommand.RaiseCanExecuteChanged();
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested && !token.IsCancellationRequested)
        {
            SetMarkerExemplarStatus(GetLocalizedString(LocalizationKeys.ManualMarkerExemplarUnavailable));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            SetMarkerExemplarStatus(FormatLocalizedString(LocalizationKeys.ManualMarkerExemplarFailure,
                GetLocalizedString(LocalizationKeys.ManualMarkerExemplarFailureMatching)));
        }
        finally
        {
            if (ReferenceEquals(_markerExemplarCancellation, requestCancellation)) _markerExemplarCancellation = null;
            requestCancellation.Dispose();
        }
    }, cancellationToken);

    private bool TryGetMarkerExemplarContext(out IMarkerExemplarWorkspace workspace, out WorkspaceTabViewModel tab,
        out GraphPoint seed, out string seriesId, out int exampleSize)
    {
        workspace = null!;
        tab = null!;
        seed = null!;
        seriesId = string.Empty;
        exampleSize = 0;
        if (_workspaceService is not IMarkerExemplarWorkspace { CanFindMarkerExamples: true } available ||
            EditorMode == ManualEditorMode.Calibration ||
            SelectedTab is not { Calibration: not null } selectedTab || SelectedPointId is not { } pointId ||
            SelectedSeriesId is not { } selectedSeries || !TryGetMarkerExemplarSize(out exampleSize) ||
            !selectedTab.SeriesCards.Any(series => series.SeriesId == selectedSeries) ||
            selectedTab.Points.SingleOrDefault(point => point.PointId == pointId) is not { } selectedPoint ||
            !double.IsFinite(selectedPoint.PixelX) || !double.IsFinite(selectedPoint.PixelY) ||
            !TryGetMarkerExemplarRectangle(selectedTab, selectedPoint, exampleSize, out _))
            return false;
        workspace = available;
        tab = selectedTab;
        seed = selectedPoint;
        seriesId = selectedSeries;
        return selectedPoint.SeriesId == selectedSeries;
    }

    private bool CanFindMarkerExemplarSizeOnly() => TryGetMarkerExemplarSize(out _);

    private bool TryGetMarkerExemplarSize(out int size) =>
        int.TryParse(MarkerExemplarExampleSizeText, NumberStyles.None, CultureInfo.InvariantCulture, out size) &&
        size is >= MinimumMarkerExemplarSize and <= MaximumMarkerExemplarSize;

    private static bool TryGetMarkerExemplarRectangle(WorkspaceTabViewModel tab, GraphPoint seed, int size,
        out MarkerExemplarRectangle rectangle)
    {
        rectangle = null!;
        if (tab.Calibration is not { } calibration || size is < MinimumMarkerExemplarSize or > MaximumMarkerExemplarSize)
            return false;
        var origin = calibration.Session1Y0;
        var upper = calibration.Session1YMaximum;
        var right = calibration.SessionMaximumY0;
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y) || !double.IsFinite(upper.Y) ||
            !double.IsFinite(right.X)) return false;
        int left = (int)Math.Ceiling(origin.X), top = (int)Math.Ceiling(upper.Y);
        int plotRight = (int)Math.Floor(right.X), bottom = (int)Math.Floor(origin.Y);
        int x = (int)Math.Floor(seed.PixelX - size / 2d), y = (int)Math.Floor(seed.PixelY - size / 2d);
        if (left < 0 || top < 0 || plotRight >= tab.PixelWidth || bottom >= tab.PixelHeight ||
            plotRight <= left || bottom <= top || x < left || y < top ||
            (long)x + size - 1 > plotRight || (long)y + size - 1 > bottom)
            return false;
        rectangle = new(x, y, size, size);
        return true;
    }

    private void AcceptMarkerExemplarCandidate(object? parameter)
    {
        if (parameter is not MarkerExemplarCandidateViewModel row || !CanAcceptMarkerExemplarCandidate(row) ||
            _markerExemplarSuggestion is not { } suggestion || _manualWorkspaceService is null || SelectedTab is not { } tab)
            return;
        WorkspaceTabSnapshot before = CaptureTabSnapshot(tab);
        try
        {
            GraphPoint added = _manualWorkspaceService.AddPoint(tab.TabId, suggestion.Request.SelectedSeriesId,
                row.Candidate.Center.X, row.Candidate.Center.Y);
            RecordManualEdit(tab, LocalizationKeys.EditAddPoint, before);
            QueueAutosave(SnapshotTrigger.PointEdited, tab.TabId, added.PointId);
            MarkerExemplarCandidates.Remove(row);
            RefreshMarkerExemplarDuplicates(tab, suggestion.Request.SelectedSeriesId);
            SetMarkerExemplarStatus(MarkerExemplarCandidates.Count == 0
                ? GetLocalizedString(LocalizationKeys.ManualMarkerExemplarNoMatches)
                : FormatLocalizedString(LocalizationKeys.ManualMarkerExemplarCount, MarkerExemplarCandidates.Count));
            UpdateMarkerExemplarOverlay();
            OnPropertyChanged(nameof(HasMarkerExemplarCandidates));
            OnPropertyChanged(nameof(AcceptAllMarkerExemplarsText));
            RelayCommand.RaiseCanExecuteChanged();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            SetOperationFailure(exception, LocalizationKeys.ManualEditRejected,
                "MARKER_EXEMPLAR_ADD_REJECTED", "review-inputs");
        }
    }

    private (WorkspaceTabViewModel? Tab, WorkspaceMarkerExemplarSuggestion? Suggestion,
        List<MarkerExemplarCandidateViewModel> Candidates, int Skipped) GetMarkerExemplarBatchCandidates()
    {
        var candidates = new List<MarkerExemplarCandidateViewModel>();
        if (IsBusy || _markerExemplarSuggestion is not { } suggestion ||
            _workspaceService is not IMarkerExemplarWorkspace workspace || _manualWorkspaceService is null ||
            SelectedTab is not { } tab || tab.TabId != suggestion.TabId ||
            SelectedPointId != suggestion.Request.SelectedPointId || SelectedSeriesId != suggestion.Request.SelectedSeriesId ||
            !TryGetMarkerExemplarSize(out int size) || size != suggestion.Request.Exemplar.Width ||
            !workspace.IsMarkerExemplarSuggestionCurrent(suggestion) ||
            !TryGetMarkerExemplarContext(out _, out WorkspaceTabViewModel contextTab, out GraphPoint seed,
                out string seriesId, out _) || !ReferenceEquals(contextTab, tab) ||
            seed.PointId != suggestion.Request.SelectedPointId || seriesId != suggestion.Request.SelectedSeriesId)
            return (null, null, candidates, MarkerExemplarCandidates.Count);

        MarkerExemplarCandidateViewModel[] validRows = MarkerExemplarCandidates
            .Where(row => IsValidMarkerCandidate(row.Candidate, suggestion.Request)).ToArray();
        foreach (MarkerExemplarCandidateViewModel row in validRows)
        {
            bool overlapsExisting = IsDuplicateMarkerPosition(row.Candidate.Center, seriesId, tab);
            bool overlapsAnother = validRows.Any(other => !ReferenceEquals(other, row) &&
                IsSameMarkerPosition(other.Candidate.Center, row.Candidate.Center));
            if (row.IsDuplicate || overlapsExisting || overlapsAnother) continue;
            candidates.Add(row);
        }
        return (tab, suggestion, candidates, MarkerExemplarCandidates.Count - candidates.Count);
    }

    private void AcceptAllMarkerExemplarCandidates()
    {
        var batch = GetMarkerExemplarBatchCandidates();
        if (batch.Tab is not { } tab || batch.Suggestion is not { } suggestion || batch.Candidates.Count == 0 ||
            _manualWorkspaceService is null) return;

        string? selectedPointId = SelectedPointId;
        string? selectedSeriesId = SelectedSeriesId;
        WorkspaceTabSnapshot before = CaptureTabSnapshot(tab);
        string lastAddedPointId = string.Empty;
        try
        {
            foreach (MarkerExemplarCandidateViewModel row in batch.Candidates)
            {
                GraphPoint added = _manualWorkspaceService.AddPoint(tab.TabId, suggestion.Request.SelectedSeriesId,
                    row.Candidate.Center.X, row.Candidate.Center.Y);
                lastAddedPointId = added.PointId;
            }

            RecordManualEdit(tab, LocalizationKeys.EditAddPoint, before);
            QueueAutosave(SnapshotTrigger.PointEdited, tab.TabId, lastAddedPointId);
            foreach (MarkerExemplarCandidateViewModel row in batch.Candidates) MarkerExemplarCandidates.Remove(row);
            RefreshMarkerExemplarDuplicates(tab, suggestion.Request.SelectedSeriesId);
            SetMarkerExemplarStatus(FormatLocalizedString(LocalizationKeys.ManualMarkerExemplarBatchAdded,
                batch.Candidates.Count, batch.Skipped));
            UpdateMarkerExemplarOverlay();
            OnPropertyChanged(nameof(HasMarkerExemplarCandidates));
            OnPropertyChanged(nameof(AcceptAllMarkerExemplarsText));
            RelayCommand.RaiseCanExecuteChanged();
        }
        catch (Exception)
        {
            try
            {
                RestoreTabSnapshot(tab, before);
            }
            catch (Exception restoreException)
            {
                ClearMarkerExemplarProposals(updateSample: true);
                SetOperationFailure(restoreException, LocalizationKeys.ManualEditRejected,
                    "MARKER_EXEMPLAR_BATCH_ROLLBACK_FAILED", "review-inputs");
                return;
            }
            if (selectedPointId is not null && tab.Points.Any(point => point.PointId == selectedPointId))
                SelectedPointId = selectedPointId;
            if (selectedSeriesId is not null && tab.SeriesCards.Any(series => series.SeriesId == selectedSeriesId))
                SelectedSeriesId = selectedSeriesId;
            ClearMarkerExemplarProposals(updateSample: true);
            QueueAutosave(SnapshotTrigger.PointEdited, tab.TabId, "marker-exemplar-batch-rollback");
            SetMarkerExemplarStatus(GetLocalizedString(LocalizationKeys.ManualMarkerExemplarBatchFailure));
        }
    }

    private bool CanAcceptMarkerExemplarCandidate(object? parameter)
    {
        if (IsBusy || parameter is not MarkerExemplarCandidateViewModel row || row.IsDuplicate ||
            !MarkerExemplarCandidates.Contains(row) || _markerExemplarSuggestion is not { } suggestion ||
            _workspaceService is not IMarkerExemplarWorkspace workspace || _manualWorkspaceService is null ||
            SelectedTab is not { } tab || tab.TabId != suggestion.TabId || SelectedPointId != suggestion.Request.SelectedPointId ||
            SelectedSeriesId != suggestion.Request.SelectedSeriesId || !workspace.IsMarkerExemplarSuggestionCurrent(suggestion) ||
            !TryGetMarkerExemplarSize(out int size) || size != suggestion.Request.Exemplar.Width ||
            !IsValidMarkerCandidate(row.Candidate, suggestion.Request))
            return false;
        return !IsDuplicateMarkerPosition(row.Candidate.Center, suggestion.Request.SelectedSeriesId, tab);
    }

    private void RemoveMarkerExemplarCandidate(object? parameter)
    {
        if (parameter is not MarkerExemplarCandidateViewModel row || !MarkerExemplarCandidates.Remove(row)) return;
        if (_markerExemplarSuggestion is { } suggestion && SelectedTab is { } tab && tab.TabId == suggestion.TabId)
            RefreshMarkerExemplarDuplicates(tab, suggestion.Request.SelectedSeriesId);
        UpdateMarkerExemplarOverlay();
        OnPropertyChanged(nameof(HasMarkerExemplarCandidates));
        OnPropertyChanged(nameof(AcceptAllMarkerExemplarsText));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void RefreshMarkerExemplarDuplicates(WorkspaceTabViewModel tab, string seriesId)
    {
        foreach (MarkerExemplarCandidateViewModel candidate in MarkerExemplarCandidates)
            candidate.IsDuplicate = IsDuplicateMarkerPosition(candidate.Candidate.Center, seriesId, tab) ||
                MarkerExemplarCandidates.Any(other => !ReferenceEquals(other, candidate) &&
                    IsSameMarkerPosition(other.Candidate.Center, candidate.Candidate.Center));
    }

    private static bool IsValidMarkerCandidate(MarkerExemplarCandidate candidate, MarkerExemplarRequest request) =>
        !string.IsNullOrWhiteSpace(candidate.CandidateId) && double.IsFinite(candidate.Similarity) &&
        candidate.Similarity is >= 0 and <= 1 && double.IsFinite(candidate.Center.X) && double.IsFinite(candidate.Center.Y) &&
        candidate.Center.X >= request.Plot.X && candidate.Center.Y >= request.Plot.Y &&
        candidate.Center.X <= (long)request.Plot.X + request.Plot.Width - 1 &&
        candidate.Center.Y <= (long)request.Plot.Y + request.Plot.Height - 1 &&
        candidate.Center.X >= 0 && candidate.Center.Y >= 0 && candidate.Center.X < request.Width &&
        candidate.Center.Y < request.Height;

    private static bool IsSameMarkerPosition(MarkerExemplarPoint left, MarkerExemplarPoint right) =>
        Math.Abs(left.X - right.X) < 1 && Math.Abs(left.Y - right.Y) < 1;

    private static bool IsDuplicateMarkerPosition(MarkerExemplarPoint candidate, string seriesId, WorkspaceTabViewModel tab) =>
        tab.Points.Any(point => point.SeriesId == seriesId && IsSameMarkerPosition(candidate,
            new MarkerExemplarPoint(point.PixelX, point.PixelY)));

    private void InvalidateMarkerExemplarInputs()
    {
        ClearMarkerExemplarProposals(updateSample: true);
        OnPropertyChanged(nameof(MarkerExemplarStatusText));
        OnPropertyChanged(nameof(HasMarkerExemplarStatus));
        OnPropertyChanged(nameof(CanShowMarkerExemplar));
    }

    private void ClearMarkerExemplarProposals(bool updateSample)
    {
        _markerExemplarGeneration++;
        _markerExemplarCancellation?.Cancel();
        _markerExemplarSuggestion = null;
        _markerExemplarStatusOverride = string.Empty;
        MarkerExemplarCandidates.Clear();
        if (SelectedTab?.OverlayContent is ManualReviewOverlay overlay)
        {
            MarkerExemplarRectangle? rectangle = updateSample && TryGetSelectedMarkerExemplarRectangle(out var sample)
                ? sample : null;
            overlay.SetMarkerExemplar(rectangle, []);
        }
        OnPropertyChanged(nameof(HasMarkerExemplarCandidates));
        OnPropertyChanged(nameof(AcceptAllMarkerExemplarsText));
        OnPropertyChanged(nameof(MarkerExemplarStatusText));
        OnPropertyChanged(nameof(HasMarkerExemplarStatus));
        OnPropertyChanged(nameof(CanShowMarkerExemplar));
        RelayCommand.RaiseCanExecuteChanged();
        AsyncRelayCommand.RaiseCanExecuteChanged();
    }

    private void UpdateMarkerExemplarOverlay()
    {
        if (SelectedTab is not { } tab) return;
        EnsureManualOverlay(tab);
        if (tab.OverlayContent is not ManualReviewOverlay overlay) return;
        MarkerExemplarRectangle? rectangle = _markerExemplarSuggestion is { } suggestion
            ? suggestion.Request.Exemplar
            : TryGetSelectedMarkerExemplarRectangle(out var sample) ? sample : null;
        overlay.SetMarkerExemplar(rectangle, MarkerExemplarCandidates.Select(row => row.Candidate.Center));
    }

    private bool TryGetSelectedMarkerExemplarRectangle(out MarkerExemplarRectangle rectangle)
    {
        rectangle = null!;
        return SelectedTab is { } tab && SelectedPointId is { } pointId && TryGetMarkerExemplarSize(out int size) &&
            tab.Points.SingleOrDefault(point => point.PointId == pointId) is { } seed &&
            TryGetMarkerExemplarRectangle(tab, seed, size, out rectangle);
    }

    private int ParseMarkerExemplarSize() => TryGetMarkerExemplarSize(out int size) ? size : DefaultMarkerExemplarSize;

    private void SetMarkerExemplarStatus(string status)
    {
        _markerExemplarStatusOverride = status;
        OnPropertyChanged(nameof(MarkerExemplarStatusText));
        OnPropertyChanged(nameof(HasMarkerExemplarStatus));
    }

    private string GetMarkerExemplarFailure(string failureCode)
    {
        string key = failureCode switch
        {
            MarkerExemplarReasonCodes.InvalidInput => LocalizationKeys.ManualMarkerExemplarFailureInvalidInput,
            MarkerExemplarReasonCodes.InvalidFrame => LocalizationKeys.ManualMarkerExemplarFailureInvalidFrame,
            MarkerExemplarReasonCodes.ResourceUnavailable => LocalizationKeys.ManualMarkerExemplarFailureResourceUnavailable,
            MarkerExemplarReasonCodes.Unavailable => LocalizationKeys.ManualMarkerExemplarFailureUnavailable,
            MarkerExemplarReasonCodes.Timeout => LocalizationKeys.ManualMarkerExemplarFailureTimeout,
            MarkerExemplarReasonCodes.UninformativeExemplar => LocalizationKeys.ManualMarkerExemplarFailureUninformative,
            MarkerExemplarReasonCodes.TooManyCandidates => LocalizationKeys.ManualMarkerExemplarFailureTooManyCandidates,
            MarkerExemplarReasonCodes.MatchingFailed => LocalizationKeys.ManualMarkerExemplarFailureMatching,
            _ => LocalizationKeys.ManualMarkerExemplarFailureMatching,
        };
        return GetLocalizedString(key);
    }
}

public sealed class MarkerExemplarCandidateViewModel(MarkerExemplarCandidate candidate, string displayText,
    bool isDuplicate) : ObservableObject
{
    private bool _isDuplicate = isDuplicate;
    public MarkerExemplarCandidate Candidate { get; } = candidate;
    public string DisplayText { get; } = displayText;
    public bool IsDuplicate
    {
        get => _isDuplicate;
        set => SetProperty(ref _isDuplicate, value);
    }
    public bool CanAccept => !IsDuplicate;
}
