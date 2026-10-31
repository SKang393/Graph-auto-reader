// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Xml.Linq;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ShellContractTests
{
    private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string AutomationNamespace = "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    private static readonly Dictionary<string, (string AutomationId, string AutomationName, string Command)> WorkflowControls =
        new Dictionary<string, (string, string, string)>(StringComparer.Ordinal)
        {
            ["WorkflowImportButton"] = ("Workflow.Import", "Workflow.Open", "{Binding ImportCommand}"),
            ["WorkflowAutoDetectButton"] = ("Workflow.AutoDetect", "Workflow.Analyze", "{Binding AutoDetectCommand}"),
            ["WorkflowReviewButton"] = ("Workflow.Review", "Workflow.Review", "{Binding ReviewCommand}"),
            ["WorkflowExportButton"] = ("Workflow.Export", "Workflow.Export", "{Binding OpenExportPreviewCommand}"),
        };

    [TestMethod]
    public void CoreWorkflowIsVisibleLocalizedAndAccessibleWithoutMenus()
    {
        var document = LoadMainWindow();
        XNamespace presentation = PresentationNamespace;
        XNamespace xaml = XamlNamespace;
        XNamespace automation = AutomationNamespace;

        Assert.IsFalse(document.Descendants(presentation + "Menu").Any());
        var tabIndices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expected in WorkflowControls)
        {
            var button = FindNamed(document, expected.Key);
            Assert.AreEqual(presentation + "Button", button.Name);
            Assert.AreEqual(expected.Value.AutomationId, button.Attribute(automation + "AutomationProperties.AutomationId")?.Value);
            Assert.AreEqual($"{{DynamicResource {expected.Value.AutomationName}}}", button.Attribute(automation + "AutomationProperties.Name")?.Value);
            Assert.AreEqual(expected.Value.Command, button.Attribute("Command")?.Value);
            Assert.IsTrue(tabIndices.Add(button.Attribute("TabIndex")?.Value ?? string.Empty));
            Assert.IsTrue(button.Descendants(presentation + "AccessText").Any());
        }

        Assert.HasCount(WorkflowControls.Count, tabIndices);
        Assert.IsNotNull(document.Root?.Attribute(xaml + "Class"));
    }

    [TestMethod]
    public void KeyboardBindingsCoverWorkflowOverlayAndCancellation()
    {
        var document = LoadMainWindow();
        XNamespace presentation = PresentationNamespace;
        var bindings = document.Descendants(presentation + "KeyBinding")
            .Select(binding => new
            {
                Key = binding.Attribute("Key")?.Value ?? string.Empty,
                Modifiers = binding.Attribute("Modifiers")?.Value ?? string.Empty,
                Command = binding.Attribute("Command")?.Value ?? string.Empty,
            })
            .ToArray();

        var expected = new[]
        {
            ("I", "Control", "{Binding ImportCommand}"),
            ("E", "Control", "{Binding EnhanceCommand}"),
            ("F5", string.Empty, "{Binding AutoDetectCommand}"),
            ("R", "Control", "{Binding ReviewCommand}"),
            ("E", "Control+Shift", "{Binding OpenExportPreviewCommand}"),
            ("P", "Control", "{Binding TogglePhaseOverlayCommand}"),
            ("Tab", "Control", "{Binding NextTabCommand}"),
            ("Tab", "Control+Shift", "{Binding PreviousTabCommand}"),
            ("Escape", string.Empty, "{Binding CancelCommand}"),
        };

        Assert.HasCount(expected.Length, bindings);
        foreach (var item in expected)
        {
            Assert.IsTrue(
                bindings.Any(binding =>
                    binding.Key == item.Item1 &&
                    binding.Modifiers == item.Item2 &&
                    binding.Command == item.Item3),
                $"Missing keyboard binding {item.Item2}+{item.Item1} -> {item.Item3}.");
        }
    }

    [TestMethod]
    public void GraphCanvasExposesKeyboardNavigationAndActivationToAutomation()
    {
        var document = LoadMainWindow();
        XNamespace automation = AutomationNamespace;
        var canvas = FindNamed(document, "GraphCanvasHost");

        Assert.AreEqual("True", canvas.Attribute("Focusable")?.Value);
        Assert.AreEqual("21", canvas.Attribute("TabIndex")?.Value);
        Assert.AreEqual("OnGraphImagePointInvoked", canvas.Attribute("ImagePointInvoked")?.Value);
        Assert.AreEqual("OnGraphImagePointNavigated", canvas.Attribute("ImagePointNavigated")?.Value);
        Assert.AreEqual(
            "{DynamicResource GraphCanvas.KeyboardHelp}",
            canvas.Attribute(automation + "AutomationProperties.HelpText")?.Value);
    }

    [TestMethod]
    public void ManualCalibrationExposesFirstVisibleSessionAsLocalizedInput()
    {
        var document = LoadMainWindow();
        XNamespace automation = AutomationNamespace;
        XElement input = document.Descendants(XName.Get("TextBox", PresentationNamespace))
            .Single(element => element.Attribute(automation + "AutomationProperties.AutomationId")?.Value
                == "Manual.FirstVisibleSession");

        Assert.AreEqual(
            "{Binding ManualFirstVisibleSessionText, UpdateSourceTrigger=PropertyChanged}",
            input.Attribute("Text")?.Value);
        Assert.AreEqual(
            "{DynamicResource Manual.FirstVisibleSession}",
            input.Attribute(automation + "AutomationProperties.Name")?.Value);
        Assert.AreEqual(
            "{DynamicResource Manual.SessionRangeHelp}",
            input.Attribute(automation + "AutomationProperties.HelpText")?.Value);

        XElement validation = document.Descendants(XName.Get("TextBlock", PresentationNamespace))
            .Single(element => element.Attribute(automation + "AutomationProperties.AutomationId")?.Value
                == "Manual.FirstVisibleSession.Validation");
        Assert.AreEqual(
            "{DynamicResource Manual.FirstVisibleSession.Invalid}",
            validation.Attribute("Text")?.Value);
        Assert.IsTrue(validation.Descendants(XName.Get("DataTrigger", PresentationNamespace)).Any(trigger =>
            trigger.Attribute("Value")?.Value == "False" &&
            trigger.Elements(XName.Get("Setter", PresentationNamespace)).Any(setter =>
                setter.Attribute("Property")?.Value == "Visibility" &&
                setter.Attribute("Value")?.Value == "Visible")));
    }

    [TestMethod]
    public void EveryShellAutomationIdHasAnExplicitLocalizedOrBoundName()
    {
        var document = LoadMainWindow();
        XNamespace automation = AutomationNamespace;
        XElement[] identified = document.Descendants()
            .Where(element => !string.IsNullOrWhiteSpace(
                element.Attribute(automation + "AutomationProperties.AutomationId")?.Value))
            .ToArray();

        Assert.IsNotEmpty(identified);
        string[] automationIds = identified
            .Select(element => element.Attribute(automation + "AutomationProperties.AutomationId")!.Value)
            .ToArray();
        Assert.AreEqual(
            automationIds.Length,
            automationIds.Distinct(StringComparer.Ordinal).Count(),
            "Shell automation IDs must be unique.");
        string[] unnamed = identified
            .Where(element => string.IsNullOrWhiteSpace(
                element.Attribute(automation + "AutomationProperties.Name")?.Value))
            .Select(element => element.Attribute(automation + "AutomationProperties.AutomationId")!.Value)
            .ToArray();
        Assert.IsEmpty(unnamed, $"Automation IDs without names: {string.Join(", ", unnamed)}");

        XElement status = identified.Single(element =>
            element.Attribute(automation + "AutomationProperties.AutomationId")?.Value == "Workflow.Status");
        Assert.AreEqual(
            "Polite",
            status.Attribute(automation + "AutomationProperties.LiveSetting")?.Value);
        Assert.AreEqual(
            "{Binding StatusMessage}",
            status.Attribute(automation + "AutomationProperties.Name")?.Value);
    }

    [TestMethod]
    public void SeriesUsesLeftPaneWhileMagnifierRemainsInRightInspector()
    {
        var document = LoadMainWindow();
        XNamespace automation = AutomationNamespace;

        var seriesPane = FindNamed(document, "SeriesPane");
        var inspector = FindNamed(document, "InspectorPane");
        var magnifier = FindNamed(document, "MagnifierInspector");
        var series = FindNamed(document, "SeriesList");
        var canvas = FindNamed(document, "GraphCanvasHost");

        Assert.IsTrue(magnifier.AncestorsAndSelf().Contains(inspector));
        Assert.IsTrue(series.AncestorsAndSelf().Contains(seriesPane));
        Assert.IsFalse(series.AncestorsAndSelf().Contains(inspector));
        Assert.IsFalse(canvas.AncestorsAndSelf().Contains(inspector));
        Assert.AreEqual("Inspector.Magnifier", magnifier.Attribute(automation + "AutomationProperties.AutomationId")?.Value);
        Assert.AreEqual("Series.List", series.Attribute(automation + "AutomationProperties.AutomationId")?.Value);
        Assert.AreEqual("Canvas.GraphHost", canvas.Attribute(automation + "AutomationProperties.AutomationId")?.Value);
        Assert.AreEqual(
            "{Binding Magnifier.NearestDetectionName}",
            magnifier.Attribute("NearestDetectionName")?.Value,
            "The nearest-detection label must come from fake detection data so the empty placeholder remains reachable.");
    }

    [TestMethod]
    public void GraphTabsAndProjectSummaryAreDirectlyAboveCanvas()
    {
        var document = LoadMainWindow();
        XNamespace presentation = PresentationNamespace;

        var workspace = FindNamed(document, "WorkspacePane");
        var summary = FindNamed(document, "ProjectSummary");
        var tabs = FindNamed(document, "GraphTabStrip");
        var canvas = FindNamed(document, "GraphCanvasHost");
        var dirtyMarker = FindNamed(document, "DirtyMarker");

        Assert.AreEqual(presentation + "TabControl", tabs.Name);
        Assert.IsTrue(summary.AncestorsAndSelf().Contains(workspace));
        Assert.IsTrue(tabs.AncestorsAndSelf().Contains(workspace));
        Assert.IsTrue(canvas.AncestorsAndSelf().Contains(workspace));
        Assert.AreEqual("1", tabs.Attribute("Grid.Row")?.Value);
        StringAssert.Contains(dirtyMarker.Attribute("Visibility")?.Value ?? string.Empty, "IsDirty");

        var closeButton = tabs.Descendants(presentation + "Button").Single();
        Assert.AreEqual(
            "{Binding DataContext.CloseTabCommand, RelativeSource={RelativeSource AncestorType=Window}}",
            closeButton.Attribute("Command")?.Value);
        Assert.AreEqual("{Binding}", closeButton.Attribute("CommandParameter")?.Value);
        Assert.IsNotNull(closeButton.Attribute("ToolTip"));
    }

    [TestMethod]
    public void CalmPrecisionPaneDefaultsAreResizableAndManualCommandsAreVisible()
    {
        var document = LoadMainWindow();
        XNamespace presentation = PresentationNamespace;
        XNamespace automation = AutomationNamespace;
        XNamespace xaml = XamlNamespace;

        var inspectorColumn = document.Descendants(presentation + "ColumnDefinition")
            .Single(element => element.Attribute(xaml + "Name")?.Value == "InspectorColumn");
        Assert.AreEqual(384, double.Parse(inspectorColumn.Attribute("Width")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsTrue(double.Parse(inspectorColumn.Attribute("MinWidth")!.Value, System.Globalization.CultureInfo.InvariantCulture) >= 340);

        var seriesColumn = document.Descendants(presentation + "ColumnDefinition")
            .Single(element => element.Attribute(xaml + "Name")?.Value == "SeriesColumn");
        Assert.AreEqual(272, double.Parse(seriesColumn.Attribute("Width")!.Value, System.Globalization.CultureInfo.InvariantCulture));

        var resizeGrip = document.Descendants(presentation + "GridSplitter")
            .Single(element => element.Attribute(automation + "AutomationProperties.AutomationId")?.Value == "Inspector.Resize");
        Assert.AreEqual("Columns", resizeGrip.Attribute("ResizeDirection")?.Value);

        string[] expectedAutomationIds =
        [
            "Manual.Calibrate",
            "Manual.CreateSeries",
            "Manual.EditSeries",
            "Manual.PointFillMode",
            "Manual.AddFilledPoint",
            "Manual.AddOpenPoint",
            "Manual.MovePoint",
            "Manual.DeletePoint",
            "Manual.AddPhaseDivider",
            "Manual.MovePhaseDivider",
            "Manual.DeletePhaseDivider",
            "Manual.EditPhaseLabel",
            "Canvas.Fit",
            "Canvas.ZoomIn",
            "Canvas.ZoomOut",
            "Canvas.ResetView",
            "Enhancement.ShowOriginal",
            "Enhancement.ShowEnhanced",
            "Enhancement.ShowComparison",
        ];

        var visibleIds = document.Descendants()
            .Select(element => element.Attribute(automation + "AutomationProperties.AutomationId")?.Value)
            .Where(static value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string id in expectedAutomationIds)
        {
            Assert.IsTrue(visibleIds.Contains(id), $"Missing visible manual control '{id}'.");
        }

        var canvas = FindNamed(document, "GraphCanvasHost");
        Assert.AreEqual("{Binding SelectedTab.DisplayImageSource}", canvas.Attribute("ImageSource")?.Value);
        Assert.AreEqual("{Binding SelectedTab.ImageSource}", canvas.Attribute("CoordinateReferenceSource")?.Value);
        Assert.AreEqual("{Binding SelectedTab.ComparisonImageSource}", canvas.Attribute("ComparisonImageSource")?.Value);
        Assert.AreEqual("{Binding SelectedTab.IsComparisonPreview}", canvas.Attribute("IsComparisonVisible")?.Value);
        Assert.AreEqual("{Binding ZoomInCommand}", FindNamed(document, "ZoomInButton").Attribute("Command")?.Value);
        Assert.AreEqual("{Binding ZoomOutCommand}", FindNamed(document, "ZoomOutButton").Attribute("Command")?.Value);
        Assert.AreEqual("{Binding FitZoomCommand}", FindNamed(document, "FitGraphButton").Attribute("Command")?.Value);
        Assert.AreEqual("{Binding ResetViewCommand}", FindNamed(document, "ResetViewButton").Attribute("Command")?.Value);
    }

    [TestMethod]
    public void ThemeSelectorIsVisibleLocalizedAccessibleAndBound()
    {
        var document = LoadMainWindow();
        XNamespace presentation = PresentationNamespace;
        XNamespace automation = AutomationNamespace;
        var selector = FindNamed(document, "ThemeSelector");

        Assert.AreEqual(presentation + "ComboBox", selector.Name);
        Assert.AreEqual("4", selector.Attribute("TabIndex")?.Value);
        Assert.AreEqual("{Binding AppearanceMode, Mode=TwoWay}", selector.Attribute("SelectedValue")?.Value);
        Assert.AreEqual("Theme.Select", selector.Attribute(automation + "AutomationProperties.AutomationId")?.Value);
        Assert.AreEqual(
            "{DynamicResource Theme.Select.AutomationName}",
            selector.Attribute(automation + "AutomationProperties.Name")?.Value);

        var items = selector.Elements(presentation + "ComboBoxItem").ToArray();
        Assert.HasCount(3, items);
        Assert.IsTrue(items.All(item =>
            (item.Attribute("Content")?.Value ?? string.Empty)
                .StartsWith("{DynamicResource Theme.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void VisibleShellLiteralsUseLocalizationResources()
    {
        var document = LoadMainWindow();
        XNamespace automation = AutomationNamespace;
        var visibleAttributes = new HashSet<XName>
        {
            "Title",
            "Text",
            "Content",
            "Header",
            "ToolTip",
            automation + "AutomationProperties.Name",
        };

        var literalAttributes = (document.Root?.DescendantsAndSelf() ?? Enumerable.Empty<XElement>())
            .Attributes()
            .Where(attribute => visibleAttributes.Contains(attribute.Name))
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Value))
            .Where(attribute => !attribute.Value.StartsWith("{DynamicResource ", StringComparison.Ordinal))
            .Where(attribute => !attribute.Value.StartsWith("{Binding", StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(
            literalAttributes,
            string.Join(", ", literalAttributes.Select(attribute => $"{attribute.Parent?.Name.LocalName}.{attribute.Name.LocalName}='{attribute.Value}'")));
    }

    private static XDocument LoadMainWindow()
    {
        return XDocument.Load(Path.Combine(RepositoryTestPaths.Root, "src", "GraphReader.App", "MainWindow.xaml"), LoadOptions.SetLineInfo);
    }

    private static XElement FindNamed(XDocument document, string name)
    {
        XNamespace xaml = XamlNamespace;
        return document.Descendants()
            .Single(element => string.Equals(element.Attribute(xaml + "Name")?.Value, name, StringComparison.Ordinal));
    }
}
