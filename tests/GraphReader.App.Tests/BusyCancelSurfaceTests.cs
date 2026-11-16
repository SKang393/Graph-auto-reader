// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;

namespace GraphReader.App.Tests;

public sealed partial class CalibrationTickViewModelTests
{
    private static readonly string[] BusyWindowResources =
        ["Themes/DesignTokens", "Themes/LightTheme", "Themes/Controls", "Localization/Resources.en-US", "Resources/Identity/IdentityAssets"];

    [TestMethod]
    public void BusyFooterCancelIsVisibleAndCancelsPendingLabelRead()
    {
        WithImage(path =>
        {
            var reader = new CooperativeLabelReader();
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new AxisStub(MeasuredGeometry()),
                calibrationOcrSuggestionService: reader);
            Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestAxesAsync(CancellationToken.None));
            Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
            var resources = new ResourceDictionary();
            foreach (string source in BusyWindowResources)
                resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GraphReader.App;component/{source}.xaml", UriKind.Relative) });
            var window = new MainWindow(themeService: null, resources);
            ((MainWindowViewModel)window.DataContext).Dispose();
            window.DataContext = vm;
            using var cleanup = new CancellationTokenSource();
            Task? pending = null;
            try
            {
                var button = (Button)window.FindName("BusyCancelButton");
                FlushBusyBindings();
                Assert.AreEqual(Visibility.Collapsed, button.Visibility);
                Assert.AreEqual("Workflow.BusyCancel", AutomationProperties.GetAutomationId(button));
                Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                Assert.AreSame(vm.CancelCommand, button.Command);
                pending = vm.ReadCalibrationLabelsAsync(cleanup.Token);
                Assert.AreEqual(1, reader.Calls);
                FlushBusyBindings();
                Assert.AreEqual(Visibility.Visible, button.Visibility);
                Assert.IsTrue(button.Command.CanExecute(button.CommandParameter));
                button.Command.Execute(button.CommandParameter);
                Assert.IsTrue(reader.ObservedToken.IsCancellationRequested);
                Pump(pending);
                FlushBusyBindings();
                Assert.IsFalse(vm.IsBusy);
                Assert.AreEqual(Visibility.Collapsed, button.Visibility);
                Assert.IsFalse(vm.HasCalibrationLabelSuggestion);
                Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            }
            finally
            {
                cleanup.Cancel();
                if (pending is { IsCompleted: false }) Pump(pending);
                window.Close();
            }
        });
    }

    private static void FlushBusyBindings()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ContextIdle);
        Dispatcher.PushFrame(frame);
    }
}
