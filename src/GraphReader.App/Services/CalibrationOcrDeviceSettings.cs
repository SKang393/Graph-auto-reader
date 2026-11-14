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
    public static CalibrationOcrDeviceSelection Automatic { get; } = new("Auto");
    public static CalibrationOcrDeviceSelection Cpu { get; } = new("Cpu");
}

public sealed class CalibrationOcrDeviceSettings
{
    public const string SettingsInvalidWarning = "CALIBRATION_OCR_DEVICE_SETTINGS_INVALID";
    public const string AdapterUnavailableWarning = "CALIBRATION_OCR_ADAPTER_UNAVAILABLE";
    public const string AdapterEnumerationFailedWarning = "CALIBRATION_OCR_ADAPTER_ENUMERATION_FAILED";
    public const string SettingsWriteFailed = "CALIBRATION_OCR_DEVICE_SETTINGS_WRITE_FAILED";
    public const string ProfileIdentityInvalid = "CALIBRATION_OCR_DEVICE_PROFILE_IDENTITY_INVALID";
    public const string ProfileIdentityMismatch = "CALIBRATION_OCR_DEVICE_PROFILE_IDENTITY_MISMATCH";

    private const string SettingsFileName = "calibration-ocr-device.json";
    private const int MaximumSettingsBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly IDxgiAdapterInventory _inventory;
    private readonly string? _settingsPath;
    private IReadOnlyList<CalibrationOcrDeviceSelection> _availableDevices =
        Array.AsReadOnly(new[] { CalibrationOcrDeviceSelection.Automatic, CalibrationOcrDeviceSelection.Cpu });
    private CalibrationOcrDeviceSelection _preference = CalibrationOcrDeviceSelection.Automatic;
    private CalibrationOcrDeviceSelection _selectedDevice = CalibrationOcrDeviceSelection.Cpu;
    private string? _profileIdentity;
    private CalibrationOcrDeviceSelection? _profileDevice;
    private string? _currentProfileIdentity;
    private bool _profileIsCurrent;
    private string? _loadWarningCode;

    public event EventHandler? Changed;

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

    public CalibrationOcrDeviceSelection Preference
    {
        get { lock (_sync) return _preference; }
    }

    public bool ProfileIsCurrent
    {
        get { lock (_sync) return _profileIsCurrent; }
    }

    public bool NeedsAutomaticProfile
    {
        get { lock (_sync) return _preference.Provider == "Auto" && !_profileIsCurrent; }
    }

    public string? LoadWarningCode
    {
        get { lock (_sync) return _loadWarningCode; }
    }

    public bool TrySelect(CalibrationOcrDeviceSelection device, out string? failureCode)
    {
        ArgumentNullException.ThrowIfNull(device);
        bool notify;
        lock (_sync)
        {
            DeviceState before = CaptureState();
            IReadOnlyList<CalibrationOcrDeviceSelection> available = EnumerateAvailable(out string? enumerationWarning);
            _availableDevices = available;
            CalibrationOcrDeviceSelection? resolved = Resolve(device, available, out failureCode);
            if (resolved is null)
            {
                failureCode = enumerationWarning ?? failureCode;
                _loadWarningCode = failureCode;
                notify = before != CaptureState();
                if (!notify) return false;
            }
            else if (!TryPersist(CreatePersistedSettings(resolved, _profileIdentity, _profileDevice)))
            {
                _loadWarningCode = SettingsWriteFailed;
                failureCode = SettingsWriteFailed;
                notify = before != CaptureState();
                if (!notify) return false;
            }
            else
            {
                _preference = resolved;
                _loadWarningCode = enumerationWarning;
                RecomputeResolvedState();
                failureCode = null;
                notify = before != CaptureState();
            }
        }

        if (notify) Changed?.Invoke(this, EventArgs.Empty);
        return failureCode is null;
    }

    public void SetProfileIdentity(string sha256)
    {
        if (!IsSha(sha256)) throw new ArgumentException("The profile identity must be a SHA-256 value.", nameof(sha256));
        bool notify;
        lock (_sync)
        {
            DeviceState before = CaptureState();
            bool identityChanged = !string.Equals(_currentProfileIdentity, sha256, StringComparison.OrdinalIgnoreCase);
            _currentProfileIdentity = sha256;
            RecomputeResolvedState();
            notify = identityChanged || before != CaptureState();
        }
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TrySaveMeasuredProfile(string identity, CalibrationOcrDeviceSelection selectedDevice,
        out string? failureCode)
    {
        ArgumentNullException.ThrowIfNull(selectedDevice);
        if (!IsSha(identity))
        {
            failureCode = ProfileIdentityInvalid;
            return false;
        }

        bool notify;
        lock (_sync)
        {
            DeviceState before = CaptureState();
            IReadOnlyList<CalibrationOcrDeviceSelection> available = EnumerateAvailable(out string? enumerationWarning);
            _availableDevices = available;
            if (!string.Equals(identity, _currentProfileIdentity, StringComparison.OrdinalIgnoreCase))
            {
                _loadWarningCode = ProfileIdentityMismatch;
                failureCode = ProfileIdentityMismatch;
                notify = before != CaptureState();
                if (!notify) return false;
            }
            else
            {
                CalibrationOcrDeviceSelection? resolved = Resolve(selectedDevice, available, out failureCode);
                if (resolved is null || resolved.Provider == "Auto")
                {
                    failureCode = enumerationWarning ?? failureCode ?? SettingsInvalidWarning;
                    _loadWarningCode = failureCode;
                    notify = before != CaptureState();
                    if (!notify) return false;
                }
                else if (!TryPersist(CreatePersistedSettings(_preference, identity, resolved)))
                {
                    _loadWarningCode = SettingsWriteFailed;
                    failureCode = SettingsWriteFailed;
                    notify = before != CaptureState();
                    if (!notify) return false;
                }
                else
                {
                    _profileIdentity = identity;
                    _profileDevice = resolved;
                    _loadWarningCode = enumerationWarning;
                    RecomputeResolvedState();
                    failureCode = null;
                    notify = before != CaptureState();
                }
            }
        }

        if (notify) Changed?.Invoke(this, EventArgs.Empty);
        return failureCode is null;
    }

    private void Load()
    {
        lock (_sync)
        {
            _availableDevices = EnumerateAvailable(out string? enumerationWarning);
            if (_settingsPath is null || !File.Exists(_settingsPath))
            {
                _loadWarningCode = enumerationWarning;
                return;
            }

            try
            {
                byte[] bytes;
                using (var stream = new FileStream(_settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length is <= 0 or > MaximumSettingsBytes)
                    {
                        _loadWarningCode = SettingsInvalidWarning;
                        return;
                    }

                    bytes = new byte[checked((int)stream.Length)];
                    stream.ReadExactly(bytes);
                    if (stream.ReadByte() != -1)
                    {
                        _loadWarningCode = SettingsInvalidWarning;
                        return;
                    }
                }

                using JsonDocument document = JsonDocument.Parse(bytes);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("version", out JsonElement versionValue) ||
                    versionValue.ValueKind != JsonValueKind.Number || !versionValue.TryGetInt32(out int version))
                {
                    _loadWarningCode = SettingsInvalidWarning;
                    return;
                }

                CalibrationOcrDeviceSelection requestedPreference;
                PersistedSettings? settingsV2 = null;
                if (version == 1)
                {
                    PersistedSelection? legacy = JsonSerializer.Deserialize<PersistedSelection>(bytes, JsonOptions);
                    if (legacy is null || !IsWellFormedSelection(new(legacy.Provider, legacy.AdapterIndex,
                            legacy.AdapterLuid, legacy.AdapterName), allowAuto: false))
                    {
                        _loadWarningCode = SettingsInvalidWarning;
                        return;
                    }
                    requestedPreference = new(legacy.Provider, legacy.AdapterIndex, legacy.AdapterLuid, legacy.AdapterName);
                }
                else if (version == 2)
                {
                    settingsV2 = JsonSerializer.Deserialize<PersistedSettings>(bytes, JsonOptions);
                    if (settingsV2 is null || !IsWellFormedSelection(settingsV2.Preference, allowAuto: true) ||
                        !ValidProfile(settingsV2.ProfileIdentity, settingsV2.ProfileDevice))
                    {
                        _loadWarningCode = SettingsInvalidWarning;
                        return;
                    }
                    requestedPreference = settingsV2.Preference;
                }
                else
                {
                    _loadWarningCode = SettingsInvalidWarning;
                    return;
                }

                _preference = requestedPreference;
                _profileIdentity = settingsV2?.ProfileIdentity;
                _profileDevice = settingsV2?.ProfileDevice;
                if (enumerationWarning is not null && requestedPreference.Provider == "DirectMl")
                    _loadWarningCode = enumerationWarning;
                else if (_profileDevice?.Provider == "DirectMl" &&
                         Resolve(_profileDevice, _availableDevices, out _) is null)
                    _loadWarningCode = enumerationWarning ?? AdapterUnavailableWarning;
                else if (Resolve(requestedPreference, _availableDevices, out string? failureCode) is null)
                    _loadWarningCode = enumerationWarning ?? failureCode ?? SettingsInvalidWarning;
                else
                    _loadWarningCode = enumerationWarning;
                RecomputeResolvedState();
            }
            catch (Exception exception) when (IsSettingsException(exception))
            {
                _preference = CalibrationOcrDeviceSelection.Automatic;
                _profileIdentity = null;
                _profileDevice = null;
                RecomputeResolvedState();
                _loadWarningCode = SettingsInvalidWarning;
            }
        }
    }

    private ReadOnlyCollection<CalibrationOcrDeviceSelection> EnumerateAvailable(out string? warningCode)
    {
        try
        {
            var devices = new List<CalibrationOcrDeviceSelection>
            {
                CalibrationOcrDeviceSelection.Automatic,
                CalibrationOcrDeviceSelection.Cpu,
            };
            devices.AddRange(_inventory.Enumerate()
                .Where(adapter => !adapter.IsSoftware)
                .Select(adapter => new CalibrationOcrDeviceSelection("DirectMl", adapter.Index, adapter.Luid, adapter.Name)));
            warningCode = null;
            return Array.AsReadOnly(devices.ToArray());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            warningCode = AdapterEnumerationFailedWarning;
            return Array.AsReadOnly(new[] { CalibrationOcrDeviceSelection.Automatic, CalibrationOcrDeviceSelection.Cpu });
        }
    }

    private static CalibrationOcrDeviceSelection? Resolve(CalibrationOcrDeviceSelection requested,
        IReadOnlyList<CalibrationOcrDeviceSelection> available, out string? failureCode)
    {
        if (requested.Provider == "Auto")
        {
            if (requested.AdapterIndex is null && requested.AdapterLuid is null && requested.AdapterName is null)
            {
                failureCode = null;
                return CalibrationOcrDeviceSelection.Automatic;
            }
            failureCode = SettingsInvalidWarning;
            return null;
        }

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

    private DeviceState CaptureState() => new(_preference, _selectedDevice, _profileIdentity, _profileDevice,
        _profileIsCurrent, _loadWarningCode);

    private void RecomputeResolvedState()
    {
        CalibrationOcrDeviceSelection? resolvedProfile = _profileDevice is null
            ? null
            : Resolve(_profileDevice, _availableDevices, out _);
        _profileIsCurrent = _profileIdentity is not null && _currentProfileIdentity is not null &&
            string.Equals(_profileIdentity, _currentProfileIdentity, StringComparison.OrdinalIgnoreCase) &&
            resolvedProfile is { Provider: "Cpu" or "DirectMl" };

        _selectedDevice = _preference.Provider switch
        {
            "Cpu" => CalibrationOcrDeviceSelection.Cpu,
            "DirectMl" => Resolve(_preference, _availableDevices, out _) ?? CalibrationOcrDeviceSelection.Cpu,
            "Auto" when _profileIsCurrent => resolvedProfile!,
            _ => CalibrationOcrDeviceSelection.Cpu,
        };
    }

    private static bool IsWellFormedSelection(CalibrationOcrDeviceSelection? selection, bool allowAuto)
    {
        if (selection is null) return false;
        if (selection.Provider == "Cpu" || allowAuto && selection.Provider == "Auto")
            return selection.AdapterIndex is null && selection.AdapterLuid is null && selection.AdapterName is null;
        return selection.Provider == "DirectMl" && selection.AdapterIndex is >= 0 && selection.AdapterLuid is not null &&
            (selection.AdapterName is null || selection.AdapterName.Length <= 256);
    }

    private static bool ValidProfile(string? identity, CalibrationOcrDeviceSelection? device) =>
        identity is null && device is null || IsSha(identity) && IsWellFormedSelection(device, allowAuto: false);

    private static bool IsSha(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static PersistedSettings CreatePersistedSettings(CalibrationOcrDeviceSelection preference,
        string? profileIdentity, CalibrationOcrDeviceSelection? profileDevice) =>
        new(2, preference, profileIdentity, profileDevice);

    private bool TryPersist(PersistedSettings settings)
    {
        if (_settingsPath is null) return true;

        string? settingsRoot = Path.GetDirectoryName(_settingsPath);
        if (settingsRoot is null) return false;
        string temporaryPath = Path.Combine(settingsRoot, "." + SettingsFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(settingsRoot);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
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

    private readonly record struct DeviceState(CalibrationOcrDeviceSelection Preference,
        CalibrationOcrDeviceSelection SelectedDevice, string? ProfileIdentity,
        CalibrationOcrDeviceSelection? ProfileDevice, bool ProfileIsCurrent, string? LoadWarningCode);

    private sealed record PersistedSelection(int Version, string Provider, int? AdapterIndex,
        long? AdapterLuid, string? AdapterName);

    private sealed record PersistedSettings(int Version, CalibrationOcrDeviceSelection Preference,
        string? ProfileIdentity, CalibrationOcrDeviceSelection? ProfileDevice);
}
