// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App;
using GraphReader.App.Localization;
using GraphReader.App.Models;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using GraphReader.Export;
using GraphReader.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PixelPoint = GraphReader.Domain.PixelPoint;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class ManualPreviewWpfCompositionTests
{
    /// <summary>
    /// Test-only generated images exercise WPF composition plumbing. They are not the
    /// mandatory maintainer graph and do not provide graph-reading accuracy evidence.
    /// </summary>
    [TestMethod]
    public void GeneratedImagesComposeTheCompleteManualWorkflowWithoutFakeGraphData() =>
        RunOnDispatcher(VerifyCompositionAsync);

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ClosingAfterUndoOfSavedPointHonorsReviewerChoice(bool saveOnClose) =>
        RunOnDispatcher(() => VerifyCloseAfterUndoAsync(saveOnClose));

    private static void RunOnDispatcher(Func<Task> verify)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(dispatcher));
                Task scenario = verify();
                _ = scenario.ContinueWith(
                    _ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
                Dispatcher.Run();
                scenario.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "ManualPreviewWpfCompositionTests.STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(
            thread.Join(TimeSpan.FromSeconds(30)),
            "The bounded STA WPF composition scenario timed out after 30 seconds.");
        if (failure is not null)
        {
            throw new AssertFailedException($"The STA WPF composition scenario failed: {failure}", failure);
        }
    }

    private static async Task VerifyCloseAfterUndoAsync(bool saveOnClose)
    {
        var token = CancellationToken.None;
        using var environment = new IntegrationSmokeTestEnvironment();
        string portable = environment.PathFor("portable");
        Directory.CreateDirectory(portable);
        File.WriteAllText(Path.Combine(portable, "portable.mode"), string.Empty);
        var paths = ApplicationPaths.Create(portable, environment.PathFor("LocalAppData"));
        using var workspace = new ClosingWorkspace(paths);
        string image = environment.PathFor("graph.png");
        WriteImage(image, 100, 100, 255, 255, 255);
        var tab = (await workspace.ImportImagesAsync([image], token)).Single();
        workspace.Calibrate(tab.TabId, new(new(10, 90), new(10, 10), new(90, 90), 100, 9));
        var series = workspace.AddSeries(tab.TabId, new("Response", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        var point = workspace.AddPoint(tab.TabId, series.SeriesId, 20, 74);
        string primary = environment.PathFor("review.garproj");

        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        var resources = new ResourceDictionary();
        foreach (string name in new[] { "DesignTokens", "LightTheme", "Controls" })
            resources.MergedDictionaries.Add(new() { Source = new Uri($"/GraphReader.App;component/Themes/{name}.xaml", UriKind.Relative) });
        var dialogs = new DeterministicWorkspaceDialogService
        {
            CloseAction = ProjectCloseAction.Save,
            ProjectSaveDestinations = [null, portable, primary],
        };
        using var vm = new MainWindowViewModel(workspace, new LocalizationService(resources), dialogService: dialogs);
        var window = new MainWindow(null, resources);
        ((IDisposable)window.DataContext).Dispose();
        window.DataContext = vm;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            window.Show();
            vm.SelectedPointId = point.PointId;
            vm.BeginMovePointCommand.Execute(null);
            await vm.HandleCanvasPointAsync(new(50, 42));
            Assert.IsTrue(tab.IsDirty);
            // Cancel the first Save As, then select an existing directory as an invalid file.
            window.Close();
            Assert.IsFalse(closed.Task.IsCompleted);
            Assert.IsTrue(tab.IsDirty);
            await RunSave(window.Close);
            Assert.IsFalse(closed.Task.IsCompleted);
            Assert.IsTrue(tab.IsDirty);
            Assert.IsNotNull(vm.LastOperationStatus);
            Assert.IsNull(workspace.CurrentProjectPath);
            await RunSave(() => vm.SaveProjectCommand.Execute(null));
            dialogs.CloseAction = ProjectCloseAction.Cancel;
            Assert.IsFalse(tab.IsDirty);
            Assert.AreEqual(new PixelPoint(50, 42), await ReopenPoint(primary));
            string savedHash = Sha256(primary);

            vm.UndoCommand.Execute(null);
            var snapshot = await workspace.LastAutosave!;
            Assert.IsTrue(snapshot.IsSuccess, string.Join(" | ", snapshot.Errors));
            string recovery = environment.PathFor("recovered.garproj");
            Assert.IsTrue((await workspace.RecoverLatestToNewFileAsync(recovery, token)).IsSuccess);
            Assert.AreEqual(new PixelPoint(20, 74), await ReopenPoint(recovery));

            // Cancel must leave the edited point in the live window and the explicit save intact.
            window.Close();
            Assert.IsFalse(closed.Task.IsCompleted,
                "Close bypassed the decision. Primary is (50,42); the undo at (20,74) is only in recovery.");
            Assert.AreEqual(3, dialogs.ClosePrompts);
            Assert.IsTrue(tab.IsDirty);
            Assert.AreEqual(20d, tab.Points.Single().PixelX);
            Assert.AreEqual(savedHash, Sha256(primary));

            vm.RedoCommand.Execute(null);
            Assert.IsTrue((await workspace.LastAutosave!).IsSuccess);
            Assert.IsFalse(tab.IsDirty, "Redo returned to the explicitly saved point.");
            vm.UndoCommand.Execute(null);
            Assert.IsTrue((await workspace.LastAutosave!).IsSuccess);
            dialogs.CloseAction = saveOnClose ? ProjectCloseAction.Save : ProjectCloseAction.Discard;
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.AreEqual(saveOnClose ? new PixelPoint(20, 74) : new PixelPoint(50, 42),
                await ReopenPoint(primary));
            Assert.AreEqual(4, dialogs.ClosePrompts);
            if (!saveOnClose)
            {
                Assert.AreEqual(savedHash, Sha256(primary));
                Assert.AreEqual(new PixelPoint(20, 74), await ReopenPoint(snapshot.Value!.SnapshotPath));
            }
        }
        finally
        {
            if (!closed.Task.IsCompleted)
            {
                window.DataContext = null;
                window.Close();
            }
        }

        async Task RunSave(Action start)
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnBusyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(vm.IsBusy) && !vm.IsBusy) finished.TrySetResult();
            }
            vm.PropertyChanged += OnBusyChanged;
            try
            {
                start();
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
            finally
            {
                vm.PropertyChanged -= OnBusyChanged;
            }
        }

        async Task<PixelPoint> ReopenPoint(string path)
        {
            using var reopened = new ManualPreviewWorkspaceService(paths);
            await reopened.OpenProjectAsync(path, token);
            return reopened.CurrentProject.Panels.Single().Points.Single(p => p.PointId.Value.ToString("D") == point.PointId).OriginalPixel;
        }
    }

    private sealed class ClosingWorkspace(ApplicationPaths paths) : ManualPreviewWorkspaceService(paths), IManualWorkspaceService
    {
        public Task<DomainResult<ProjectSnapshotReceipt>>? LastAutosave { get; private set; }
        Task<DomainResult<ProjectSnapshotReceipt>> IManualWorkspaceService.AutosaveAsync(
            SnapshotTrigger trigger, string? tabId, string? entityId, CancellationToken cancellationToken) =>
            LastAutosave = base.AutosaveAsync(trigger, tabId, entityId, cancellationToken);
    }

    private static async Task VerifyCompositionAsync()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string imageDirectory = environment.PathFor("test-only-generated-images");
        Directory.CreateDirectory(imageDirectory);
        string firstImage = Path.Combine(imageDirectory, "generated-workflow-one.png");
        string secondImage = Path.Combine(imageDirectory, "generated-workflow-two.png");
        WriteImage(firstImage, width: 100, height: 100, blue: 240, green: 245, red: 250);
        WriteImage(secondImage, width: 100, height: 100, blue: 220, green: 235, red: 245);
        string firstHash = Sha256(firstImage);
        string secondHash = Sha256(secondImage);
        string projectPath = environment.PathFor("manual-composition.garproj");
        string recoveredProjectPath = environment.PathFor("manual-composition-command-recovered.garproj");
        string exportDirectory = environment.PathFor("csv-export");
        Directory.CreateDirectory(exportDirectory);
        string executableDirectory = environment.PathFor("test-only-portable-runtime");
        Directory.CreateDirectory(executableDirectory);
        File.WriteAllText(Path.Combine(executableDirectory, "portable.mode"), string.Empty);
        ApplicationPaths applicationPaths = ApplicationPaths.Create(
            executableDirectory,
            environment.PathFor("test-only-local-app-data"));

        var dialogs = new DeterministicWorkspaceDialogService
        {
            Images = [firstImage, secondImage],
            ProjectToOpen = projectPath,
            ProjectSaveDestinations = [projectPath, recoveredProjectPath],
            ExportDirectory = exportDirectory,
        };
        var workspace = new ManualPreviewWorkspaceService(applicationPaths);
        var localization = new DeterministicLocalizationService();
        using var viewModel = new MainWindowViewModel(
            workspace,
            localization,
            dialogService: dialogs);

        Assert.IsFalse(workspace.UsesFakeGraphData);
        Assert.HasCount(0, viewModel.Tabs);
        Assert.IsTrue(workspace.AutomaticStages.All(static stage =>
            stage.State == AutomaticStageState.Unavailable &&
            !string.IsNullOrWhiteSpace(stage.Explanation)));
        StringAssert.Contains(
            workspace.AutomaticStages.Single(static stage => stage.Stage == "axis").Explanation,
            "manual three-anchor calibration");
        Assert.IsFalse(viewModel.EnhanceCommand.CanExecute(null));
        Assert.IsFalse(viewModel.AutoDetectCommand.CanExecute(null));
        StringAssert.Contains(viewModel.EnhancementAvailabilityText, "unavailable");
        StringAssert.Contains(viewModel.AutoDetectionAvailabilityText, "manual tools");
        StringAssert.Contains(viewModel.MissingAutomaticStagesText, "axis");
        StringAssert.Contains(viewModel.MissingAutomaticStagesText, "markers");

        Execute(viewModel.ImportCommand);
        await WaitForAsync(
            () => viewModel.Tabs.Count == 2 && !viewModel.IsBusy,
            "multi-image import through ImportCommand");
        Assert.AreEqual(1, dialogs.ImageSelectionCount);
        Assert.IsTrue(viewModel.Tabs.All(static tab =>
            tab.Points.Count == 0 && tab.SeriesCards.Count == 0 && tab.PhaseDividers.Count == 0));
        Assert.AreEqual(secondImage, viewModel.SelectedTab!.SourcePath);

        WorkspaceTabViewModel retainedTab = viewModel.Tabs[0];
        WorkspaceTabViewModel tabToClose = viewModel.Tabs[1];
        viewModel.SelectedTab = retainedTab;
        Assert.AreSame(retainedTab, viewModel.SelectedTab);
        viewModel.SelectedTab = tabToClose;
        Execute(viewModel.CloseTabCommand);
        Assert.HasCount(1, viewModel.Tabs);
        Assert.AreSame(retainedTab, viewModel.SelectedTab);
        Assert.HasCount(1, workspace.CreateWorkspace());

        viewModel.ManualYMaximum = 100;
        viewModel.ManualXMaximum = 9;
        Execute(viewModel.StartCalibrationCommand);
        Assert.AreEqual(ManualEditorMode.Calibration, viewModel.EditorMode);
        await viewModel.HandleCanvasPointAsync(new Point(10, 90));
        await viewModel.HandleCanvasPointAsync(new Point(10, 10));
        await viewModel.HandleCanvasPointAsync(new Point(90, 90));
        Assert.AreEqual(ManualEditorMode.Select, viewModel.EditorMode);
        Assert.IsNotNull(viewModel.SelectedTab!.Calibration);
        Assert.AreEqual(9, viewModel.SelectedTab.Calibration.XMaximum);
        Assert.AreEqual(100, viewModel.SelectedTab.Calibration.YMaximum);

        SeriesCardViewModel baseline = CreateSeries(
            viewModel,
            "Baseline",
            MarkerShape.Square,
            MarkerFill.Open,
            SemanticRole.Baseline);
        SeriesCardViewModel intervention = CreateSeries(
            viewModel,
            "Intervention",
            MarkerShape.Circle,
            MarkerFill.Filled,
            SemanticRole.Intervention);
        SeriesCardViewModel maintenance = CreateSeries(
            viewModel,
            "Maintenance",
            MarkerShape.TriangleUp,
            MarkerFill.Open,
            SemanticRole.Maintenance);
        Assert.AreEqual("□", baseline.Symbol);
        Assert.AreEqual(MarkerShape.Square, baseline.Shape);
        Assert.AreEqual(MarkerFill.Open, baseline.Fill);
        Assert.AreEqual("●", intervention.Symbol);
        Assert.AreEqual(MarkerShape.Circle, intervention.Shape);
        Assert.AreEqual(MarkerFill.Filled, intervention.Fill);

        viewModel.SelectedSeriesId = intervention.SeriesId;
        viewModel.SelectedSharedBaselineSeriesId = baseline.SeriesId;
        SeriesRelationChoiceViewModel maintenanceChoice = viewModel.ProbeRelationChoices.Single(
            choice => choice.SeriesId == maintenance.SeriesId);
        maintenanceChoice.IsSelected = true;
        Execute(viewModel.ApplySeriesRelationsCommand);
        SeriesRecord relatedIntervention = workspace.CurrentProject.Panels.Single().Series.Single(
            series => series.SeriesId.Value.ToString("D") == intervention.SeriesId);
        Assert.AreEqual(
            baseline.SeriesId,
            relatedIntervention.SharedBaselineSeriesId?.Value.ToString("D"));
        CollectionAssert.AreEqual(
            new[] { maintenance.SeriesId },
            relatedIntervention.ApplicableProbeSeriesIds
                .Select(static id => id.Value.ToString("D"))
                .ToArray());

        string baselinePointId = await AddPointAsync(viewModel, baseline.SeriesId, new Point(20, 80));
        string interventionPointId = await AddPointAsync(viewModel, intervention.SeriesId, new Point(50, 50));
        string pointToDeleteId = await AddPointAsync(viewModel, intervention.SeriesId, new Point(75, 30));
        viewModel.SelectedPointId = interventionPointId;
        Execute(viewModel.BeginMovePointCommand);
        await viewModel.HandleCanvasPointAsync(new Point(55, 45));
        GraphReader.App.Models.GraphPoint moved = viewModel.SelectedTab.Points.Single(
            point => point.PointId == interventionPointId);
        Assert.AreEqual(55, moved.PixelX);
        Assert.AreEqual(45, moved.PixelY);

        Assert.IsTrue(viewModel.ReassignPointCommand.CanExecute(baseline.SeriesId));
        viewModel.ReassignPointCommand.Execute(baseline.SeriesId);
        Assert.AreEqual(baseline.SeriesId, moved.SeriesId);
        Assert.IsTrue(viewModel.ReassignPointCommand.CanExecute(intervention.SeriesId));
        viewModel.ReassignPointCommand.Execute(intervention.SeriesId);
        Assert.AreEqual(intervention.SeriesId, moved.SeriesId);

        Assert.IsTrue(viewModel.DeletePointCommand.CanExecute(pointToDeleteId));
        viewModel.DeletePointCommand.Execute(pointToDeleteId);
        Assert.IsFalse(viewModel.SelectedTab.Points.Any(point => point.PointId == pointToDeleteId));
        Assert.HasCount(2, viewModel.SelectedTab.Points);
        Assert.AreEqual(1, baseline.Count);
        Assert.AreEqual(1, intervention.Count);

        viewModel.PhaseCode = "b";
        viewModel.PhaseLabel = "Intervention";
        Execute(viewModel.BeginAddPhaseDividerCommand);
        await viewModel.HandleCanvasPointAsync(new Point(50, 0));
        string dividerToDeleteId = viewModel.SelectedDividerId!;
        Execute(viewModel.BeginMovePhaseDividerCommand);
        await viewModel.HandleCanvasPointAsync(new Point(60, 0));
        EditablePhaseDivider movedDivider = viewModel.SelectedTab.PhaseDividers.Single(
            divider => divider.DividerId == dividerToDeleteId);
        Assert.AreEqual(60, movedDivider.OriginalX);
        viewModel.PhaseCode = "b";
        viewModel.PhaseLabel = "Treatment";
        Execute(viewModel.LabelPhaseDividerCommand);
        Assert.AreEqual("Treatment", movedDivider.Label);
        Execute(viewModel.DeletePhaseDividerCommand);
        Assert.HasCount(0, viewModel.SelectedTab.PhaseDividers);

        viewModel.PhaseCode = "b";
        viewModel.PhaseLabel = "Treatment";
        Execute(viewModel.BeginAddPhaseDividerCommand);
        await viewModel.HandleCanvasPointAsync(new Point(40, 0));
        Assert.HasCount(1, viewModel.SelectedTab.PhaseDividers);
        Assert.AreEqual("b", moved.PhaseCode);

        Execute(viewModel.SaveProjectAsCommand);
        await WaitForAsync(
            () => File.Exists(projectPath) &&
                string.Equals(workspace.CurrentProjectPath, Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase) &&
                !viewModel.IsBusy,
            "Save As through SaveProjectAsCommand");
        Assert.AreEqual(1, dialogs.ProjectSaveSelectionCount);
        Assert.IsNull(dialogs.SaveCurrentPaths.Single());
        Assert.IsTrue(new FileInfo(projectPath).Length > 0);
        Assert.AreEqual(firstHash, Sha256(firstImage));
        Assert.AreEqual(secondHash, Sha256(secondImage));
        CollectionAssert.AreEquivalent(
            new[] { firstHash },
            workspace.CurrentProject.Sources.Select(static source => source.Sha256).ToArray(),
            "Closing the second image removes its project source, not the original image file.");

        PanelRecord savedPanel = workspace.CurrentProject.Panels.Single();
        Assert.HasCount(0, savedPanel.Markers);
        Assert.HasCount(0, savedPanel.OcrRegions);
        Assert.IsNull(savedPanel.Enhancement);
        Assert.IsTrue(savedPanel.Points.All(static point =>
            point.MarkerId is null &&
            point.ModelVersion is null &&
            string.Equals(point.SourceStage, "manual", StringComparison.Ordinal)));
        Assert.IsFalse(
            (await File.ReadAllTextAsync(projectPath)).Contains("recorded_fake", StringComparison.Ordinal));

        string saveAsContents = await File.ReadAllTextAsync(projectPath);
        viewModel.SelectedPointId = interventionPointId;
        Execute(viewModel.BeginMovePointCommand);
        await viewModel.HandleCanvasPointAsync(new Point(57, 43));
        Execute(viewModel.SaveProjectCommand);
        await WaitForAsync(
            () => !viewModel.IsBusy &&
                !string.Equals(File.ReadAllText(projectPath), saveAsContents, StringComparison.Ordinal),
            "existing-project save through SaveProjectCommand");
        Assert.AreEqual(
            1,
            dialogs.ProjectSaveSelectionCount,
            "SaveProjectCommand must reuse CurrentProjectPath without reopening the dialog.");
        DomainResult<ProjectDocument> savedUpdate = await new ProjectFileStore().LoadAsync(
            projectPath,
            CancellationToken.None);
        Assert.IsTrue(
            savedUpdate.IsSuccess,
            string.Join(" | ", savedUpdate.Errors.Select(static error => error.TechnicalMessage)));
        PointRecord savedUpdatedPoint = savedUpdate.Value!.Panels.Single().Points.Single(
            point => point.PointId.Value.ToString("D") == interventionPointId);
        Assert.AreEqual(57, savedUpdatedPoint.OriginalPixel.X);
        Assert.AreEqual(43, savedUpdatedPoint.OriginalPixel.Y);

        viewModel.SelectedPointId = interventionPointId;
        Execute(viewModel.BeginMovePointCommand);
        await viewModel.HandleCanvasPointAsync(new Point(59, 41));
        string autosavePath = new ProjectSnapshotService(applicationPaths.AutosaveRoot)
            .GetSnapshotPath(workspace.CurrentProject.ProjectId);
        Assert.IsTrue(File.Exists(autosavePath), "The command-triggered edit must create a real autosave file.");
        DomainResult<ProjectDocument> unchangedPrimary = await new ProjectFileStore().LoadAsync(
            projectPath,
            CancellationToken.None);
        Assert.IsTrue(unchangedPrimary.IsSuccess);
        Assert.AreEqual(
            57,
            unchangedPrimary.Value!.Panels.Single().Points.Single(
                point => point.PointId.Value.ToString("D") == interventionPointId).OriginalPixel.X,
            "The recovery-only edit must remain in autosave until recovery.");

        WorkspaceTabViewModel beforeRecoveryTab = viewModel.SelectedTab;
        Execute(viewModel.RecoverProjectCommand);
        await WaitForAsync(
            () => !viewModel.IsBusy &&
                File.Exists(recoveredProjectPath) &&
                string.Equals(
                    workspace.CurrentProjectPath,
                    Path.GetFullPath(recoveredProjectPath),
                    StringComparison.OrdinalIgnoreCase) &&
                viewModel.SelectedTab is not null &&
                !ReferenceEquals(viewModel.SelectedTab, beforeRecoveryTab),
            "recovery-to-new-file through RecoverProjectCommand");
        Assert.AreEqual(2, dialogs.ProjectSaveSelectionCount);
        Assert.AreNotEqual(Path.GetFullPath(projectPath), Path.GetFullPath(recoveredProjectPath));
        Assert.IsTrue(dialogs.SaveCurrentPaths[1]!.EndsWith("-recovered.garproj", StringComparison.Ordinal));
        GraphReader.App.Models.GraphPoint recoveredEditedPoint = viewModel.SelectedTab!.Points.Single(
            point => point.PointId == interventionPointId);
        Assert.AreEqual(59, recoveredEditedPoint.PixelX);
        Assert.AreEqual(41, recoveredEditedPoint.PixelY);
        Assert.HasCount(3, viewModel.SelectedTab.SeriesCards);
        Assert.HasCount(1, viewModel.SelectedTab.PhaseDividers);
        SeriesRecord recoveredRelation = workspace.CurrentProject.Panels.Single().Series.Single(
            series => series.SeriesId.Value.ToString("D") == intervention.SeriesId);
        Assert.AreEqual(
            baseline.SeriesId,
            recoveredRelation.SharedBaselineSeriesId?.Value.ToString("D"));
        Assert.IsTrue(workspace.CurrentProject.Audit.LastAutosaveUtc.HasValue);

        WorkspaceTabViewModel editedTab = viewModel.SelectedTab;
        Execute(viewModel.OpenProjectCommand);
        await WaitForAsync(
            () => dialogs.ProjectOpenSelectionCount == 1 &&
                !viewModel.IsBusy &&
                viewModel.Tabs.Count == 1 &&
                viewModel.SelectedTab is not null &&
                !ReferenceEquals(viewModel.SelectedTab, editedTab),
            "project reopen through OpenProjectCommand");
        Assert.HasCount(2, viewModel.SelectedTab!.Points);
        Assert.HasCount(3, viewModel.SelectedTab.SeriesCards);
        Assert.HasCount(1, viewModel.SelectedTab.PhaseDividers);
        Assert.IsNotNull(viewModel.SelectedTab.Calibration);
        Assert.AreEqual(firstHash, viewModel.SelectedTab.SourceSha256);
        Assert.AreEqual(firstHash, Sha256(firstImage));
        Assert.AreEqual(secondHash, Sha256(secondImage));

        SeriesRecord reopenedIntervention = workspace.CurrentProject.Panels.Single().Series.Single(
            series => series.SeriesId.Value.ToString("D") == intervention.SeriesId);
        Assert.AreEqual(
            baseline.SeriesId,
            reopenedIntervention.SharedBaselineSeriesId?.Value.ToString("D"));
        CollectionAssert.AreEqual(
            new[] { maintenance.SeriesId },
            reopenedIntervention.ApplicableProbeSeriesIds
                .Select(static id => id.Value.ToString("D"))
                .ToArray());
        PointRecord reopenedMovedPoint = workspace.CurrentProject.Panels.Single().Points.Single(
            point => point.PointId.Value.ToString("D") == interventionPointId);
        Assert.IsTrue(reopenedMovedPoint.ModificationHistory.Count >= 3);
        Assert.IsTrue(reopenedMovedPoint.ModificationHistory.Any(static modification =>
            modification.Reason.Contains("moved", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(reopenedMovedPoint.ModificationHistory.Count(static modification =>
            modification.Reason.Contains("reassigned", StringComparison.OrdinalIgnoreCase)) >= 2);
        Assert.IsTrue(workspace.CurrentProject.Panels.Single().Points.Any(
            point => point.PointId.Value.ToString("D") == baselinePointId));

        dialogs.ObservationOrderChoices.Enqueue(true);
        Execute(viewModel.ExportCommand);
        await WaitForAsync(
            () => !viewModel.IsBusy &&
                viewModel.CurrentStage == WorkflowStage.Export &&
                Directory.GetFiles(exportDirectory, "*.csv", SearchOption.AllDirectories).Length > 0,
            "CSV export through ExportCommand");
        Assert.AreEqual(1, dialogs.ExportDirectorySelectionCount);
        Assert.IsEmpty(dialogs.ObservationOrderChoices);
        string[] csvFiles = Directory.GetFiles(exportDirectory, "*.csv", SearchOption.AllDirectories);
        Assert.IsTrue(csvFiles.Any(file =>
            string.Equals(File.ReadLines(file).FirstOrDefault(), ExportContract.MinimalCsvHeader, StringComparison.Ordinal)));
        Assert.IsTrue(Directory.GetFiles(exportDirectory, "*.json", SearchOption.AllDirectories).Length > 0);
        Assert.AreEqual(firstHash, Sha256(firstImage));
        Assert.AreEqual(secondHash, Sha256(secondImage));

        IReadOnlyList<AuditEvent> audit = workspace.CurrentProject.Audit.Events;
        Assert.IsTrue(audit.Count >= 15);
        Assert.IsTrue(audit.Any(static entry => entry.Kind == DomainEventKind.CalibrationChanged));
        Assert.IsTrue(audit.Any(static entry => entry.Kind == DomainEventKind.PointEdited));
        Assert.IsTrue(audit.Any(static entry => entry.Kind == DomainEventKind.PhaseEdited));
        Assert.IsTrue(audit.Any(static entry => entry.Kind == DomainEventKind.ExportSettingsChanged));
        Assert.IsTrue(audit.Any(static entry => entry.Note == "Manual point moved"));
        Assert.IsTrue(audit.Any(static entry => entry.Note == "Manual point reassigned"));
        Assert.IsTrue(audit.Any(static entry => entry.Note == "Manual phase divider deleted"));
        Assert.IsTrue(audit.Any(static entry => entry.Note == "Manual intervention series relations changed"));
        PanelId exportedPanelId = workspace.CurrentProject.Panels.Single().PanelId;
        Assert.IsTrue(audit.Any(entry =>
            entry.Kind == DomainEventKind.ExportSettingsChanged &&
            entry.PanelId == exportedPanelId &&
            entry.Note == "Workspace export prepared from current reviewed state"));
        Assert.IsFalse(workspace.UsesFakeGraphData);

        using var cancellationObserver = new BlockingSecondImageObserver(secondImage);
        var cancellationWorkspace = new ManualPreviewWorkspaceService(
            imageImportService: new ImageImportService(cancellationObserver));
        var cancellationDialogs = new DeterministicWorkspaceDialogService
        {
            Images = [firstImage, secondImage],
        };
        using var cancellationViewModel = new MainWindowViewModel(
            cancellationWorkspace,
            localization,
            dialogService: cancellationDialogs);

        Execute(cancellationViewModel.ImportCommand);
        await WaitForAsync(
            () => cancellationObserver.SecondImageStarted.IsSet,
            "second image import to start");
        Execute(cancellationViewModel.CancelCommand);
        cancellationObserver.ReleaseSecondImage.Set();
        await WaitForAsync(
            () => !cancellationViewModel.IsBusy && cancellationViewModel.Tabs.Count == 1,
            "completed image to remain visible after batch cancellation");

        Assert.AreEqual(LocalizationKeys.StatusCancelled, cancellationViewModel.StatusMessage);
        Assert.AreEqual(firstImage, cancellationViewModel.Tabs.Single().SourcePath);
        Assert.AreEqual(firstHash, cancellationViewModel.Tabs.Single().SourceSha256);
        Assert.HasCount(1, cancellationWorkspace.CreateWorkspace());
        Assert.HasCount(1, cancellationWorkspace.CurrentProject.Sources);
        Assert.HasCount(1, cancellationWorkspace.CurrentProject.Panels);
        Assert.AreEqual(firstHash, cancellationWorkspace.CurrentProject.Sources.Single().Sha256);
        Assert.AreEqual(firstHash, Sha256(firstImage));
        Assert.AreEqual(secondHash, Sha256(secondImage));
        Assert.IsFalse(cancellationWorkspace.UsesFakeGraphData);
    }

    private static SeriesCardViewModel CreateSeries(
        MainWindowViewModel viewModel,
        string name,
        MarkerShape shape,
        MarkerFill fill,
        SemanticRole role)
    {
        viewModel.NewSeriesName = name;
        viewModel.NewSeriesShape = shape;
        viewModel.NewSeriesFill = fill;
        viewModel.NewSeriesRole = role;
        Execute(viewModel.CreateSeriesCommand);
        return viewModel.SeriesCards.Single(series => series.Label == name);
    }

    private static async Task<string> AddPointAsync(
        MainWindowViewModel viewModel,
        string seriesId,
        Point point)
    {
        viewModel.SelectedSeriesId = seriesId;
        Execute(viewModel.BeginAddPointCommand);
        Assert.AreEqual(ManualEditorMode.AddPoint, viewModel.EditorMode);
        await viewModel.HandleCanvasPointAsync(point);
        Assert.AreEqual(ManualEditorMode.Select, viewModel.EditorMode);
        return viewModel.SelectedPointId!;
    }

    private static void Execute(ICommand command)
    {
        Assert.IsTrue(command.CanExecute(null), $"Command '{command.GetType().Name}' was unexpectedly disabled.");
        command.Execute(null);
    }

    private static async Task WaitForAsync(Func<bool> completed, string operation)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!completed())
        {
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(5))
            {
                Assert.Fail($"Timed out after 5 seconds waiting for {operation}.");
            }

            await Task.Delay(10);
        }
    }

    private static void WriteImage(
        string path,
        int width,
        int height,
        byte blue,
        byte green,
        byte red)
    {
        var pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = byte.MaxValue;
        }

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

    private static string Sha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class BlockingSecondImageObserver : IImageImportStageObserver, IDisposable
    {
        private readonly string secondImagePath;

        public BlockingSecondImageObserver(string secondImagePath)
        {
            this.secondImagePath = Path.GetFullPath(secondImagePath);
        }

        public ManualResetEventSlim SecondImageStarted { get; } = new(initialState: false);

        public ManualResetEventSlim ReleaseSecondImage { get; } = new(initialState: false);

        public void Observe(ImageImportStage stage, string path, CancellationToken cancellationToken)
        {
            if (stage != ImageImportStage.BeforeHash ||
                !string.Equals(Path.GetFullPath(path), secondImagePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SecondImageStarted.Set();
            if (!ReleaseSecondImage.Wait(TimeSpan.FromSeconds(5), cancellationToken))
            {
                throw new TimeoutException("Timed out waiting to release the second image import.");
            }
        }

        public void Dispose()
        {
            SecondImageStarted.Dispose();
            ReleaseSecondImage.Dispose();
        }
    }

    private sealed class DeterministicWorkspaceDialogService : IWorkspaceDialogService
    {
        public IReadOnlyList<string> Images { get; init; } = [];

        public string? ProjectToOpen { get; init; }

        public IReadOnlyList<string?> ProjectSaveDestinations { get; init; } = [];

        public string? ExportDirectory { get; init; }

        public ProjectCloseAction CloseAction { get; set; } = ProjectCloseAction.Cancel;

        public int ClosePrompts { get; private set; }

        public ProjectCloseAction ChooseProjectCloseAction()
        {
            ClosePrompts++;
            return CloseAction;
        }

        public int ImageSelectionCount { get; private set; }

        public int ProjectOpenSelectionCount { get; private set; }

        public int ProjectSaveSelectionCount { get; private set; }

        public int ExportDirectorySelectionCount { get; private set; }

        public List<string?> SaveCurrentPaths { get; } = [];

        public IReadOnlyList<string> SelectImages()
        {
            ImageSelectionCount++;
            return Images;
        }

        public string? SelectProjectToOpen()
        {
            ProjectOpenSelectionCount++;
            return ProjectToOpen;
        }

        public string? SelectProjectToSave(string? currentPath)
        {
            int selectionIndex = ProjectSaveSelectionCount++;
            SaveCurrentPaths.Add(currentPath);
            return selectionIndex < ProjectSaveDestinations.Count
                ? ProjectSaveDestinations[selectionIndex]
                : null;
        }

        public Queue<bool> ObservationOrderChoices { get; } = new();

        public bool ConfirmObservationOrder() =>
            ObservationOrderChoices.TryDequeue(out bool choice) && choice;

        public string? SelectExportDirectory()
        {
            ExportDirectorySelectionCount++;
            return ExportDirectory;
        }
    }

    private sealed class DeterministicLocalizationService : ILocalizationService
    {
        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

        public CultureInfo CurrentCulture => English;

        public IReadOnlyList<CultureInfo> AvailableCultures { get; } = [English];

        public event EventHandler<LocalizationChangedEventArgs>? CultureChanged;

        public string GetString(string key) => key switch
        {
            LocalizationKeys.WorkflowEnhanceUnavailable =>
                "Enhancement is unavailable until approved local components are configured.",
            LocalizationKeys.WorkflowAutoDetectUnavailable =>
                "Automatic detection is unavailable. Use the manual tools.",
            _ => key,
        };

        public void ApplyCulture(CultureInfo culture)
        {
            ArgumentNullException.ThrowIfNull(culture);
            CultureChanged?.Invoke(this, new LocalizationChangedEventArgs(English));
        }
    }
}
