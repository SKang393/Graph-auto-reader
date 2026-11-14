// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Collections.ObjectModel;
using System.Security;
using System.Text.Json;
using GraphReader.Inference;

namespace GraphReader.App.Services;

public sealed record CalibrationOcrDeviceSelection(string Provider, int? AdapterIndex = null,
    long? AdapterLuid = null, string? AdapterName = null)
{
    public static CalibrationOcrDeviceSelection Cpu { get; } = new("Cpu");
}

public sealed class CalibrationOcrDeviceSettings
{
    public const string SettingsInvalidWarning = "CALIBRATION_OCR_DEVICE_SETTINGS_INVALID";
    public const string AdapterUnavailableWarning = "CALIBRATION_OCR_ADAPTER_UNAVAILABLE";
    public const string AdapterEnumerationFailedWarning = "CALIBRATION_OCR_ADAPTER_ENUMERATION_FAILED";
    public const string SettingsWriteFailed = "CALIBRATION_OCR_DEVICE_SETTINGS_WRITE_FAILED";

    private const string SettingsFileName = "calibration-ocr-device.json";
    private const int MaximumSettingsBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly IDxgiAdapterInventory _inventory;
    private readonly string? _settingsPath;
    private IReadOnlyList<CalibrationOcrDeviceSelection> _availableDevices =
        Array.AsReadOnly(new[] { CalibrationOcrDeviceSelection.Cpu });
    private CalibrationOcrDeviceSelection _selectedDevice = CalibrationOcrDeviceSelection.Cpu;
    private string? _loadWarningCode;

    public CalibrationOcrDeviceSettings(string? settingsRoot, IDxgiAdapterInventory? inventory = null)
    {
        _inventory = inventory ?? new WindowsDxgiAdapterInventory();
        _settingsPath = string.IsNullOrWhiteSpace(settingsRoot)
            ? null
            : Path.Combine(Path.GetFullPath(settingsRoot), SettingsFileName);
        Load();
    }

    public IReadOnlyList<CalibrationOcrDeviceSelection> AvailableDevices
    {
        get { lock (_sync) return _availableDevices; }
    }

    public CalibrationOcrDeviceSelection SelectedDevice
    {
        get { lock (_sync) return _selectedDevice; }
    }

    public string? LoadWarningCode
    {
        get { lock (_sync) return _loadWarningCode; }
    }

    public bool TrySelect(CalibrationOcrDeviceSelection device, out string? failureCode)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_sync)
        {
            IReadOnlyList<CalibrationOcrDeviceSelection> available = EnumerateAvailable(out string? enumerationWarning);
            _availableDevices = available;
            CalibrationOcrDeviceSelection? resolved = Resolve(device, available, out failureCode);
            if (resolved is null)
            {
                failureCode = enumerationWarning ?? failureCode;
                _loadWarningCode = failureCode;
                return false;
            }

            if (!TryPersist(resolved))
            {
                _loadWarningCode = SettingsWriteFailed;
                failureCode = SettingsWriteFailed;
                return false;
            }

            _selectedDevice = resolved;
            _loadWarningCode = enumerationWarning;
            failureCode = null;
            return true;
        }
    }

    private void Load()
    {
        lock (_sync)
        {
            _availableDevices = EnumerateAvailable(out string? enumerationWarning);
            if (_settingsPath is null)
            {
                _loadWarningCode = enumerationWarning;
                return;
            }

            if (!File.Exists(_settingsPath))
            {
                _loadWarningCode = enumerationWarning;
                return;
            }

            try
            {
                var info = new FileInfo(_settingsPath);
                if (info.Length is <= 0 or > MaximumSettingsBytes)
                {
                    _loadWarningCode = SettingsInvalidWarning;
                    return;
                }

                using FileStream stream = File.OpenRead(_settingsPath);
                if (stream.Length is <= 0 or > MaximumSettingsBytes)
                {
                    _loadWarningCode = SettingsInvalidWarning;
                    return;
                }
                PersistedSelection? persisted = JsonSerializer.Deserialize<PersistedSelection>(stream, JsonOptions);
                if (persisted is null || persisted.Version != 1)
                {
                    _loadWarningCode = SettingsInvalidWarning;
                    return;
                }

                if (enumerationWarning is not null && persisted.Provider == "DirectMl")
                {
                    _loadWarningCode = enumerationWarning;
                    return;
                }

                var requested = new CalibrationOcrDeviceSelection(persisted.Provider, persisted.AdapterIndex,
                    persisted.AdapterLuid, persisted.AdapterName);
                CalibrationOcrDeviceSelection? resolved = Resolve(requested, _availableDevices, out string? failureCode);
                if (resolved is null)
                {
                    _loadWarningCode = failureCode ?? SettingsInvalidWarning;
                    return;
                }

                _selectedDevice = resolved;
                _loadWarningCode = enumerationWarning;
            }
            catch (Exception exception) when (IsSettingsException(exception))
            {
                _loadWarningCode = SettingsInvalidWarning;
            }
        }
    }

    private ReadOnlyCollection<CalibrationOcrDeviceSelection> EnumerateAvailable(out string? warningCode)
    {
        try
        {
            var devices = new List<CalibrationOcrDeviceSelection> { CalibrationOcrDeviceSelection.Cpu };
            devices.AddRange(_inventory.Enumerate()
                .Where(adapter => !adapter.IsSoftware)
                .Select(adapter => new CalibrationOcrDeviceSelection("DirectMl", adapter.Index, adapter.Luid, adapter.Name)));
            warningCode = null;
            return Array.AsReadOnly(devices.ToArray());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            warningCode = AdapterEnumerationFailedWarning;
            return Array.AsReadOnly(new[] { CalibrationOcrDeviceSelection.Cpu });
        }
    }

    private static CalibrationOcrDeviceSelection? Resolve(CalibrationOcrDeviceSelection requested,
        IReadOnlyList<CalibrationOcrDeviceSelection> available, out string? failureCode)
    {
        if (requested.Provider == "Cpu")
        {
            if (requested.AdapterIndex is null && requested.AdapterLuid is null && requested.AdapterName is null)
            {
                failureCode = null;
                return CalibrationOcrDeviceSelection.Cpu;
            }
            failureCode = SettingsInvalidWarning;
            return null;
        }

        if (requested.Provider == "DirectMl" && requested.AdapterIndex is int index && index >= 0 &&
            requested.AdapterLuid is long luid)
        {
            CalibrationOcrDeviceSelection? current = available.FirstOrDefault(device => device.Provider == "DirectMl" &&
                device.AdapterIndex == index && device.AdapterLuid == luid);
            if (current is not null)
            {
                failureCode = null;
                return current;
            }
            failureCode = AdapterUnavailableWarning;
            return null;
        }

        failureCode = SettingsInvalidWarning;
        return null;
    }

    private bool TryPersist(CalibrationOcrDeviceSelection selection)
    {
        if (_settingsPath is null) return true;

        string? settingsRoot = Path.GetDirectoryName(_settingsPath);
        if (settingsRoot is null) return false;
        string temporaryPath = Path.Combine(settingsRoot, "." + SettingsFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(settingsRoot);
            var persisted = new PersistedSelection(1, selection.Provider, selection.AdapterIndex,
                selection.AdapterLuid, selection.AdapterName);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(persisted, JsonOptions);
            if (bytes.Length > MaximumSettingsBytes) return false;
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (IsSettingsException(exception))
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception exception) when (IsSettingsException(exception)) { }
        }
    }

    private static bool IsSettingsException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or
            NotSupportedException or JsonException;

    private sealed record PersistedSelection(int Version, string Provider, int? AdapterIndex,
        long? AdapterLuid, string? AdapterName);
}
