// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Windows;
using System.Windows.Controls;
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
public sealed class MagnifierInspectionTests
{
    [STATestMethod(UseSTASynchronizationContext = true)]
    [DataRow(200, 80)]
    [DataRow(80, 200)]
    public async Task InspectingPendingPointRendersTwoTimesSourcePixelsAndPreservesExport(int width, int height)
    {
        const int x = 50, y = 56;
        // An asymmetric 3-by-3 patch distinguishes either axis moving by a single source pixel.
        uint[] patch = [
            0xFFFF0000, 0xFF00FF00, 0xFF0000FF,
            0xFF00FFFF, 0xFFFF00FF, 0xFFFFFF00,
            0xFF000000, 0xFFFFFFFF, 0xFF888888,
        ];
        using var temp = new IntegrationSmokeTestEnvironment();
        string file = temp.PathFor("inspection.png");
        var source = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        source.WritePixels(new Int32Rect(x - 1, y - 1, 3, 3), patch, 3 * sizeof(uint), 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var stream = File.Create(file)) encoder.Save(stream);

        var token = CancellationToken.None;
        using var workspace = new ManualPreviewWorkspaceService();
        var tab = (await workspace.ImportImagesAsync([file], token)).Single();
        var series = workspace.AddSeries(tab.TabId, new("Response", "●", MarkerShape.Circle,
            MarkerFill.Filled, SemanticRole.Intervention));
        workspace.Calibrate(tab.TabId, new(new(20, 60), new(20, 20), new(60, 60), 10, 5, 1)
            { UsesPrintedXCoordinates = true });
        var accepted = workspace.AddPoint(tab.TabId, series.SeriesId, 30, 40);
        workspace.SetPointReviewStatus(tab.TabId, accepted.PointId, ReviewStatus.Accepted);
        var pending = workspace.AddPoint(tab.TabId, series.SeriesId, x, y);
        workspace.ConfirmPhaseLabel(tab.TabId, workspace.CurrentProject.Panels.Single().Phases.Single());

        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
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
            var before = await Export("before");
            var snapshot = workspace.CurrentProject;
            vm.Magnifier.ZoomLevel = 2;
            var issue = vm.ReviewIssues.Single(i => i.EntityId == pending.PointId && i.PointStatusToAccept == ReviewStatus.Corrected);
            vm.SelectReviewIssueCommand.Execute(issue);
            window.UpdateLayout();

            var lens = (MagnifierControl)window.FindName("MagnifierInspector");
            Assert.AreEqual(new Point(x, y), lens.PixelPosition);
            Assert.AreEqual(2d, lens.ZoomLevel);
            Assert.IsTrue(lens.ShowCrosshair);
            var viewport = (Canvas)lens.FindName("LensViewport");
            var crosshair = ((Grid)viewport.Parent).Children.OfType<CrosshairOverlay>().Single();
            Assert.IsTrue(crosshair.IsVisible);
            var aim = crosshair.TranslatePoint(new Point(crosshair.Position.X * crosshair.ActualWidth,
                crosshair.Position.Y * crosshair.ActualHeight), viewport);
            Assert.AreEqual(viewport.ActualWidth / 2, aim.X, 1e-6);
            Assert.AreEqual(viewport.ActualHeight / 2, aim.Y, 1e-6);

            // Render the image layer underneath the reticle. Sample each source square
            // half a DIP inside each side, so a high-DPI window can use fractional DIPs.
            var raster = new RenderTargetBitmap((int)Math.Ceiling(viewport.ActualWidth * 2),
                (int)Math.Ceiling(viewport.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            raster.Render(viewport);
            for (int j = 0; j < 6; j++)
            {
                for (int i = 0; i < 6; i++)
                {
                    int rx = (int)Math.Floor(2 * (aim.X - 2 + i + 0.5));
                    int ry = (int)Math.Floor(2 * (aim.Y - 2 + j + 0.5));
                    uint[] pixel = new uint[1];
                    raster.CopyPixels(new Int32Rect(rx, ry, 1, 1), pixel, sizeof(uint), 0);
                    Assert.AreEqual(patch[(j / 2) * 3 + i / 2], pixel[0], $"Image sample at ({i}, {j}) from the patch corner.");
                }
            }

            Assert.AreSame(snapshot, workspace.CurrentProject);
            var after = await Export("after");
            CollectionAssert.AreEqual(before.Csv, after.Csv);
            CollectionAssert.AreEqual(before.Rows, after.Rows);
        }
        finally
        {
            window.DataContext = null;
            window.Close();
        }

        async Task<(byte[] Csv, ExtendedAuditRow[] Rows)> Export(string stage)
        {
            var result = await workspace.ExportAsync(tab.TabId, temp.PathFor(stage), token);
            Assert.IsTrue(result.Succeeded);
            string path = result.MinimalArtifacts.Single().WrittenPath!;
            Assert.AreEqual("x_value,y_value,phase\n2,5,a\n4,1,a\n", await File.ReadAllTextAsync(path, token));
            var rows = result.AuditArtifacts.Single(a => a.Format == ExportAuditArtifactFormat.Json).Rows.ToArray();
            Assert.HasCount(2, rows);
            Assert.AreEqual(ExportReviewStatus.Accepted, rows[0].ReviewStatus);
            Assert.AreEqual("point_placed|point_accepted", rows[0].ReviewEvidence!.PointActions);
            Assert.AreEqual(ExportReviewStatus.Corrected, rows[1].ReviewStatus);
            Assert.AreEqual("point_placed", rows[1].ReviewEvidence!.PointActions);
            return (await File.ReadAllBytesAsync(path, token), rows);
        }
    }
}
