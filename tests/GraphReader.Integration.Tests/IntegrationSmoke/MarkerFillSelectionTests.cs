// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
public sealed class MarkerFillSelectionTests
{
    [STATestMethod(UseSTASynchronizationContext = true)]
    [DataRow(MarkerFill.Filled)]
    [DataRow(MarkerFill.Open)]
    public async Task AmbiguousFillRequiresSeriesChoiceBeforeConsecutivePlacement(MarkerFill fill)
    {
        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        using var temp = new IntegrationSmokeTestEnvironment();
        var image = temp.PathFor("crossing.png");
        DrawCrossing(image, fill);
        using var workspace = new ManualPreviewWorkspaceService();
        var tab = (await workspace.ImportImagesAsync([image], default)).Single();
        var otherFill = fill == MarkerFill.Open ? MarkerFill.Filled : MarkerFill.Open;
        var a = workspace.AddSeries(tab.TabId, new("Left trace", "A", MarkerShape.Square, fill, SemanticRole.Intervention));
        var b = workspace.AddSeries(tab.TabId, new("Right trace", "B", MarkerShape.Circle, fill, SemanticRole.Intervention));
        var c = workspace.AddSeries(tab.TabId, new("Control", "C", MarkerShape.Circle, otherFill, SemanticRole.Intervention));
        workspace.Calibrate(tab.TabId, new(new(10, 70), new(10, 10), new(110, 70), 60, 11, 1)
            { UsesPrintedXCoordinates = true });

        var resources = new ResourceDictionary();
        foreach (string name in new[] { "DesignTokens", "LightTheme", "Controls" })
            resources.MergedDictionaries.Add(new() { Source = new Uri($"/GraphReader.App;component/Themes/{name}.xaml", UriKind.Relative) });
        using var vm = new MainWindowViewModel(workspace, new LocalizationService(resources)) { SelectedTab = tab };
        var window = new MainWindow(null, resources);
        ((MainWindowViewModel)window.DataContext).Dispose();
        window.DataContext = vm;
        try
        {
            window.Show();
            var canvas = (GraphCanvasControl)window.FindName("GraphCanvasHost");
            var filled = (Button)window.FindName("AddFilledPointButton");
            var open = (Button)window.FindName("AddOpenPointButton");
            var ambiguous = fill == MarkerFill.Filled ? filled : open;
            var unique = fill == MarkerFill.Filled ? open : filled;

            // A single available target remains selectable via its fill. Keep its placement mode armed.
            a.SelectCommand.Execute(null);
            unique.Command.Execute(null);
            Assert.AreEqual(c.SeriesId, vm.SelectedSeriesId);
            Place(20, 18, ModifierKeys.Shift);
            Assert.AreEqual(ManualEditorMode.AddPoint, vm.EditorMode);
            var snapshot = workspace.CurrentProject;
            ambiguous.Command.Execute(null);
            Assert.AreEqual(ManualEditorMode.Select, vm.EditorMode);
            Assert.AreEqual(c.SeriesId, vm.SelectedSeriesId);
            StringAssert.Contains(vm.StatusMessage, "Select the intended series card");
            Place(60, 40, ModifierKeys.Shift);
            Assert.AreSame(snapshot, workspace.CurrentProject);
            Assert.HasCount(1, tab.Points);

            // Both traces can legitimately own the crossing. Each placement and acceptance is explicit.
            b.SelectCommand.Execute(null);
            ambiguous.Command.Execute(null);
            Place(60, 40, ModifierKeys.Shift);
            Place(90, 55, ModifierKeys.None);
            Assert.AreEqual(ManualEditorMode.Select, vm.EditorMode);
            a.SelectCommand.Execute(null);
            ambiguous.Command.Execute(null);
            Place(60, 40, ModifierKeys.None);
            Assert.HasCount(4, tab.Points);
            workspace.ConfirmPhaseLabel(tab.TabId, workspace.CurrentProject.Panels.Single().Phases.Single());
            var crossing = tab.Points.Single(p => p.SeriesId == b.SeriesId && p.PixelX == 60);
            vm.SelectedDataPreviewRow = vm.DataPreviewRows.Single(p => p.PointId == crossing.PointId);
            Assert.IsTrue(vm.AcceptCorrectedPointCommand.CanExecute(null));
            vm.AcceptCorrectedPointCommand.Execute(null);
            var result = await workspace.ExportAsync(tab.TabId, temp.PathFor("export"), default);
            Assert.IsTrue(result.Succeeded);
            var rows = result.AuditArtifacts.Where(x => x.Format == ExportAuditArtifactFormat.Json)
                .SelectMany(x => x.Rows).ToArray();
            Assert.HasCount(4, rows);
            Assert.HasCount(2, rows.Where(r => r.OriginalPixel.X == 60 && r.OriginalPixel.Y == 40).ToArray());
            var reviewed = rows.Single(r => r.ReviewStatus == ExportReviewStatus.Accepted);
            Assert.AreEqual(Guid.Parse(crossing.PointId), reviewed.PointId);
            Assert.AreEqual(Guid.Parse(b.SeriesId), reviewed.TargetInterventionSeriesId);
            Assert.AreEqual("explicit_label", reviewed.ReviewEvidence!.PhaseLabelReview);
            Assert.AreEqual("point_placed|point_accepted", reviewed.ReviewEvidence.PointActions);
            Assert.IsTrue(rows.All(r => r.TargetInterventionSeriesId == r.SourceSeriesId));
            Assert.IsTrue(rows.Where(r => r.PointId != reviewed.PointId).All(r =>
                r.ReviewStatus == ExportReviewStatus.Corrected && r.ReviewEvidence!.PointActions == "point_placed"));

            void Place(int x, int y, ModifierKeys modifiers)
            {
                canvas.CrosshairPosition = new Point((double)x / tab.PixelWidth, (double)y / tab.PixelHeight);
                Assert.IsTrue(canvas.TryHandleKeyboardInput(Key.Enter, modifiers));
            }
        }
        finally
        {
            window.DataContext = null;
            window.Close();
        }
    }

    private static void DrawCrossing(string file, MarkerFill fill)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            var pen = new Pen(Brushes.Black, 1);
            var brush = fill == MarkerFill.Filled ? Brushes.Black : Brushes.White;
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 128, 80));
            dc.DrawLine(pen, new Point(30, 55), new Point(90, 25));
            dc.DrawLine(pen, new Point(30, 25), new Point(90, 55));
            foreach (var p in new Point[] { new(30, 55), new(60, 40), new(90, 25) })
                dc.DrawRectangle(brush, pen, new Rect(p.X - 4, p.Y - 4, 8, 8));
            foreach (var p in new Point[] { new(30, 25), new(60, 40), new(90, 55) })
                dc.DrawEllipse(brush, pen, p, 3, 3);
            dc.DrawEllipse(fill == MarkerFill.Filled ? Brushes.White : Brushes.Black, pen, new Point(20, 18), 3, 3);
        }
        var raster = new RenderTargetBitmap(128, 80, 96, 96, PixelFormats.Pbgra32);
        raster.Render(drawing);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(raster));
        using var stream = File.Create(file);
        png.Save(stream);
    }
}
