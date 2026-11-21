// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
public sealed class ManualViewportZoomTests
{
    [STATestMethod(UseSTASynchronizationContext = true)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ManualPlacementAfterZoomPreservesOriginalPixelsAndPrintedValues(bool anchorWheel)
    {
        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        using var temp = new IntegrationSmokeTestEnvironment();
        string fixture = Path.Combine(RepositoryRoot.Find(), "tests", "GraphReader.Integration.Tests",
            "Validation", "Specifications", "reviewed-export.png");
        using var workspace = new ManualPreviewWorkspaceService();
        var token = CancellationToken.None;
        var tab = (await workspace.ImportImagesAsync([fixture], token)).Single();
        var series = workspace.AddSeries(tab.TabId, new("Response", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        workspace.Calibrate(tab.TabId, new(new(40, 152), new(40, 24), new(296, 152), 64, 18, 10)
            { UsesPrintedXCoordinates = true });
        workspace.AddPhaseDivider(tab.TabId, 120, "b", "Training");
        using var vm = new MainWindowViewModel(workspace, localizationService: new LocalizationService(new ResourceDictionary()));
        vm.SelectedTab = tab;
        vm.SelectedSeriesId = series.SeriesId;

        var canvas = new GraphCanvasControl { ImageSource = tab.ImageSource };
        var window = new Window { Content = canvas, Width = 324, Height = 200, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var image = (Image)canvas.FindName("GraphImage");
            var scroll = (ScrollViewer)canvas.FindName("ViewportScrollViewer");
            var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            Point[] disks = [new(200, 56), new(232, 104)];
            Point pointer = image.TranslatePoint(disks[0], scroll);
            bool Visible(Point disk) => new Rect(viewport.RenderSize).Contains(image.TranslatePoint(disk, viewport));

            for (int i = 0; i < 4; i++)
            {
                if (anchorWheel) canvas.ZoomAtViewportPoint(1.25, pointer);
                else canvas.ZoomBy(1.25);
                window.UpdateLayout();
            }

            Assert.AreEqual(anchorWheel, disks.All(Visible));
            if (anchorWheel)
            {
                // Reverse and repeat the final step from a nonzero scroll offset.
                canvas.ZoomAtViewportPoint(0.8, pointer);
                canvas.ZoomAtViewportPoint(1.25, pointer);
                window.UpdateLayout();
                Assert.IsTrue((scroll.TranslatePoint(pointer, image) - disks[0]).Length < 1);
            }
            else
            {
                Point middle = disks[0] + (disks[1] - disks[0]) / 2;
                Point position = image.TranslatePoint(middle, viewport);
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + position.X - viewport.ActualWidth / 2);
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + position.Y - viewport.ActualHeight / 2);
                window.UpdateLayout();
            }
            Assert.IsEmpty(tab.Points);
            foreach (Point disk in disks)
            {
                Assert.IsTrue(Visible(disk));
                vm.BeginAddPointCommand.Execute(null);
                await vm.HandleCanvasPointAsync(disk);
                Assert.AreEqual(ReviewStatus.Corrected, workspace.CurrentProject.Panels.Single().Points[^1].ReviewStatus);
                vm.AcceptCorrectedPointCommand.Execute(null);
            }
            var result = await workspace.ExportAsync(tab.TabId, temp.PathFor("export"), token);
            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual("x_value,y_value,phase\n15,48,b\n16,24,b\n",
                await File.ReadAllTextAsync(result.MinimalArtifacts.Single().WrittenPath!, token));
            using var audit = JsonDocument.Parse(await File.ReadAllBytesAsync(result.AuditArtifacts.Single(
                a => a.Format == ExportAuditArtifactFormat.Json).WrittenPath!, token));
            var rows = audit.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            Assert.HasCount(disks.Length, rows);
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                Assert.AreEqual(disks[i], new Point(row.GetProperty("original_pixel_x").GetDouble(), row.GetProperty("original_pixel_y").GetDouble()));
                Assert.AreEqual("printed_session", row.GetProperty("export_mode").GetString());
                Assert.AreEqual("valid", row.GetProperty("calibration_status").GetString());
                Assert.AreEqual("accepted", row.GetProperty("review_status").GetString());
                Assert.AreEqual("point_placed|point_accepted", row.GetProperty("point_review_actions").GetString());
                Assert.AreEqual("manual", row.GetProperty("phase_source").GetString());
                Assert.AreEqual("explicit_label", row.GetProperty("phase_label_review").GetString());
            }
        }
        finally
        {
            window.Close();
        }
    }
}
