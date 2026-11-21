// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Windows;
using System.Windows.Input;
using GraphReader.App;
using GraphReader.App.Controls;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using GraphReader.Export;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class ConsecutivePlacementTests
{
    [STATestMethod(UseSTASynchronizationContext = true)]
    public async Task ShiftKeepsPlacementActiveAndEachNewPointRequiresReview()
    {
        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        using var temp = new IntegrationSmokeTestEnvironment();
        using var workspace = new ManualPreviewWorkspaceService();
        var image = Path.Combine(RepositoryRoot.Find(), "tests", "GraphReader.Integration.Tests",
            "Validation", "Specifications", "reviewed-export.png");
        var tab = (await workspace.ImportImagesAsync([image], default)).Single();
        var series = workspace.AddSeries(tab.TabId, new("Response", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        workspace.Calibrate(tab.TabId, new(new(40, 152), new(40, 24), new(296, 152), 64, 18, 10)
            { UsesPrintedXCoordinates = true });
        workspace.AddPhaseDivider(tab.TabId, 120, "b", "Training");

        var resources = new ResourceDictionary();
        foreach (string name in new[] { "DesignTokens", "LightTheme", "Controls" })
            resources.MergedDictionaries.Add(new() { Source = new Uri($"/GraphReader.App;component/Themes/{name}.xaml", UriKind.Relative) });
        using var vm = new MainWindowViewModel(workspace, new LocalizationService(resources))
            { SelectedTab = tab, SelectedSeriesId = series.SeriesId };
        var window = new MainWindow(null, resources);
        ((MainWindowViewModel)window.DataContext).Dispose();
        window.DataContext = vm;
        try
        {
            window.Show();
            var canvas = (GraphCanvasControl)window.FindName("GraphCanvasHost");
            vm.BeginAddPointCommand.Execute(null);
            StringAssert.Contains(vm.EditorInstruction, "Shift");
            canvas.CrosshairPosition = new Point(200d / tab.PixelWidth, 56d / tab.PixelHeight);
            Assert.IsTrue(canvas.TryHandleKeyboardInput(Key.Enter, ModifierKeys.Shift));
            Assert.AreEqual(ManualEditorMode.AddPoint, vm.EditorMode);
            Assert.IsTrue(canvas.TryHandleKeyboardInput(Key.Enter, ModifierKeys.None, isRepeat: true));
            Assert.HasCount(1, tab.Points);

            canvas.CrosshairPosition = new Point(232d / tab.PixelWidth, 104d / tab.PixelHeight);
            Assert.IsTrue(canvas.TryHandleKeyboardInput(Key.Enter, ModifierKeys.None));
            Assert.AreEqual(ManualEditorMode.Select, vm.EditorMode);
            Assert.IsTrue(canvas.TryHandleKeyboardInput(Key.Enter, ModifierKeys.Shift));
            Assert.HasCount(2, tab.Points);
            vm.UndoCommand.Execute(null);
            Assert.HasCount(1, tab.Points);
            Assert.AreEqual(new Point(200, 56), new Point(tab.Points[0].PixelX, tab.Points[0].PixelY));
            vm.RedoCommand.Execute(null);
            await CheckEvidence("pending", 0);
            vm.SelectedPointId = tab.Points[1].PointId;
            vm.AcceptCorrectedPointCommand.Execute(null);
            await CheckEvidence("reviewed", 1);
        }
        finally
        {
            window.DataContext = null;
            window.Close();
        }

        async Task CheckEvidence(string stage, int accepted)
        {
            var result = await workspace.ExportAsync(tab.TabId, temp.PathFor(stage), default);
            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual("x_value,y_value,phase\n15,48,b\n16,24,b\n",
                await File.ReadAllTextAsync(result.MinimalArtifacts.Single().WrittenPath!));
            var rows = result.AuditArtifacts.Single(a => a.Format == ExportAuditArtifactFormat.Json).Rows;
            Assert.HasCount(2, rows);
            Assert.AreEqual(ExportReviewStatus.Corrected, rows[0].ReviewStatus);
            Assert.AreEqual(accepted == 1 ? ExportReviewStatus.Accepted : ExportReviewStatus.Corrected, rows[1].ReviewStatus);
            Assert.AreEqual(accepted, rows.Count(r => r.ReviewEvidence!.PointActions.Split('|').Contains("point_accepted")));
            Assert.IsTrue(rows.All(r => r.ReviewEvidence!.PhaseLabelReview == "explicit_label"));
            Assert.AreEqual(new Point(232, 104), new Point(tab.Points[1].PixelX, tab.Points[1].PixelY));
        }
    }
}
