// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Localization;
using GraphReader.App.Models;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using GraphReader.Export;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ExportSummaryIsolationTests
{
    private static readonly string[] WarningFromA = ["warning from A"];
    private static readonly string[] WarningsFromB = ["warning B1", "warning B2"];

    [TestMethod]
    public void CompletedExportSummaryUsesExportedTabSnapshotAfterSelectionChanges()
    {
        string directory = Path.Combine(Path.GetTempPath(), "export-summary-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            StaTestHost.Run(() =>
            {
                string sourceA = Path.Combine(directory, "graph-A.png");
                string sourceB = Path.Combine(directory, "graph-B.png");
                WriteImage(sourceA, 180, 140, 255);
                WriteImage(sourceB, 180, 140, 128);
                string output = Path.Combine(directory, "exported");
                Directory.CreateDirectory(output);

                var delayedExport = new DelayedExportService();
                using var workspace = new ManualPreviewWorkspaceService(exportService: delayedExport);
                Pump(workspace.ImportImagesAsync([sourceA, sourceB], CancellationToken.None));
                WorkspaceTabViewModel[] tabs = workspace.CreateWorkspace().ToArray();
                WorkspaceTabViewModel tabA = tabs[0];
                WorkspaceTabViewModel tabB = tabs[1];
                PrepareTab(workspace, tabA, "Series A", [(45, 80)]);
                PrepareTab(workspace, tabB, "Series B1", [(35, 65), (55, 55)]);
                workspace.AddSeries(tabB.TabId,
                    new("Series B2", "■", MarkerShape.Square, MarkerFill.Open, SemanticRole.Intervention));
                workspace.AddPoint(tabB.TabId, tabB.SeriesCards[1].SeriesId, 70, 70);

                using var viewModel = new MainWindowViewModel(
                    workspace,
                    localizationService: Localizer(),
                    dialogService: new ExportDialog(output),
                    pipelineWarningsProvider: tab => tab.TabId == tabA.TabId
                        ? [new("Review.PipelineWarning.Interpretation", "warning from A")]
                        : [new("Review.PipelineWarning.Interpretation", "warning B1"),
                           new("Review.PipelineWarning.Interpretation", "warning B2")]);
                viewModel.OpenExportPreviewCommand.Execute(null);
                viewModel.ExportSummary!.WarningsAcknowledged = true;
                viewModel.SelectedTab = tabB;
                Assert.IsFalse(viewModel.ExportSummary!.WarningsAcknowledged,
                    "Acknowledging A's warning must not acknowledge B's different warning set.");
                CollectionAssert.AreEquivalent(WarningsFromB, viewModel.ExportSummary.WarningIssues
                    .Select(issue => issue.TechnicalMessage).ToArray());
                viewModel.SelectedTab = tabA;

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool observedBusy = false;
                viewModel.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName != nameof(MainWindowViewModel.IsBusy)) return;
                    if (viewModel.IsBusy) observedBusy = true;
                    else if (observedBusy && viewModel.ExportSummary is not null) completion.TrySetResult();
                };

                viewModel.ExportCommand.Execute(null);
                ExportRequest request = Pump(delayedExport.Started.Task);
                Assert.AreEqual(Guid.Parse(tabA.PanelId!), request.PanelId, "The export request must remain bound to A.");
                Assert.AreEqual(1, viewModel.ReviewIssues.Count(issue => issue.Severity == ReviewIssueSeverity.Warning));

                viewModel.SelectedTab = tabB;
                Assert.AreSame(tabB, viewModel.SelectedTab);
                CollectionAssert.AreEquivalent(
                    WarningsFromB,
                    viewModel.ReviewIssues.Where(issue => issue.Severity == ReviewIssueSeverity.Warning)
                        .Select(issue => issue.TechnicalMessage).ToArray());
                delayedExport.AllowComplete.TrySetResult();
                Pump(completion.Task);

                ExportSummaryViewModel completed = viewModel.ExportSummary!;
                Assert.AreEqual(tabA.Points.Count, completed.PointCount);
                Assert.AreEqual(tabA.SeriesCards.Count, completed.SeriesCount);
                StringAssert.Contains(completed.ProvenanceSummary, Path.GetFileName(sourceA));
                CollectionAssert.AreEqual(WarningFromA, completed.WarningIssues
                    .Select(issue => issue.TechnicalMessage).ToArray());
                Assert.IsNotNull(delayedExport.Result);
                CollectionAssert.AreEquivalent(
                    delayedExport.Result.MinimalArtifacts.Select(static artifact => artifact.FileName)
                        .Concat(delayedExport.Result.AuditArtifacts.Select(static artifact => artifact.FileName)).ToArray(),
                    completed.OutputFileNames.ToArray());

                // Selection changes refresh the projection while the written
                // summary is visible. The receipt must remain bound to A.
                viewModel.SelectedTab = tabA;
                viewModel.SelectedTab = tabB;
                Assert.AreSame(completed, viewModel.ExportSummary);
                Assert.AreEqual(tabA.Points.Count, viewModel.ExportSummary!.PointCount);
                Assert.AreEqual(tabA.SeriesCards.Count, viewModel.ExportSummary.SeriesCount);
                StringAssert.Contains(viewModel.ExportSummary.ProvenanceSummary, Path.GetFileName(sourceA));
                CollectionAssert.AreEqual(WarningFromA, viewModel.ExportSummary.WarningIssues
                    .Select(issue => issue.TechnicalMessage).ToArray());
                Assert.IsFalse(viewModel.ConfirmExportCommand.CanExecute(null),
                    "A completed receipt must not authorize exporting the currently selected tab.");

                viewModel.OpenExportPreviewCommand.Execute(null);
                ExportSummaryViewModel currentPreview = viewModel.ExportSummary!;
                Assert.AreEqual(tabB.Points.Count, currentPreview.PointCount);
                Assert.AreEqual(tabB.SeriesCards.Count, currentPreview.SeriesCount);
                StringAssert.Contains(currentPreview.ProvenanceSummary, Path.GetFileName(sourceB));
                CollectionAssert.AreEquivalent(WarningsFromB, currentPreview.WarningIssues
                    .Select(issue => issue.TechnicalMessage).ToArray());
                Assert.IsTrue(currentPreview.IsDestinationPending, "Opening B's ordinary preview must not reuse A's written destination.");
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void PrepareTab(ManualPreviewWorkspaceService workspace, WorkspaceTabViewModel tab,
        string seriesName, IReadOnlyList<(double X, double Y)> points)
    {
        workspace.Calibrate(tab.TabId, new(new(10, 120), new(10, 10), new(170, 120), 10, 24, 1));
        SeriesCardViewModel series = workspace.AddSeries(tab.TabId,
            new(seriesName, "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        foreach ((double x, double y) in points)
            workspace.AddPoint(tab.TabId, series.SeriesId, x, y);
    }

    private static LocalizationService Localizer()
    {
        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        return new(new ResourceDictionary());
    }

    private static void WriteImage(string path, int width, int height, byte value)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Gray8, null, Enumerable.Repeat(value, checked(width * height)).ToArray(), width)));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static T Pump<T>(Task<T> task)
    {
        Pump((Task)task);
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

    private sealed class DelayedExportService : IExportService
    {
        private readonly ExportService _inner = new();
        public TaskCompletionSource<ExportRequest> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExportResult? Result { get; private set; }

        public async Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken)
        {
            Started.SetResult(request);
            await AllowComplete.Task.WaitAsync(cancellationToken);
            Result = await _inner.ExportAsync(request, cancellationToken);
            return Result;
        }
    }

    private sealed class ExportDialog(string outputDirectory) : IWorkspaceDialogService
    {
        public IReadOnlyList<string> SelectImages() => [];
        public string? SelectProjectToOpen() => null;
        public string? SelectProjectToSave(string? currentPath) => null;
        public string? SelectExportDirectory() => outputDirectory;
    }
}
