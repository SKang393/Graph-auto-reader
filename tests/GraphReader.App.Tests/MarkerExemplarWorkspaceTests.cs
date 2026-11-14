// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Services;
using GraphReader.Domain;
using GraphReader.Imaging;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class MarkerExemplarWorkspaceTests
{
    [TestMethod]
    public void OriginalBytesAndBindingGuardSeedCalibrationAndSeries() => StaTestHost.Run(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "marker-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "fixture.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 120, 96, 96, PixelFormats.Gray8, null,
                Enumerable.Repeat((byte)255, 160 * 120).ToArray(), 160)));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var worker = new FakeWorker();
            using var workspace = new ManualPreviewWorkspaceService(markerExemplarSuggestionService: worker);
            var tab = Pump(workspace.ImportImagesAsync([path], CancellationToken.None)).Single();
            var series = workspace.AddSeries(tab.TabId, new("Series", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
            Assert.IsNull(Pump(workspace.FindMarkerExamplesAsync(tab.TabId, "missing", series.SeriesId, 13, CancellationToken.None)));
            var point = workspace.AddPoint(tab.TabId, series.SeriesId, 50, 50);
            Assert.IsNull(Pump(workspace.FindMarkerExamplesAsync(tab.TabId, point.PointId, series.SeriesId, 13, CancellationToken.None)));
            workspace.Calibrate(tab.TabId, new(new(10, 100), new(12, 10), new(150, 101), 100, 20));
            int count = tab.Points.Count;
            Assert.IsNull(Pump(workspace.FindMarkerExamplesAsync(tab.TabId, point.PointId, series.SeriesId, 4, CancellationToken.None)));
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert.ThrowsExactly<OperationCanceledException>(() =>
                    Pump(workspace.FindMarkerExamplesAsync(tab.TabId, point.PointId, series.SeriesId, 13, cancellation.Token)));
            }
            WorkspaceMarkerExemplarSuggestion result = Pump(workspace.FindMarkerExamplesAsync(tab.TabId, point.PointId, series.SeriesId, 13, CancellationToken.None))!;
            Assert.IsNotNull(result);
            CollectionAssert.AreEqual(File.ReadAllBytes(path), worker.Image!);
            Assert.AreEqual(new MarkerExemplarRectangle(43, 43, 13, 13), result.Request.Exemplar);
            Assert.AreEqual(count, tab.Points.Count);
            Assert.IsTrue(workspace.IsMarkerExemplarSuggestionCurrent(result));
            var originals = (Dictionary<string, ImmutableImageBytes>)typeof(ManualPreviewWorkspaceService)
                .GetField("_originalPanelBytesByTab", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(workspace)!;
            originals[tab.TabId] = new ImmutableImageBytes(File.ReadAllBytes(path));
            Assert.IsFalse(workspace.IsMarkerExemplarSuggestionCurrent(result), "Replacing the original object invalidates even byte-identical evidence.");
            result = Pump(workspace.FindMarkerExamplesAsync(tab.TabId, point.PointId, series.SeriesId, 13, CancellationToken.None))!;
            workspace.MovePoint(tab.TabId, point.PointId, 51, 50);
            Assert.IsFalse(workspace.IsMarkerExemplarSuggestionCurrent(result));
            result = Pump(workspace.FindMarkerExamplesAsync(tab.TabId, point.PointId, series.SeriesId, 13, CancellationToken.None))!;
            var other = workspace.AddSeries(tab.TabId, new("Other", "■", MarkerShape.Square, MarkerFill.Filled, SemanticRole.Intervention));
            workspace.ReassignPoint(tab.TabId, point.PointId, other.SeriesId);
            Assert.IsFalse(workspace.IsMarkerExemplarSuggestionCurrent(result));
            result = Pump(workspace.FindMarkerExamplesAsync(tab.TabId, point.PointId, other.SeriesId, 13, CancellationToken.None))!;
            workspace.Calibrate(tab.TabId, new(new(11, 100), new(11, 10), new(150, 100), 100, 20));
            Assert.IsFalse(workspace.IsMarkerExemplarSuggestionCurrent(result));
            worker.OnFind = () => workspace.MovePoint(tab.TabId, point.PointId, 52, 50);
            Assert.IsNull(Pump(workspace.FindMarkerExamplesAsync(tab.TabId, point.PointId, other.SeriesId, 13, CancellationToken.None)));
            Assert.AreEqual(count, tab.Points.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private sealed class FakeWorker : IMarkerExemplarSuggestionService
    {
        public byte[]? Image { get; private set; }
        public Action? OnFind { get; set; }
        public Task<MarkerExemplarResponse> FindAsync(MarkerExemplarRequest request, ReadOnlyMemory<byte> image, CancellationToken token)
        {
            Image = image.ToArray();
            OnFind?.Invoke();
            return Task.FromResult(MarkerExemplarResponse.Failure(request, MarkerExemplarReasonCodes.UninformativeExemplar));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }
}
