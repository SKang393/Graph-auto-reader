// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using GraphReader.App.Localization;
using GraphReader.App.Services;

namespace GraphReader.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CalibrationTickProposalResult? _calibrationTickProposal;
    // Editing the numeric fields invalidates Apply until review. Keep the last displayed pairs for the audit only.
    private (WorkspaceCalibrationLabelSuggestion? Source, CalibrationTickProposalResult? Fit) _displayedLabelEvidence;
    private string _calibrationTickProposalStatusText = string.Empty;
    private bool _hasEstimatedCalibrationPositions;
    private bool _refittingCalibrationLabels;
    private ICommand? _fitForEnteredFirstSessionCommand;
    private ICommand? _usePrintedXCoordinatesCommand;

    public ICommand SuggestCalibrationFromTicksCommand { get; private set; } = null!;
    public ICommand RefitSelectedCalibrationLabelsCommand { get; private set; } = null!;
    public ICommand FitForEnteredFirstSessionCommand => _fitForEnteredFirstSessionCommand ??= new RelayCommand(
        _ => FitForEnteredFirstSession(), _ => CanFitForEnteredFirstSession());
    public ICommand UsePrintedXCoordinatesCommand => _usePrintedXCoordinatesCommand ??= new RelayCommand(
        _ => UsePrintedXCoordinates(), _ => !IsBusy && CanRefitCalibrationLabelsCore() && HasPrintedXReviewDraft);
    public ObservableCollection<string> CalibrationPairedTickRows { get; } = [];
    public bool HasCalibrationTickProposal => _calibrationTickProposal is not null;
    public bool HasCalibrationTickProposalStatus => _calibrationTickProposalStatusText.Length > 0;
    public string CalibrationTickProposalStatusText => _calibrationTickProposalStatusText;
    public bool HasEstimatedCalibrationPositions => _hasEstimatedCalibrationPositions;
    public bool HasPrintedXReviewDraft => !SuggestedUsesPrintedXCoordinates && _calibrationTickProposal?.PrintedXReviewDraft is not null;

    private void UsePrintedXCoordinates()
    {
        if (!UsePrintedXCoordinatesCommand.CanExecute(null) ||
            _calibrationTickProposal is not { PrintedXReviewDraft: { } draft } proposal) return;
        if (!SetCalibrationAnchorsFromWorkingFrame(draft.Anchors)) return;
        SuggestedYMaximum = draft.YMaximum.ToString("R", CultureInfo.InvariantCulture);
        SuggestedFirstSession = draft.FirstX.ToString("R", CultureInfo.InvariantCulture);
        SuggestedFinalSession = draft.FinalX.ToString("R", CultureInfo.InvariantCulture);
        SetSuggestedPrintedXCoordinates(true);
        SetEstimatedCalibrationPositions(_hasEstimatedCalibrationPositions || draft.UsesEstimatedPositions);
        UpdateAxisSuggestionOverlay();
        AxisSuggestionReviewed = false;
        _calibrationTickProposal = proposal;
        PopulateCalibrationPairedTickRows(proposal);
        _calibrationTickProposalStatusText = GetLocalizedString(LocalizationKeys.ManualPrintedXReview);
        OnPropertyChanged(nameof(HasCalibrationTickProposal));
        OnPropertyChanged(nameof(HasPrintedXReviewDraft));
        OnPropertyChanged(nameof(CalibrationTickProposalStatusText));
        OnPropertyChanged(nameof(HasCalibrationTickProposalStatus));
        OnPropertyChanged(nameof(AxisSuggestionSummary));
        RelayCommand.RaiseCanExecuteChanged();
    }

    public async Task SuggestCalibrationFromTicksAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        cancellationToken.ThrowIfCancellationRequested();
        bool useCurrentGeometry = CanReadCalibrationLabelsCore();
        if (useCurrentGeometry) ClearCalibrationLabelComputedValues();
        else ClearCalibrationTickProposal();
        if (SelectedTab is not { } expectedTab || _workspaceService is not IAxisSuggestionWorkspace axes ||
            _workspaceService is not ICalibrationLabelSuggestionWorkspace labels)
            return;

        if (!useCurrentGeometry) await SuggestAxesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(SelectedTab, expectedTab) || _axisSuggestion is not { } suggestion ||
            suggestion.TabId != expectedTab.TabId || !axes.IsAxisSuggestionCurrent(suggestion))
            return;

        await ReadCalibrationLabelsAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(SelectedTab, expectedTab) || !ReferenceEquals(_axisSuggestion, suggestion)) return;
        if (_calibrationLabelSuggestion is not { } labelsResult || !labels.IsCalibrationLabelSuggestionCurrent(labelsResult))
        {
            _calibrationTickProposalStatusText = GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsUnavailable);
            OnPropertyChanged(nameof(CalibrationTickProposalStatusText));
            OnPropertyChanged(nameof(HasCalibrationTickProposalStatus));
            return;
        }

        RefitSelectedCalibrationLabels();
    }

    public void RefitSelectedCalibrationLabels() => RefitCalibrationLabels(null);

    private bool CanFitForEnteredFirstSession() => !SuggestedUsesPrintedXCoordinates && !IsBusy && !_refittingCalibrationLabels && CanRefitCalibrationLabelsCore() &&
        int.TryParse(SuggestedFirstSession, NumberStyles.Integer, CultureInfo.InvariantCulture, out int first) && first > 0 &&
        CalibrationLabelRows.Any(row => row.IsIncluded && row.FitEvidence.EffectiveProposal is
            { Role: "XTick", NumericLiteral: true, NumericValue: <= 0 });

    public void FitForEnteredFirstSession()
    {
        if (!CanFitForEnteredFirstSession() || !int.TryParse(SuggestedFirstSession, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int first)) return;
        RefitCalibrationLabels(first);
    }

    private void RefitCalibrationLabels(int? enteredFirstSession)
    {
        if (IsBusy || _refittingCalibrationLabels || !CanRefitCalibrationLabelsCore() ||
            _calibrationLabelSuggestion is not { } original || _axisSuggestion is not { } suggestion ||
            _workspaceService is not ICalibrationLabelSuggestionWorkspace labels)
            return;
        int generation = _calibrationLabelGeneration;
        CalibrationTickFitEvidence[] included = CalibrationLabelRows.Where(row => row.IsIncluded)
            .Select(row => row.FitEvidence).ToArray();
        _refittingCalibrationLabels = true;
        try
        {
            ClearCalibrationLabelComputedValues();
            if (CalibrationLabelRows.Any(row => row.HasInvalidCorrection))
            {
                _calibrationTickProposalStatusText = GetLocalizedString(LocalizationKeys.ManualCalibrationTicksIncomplete);
                OnPropertyChanged(nameof(CalibrationTickProposalStatusText));
                OnPropertyChanged(nameof(HasCalibrationTickProposalStatus));
                return;
            }
            CalibrationTickProposalResult proposal = enteredFirstSession is int first
                ? CalibrationTickProposal.CreateReviewDraftForFirstSession(original.FitGeometry, original.Response, included, first)
                : CalibrationTickProposal.CreateReviewDraft(original.FitGeometry, original.Response, included);
            if (generation != _calibrationLabelGeneration || !ReferenceEquals(_calibrationLabelSuggestion, original) ||
                !ReferenceEquals(_axisSuggestion, suggestion) || !labels.IsCalibrationLabelSuggestionCurrent(original) ||
                !CanRefitCalibrationLabelsCore())
                return;
            PublishCalibrationTickProposal(proposal);
        }
        finally { _refittingCalibrationLabels = false; RelayCommand.RaiseCanExecuteChanged(); }
    }

    private void ClearCalibrationLabelComputedValues()
    {
        _displayedLabelEvidence = default;
        SetSuggestedPrintedXCoordinates(false);
        ClearCalibrationTickProposal();
        SuggestedYMaximum = SuggestedFirstSession = SuggestedFinalSession = string.Empty;
        AxisSuggestionReviewed = false;
        if (_calibrationLabelReadAnchors.Length == 3)
        {
            _suggestedAnchors = _calibrationLabelReadAnchors.ToArray();
            UpdateAxisSuggestionOverlay();
            OnPropertyChanged(nameof(AxisSuggestionSummary));
        }
    }

    private void PublishCalibrationTickProposal(CalibrationTickProposalResult proposal)
    {
        SetEstimatedCalibrationPositions(_hasEstimatedCalibrationPositions || proposal.UsesEstimatedPositions || proposal.UsesUserEnteredSessionPosition);
        if (!proposal.IsComplete)
        {
            if (!PopulatePartialCalibrationAxes(proposal)) return;
            _calibrationTickProposal = proposal;
            PopulateCalibrationPairedTickRows(proposal);
            _calibrationTickProposalStatusText = GetLocalizedString(proposal.Reasons.Contains(CalibrationTickProposalReasons.AmbiguousNumericScale)
                ? LocalizationKeys.ManualCalibrationTicksAmbiguousScale : proposal.PrintedXReviewDraft is not null
                ? LocalizationKeys.ManualPrintedXAvailable : proposal.XReviewDraft is not null
                ? LocalizationKeys.ManualCalibrationTicksPartialX : proposal.YReviewDraft is not null
                    ? LocalizationKeys.ManualCalibrationTicksPartialY : LocalizationKeys.ManualCalibrationTicksIncomplete);
            OnPropertyChanged(nameof(HasCalibrationTickProposal));
            OnPropertyChanged(nameof(HasPrintedXReviewDraft));
            OnPropertyChanged(nameof(CalibrationTickProposalStatusText));
            OnPropertyChanged(nameof(HasCalibrationTickProposalStatus));
            RelayCommand.RaiseCanExecuteChanged();
            return;
        }

        CalibrationTickAnchors anchors = proposal.Anchors!;
        if (!SetCalibrationAnchorsFromWorkingFrame(anchors)) return;
        SuggestedYMaximum = proposal.YMaximum!.Value.ToString("R", CultureInfo.InvariantCulture);
        SuggestedFirstSession = proposal.FirstSession!.Value.ToString(CultureInfo.InvariantCulture);
        SuggestedFinalSession = proposal.FinalSession!.Value.ToString("R", CultureInfo.InvariantCulture);
        UpdateAxisSuggestionOverlay();
        AxisSuggestionReviewed = false;
        _calibrationTickProposal = proposal;
        PopulateCalibrationPairedTickRows(proposal);
        _calibrationTickProposalStatusText = FormatLocalizedString(proposal.UsesEstimatedPositions || proposal.UsesUserEnteredSessionPosition
                ? LocalizationKeys.ManualCalibrationTicksEstimatedComplete : LocalizationKeys.ManualCalibrationTicksComplete,
            proposal.PairedTicks.Count);
        OnPropertyChanged(nameof(HasCalibrationTickProposal));
        OnPropertyChanged(nameof(CalibrationTickProposalStatusText));
        OnPropertyChanged(nameof(HasCalibrationTickProposalStatus));
        OnPropertyChanged(nameof(AxisSuggestionSummary));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private bool PopulatePartialCalibrationAxes(CalibrationTickProposalResult proposal)
    {
        SuggestedFirstSession = proposal.XReviewDraft?.FirstSession.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SuggestedFinalSession = proposal.XReviewDraft?.FinalSession.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;
        SuggestedYMaximum = proposal.YReviewDraft?.YMaximum.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;
        AxisSuggestionReviewed = false;
        if (_suggestedAnchors.Length != 3) return false;
        CalibrationOcrPoint[] working = _suggestedAnchors.Select(point =>
            _calibrationLabelSuggestion?.WorkingFrame?.ToDerived(new(point.X, point.Y)) ?? new(point.X, point.Y)).ToArray();
        if (proposal.XReviewDraft is { } x)
        {
            working[0] = new(x.FirstPixelPosition, working[0].Y);
            working[1] = new(x.FirstPixelPosition, working[1].Y);
            working[2] = new(x.FinalPixelPosition, working[2].Y);
        }
        if (proposal.YReviewDraft is { } y)
        {
            working[0] = new(working[0].X, y.ZeroPixelPosition);
            working[1] = new(working[1].X, y.MaximumPixelPosition);
            working[2] = new(working[2].X, y.ZeroPixelPosition);
        }
        if (!SetCalibrationAnchorsFromWorkingFrame(new(working[0], working[1], working[2]))) return false;
        UpdateAxisSuggestionOverlay();
        OnPropertyChanged(nameof(AxisSuggestionSummary));
        return true;
    }

    private JsonElement? CreateReviewedLabelAudit(ManualCalibrationRequest confirmed)
    {
        if (confirmed.ReviewedTickEvidence is not null || _displayedLabelEvidence.Fit is not { } draft ||
            _calibrationLabelSuggestion is not { Response.Request: { } request } source ||
            !ReferenceEquals(_displayedLabelEvidence.Source, source) || !CanRefitCalibrationLabelsCore())
            return null;

        var pairs = draft.PairedTicks.ToLookup(pair => pair.Label.RegionId, StringComparer.Ordinal);
        var included = CalibrationLabelRows.Where(row => row.IsIncluded).ToLookup(row => row.Proposal.RegionId, StringComparer.Ordinal);
        return JsonSerializer.SerializeToElement(new
        {
            method = "reviewed_ocr_labels", request_id = request.RequestId,
            source_sha256 = request.SourceFileSha256, ocr_frame_sha256 = request.PanelImageSha256,
            model_sha256 = source.Response.Runtime?.ModelSha256, rectified = source.IsRectified,
            fit_reasons = draft.Reasons, fit_current_at_apply = ReferenceEquals(draft, _calibrationTickProposal),
            readings = CalibrationLabelRows.Select(row =>
            {
                var proposal = row.Proposal;
                // Several candidates with the same identity cannot identify a unique fitted sample.
                var matches = pairs[proposal.RegionId];
                var pair = row.IsIncluded && included[proposal.RegionId].Count() == 1 && matches.Count() == 1 ? matches.First() : null;
                return new
                {
                    region_id = proposal.RegionId, axis = proposal.SuggestedAxis, role = proposal.Role, raw_text = proposal.Text,
                    confidence = proposal.Confidence, interval_label = proposal.IsIntervalXLabel, failure_code = proposal.FailureCode,
                    edited_text = row.HasCorrection ? row.CorrectedText : null, included = row.IsIncluded,
                    original_polygon = source.OriginalPolygon(proposal),
                    parent_reading = proposal.ParentReading is not { } parent ? null : new
                    {
                        region_id = parent.RegionId, raw_text = parent.Text, confidence = parent.Confidence,
                        original_polygon = parent.Polygon.Select(p => source.WorkingFrame?.ToOriginal(p) ?? p),
                    },
                    pair = pair is null ? null : new
                    {
                        value = pair.Value, estimated_position = pair.IsEstimated,
                        original_position = source.OriginalTickPoint(pair.Axis, pair.PixelPosition),
                    },
                };
            }),
            confirmed = new
            {
                first_x = confirmed.FirstVisibleSession, last_x = confirmed.XMaximum, upper_y = confirmed.YMaximum,
                printed_x_coordinates = confirmed.UsesPrintedXCoordinates,
                anchors = new[] { confirmed.Session1Y0, confirmed.Session1YMaximum, confirmed.SessionMaximumY0 },
            },
        });
    }

    private void PopulateCalibrationPairedTickRows(CalibrationTickProposalResult proposal)
    {
        _displayedLabelEvidence = (_calibrationLabelSuggestion, proposal);
        CalibrationPairedTickRows.Clear();
        foreach (CalibrationPairedTick pair in proposal.PairedTicks)
        {
            string axis = GetLocalizedString(pair.Axis == "X"
                ? LocalizationKeys.ManualCalibrationTicksAxisX : LocalizationKeys.ManualCalibrationTicksAxisY);
            if (_calibrationLabelSuggestion is { IsRectified: true } result)
            {
                CalibrationOcrPoint point = result.OriginalTickPoint(pair.Axis, pair.PixelPosition);
                CalibrationPairedTickRows.Add(FormatLocalizedString(pair.IsEstimated
                        ? LocalizationKeys.ManualCalibrationTicksEstimatedPointRow : LocalizationKeys.ManualCalibrationTicksPairedPointRow,
                    axis, pair.Value.ToString("R", CultureInfo.InvariantCulture),
                    point.X.ToString("0.##", CultureInfo.CurrentCulture), point.Y.ToString("0.##", CultureInfo.CurrentCulture)));
                continue;
            }
            CalibrationPairedTickRows.Add(FormatLocalizedString(pair.IsEstimated
                    ? LocalizationKeys.ManualCalibrationTicksEstimatedRow : LocalizationKeys.ManualCalibrationTicksPairedRow,
                axis, pair.Value.ToString("R", CultureInfo.InvariantCulture), pair.PixelPosition));
        }
    }

    private bool SetCalibrationAnchorsFromWorkingFrame(CalibrationTickAnchors anchors)
    {
        CalibrationTickAnchors original = _calibrationLabelSuggestion?.OriginalAnchors(anchors) ?? anchors;
        if (_calibrationLabelSuggestion?.ContainsOriginal(original) == false)
        {
            _calibrationTickProposalStatusText = GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsFailureInvalidFrame);
            AxisSuggestionReviewed = false;
            OnPropertyChanged(nameof(CalibrationTickProposalStatusText));
            OnPropertyChanged(nameof(HasCalibrationTickProposalStatus));
            return false;
        }
        _suggestedAnchors = [new(original.FirstSessionY0.X, original.FirstSessionY0.Y),
            new(original.FirstSessionYMaximum.X, original.FirstSessionYMaximum.Y), new(original.FinalSessionY0.X, original.FinalSessionY0.Y)];
        return true;
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
        if (hadProposal) OnPropertyChanged(nameof(HasPrintedXReviewDraft));
        if (hadStatus) OnPropertyChanged(nameof(CalibrationTickProposalStatusText));
        if (hadStatus) OnPropertyChanged(nameof(HasCalibrationTickProposalStatus));
    }
}
