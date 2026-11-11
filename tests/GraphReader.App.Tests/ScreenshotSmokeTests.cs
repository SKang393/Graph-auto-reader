// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Windows;
using GraphReader.App.Controls;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ScreenshotSmokeTests
{
    private const string LightThemeSource =
        "/GraphReader.App;component/Themes/LightTheme.xaml";
    private const string DarkThemeSource =
        "/GraphReader.App;component/Themes/DarkTheme.xaml";

    [TestMethod]
    public void FakeGraphWorkspaceRendersDeterministicLightTheme()
    {
        AssertDeterministicRender(LightThemeSource, "calm-precision-workspace-light");
    }

    [TestMethod]
    public void FakeGraphWorkspaceRendersDeterministicDarkTheme()
    {
        AssertDeterministicRender(DarkThemeSource, "calm-precision-workspace-dark");
    }

    [TestMethod]
    [DataRow(100, 1280, 800)]
    [DataRow(125, 1600, 1000)]
    [DataRow(150, 1920, 1200)]
    [DataRow(200, 2560, 1600)]
    public void WorkspaceLayoutRendersAtSupportedDpiScale(
        int scalePercent,
        int pixelWidth,
        int pixelHeight)
    {
        double scale = scalePercent / 100d;
        RenderedScreenshot rendered = WpfScreenshotHarness.Render(
            () =>
            {
                FrameworkElement workspace = CreateFakeWorkspace(LightThemeSource, true);
                workspace.Width = pixelWidth / scale;
                workspace.Height = pixelHeight / scale;
                return workspace;
            },
            pixelWidth,
            pixelHeight,
            scale);

        Assert.AreEqual(pixelWidth, rendered.Width);
        Assert.AreEqual(pixelHeight, rendered.Height);
        Assert.IsGreaterThan(2_000, rendered.PngBytes.Length);

        string evidenceDirectory = Path.Combine(
            RepositoryTestPaths.Root,
            ".codex",
            "full-build",
            "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        File.WriteAllBytes(
            Path.Combine(evidenceDirectory, $"calm-precision-dpi-{scalePercent}.png"),
            rendered.PngBytes);
    }

    [TestMethod]
    public void PhaseOverlayToggleChangesRenderedPixels()
    {
        var visible = WpfScreenshotHarness.Render(
            () => CreatePhaseCanvas(true),
            520,
            280);
        var hidden = WpfScreenshotHarness.Render(
            () => CreatePhaseCanvas(false),
            520,
            280);

        string evidenceDirectory = Path.Combine(
            RepositoryTestPaths.Root,
            "artifacts",
            "dev-portable",
            "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        File.WriteAllBytes(Path.Combine(evidenceDirectory, "phase-overlay-visible.png"), visible.PngBytes);
        File.WriteAllBytes(Path.Combine(evidenceDirectory, "phase-overlay-hidden.png"), hidden.PngBytes);

        Assert.AreNotEqual(visible.Sha256, hidden.Sha256);
        CollectionAssert.AreNotEqual(visible.PngBytes, hidden.PngBytes);
    }

    [TestMethod]
    public void ManualPreviewRendersEmptyRealDataWorkspaceWithDevelopmentBanner()
    {
        var workspace = new ManualPreviewWorkspaceService();
        Assert.HasCount(0, workspace.CreateWorkspace());
        Assert.IsFalse(workspace.UsesFakeGraphData);
        var localization = new LocalizationService(new ResourceDictionary());
        var viewModel = new MainWindowViewModel(workspace, localization);
        Assert.IsNull(viewModel.Magnifier.GraphPosition);
        Assert.IsNull(viewModel.Magnifier.NearestDetectionName);
        Assert.IsNull(viewModel.Magnifier.NearestDetectionConfidence);
        Assert.IsFalse(viewModel.Magnifier.IsCrosshairVisible);
        Assert.IsFalse(viewModel.EnhanceCommand.CanExecute(null));
        Assert.IsFalse(viewModel.AutoDetectCommand.CanExecute(null));
        Assert.AreEqual(
            localization.GetString(LocalizationKeys.WorkflowEnhanceUnavailable),
            viewModel.EnhancementAvailabilityText);
        Assert.AreEqual(
            localization.GetString(LocalizationKeys.WorkflowAutoDetectUnavailable),
            viewModel.AutoDetectionAvailabilityText);

        RenderedScreenshot rendered = WpfScreenshotHarness.Render(
            () => CreateManualPreviewWorkspace(viewModel),
            1400,
            900);

        Assert.AreEqual(1400, rendered.Width);
        Assert.AreEqual(900, rendered.Height);
        Assert.IsGreaterThan(2_000, rendered.PngBytes.Length);
        string evidenceDirectory = Path.Combine(
            RepositoryTestPaths.Root,
            "artifacts",
            "dev-portable",
            "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        File.WriteAllBytes(
            Path.Combine(evidenceDirectory, "manual-preview-empty.png"),
            rendered.PngBytes);
    }

    private static GraphCanvasControl CreatePhaseCanvas(bool showPhaseOverlay)
    {
        var tab = new FakeWorkspaceService().CreateWorkspace()[0];
        var canvas = new GraphCanvasControl
        {
            ImageSource = tab.ImageSource,
            PhaseOverlayContent = tab.PhaseOverlayContent,
            PhaseOverlayVisible = showPhaseOverlay,
            ShowCrosshair = false,
        };
        AddResources(canvas, LightThemeSource);
        return canvas;
    }

    private static void AssertDeterministicRender(string themeSource, string evidenceFilePrefix)
    {
        foreach ((int width, int height) in new[] { (1280, 800), (1440, 900) })
        {
            var first = WpfScreenshotHarness.Render(
                () => CreateFakeWorkspace(themeSource, true),
                width,
                height);
            var second = WpfScreenshotHarness.Render(
                () => CreateFakeWorkspace(themeSource, true),
                width,
                height);

            Assert.AreEqual(width, first.Width);
            Assert.AreEqual(height, first.Height);
            Assert.IsGreaterThan(2_000, first.PngBytes.Length);
            Assert.AreEqual(first.Sha256, second.Sha256);
            CollectionAssert.AreEqual(first.PngBytes, second.PngBytes);

            var evidenceDirectory = Path.Combine(
                RepositoryTestPaths.Root,
                ".codex",
                "full-build",
                "evidence");
            Directory.CreateDirectory(evidenceDirectory);
            File.WriteAllBytes(
                Path.Combine(evidenceDirectory, $"{evidenceFilePrefix}-{width}x{height}.png"),
                first.PngBytes);
        }
    }

    private static FrameworkElement CreateFakeWorkspace(string themeSource, bool showPhaseOverlay)
    {
        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        var localizationResources = new ResourceDictionary
        {
            Source = new Uri(
                "/GraphReader.App;component/Localization/Resources.en-US.xaml",
                UriKind.RelativeOrAbsolute),
        };
        var viewModel = new MainWindowViewModel(
            new FakeWorkspaceService(),
            new LocalizationService(localizationResources))
        {
            IsPhaseOverlayVisible = showPhaseOverlay,
        };
        var window = new MainWindow(themeService: null, CreateResources(themeSource))
        {
            DataContext = viewModel,
        };
        FrameworkElement content = (FrameworkElement)window.Content;
        window.Content = null;
        content.DataContext = viewModel;
        AddResources(content, themeSource);
        return content;
    }

    private static FrameworkElement CreateManualPreviewWorkspace(MainWindowViewModel viewModel)
    {
        var window = new MainWindow(themeService: null, CreateResources(LightThemeSource))
        {
            DataContext = viewModel,
        };
        FrameworkElement content = (FrameworkElement)window.Content;
        window.Content = null;
        content.DataContext = viewModel;
        AddResources(content, LightThemeSource);
        return content;
    }

    private static void AddResources(FrameworkElement element, string themeSource)
    {
        element.Resources.MergedDictionaries.Add(CreateResources(themeSource));
    }

    private static ResourceDictionary CreateResources(string themeSource)
    {
        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        var resources = new ResourceDictionary();
        string[] resourceSources =
        [
            "/GraphReader.App;component/Themes/DesignTokens.xaml",
            themeSource,
            "/GraphReader.App;component/Themes/Controls.xaml",
            "/GraphReader.App;component/Localization/Resources.en-US.xaml",
            "/GraphReader.App;component/Resources/Identity/IdentityAssets.xaml",
        ];
        foreach (var source in resourceSources)
        {
            resources.MergedDictionaries.Add(
                new ResourceDictionary
                {
                    Source = new Uri(source, UriKind.RelativeOrAbsolute),
                });
        }
        return resources;
    }
}
