// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.Integration.Workflow;
using GraphReader.Ocr;

namespace GraphReader.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private WorkspaceCalibrationLabelSuggestion? _calibrationLabelSuggestion;
    private CalibrationLabelRowViewModel? _selectedCalibrationLabel;
    private string _calibrationLabelStatusOverride = string.Empty;
    private int _calibrationLabelGeneration;
    private CancellationTokenSource? _calibrationLabelReadCancellation;
    private Point[] _calibrationLabelReadAnchors = [];

    public ObservableCollection<CalibrationLabelRowViewModel> CalibrationLabelRows { get; } = [];
    public ICommand ReadCalibrationLabelsCommand { get; private set; } = null!;
    public ICommand UseCalibrationLabelForYMaximumCommand { get; private set; } = null!;
    public ICommand UseCalibrationLabelForFirstSessionCommand { get; private set; } = null!;
    public ICommand UseCalibrationLabelForFinalSessionCommand { get; private set; } = null!;

    public bool HasCalibrationLabelSuggestion => _calibrationLabelSuggestion is not null;
    public bool HasCalibrationLabelWarning => _calibrationLabelSuggestion is { } result &&
        (result.IsRectified || result.Response.FailureCode is not null ||
         result.Response.XSequence is { } x && (x.NeedsReview || x.RejectedCount > 0) ||
         result.Response.YSequence is { } y && (y.NeedsReview || y.RejectedCount > 0) ||
         result.Response.Proposals.Any(proposal => proposal.FailureCode is not null));
    public string CalibrationLabelWarningText => _calibrationLabelSuggestion is { IsRectified: true }
        ? GetLocalizedString(LocalizationKeys.ManualCalibrationRectifiedWarning) + "\n" +
            GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsWarning)
        : HasCalibrationLabelWarning ? GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsWarning) : string.Empty;
    public string CalibrationLabelZeroReminder => GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsZeroReminder);

    public string CalibrationLabelStatusText
    {
        get
        {
            if (_calibrationLabelSuggestion is not { } result)
            {
                if (_calibrationLabelStatusOverride.Length > 0) return _calibrationLabelStatusOverride;
                if (_axisSuggestion is null) return string.Empty;
                return _workspaceService is ICalibrationLabelSuggestionWorkspace { CanReadCalibrationLabels: true }
                    ? GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsNotRead)
                    : GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsReaderUnavailable);
            }
            if (result.Response.FailureCode is { } failureCode)
                return FormatLocalizedString(LocalizationKeys.ManualCalibrationLabelsReadFailure, GetCalibrationLabelFailure(failureCode));
            if (result.Response.Proposals.Count == 0)
                return GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsEmpty);
            return FormatLocalizedString(LocalizationKeys.ManualCalibrationLabelsCount, result.Response.Proposals.Count);
        }
    }

    public CalibrationLabelRowViewModel? SelectedCalibrationLabel
    {
        get => _selectedCalibrationLabel;
        set
        {
            if (SetProperty(ref _selectedCalibrationLabel, value))
            {
                ResetAxisSuggestionReview();
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void InitializeCalibrationLabelCommands()
    {
        ReadCalibrationLabelsCommand = new AsyncRelayCommand(ReadCalibrationLabelsAsync,
            () => !IsBusy && CanReadCalibrationLabelsCore());
        RefitSelectedCalibrationLabelsCommand = new RelayCommand(_ => RefitSelectedCalibrationLabels(),
            _ => !IsBusy && !_refittingCalibrationLabels && CanRefitCalibrationLabelsCore());
        UseCalibrationLabelForYMaximumCommand = new RelayCommand(_ => UseSelectedCalibrationLabelForYMaximum(), _ => CanUseSelectedCalibrationLabelForYMaximum());
        UseCalibrationLabelForFirstSessionCommand = new RelayCommand(_ => UseSelectedCalibrationLabelForFirstSession(), _ => CanUseSelectedCalibrationLabelForFirstSession());
        UseCalibrationLabelForFinalSessionCommand = new RelayCommand(_ => UseSelectedCalibrationLabelForFinalSession(), _ => CanUseSelectedCalibrationLabelForFinalSession());
    }

    public Task ReadCalibrationLabelsAsync(CancellationToken cancellationToken) => RunBusyAsync(async token =>
    {
        if (!CanReadCalibrationLabelsCore() || SelectedTab is not { } tab ||
            _axisSuggestion is not { } suggestion || _workspaceService is not ICalibrationLabelSuggestionWorkspace workspace)
            return;

        bool hadComputedSelection = _calibrationTickProposal is not null || CalibrationLabelRows.Any(row => !row.IsIncluded);
        if (hadComputedSelection) ClearCalibrationLabelComputedValues();
        ClearCalibrationLabelResults(clearPickedValues: false);
        ResetAxisSuggestionReview();
        int generation = _calibrationLabelGeneration;
        Point[] anchors = _suggestedAnchors.ToArray();
        _calibrationLabelStatusOverride = GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsReading);
        OnPropertyChanged(nameof(CalibrationLabelStatusText));

        WorkspaceCalibrationLabelSuggestion? result;
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _calibrationLabelReadCancellation = readCancellation;
        try
        {
            result = await workspace.ReadCalibrationLabelsAsync(suggestion, readCancellation.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && generation != _calibrationLabelGeneration)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            if (generation == _calibrationLabelGeneration)
            {
                _calibrationLabelStatusOverride = FormatLocalizedString(LocalizationKeys.ManualCalibrationLabelsReadFailure,
                    GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsFailureCancelled));
                OnPropertyChanged(nameof(CalibrationLabelStatusText));
            }
            throw;
        }
        catch (ProductionWorkflowStageException error) when (error.Failure.Code == "CALIBRATION_RECTIFICATION_INVALID_FRAME")
        {
            if (generation == _calibrationLabelGeneration)
            {
                _calibrationLabelStatusOverride = GetCalibrationLabelFailure(error.Failure.Code);
                OnPropertyChanged(nameof(CalibrationLabelStatusText));
            }
            return;
        }
        catch
        {
            if (generation == _calibrationLabelGeneration)
            {
                _calibrationLabelStatusOverride = FormatLocalizedString(LocalizationKeys.ManualCalibrationLabelsReadFailure,
                    GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsFailureOther));
                OnPropertyChanged(nameof(CalibrationLabelStatusText));
            }
            throw;
        }
        finally
        {
            if (ReferenceEquals(_calibrationLabelReadCancellation, readCancellation))
                _calibrationLabelReadCancellation = null;
        }
        token.ThrowIfCancellationRequested();
        if (generation != _calibrationLabelGeneration || !ReferenceEquals(SelectedTab, tab) ||
            !ReferenceEquals(_axisSuggestion, suggestion) || !_suggestedAnchors.SequenceEqual(anchors) ||
            result is null || !workspace.IsCalibrationLabelSuggestionCurrent(result))
        {
            if (generation == _calibrationLabelGeneration)
            {
                _calibrationLabelStatusOverride = GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsUnavailable);
                OnPropertyChanged(nameof(CalibrationLabelStatusText));
            }
            return;
        }

        _calibrationLabelSuggestion = result;
        _calibrationLabelReadAnchors = anchors;
        CalibrationLabelRows.Clear();
        foreach (CalibrationOcrProposal proposal in result.Response.Proposals)
            CalibrationLabelRows.Add(new CalibrationLabelRowViewModel(proposal, FormatCalibrationLabelRow(proposal),
                CanChangeCalibrationLabelInclusion, OnCalibrationLabelInclusionChanged));
        SelectedCalibrationLabel = null;
        OnPropertyChanged(nameof(HasCalibrationLabelSuggestion));
        OnPropertyChanged(nameof(HasCalibrationLabelWarning));
        OnPropertyChanged(nameof(CalibrationLabelWarningText));
        OnPropertyChanged(nameof(CalibrationLabelStatusText));
        RelayCommand.RaiseCanExecuteChanged();
    }, cancellationToken);

    private bool CanReadCalibrationLabelsCore() => EditorMode != ManualEditorMode.Calibration &&
        _calibrationAnchors.Count == 0 && SelectedTab is not null &&
        _axisSuggestion is { } suggestion && (suggestion.Geometry is not null || suggestion.AffineGeometry is not null) &&
        _suggestedAnchors.Length == 3 && SelectedTab.TabId == suggestion.TabId &&
        _workspaceService is IAxisSuggestionWorkspace axes && axes.IsAxisSuggestionCurrent(suggestion) &&
        _workspaceService is ICalibrationLabelSuggestionWorkspace labels && labels.CanReadCalibrationLabels;

    private bool CanRefitCalibrationLabelsCore() => CanReadCalibrationLabelsCore() &&
        _calibrationLabelSuggestion is { Response.FailureCode: null } result &&
        ReferenceEquals(_axisSuggestion, result.AxisSuggestion) &&
        _workspaceService is ICalibrationLabelSuggestionWorkspace workspace && workspace.IsCalibrationLabelSuggestionCurrent(result) &&
        CalibrationLabelRows.Count == result.Response.Proposals.Count &&
        CalibrationLabelRows.Zip(result.Response.Proposals).All(pair => ReferenceEquals(pair.First.Proposal, pair.Second));

    private bool CanChangeCalibrationLabelInclusion(CalibrationLabelRowViewModel row) =>
        !IsBusy && !_refittingCalibrationLabels && CalibrationLabelRows.Contains(row) && CanRefitCalibrationLabelsCore();

    private void OnCalibrationLabelInclusionChanged(CalibrationLabelRowViewModel row)
    {
        _ = row;
        ClearCalibrationLabelComputedValues();
        _calibrationTickProposalStatusText = GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsSelectionChanged);
        OnPropertyChanged(nameof(CalibrationTickProposalStatusText));
        OnPropertyChanged(nameof(HasCalibrationTickProposalStatus));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private bool TryGetSelectedNumericCalibrationLabel(out double value)
    {
        value = 0;
        if (_calibrationLabelSuggestion is not { } result || SelectedCalibrationLabel is not { } row ||
            _workspaceService is not ICalibrationLabelSuggestionWorkspace workspace ||
            !workspace.IsCalibrationLabelSuggestionCurrent(result) ||
            !ReferenceEquals(_axisSuggestion, result.AxisSuggestion) ||
            !result.Response.Proposals.Contains(row.Proposal) || row.Proposal.IsAmbiguousIntervalXLabel)
            return false;
        CalibrationOcrProposal effective = row.EffectiveProposal;
        if (effective.FailureCode is not null || !effective.NumericLiteral || effective.NumericValue is not double parsed || !double.IsFinite(parsed) ||
            GraphNumericParser.Parse(effective.Text).Alternatives.Count > 1)
            return false;
        value = parsed;
        return true;
    }

    private bool CanUseSelectedCalibrationLabelForYMaximum() => !IsBusy &&
        TryGetSelectedNumericCalibrationLabel(out double value) && value > 0;

    private bool CanUseSelectedCalibrationLabelForFirstSession()
    {
        if (IsBusy || !TryGetSelectedNumericCalibrationLabel(out double value) ||
            !SuggestedUsesPrintedXCoordinates && (value <= 0 || value != Math.Truncate(value) || value > int.MaxValue))
            return false;
        return !double.TryParse(SuggestedFinalSession, NumberStyles.Float, CultureInfo.InvariantCulture, out double final) ||
            !double.IsFinite(final) || value < final;
    }

    private bool CanUseSelectedCalibrationLabelForFinalSession() => !IsBusy &&
        TryGetSelectedNumericCalibrationLabel(out double value) && double.TryParse(
            SuggestedFirstSession, NumberStyles.Float, CultureInfo.InvariantCulture, out double first) && double.IsFinite(first) &&
        (SuggestedUsesPrintedXCoordinates || first > 0 && first == Math.Truncate(first) && first <= int.MaxValue) && value > first;

    private void UseSelectedCalibrationLabelForYMaximum()
    {
        if (!CanUseSelectedCalibrationLabelForYMaximum() || !TryGetSelectedNumericCalibrationLabel(out double value)) return;
        SuggestedYMaximum = value.ToString("R", CultureInfo.InvariantCulture);
        ResetAxisSuggestionReview();
    }

    private void UseSelectedCalibrationLabelForFirstSession()
    {
        if (!CanUseSelectedCalibrationLabelForFirstSession() || !TryGetSelectedNumericCalibrationLabel(out double value)) return;
        SuggestedFirstSession = value.ToString("R", CultureInfo.InvariantCulture);
        ResetAxisSuggestionReview();
    }

    private void UseSelectedCalibrationLabelForFinalSession()
    {
        if (!CanUseSelectedCalibrationLabelForFinalSession() || !TryGetSelectedNumericCalibrationLabel(out double value)) return;
        SuggestedFinalSession = value.ToString("R", CultureInfo.InvariantCulture);
        ResetAxisSuggestionReview();
    }

    private void ClearCalibrationLabelResults(bool clearPickedValues)
    {
        ClearCalibrationTickProposal();
        _calibrationLabelGeneration++;
        _calibrationLabelReadCancellation?.Cancel();
        _calibrationLabelSuggestion = null;
        _displayedLabelEvidence = default;
        _calibrationLabelReadAnchors = [];
        _calibrationLabelStatusOverride = string.Empty;
        CalibrationLabelRows.Clear();
        SelectedCalibrationLabel = null;
        if (clearPickedValues)
            SuggestedYMaximum = SuggestedFirstSession = SuggestedFinalSession = string.Empty;
        ResetAxisSuggestionReview();
        OnPropertyChanged(nameof(HasCalibrationLabelSuggestion));
        OnPropertyChanged(nameof(HasCalibrationLabelWarning));
        OnPropertyChanged(nameof(CalibrationLabelWarningText));
        OnPropertyChanged(nameof(CalibrationLabelStatusText));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void ResetAxisSuggestionReview()
    {
        ClearCalibrationTickProposal();
        AxisSuggestionReviewed = false;
        RelayCommand.RaiseCanExecuteChanged();
    }

    private string FormatCalibrationLabelRow(CalibrationOcrProposal proposal)
    {
        IReadOnlyList<CalibrationOcrPoint> polygon = _calibrationLabelSuggestion?.OriginalPolygon(proposal) ?? proposal.Polygon;
        double? x = polygon.Count > 0 ? polygon.Average(point => point.X) : null;
        double? y = polygon.Count > 0 ? polygon.Average(point => point.Y) : null;
        string position = x is double px && y is double py
            ? FormatLocalizedString(LocalizationKeys.ManualCalibrationLabelsPosition,
                px.ToString("0.##", CultureInfo.CurrentCulture), py.ToString("0.##", CultureInfo.CurrentCulture))
            : GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsPositionUnknown);
        string roleKey = proposal.Role switch
        {
            "XTick" => LocalizationKeys.ManualCalibrationLabelsRoleXTick,
            "YTick" => LocalizationKeys.ManualCalibrationLabelsRoleYTick,
            _ => LocalizationKeys.ManualCalibrationLabelsRoleOther,
        };
        string role = GetLocalizedString(roleKey);
        string confidence = proposal.Confidence.ToString("P0", CultureInfo.CurrentCulture);
        string? detail = proposal.FailureCode is { } failureCode ? GetCalibrationLabelFailure(failureCode) : null;
        if (proposal.IsAmbiguousIntervalXLabel)
        {
            string warning = GetLocalizedString(LocalizationKeys.ManualCalibrationLabelsAmbiguousInterval);
            detail = detail is null ? warning : $"{detail} {warning}";
        }
        return detail is not null
            ? FormatLocalizedString(LocalizationKeys.ManualCalibrationLabelsRowWithFailure,
                proposal.Text, position, role, confidence, detail)
            : FormatLocalizedString(LocalizationKeys.ManualCalibrationLabelsRow,
                proposal.Text, position, role, confidence);
    }

    private string GetCalibrationLabelFailure(string failureCode)
    {
        string key = failureCode switch
        {
            "CALIBRATION_RECTIFICATION_INVALID_FRAME" => LocalizationKeys.ManualCalibrationRectificationFailed,
            CalibrationOcrReasonCodes.Unavailable => LocalizationKeys.ManualCalibrationLabelsFailureUnavailable,
            CalibrationOcrReasonCodes.InvalidInput => LocalizationKeys.ManualCalibrationLabelsFailureInvalidInput,
            CalibrationOcrReasonCodes.AssetMismatch => LocalizationKeys.ManualCalibrationLabelsFailureAssetMismatch,
            CalibrationOcrReasonCodes.ResourceUnavailable => LocalizationKeys.ManualCalibrationLabelsFailureResourceUnavailable,
            CalibrationOcrReasonCodes.Timeout => LocalizationKeys.ManualCalibrationLabelsFailureTimeout,
            CalibrationOcrReasonCodes.InvalidFrame => LocalizationKeys.ManualCalibrationLabelsFailureInvalidFrame,
            CalibrationOcrReasonCodes.Cancelled => LocalizationKeys.ManualCalibrationLabelsFailureCancelled,
            CalibrationOcrReasonCodes.RecognitionFailed => LocalizationKeys.ManualCalibrationLabelsFailureRecognition,
            CalibrationOcrReasonCodes.TooManyCandidates => LocalizationKeys.ManualCalibrationLabelsFailureTooManyCandidates,
            CalibrationOcrReasonCodes.NonnumericReading => LocalizationKeys.ManualCalibrationLabelsFailureNonnumeric,
            _ => LocalizationKeys.ManualCalibrationLabelsFailureOther,
        };
        return GetLocalizedString(key);
    }

    private void UpdateAxisSuggestionGeometryFromCorrectedAnchors()
    {
        if (_axisSuggestion is not { } suggestion || _suggestedAnchors.Length != 3) return;
        bool tiltedRectangleCorrection = suggestion.Geometry is not null &&
            !ManualCalibrationMapper.HasLegacyAlignment(
                new(_suggestedAnchors[0].X, _suggestedAnchors[0].Y),
                new(_suggestedAnchors[1].X, _suggestedAnchors[1].Y),
                new(_suggestedAnchors[2].X, _suggestedAnchors[2].Y));
        if (suggestion.IsUserDefined || tiltedRectangleCorrection)
        {
            if (_workspaceService is not IAxisSuggestionWorkspace axes || !axes.IsAxisSuggestionCurrent(suggestion))
            {
                ClearAxisSuggestion();
                SetStatus(GetLocalizedString(LocalizationKeys.ManualAxisDraftInvalid));
                return;
            }
            WorkspaceAxisSuggestion? corrected = (_workspaceService as IManualAxisDraftWorkspace)?.CreateManualAxisDraft(
                suggestion.TabId, _suggestedAnchors.Select(point => new GraphReader.Axis.PixelPoint(point.X, point.Y)).ToArray());
            if (corrected is null)
            {
                ClearAxisSuggestion();
                SetStatus(GetLocalizedString(LocalizationKeys.ManualAxisDraftInvalid));
                return;
            }
            _axisSuggestion = corrected;
            OnPropertyChanged(nameof(HasAffineAxisSuggestion));
            OnPropertyChanged(nameof(IsManualAxisDraft));
            OnPropertyChanged(nameof(HasAxisGeometryReviewWarning));
            OnPropertyChanged(nameof(AxisGeometryReviewWarningText));
            OnPropertyChanged(nameof(AxisSuggestionSummary));
            RelayCommand.RaiseCanExecuteChanged();
            return;
        }
        Point origin = _suggestedAnchors[0];
        Point top = _suggestedAnchors[1];
        Point final = _suggestedAnchors[2];
        if (suggestion.AffineGeometry is { } frame && suggestion.Geometry is null)
        {
            _axisSuggestion = suggestion with
            {
                AffineGeometry = new(new(origin.X, origin.Y), new(final.X, final.Y), new(top.X, top.Y), frame.Confidence),
            };
            AxisSuggestionReviewed = false;
            OnPropertyChanged(nameof(AxisSuggestionSummary));
            RelayCommand.RaiseCanExecuteChanged();
            return;
        }
        if (suggestion.Geometry is not { } rectangle) return;
        _axisSuggestion = suggestion with
        {
            Geometry = new GraphReader.Axis.RustAxisGeometry(
                (int)Math.Round(origin.X, MidpointRounding.AwayFromZero),
                (int)Math.Round(origin.Y, MidpointRounding.AwayFromZero),
                (int)Math.Round(final.X, MidpointRounding.AwayFromZero),
                (int)Math.Round(top.Y, MidpointRounding.AwayFromZero), rectangle.Confidence),
        };
        OnPropertyChanged(nameof(AxisSuggestionSummary));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void StartAxisCalibrationCorrection()
    {
        ClearManualAxisDraftSelection();
        bool preservePartialValues = _calibrationTickProposal is { IsComplete: false } proposal &&
            (proposal.XReviewDraft is not null || proposal.YReviewDraft is not null);
        ClearCalibrationLabelResults(clearPickedValues: !preservePartialValues);
        BeginCalibration();
    }
}

public sealed class CalibrationLabelRowViewModel(CalibrationOcrProposal proposal, string displayText,
    Func<CalibrationLabelRowViewModel, bool>? canChangeInclusion = null,
    Action<CalibrationLabelRowViewModel>? inclusionChanged = null) : ObservableObject
{
    private bool _isIncluded = true;
    private string _correctedText = proposal.Text;
    private string _displayText = displayText;
    public CalibrationOcrProposal Proposal { get; } = proposal;
    public string DisplayText => _displayText;
    internal void UpdateDisplayText(string value) => SetProperty(ref _displayText, value, nameof(DisplayText));
    public bool IsAxisTick => Proposal.SuggestedAxis is "X" or "Y";
    public bool CanEditCorrection => IsAxisTick && IsIncluded;
    public bool HasCorrection => !string.Equals(_correctedText, Proposal.Text, StringComparison.Ordinal);
    public bool HasValidCorrection => HasCorrection && EffectiveProposal is
        { NumericLiteral: true, FailureCode: null, NumericValue: double value } && double.IsFinite(value);
    public bool HasInvalidCorrection => IsIncluded && HasCorrection && !HasValidCorrection;
    public string CorrectedText
    {
        get => _correctedText;
        set
        {
            if (!CanEditCorrection || canChangeInclusion?.Invoke(this) == false)
            {
                OnPropertyChanged(nameof(CorrectedText));
                return;
            }
            if (SetProperty(ref _correctedText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasCorrection));
                OnPropertyChanged(nameof(HasValidCorrection));
                OnPropertyChanged(nameof(HasInvalidCorrection));
                inclusionChanged?.Invoke(this);
            }
        }
    }
    public ICommand ResetCorrectionCommand => new RelayCommand(_ => CorrectedText = Proposal.Text,
        _ => CanEditCorrection && HasCorrection && canChangeInclusion?.Invoke(this) != false);
    internal CalibrationTickFitEvidence FitEvidence => new(Proposal, HasCorrection ? CorrectedText : null);
    internal CalibrationOcrProposal EffectiveProposal => FitEvidence.EffectiveProposal;
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (canChangeInclusion?.Invoke(this) == false)
            {
                OnPropertyChanged(nameof(IsIncluded));
                return;
            }
            if (SetProperty(ref _isIncluded, value))
            {
                OnPropertyChanged(nameof(CanEditCorrection));
                OnPropertyChanged(nameof(HasInvalidCorrection));
                inclusionChanged?.Invoke(this);
            }
        }
    }
}
