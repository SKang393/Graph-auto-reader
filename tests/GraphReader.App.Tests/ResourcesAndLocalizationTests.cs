// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using GraphReader.App.Appearance;
using GraphReader.App.Localization;
using GraphReader.App.ViewModels;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ResourcesAndLocalizationTests
{
    private static readonly string[] ExpectedFontFiles =
    [
        "NotoSans-Regular.ttf",
        "NotoSans-Medium.ttf",
        "NotoSans-SemiBold.ttf",
    ];

    private static readonly string[] ThemeFiles =
    {
        "LightTheme.xaml",
        "DarkTheme.xaml",
        "SystemTheme.xaml",
    };

    [TestMethod]
    public void LightDarkAndSystemThemesExposeTheSameTokens()
    {
        var themeDirectory = Path.Combine(RepositoryTestPaths.Root, "src", "GraphReader.App", "Themes");
        var themeKeys = ThemeFiles.Select(file => ReadResourceKeys(Path.Combine(themeDirectory, file))).ToArray();

        Assert.IsGreaterThanOrEqualTo(15, themeKeys[0].Count);
        CollectionAssert.AreEquivalent(themeKeys[0].ToArray(), themeKeys[1].ToArray());
        CollectionAssert.AreEquivalent(themeKeys[0].ToArray(), themeKeys[2].ToArray());

        var designTokens = File.ReadAllText(Path.Combine(themeDirectory, "DesignTokens.xaml"));
        StringAssert.Contains(designTokens, "../Assets/Fonts/#Noto Sans");
        Assert.IsFalse(designTokens.Contains("IBM Plex", StringComparison.Ordinal));
        Assert.IsFalse(designTokens.Contains("Segoe UI", StringComparison.Ordinal));

        var fontDirectory = Path.Combine(RepositoryTestPaths.Root, "src", "GraphReader.App", "Assets", "Fonts");
        string[] fontFiles = Directory.EnumerateFiles(fontDirectory, "*.ttf", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEquivalent(
            ExpectedFontFiles,
            fontFiles);
    }

    [TestMethod]
    public void ThemeServiceFollowsSystemUntilExplicitlyOverridden()
    {
        StaTestHost.Run(
            () =>
            {
                var resources = new ResourceDictionary();
                using var systemTheme = new FakeSystemThemeProvider(ApplicationTheme.Dark);
                using var themeService = new ThemeService(resources, systemTheme);
                var changes = new List<ThemeChangedEventArgs>();
                themeService.ThemeChanged += (_, args) => changes.Add(args);

                Assert.AreEqual(ApplicationTheme.System, themeService.Theme);
                Assert.AreEqual(ApplicationTheme.Dark, themeService.EffectiveTheme);
                AssertThemeSource(resources, "DarkTheme.xaml");

                systemTheme.SetTheme(ApplicationTheme.Light);
                Assert.AreEqual(ApplicationTheme.System, themeService.Theme);
                Assert.AreEqual(ApplicationTheme.Light, themeService.EffectiveTheme);
                AssertThemeSource(resources, "LightTheme.xaml");

                themeService.ApplyTheme(ApplicationTheme.Dark);
                systemTheme.SetTheme(ApplicationTheme.Light);
                Assert.AreEqual(ApplicationTheme.Dark, themeService.Theme);
                Assert.AreEqual(ApplicationTheme.Dark, themeService.EffectiveTheme);
                AssertThemeSource(resources, "DarkTheme.xaml");

                themeService.ApplyTheme(ApplicationTheme.System);
                Assert.AreEqual(ApplicationTheme.Light, themeService.EffectiveTheme);
                AssertThemeSource(resources, "LightTheme.xaml");
                Assert.IsGreaterThanOrEqualTo(3, changes.Count);
            });
    }

    [TestMethod]
    public void AccessibleShellThemeSelectorAppliesRuntimeOverride()
    {
        StaTestHost.Run(
            () =>
            {
                using var themeService = new RecordingThemeService();
                var window = new MainWindow(themeService);
                var viewModel = (MainWindowViewModel)window.DataContext;
                var selector = (ComboBox)window.FindName("ThemeSelector");
                window.Show();

                Assert.AreEqual(
                    "Theme.Select",
                    System.Windows.Automation.AutomationProperties.GetAutomationId(selector));
                Assert.AreEqual(ApplicationTheme.System, selector.SelectedValue);

                selector.SelectedValue = ApplicationTheme.Dark;
                selector.GetBindingExpression(ComboBox.SelectedValueProperty)?.UpdateSource();

                Assert.AreEqual(ApplicationTheme.Dark, viewModel.AppearanceMode);
                Assert.AreEqual(ApplicationTheme.Dark, themeService.Theme);
                Assert.AreEqual(ApplicationTheme.Dark, themeService.EffectiveTheme);
                Assert.Contains(ApplicationTheme.Dark, themeService.AppliedThemes);
                window.Close();
            });
    }

    [TestMethod]
    public void LocalizationRegistryAndDictionaryAreComplete()
    {
        var localizationPath = Path.Combine(
            RepositoryTestPaths.Root,
            "src",
            "GraphReader.App",
            "Localization",
            "Resources.en-US.xaml");
        var dictionaryKeys = ReadResourceKeys(localizationPath);
        var registeredKeys = typeof(LocalizationKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.IsNotEmpty(dictionaryKeys);
        CollectionAssert.AreEquivalent(
            dictionaryKeys.Order(StringComparer.Ordinal).ToArray(),
            registeredKeys.Order(StringComparer.Ordinal).ToArray(),
            "Every localized string must be registered and every registered key must be translated.");
    }

    [TestMethod]
    public void ManualEditHistoryLabelsResolveAndAreNeverPassedAsUnregisteredLiterals()
    {
        string viewModelPath = Path.Combine(
            RepositoryTestPaths.Root,
            "src",
            "GraphReader.App",
            "ViewModels",
            "MainWindowViewModel.cs");
        string viewModelSource = File.ReadAllText(viewModelPath);
        Assert.IsFalse(
            Regex.IsMatch(viewModelSource, @"RecordManualEdit\(\s*[^,]+,\s*""Edit\."),
            "History descriptions must use registered localization constants.");

        string[] historyKeys =
        [
            LocalizationKeys.EditAddPoint,
            LocalizationKeys.EditDeletePoint,
            LocalizationKeys.EditMovePoint,
            LocalizationKeys.EditMergeSeries,
            LocalizationKeys.EditSplitSeries,
            LocalizationKeys.EditReassignPoint,
            LocalizationKeys.EditCalibrate,
            LocalizationKeys.EditAddPhaseDivider,
            LocalizationKeys.EditMovePhaseDivider,
            LocalizationKeys.EditCreateSeries,
            LocalizationKeys.EditEditSeries,
            LocalizationKeys.EditDeletePhaseDivider,
            LocalizationKeys.EditLabelPhaseDivider,
        ];

        string localizationPath = Path.Combine(
            RepositoryTestPaths.Root,
            "src",
            "GraphReader.App",
            "Localization",
            "Resources.en-US.xaml");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        Dictionary<string, string> translations = XDocument.Load(localizationPath)
            .Descendants()
            .Select(element => new
            {
                Key = element.Attribute(xaml + "Key")?.Value,
                Value = element.Value,
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToDictionary(entry => entry.Key!, entry => entry.Value, StringComparer.Ordinal);

        foreach (string key in historyKeys)
        {
            Assert.IsTrue(translations.TryGetValue(key, out string? value), key);
            Assert.IsFalse(string.IsNullOrWhiteSpace(value), key);
            Assert.AreNotEqual(key, value, key);
        }
    }

    [TestMethod]
    public void LocalizationServiceResolvesSupportedCultureAndRejectsUnknownKeys()
    {
        StaTestHost.Run(
            () =>
            {
                var previousCulture = CultureInfo.CurrentUICulture;
                var previousDefaultCulture = CultureInfo.DefaultThreadCurrentUICulture;
                try
                {
                    var resources = new ResourceDictionary();
                    var service = new LocalizationService(resources);

                    service.ApplyCulture(CultureInfo.GetCultureInfo("fr-FR"));

                    Assert.AreEqual("en-US", service.CurrentCulture.Name);
                    Assert.HasCount(1, service.AvailableCultures);
                    Assert.AreEqual("Graph Auto Reader", service.GetString(LocalizationKeys.AppTitle));
                    Assert.ThrowsExactly<KeyNotFoundException>(() => service.GetString("Missing.Key"));
                }
                finally
                {
                    CultureInfo.CurrentUICulture = previousCulture;
                    CultureInfo.DefaultThreadCurrentUICulture = previousDefaultCulture;
                }
            });
    }

    [TestMethod]
    public void EveryVisibleDynamicResourceHasAnEnglishTranslation()
    {
        var appDirectory = Path.Combine(RepositoryTestPaths.Root, "src", "GraphReader.App");
        var localizationKeys = ReadResourceKeys(Path.Combine(appDirectory, "Localization", "Resources.en-US.xaml"));
        string[] visibleAttributeNames =
        {
            "Title",
            "Text",
            "Content",
            "Header",
            "ToolTip",
            "AutomationProperties.Name",
        };

        var unresolved = new List<string>();
        foreach (var xamlPath in Directory.EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(xamlPath);
            foreach (var attribute in document.Descendants().Attributes())
            {
                bool isAutomationName = attribute.Name.LocalName == "Name"
                    && attribute.Name.NamespaceName.Contains("System.Windows.Automation", StringComparison.Ordinal);
                if (!isAutomationName && !visibleAttributeNames.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
                {
                    continue;
                }

                const string prefix = "{DynamicResource ";
                if (!attribute.Value.StartsWith(prefix, StringComparison.Ordinal) || !attribute.Value.EndsWith('}'))
                {
                    continue;
                }

                var key = attribute.Value[prefix.Length..^1];
                if (!key.StartsWith("{x:Static ", StringComparison.Ordinal) && !localizationKeys.Contains(key))
                {
                    unresolved.Add($"{Path.GetRelativePath(RepositoryTestPaths.Root, xamlPath)}: {key}");
                }
            }
        }

        Assert.IsEmpty(unresolved, string.Join(Environment.NewLine, unresolved));
    }

    private static HashSet<string> ReadResourceKeys(string path)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path)
            .Descendants()
            .Select(element => element.Attribute(xaml + "Key")?.Value)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertThemeSource(ResourceDictionary resources, string expectedFile)
    {
        Assert.HasCount(1, resources.MergedDictionaries);
        Assert.IsTrue(
            (resources.MergedDictionaries[0].Source?.OriginalString ?? string.Empty)
                .EndsWith(expectedFile, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeSystemThemeProvider(ApplicationTheme theme) : ISystemThemeProvider
    {
        public ApplicationTheme EffectiveTheme { get; private set; } = theme;

        public bool IsHighContrast => false;

        public bool AreClientAreaAnimationsEnabled => true;

        public event EventHandler? ThemeChanged;

        public void SetTheme(ApplicationTheme themeValue)
        {
            EffectiveTheme = themeValue;
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingThemeService : IThemeService
    {
        public ApplicationTheme Theme { get; private set; } = ApplicationTheme.System;

        public ApplicationTheme EffectiveTheme { get; private set; } = ApplicationTheme.Light;

        public List<ApplicationTheme> AppliedThemes { get; } = [];

        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

        public void ApplyTheme(ApplicationTheme theme)
        {
            Theme = theme;
            EffectiveTheme = theme == ApplicationTheme.System ? ApplicationTheme.Light : theme;
            AppliedThemes.Add(theme);
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(Theme, EffectiveTheme));
        }

        public void Dispose()
        {
        }
    }
}
