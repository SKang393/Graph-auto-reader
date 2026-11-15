// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Inference;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationDeviceViewModelTests
{
    private static readonly string[] DevicePickerThemes = ["DesignTokens", "DarkTheme", "Controls"];
    [TestMethod]
    public void DeviceChoiceUsesSharedSettingsAndSurvivesFreshViewModel() => WithSettings(root =>
    {
        var inventory = new TestInventory();
        var settings = new CalibrationOcrDeviceSettings(root, inventory);
        using var vm = new MainWindowViewModel(new FakeWorkspaceService(), Localizer(), calibrationOcrDevices: settings);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Automatic, vm.SelectedCalibrationOcrDevice!.Device);
        Assert.AreEqual("Automatic (CPU until checked)", vm.SelectedCalibrationOcrDevice.Label);
        Assert.AreEqual("Automatic (CPU until checked)", vm.CalibrationDeviceStatusText, "First-run CPU is not an error.");
        Assert.HasCount(4, vm.CalibrationOcrDeviceChoices);
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
        Assert.AreEqual(CalibrationOcrDeviceSelection.Automatic, vm.SelectedCalibrationOcrDevice!.Device);
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
        Assert.AreEqual(CalibrationOcrDeviceSelection.Automatic, vm.SelectedCalibrationOcrDevice!.Device);
        Assert.AreEqual(localization.GetString(LocalizationKeys.SettingsAxisLabelsSaveFailed), vm.CalibrationDeviceStatusText);
    });

    private static LocalizationService Localizer()
    {
        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        return new(new ResourceDictionary());
    }

    [TestMethod]
    public void BackgroundProfileChangesRefreshTheViewModelOnItsStaContext() => WithSettings(root =>
    {
        var settings = new CalibrationOcrDeviceSettings(root, new TestInventory());
        CalibrationOcrDeviceSelection gpu = settings.AvailableDevices.Single(device => device.AdapterIndex == 1);
        using var optimizer = new CalibrationOcrDeviceOptimizer(settings,
            (_, _) => Task.FromResult(CalibrationOcrDeviceOptimizerTests.Success(gpu)),
            _ => Task.FromResult(new string('a', 64)), token => Task.Delay(Timeout.Infinite, token));
        using var vm = new MainWindowViewModel(new FakeWorkspaceService(), Localizer(),
            calibrationOcrDevices: settings, calibrationDeviceOptimizer: optimizer);
        var combo = new ComboBox { DataContext = vm, DisplayMemberPath = nameof(CalibrationOcrDeviceChoice.Label) };
        foreach (string theme in DevicePickerThemes)
            combo.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GraphReader.App;component/Themes/{theme}.xaml", UriKind.Relative) });
        combo.SetResourceReference(FrameworkElement.StyleProperty, "App.Style.ComboBox");
        combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.CalibrationOcrDeviceChoices)));
        combo.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(vm.SelectedCalibrationOcrDevice)) { Mode = BindingMode.TwoWay });
        combo.Measure(new Size(400, 100));
        combo.Arrange(new Rect(0, 0, 400, 40));
        combo.UpdateLayout();
        Assert.AreEqual(vm.SelectedCalibrationOcrDevice, combo.SelectedItem);
        int uiThread = Environment.CurrentManagedThreadId;
        int notificationThread = 0;
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CalibrationOcrDeviceChoice originalChoice = vm.SelectedCalibrationOcrDevice!;
        originalChoice.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CalibrationOcrDeviceChoice.Label) &&
                settings.ProfileIsCurrent && originalChoice.Label.Contains("Discrete test GPU", StringComparison.Ordinal))
            {
                notificationThread = Environment.CurrentManagedThreadId;
                refreshed.TrySetResult();
            }
        };
        Pump(Task.WhenAll(optimizer.CheckAsync(CancellationToken.None), refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5))));
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
        combo.UpdateLayout();
        Assert.AreEqual(uiThread, notificationThread);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Automatic, vm.SelectedCalibrationOcrDevice!.Device);
        Assert.AreSame(originalChoice, vm.SelectedCalibrationOcrDevice);
        StringAssert.Contains(vm.SelectedCalibrationOcrDevice.Label, "Discrete test GPU");
        Assert.AreSame(vm.SelectedCalibrationOcrDevice, combo.SelectedItem);
        Assert.IsTrue(Descendants(combo).OfType<TextBlock>().Any(text => text.Text.Contains("Discrete test GPU", StringComparison.Ordinal)),
            "The themed collapsed picker must render the selected GPU label.");
        StringAssert.Contains(((CalibrationOcrDeviceChoice)combo.SelectedItem).Label, "Discrete test GPU");
        Assert.AreEqual(gpu, settings.SelectedDevice);
        Assert.IsTrue(vm.CheckCalibrationDevicesCommand.CanExecute(null));
    });

    private static void WithSettings(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "calibration-device-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { StaTestHost.Run(() => action(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
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
