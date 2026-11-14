// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using GraphReader.App.Services;
using GraphReader.Inference;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationOcrDeviceSettingsTests
{
    [TestMethod]
    public void FirstRunDefaultsToAutomaticIntentResolvedToCpuAndListsDevicesInOrder() => WithDirectory(root =>
    {
        var inventory = new FakeInventory(
        [
            new(0, "Software", 0, 0, 1, true, 0, 0),
            new(2, "Integrated Radeon", 0x1002, 0x1638, 20, false, 512UL << 20, 8UL << 30),
            new(4, "RX 6600 XT", 0x1002, 0x73ff, 40, false, 8UL << 30, 8UL << 30),
        ]);

        var settings = new CalibrationOcrDeviceSettings(root, inventory);

        Assert.AreEqual(CalibrationOcrDeviceSelection.Automatic, settings.Preference);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        Assert.IsNull(settings.LoadWarningCode);
        Assert.AreEqual(4, settings.AvailableDevices.Count);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Automatic, settings.AvailableDevices[0]);
        Assert.AreEqual("Cpu", settings.AvailableDevices[1].Provider);
        Assert.AreEqual(2, settings.AvailableDevices[2].AdapterIndex);
        Assert.AreEqual(20L, settings.AvailableDevices[2].AdapterLuid);
        Assert.AreEqual("RX 6600 XT", settings.AvailableDevices[3].AdapterName);
        Assert.IsTrue(settings.NeedsAutomaticProfile);
    });

    [TestMethod]
    public void ExplicitAdapterChoicePersistsAndReloadsByIndexAndLuid() => WithDirectory(root =>
    {
        var inventory = new FakeInventory([new(3, "RX 6600 XT", 0x1002, 0x73ff, 0x1234, false)]);
        var settings = new CalibrationOcrDeviceSettings(root, inventory);

        bool selected = settings.TrySelect(new("DirectMl", 3, 0x1234, "RX 6600 XT"), out string? failureCode);

        Assert.IsTrue(selected);
        Assert.IsNull(failureCode);
        Assert.AreEqual("DirectMl", settings.Preference.Provider);
        Assert.AreEqual("DirectMl", settings.SelectedDevice.Provider);
        Assert.AreEqual(3, settings.SelectedDevice.AdapterIndex);
        Assert.AreEqual(0x1234L, settings.SelectedDevice.AdapterLuid);
        Assert.IsNull(settings.LoadWarningCode);
        Assert.IsTrue(File.Exists(Path.Combine(root, "calibration-ocr-device.json")));
        Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp", SearchOption.TopDirectoryOnly).Length);

        var reloaded = new CalibrationOcrDeviceSettings(root, inventory);
        Assert.AreEqual(settings.SelectedDevice, reloaded.SelectedDevice);
        Assert.IsNull(reloaded.LoadWarningCode);
    });

    [TestMethod]
    public void ReindexedOrReplacedAdapterFallsBackToCpuWithUnavailableWarning() => WithDirectory(root =>
    {
        File.WriteAllText(Path.Combine(root, "calibration-ocr-device.json"),
            "{\"version\":1,\"provider\":\"DirectMl\",\"adapterIndex\":3,\"adapterLuid\":4660,\"adapterName\":\"RX 6600 XT\"}");
        var inventory = new FakeInventory([new(3, "Different adapter", 0x1002, 0x73ff, 0x5678, false)]);

        var settings = new CalibrationOcrDeviceSettings(root, inventory);

        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        Assert.AreEqual(CalibrationOcrDeviceSettings.AdapterUnavailableWarning, settings.LoadWarningCode);
    });

    [TestMethod]
    public void LegacyDirectMlPreferenceRemainsExplicitOverride() => WithDirectory(root =>
    {
        File.WriteAllText(Path.Combine(root, "calibration-ocr-device.json"),
            "{\"version\":1,\"provider\":\"DirectMl\",\"adapterIndex\":3,\"adapterLuid\":4660,\"adapterName\":\"RX 6600 XT\"}");
        var inventory = new FakeInventory([new(3, "RX 6600 XT", 0x1002, 0x73ff, 0x1234, false)]);

        var settings = new CalibrationOcrDeviceSettings(root, inventory);

        Assert.AreEqual("DirectMl", settings.Preference.Provider);
        Assert.AreEqual("DirectMl", settings.SelectedDevice.Provider);
        Assert.IsFalse(settings.NeedsAutomaticProfile);
    });

    [TestMethod]
    public void SameAdapterAtNewDxgiIndexDoesNotSilentlyChangeSelection() => WithDirectory(root =>
    {
        File.WriteAllText(Path.Combine(root, "calibration-ocr-device.json"),
            "{\"version\":1,\"provider\":\"DirectMl\",\"adapterIndex\":3,\"adapterLuid\":4660,\"adapterName\":\"RX 6600 XT\"}");
        var inventory = new FakeInventory([new(2, "RX 6600 XT", 0x1002, 0x73ff, 0x1234, false)]);

        var settings = new CalibrationOcrDeviceSettings(root, inventory);

        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        Assert.AreEqual(CalibrationOcrDeviceSettings.AdapterUnavailableWarning, settings.LoadWarningCode);
    });

    [TestMethod]
    public void InvalidPersistedPreferenceFallsBackToCpuWithInvalidWarning() => WithDirectory(root =>
    {
        File.WriteAllText(Path.Combine(root, "calibration-ocr-device.json"),
            "{\"version\":1,\"provider\":\"FastestGpu\"}");

        var settings = new CalibrationOcrDeviceSettings(root, new FakeInventory([]));

        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        Assert.AreEqual(CalibrationOcrDeviceSettings.SettingsInvalidWarning, settings.LoadWarningCode);
    });

    [TestMethod]
    public void OversizedSettingsFileIsRejectedBeforeReadingItsContents() => WithDirectory(root =>
    {
        File.WriteAllBytes(Path.Combine(root, "calibration-ocr-device.json"), new byte[4097]);

        var settings = new CalibrationOcrDeviceSettings(root, new FakeInventory([]));

        Assert.AreEqual(CalibrationOcrDeviceSelection.Automatic, settings.Preference);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        Assert.AreEqual(CalibrationOcrDeviceSettings.SettingsInvalidWarning, settings.LoadWarningCode);
    });

    [TestMethod]
    [DataRow("[]")]
    [DataRow("{\"version\":\"1\"}")]
    public void NonObjectOrNonnumericVersionIsRejectedWithoutThrowing(string json) => WithDirectory(root =>
    {
        File.WriteAllText(Path.Combine(root, "calibration-ocr-device.json"), json);

        var settings = new CalibrationOcrDeviceSettings(root, new FakeInventory([]));

        Assert.AreEqual(CalibrationOcrDeviceSelection.Automatic, settings.Preference);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        Assert.AreEqual(CalibrationOcrDeviceSettings.SettingsInvalidWarning, settings.LoadWarningCode);
    });

    [TestMethod]
    public void AdapterEnumerationFailureDoesNotMisreportStaleSelection() => WithDirectory(root =>
    {
        File.WriteAllText(Path.Combine(root, "calibration-ocr-device.json"),
            "{\"version\":1,\"provider\":\"DirectMl\",\"adapterIndex\":3,\"adapterLuid\":4660,\"adapterName\":\"RX 6600 XT\"}");

        var settings = new CalibrationOcrDeviceSettings(root, new FakeInventory([], fail: true));

        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        Assert.AreEqual(CalibrationOcrDeviceSettings.AdapterEnumerationFailedWarning, settings.LoadWarningCode);
    });

    [TestMethod]
    public void ExplicitCpuSelectionPersistsEvenWhenAdapterEnumerationFails() => WithDirectory(root =>
    {
        var settings = new CalibrationOcrDeviceSettings(root, new FakeInventory([], fail: true));

        bool selected = settings.TrySelect(CalibrationOcrDeviceSelection.Cpu, out string? failureCode);

        Assert.IsTrue(selected);
        Assert.IsNull(failureCode);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        Assert.AreEqual(CalibrationOcrDeviceSettings.AdapterEnumerationFailedWarning, settings.LoadWarningCode);
        Assert.IsTrue(File.Exists(Path.Combine(root, "calibration-ocr-device.json")));
    });

    [TestMethod]
    public void TrySelectRejectsSoftwareOrUnlistedAdapterAndKeepsCpu() => WithDirectory(root =>
    {
        var settings = new CalibrationOcrDeviceSettings(root,
            new FakeInventory([new(1, "Software", 0, 0, 7, true)]));

        bool selected = settings.TrySelect(new("DirectMl", 1, 7, "Software"), out string? failureCode);

        Assert.IsFalse(selected);
        Assert.AreEqual(CalibrationOcrDeviceSettings.AdapterUnavailableWarning, failureCode);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
    });

    [TestMethod]
    public void MatchingMeasuredAutoProfileReloadsAndResolvesToSavedDevice() => WithDirectory(root =>
    {
        const string identity = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var inventory = new FakeInventory([new(3, "RX 6600 XT", 0x1002, 0x73ff, 0x1234, false)]);
        var settings = new CalibrationOcrDeviceSettings(root, inventory);
        settings.SetProfileIdentity(identity);

        bool saved = settings.TrySaveMeasuredProfile(identity, new("DirectMl", 3, 0x1234, "RX 6600 XT"), out string? failureCode);

        Assert.IsTrue(saved);
        Assert.IsNull(failureCode);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Automatic, settings.Preference);
        Assert.IsTrue(settings.ProfileIsCurrent);
        Assert.AreEqual("DirectMl", settings.SelectedDevice.Provider);

        var reloaded = new CalibrationOcrDeviceSettings(root, inventory);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Automatic, reloaded.Preference);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, reloaded.SelectedDevice);
        reloaded.SetProfileIdentity(identity);
        Assert.IsTrue(reloaded.ProfileIsCurrent);
        Assert.AreEqual("DirectMl", reloaded.SelectedDevice.Provider);
    });

    [TestMethod]
    public void StaleMeasuredProfileResolvesAutoToCpuAndNeedsRefresh() => WithDirectory(root =>
    {
        const string measuredIdentity = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string currentIdentity = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var inventory = new FakeInventory([new(3, "RX 6600 XT", 0x1002, 0x73ff, 0x1234, false)]);
        var settings = new CalibrationOcrDeviceSettings(root, inventory);
        settings.SetProfileIdentity(measuredIdentity);
        Assert.IsTrue(settings.TrySaveMeasuredProfile(measuredIdentity,
            new("DirectMl", 3, 0x1234, "RX 6600 XT"), out _));

        var reloaded = new CalibrationOcrDeviceSettings(root, inventory);
        reloaded.SetProfileIdentity(currentIdentity);

        Assert.AreEqual(CalibrationOcrDeviceSelection.Automatic, reloaded.Preference);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, reloaded.SelectedDevice);
        Assert.IsFalse(reloaded.ProfileIsCurrent);
        Assert.IsTrue(reloaded.NeedsAutomaticProfile);
    });

    [TestMethod]
    public void ExplicitOverrideWinsAndReturningToAutoRestoresCurrentProfile() => WithDirectory(root =>
    {
        const string identity = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var inventory = new FakeInventory([new(3, "RX 6600 XT", 0x1002, 0x73ff, 0x1234, false)]);
        var settings = new CalibrationOcrDeviceSettings(root, inventory);
        settings.SetProfileIdentity(identity);
        Assert.IsTrue(settings.TrySaveMeasuredProfile(identity,
            new("DirectMl", 3, 0x1234, "RX 6600 XT"), out _));

        Assert.IsTrue(settings.TrySelect(CalibrationOcrDeviceSelection.Cpu, out _));
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        Assert.IsTrue(settings.ProfileIsCurrent);
        Assert.IsTrue(settings.TrySelect(CalibrationOcrDeviceSelection.Automatic, out _));
        Assert.AreEqual(CalibrationOcrDeviceSelection.Automatic, settings.Preference);
        Assert.AreEqual("DirectMl", settings.SelectedDevice.Provider);
    });

    [TestMethod]
    public void ProfileReplacementRaisesChangedEvenWhenExplicitOverrideKeepsResolvedDevice() => WithDirectory(root =>
    {
        const string identity = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var inventory = new FakeInventory([new(3, "RX 6600 XT", 0x1002, 0x73ff, 0x1234, false)]);
        var settings = new CalibrationOcrDeviceSettings(root, inventory);
        settings.SetProfileIdentity(identity);
        Assert.IsTrue(settings.TrySelect(CalibrationOcrDeviceSelection.Cpu, out _));
        Assert.IsTrue(settings.TrySaveMeasuredProfile(identity, CalibrationOcrDeviceSelection.Cpu, out _));
        int changeCount = 0;
        settings.Changed += (_, _) => changeCount++;

        bool saved = settings.TrySaveMeasuredProfile(identity,
            new("DirectMl", 3, 0x1234, "RX 6600 XT"), out string? failureCode);

        Assert.IsTrue(saved);
        Assert.IsNull(failureCode);
        Assert.AreEqual(1, changeCount);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
    });

    [TestMethod]
    public void ProfileSaveFailsWhenSettingsPathCannotBeWritten()
    {
        string root = Path.Combine(Path.GetTempPath(), "calibration-device-settings-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(root, "not a directory");
        try
        {
            var settings = new CalibrationOcrDeviceSettings(root, new FakeInventory([]));
            const string identity = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            settings.SetProfileIdentity(identity);

            bool saved = settings.TrySaveMeasuredProfile(identity, CalibrationOcrDeviceSelection.Cpu, out string? failureCode);

            Assert.IsFalse(saved);
            Assert.AreEqual(CalibrationOcrDeviceSettings.SettingsWriteFailed, failureCode);
            Assert.IsFalse(settings.ProfileIsCurrent);
        }
        finally { File.Delete(root); }
    }

    private static void WithDirectory(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "calibration-device-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class FakeInventory(IReadOnlyList<DxgiAdapter> adapters, bool fail = false) : IDxgiAdapterInventory
    {
        public IReadOnlyList<DxgiAdapter> Enumerate() => fail ? throw new InvalidOperationException("Enumeration unavailable") : adapters;
    }
}
