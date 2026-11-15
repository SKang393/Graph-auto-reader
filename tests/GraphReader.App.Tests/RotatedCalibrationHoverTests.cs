// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Models;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class RotatedCalibrationHoverTests
{
    [TestMethod]
    public void CanvasHoverMapsInteriorOriginalPixelThroughPositiveAndNegativeTwoDegreeCalibration()
    {
        StaTestHost.Run(() =>
        {
            foreach (double degrees in new[] { 2d, -2d })
            {
                using var viewModel = new MainWindowViewModel(new FakeWorkspaceService());
                WorkspaceTabViewModel tab = viewModel.SelectedTab!;

                const double originX = 100;
                const double originY = 400;
                const double yRange = 10;
                const double xVectorLengthPixels = 400;
                const double yVectorLengthPixels = 200;
                double radians = degrees * Math.PI / 180d;
                double cosine = Math.Cos(radians);
                double sine = Math.Sin(radians);
                var origin = new PixelPoint(originX, originY);
                var upper = new PixelPoint(originX + sine * yVectorLengthPixels, originY - cosine * yVectorLengthPixels);
                var final = new PixelPoint(originX + cosine * xVectorLengthPixels, originY + sine * xVectorLengthPixels);

                // Null scalar transforms deliberately exercise the three-anchor affine map.
                tab.Calibration = new ManualCalibrationState(origin, upper, final,
                    YMaximum: yRange, XMaximum: 21, XTransform: null, YTransform: null,
                    Confidence: 1, FirstVisibleSession: 1);

                // This is the original-pixel location for graph coordinate (11, 5),
                // halfway along both calibrated axis vectors.
                var pixel = new Point(
                    originX + cosine * xVectorLengthPixels / 2 + sine * yVectorLengthPixels / 2,
                    originY + sine * xVectorLengthPixels / 2 - cosine * yVectorLengthPixels / 2);

                viewModel.HandleCanvasNavigation(pixel);

                Assert.AreEqual(pixel, viewModel.Magnifier.PixelPosition, "The hover location stays in original image pixels.");
                Assert.IsNotNull(viewModel.Magnifier.GraphPosition);
                Assert.AreEqual(11d, viewModel.Magnifier.GraphPosition!.Value.X, 1e-9, $"X mapping at {degrees} degrees.");
                Assert.AreEqual(5d, viewModel.Magnifier.GraphPosition.Value.Y, 1e-9, $"Y mapping at {degrees} degrees.");
            }
        });
    }

    [TestMethod]
    public void AffineCalibrationNoticeAndPhaseCommandsTrackApplyUndoRedoAndTabSelection()
    {
        WithImages((firstPath, secondPath) =>
        {
            using var workspace = new ManualPreviewWorkspaceService();
            WorkspaceTabViewModel[] tabs = Pump(workspace.ImportImagesAsync([firstPath, secondPath], CancellationToken.None)).ToArray();
            Assert.HasCount(2, tabs);
            using var viewModel = new MainWindowViewModel(workspace);
            WorkspaceTabViewModel calibratedTab = tabs[0];
            WorkspaceTabViewModel uncalibratedTab = tabs[1];
            viewModel.SelectedTab = calibratedTab;

            Assert.IsFalse(viewModel.HasAffineManualCalibration);
            Assert.IsTrue(viewModel.BeginAddPhaseDividerCommand.CanExecute(null));
            Assert.IsFalse(viewModel.BeginMovePhaseDividerCommand.CanExecute(null));

            const double originX = 30;
            const double originY = 90;
            const double xLength = 100;
            const double yLength = 60;
            double radians = 2d * Math.PI / 180d;
            double cosine = Math.Cos(radians);
            double sine = Math.Sin(radians);
            viewModel.ManualYMaximum = 10;
            viewModel.ManualXMaximum = 21;
            viewModel.StartCalibrationCommand.Execute(null);
            Pump(viewModel.HandleCanvasPointAsync(new(originX, originY)));
            Pump(viewModel.HandleCanvasPointAsync(new(originX + sine * yLength, originY - cosine * yLength)));
            Pump(viewModel.HandleCanvasPointAsync(new(originX + cosine * xLength, originY + sine * xLength)));

            Assert.IsNotNull(calibratedTab.Calibration);
            Assert.IsTrue(viewModel.HasAffineManualCalibration);
            Assert.IsFalse(viewModel.BeginAddPhaseDividerCommand.CanExecute(null));
            Assert.IsFalse(viewModel.BeginMovePhaseDividerCommand.CanExecute(null));
            Assert.IsTrue(viewModel.UndoCommand.CanExecute(null));
            viewModel.UndoCommand.Execute(null);
            Assert.IsNull(calibratedTab.Calibration);
            Assert.IsFalse(viewModel.HasAffineManualCalibration);
            Assert.IsTrue(viewModel.BeginAddPhaseDividerCommand.CanExecute(null));
            Assert.IsTrue(viewModel.RedoCommand.CanExecute(null));
            viewModel.RedoCommand.Execute(null);
            Assert.IsNotNull(calibratedTab.Calibration);
            Assert.IsTrue(viewModel.HasAffineManualCalibration);
            Assert.IsFalse(viewModel.BeginAddPhaseDividerCommand.CanExecute(null));

            viewModel.SelectedTab = uncalibratedTab;
            Assert.IsFalse(viewModel.HasAffineManualCalibration);
            Assert.IsTrue(viewModel.BeginAddPhaseDividerCommand.CanExecute(null));
            EditablePhaseDivider divider = workspace.AddPhaseDivider(uncalibratedTab.TabId, 80, "b", "Intervention");
            viewModel.SelectedDividerId = divider.DividerId;
            Assert.IsTrue(viewModel.BeginMovePhaseDividerCommand.CanExecute(null));

            viewModel.SelectedTab = calibratedTab;
            Assert.IsTrue(viewModel.HasAffineManualCalibration);
            Assert.IsFalse(viewModel.BeginAddPhaseDividerCommand.CanExecute(null));
            Assert.IsFalse(viewModel.BeginMovePhaseDividerCommand.CanExecute(null));
            viewModel.SelectedTab = uncalibratedTab;
            Assert.IsFalse(viewModel.HasAffineManualCalibration);
            Assert.IsTrue(viewModel.BeginMovePhaseDividerCommand.CanExecute(null));
        });
    }

    private static void WithImages(Action<string, string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), $"graph-reader-affine-hover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string first = Path.Combine(root, "first.png");
        string second = Path.Combine(root, "second.png");
        try
        {
            StaTestHost.Run(() =>
            {
                var bitmap = BitmapSource.Create(160, 120, 96, 96, PixelFormats.Gray8, null,
                    Enumerable.Repeat((byte)255, 160 * 120).ToArray(), 160);
                bitmap.Freeze();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(first)) encoder.Save(stream);
                File.Copy(first, second);
                action(first, second);
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
