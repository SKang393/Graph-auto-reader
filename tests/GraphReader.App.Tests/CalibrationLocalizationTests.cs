// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using GraphReader.App.Localization;
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

            var resources = new ResourceDictionary();
            foreach (string source in CalibrationResourceSources)
            {
                resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"/GraphReader.App;component/{source}.xaml", UriKind.Relative),
                });
            }
            var window = new MainWindow(themeService: null, resources);
            ((MainWindowViewModel)window.DataContext).Dispose();
            window.DataContext = vm;
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
