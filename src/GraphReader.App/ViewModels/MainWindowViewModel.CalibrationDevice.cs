// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.ComponentModel;
using GraphReader.App.Localization;
using GraphReader.App.Services;

namespace GraphReader.App.ViewModels;

public sealed record CalibrationOcrDeviceChoice(CalibrationOcrDeviceSelection Device, string Label);

public sealed partial class MainWindowViewModel
{
    private CalibrationOcrDeviceSettings? _calibrationOcrDevices;
    private CalibrationOcrDeviceChoice? _selectedCalibrationOcrDevice;
    private string? _calibrationDeviceWarningCode;
    private bool _calibrationDeviceSaved;
    private CalibrationOcrRuntime? _lastCalibrationDeviceRuntime;

    public IReadOnlyList<CalibrationOcrDeviceChoice> CalibrationOcrDeviceChoices { get; private set; } = [];
    public bool CanChangeCalibrationOcrDevice => !IsBusy && _calibrationOcrDevices is not null;

    public CalibrationOcrDeviceChoice? SelectedCalibrationOcrDevice
    {
        get => _selectedCalibrationOcrDevice;
        set
        {
            if (Equals(value, _selectedCalibrationOcrDevice)) return;
            if (!CanChangeCalibrationOcrDevice || value is null ||
                !CalibrationOcrDeviceChoices.Contains(value))
            {
                OnPropertyChanged();
                return;
            }

            if (_calibrationOcrDevices!.TrySelect(value.Device, out string? failureCode))
            {
                _calibrationDeviceWarningCode = _calibrationOcrDevices.LoadWarningCode;
                _calibrationDeviceSaved = true;
                RefreshCalibrationDeviceChoices();
            }
            else
            {
                _calibrationDeviceWarningCode = failureCode;
                _calibrationDeviceSaved = false;
                RefreshCalibrationDeviceChoices();
            }
            OnPropertyChanged(nameof(CalibrationDeviceStatusText));
        }
    }

    public string CalibrationDeviceStatusText
    {
        get
        {
            string? key = _calibrationDeviceWarningCode switch
            {
                CalibrationOcrDeviceSettings.SettingsWriteFailed => LocalizationKeys.SettingsAxisLabelsSaveFailed,
                CalibrationOcrDeviceSettings.AdapterUnavailableWarning => LocalizationKeys.SettingsAxisLabelsDeviceUnavailable,
                CalibrationOcrDeviceSettings.AdapterEnumerationFailedWarning => LocalizationKeys.SettingsAxisLabelsEnumerationFailed,
                CalibrationOcrDeviceSettings.SettingsInvalidWarning => LocalizationKeys.SettingsAxisLabelsSavedChoiceInvalid,
                _ => _calibrationDeviceSaved ? LocalizationKeys.SettingsAxisLabelsSaved : null,
            };
            return key is null ? string.Empty : GetLocalizedString(key);
        }
    }

    public bool HasCalibrationDeviceRuntime => _lastCalibrationDeviceRuntime is not null;
    public string CalibrationDeviceRuntimeText => _lastCalibrationDeviceRuntime switch
    {
        { Provider: "Cpu", FallbackReason: { } reason } => GetLocalizedString(CalibrationDeviceFallbackKey(reason)),
        { Provider: "Cpu" } => GetLocalizedString(LocalizationKeys.SettingsAxisLabelsLastCpu),
        { Provider: "DirectMl", AdapterName: { Length: > 0 } name } =>
            FormatLocalizedString(LocalizationKeys.SettingsAxisLabelsLastGpu, name),
        _ => string.Empty,
    };

    private static string CalibrationDeviceFallbackKey(string reason) => reason switch
    {
        "CALIBRATION_OCR_ADAPTER_UNAVAILABLE" => LocalizationKeys.SettingsAxisLabelsFallbackAdapter,
        "CALIBRATION_OCR_GPU_MEMORY_BUDGET_UNAVAILABLE" => LocalizationKeys.SettingsAxisLabelsFallbackMemoryCheck,
        "CALIBRATION_OCR_GPU_MEMORY_HEADROOM_LOW" => LocalizationKeys.SettingsAxisLabelsFallbackMemoryLow,
        "CALIBRATION_OCR_DIRECTML_SESSION_FAILED" => LocalizationKeys.SettingsAxisLabelsFallbackStartup,
        "CALIBRATION_OCR_DIRECTML_INFERENCE_FAILED" => LocalizationKeys.SettingsAxisLabelsFallbackReading,
        _ => LocalizationKeys.SettingsAxisLabelsLastCpuFallback,
    };

    private void InitializeCalibrationDevices(CalibrationOcrDeviceSettings? settings)
    {
        _calibrationOcrDevices = settings;
        _calibrationDeviceWarningCode = settings?.LoadWarningCode;
        RefreshCalibrationDeviceChoices();
        PropertyChanged += OnCalibrationDeviceStateChanged;
    }

    private void RefreshCalibrationDeviceChoices()
    {
        IReadOnlyList<CalibrationOcrDeviceSelection> devices = _calibrationOcrDevices?.AvailableDevices ?? [CalibrationOcrDeviceSelection.Cpu];
        CalibrationOcrDeviceSelection selected = _calibrationOcrDevices?.SelectedDevice ?? CalibrationOcrDeviceSelection.Cpu;
        CalibrationOcrDeviceChoices = devices.Select(device => new CalibrationOcrDeviceChoice(device,
            device.Provider == "Cpu"
                ? GetLocalizedString(LocalizationKeys.SettingsAxisLabelsCpu)
                : FormatLocalizedString(LocalizationKeys.SettingsAxisLabelsGpu,
                    string.IsNullOrWhiteSpace(device.AdapterName)
                        ? FormatLocalizedString(LocalizationKeys.SettingsAxisLabelsUnnamedGpu,
                            device.AdapterIndex?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty).Trim()
                        : device.AdapterName))).ToArray();
        _selectedCalibrationOcrDevice = CalibrationOcrDeviceChoices.FirstOrDefault(choice => choice.Device == selected);
        OnPropertyChanged(nameof(CalibrationOcrDeviceChoices));
        OnPropertyChanged(nameof(SelectedCalibrationOcrDevice));
    }

    private void OnCalibrationDeviceStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(IsBusy)) OnPropertyChanged(nameof(CanChangeCalibrationOcrDevice));
        if (args.PropertyName == nameof(CalibrationLabelStatusText) &&
            _calibrationLabelSuggestion?.Response.Runtime is { } runtime)
        {
            _lastCalibrationDeviceRuntime = runtime;
            OnPropertyChanged(nameof(HasCalibrationDeviceRuntime));
            OnPropertyChanged(nameof(CalibrationDeviceRuntimeText));
        }
    }

    private void DisposeCalibrationDevices() => PropertyChanged -= OnCalibrationDeviceStateChanged;
}
