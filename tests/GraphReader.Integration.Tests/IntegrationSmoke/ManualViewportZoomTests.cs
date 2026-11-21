// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
public sealed class ManualViewportZoomTests(TestContext context)
{
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);

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

            if (anchorWheel) Assert.IsTrue(image.CaptureMouse());
            for (int i = 0; i < 4; i++)
            {
                if (anchorWheel) Wheel(120, pointer);
                else canvas.ZoomBy(1.25);
                window.UpdateLayout();
            }

            Assert.AreEqual(anchorWheel, disks.All(Visible));
            if (anchorWheel)
            {
                // Reverse and repeat the final step from a nonzero scroll offset.
                Wheel(-120, pointer);
                Wheel(120, pointer);
                window.UpdateLayout();
                Assert.IsTrue(scroll.HorizontalOffset > 0 && scroll.VerticalOffset > 0);
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
            Task? placement = null;
            canvas.ImagePointInvoked += (_, e) => placement = vm.HandleCanvasPointAsync(e.ImagePoint);
            foreach (Point disk in disks)
            {
                Assert.IsTrue(Visible(disk));
                vm.BeginAddPointCommand.Execute(null);
                canvas.CrosshairPosition = new Point(disk.X / tab.PixelWidth, disk.Y / tab.PixelHeight);
                Assert.IsTrue(canvas.TryHandleKeyboardInput(Key.Enter, ModifierKeys.None));
                await placement!;
                Assert.AreEqual(ReviewStatus.Corrected, workspace.CurrentProject.Panels.Single().Points[^1].ReviewStatus);
            }
            if (anchorWheel)
            {
                scroll.ScrollToHorizontalOffset(0);
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2);
                window.UpdateLayout();
                Point edgePointer = viewport.TranslatePoint(new Point(viewport.ActualWidth / 2, viewport.ActualHeight / 2), scroll);
                Point before = scroll.TranslatePoint(edgePointer, image);
                Wheel(-120, edgePointer);
                Point after = scroll.TranslatePoint(edgePointer, image);
                Assert.AreEqual(0d, scroll.HorizontalOffset);
                Assert.IsTrue(after.X > before.X + 1, "The left edge must prevent full horizontal anchoring.");
                Assert.AreEqual(before.Y, after.Y, 1, "The scrollable vertical axis must still anchor.");
                context.WriteLine($"Wheel at left limit: horizontal offset {scroll.HorizontalOffset}, source delta {after - before}.");
            }

            // Keep one point pending through the first export, then accept just that point.
            vm.SelectedPointId = tab.Points[0].PointId;
            vm.AcceptCorrectedPointCommand.Execute(null);
            await CheckExport("pending", lastAccepted: false);
            vm.SelectedPointId = tab.Points[1].PointId;
            vm.AcceptCorrectedPointCommand.Execute(null);
            await CheckExport("accepted", lastAccepted: true);

            void Wheel(int delta, Point at)
            {
                // Send the wheel message only to this HWND. Capture keeps WPF hit testing
                // on the graph without moving the desktop cursor or generating global input.
                const uint mouseWheel = 0x020A;
                var screen = scroll.PointToScreen(at);
                int x = (int)Math.Round(screen.X);
                int y = (int)Math.Round(screen.Y);
                double previous = canvas.ZoomLevel;
                SendMessageW(new WindowInteropHelper(window).Handle, mouseWheel, delta << 16, (y << 16) | (x & 0xffff));
                Assert.AreEqual(previous * (delta > 0 ? 1.25 : 0.8), canvas.ZoomLevel, 1e-9);
            }

            async Task CheckExport(string folder, bool lastAccepted)
            {
                var result = await workspace.ExportAsync(tab.TabId, temp.PathFor(folder), token);
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
                    bool accepted = i == 0 || lastAccepted;
                    Assert.AreEqual(accepted ? "accepted" : "corrected", row.GetProperty("review_status").GetString());
                    Assert.AreEqual(accepted ? "point_placed|point_accepted" : "point_placed", row.GetProperty("point_review_actions").GetString());
                    Assert.AreEqual("manual", row.GetProperty("phase_source").GetString());
                    Assert.AreEqual("explicit_label", row.GetProperty("phase_label_review").GetString());
                }
                context.WriteLine($"{folder}: strict {(lastAccepted ? 2 : 1)}/2 at (200,56) and (232,104).");
            }
        }
        finally
        {
            window.Close();
        }
    }
}
