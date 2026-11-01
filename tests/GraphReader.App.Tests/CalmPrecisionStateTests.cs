// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Models;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using GraphPoint = GraphReader.App.Models.GraphPoint;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalmPrecisionStateTests
{
    private static readonly string[] ExpectedPreviewFileNames =
        [
            "Example graph_Primary intervention.csv",
            "Example graph_Primary intervention.audit.csv",
            "Example graph_Primary intervention.audit.json",
        ];

    [TestMethod]
    public void EmptyReadyAndRecordedReviewStatesAreDerivedWithoutGuessing()
    {
        using var empty = new MainWindowViewModel(new UnavailableWorkspaceService());
        using var ready = new MainWindowViewModel(new ControlledWorkspaceService(
            CreateEmptyTab(),
            static (_, _) => Task.CompletedTask));
        using var reviewing = new MainWindowViewModel(new FakeWorkspaceService());

        Assert.AreEqual(WorkspaceSurfaceState.Empty, empty.SurfaceState);
        Assert.AreEqual(WorkspaceSurfaceState.Ready, ready.SurfaceState);
        Assert.AreEqual(WorkspaceSurfaceState.Reviewing, reviewing.SurfaceState);
    }

    [TestMethod]
    public async Task AnalyzeIsExplicitCancelableAndRestoresPriorSurface()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new MainWindowViewModel(new ControlledWorkspaceService(
            CreateEmptyTab(),
            async (stage, cancellationToken) =>
            {
                Assert.AreEqual(WorkflowStage.Detect, stage);
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }));

        Assert.AreEqual(WorkspaceSurfaceState.Ready, viewModel.SurfaceState);
        viewModel.AutoDetectCommand.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(WorkspaceSurfaceState.Analyzing, viewModel.SurfaceState);
        Assert.IsTrue(viewModel.IsBusy);

        viewModel.CancelCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.AreEqual(WorkspaceSurfaceState.Ready, viewModel.SurfaceState);
    }

    [TestMethod]
    public async Task AnalyzeFailureRestoresReviewAndShowsOnlySafeLocalizedStatus()
    {
        WorkspaceTabViewModel reviewTab = new FakeWorkspaceService().CreateWorkspace()[0];
        using var viewModel = new MainWindowViewModel(new ControlledWorkspaceService(
            reviewTab,
            static (_, _) => throw new InvalidDataException(
                "C:\\private\\models\\detector.onnx failed with internal stack details")));
        int pointCount = viewModel.DataPreviewRows.Count;
        int issueCount = viewModel.ReviewIssues.Count;

        viewModel.AutoDetectCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.AreEqual(WorkspaceSurfaceState.Reviewing, viewModel.SurfaceState);
        Assert.AreEqual(pointCount, viewModel.DataPreviewRows.Count);
        Assert.AreEqual(issueCount, viewModel.ReviewIssues.Count);
        Assert.IsNotNull(viewModel.LastOperationStatus);
        Assert.AreEqual("WORKSPACE_STAGE_FAILED", viewModel.LastOperationStatus.Code);
        Assert.AreEqual("Errors.ProductionWorkflowUnavailable", viewModel.StatusMessageKey);
        Assert.Contains("detector.onnx", viewModel.LastOperationStatus.TechnicalMessage);
        Assert.DoesNotContain("detector.onnx", viewModel.StatusMessage);
        Assert.DoesNotContain("internal stack", viewModel.StatusMessage);
    }

    [TestMethod]
    public async Task CalibrationFailureSelectsOnlyTheMatchingPanelAndShowsRecoveryAction()
    {
        Guid panelId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        string hash = new('a', 64);
        var target = new WorkspaceTabViewModel(
            "second", "Second graph", new ObservableCollection<GraphPoint>(),
            new ObservableCollection<SeriesCardViewModel>(), null, null, null,
            panelId: panelId.ToString("D"), sourceSha256: hash, pixelWidth: 640, pixelHeight: 480);
        var evidence = new WorkflowVisionEnvelope(1, runId, projectId, panelId, "axis", "1", hash,
            null, new WorkflowVisionTiming(0, 0, 0, 0), 1);
        var failure = new ProductionWorkflowFailure(
            ProductionWorkflowFailureCodes.RecalibrationRequired, "Errors.CalibrationInvalid",
            "private technical details", true, "calibrate");
        using var viewModel = new MainWindowViewModel(new ControlledWorkspaceService(
            CreateEmptyTab(), (_, _) => throw new ProductionWorkflowStageException(failure, [evidence])));
        viewModel.Tabs.Add(target);

        viewModel.AutoDetectCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.AreSame(target, viewModel.SelectedTab);
        Assert.AreEqual("Errors.DetectionCalibrationRequired", viewModel.StatusMessageKey);
        Assert.AreEqual(ProductionWorkflowFailureCodes.RecalibrationRequired, viewModel.LastOperationStatus?.Code);
        Assert.DoesNotContain("private technical details", viewModel.StatusMessage);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public void InterruptedDetectionMakesOnlyCompletedPanelReviewable(bool cancel, bool retainPriorPanel)
    {
        string root = Path.Combine(Path.GetTempPath(), $"graph-reader-partial-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            StaTestHost.Run(() =>
            {
                string firstPath = Path.Combine(root, "first.png");
                string secondPath = Path.Combine(root, "second.png");
                WriteImage(firstPath, 8, 8);
                WriteImage(secondPath, 9, 9);
                var service = new CheckpointWorkspaceService(cancel);
                WorkspaceTabViewModel[] tabs = service.ImportImagesAsync([firstPath, secondPath], CancellationToken.None)
                    .GetAwaiter().GetResult().ToArray();
                if (retainPriorPanel) service.SeedPriorPanel(tabs[1]);
                using var viewModel = new MainWindowViewModel(service);
                Assert.AreSame(tabs[0], viewModel.SelectedTab);
                Assert.IsFalse(viewModel.ReviewCommand.CanExecute(null));

                viewModel.AutoDetectCommand.Execute(null);
                if (cancel) viewModel.CancelCommand.Execute(null);
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                timer.Tick += (_, _) =>
                {
                    if (!viewModel.IsBusy || DateTime.UtcNow >= deadline)
                    {
                        timer.Stop();
                        frame.Continue = false;
                    }
                };
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Assert.IsFalse(viewModel.IsBusy, "Detection did not finish after interruption.");

                viewModel.SelectedTab = tabs[0];
                Assert.AreEqual(WorkspaceSurfaceState.Reviewing, viewModel.SurfaceState);
                Assert.IsTrue(viewModel.ReviewCommand.CanExecute(null));
                viewModel.SelectedTab = tabs[1];
                Assert.AreEqual(WorkspaceSurfaceState.Ready, viewModel.SurfaceState);
                Assert.IsFalse(viewModel.ReviewCommand.CanExecute(null));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("mixed")]
    [DataRow("empty")]
    [DataRow("different_image")]
    public async Task CalibrationFailureDoesNotNavigateOnMixedOrMismatchedEvidence(string scenario)
    {
        Guid panelId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        string hash = new('a', 64);
        var original = CreateEmptyTab();
        var target = new WorkspaceTabViewModel(
            "second", "Second graph", new ObservableCollection<GraphPoint>(),
            new ObservableCollection<SeriesCardViewModel>(), null, null, null,
            panelId: panelId.ToString("D"), sourceSha256: hash, pixelWidth: 640, pixelHeight: 480);
        var first = new WorkflowVisionEnvelope(1, runId, projectId, panelId, "axis", "1", hash,
            null, new WorkflowVisionTiming(0, 0, 0, 0), 1);
        var mixed = new WorkflowVisionEnvelope(1, runId, projectId, Guid.NewGuid(), "ocr", "1", hash,
            null, new WorkflowVisionTiming(0, 0, 0, 0), 1);
        var differentImage = new WorkflowVisionEnvelope(1, runId, projectId, panelId, "axis", "1", new string('b', 64),
            null, new WorkflowVisionTiming(0, 0, 0, 0), 1);
        WorkflowVisionEnvelope[] evidence = scenario switch
        {
            "mixed" => [first, mixed],
            "empty" => [],
            "different_image" => [differentImage],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var failure = new ProductionWorkflowFailure(
            ProductionWorkflowFailureCodes.RecalibrationRequired, "Errors.CalibrationInvalid", "invalid", true, "calibrate");
        using var viewModel = new MainWindowViewModel(new ControlledWorkspaceService(
            original, (_, _) => throw new ProductionWorkflowStageException(failure, evidence)));
        viewModel.Tabs.Add(target);

        viewModel.AutoDetectCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.AreSame(original, viewModel.SelectedTab);
        Assert.AreEqual("Errors.DetectionCalibrationPanelUnknown", viewModel.StatusMessageKey);
    }

    [TestMethod]
    public void CalibrationFailureDoesNotSelectAFormerProjectPanel()
    {
        var service = new ManualPreviewWorkspaceService();
        using var viewModel = new MainWindowViewModel(service);
        Guid panelId = Guid.NewGuid();
        string hash = new('a', 64);
        var target = new WorkspaceTabViewModel(
            "second", "Second graph", new ObservableCollection<GraphPoint>(),
            new ObservableCollection<SeriesCardViewModel>(), null, null, null,
            panelId: panelId.ToString("D"), sourceSha256: hash, pixelWidth: 640, pixelHeight: 480);
        viewModel.Tabs.Add(target);
        var foreignEvidence = new WorkflowVisionEnvelope(1, Guid.NewGuid(), Guid.NewGuid(), panelId,
            "axis", "1", hash, null, new WorkflowVisionTiming(0, 0, 0, 0), 1);
        var failure = new ProductionWorkflowFailure(
            ProductionWorkflowFailureCodes.RecalibrationRequired, "Errors.CalibrationInvalid", "invalid", true, "calibrate");

        Assert.IsFalse(viewModel.SelectCalibrationFailurePanel(new ProductionWorkflowStageException(failure, [foreignEvidence])));
        Assert.AreNotSame(target, viewModel.SelectedTab);
    }

    [TestMethod]
    public void ReviewProjectionAndTableSelectionStaySynchronized()
    {
        using var viewModel = new MainWindowViewModel(new FakeWorkspaceService());

        Assert.HasCount(1, viewModel.ReviewIssues);
        Assert.AreEqual(ReviewIssueKind.Calibration, viewModel.ReviewIssues[0].Kind);
        Assert.AreEqual(ReviewIssueSeverity.Blocking, viewModel.ReviewIssues[0].Severity);
        Assert.AreEqual(viewModel.SelectedTab!.Points.Count, viewModel.DataPreviewRows.Count);

        DataPreviewRowViewModel target = viewModel.DataPreviewRows[^1];
        viewModel.SelectedDataPreviewRow = target;

        Assert.AreEqual(target.PointId, viewModel.SelectedPointId);
        Assert.AreSame(target, viewModel.SelectedDataPreviewRow);
        Assert.AreEqual(target.PixelX, viewModel.Magnifier.PixelPosition.X);
        Assert.AreEqual(target.PixelY, viewModel.Magnifier.PixelPosition.Y);

        viewModel.HandleCanvasNavigation(new System.Windows.Point(0, 0));
        viewModel.SelectDataPreviewRowCommand.Execute(target);

        Assert.AreEqual(target.PixelX, viewModel.Magnifier.PixelPosition.X);
        Assert.AreEqual(target.PixelY, viewModel.Magnifier.PixelPosition.Y);

        var pointIssue = new ReviewIssueViewModel(
            "point-backed-test",
            viewModel.SelectedTab.TabId,
            target.PointId,
            ReviewIssueKind.Point,
            ReviewIssueSeverity.Warning,
            "Review.Title",
            "Review.PointConfidence.Interpretation",
            "Review.PointConfidence.Action",
            "Point review",
            "Inspect the selected point.",
            "Correct or accept the point.");
        viewModel.SelectedReviewIssue = pointIssue;

        Assert.AreEqual(target.PointId, viewModel.SelectedPointId);
        Assert.AreEqual(target.SeriesId, viewModel.SelectedSeriesId);
        Assert.AreSame(target, viewModel.SelectedDataPreviewRow);
        Assert.AreEqual(target.PixelX, viewModel.Magnifier.PixelPosition.X);
        Assert.AreEqual(target.PixelY, viewModel.Magnifier.PixelPosition.Y);
        double expectedCrosshairX = viewModel.SelectedTab.PixelWidth > 0
            ? target.PixelX / viewModel.SelectedTab.PixelWidth
            : 0;
        double expectedCrosshairY = viewModel.SelectedTab.PixelHeight > 0
            ? target.PixelY / viewModel.SelectedTab.PixelHeight
            : 0;
        Assert.AreEqual(expectedCrosshairX, viewModel.Magnifier.CrosshairPosition.X, 0.0001);
        Assert.AreEqual(expectedCrosshairY, viewModel.Magnifier.CrosshairPosition.Y, 0.0001);

        viewModel.HandleCanvasNavigation(new System.Windows.Point(0, 0));
        viewModel.SelectReviewIssueCommand.Execute(pointIssue);

        Assert.AreEqual(target.PixelX, viewModel.Magnifier.PixelPosition.X);
        Assert.AreEqual(target.PixelY, viewModel.Magnifier.PixelPosition.Y);
    }

    [TestMethod]
    public void DefaultPipelineWarningsStayWithTheAnalyzedTab()
    {
        string root = Path.Combine(Path.GetTempPath(), $"graph-reader-warning-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string analyzedPath = Path.Combine(root, "analyzed.png");
        string otherPath = Path.Combine(root, "other.png");

        try
        {
            StaTestHost.Run(() =>
            {
                WriteImage(analyzedPath, 160, 120);
                WriteImage(otherPath, 160, 120);
                var workspace = new ProductionWorkspaceService();
                IReadOnlyList<WorkspaceTabViewModel> tabs = workspace
                    .ImportImagesAsync([analyzedPath, otherPath], CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                WorkspaceTabViewModel analyzedTab = tabs[0];
                WorkspaceTabViewModel otherTab = tabs[1];
                WorkflowRunResult run = CreateWarningRun(analyzedTab, projectId: workspace.CurrentProject.ProjectId.Value);
                PropertyInfo lastRunProperty = typeof(ProductionWorkspaceService).GetProperty(
                    nameof(ProductionWorkspaceService.LastAutomaticRun)) ??
                    throw new InvalidOperationException("LastAutomaticRun property is unavailable.");
                lastRunProperty.SetValue(workspace, run);
                using var viewModel = new MainWindowViewModel(workspace);

                viewModel.SelectedTab = otherTab;

                Assert.IsFalse(viewModel.ReviewIssues.Any(
                    static issue => issue.Kind == ReviewIssueKind.PipelineWarning));

                viewModel.SelectedTab = analyzedTab;

                ReviewIssueViewModel warning = viewModel.ReviewIssues.Single(
                    static issue => issue.Kind == ReviewIssueKind.PipelineWarning);
                Assert.DoesNotContain("private", warning.Interpretation);
                Assert.IsNotNull(warning.TechnicalMessage);
                Assert.Contains("private", warning.TechnicalMessage!);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void WidePhaseClusterWarningExplainsTheReviewNeeded()
    {
        string root = Path.Combine(Path.GetTempPath(), $"graph-reader-phase-warning-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            StaTestHost.Run(() =>
            {
                string imagePath = Path.Combine(root, "source.png");
                WriteImage(imagePath, 160, 120);
                var workspace = new ProductionWorkspaceService();
                WorkspaceTabViewModel tab = workspace.ImportImagesAsync([imagePath], CancellationToken.None)
                    .GetAwaiter().GetResult().Single();
                WorkflowRunResult run = CreateWarningRun(tab, "phase_divider_wide_cluster_needs_review",
                    workspace.CurrentProject.ProjectId.Value);
                PropertyInfo lastRunProperty = typeof(ProductionWorkspaceService).GetProperty(
                    nameof(ProductionWorkspaceService.LastAutomaticRun)) ??
                    throw new InvalidOperationException("LastAutomaticRun property is unavailable.");
                lastRunProperty.SetValue(workspace, run);
                var localization = new GraphReader.App.Localization.LocalizationService(new System.Windows.ResourceDictionary());
                using var viewModel = new MainWindowViewModel(workspace, localization);

                ReviewIssueViewModel warning = viewModel.ReviewIssues.Single(
                    static issue => issue.Kind == ReviewIssueKind.PipelineWarning);
                Assert.AreEqual("Nearby phase lines were grouped. Check whether they mark one boundary or several.",
                    warning.Interpretation);
                Assert.AreEqual(ReviewIssueSeverity.Warning, warning.Severity);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RetainedPhaseWarningIsShownWithoutAnInMemoryRun()
    {
        string root = Path.Combine(Path.GetTempPath(), $"graph-reader-retained-warning-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            StaTestHost.Run(() =>
            {
                string imagePath = Path.Combine(root, "source.png");
                WriteImage(imagePath, 160, 120);
                var store = new ProductionWorkflowPanelStore();
                var workspace = new ProductionWorkspaceService(panelStore: store);
                WorkspaceTabViewModel tab = workspace.ImportImagesAsync([imagePath], CancellationToken.None)
                    .GetAwaiter().GetResult().Single();
                WorkflowReviewPanel panel = CreateWarningRun(tab).Review.Panels.Single();
                store.Register(new ProductionPanelEvidence(panel.PreparedPanel.ImportedPanel,
                    WorkflowSourceKind.Image, File.ReadAllBytes(imagePath)));
                var envelope = new WorkflowVisionEnvelope(1, Guid.NewGuid(),
                    workspace.CurrentProject.ProjectId.Value, panel.PanelId, "phases", "1",
                    panel.PreparedPanel.Original.Sha256, null, new WorkflowVisionTiming(0, 0, 0, 0), 1,
                    warnings: ["phase_divider_wide_cluster_needs_review"]);
                store.SetExportEvidence(panel.PanelId, new ProductionPanelExportEvidence(
                    new GraphReader.Export.ExportCalibration(GraphReader.Export.ExportCalibrationStatus.Valid,
                        true, true, true, 1, 1), [], [], [], [], [envelope]));
                var localization = new GraphReader.App.Localization.LocalizationService(new System.Windows.ResourceDictionary());
                using var viewModel = new MainWindowViewModel(workspace, localization);

                Assert.IsNull(workspace.LastAutomaticRun);
                ReviewIssueViewModel warning = viewModel.ReviewIssues.Single(
                    static issue => issue.Kind == ReviewIssueKind.PipelineWarning);
                Assert.AreEqual("Nearby phase lines were grouped. Check whether they mark one boundary or several.",
                    warning.Interpretation);
                Assert.AreEqual(ReviewIssueSeverity.Warning, warning.Severity);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void EditHistoryIsPerTabAndRestoresManualPointMovement()
    {
        using var viewModel = new MainWindowViewModel(new FakeWorkspaceService());
        WorkspaceTabViewModel firstTab = viewModel.SelectedTab!;
        GraphPoint point = firstTab.Points[0];
        double originalX = point.PixelX;
        double originalY = point.PixelY;

        viewModel.MovePoint(point.PointId, originalX + 11, originalY + 7);

        Assert.IsTrue(viewModel.CanUndo);
        Assert.AreEqual(originalX + 11, viewModel.SelectedTab!.Points[0].PixelX);
        viewModel.UndoCommand.Execute(null);
        Assert.AreEqual(originalX, viewModel.SelectedTab.Points[0].PixelX);
        Assert.AreEqual(originalY, viewModel.SelectedTab.Points[0].PixelY);
        Assert.IsTrue(viewModel.CanRedo);

        viewModel.RedoCommand.Execute(null);
        Assert.AreEqual(originalX + 11, viewModel.SelectedTab.Points[0].PixelX);
        Assert.AreEqual(originalY + 7, viewModel.SelectedTab.Points[0].PixelY);

        var secondTab = new WorkspaceTabViewModel(
            "second-tab",
            "Second graph",
            new ObservableCollection<GraphPoint>(),
            new ObservableCollection<SeriesCardViewModel>(),
            imageSource: null,
            enhancedImageSource: null,
            phaseOverlayContent: null,
            overlayContent: new object());
        viewModel.Tabs.Add(secondTab);
        viewModel.SelectedTab = secondTab;
        Assert.IsFalse(viewModel.CanUndo);
        viewModel.SelectedTab = firstTab;
        Assert.IsTrue(viewModel.CanUndo);
    }

    [TestMethod]
    public void EditHistoryRestoresSharedBaselineAndApplicableProbeRelations()
    {
        string root = Path.Combine(Path.GetTempPath(), $"graph-reader-relations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string imagePath = Path.Combine(root, "relations.png");

        try
        {
            StaTestHost.Run(() =>
            {
                WriteImage(imagePath, 160, 120);
                var workspace = new ManualPreviewWorkspaceService();
                WorkspaceTabViewModel tab = workspace.ImportImagesAsync([imagePath], CancellationToken.None)
                    .GetAwaiter().GetResult().Single();
                SeriesCardViewModel baseline = workspace.AddSeries(
                    tab.TabId,
                    new ManualSeriesDefinition(
                        "Baseline",
                        "□",
                        MarkerShape.Square,
                        MarkerFill.Open,
                        SemanticRole.Baseline));
                SeriesCardViewModel intervention = workspace.AddSeries(
                    tab.TabId,
                    new ManualSeriesDefinition(
                        "Intervention",
                        "●",
                        MarkerShape.Circle,
                        MarkerFill.Filled,
                        SemanticRole.Intervention));
                SeriesCardViewModel maintenance = workspace.AddSeries(
                    tab.TabId,
                    new ManualSeriesDefinition(
                        "Maintenance",
                        "△",
                        MarkerShape.TriangleUp,
                        MarkerFill.Open,
                        SemanticRole.Maintenance));
                using var viewModel = new MainWindowViewModel(workspace);
                viewModel.SelectedSeriesId = intervention.SeriesId;
                viewModel.SelectedSharedBaselineSeriesId = baseline.SeriesId;
                viewModel.ProbeRelationChoices.Single(
                    choice => choice.SeriesId == maintenance.SeriesId).IsSelected = true;

                viewModel.ApplySeriesRelationsCommand.Execute(null);

                SeriesRecord applied = FindSeries(workspace, intervention.SeriesId);
                Assert.AreEqual(Guid.Parse(baseline.SeriesId), applied.SharedBaselineSeriesId?.Value);
                CollectionAssert.AreEqual(
                    new[] { Guid.Parse(maintenance.SeriesId) },
                    applied.ApplicableProbeSeriesIds.Select(static id => id.Value).ToArray());
                Assert.IsTrue(viewModel.CanUndo);

                viewModel.UndoCommand.Execute(null);

                SeriesRecord undone = FindSeries(workspace, intervention.SeriesId);
                Assert.IsNull(undone.SharedBaselineSeriesId);
                Assert.HasCount(0, undone.ApplicableProbeSeriesIds);
                Assert.IsTrue(viewModel.CanRedo);

                viewModel.RedoCommand.Execute(null);

                SeriesRecord redone = FindSeries(workspace, intervention.SeriesId);
                Assert.AreEqual(Guid.Parse(baseline.SeriesId), redone.SharedBaselineSeriesId?.Value);
                CollectionAssert.AreEqual(
                    new[] { Guid.Parse(maintenance.SeriesId) },
                    redone.ApplicableProbeSeriesIds.Select(static id => id.Value).ToArray());
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ExportPreviewSummarizesEvidenceAndHonorsBlockingIssues()
    {
        WorkspaceTabViewModel tab = new FakeWorkspaceService().CreateWorkspace()[0];
        tab.SeriesCards[0].SemanticRole = SemanticRole.Intervention;
        tab.SeriesCards[1].SemanticRole = SemanticRole.Generalization;
        tab.Calibration = CreateCalibration();
        int stagesRun = 0;
        using var viewModel = new MainWindowViewModel(
            new ControlledWorkspaceService(
                tab,
                (_, _) =>
                {
                    stagesRun++;
                    return Task.CompletedTask;
                }),
            localizationService: null,
            pipelineWarningsProvider: static _ => [new PipelineWarningPresentation(
                "Review.PipelineWarning.Interpretation",
                "Axis labels require review before export.")]);

        viewModel.OpenExportPreviewCommand.Execute(null);

        Assert.AreEqual(WorkspaceSurfaceState.ExportPreview, viewModel.SurfaceState);
        Assert.IsNotNull(viewModel.ExportSummary);
        Assert.AreEqual(viewModel.SelectedTab!.Points.Count, viewModel.ExportSummary.PointCount);
        Assert.AreEqual(viewModel.SeriesCards.Count, viewModel.ExportSummary.SeriesCount);
        Assert.AreEqual(2, viewModel.ExportSummary.PhaseCount);
        Assert.AreEqual(viewModel.BlockingReviewIssueCount, viewModel.ExportSummary.BlockingIssueCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(viewModel.ExportSummary.ProvenanceSummary));
        Assert.IsFalse(string.IsNullOrWhiteSpace(viewModel.ExportSummary.OutputDirectoryDisplay));
        Assert.AreEqual(ExportDestinationStatus.PendingSelection, viewModel.ExportSummary.DestinationStatus);
        Assert.IsTrue(viewModel.ExportSummary.IsDestinationPending);
        CollectionAssert.AreEqual(
            ExpectedPreviewFileNames,
            viewModel.ExportSummary.OutputFileNames.ToArray());
        Assert.HasCount(0, viewModel.ExportSummary.BlockingIssues);
        Assert.HasCount(1, viewModel.ExportSummary.WarningIssues);
        Assert.AreEqual(ReviewIssueKind.PipelineWarning, viewModel.ExportSummary.WarningIssues[0].Kind);
        Assert.AreEqual(
            "Review.PipelineWarning.Interpretation",
            viewModel.ExportSummary.WarningIssues[0].InterpretationKey);
        Assert.IsNotNull(viewModel.ExportSummary.WarningIssues[0].TechnicalMessage);
        Assert.Contains("Axis labels", viewModel.ExportSummary.WarningIssues[0].TechnicalMessage!);
        Assert.IsFalse(viewModel.ExportSummary.WarningsAcknowledged);
        Assert.HasCount(0, viewModel.ExportSummary.AcknowledgedWarningIssues);
        Assert.IsFalse(viewModel.ExportSummary.CanExport);
        Assert.IsFalse(viewModel.ConfirmExportCommand.CanExecute(null));

        viewModel.ExportSummary.WarningsAcknowledged = true;

        Assert.HasCount(1, viewModel.ExportSummary.AcknowledgedWarningIssues);
        Assert.IsTrue(viewModel.ExportSummary.CanExport);
        Assert.IsTrue(viewModel.ConfirmExportCommand.CanExecute(null));
        Assert.AreEqual(0, stagesRun);
    }

    [TestMethod]
    public void ExportWarningsRequireExplicitAcknowledgement()
    {
        var summary = new ExportSummaryViewModel(
            pointCount: 4,
            seriesCount: 1,
            phaseCount: 2,
            blockingIssueCount: 0,
            warningCount: 1,
            outputDirectory: null,
            outputFileNames: ["participant_intervention.csv"]);

        Assert.IsTrue(summary.RequiresWarningAcknowledgement);
        Assert.IsFalse(summary.CanExport);

        summary.WarningsAcknowledged = true;

        Assert.IsTrue(summary.CanExport);
    }

    [TestMethod]
    public void RecentProjectsLoadOnlyExistingDistinctPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), $"graph-reader-recent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string existingProject = Path.Combine(root, "example.garproj");
        string recentList = Path.Combine(root, "recent-projects.txt");
        File.WriteAllText(existingProject, "test");
        File.WriteAllLines(recentList, [existingProject, existingProject, Path.Combine(root, "missing.garproj")]);

        try
        {
            using var viewModel = new MainWindowViewModel(
                new UnavailableWorkspaceService(),
                localizationService: null,
                recentProjectsPath: recentList);

            Assert.IsTrue(viewModel.HasRecentProjects);
            Assert.HasCount(1, viewModel.RecentProjects);
            Assert.AreEqual(existingProject, viewModel.RecentProjects[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static WorkspaceTabViewModel CreateEmptyTab() => new(
        "ready-tab",
        "Ready graph",
        new ObservableCollection<GraphPoint>(),
        new ObservableCollection<SeriesCardViewModel>(),
        imageSource: null,
        enhancedImageSource: null,
        phaseOverlayContent: null,
        pixelWidth: 640,
        pixelHeight: 480,
        overlayContent: new object());

    private static ManualCalibrationState CreateCalibration() => new(
        new GraphReader.Axis.PixelPoint(10, 100),
        new GraphReader.Axis.PixelPoint(10, 10),
        new GraphReader.Axis.PixelPoint(500, 100),
        YMaximum: 100,
        XMaximum: 10,
        new GraphReader.Axis.LinearAxisTransform(9d / 490d, 40d / 49d),
        new GraphReader.Axis.LinearAxisTransform(-10d / 9d, 1000d / 9d),
        Confidence: 1);

    private static WorkflowRunResult CreateWarningRun(WorkspaceTabViewModel tab,
        string warning = "C:\\private\\models\\axis.onnx requires review.", Guid? projectId = null)
    {
        var original = new WorkflowImageEvidence(
            tab.SourcePath ?? throw new InvalidOperationException("Source path is required."),
            tab.SourceSha256 ?? throw new InvalidOperationException("Source hash is required."),
            tab.PixelWidth,
            tab.PixelHeight,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(
            Guid.Parse(tab.PanelId ?? throw new InvalidOperationException("Panel ID is required.")),
            Guid.Parse(tab.SourceId ?? throw new InvalidOperationException("Source ID is required.")),
            tab.DisplayName,
            original);
        var prepared = new WorkflowPreparedPanel(imported, original, enhanced: null);
        var reviewPanel = new WorkflowReviewPanel(prepared, []);
        return new WorkflowRunResult(
            Guid.NewGuid(),
            new WorkflowReviewState(
                projectId ?? Guid.NewGuid(),
                [reviewPanel],
                warnings: [warning]),
            []);
    }

    private static SeriesRecord FindSeries(ManualPreviewWorkspaceService workspace, string seriesId) =>
        workspace.CurrentProject.Panels.SelectMany(static panel => panel.Series).Single(
            series => series.SeriesId.Value == Guid.Parse(seriesId));

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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ControlledWorkspaceService(
        WorkspaceTabViewModel initialTab,
        Func<WorkflowStage, CancellationToken, Task> runStage) : IWorkspaceService
    {
        public IReadOnlyList<WorkspaceTabViewModel> CreateWorkspace() => [initialTab];

        public Task RunStageAsync(WorkflowStage stage, CancellationToken cancellationToken) =>
            runStage(stage, cancellationToken);
    }

    private sealed class CheckpointWorkspaceService(bool cancel) : ManualPreviewWorkspaceService(
        automaticStages:
        [
            new("axis", AutomaticStageState.Approved, "fixture"),
            new("ocr", AutomaticStageState.Approved, "fixture"),
            new("markers", AutomaticStageState.Approved, "fixture"),
            new("legends", AutomaticStageState.Approved, "fixture"),
            new("phases", AutomaticStageState.Approved, "fixture"),
        ]), IAutomaticWorkspaceService
    {
        public WorkflowRunResult? LastAutomaticRun { get; private set; }

        public void SeedPriorPanel(WorkspaceTabViewModel tab)
        {
            WorkflowReviewPanel prior = CreateWarningRun(tab).Review.Panels.Single();
            LastAutomaticRun = new WorkflowRunResult(Guid.NewGuid(),
                new WorkflowReviewState(CurrentProject.ProjectId.Value, [prior]), []);
        }

        public Task<WorkflowRunResult> RunAutomaticDetectionAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("The fixture exercises the workspace stage entry point.");

        public override async Task RunStageAsync(WorkflowStage stage, CancellationToken cancellationToken)
        {
            if (stage != WorkflowStage.Detect) return;
            WorkspaceTabViewModel[] tabs = CreateWorkspace().ToArray();
            WorkflowReviewPanel completed = CreateWarningRun(tabs[0]).Review.Panels.Single();
            WorkflowReviewPanel[] prior = LastAutomaticRun?.Review.Panels.ToArray() ?? [];
            LastAutomaticRun = new WorkflowRunResult(Guid.NewGuid(),
                new WorkflowReviewState(CurrentProject.ProjectId.Value, [completed, .. prior]), []);
            if (cancel)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return;
            }
            var evidence = new WorkflowVisionEnvelope(1, Guid.NewGuid(), CurrentProject.ProjectId.Value,
                Guid.Parse(tabs[1].PanelId!), "axis", "fixture", tabs[1].SourceSha256!, null,
                new WorkflowVisionTiming(0, 0, 0, 0), 1);
            throw new ProductionWorkflowStageException(new ProductionWorkflowFailure(
                ProductionWorkflowFailureCodes.RecalibrationRequired, "Errors.CalibrationInvalid",
                "The next panel needs review.", true, "Calibrate."), [evidence]);
        }
    }
}
