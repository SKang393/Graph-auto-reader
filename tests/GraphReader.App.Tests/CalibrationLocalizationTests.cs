// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GraphReader.App.Controls;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;

namespace GraphReader.App.Tests;

public sealed partial class CalibrationTickViewModelTests
{
    private static readonly string[] CalibrationResourceSources =
        ["Themes/DesignTokens", "Themes/LightTheme", "Themes/Controls",
            "Localization/Resources.en-US", "Resources/Identity/IdentityAssets"];
    private static readonly string[] CalibrationLocalizedProperties =
        [nameof(MainWindowViewModel.ManualFirstXLabel), nameof(MainWindowViewModel.ManualFinalXLabel),
            nameof(MainWindowViewModel.ManualXRangeHelp), nameof(MainWindowViewModel.ManualFirstXInvalidText),
            nameof(MainWindowViewModel.SuggestedFirstXLabel), nameof(MainWindowViewModel.SuggestedFinalXLabel),
            nameof(MainWindowViewModel.UseCalibrationLabelForFirstXText),
            nameof(MainWindowViewModel.UseCalibrationLabelForFinalXText), nameof(MainWindowViewModel.AxisSuggestionSummary)];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CalibrationAccessibleTextRefreshesOnLanguageChangeWithoutResettingReview(bool printedX)
    {
        WithImage(path =>
        {
            Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
            var localization = new CultureSwitchingLocalizationService();
            using var workspace = CreateWorkspace(path, ZeroOriginProposals(), MeasuredGeometry());
            using var vm = new MainWindowViewModel(workspace, localization);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            if (printedX)
            {
                vm.UsePrintedXCoordinatesCommand.Execute(null);
            }
            else
            {
                vm.SuggestedFirstSession = "1";
                vm.FitForEnteredFirstSessionCommand.Execute(null);
            }
            vm.AxisSuggestionReviewed = true;
            vm.ManualUsesPrintedXCoordinates = printedX;
            vm.ManualFirstVisibleSessionText = "invalid";
            string suggestedFirst = vm.SuggestedFirstSession;
            string suggestedFinal = vm.SuggestedFinalSession;
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));

            var window = CreateCalibrationWindow(vm);
            try
            {
                TextBox first = FindCalibrationElement<TextBox>(window, "Manual.FirstVisibleSession");
                TextBox final = FindCalibrationElement<TextBox>(window, "Manual.XMaximum");
                TextBlock validation = FindCalibrationElement<TextBlock>(window, "Manual.FirstVisibleSession.Validation");
                FlushCalibrationBindings();
                AssertAccessibleText();
                var notifications = new HashSet<string>(StringComparer.Ordinal);
                vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName ?? string.Empty);
                localization.ApplyCulture(CultureInfo.GetCultureInfo("fr-FR"));
                FlushCalibrationBindings();
                AssertAccessibleText();
                foreach (string property in CalibrationLocalizedProperties)
                {
                    Assert.Contains(property, notifications);
                }
                Assert.AreEqual(printedX, vm.ManualUsesPrintedXCoordinates);
                Assert.AreEqual(printedX, vm.SuggestedUsesPrintedXCoordinates);
                Assert.AreEqual("invalid", vm.ManualFirstVisibleSessionText);
                Assert.AreEqual(suggestedFirst, vm.SuggestedFirstSession);
                Assert.AreEqual(suggestedFinal, vm.SuggestedFinalSession);
                Assert.IsTrue(vm.AxisSuggestionReviewed);
                Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));

                vm.Dispose();
                notifications.Clear();
                localization.ApplyCulture(CultureInfo.GetCultureInfo("en-US"));
                Assert.IsEmpty(notifications, "Disposed view models must detach from culture changes.");

                void AssertAccessibleText()
                {
                    string firstLabel = localization.GetString(printedX
                        ? LocalizationKeys.ManualFirstPrintedX : LocalizationKeys.ManualFirstVisibleSession);
                    string finalLabel = localization.GetString(printedX
                        ? LocalizationKeys.ManualFinalPrintedX : LocalizationKeys.ManualXMaximum);
                    string help = localization.GetString(printedX
                        ? LocalizationKeys.ManualPrintedXRangeHelp : LocalizationKeys.ManualSessionRangeHelp);
                    string invalid = localization.GetString(printedX
                        ? LocalizationKeys.ManualPrintedXInvalid : LocalizationKeys.ManualFirstVisibleSessionInvalid);
                    Assert.AreEqual(firstLabel, AutomationProperties.GetName(first));
                    Assert.AreEqual(finalLabel, AutomationProperties.GetName(final));
                    Assert.AreEqual(help, AutomationProperties.GetHelpText(first));
                    Assert.AreEqual(help, AutomationProperties.GetHelpText(final));
                    Assert.AreEqual(invalid, validation.Text);
                    Assert.AreEqual(invalid, AutomationProperties.GetName(validation));
                    Assert.AreEqual(Visibility.Visible, validation.Visibility);
                    Assert.AreEqual(firstLabel, vm.SuggestedFirstXLabel);
                    Assert.AreEqual(finalLabel, vm.SuggestedFinalXLabel);
                    Assert.AreEqual(localization.GetString(printedX ? LocalizationKeys.ManualUseFirstPrintedX
                        : LocalizationKeys.ManualCalibrationLabelsUseFirstSession), vm.UseCalibrationLabelForFirstXText);
                    Assert.AreEqual(localization.GetString(printedX ? LocalizationKeys.ManualUseFinalPrintedX
                        : LocalizationKeys.ManualCalibrationLabelsUseFinalSession), vm.UseCalibrationLabelForFinalXText);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow("Manual.YMaximum", "")]
    [DataRow("Manual.XMaximum", "3x")]
    public void InvalidVisibleLimitCannotCommitThePreviousScale(string fieldId, string invalid)
    {
        WithImage(path =>
        {
            Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
            using var workspace = new ManualPreviewWorkspaceService();
            var tab = Pump(workspace.ImportImagesAsync([path], CancellationToken.None)).Single();
            using var vm = new MainWindowViewModel(workspace, new LocalizationService(new ResourceDictionary()))
            {
                ManualUsesPrintedXCoordinates = true, ManualFirstVisibleSessionText = "-8",
                ManualXMaximum = 12, ManualYMaximum = 30
            };
            var window = CreateCalibrationWindow(vm);
            try
            {
                window.Show();
                TextBox field = FindCalibrationElement<TextBox>(window, fieldId);
                var start = (Button)window.FindName("CalibrationButton");
                start.Command.Execute(null);
                Pump(vm.HandleCanvasPointAsync(new(16, 90)));
                Pump(vm.HandleCanvasPointAsync(new(16, 18)));
                field.SetCurrentValue(TextBox.TextProperty, invalid);
                FlushCalibrationBindings();

                Assert.AreEqual(invalid, field.Text);
                Assert.IsFalse(start.Command.CanExecute(null));
                string noticeId = fieldId == "Manual.YMaximum" ? "Manual.YMaximum.Validation" : "Manual.FirstVisibleSession.Validation";
                var notice = FindCalibrationElement<TextBlock>(window, noticeId);
                Assert.AreEqual(Visibility.Visible, notice.Visibility);
                Assert.AreEqual(notice.Text, vm.StatusMessage);
                Pump(vm.HandleCanvasPointAsync(new(144, 90)));
                Assert.IsNull(tab.Calibration);
                Assert.IsNull(workspace.CurrentProject.Panels.Single().Calibration);

                // A corrected limit requires three fresh anchors. Negative printed X remains valid.
                field.SetCurrentValue(TextBox.TextProperty, fieldId == "Manual.YMaximum" ? "7.5" : "-2.5");
                FlushCalibrationBindings();
                Assert.IsTrue(start.Command.CanExecute(null));
                Assert.AreEqual(Visibility.Collapsed, notice.Visibility);
                Pump(vm.HandleCanvasPointAsync(new(16, 90)));
                Pump(vm.HandleCanvasPointAsync(new(16, 18)));
                Assert.IsNull(tab.Calibration);
                Pump(vm.HandleCanvasPointAsync(new(144, 90)));
                Assert.IsNotNull(tab.Calibration);
                Assert.AreEqual(-8d, tab.Calibration.FirstVisibleSession);
                Assert.AreEqual(fieldId == "Manual.YMaximum" ? 7.5d : 30d, tab.Calibration.YMaximum);
                Assert.AreEqual(fieldId == "Manual.XMaximum" ? -2.5d : 12d, tab.Calibration.XMaximum);
            }
            finally
            {
                window.DataContext = null;
                window.Close();
            }
        });
    }

    [TestMethod]
    public void NativeMarkerPreviewToggleShowsOnlyUncalibratedPixelLocations()
    {
        WithImage(path =>
        {
            Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
            using var workspace = new ManualPreviewWorkspaceService();
            var tab = Pump(workspace.ImportImagesAsync([path], CancellationToken.None)).Single();
            using var vm = new MainWindowViewModel(workspace);
            var window = CreateCalibrationWindow(vm);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Top = SystemParameters.VirtualScreenTop - window.Height - 100;
            window.Left = SystemParameters.VirtualScreenLeft - window.Width - 100;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.AreNotEqual(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                CheckBox toggle = FindCalibrationElement<CheckBox>(window, "Review.UncalibratedMarkers");
                Assert.IsFalse(toggle.IsVisible);
                var evidence = new WorkflowVisionEnvelope(1, Guid.NewGuid(), workspace.CurrentProject.ProjectId.Value,
                    Guid.Parse(tab.PanelId!), "markers", "1", tab.SourceSha256!, null, new(0, 0, 0, 0), 1);
                var failure = new ProductionWorkflowStageException(new(ProductionWorkflowFailureCodes.RecalibrationRequired,
                    "Errors.CalibrationInvalid", "review calibration", true, "calibrate"), [evidence])
                {
                    MarkerPreviews = [new(evidence, [new(20, 40), new(80, 60)])],
                };
                Assert.IsTrue(vm.SelectCalibrationFailurePanel(failure));
                FlushCalibrationBindings();
                Assert.IsTrue(toggle.IsVisible && toggle.IsEnabled);
                Assert.IsFalse(toggle.IsChecked);
                var overlay = (ManualReviewOverlay)tab.OverlayContent!;
                Assert.IsEmpty(Boxes());
                var peer = (IToggleProvider)new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)!;
                peer.Toggle();
                CollectionAssert.AreEquivalent(new[] { new Rect(14, 34, 12, 12), new Rect(74, 54, 12, 12) }, Boxes());
                peer.Toggle();
                Assert.IsEmpty(Boxes());
                Assert.IsEmpty(tab.Points);
                Assert.IsNull(tab.Calibration);
                Assert.IsFalse(window.IsActive);

                Rect[] Boxes()
                {
                    FlushCalibrationBindings();
                    window.UpdateLayout();
                    return VisualTreeHelper.GetDrawing(overlay)?.Children.OfType<GeometryDrawing>()
                        .Select(drawing => drawing.Geometry).OfType<RectangleGeometry>()
                        .Select(geometry => geometry.Rect).ToArray() ?? [];
                }
            }
            finally { window.Close(); }
        });
    }

    private static MainWindow CreateCalibrationWindow(MainWindowViewModel vm)
    {
        var resources = new ResourceDictionary();
        foreach (string source in CalibrationResourceSources)
            resources.MergedDictionaries.Add(new() { Source = new Uri($"/GraphReader.App;component/{source}.xaml", UriKind.Relative) });
        var window = new MainWindow(themeService: null, resources);
        ((MainWindowViewModel)window.DataContext).Dispose();
        window.DataContext = vm;
        return window;
    }

    private static T FindCalibrationElement<T>(DependencyObject root, string id) where T : DependencyObject
    {
        return Enumerate(root).OfType<T>().Single(element => AutomationProperties.GetAutomationId(element) == id);

        static IEnumerable<DependencyObject> Enumerate(DependencyObject element)
        {
            yield return element;
            foreach (DependencyObject child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            {
                foreach (DependencyObject descendant in Enumerate(child)) yield return descendant;
            }
        }
    }

    private static void FlushCalibrationBindings()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ContextIdle);
        Dispatcher.PushFrame(frame);
    }

    // Exercise the culture event with distinguishable strings without claiming a shipped French translation.
    private sealed class CultureSwitchingLocalizationService : ILocalizationService
    {
        private readonly ResourceDictionary _english = new()
        {
            Source = new Uri(LocalizationService.EnglishSource, UriKind.Relative),
        };
        public CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
        public IReadOnlyList<CultureInfo> AvailableCultures { get; } =
            [CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("fr-FR")];
        public event EventHandler<LocalizationChangedEventArgs>? CultureChanged;
        public string GetString(string key) => $"{CurrentCulture.Name}: {_english[key]}";
        public void ApplyCulture(CultureInfo culture)
        {
            CurrentCulture = culture;
            CultureChanged?.Invoke(this, new LocalizationChangedEventArgs(culture));
        }
    }
}
