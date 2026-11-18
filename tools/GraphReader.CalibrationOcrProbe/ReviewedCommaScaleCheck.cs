// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using GraphReader.App;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;

namespace GraphReader.CalibrationOcrProbe;

internal static class ReviewedCommaScaleCheck
{
    internal static Task<object> RunAsync(string mode, string image, string app, string assets, string output, CancellationToken token)
    {
        if (mode is not ("Fraction" or "Ambiguous") || !Path.IsPathFullyQualified(image) ||
            !Path.IsPathFullyQualified(app) || !Path.IsPathFullyQualified(assets) ||
            !Path.IsPathFullyQualified(output) || !Directory.Exists(output) || Directory.EnumerateFileSystemEntries(output).Any())
            throw new ArgumentException("Use a generated comma-scale control, absolute paths and an empty output directory.");
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { completion.SetResult(await CheckAsync(mode, image, app, assets, output, token)); }
                catch (Exception error) { completion.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private static async Task<object> CheckAsync(string mode, string image, string app, string assets, string output, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        string originalHash = Program.Hash(Program.ReadBounded(image, 65536));
        string rust = Path.Combine(Path.GetDirectoryName(app)!, "native", "graphreader-engine.exe");
        using var workspace = new ManualPreviewWorkspaceService(
            axisProposalService: new RustFirstAxisProposalService(new RustAxisProposalService(rust), new OpenCvAxisProposalService(app)),
            axisTickMeasurementService: new RustAxisTickMeasurementService(rust),
            calibrationOcrSuggestionService: new CalibrationOcrWorkerClient(app, assets, () => CalibrationOcrDeviceSelection.Cpu));
        var tab = (await workspace.ImportImagesAsync([image], token)).Single();
        var crop = workspace.CurrentProject.Panels.Single().Crop;
        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        var resources = new ResourceDictionary();
        foreach (string resource in new[] { "Themes/DesignTokens", "Themes/LightTheme", "Themes/Controls", "Resources/Identity/IdentityAssets" })
            resources.MergedDictionaries.Add(new() { Source = new Uri($"/GraphReader.App;component/{resource}.xaml", UriKind.Relative) });
        var localization = new LocalizationService(resources);
        using var vm = new MainWindowViewModel(workspace, localization);
        var window = new MainWindow(null, resources)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, WindowState = WindowState.Normal,
            Top = SystemParameters.VirtualScreenTop - 3000, Left = SystemParameters.VirtualScreenLeft - 3000,
            Width = 1200, Height = 800, ShowInTaskbar = false, ShowActivated = false,
        };
        ((MainWindowViewModel)window.DataContext).Dispose();
        window.DataContext = vm;
        try
        {
            window.Show();
            window.UpdateLayout();
            Require(new WindowInteropHelper(window).Handle != IntPtr.Zero && !window.IsActive, "OFFSCREEN_WINDOW");
            var suggest = Find<Button>(window, "Manual.CalibrationTicks.Suggest");
            var review = Find<CheckBox>(window, "Manual.AxisProposal.Reviewed");
            var apply = Find<Button>(window, "Manual.AxisProposal.Apply");
            Invoke(suggest);
            await SettleAsync(vm, token);
            bool ambiguous = mode == "Ambiguous";
            Require(vm.HasCalibrationTickProposal && vm.CalibrationPairedTickRows.Count == 6 && !vm.HasEstimatedCalibrationPositions,
                $"MEASURED_FIT: {vm.StatusMessage}, {vm.CalibrationTickProposalStatusText}, {vm.CalibrationPairedTickRows.Count} pairs");
            var readings = vm.CalibrationLabelRows.Select(row => row.Proposal)
                .OrderBy(row => row.SuggestedAxis, StringComparer.Ordinal)
                .ThenBy(row => row.SuggestedAxis == "X" ? row.Polygon.Average(p => p.X) : row.Polygon.Average(p => p.Y))
                .ToArray();
            string[] expectedText = ["2", "6", "10", ambiguous ? "2,500" : "0,500", ambiguous ? "1,250" : "0,250", "0"];
            Require(readings.Select(row => row.Text).SequenceEqual(expectedText), "OCR_TRANSCRIPTION");
            Require(!apply.IsEnabled && review.IsChecked == false && tab.Calibration is null, "PRE_REVIEW_GATE");
            if (ambiguous)
            {
                Require(vm.SuggestedYMaximum.Length == 0 && vm.CalibrationTickProposalStatusText ==
                    localization.GetString(LocalizationKeys.ManualCalibrationTicksAmbiguousScale), "AMBIGUITY_WARNING");
            }
            else
            {
                double[] expectedValues = [2, 6, 10, .5, .25, 0];
                Require(readings.Select(row => row.NumericValue).SequenceEqual(expectedValues.Select(value => (double?)value)), "NUMERIC_TICKS");
            }
            ((IToggleProvider)new CheckBoxAutomationPeer(review).GetPattern(PatternInterface.Toggle)!).Toggle();
            await SettleAsync(vm, token);
            Require(review.IsChecked == true && apply.IsEnabled != ambiguous, "REVIEW_GATE");
            if (ambiguous)
            {
                try
                {
                    await workspace.ExportAsync(tab.TabId, Path.Combine(output, "export"), token);
                    throw new InvalidDataException("AMBIGUOUS_EXPORT_ACCEPTED");
                }
                catch (InvalidOperationException) when (tab.Calibration is null) { }
                Require(tab.Calibration is null && !Directory.EnumerateFileSystemEntries(output).Any(), "AMBIGUOUS_STATE");
                return new { mode, measured_ticks = 6, matched_text = 6, apply_blocked = true, export_blocked = true, elapsed_ms = timer.ElapsedMilliseconds };
            }
            Invoke(apply);
            await SettleAsync(vm, token);
            Require(tab.Calibration is { YMaximum: .5, FirstVisibleSession: 2, XMaximum: 10 }, "APPLIED_SCALE");
            var series = workspace.AddSeries(tab.TabId, new("Control", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
            workspace.AddPhaseDivider(tab.TabId, 260 - crop.X, "b", "Intervention");
            foreach (var (x, y) in new[] { (190, 305), (340, 200), (490, 110) })
                workspace.AddPoint(tab.TabId, series.SeriesId, x - crop.X, y - crop.Y);
            var saved = Path.Combine(output, "reviewed.garproj");
            Require((await workspace.SaveProjectAsync(saved, token)).IsSuccess, "SAVE");
            using var reopened = new ManualPreviewWorkspaceService();
            var restored = (await reopened.OpenProjectAsync(saved, token)).Single();
            Require(restored.Calibration == tab.Calibration && restored.Points.Count == 3, "REOPEN");
            var exported = await reopened.ExportAsync(restored.TabId, Path.Combine(output, "export"), token);
            Require(exported.Succeeded && exported.Mode == GraphReader.Export.ExportMode.PrintedSession, "EXPORT");
            string[] lines = await File.ReadAllLinesAsync(exported.MinimalArtifacts.Single().WrittenPath!, token);
            Require(lines.Length == 4 && lines[0] == "x_value,y_value,phase", "CSV_SHAPE");
            (double X, double Y, string Phase)[] reference = [(3, .125, "a"), (6, .3, "b"), (9, .45, "b")];
            for (int i = 0; i < reference.Length; i++)
            {
                string[] cells = lines[i + 1].Split(',');
                var expected = reference[i];
                Require(cells.Length == 3 && double.Parse(cells[0], CultureInfo.InvariantCulture) == expected.X &&
                    Math.Abs(double.Parse(cells[1], CultureInfo.InvariantCulture) - expected.Y) < 1e-9 &&
                    cells[2] == expected.Phase, $"CSV_ROW_{i + 1}: {lines[i + 1]}");
            }
            return new { mode, measured_ticks = 6, matched_numeric_ticks = 6, matched_rows = 3,
                incorrect_rows = 0, missing_rows = 0, csv = lines, elapsed_ms = timer.ElapsedMilliseconds };
        }
        finally
        {
            window.DataContext = null;
            window.Close();
            Program.RequireHash(Program.ReadBounded(image, 65536), originalHash);
        }
    }

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Descendants(root).OfType<T>().Single(element => AutomationProperties.GetAutomationId(element) == id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants(child)) yield return item;
    }

    private static void Invoke(Button button)
    {
        Require(button.IsEnabled && button.IsVisible, AutomationProperties.GetAutomationId(button));
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
    }

    private static async Task SettleAsync(MainWindowViewModel vm, CancellationToken token)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        while (vm.IsBusy) await Task.Delay(25, token);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void Require(bool condition, string stage)
    {
        if (!condition) throw new InvalidDataException(stage);
    }
}
