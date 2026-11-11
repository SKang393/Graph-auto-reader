// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using GraphReader.App.Appearance;
using GraphReader.App.Controls;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ThemeAccessibilityTests
{
    private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string AutomationNamespace = "clr-namespace:System.Windows.Automation;assembly=PresentationCore";
    private static readonly string[] TabThemes = ["DarkTheme", "LightTheme", "SystemTheme"];

    [TestMethod]
    public void OverflowingPanelTabsReserveFullHeaderHeightAboveScrollbar()
    {
        XNamespace presentation = PresentationNamespace;
        XNamespace xaml = XamlNamespace;
        XElement tabs = new(LoadAppXaml("MainWindow.xaml").Descendants(presentation + "TabControl")
            .Single(element => element.Attribute(xaml + "Name")?.Value == "GraphTabStrip"));
        tabs.SetAttributeValue(XNamespace.Xmlns + "x", XamlNamespace);
        tabs.SetAttributeValue(XNamespace.Xmlns + "automation", AutomationNamespace);
        tabs.Attribute("ItemsSource")!.Remove();
        tabs.Attribute("SelectedItem")!.Remove();
        tabs.AddFirst(new XElement(presentation + "TabControl.Resources",
            new XElement(presentation + "ResourceDictionary",
                new XElement(presentation + "ResourceDictionary.MergedDictionaries",
                    Dictionary("DesignTokens"), Dictionary("DarkTheme"), Dictionary("Controls")),
                new XElement(presentation + "BooleanToVisibilityConverter",
                    new XAttribute(xaml + "Key", "BooleanToVisibilityConverter")))));

        StaTestHost.Run(() =>
        {
            Application.ResourceAssembly ??= typeof(ThemeService).Assembly;
            var strip = (TabControl)XamlReader.Parse(tabs.ToString());
            strip.Items.Add(new { DisplayName = "First imported source with a long panel name", AnalysisNeedsReview = true, IsDirty = true });
            strip.Items.Add(new { DisplayName = "Second imported source with a long panel name", AnalysisNeedsReview = false, IsDirty = false });
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(strip, 0);
            layout.Children.Add(strip);
            layout.Measure(new Size(320, 240));
            layout.Arrange(new Rect(0, 0, 320, 240));
            layout.UpdateLayout();

            ScrollViewer scroll = Descendants(strip).OfType<ScrollViewer>().Single();
            Assert.AreEqual(Visibility.Visible, scroll.ComputedHorizontalScrollBarVisibility,
                "The actual tab-strip template must exercise horizontal overflow.");
            var first = (TabItem)strip.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.IsGreaterThanOrEqualTo(first.DesiredSize.Height, scroll.ViewportHeight,
                "The scrollbar must have its own space below the full header.");
            Button close = Descendants(first).OfType<Button>().Single();
            var viewport = Descendants(scroll).OfType<ScrollContentPresenter>().Single();
            Rect closeBounds = close.TransformToAncestor(viewport).TransformBounds(new Rect(close.RenderSize));
            Assert.IsLessThanOrEqualTo(scroll.ViewportHeight + 0.01, closeBounds.Bottom,
                "The close button must fit vertically inside the header viewport.");
        });

        XElement Dictionary(string name) => new(presentation + "ResourceDictionary",
            new XAttribute("Source", $"/GraphReader.App;component/Themes/{name}.xaml"));

        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
            }
        }
    }

    [TestMethod]
    public void PanelTabChromeUsesThemeBrushesForSelectedAndUnselectedHeaders()
    {
        XNamespace presentation = PresentationNamespace;
        XNamespace xaml = XamlNamespace;
        XElement tabs = LoadAppXaml("MainWindow.xaml").Descendants(presentation + "TabControl")
            .Single(element => element.Attribute(xaml + "Name")?.Value == "GraphTabStrip");
        Assert.AreEqual("{StaticResource App.Style.TabItem}", tabs
            .Element(presentation + "TabControl.ItemContainerStyle")!
            .Element(presentation + "Style")!.Attribute("BasedOn")?.Value);

        StaTestHost.Run(() =>
        {
            Application.ResourceAssembly ??= typeof(ThemeService).Assembly;
            var resources = new ResourceDictionary();
            ResourceDictionary Load(string name) => new()
            {
                Source = new Uri($"/GraphReader.App;component/Themes/{name}.xaml", UriKind.Relative),
            };
            resources.MergedDictionaries.Add(Load("DesignTokens"));
            resources.MergedDictionaries.Add(Load("DarkTheme"));
            resources.MergedDictionaries.Add(Load("Controls"));
            var label = new TextBlock { Text = "Panel 2" };
            var tab = new TabItem
            {
                Header = label,
                Resources = resources,
                Style = (Style)resources["App.Style.TabItem"],
            };
            tab.ApplyTemplate();
            tab.Measure(new Size(300, 42));
            tab.Arrange(new Rect(0, 0, 300, 42));
            var chrome = (Border)tab.Template.FindName("Chrome", tab);
            Assert.IsNotNull(tab.FocusVisualStyle);
            Assert.IsTrue(tab.Focusable);
            foreach (string theme in TabThemes)
            {
                resources.MergedDictionaries[1] = Load(theme);
                tab.IsSelected = false;
                AssertBrush("App.Brush.Surface", chrome.Background);
                AssertBrush("App.Brush.Text", label.Foreground);
                tab.IsSelected = true;
                AssertBrush("App.Brush.PrimarySubtle", chrome.Background);
                AssertBrush("App.Brush.Primary", chrome.BorderBrush);
                AssertBrush("App.Brush.Text", label.Foreground);
            }

            void AssertBrush(string key, Brush actual) => Assert.AreEqual(
                ((SolidColorBrush)resources[key]).Color, ((SolidColorBrush)actual).Color, key);
        });
    }

    [TestMethod]
    public void HighContrastUsesSystemPaletteAndReducedMotionUsesZeroDuration()
    {
        StaTestHost.Run(
            () =>
            {
                Application.ResourceAssembly ??= typeof(ThemeService).Assembly;
                var resources = new ResourceDictionary();
                using var systemTheme = new FakeSystemThemeProvider(
                    ApplicationTheme.Dark,
                    isHighContrast: true,
                    animationsEnabled: false);
                using var themeService = new ThemeService(resources, systemTheme);

                AssertThemeSource(resources, "SystemTheme.xaml");
                Assert.AreEqual(TimeSpan.Zero, ReadMotionDuration(resources));

                systemTheme.SetAccessibility(isHighContrast: false, animationsEnabled: true);

                AssertThemeSource(resources, "DarkTheme.xaml");
                Assert.AreEqual(TimeSpan.FromMilliseconds(140), ReadMotionDuration(resources));
            });
    }

    [TestMethod]
    public void EmptySurfaceAndWorkspaceAreMutuallyExclusiveLayoutSiblings()
    {
        XDocument shell = LoadAppXaml("MainWindow.xaml");
        XNamespace xaml = XamlNamespace;
        XNamespace presentation = PresentationNamespace;
        XElement workspace = shell.Descendants()
            .Single(element => element.Attribute(xaml + "Name")?.Value == "WorkspaceSurface");
        XElement empty = shell.Descendants()
            .Single(element => element.Attribute(xaml + "Name")?.Value == "EmptySurface");

        Assert.AreSame(workspace.Parent, empty.Parent);
        Assert.AreEqual("{StaticResource State.Workspace}", workspace.Attribute("Style")?.Value);
        Assert.AreEqual("{StaticResource State.Empty}", empty.Attribute("Style")?.Value);

        XElement workspaceStyle = shell.Descendants(presentation + "Style")
            .Single(element => element.Attribute(xaml + "Key")?.Value == "State.Workspace");
        XElement emptyTrigger = workspaceStyle.Descendants(presentation + "DataTrigger")
            .Single(element => element.Attribute("Value")?.Value.Contains("WorkspaceSurfaceState.Empty", StringComparison.Ordinal) == true);
        Assert.IsTrue(emptyTrigger.Descendants(presentation + "Setter")
            .Any(element => element.Attribute("Property")?.Value == "Visibility" &&
                element.Attribute("Value")?.Value == "Collapsed"));
    }

    [TestMethod]
    public void ReviewIssuesAndDataRowsExposeNativeVisibleSelection()
    {
        XDocument shell = LoadAppXaml("MainWindow.xaml");
        XDocument controls = LoadAppXaml("Themes", "Controls.xaml");
        XNamespace presentation = PresentationNamespace;
        XNamespace xaml = XamlNamespace;

        XElement review = FindList(shell, "{Binding ReviewIssues}");
        XElement data = FindList(shell, "{Binding DataPreviewRows}");
        Assert.AreEqual("{Binding SelectedReviewIssue, Mode=TwoWay}", review.Attribute("SelectedItem")?.Value);
        Assert.AreEqual("{Binding SelectedDataPreviewRow, Mode=TwoWay}", data.Attribute("SelectedItem")?.Value);
        Assert.AreEqual("Stretch", review.Attribute("HorizontalContentAlignment")?.Value);
        Assert.AreEqual("Disabled", review.Attribute("ScrollViewer.HorizontalScrollBarVisibility")?.Value);
        Assert.AreEqual("Stretch", data.Attribute("HorizontalContentAlignment")?.Value);
        Assert.AreEqual("Disabled", data.Attribute("ScrollViewer.HorizontalScrollBarVisibility")?.Value);

        XElement selectionStyle = controls.Descendants(presentation + "Style")
            .Single(element => element.Attribute(xaml + "Key")?.Value == "App.Style.ListBoxItem");
        XElement selectedTrigger = selectionStyle.Descendants(presentation + "Trigger")
            .Single(element => element.Attribute("Property")?.Value == "IsSelected");
        string selectionMarkup = selectedTrigger.ToString(SaveOptions.DisableFormatting);
        StringAssert.Contains(selectionMarkup, "App.Brush.Selection");
        StringAssert.Contains(selectionMarkup, "App.Brush.SelectionText");
        StringAssert.Contains(selectionMarkup, "App.Brush.Primary");
    }

    [TestMethod]
    public void SelectionTransitionUsesApprovedDurationToken()
    {
        XDocument tokens = LoadAppXaml("Themes", "DesignTokens.xaml");
        XNamespace xaml = XamlNamespace;
        XElement duration = tokens.Descendants()
            .Single(element => element.Attribute(xaml + "Key")?.Value == "App.Motion.Duration.Short");
        TimeSpan parsed = TimeSpan.Parse(duration.Value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100), parsed);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMilliseconds(180), parsed);

        string controls = File.ReadAllText(Path.Combine(AppDirectory, "Themes", "Controls.xaml"));
        StringAssert.Contains(controls, "Tag\" Value=\"{DynamicResource App.Motion.Enabled}\"");
        StringAssert.Contains(controls, "Duration=\"0:0:0.14\"");
    }

    [TestMethod]
    public void GraphCanvasExposesSemanticPaneAutomationType()
    {
        StaTestHost.Run(
            () =>
            {
                Application.ResourceAssembly ??= typeof(GraphCanvasControl).Assembly;
                var control = new GraphCanvasControl();
                AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(control);

                Assert.IsNotNull(peer);
                Assert.AreEqual(AutomationControlType.Pane, peer.GetAutomationControlType());
                Assert.AreEqual(nameof(GraphCanvasControl), peer.GetClassName());
            });
    }

    [TestMethod]
    public void InteractiveContainersExposeReadableAutomationNames()
    {
        XDocument shell = LoadAppXaml("MainWindow.xaml");
        XNamespace presentation = PresentationNamespace;
        XNamespace xaml = XamlNamespace;
        XNamespace automation = AutomationNamespace;

        XElement tabs = shell.Descendants(presentation + "TabControl")
            .Single(element => element.Attribute(xaml + "Name")?.Value == "GraphTabStrip");
        XElement tabNameSetter = tabs.Descendants(presentation + "Setter")
            .Single(element =>
                element.Attribute("Property")?.Value == "automation:AutomationProperties.Name");
        Assert.AreEqual("{Binding DisplayName}", tabNameSetter.Attribute("Value")?.Value);

        XElement probes = shell.Descendants(presentation + "ItemsControl")
            .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding ProbeRelationChoices}");
        Assert.AreEqual("False", probes.Attribute("Focusable")?.Value);
        Assert.AreEqual("False", probes.Attribute("IsTabStop")?.Value);
        Assert.AreEqual(
            "Manual.ApplicableProbes",
            probes.Attribute(automation + "AutomationProperties.AutomationId")?.Value);
        Assert.AreEqual(
            "{DynamicResource Manual.ApplicableProbes}",
            probes.Attribute(automation + "AutomationProperties.Name")?.Value);
        XElement emptyProbeTrigger = probes.Descendants(presentation + "DataTrigger")
            .Single(element => element.Attribute("Value")?.Value == "0");
        Assert.AreEqual(
            "{Binding ProbeRelationChoices.Count}",
            emptyProbeTrigger.Attribute("Binding")?.Value);
        Assert.IsTrue(emptyProbeTrigger.Descendants(presentation + "Setter")
            .Any(element =>
                element.Attribute("Property")?.Value == "Visibility" &&
                element.Attribute("Value")?.Value == "Collapsed"));

        XElement manualTools = shell.Descendants(presentation + "ScrollViewer")
            .Single(element => element.Descendants(presentation + "TextBlock")
                .Any(text => text.Attribute("Text")?.Value == "{DynamicResource Manual.Title}"));
        Assert.AreEqual(
            "Inspector.ManualTools",
            manualTools.Attribute(automation + "AutomationProperties.AutomationId")?.Value);
        Assert.AreEqual(
            "{DynamicResource Manual.Title}",
            manualTools.Attribute(automation + "AutomationProperties.Name")?.Value);
    }

    private static XElement FindList(XDocument shell, string itemsSource) =>
        shell.Descendants(XName.Get("ListBox", PresentationNamespace))
            .Single(element => element.Attribute("ItemsSource")?.Value == itemsSource);

    private static TimeSpan ReadMotionDuration(ResourceDictionary resources)
    {
        var duration = (Duration)resources["App.Motion.Duration.Short"];
        Assert.IsTrue(duration.HasTimeSpan);
        return duration.TimeSpan;
    }

    private static void AssertThemeSource(ResourceDictionary resources, string expectedFile)
    {
        Assert.HasCount(1, resources.MergedDictionaries);
        Assert.IsTrue(
            (resources.MergedDictionaries[0].Source?.OriginalString ?? string.Empty)
                .EndsWith(expectedFile, StringComparison.OrdinalIgnoreCase));
    }

    private static string AppDirectory => Path.Combine(
        RepositoryTestPaths.Root,
        "src",
        "GraphReader.App");

    private static XDocument LoadAppXaml(params string[] segments) =>
        XDocument.Load(Path.Combine([AppDirectory, .. segments]));

    private sealed class FakeSystemThemeProvider(
        ApplicationTheme theme,
        bool isHighContrast,
        bool animationsEnabled) : ISystemThemeProvider
    {
        public ApplicationTheme EffectiveTheme { get; } = theme;

        public bool IsHighContrast { get; private set; } = isHighContrast;

        public bool AreClientAreaAnimationsEnabled { get; private set; } = animationsEnabled;

        public event EventHandler? ThemeChanged;

        public void SetAccessibility(bool isHighContrast, bool animationsEnabled)
        {
            IsHighContrast = isHighContrast;
            AreClientAreaAnimationsEnabled = animationsEnabled;
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }
}
