// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CloseTabProjectIntegrityTests
{
    [TestMethod]
    public void ClosingLastTabForSourceRemovesOrphanSourceAndProductionImportSucceeds() =>
        StaTestHost.Run(() =>
        {
            using var directory = new TemporaryDirectory();
            string firstImage = WriteImage(directory.Path, "first.png", 10);
            string secondImage = WriteImage(directory.Path, "second.png", 20);
            using var workspace = new ImportRequestWorkspace();

            WorkspaceTabViewModel[] imported = workspace
                .ImportImagesAsync([firstImage, secondImage], CancellationToken.None)
                .GetAwaiter().GetResult().ToArray();
            Assert.AreEqual(2, imported.Length);
            Assert.AreEqual(2, workspace.CurrentProject.Sources.Count);
            Assert.AreEqual(2, workspace.CurrentProject.Panels.Count);

            Assert.IsTrue(workspace.CloseTab(imported[0].TabId));
            workspace.AddSeries(imported[1].TabId,
                new("Series", "?", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));

            Assert.AreEqual(1, workspace.CreateWorkspace().Count);
            Assert.AreEqual(1, workspace.CurrentProject.Panels.Count,
                "The next workspace synchronization drops panels for closed tabs.");
            Assert.AreEqual(1, workspace.CurrentProject.Sources.Count,
                "A source with no remaining panels must be removed with its last tab.");

            WorkflowImportRequest request = workspace.CreateProductionImportRequest();
            Assert.AreEqual(1, request.Sources.Count);
            Assert.AreEqual(Guid.Parse(imported[1].SourceId!), request.Sources.Single().SourceId);

            string projectPath = Path.Combine(directory.Path, "closed-tab.garproj");
            var saved = workspace.SaveProjectAsync(projectPath, CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsTrue(saved.IsSuccess, "The closed-tab project should remain valid for persistence.");
            using var reopened = new ImportRequestWorkspace();
            WorkspaceTabViewModel[] reopenedTabs = reopened
                .OpenProjectAsync(projectPath, CancellationToken.None)
                .GetAwaiter().GetResult().ToArray();
            Assert.AreEqual(1, reopenedTabs.Length);
            Assert.AreEqual(1, reopened.CurrentProject.Sources.Count);
            Assert.AreEqual(1, reopened.CurrentProject.Panels.Count);
            Assert.AreEqual(1, reopened.CreateProductionImportRequest().Sources.Count);
        });

    [TestMethod]
    public void ClosingRasterCropKeepsSharedSourceWhileParentPanelRemains() =>
        StaTestHost.Run(() =>
        {
            using var directory = new TemporaryDirectory();
            string image = WriteImage(directory.Path, "shared-source.png", 10);
            using var workspace = new ImportRequestWorkspace();

            WorkspaceTabViewModel parent = workspace
                .ImportImagesAsync([image], CancellationToken.None)
                .GetAwaiter().GetResult().Single();
            string sourceId = parent.SourceId!;
            PanelCropContext context = workspace.GetPanelCropContext(parent.TabId)!;
            WorkspaceTabViewModel crop = Pump(workspace.CreatePanelCropAsync(
                context,
                new MarkerExemplarRectangle(10, 10, 60, 60),
                CancellationToken.None));
            Assert.AreEqual(1, workspace.CurrentProject.Sources.Count);
            Assert.AreEqual(2, workspace.CurrentProject.Panels.Count);

            Assert.IsTrue(workspace.CloseTab(crop.TabId));

            Assert.AreEqual(1, workspace.CurrentProject.Sources.Count,
                "Closing one panel must not remove a source still used by the parent panel.");
            Assert.AreEqual(sourceId, workspace.CurrentProject.Sources.Single().SourceId.Value.ToString("D"));
            Assert.AreEqual(1, workspace.CurrentProject.Panels.Count);
            WorkflowImportRequest request = workspace.CreateProductionImportRequest();
            Assert.AreEqual(1, request.Sources.Count);
            Assert.AreEqual(Guid.Parse(sourceId), request.Sources.Single().SourceId);
        });

    [TestMethod]
    public void ViewModelUndoAndRedoRestoreDeletedPointCorrectionState() =>
        StaTestHost.Run(() =>
        {
            using var directory = new TemporaryDirectory();
            string image = WriteImage(directory.Path, "undo-delete.png", 10);
            using var workspace = new ImportRequestWorkspace();
            WorkspaceTabViewModel tab = workspace
                .ImportImagesAsync([image], CancellationToken.None)
                .GetAwaiter().GetResult().Single();
            SeriesCardViewModel series = workspace.AddSeries(tab.TabId,
                new("Series", "?", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
            var point = workspace.AddPoint(tab.TabId, series.SeriesId, 45, 35);

            using var viewModel = new MainWindowViewModel(workspace);
            viewModel.SelectedTab = viewModel.Tabs.Single();
            viewModel.DeletePoint(point.PointId);
            Assert.AreEqual(1, workspace.CapturePointCorrectionState(tab.TabId).DeletedPoints.Count);
            Assert.IsFalse(tab.Points.Any(candidate => candidate.PointId == point.PointId));

            viewModel.UndoCommand.Execute(null);

            Assert.AreEqual(0, workspace.CapturePointCorrectionState(tab.TabId).DeletedPoints.Count);
            Assert.IsTrue(tab.Points.Any(candidate => candidate.PointId == point.PointId));

            viewModel.RedoCommand.Execute(null);

            Assert.AreEqual(1, workspace.CapturePointCorrectionState(tab.TabId).DeletedPoints.Count);
            Assert.IsFalse(tab.Points.Any(candidate => candidate.PointId == point.PointId));
        });

    private static string WriteImage(string directory, string name, byte shade)
    {
        string path = Path.Combine(directory, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(
            160, 120, 96, 96, PixelFormats.Gray8, null,
            Enumerable.Repeat(shade, 160 * 120).ToArray(), 160)));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static T Pump<T>(Task<T> task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(
            _ => System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) System.Windows.Threading.Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private sealed class ImportRequestWorkspace : ManualPreviewWorkspaceService
    {
        public WorkflowImportRequest CreateProductionImportRequest() =>
            CreateProductionWorkflowImportRequest(enhancementEnabled: false);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "graph-reader-close-tab-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
