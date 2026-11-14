// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;
using GraphReader.Domain;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class MarkerExemplarViewModelTests
{
    [TestMethod]
    public void FindOnlyPreviewsAndUserAddsCandidateToSelectedSeries() => WithImage(path =>
    {
        var matcher = new MatcherStub
        {
            Candidates = [
                new("candidate-1", new(105, 61), .95),
                new("candidate-2", new(120, 70), .93),
            ],
        };
        using var workspace = new ManualPreviewWorkspaceService(markerExemplarSuggestionService: matcher);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        WorkspaceTabViewModel tab = workspace.CreateWorkspace().Single();
        workspace.Calibrate(tab.TabId, Calibration());
        var series = workspace.AddSeries(tab.TabId,
            new("Intervention", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        GraphReader.App.Models.GraphPoint seed = workspace.AddPoint(tab.TabId, series.SeriesId, 80, 55);
        using var vm = new MainWindowViewModel(workspace);
        vm.SelectedSeriesId = series.SeriesId;
        vm.SelectedPointId = seed.PointId;
        int pointsBeforeFind = tab.Points.Count;

        vm.BeginAddPointCommand.Execute(null);
        Assert.AreEqual(ManualEditorMode.AddPoint, vm.EditorMode);
        Assert.IsTrue(vm.FindMarkerExemplarsCommand.CanExecute(null));
        Pump(vm.FindMarkerExemplarsAsync(CancellationToken.None));
        Assert.AreEqual(ManualEditorMode.Select, vm.EditorMode);

        Assert.AreEqual(pointsBeforeFind, tab.Points.Count, "Searching only displays transient proposals.");
        Assert.HasCount(2, vm.MarkerExemplarCandidates);
        Assert.AreEqual(17, matcher.LastRequest!.Exemplar.Width);
        Assert.AreEqual(17, matcher.LastRequest.Exemplar.Height);
        Assert.AreEqual(71, matcher.LastRequest.Exemplar.X);
        Assert.AreEqual(46, matcher.LastRequest.Exemplar.Y);
        Assert.AreEqual(seed.PointId, vm.SelectedPointId);
        Assert.AreEqual(series.SeriesId, vm.SelectedSeriesId);

        var row = vm.MarkerExemplarCandidates.Single(candidate => candidate.Candidate.CandidateId == "candidate-1");
        Assert.IsTrue(vm.AcceptMarkerExemplarCandidateCommand.CanExecute(row));
        vm.AcceptMarkerExemplarCandidateCommand.Execute(row);

        Assert.AreEqual(pointsBeforeFind + 1, tab.Points.Count);
        Assert.IsTrue(tab.Points.Any(point => point.SeriesId == series.SeriesId &&
            point.PixelX == 105 && point.PixelY == 61));
        Assert.AreEqual(seed.PointId, vm.SelectedPointId, "Adding a match keeps the exemplar seed selected.");
        Assert.AreEqual(series.SeriesId, vm.SelectedSeriesId);
        Assert.HasCount(1, vm.MarkerExemplarCandidates, "Accepting one candidate preserves the other proposal for review.");
        vm.UndoCommand.Execute(null);
        Assert.AreEqual(pointsBeforeFind, tab.Points.Count, "The accepted point is an ordinary undoable manual edit.");
        vm.RedoCommand.Execute(null);
        Assert.AreEqual(pointsBeforeFind + 1, tab.Points.Count);
    });

    [TestMethod]
    public void DuplicateCandidateCannotBeAddedAndChangingSeedClearsPreview() => WithImage(path =>
    {
        var matcher = new MatcherStub { Candidates = [new("candidate-1", new(105, 61), .95)] };
        using var workspace = new ManualPreviewWorkspaceService(markerExemplarSuggestionService: matcher);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        WorkspaceTabViewModel tab = workspace.CreateWorkspace().Single();
        workspace.Calibrate(tab.TabId, Calibration());
        var series = workspace.AddSeries(tab.TabId,
            new("Intervention", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        GraphReader.App.Models.GraphPoint seed = workspace.AddPoint(tab.TabId, series.SeriesId, 80, 55);
        workspace.AddPoint(tab.TabId, series.SeriesId, 105, 61);
        using var vm = new MainWindowViewModel(workspace);
        vm.SelectedSeriesId = series.SeriesId;
        vm.SelectedPointId = seed.PointId;
        Pump(vm.FindMarkerExemplarsAsync(CancellationToken.None));

        var row = vm.MarkerExemplarCandidates.Single();
        Assert.IsFalse(vm.AcceptMarkerExemplarCandidateCommand.CanExecute(row));
        vm.SelectedPointId = tab.Points.Last().PointId;
        Assert.AreEqual(0, vm.MarkerExemplarCandidates.Count);
        Assert.IsFalse(vm.HasMarkerExemplarCandidates, "A seed change discards the old binding and its proposals.");
    });

    private static ManualCalibrationRequest Calibration() => new(
        new(10, 100), new(10, 10), new(150, 100), 10, 24);

    private sealed class MatcherStub : IMarkerExemplarSuggestionService
    {
        public MarkerExemplarRequest? LastRequest { get; private set; }
        public IReadOnlyList<MarkerExemplarCandidate> Candidates { get; init; } =
            [new("candidate-1", new(105, 61), .95)];

        public Task<MarkerExemplarResponse> FindAsync(MarkerExemplarRequest request,
            ReadOnlyMemory<byte> encodedOriginalPanel, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            var timings = new MarkerExemplarTimings(1, 1, 2);
            var budget = new CalibrationOcrCpuBudget(2, 2500, .5, true) { PriorityClass = "BelowNormal" };
            var runtime = new MarkerExemplarRuntime(budget, 1,
                "original-panel-user-exemplar-single-scale-v1");
            return Task.FromResult(new MarkerExemplarResponse(request, Candidates, timings, runtime, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void WithImage(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "marker-exemplar-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            StaTestHost.Run(() =>
            {
                string path = Path.Combine(directory, "owned-test.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 120, 96, 96,
                    PixelFormats.Gray8, null, Enumerable.Repeat((byte)255, 160 * 120).ToArray(), 160)));
                using (FileStream stream = File.Create(path)) encoder.Save(stream);
                action(path);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
