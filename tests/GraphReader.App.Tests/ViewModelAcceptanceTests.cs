// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Appearance;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ViewModelAcceptanceTests
{
    [TestMethod]
    public void ExplicitRecordedWorkspaceIsDeterministicAndReviewReady()
    {
        var first = new MainWindowViewModel(new FakeWorkspaceService());
        var second = new MainWindowViewModel(new FakeWorkspaceService());

        Assert.IsNotEmpty(first.Tabs);
        Assert.IsNotNull(first.SelectedTab);
        Assert.AreSame(first.SelectedTab.SeriesCards, first.SeriesCards);
        Assert.IsGreaterThanOrEqualTo(2, first.SeriesCards.Count);
        Assert.IsTrue(first.SeriesCards.All(card =>
            !string.IsNullOrWhiteSpace(card.Symbol) &&
            !string.IsNullOrWhiteSpace(card.AccessibleName) &&
            !string.IsNullOrWhiteSpace(card.Label) &&
            card.Count > 0 &&
            card.Confidence is >= 0 and <= 1));

        CollectionAssert.AreEqual(Snapshot(first), Snapshot(second));
        Assert.AreEqual(ApplicationTheme.System, first.AppearanceMode);
        Assert.IsTrue(first.IsPhaseOverlayVisible);
    }

    [TestMethod]
    public void ManualCalibrationUsesExplicitFirstVisibleSession()
    {
        var viewModel = CreateWorkspace();

        Assert.AreEqual(1, viewModel.ManualFirstVisibleSession);
        Assert.AreEqual("1", viewModel.ManualFirstVisibleSessionText);
        Assert.IsTrue(viewModel.IsManualFirstVisibleSessionInputValid);

        viewModel.ManualFirstVisibleSession = 7;

        Assert.AreEqual(7, viewModel.ManualFirstVisibleSession);
        Assert.AreEqual("7", viewModel.ManualFirstVisibleSessionText);
    }

    [TestMethod]
    public void InvalidFirstVisibleSessionCannotReuseStaleValueForCalibration()
    {
        string root = Path.Combine(Path.GetTempPath(), $"graph-reader-session-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string imagePath = Path.Combine(root, "synthetic.png");

        try
        {
            StaTestHost.Run(() =>
            {
                WriteImage(imagePath, 160, 120);
                var workspace = new ManualPreviewWorkspaceService();
                _ = workspace.ImportImagesAsync([imagePath], CancellationToken.None).GetAwaiter().GetResult();
                using var viewModel = new MainWindowViewModel(workspace);
                viewModel.ManualXMaximum = 35;
                viewModel.ManualFirstVisibleSessionText = "29";

                Assert.AreEqual(29, viewModel.ManualFirstVisibleSession);
                Assert.IsTrue(viewModel.IsManualFirstVisibleSessionInputValid);
                Assert.IsTrue(viewModel.StartCalibrationCommand.CanExecute(null));

                viewModel.ManualFirstVisibleSessionText = "29x";

                Assert.AreEqual(29, viewModel.ManualFirstVisibleSession);
                Assert.IsFalse(viewModel.IsManualFirstVisibleSessionInputValid);
                Assert.IsFalse(viewModel.StartCalibrationCommand.CanExecute(null));

                viewModel.ManualFirstVisibleSessionText = "2147483648";
                Assert.IsFalse(viewModel.IsManualFirstVisibleSessionInputValid);
                Assert.IsFalse(viewModel.StartCalibrationCommand.CanExecute(null));

                viewModel.ManualFirstVisibleSessionText = "0";
                Assert.AreEqual(0, viewModel.ManualFirstVisibleSession);
                Assert.IsFalse(viewModel.IsManualFirstVisibleSessionInputValid);
                Assert.IsFalse(viewModel.StartCalibrationCommand.CanExecute(null));

                viewModel.ManualFirstVisibleSessionText = "35";
                Assert.IsFalse(viewModel.IsManualFirstVisibleSessionInputValid);
                Assert.IsFalse(viewModel.StartCalibrationCommand.CanExecute(null));

                viewModel.ManualXMaximum = 36;
                Assert.IsTrue(viewModel.IsManualFirstVisibleSessionInputValid);
                Assert.IsTrue(viewModel.StartCalibrationCommand.CanExecute(null));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void CalibrationInputChangesClearAnchorsAndInvalidTextCannotCompleteCalibration()
    {
        string root = Path.Combine(Path.GetTempPath(), $"graph-reader-session-anchor-reset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string imagePath = Path.Combine(root, "synthetic.png");

        try
        {
            StaTestHost.Run(() =>
            {
                WriteImage(imagePath, 160, 120);
                var workspace = new ManualPreviewWorkspaceService();
                _ = workspace.ImportImagesAsync([imagePath], CancellationToken.None).GetAwaiter().GetResult();
                using var viewModel = new MainWindowViewModel(workspace);
                viewModel.ManualXMaximum = 35;
                viewModel.ManualFirstVisibleSessionText = "29";
                viewModel.StartCalibrationCommand.Execute(null);

                Assert.AreEqual(ManualEditorMode.Calibration, viewModel.EditorMode);
                viewModel.HandleCanvasPointAsync(new System.Windows.Point(10, 100)).GetAwaiter().GetResult();
                viewModel.HandleCanvasPointAsync(new System.Windows.Point(10, 10)).GetAwaiter().GetResult();

                viewModel.ManualFirstVisibleSessionText = "31";
                Assert.AreEqual(31, viewModel.ManualFirstVisibleSession);
                viewModel.HandleCanvasPointAsync(new System.Windows.Point(10, 100)).GetAwaiter().GetResult();
                Assert.IsNull(workspace.CurrentProject.Panels.Single().Calibration);
                Assert.AreEqual(ManualEditorMode.Calibration, viewModel.EditorMode);

                viewModel.ManualXMaximum = 36;
                viewModel.HandleCanvasPointAsync(new System.Windows.Point(10, 10)).GetAwaiter().GetResult();
                viewModel.HandleCanvasPointAsync(new System.Windows.Point(150, 100)).GetAwaiter().GetResult();
                Assert.IsNull(workspace.CurrentProject.Panels.Single().Calibration);
                Assert.AreEqual(ManualEditorMode.Calibration, viewModel.EditorMode);

                viewModel.ManualFirstVisibleSessionText = "31x";
                Assert.AreEqual(31, viewModel.ManualFirstVisibleSession);
                Assert.IsFalse(viewModel.IsManualFirstVisibleSessionInputValid);
                for (int index = 0; index < 3; index++)
                {
                    viewModel.HandleCanvasPointAsync(new System.Windows.Point(20 + index, 90 - index)).GetAwaiter().GetResult();
                }

                Assert.AreEqual(ManualEditorMode.Calibration, viewModel.EditorMode);
                Assert.IsNull(workspace.CurrentProject.Panels.Single().Calibration);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void AddAndDeleteUpdateSeriesCountImmediately()
    {
        var viewModel = CreateWorkspace();
        var series = viewModel.SeriesCards[0];
        var originalIds = viewModel.SelectedTab!.Points.Select(point => point.PointId).ToHashSet(StringComparer.Ordinal);
        var originalCount = series.Count;
        var countNotifications = WatchCount(series);

        viewModel.AddPoint(series.SeriesId);

        Assert.AreEqual(originalCount + 1, series.Count);
        var addedPoint = viewModel.SelectedTab.Points.Single(point => !originalIds.Contains(point.PointId));
        Assert.AreEqual(series.SeriesId, addedPoint.SeriesId);
        Assert.IsGreaterThanOrEqualTo(1, countNotifications());

        viewModel.DeletePoint(addedPoint.PointId);

        Assert.AreEqual(originalCount, series.Count);
        Assert.IsFalse(viewModel.SelectedTab.Points.Any(point => point.PointId == addedPoint.PointId));
        Assert.IsGreaterThanOrEqualTo(2, countNotifications());
    }

    [TestMethod]
    public void MoveUpdatesCoordinatesAndNotifiesCountBinding()
    {
        var viewModel = CreateWorkspace();
        var point = viewModel.SelectedTab!.Points[0];
        var series = viewModel.SeriesCards.Single(card => card.SeriesId == point.SeriesId);
        var originalCount = series.Count;
        var countNotifications = WatchCount(series);
        var expectedPixelX = point.PixelX + 7.5;
        var expectedPixelY = point.PixelY - 4.25;

        viewModel.MovePoint(point.PointId, expectedPixelX, expectedPixelY);

        Assert.AreEqual(originalCount, series.Count);
        Assert.AreEqual(expectedPixelX, viewModel.SelectedTab.Points.Single(candidate => candidate.PointId == point.PointId).PixelX);
        Assert.AreEqual(expectedPixelY, viewModel.SelectedTab.Points.Single(candidate => candidate.PointId == point.PointId).PixelY);
        Assert.IsGreaterThanOrEqualTo(1, countNotifications());
    }

    [TestMethod]
    public void ReassignmentUpdatesBothSeriesCountsImmediately()
    {
        var viewModel = CreateWorkspace();
        var source = viewModel.SeriesCards[0];
        var target = viewModel.SeriesCards[1];
        var point = viewModel.SelectedTab!.Points.First(candidate => candidate.SeriesId == source.SeriesId);
        var sourceCount = source.Count;
        var targetCount = target.Count;
        var sourceNotifications = WatchCount(source);
        var targetNotifications = WatchCount(target);

        viewModel.ReassignPoint(point.PointId, target.SeriesId);

        Assert.AreEqual(sourceCount - 1, source.Count);
        Assert.AreEqual(targetCount + 1, target.Count);
        Assert.AreEqual(
            target.SeriesId,
            viewModel.SelectedTab.Points.Single(candidate => candidate.PointId == point.PointId).SeriesId);
        Assert.IsGreaterThanOrEqualTo(1, sourceNotifications());
        Assert.IsGreaterThanOrEqualTo(1, targetNotifications());
    }

    [TestMethod]
    public void ReassignmentCommandAcceptsSeriesCardTargetForSelectedPoint()
    {
        var viewModel = CreateWorkspace();
        var selectedPoint = viewModel.SelectedTab!.Points[0];
        var target = viewModel.SeriesCards.Single(card => card.SeriesId != selectedPoint.SeriesId);
        viewModel.SelectedPointId = selectedPoint.PointId;

        Assert.IsTrue(viewModel.ReassignPointCommand.CanExecute(target.SeriesId));

        viewModel.ReassignPointCommand.Execute(target.SeriesId);

        Assert.AreEqual(target.SeriesId, selectedPoint.SeriesId);
        Assert.AreEqual(selectedPoint.PointId, viewModel.SelectedPointId);
    }

    [TestMethod]
    public void MergePreservesPointsAndUpdatesTargetCount()
    {
        var viewModel = CreateWorkspace();
        var source = viewModel.SeriesCards[0];
        var target = viewModel.SeriesCards[1];
        var expectedTargetCount = source.Count + target.Count;
        var totalPoints = viewModel.SelectedTab!.Points.Count;
        var targetNotifications = WatchCount(target);

        viewModel.MergeSeries(source.SeriesId, target.SeriesId);

        Assert.AreEqual(totalPoints, viewModel.SelectedTab.Points.Count);
        Assert.IsFalse(viewModel.SelectedTab.Points.Any(point => point.SeriesId == source.SeriesId));
        Assert.AreEqual(expectedTargetCount, target.Count);
        Assert.IsGreaterThanOrEqualTo(1, targetNotifications());
        Assert.IsFalse(viewModel.SeriesCards.Any(card => card.SeriesId == source.SeriesId && card.Count > 0));
    }

    [TestMethod]
    public void SplitCreatesNonColorSeriesAndPreservesTotalCount()
    {
        var viewModel = CreateWorkspace();
        var source = viewModel.SeriesCards.First(card => card.Count >= 2);
        var pointToSplit = viewModel.SelectedTab!.Points.First(point => point.SeriesId == source.SeriesId);
        var existingSeriesIds = viewModel.SeriesCards.Select(card => card.SeriesId).ToHashSet(StringComparer.Ordinal);
        var totalPoints = viewModel.SelectedTab.Points.Count;
        var originalSourceCount = source.Count;
        var sourceNotifications = WatchCount(source);

        viewModel.SplitSeries(source.SeriesId, new[] { pointToSplit.PointId });

        Assert.AreEqual(totalPoints, viewModel.SelectedTab.Points.Count);
        Assert.AreEqual(originalSourceCount - 1, source.Count);
        var createdSeries = viewModel.SeriesCards.Single(card => !existingSeriesIds.Contains(card.SeriesId));
        Assert.AreEqual(1, createdSeries.Count);
        Assert.AreEqual(
            createdSeries.SeriesId,
            viewModel.SelectedTab.Points.Single(point => point.PointId == pointToSplit.PointId).SeriesId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(createdSeries.Symbol));
        Assert.IsFalse(string.IsNullOrWhiteSpace(createdSeries.AccessibleName));
        Assert.IsGreaterThanOrEqualTo(1, sourceNotifications());
    }

    [TestMethod]
    public void WorkflowAndEditCommandsAreDiscoverableAndOverlayCommandActsImmediately()
    {
        var viewModel = CreateWorkspace();
        var commands = new[]
        {
            viewModel.ImportCommand,
            viewModel.EnhanceCommand,
            viewModel.AutoDetectCommand,
            viewModel.ReviewCommand,
            viewModel.ExportCommand,
            viewModel.CancelCommand,
            viewModel.AddPointCommand,
            viewModel.DeletePointCommand,
            viewModel.MovePointCommand,
            viewModel.MergeSeriesCommand,
            viewModel.SplitSeriesCommand,
            viewModel.ReassignPointCommand,
            viewModel.TogglePhaseOverlayCommand,
            viewModel.ZoomInCommand,
            viewModel.ZoomOutCommand,
            viewModel.FitZoomCommand,
        };

        Assert.IsTrue(commands.All(command => command is not null));
        Assert.IsTrue(viewModel.ImportCommand.CanExecute(null));
        var original = viewModel.IsPhaseOverlayVisible;

        viewModel.TogglePhaseOverlayCommand.Execute(null);

        Assert.AreEqual(!original, viewModel.IsPhaseOverlayVisible);
    }

    [TestMethod]
    public void SeriesSelectionZoomAndTabSwitchClearStaleInspectorState()
    {
        var viewModel = CreateWorkspace();
        SeriesCardViewModel target = viewModel.SeriesCards[1];
        viewModel.SelectedSeriesId = viewModel.SeriesCards[0].SeriesId;

        target.SelectCommand.Execute(null);

        Assert.AreEqual(target.SeriesId, viewModel.SelectedSeriesId);
        double initialZoom = viewModel.SelectedTab!.ZoomLevel;
        viewModel.ZoomInCommand.Execute(null);
        Assert.IsGreaterThan(initialZoom, viewModel.SelectedTab.ZoomLevel);
        viewModel.ZoomOutCommand.Execute(null);
        viewModel.FitZoomCommand.Execute(null);
        Assert.AreEqual(1, viewModel.SelectedTab.ZoomLevel);

        viewModel.Magnifier.GraphPosition = new System.Windows.Point(12, 34);
        viewModel.Magnifier.NearestDetectionName = "stale";
        viewModel.Magnifier.NearestDetectionConfidence = 0.9;
        viewModel.SelectedTab = null;

        Assert.IsNull(viewModel.Magnifier.GraphPosition);
        Assert.IsNull(viewModel.Magnifier.NearestDetectionName);
        Assert.IsNull(viewModel.Magnifier.NearestDetectionConfidence);
        Assert.IsFalse(viewModel.Magnifier.IsCrosshairVisible);
    }

    private static MainWindowViewModel CreateWorkspace()
    {
        var viewModel = new MainWindowViewModel(new FakeWorkspaceService());
        Assert.IsNotNull(viewModel.SelectedTab);
        Assert.IsGreaterThanOrEqualTo(2, viewModel.SeriesCards.Count);
        Assert.IsGreaterThanOrEqualTo(2, viewModel.SelectedTab.Points.Count);
        return viewModel;
    }

    private static void WriteImage(string path, int width, int height)
    {
        byte[] pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Func<int> WatchCount(SeriesCardViewModel series)
    {
        var count = 0;
        series.PropertyChanged += OnPropertyChanged;
        return () => count;

        void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            _ = sender;
            if (args.PropertyName is nameof(SeriesCardViewModel.Count) or null or "")
            {
                count++;
            }
        }
    }

    private static string[] Snapshot(MainWindowViewModel viewModel)
    {
        return viewModel.Tabs
            .SelectMany(tab => tab.Points.Select(point =>
                $"{tab.TabId}|{tab.DisplayName}|{point.PointId}|{point.SeriesId}|{point.PixelX:R}|{point.PixelY:R}|{point.GraphX:R}|{point.GraphY:R}|{point.PhaseCode}"))
            .Concat(viewModel.SeriesCards.Select(card =>
                $"{card.SeriesId}|{card.Symbol}|{card.AccessibleName}|{card.Label}|{card.Count}|{card.Confidence:R}|{card.IsVisible}"))
            .ToArray();
    }
}
