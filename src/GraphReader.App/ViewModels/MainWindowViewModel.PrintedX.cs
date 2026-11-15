// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Localization;

namespace GraphReader.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public string ManualFirstXLabel => GetLocalizedString(ManualUsesPrintedXCoordinates
        ? LocalizationKeys.ManualFirstPrintedX : LocalizationKeys.ManualFirstVisibleSession);
    public string ManualFinalXLabel => GetLocalizedString(ManualUsesPrintedXCoordinates
        ? LocalizationKeys.ManualFinalPrintedX : LocalizationKeys.ManualXMaximum);
    public string ManualXRangeHelp => GetLocalizedString(ManualUsesPrintedXCoordinates
        ? LocalizationKeys.ManualPrintedXRangeHelp : LocalizationKeys.ManualSessionRangeHelp);
    public string ManualFirstXInvalidText => GetLocalizedString(ManualUsesPrintedXCoordinates
        ? LocalizationKeys.ManualPrintedXInvalid : LocalizationKeys.ManualFirstVisibleSessionInvalid);

    private void OnCalibrationCultureChanged(object? sender, LocalizationChangedEventArgs e)
    {
        NotifyManualPrintedXLabelsChanged();
        NotifySuggestedPrintedXLabelsChanged();
        OnPropertyChanged(nameof(AxisSuggestionSummary));
        OnPropertyChanged(nameof(AxisGeometryReviewWarningText));
    }

    private void NotifyManualPrintedXLabelsChanged()
    {
        OnPropertyChanged(nameof(ManualFirstXLabel));
        OnPropertyChanged(nameof(ManualFinalXLabel));
        OnPropertyChanged(nameof(ManualXRangeHelp));
        OnPropertyChanged(nameof(ManualFirstXInvalidText));
    }
}
