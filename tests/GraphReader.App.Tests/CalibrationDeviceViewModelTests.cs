// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Windows;
using System.Windows.Threading;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Inference;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationDeviceViewModelTests
{
    [TestMethod]
    public void DeviceChoiceUsesSharedSettingsAndSurvivesFreshViewModel() => WithSettings(root =>
    {
        var inventory = new TestInventory();
        var settings = new CalibrationOcrDeviceSettings(root, inventory);
        using var vm = new MainWindowViewModel(new FakeWorkspaceService(), Localizer(), calibrationOcrDevices: settings);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, vm.SelectedCalibrationOcrDevice!.Device);
        Assert.AreEqual("CPU (default)", vm.SelectedCalibrationOcrDevice.Label);
        Assert.AreEqual(string.Empty, vm.CalibrationDeviceStatusText, "First-run CPU is not an error.");
        Assert.HasCount(3, vm.CalibrationOcrDeviceChoices);
        CalibrationOcrDeviceChoice gpu = vm.CalibrationOcrDeviceChoices.Single(choice => choice.Device.AdapterIndex == 1);
        StringAssert.Contains(gpu.Label, "Discrete test GPU");
        vm.SelectedCalibrationOcrDevice = gpu;
        Assert.AreEqual(gpu.Device, settings.SelectedDevice, "The worker callback reads this same settings instance.");
        using var reopened = new MainWindowViewModel(new FakeWorkspaceService(), Localizer(),
            calibrationOcrDevices: new CalibrationOcrDeviceSettings(root, inventory));
        Assert.AreEqual(gpu.Device, reopened.SelectedCalibrationOcrDevice!.Device);
        reopened.SelectedCalibrationOcrDevice = reopened.CalibrationOcrDeviceChoices.Single(choice => choice.Device.Provider == "Cpu");
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, new CalibrationOcrDeviceSettings(root, inventory).SelectedDevice);
    });

    [TestMethod]
    public void BusyOperationCannotChangeOrPersistDeviceChoice() => WithSettings(root =>
    {
        var settings = new CalibrationOcrDeviceSettings(root, new TestInventory());
        var workspace = new PendingWorkspace();
        using var vm = new MainWindowViewModel(workspace, Localizer(), calibrationOcrDevices: settings);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IsBusy) && !vm.IsBusy) idle.TrySetResult();
        };
        Assert.IsTrue(vm.AutoDetectCommand.CanExecute(null));
        vm.AutoDetectCommand.Execute(null);
        Assert.IsTrue(vm.IsBusy);
        Assert.IsFalse(vm.CanChangeCalibrationOcrDevice);
        vm.SelectedCalibrationOcrDevice = vm.CalibrationOcrDeviceChoices.Single(choice => choice.Device.AdapterIndex == 1);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, vm.SelectedCalibrationOcrDevice!.Device);
        workspace.Completed.SetResult();
        Pump(idle.Task);
        Assert.IsTrue(vm.CanChangeCalibrationOcrDevice);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, new CalibrationOcrDeviceSettings(root, new TestInventory()).SelectedDevice);
    });

    [TestMethod]
    public void SaveFailurePreservesChoiceAndShowsLocalizedExplanation() => WithSettings(root =>
    {
        string blocked = Path.Combine(root, "blocked-settings");
        File.WriteAllText(blocked, "owned test file");
        var settings = new CalibrationOcrDeviceSettings(blocked, new TestInventory());
        var localization = Localizer();
        using var vm = new MainWindowViewModel(new FakeWorkspaceService(), localization, calibrationOcrDevices: settings);
        vm.SelectedCalibrationOcrDevice = vm.CalibrationOcrDeviceChoices.Single(choice => choice.Device.AdapterIndex == 1);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, vm.SelectedCalibrationOcrDevice!.Device);
        Assert.AreEqual(localization.GetString(LocalizationKeys.SettingsAxisLabelsSaveFailed), vm.CalibrationDeviceStatusText);
    });

    private static LocalizationService Localizer()
    {
        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        return new(new ResourceDictionary());
    }

    private static void WithSettings(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "calibration-device-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { StaTestHost.Run(() => action(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestInventory : IDxgiAdapterInventory
    {
        public IReadOnlyList<DxgiAdapter> Enumerate() =>
        [
            new(0, "Integrated test GPU", 1, 1, 100, false),
            new(1, "Discrete test GPU", 1, 2, 200, false),
            new(2, "Software test adapter", 0, 0, 300, true),
        ];
    }

    private sealed class PendingWorkspace : IWorkspaceService
    {
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<WorkspaceTabViewModel> CreateWorkspace() => new FakeWorkspaceService().CreateWorkspace();
        public Task RunStageAsync(WorkflowStage stage, CancellationToken cancellationToken) => Completed.Task;
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
