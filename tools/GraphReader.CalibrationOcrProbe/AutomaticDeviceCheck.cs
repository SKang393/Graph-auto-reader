// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using GraphReader.App.Services;

namespace GraphReader.CalibrationOcrProbe;

internal static class AutomaticDeviceCheck
{
    internal static async Task<object> RunAsync(string appExe, string assets, string settingsRoot,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(appExe) || !File.Exists(appExe) ||
            !Path.IsPathFullyQualified(assets) || !Directory.Exists(assets) ||
            !Path.IsPathFullyQualified(settingsRoot) || !Directory.Exists(settingsRoot) ||
            Directory.EnumerateFileSystemEntries(settingsRoot).Any())
            throw new InvalidDataException("Existing absolute App and asset paths and an empty settings root are required.");

        string appRoot = Path.GetDirectoryName(appExe) ?? throw new InvalidDataException("App directory missing.");
        string identity = await CalibrationOcrDeviceIdentity.ReadAsync(appRoot, assets, cancellationToken);
        var settings = new CalibrationOcrDeviceSettings(settingsRoot);
        if (settings.Preference != CalibrationOcrDeviceSelection.Automatic || settings.SelectedDevice != CalibrationOcrDeviceSelection.Cpu)
            throw new InvalidDataException("Fresh settings do not start in automatic CPU mode.");
        settings.SetProfileIdentity(identity);
        var benchmark = new CalibrationOcrDeviceBenchmark(device => new CalibrationOcrWorkerClient(appExe, assets, () => device));
        CalibrationOcrDeviceBenchmarkResult result = await benchmark.RunAsync(settings.AvailableDevices, cancellationToken);
        CalibrationOcrDeviceMeasurement? cpu = result.Measurements.FirstOrDefault(m => m.Device == CalibrationOcrDeviceSelection.Cpu);
        if (cpu is null || cpu.FailureCode is not null || !cpu.OutputMatchesCpu || cpu.FourReadTotalMilliseconds is null)
            return new { review_only = true, profile_saved = false, failure = cpu?.FailureCode ?? "CPU_BASELINE_MISSING", result.Measurements };

        if (!settings.TrySaveMeasuredProfile(identity, result.SelectedDevice, out string? failure))
            throw new InvalidDataException(failure ?? "Profile save failed.");
        var reload = new CalibrationOcrDeviceSettings(settingsRoot);
        reload.SetProfileIdentity(identity);
        bool sameIdentity = reload.Preference == CalibrationOcrDeviceSelection.Automatic &&
            reload.ProfileIsCurrent && reload.SelectedDevice == result.SelectedDevice;
        string changedIdentity = (identity[0] == '0' ? "1" : "0") + identity[1..];
        reload.SetProfileIdentity(changedIdentity);
        bool changedFallsBack = !reload.ProfileIsCurrent && reload.SelectedDevice == CalibrationOcrDeviceSelection.Cpu;
        reload.SetProfileIdentity(identity);
        if (!reload.TrySelect(CalibrationOcrDeviceSelection.Cpu, out failure) ||
            !reload.TrySaveMeasuredProfile(identity, result.SelectedDevice, out failure))
            throw new InvalidDataException(failure ?? "Override save failed.");
        var overridden = new CalibrationOcrDeviceSettings(settingsRoot);
        overridden.SetProfileIdentity(identity);
        bool overrideSurvives = overridden.Preference == CalibrationOcrDeviceSelection.Cpu &&
            overridden.SelectedDevice == CalibrationOcrDeviceSelection.Cpu;
        if (!sameIdentity || !changedFallsBack || !overrideSurvives)
            throw new InvalidDataException("Device profile persistence check failed.");
        return new { review_only = true, identity, result.SelectedDevice, result.Measurements,
            profile_saved = true, same_identity_restored = sameIdentity,
            changed_identity_cpu = changedFallsBack, explicit_override_preserved = overrideSurvives };
    }
}
