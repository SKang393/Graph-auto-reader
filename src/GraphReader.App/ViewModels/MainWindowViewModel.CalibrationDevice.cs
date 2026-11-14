// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.ComponentModel;
using System.Windows.Input;
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
    private CalibrationOcrDeviceOptimizer? _calibrationDeviceOptimizer;
    private SynchronizationContext? _calibrationDeviceUiContext;
    private int _calibrationDeviceUiThread;
    private bool _calibrationDevicesDisposed;

    public ICommand CheckCalibrationDevicesCommand { get; private set; } = null!;
    public ICommand CancelCalibrationDeviceCheckCommand { get; private set; } = null!;
    public bool CanCheckCalibrationDevices => !IsBusy && _calibrationDeviceOptimizer is { IsChecking: false };
    public bool CanCancelCalibrationDeviceCheck => _calibrationDeviceOptimizer is { } optimizer &&
        (optimizer.IsChecking || optimizer.State == CalibrationOcrDeviceOptimizerState.Waiting);

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
            if (_calibrationDeviceOptimizer is { IsChecking: true })
                return GetLocalizedString(LocalizationKeys.SettingsAxisLabelsChecking);
            string? key = _calibrationDeviceWarningCode switch
            {
                CalibrationOcrDeviceSettings.SettingsWriteFailed => LocalizationKeys.SettingsAxisLabelsSaveFailed,
                CalibrationOcrDeviceSettings.AdapterUnavailableWarning => LocalizationKeys.SettingsAxisLabelsDeviceUnavailable,
                CalibrationOcrDeviceSettings.AdapterEnumerationFailedWarning => LocalizationKeys.SettingsAxisLabelsEnumerationFailed,
                CalibrationOcrDeviceSettings.SettingsInvalidWarning => LocalizationKeys.SettingsAxisLabelsSavedChoiceInvalid,
                _ => null,
            };
            if (key is not null) return GetLocalizedString(key);
            if (_calibrationDeviceOptimizer is { } optimizer)
            {
                if (optimizer.IsChecking) return GetLocalizedString(LocalizationKeys.SettingsAxisLabelsChecking);
                if (optimizer.State == CalibrationOcrDeviceOptimizerState.Failed)
                    return GetLocalizedString(optimizer.ErrorCode switch
                    {
                        CalibrationOcrDeviceOptimizer.BaselineFailed => LocalizationKeys.SettingsAxisLabelsBaselineFailed,
                        CalibrationOcrDeviceOptimizer.IdentityChanged => LocalizationKeys.SettingsAxisLabelsProfileStale,
                        CalibrationOcrDeviceOptimizer.CheckTimedOut => LocalizationKeys.SettingsAxisLabelsCheckTimedOut,
                        CalibrationOcrDeviceSettings.SettingsWriteFailed => LocalizationKeys.SettingsAxisLabelsSaveFailed,
                        _ => LocalizationKeys.SettingsAxisLabelsCheckFailed,
                    });
                if (optimizer.State == CalibrationOcrDeviceOptimizerState.Cancelled)
                    return GetLocalizedString(LocalizationKeys.SettingsAxisLabelsCheckCancelled);
                if (_calibrationOcrDevices?.Preference.Provider != "Auto" && optimizer.State == CalibrationOcrDeviceOptimizerState.Ready)
                    return GetLocalizedString(LocalizationKeys.SettingsAxisLabelsCheckedOverride);
                if (optimizer.ErrorCode == CalibrationOcrDeviceOptimizer.GpuFallback)
                    return GetLocalizedString(LocalizationKeys.SettingsAxisLabelsCheckGpuFallback);
            }
            if (_calibrationOcrDevices is { Preference.Provider: "Auto" } settings)
                return settings.NeedsAutomaticProfile
                    ? GetLocalizedString(LocalizationKeys.SettingsAxisLabelsAutomaticPending)
                    : FormatLocalizedString(LocalizationKeys.SettingsAxisLabelsAutomaticSelected, DeviceLabel(settings.SelectedDevice));
            return _calibrationDeviceSaved ? GetLocalizedString(LocalizationKeys.SettingsAxisLabelsSaved) : string.Empty;
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

    private void InitializeCalibrationDevices(CalibrationOcrDeviceSettings? settings, CalibrationOcrDeviceOptimizer? optimizer)
    {
        _calibrationOcrDevices = settings;
        _calibrationDeviceOptimizer = optimizer;
        _calibrationDeviceUiContext = SynchronizationContext.Current;
        _calibrationDeviceUiThread = Environment.CurrentManagedThreadId;
        _calibrationDeviceWarningCode = settings?.LoadWarningCode;
        CheckCalibrationDevicesCommand = new AsyncRelayCommand(CheckCalibrationDevicesAsync, () => CanCheckCalibrationDevices);
        CancelCalibrationDeviceCheckCommand = new RelayCommand(_ => optimizer?.Cancel(), _ => CanCancelCalibrationDeviceCheck);
        RefreshCalibrationDeviceChoices();
        if (settings is not null) settings.Changed += OnCalibrationDeviceExternalChanged;
        if (optimizer is not null) optimizer.Changed += OnCalibrationDeviceExternalChanged;
        PropertyChanged += OnCalibrationDeviceStateChanged;
        optimizer?.SetForegroundBusy(IsBusy);
        optimizer?.Start();
    }

    private void RefreshCalibrationDeviceChoices()
    {
        IReadOnlyList<CalibrationOcrDeviceSelection> devices = _calibrationOcrDevices?.AvailableDevices ?? [CalibrationOcrDeviceSelection.Cpu];
        CalibrationOcrDeviceSelection selected = _calibrationOcrDevices?.Preference ?? CalibrationOcrDeviceSelection.Cpu;
        CalibrationOcrDeviceChoices = devices.Select(device => new CalibrationOcrDeviceChoice(device,
            device.Provider == "Auto"
                ? _calibrationOcrDevices?.NeedsAutomaticProfile == true
                    ? GetLocalizedString(LocalizationKeys.SettingsAxisLabelsAutomaticPending)
                    : FormatLocalizedString(LocalizationKeys.SettingsAxisLabelsAutomaticSelected,
                        DeviceLabel(_calibrationOcrDevices?.SelectedDevice ?? CalibrationOcrDeviceSelection.Cpu))
                : DeviceLabel(device))).ToArray();
        _selectedCalibrationOcrDevice = CalibrationOcrDeviceChoices.FirstOrDefault(choice => choice.Device == selected);
        OnPropertyChanged(nameof(CalibrationOcrDeviceChoices));
        OnPropertyChanged(nameof(SelectedCalibrationOcrDevice));
    }

    private void OnCalibrationDeviceStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(IsBusy))
        {
            _calibrationDeviceOptimizer?.SetForegroundBusy(IsBusy);
            OnPropertyChanged(nameof(CanChangeCalibrationOcrDevice));
            NotifyCalibrationDeviceCheckState();
        }
        if (args.PropertyName == nameof(CalibrationLabelStatusText) &&
            _calibrationLabelSuggestion?.Response.Runtime is { } runtime)
        {
            _lastCalibrationDeviceRuntime = runtime;
            OnPropertyChanged(nameof(HasCalibrationDeviceRuntime));
            OnPropertyChanged(nameof(CalibrationDeviceRuntimeText));
        }
    }

    private string DeviceLabel(CalibrationOcrDeviceSelection device) => device.Provider == "Cpu"
        ? GetLocalizedString(LocalizationKeys.SettingsAxisLabelsCpu)
        : FormatLocalizedString(LocalizationKeys.SettingsAxisLabelsGpu,
            string.IsNullOrWhiteSpace(device.AdapterName)
                ? FormatLocalizedString(LocalizationKeys.SettingsAxisLabelsUnnamedGpu,
                    device.AdapterIndex?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty).Trim()
                : device.AdapterName);

    private async Task CheckCalibrationDevicesAsync(CancellationToken token)
    {
        if (!CanCheckCalibrationDevices || _calibrationDeviceOptimizer is null) return;
        _calibrationDeviceSaved = false;
        try { await _calibrationDeviceOptimizer.CheckAsync(token); }
        catch (OperationCanceledException) { }
        NotifyCalibrationDeviceCheckState();
    }

    private void OnCalibrationDeviceExternalChanged(object? sender, EventArgs args)
    {
        void Refresh()
        {
            if (_calibrationDevicesDisposed) return;
            _calibrationDeviceWarningCode = _calibrationOcrDevices?.LoadWarningCode;
            RefreshCalibrationDeviceChoices();
            NotifyCalibrationDeviceCheckState();
        }
        if (_calibrationDeviceUiContext is not null && Environment.CurrentManagedThreadId != _calibrationDeviceUiThread)
            _calibrationDeviceUiContext.Post(_ => Refresh(), null);
        else Refresh();
    }

    private void NotifyCalibrationDeviceCheckState()
    {
        OnPropertyChanged(nameof(CanCheckCalibrationDevices));
        OnPropertyChanged(nameof(CanCancelCalibrationDeviceCheck));
        OnPropertyChanged(nameof(CalibrationDeviceStatusText));
        AsyncRelayCommand.RaiseCanExecuteChanged();
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void DisposeCalibrationDevices()
    {
        _calibrationDevicesDisposed = true;
        PropertyChanged -= OnCalibrationDeviceStateChanged;
        if (_calibrationOcrDevices is not null) _calibrationOcrDevices.Changed -= OnCalibrationDeviceExternalChanged;
        if (_calibrationDeviceOptimizer is not null)
        {
            _calibrationDeviceOptimizer.Changed -= OnCalibrationDeviceExternalChanged;
            _calibrationDeviceOptimizer.Dispose();
        }
    }
}
