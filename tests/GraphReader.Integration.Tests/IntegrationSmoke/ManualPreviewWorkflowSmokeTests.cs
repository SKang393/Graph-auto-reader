// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Domain;
using GraphReader.Export;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class ManualPreviewWorkflowSmokeTests
{
    private static readonly string[] ExpectedProbePhaseCodes = ["b", "b", "g", "m"];
    private static readonly string[] ExpectedSemanticProbeCodes = ["g", "m"];
    private static readonly double[] ExpectedManualOriginSessions = [29, 31, 35];
    private static readonly double[] ManualOriginPointXs = [300, 320, 360];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FirstVisibleSessionRoundTripsAndExportsEstimatedGapsWithAudit(bool production)
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string imagePath = environment.PathFor("manual-origin.png");
        WriteImage(imagePath, 400, 100);
        var capture = new CapturingExportService();
        ManualPreviewWorkspaceService NewWorkspace() => production
            ? new ProductionWorkspaceService(exportService: capture)
            : new ManualPreviewWorkspaceService(exportService: capture);
        ManualPreviewWorkspaceService workspace = NewWorkspace();
        var tab = (await workspace.ImportImagesAsync([imagePath], CancellationToken.None)).Single();
        workspace.Calibrate(tab.TabId, new ManualCalibrationRequest(
            new GraphReader.Axis.PixelPoint(300, 90), new GraphReader.Axis.PixelPoint(300, 10),
            new GraphReader.Axis.PixelPoint(360, 90), 100, 35, FirstVisibleSession: 29));
        var series = workspace.AddSeries(tab.TabId,
            new ManualSeriesDefinition("Intervention", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        foreach (double x in ManualOriginPointXs) workspace.AddPoint(tab.TabId, series.SeriesId, x, 50);
        var unrelated = workspace.AddSeries(tab.TabId,
            new ManualSeriesDefinition("Unlinked baseline", "○", MarkerShape.Circle, MarkerFill.Open, SemanticRole.Baseline));
        workspace.AddPoint(tab.TabId, unrelated.SeriesId, 20, 50); // Unselected series resolves to session 1.
        string projectPath = environment.PathFor("manual-origin.garproj");
        DomainResult<ProjectSaveReceipt> saved = await workspace.SaveProjectAsync(projectPath, CancellationToken.None);
        Assert.IsTrue(saved.IsSuccess, Format(saved.Errors));
        ManualPreviewWorkspaceService reopened = NewWorkspace();
        var reopenedTab = (await reopened.OpenProjectAsync(projectPath, CancellationToken.None)).Single();
        Assert.AreEqual(29d, reopenedTab.Calibration!.FirstVisibleSession);
        CalibrationRecord reopenedCalibration = reopened.CurrentProject.Panels.Single().Calibration!;
        Assert.IsTrue(reopenedCalibration.Anchors.Any(static anchor =>
            anchor.Kind == GraphReader.Domain.CalibrationAnchorKind.FirstVisibleSessionY0 && anchor.Graph.X == 29));
        Assert.IsFalse(reopenedCalibration.Anchors.Any(static anchor => anchor.Kind == GraphReader.Domain.CalibrationAnchorKind.Session1Y0));
        ExportResult result = await reopened.ExportAsync(reopenedTab.TabId, environment.PathFor("manual-origin-output"), CancellationToken.None);
        Assert.IsFalse(result.Preview.FinalExportBlocked);
        CollectionAssert.AreEqual(ExpectedManualOriginSessions, result.MinimalArtifacts.Single().Rows.Select(row => row.XValue).ToArray());
        Assert.IsTrue(result.AuditArtifacts.SelectMany(a => a.Rows).All(row =>
            row.XSource == ExportXValueSource.Estimated && row.SessionOriginOverrideApplied &&
            !string.IsNullOrWhiteSpace(row.SessionOriginOverrideReason) && row.SessionOriginOverrideConfirmedAtUtc.HasValue));
        string firstPointId = reopenedTab.Points.Where(point => point.SeriesId == series.SeriesId).OrderBy(static point => point.PixelX).First().PointId;
        reopened.DeletePoint(reopenedTab.TabId, firstPointId);
        await reopened.ExportAsync(reopenedTab.TabId, environment.PathFor("later-first-point"), CancellationToken.None);
        Assert.AreEqual(31d, capture.LastRequest!.Calibration.FirstObservedSession);

        var fileStore = new ProjectFileStore();
        var loaded = await fileStore.LoadAsync(projectPath, CancellationToken.None);
        Assert.IsTrue(loaded.IsSuccess, Format(loaded.Errors));
        PanelRecord panel = loaded.Value!.Panels.Single();
        JsonObject validation = JsonNode.Parse(panel.Validation!.Value.GetRawText())!.AsObject();
        validation["graphreader_manual_origin_confirmation"]!["reason"] = "altered";
        string tamperedPath = environment.PathFor("tampered-origin.garproj");
        var tampered = await fileStore.SaveAsync(loaded.Value with
        {
            Panels = [panel with { Validation = JsonSerializer.SerializeToElement(validation) }],
        }, tamperedPath, CancellationToken.None);
        Assert.IsTrue(tampered.IsSuccess, Format(tampered.Errors));
        ManualPreviewWorkspaceService rejected = NewWorkspace();
        var rejectedTab = (await rejected.OpenProjectAsync(tamperedPath, CancellationToken.None)).Single();
        string rejectedOutput = environment.PathFor("rejected-output");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            rejected.ExportAsync(rejectedTab.TabId, rejectedOutput, CancellationToken.None));
        Assert.IsFalse(Directory.Exists(rejectedOutput));

        CalibrationRecord mixedCalibration = panel.Calibration! with
        {
            Anchors = panel.Calibration!.Anchors.Select(anchor =>
                anchor.Kind == GraphReader.Domain.CalibrationAnchorKind.FirstVisibleSessionYmax
                    ? anchor with { Kind = GraphReader.Domain.CalibrationAnchorKind.Session1Ymax }
                    : anchor).ToArray(),
        };
        string mixedPath = environment.PathFor("mixed-origin.garproj");
        var mixed = await fileStore.SaveAsync(loaded.Value with
        {
            Panels = [panel with { Calibration = mixedCalibration }],
        }, mixedPath, CancellationToken.None);
        Assert.IsTrue(mixed.IsSuccess, Format(mixed.Errors));
        ManualPreviewWorkspaceService mixedWorkspace = NewWorkspace();
        var mixedTab = (await mixedWorkspace.OpenProjectAsync(mixedPath, CancellationToken.None)).Single();
        Assert.IsNull(mixedTab.Calibration, "Mixed anchor families must not reconstruct an editable calibration.");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            mixedWorkspace.ExportAsync(mixedTab.TabId, environment.PathFor("mixed-output"), CancellationToken.None));
    }

    [TestMethod]
    public async Task ManualCalibrationRejectsNonFiniteAndDegenerateInputsWithoutMutatingAcceptedState()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string imagePath = environment.PathFor("calibration-validation.png");
        WriteImage(imagePath, 100, 100);
        var workspace = new ManualPreviewWorkspaceService();
        GraphReader.App.ViewModels.WorkspaceTabViewModel tab =
            (await workspace.ImportImagesAsync([imagePath], CancellationToken.None)).Single();
        Calibrate(workspace, tab.TabId);

        GraphReader.App.Models.ManualCalibrationState accepted = tab.Calibration!;
        PanelRecord acceptedPanel = workspace.CurrentProject.Panels.Single();
        int acceptedAuditCount = workspace.CurrentProject.Audit.Events.Count;
        ManualCalibrationRequest valid = new(
            new GraphReader.Axis.PixelPoint(10, 90),
            new GraphReader.Axis.PixelPoint(10, 10),
            new GraphReader.Axis.PixelPoint(90, 90),
            YMaximum: 100,
            XMaximum: 9);
        ManualCalibrationRequest[] invalid =
        [
            valid with { YMaximum = double.NaN },
            valid with { YMaximum = double.PositiveInfinity },
            valid with { XMaximum = double.NaN },
            valid with { XMaximum = double.NegativeInfinity },
            valid with { Session1YMaximum = valid.Session1Y0 },
            valid with { SessionMaximumY0 = valid.Session1Y0 },
            valid with
            {
                SessionMaximumY0 = new GraphReader.Axis.PixelPoint(10.5, 90),
                XMaximum = double.MaxValue,
            },
            valid with
            {
                Session1Y0 = new GraphReader.Axis.PixelPoint(10, 0),
                Session1YMaximum = new GraphReader.Axis.PixelPoint(10, double.Epsilon),
                SessionMaximumY0 = new GraphReader.Axis.PixelPoint(90, 0),
                YMaximum = double.MaxValue,
            },
            valid with
            {
                SessionMaximumY0 = new GraphReader.Axis.PixelPoint(1e15, 90),
            },
        ];

        foreach (ManualCalibrationRequest request in invalid)
        {
            Assert.ThrowsExactly<ArgumentException>(() => workspace.Calibrate(tab.TabId, request));
            Assert.AreSame(accepted, tab.Calibration);
            Assert.AreSame(acceptedPanel, workspace.CurrentProject.Panels.Single());
            Assert.AreEqual(acceptedAuditCount, workspace.CurrentProject.Audit.Events.Count);
        }

        GraphReader.App.Models.ManualCalibrationState reversed = workspace.Calibrate(
            tab.TabId,
            valid with
            {
                Session1YMaximum = new GraphReader.Axis.PixelPoint(10, 95),
                SessionMaximumY0 = new GraphReader.Axis.PixelPoint(5, 90),
            });
        Assert.IsTrue(double.IsFinite(reversed.XTransform.Slope));
        Assert.IsTrue(double.IsFinite(reversed.YTransform.Slope));
    }

    [TestMethod]
    public void ViewModelAuthorsValidatedExportRelationships()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                VerifyViewModelRelations();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "STA relation-control verification timed out after 10 seconds.");
        if (failure is not null)
        {
            throw new AssertFailedException($"STA relation-control verification failed: {failure}", failure);
        }
    }

    private static void VerifyViewModelRelations()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string imagePath = environment.PathFor("relation controls graph.png");
        WriteImage(imagePath, 80, 80);
        var workspace = new ManualPreviewWorkspaceService();
        _ = workspace.ImportImagesAsync([imagePath], CancellationToken.None).GetAwaiter().GetResult();
        using var viewModel = new GraphReader.App.ViewModels.MainWindowViewModel(workspace);

        CreateSeries("Baseline", SemanticRole.Baseline);
        CreateSeries("Treatment", SemanticRole.Intervention);
        CreateSeries("Maintenance", SemanticRole.Maintenance);

        GraphReader.App.ViewModels.SeriesCardViewModel baseline = viewModel.SeriesCards.Single(
            static series => series.SemanticRole == SemanticRole.Baseline);
        GraphReader.App.ViewModels.SeriesCardViewModel intervention = viewModel.SeriesCards.Single(
            static series => series.SemanticRole == SemanticRole.Intervention);
        GraphReader.App.ViewModels.SeriesCardViewModel probe = viewModel.SeriesCards.Single(
            static series => series.SemanticRole == SemanticRole.Maintenance);
        viewModel.SelectedSeriesId = intervention.SeriesId;

        Assert.IsTrue(viewModel.BaselineRelationChoices.Any(choice => choice.SeriesId == baseline.SeriesId));
        GraphReader.App.Models.SeriesRelationChoiceViewModel probeChoice =
            viewModel.ProbeRelationChoices.Single(choice => choice.SeriesId == probe.SeriesId);
        viewModel.SelectedSharedBaselineSeriesId = baseline.SeriesId;
        probeChoice.IsSelected = true;
        Assert.IsTrue(viewModel.ApplySeriesRelationsCommand.CanExecute(null));
        viewModel.ApplySeriesRelationsCommand.Execute(null);

        SeriesRecord persisted = workspace.CurrentProject.Panels.Single().Series.Single(
            series => series.SeriesId.Value.ToString("D") == intervention.SeriesId);
        Assert.AreEqual(baseline.SeriesId, persisted.SharedBaselineSeriesId?.Value.ToString("D"));
        Assert.HasCount(1, persisted.ApplicableProbeSeriesIds);
        Assert.AreEqual(probe.SeriesId, persisted.ApplicableProbeSeriesIds[0].Value.ToString("D"));

        void CreateSeries(string name, SemanticRole role)
        {
            viewModel.NewSeriesName = name;
            viewModel.NewSeriesRole = role;
            Assert.IsTrue(viewModel.CreateSeriesCommand.CanExecute(null));
            viewModel.CreateSeriesCommand.Execute(null);
        }
    }

    [TestMethod]
    public void WpfCloseTabRemovesTheRealWorkspaceTab()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                VerifyCloseTab();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "STA close-tab verification timed out after 10 seconds.");
        if (failure is not null)
        {
            throw new AssertFailedException($"STA close-tab verification failed: {failure}", failure);
        }
    }

    private static void VerifyCloseTab()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string imagePath = environment.PathFor("close tab graph.png");
        WriteImage(imagePath, 40, 40);
        var workspace = new ManualPreviewWorkspaceService();
        _ = workspace.ImportImagesAsync([imagePath], CancellationToken.None).GetAwaiter().GetResult();
        using var viewModel = new GraphReader.App.ViewModels.MainWindowViewModel(workspace);

        Assert.HasCount(1, viewModel.Tabs);
        Assert.HasCount(1, workspace.CreateWorkspace());
        viewModel.CreateSeriesCommand.Execute(null);
        Assert.IsTrue(viewModel.SelectedTab!.IsDirty);
        viewModel.CloseTabCommand.Execute(null);
        Assert.HasCount(1, viewModel.Tabs);
        Assert.HasCount(1, workspace.CreateWorkspace());
        Assert.AreEqual(LocalizationKeys.ProjectCloseDirtyBlocked, viewModel.StatusMessage);

        var cleanWorkspace = new ManualPreviewWorkspaceService();
        _ = cleanWorkspace.ImportImagesAsync([imagePath], CancellationToken.None).GetAwaiter().GetResult();
        using var cleanViewModel = new GraphReader.App.ViewModels.MainWindowViewModel(cleanWorkspace);
        cleanViewModel.CloseTabCommand.Execute(null);
        Assert.HasCount(0, cleanViewModel.Tabs);
        Assert.HasCount(0, cleanWorkspace.CreateWorkspace());
    }

    [TestMethod]
    public async Task RealImageManualWorkflowPersistsRecoversAndExportsWithoutModelsOrFakeData()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string executableRoot = environment.PathFor("portable manual preview 한글");
        Directory.CreateDirectory(executableRoot);
        File.WriteAllText(Path.Combine(executableRoot, "portable.mode"), string.Empty);
        ApplicationPaths paths = ApplicationPaths.Create(executableRoot, environment.PathFor("LocalAppData"));
        string imagePath = environment.PathFor("maintainer graph.png");
        WriteImage(imagePath, 100, 100);
        string originalHash = Sha256(imagePath);
        var workspace = new ManualPreviewWorkspaceService(paths);

        IReadOnlyList<GraphReader.App.ViewModels.WorkspaceTabViewModel> imported =
            await workspace.ImportImagesAsync([imagePath], CancellationToken.None);

        Assert.HasCount(1, imported);
        GraphReader.App.ViewModels.WorkspaceTabViewModel tab = imported[0];
        Assert.HasCount(0, tab.Points);
        Assert.HasCount(0, tab.SeriesCards);
        Assert.IsFalse(workspace.UsesFakeGraphData);
        Assert.IsTrue(workspace.AutomaticStages.All(stage => stage.State == AutomaticStageState.Unavailable));
        workspace.Calibrate(
            tab.TabId,
            new ManualCalibrationRequest(
                new GraphReader.Axis.PixelPoint(10, 90),
                new GraphReader.Axis.PixelPoint(10, 10),
                new GraphReader.Axis.PixelPoint(90, 90),
                YMaximum: 100,
                XMaximum: 9));
        var baseline = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Baseline", "○", MarkerShape.Circle, MarkerFill.Open, SemanticRole.Baseline));
        var intervention = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Intervention", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        GraphReader.App.Models.GraphPoint baselinePoint = workspace.AddPoint(tab.TabId, baseline.SeriesId, 20, 70);
        GraphReader.App.Models.GraphPoint interventionPoint = workspace.AddPoint(tab.TabId, intervention.SeriesId, 60, 40);
        GraphReader.App.Models.GraphPoint discarded = workspace.AddPoint(tab.TabId, intervention.SeriesId, 80, 30);
        workspace.MovePoint(tab.TabId, interventionPoint.PointId, 70, 35);
        workspace.ReassignPoint(tab.TabId, interventionPoint.PointId, baseline.SeriesId);
        workspace.ReassignPoint(tab.TabId, interventionPoint.PointId, intervention.SeriesId);
        workspace.DeletePoint(tab.TabId, discarded.PointId);
        GraphReader.App.Models.EditablePhaseDivider divider =
            workspace.AddPhaseDivider(tab.TabId, 50, "b", "Intervention");
        workspace.MovePhaseDivider(tab.TabId, divider.DividerId, 55);
        workspace.LabelPhaseDivider(tab.TabId, divider.DividerId, "b", "Treatment");
        workspace.DeletePhaseDivider(tab.TabId, divider.DividerId);
        divider = workspace.AddPhaseDivider(tab.TabId, 55, "b", "Treatment");

        string projectPath = environment.PathFor("manual workflow.garproj");
        DomainResult<ProjectSaveReceipt> saved =
            await workspace.SaveProjectAsync(projectPath, CancellationToken.None);
        Assert.IsTrue(saved.IsSuccess, Format(saved.Errors));
        Assert.AreEqual(originalHash, Sha256(imagePath), "Import and editing must not mutate original image bytes.");
        PointRecord persistedBaseline = workspace.CurrentProject.Panels[0].Points.Single(
            point => point.PointId.Value.ToString("D") == baselinePoint.PointId);
        PointRecord persistedIntervention = workspace.CurrentProject.Panels[0].Points.Single(
            point => point.PointId.Value.ToString("D") == interventionPoint.PointId);
        Assert.AreEqual(1, persistedBaseline.ObservationIndex);
        Assert.AreEqual(1, persistedIntervention.ObservationIndex);
        Assert.IsNull(persistedBaseline.PrintedXValue);
        Assert.IsNull(persistedIntervention.PrintedXValue);
        Assert.AreEqual(PointXSource.Estimated, persistedBaseline.XSource);
        Assert.AreEqual(PointXSource.Estimated, persistedIntervention.XSource);
        Assert.AreEqual(0, persistedBaseline.XConfidence);
        Assert.AreEqual(0, persistedIntervention.XConfidence);
        Assert.AreEqual(2, persistedBaseline.EstimatedXValue!.Value, 0.0001);
        Assert.AreEqual(7, persistedIntervention.EstimatedXValue!.Value, 0.0001);
        Assert.IsNull(workspace.CurrentProject.Panels[0].Calibration!.SessionLattice!.PrintedMin);
        Assert.IsNull(workspace.CurrentProject.Panels[0].Calibration!.SessionLattice!.PrintedMax);

        DomainResult<ProjectSnapshotReceipt> autosaved = await workspace.AutosaveAsync(
            SnapshotTrigger.PointEdited,
            tab.TabId,
            interventionPoint.PointId,
            CancellationToken.None);
        Assert.IsTrue(autosaved.IsSuccess, Format(autosaved.Errors));
        Assert.IsTrue(File.Exists(autosaved.Value!.SnapshotPath));
        Assert.AreEqual(
            autosaved.Value.Snapshot.Audit.LastAutosaveUtc,
            workspace.CurrentProject.Audit.LastAutosaveUtc,
            "A successful event autosave must advance live scheduling state.");

        var recoveryWorkspace = new ManualPreviewWorkspaceService(paths);
        _ = await recoveryWorkspace.OpenProjectAsync(projectPath, CancellationToken.None);
        string recoveredPath = environment.PathFor("manual workflow recovered.garproj");
        DomainResult<ProjectSaveReceipt> recovery = await recoveryWorkspace.RecoverLatestToNewFileAsync(
            recoveredPath,
            CancellationToken.None);
        Assert.IsTrue(recovery.IsSuccess, Format(recovery.Errors));
        Assert.IsTrue(File.Exists(recoveredPath));
        Assert.AreEqual(Path.GetFullPath(projectPath), recoveryWorkspace.CurrentProjectPath);

        var capture = new CapturingExportService();
        var reopenedWorkspace = new ManualPreviewWorkspaceService(paths, exportService: capture);
        IReadOnlyList<GraphReader.App.ViewModels.WorkspaceTabViewModel> reopened =
            await reopenedWorkspace.OpenProjectAsync(projectPath, CancellationToken.None);
        Assert.HasCount(1, reopened);
        Assert.HasCount(2, reopened[0].Points);
        Assert.HasCount(2, reopened[0].SeriesCards);
        Assert.HasCount(1, reopened[0].PhaseDividers);
        Assert.IsNotNull(reopened[0].Calibration);
        Assert.IsTrue(reopenedWorkspace.CurrentProject.Panels[0].Points.All(
            static point => point.PrintedXValue is null && point.XSource == PointXSource.Estimated));
        Assert.AreEqual(1, reopenedWorkspace.CurrentProject.Panels[0].Points.Single(
            point => point.PointId.Value.ToString("D") == interventionPoint.PointId).ObservationIndex);

        string exportRoot = environment.PathFor("CSV output");
        ExportResult exported = await reopenedWorkspace.ExportAsync(
            reopened[0].TabId,
            exportRoot,
            CancellationToken.None);
        Assert.IsTrue(exported.Succeeded, string.Join(" | ", exported.Failures.Select(failure => failure.TechnicalMessage)));
        Assert.AreEqual(1, capture.LastRequest!.Points.Single(
            point => point.PointId.ToString("D") == interventionPoint.PointId).ObservationIndex);
        Assert.HasCount(1, capture.LastRequest.Series.Single(
            series => series.SemanticRole == ExportSeriesRole.Intervention).PointIds);
        Assert.AreEqual(
            Guid.Parse(interventionPoint.PointId),
            capture.LastRequest.Series.Single(series => series.SemanticRole == ExportSeriesRole.Intervention).PointIds[0]);
        Assert.IsNull(capture.LastRequest.Relations.Single().SharedBaselineSeriesId);
        Assert.HasCount(1, exported.MinimalArtifacts);
        Assert.HasCount(1, exported.MinimalArtifacts[0].Rows);
        Assert.AreEqual(
            1,
            exported.MinimalArtifacts[0].Rows[0].XValue,
            "Observation-order export reindexes each intervention artifact while retaining the source observation index in project state.");
        Assert.IsTrue(File.Exists(exported.MinimalArtifacts[0].WrittenPath));
        string csv = await File.ReadAllTextAsync(exported.MinimalArtifacts[0].WrittenPath!);
        StringAssert.StartsWith(csv, ExportContract.MinimalCsvHeader + "\n");
        StringAssert.Contains(csv, ",b\n");
        Assert.AreEqual(ExportMode.ObservationOrder, capture.LastRequest.Mode);
        Assert.IsFalse(capture.LastRequest.Calibration.HasPrintedSessionCalibration);
        Assert.IsNull(capture.LastRequest.Calibration.FirstObservedSession);
        Assert.IsFalse(capture.LastRequest.SessionOriginPolicy.RequireFirstObservedSessionOne);
        Assert.IsNull(capture.LastRequest.Relations.Single().SharedBaselineSeriesId);
        Assert.HasCount(0, capture.LastRequest.Relations.Single().ApplicableProbeSeriesIds);
        Assert.AreEqual(originalHash, Sha256(imagePath));
        Assert.AreEqual(baselinePoint.PointId, reopenedWorkspace.CurrentProject.Panels[0].Points[0].PointId.Value.ToString("D"));
    }

    [TestMethod]
    public async Task ExportUsesOnlyPersistedSeriesRelationsAndNeverAutoAttachesAuxiliarySeries()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        ApplicationPaths paths = CreatePortablePaths(environment, "scientific relations");
        string imagePath = environment.PathFor("relations graph.png");
        WriteImage(imagePath, 100, 100);
        var capture = new CapturingExportService();
        var workspace = new ManualPreviewWorkspaceService(paths, exportService: capture);
        GraphReader.App.ViewModels.WorkspaceTabViewModel tab =
            (await workspace.ImportImagesAsync([imagePath], CancellationToken.None)).Single();
        Calibrate(workspace, tab.TabId);

        var baseline = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Baseline", "□", MarkerShape.Square, MarkerFill.Open, SemanticRole.Baseline));
        var interventionOne = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Intervention One", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        var interventionTwo = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Intervention Two", "○", MarkerShape.Circle, MarkerFill.Open, SemanticRole.Intervention));
        var maintenance = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Maintenance", "△", MarkerShape.TriangleUp, MarkerFill.Open, SemanticRole.Maintenance));
        var generalization = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Generalization", "◇", MarkerShape.Diamond, MarkerFill.Open, SemanticRole.Generalization));
        workspace.AddPoint(tab.TabId, baseline.SeriesId, 20, 80);
        workspace.AddPoint(tab.TabId, interventionOne.SeriesId, 40, 60);
        workspace.AddPoint(tab.TabId, interventionTwo.SeriesId, 50, 50);
        workspace.AddPoint(tab.TabId, maintenance.SeriesId, 70, 40);
        workspace.AddPoint(tab.TabId, generalization.SeriesId, 80, 30);

        string projectPath = environment.PathFor("unlinked relations.garproj");
        DomainResult<ProjectSaveReceipt> saved = await workspace.SaveProjectAsync(projectPath, CancellationToken.None);
        Assert.IsTrue(saved.IsSuccess, Format(saved.Errors));
        ExportResult unlinked = await workspace.ExportAsync(
            tab.TabId,
            environment.PathFor("unlinked export"),
            CancellationToken.None);
        Assert.IsTrue(unlinked.Succeeded, Format(unlinked.Failures));
        Assert.HasCount(2, unlinked.MinimalArtifacts);
        Assert.IsTrue(unlinked.MinimalArtifacts.All(static artifact => artifact.Rows.Count == 1));
        Assert.IsTrue(capture.LastRequest!.Relations.All(
            static relation => relation.SharedBaselineSeriesId is null && relation.ApplicableProbeSeriesIds.Count == 0));
        Assert.IsTrue(unlinked.AuditArtifacts.All(artifact => artifact.Rows.All(
            static row => row.Inclusion == ExportRowInclusion.Intervention)));

        SeriesId baselineId = SeriesId.FromGuid(Guid.Parse(baseline.SeriesId));
        SeriesId maintenanceId = SeriesId.FromGuid(Guid.Parse(maintenance.SeriesId));
        SeriesId generalizationId = SeriesId.FromGuid(Guid.Parse(generalization.SeriesId));
        SeriesId interventionOneId = SeriesId.FromGuid(Guid.Parse(interventionOne.SeriesId));
        SeriesId interventionTwoId = SeriesId.FromGuid(Guid.Parse(interventionTwo.SeriesId));
        int auditEventCount = workspace.CurrentProject.Audit.Events.Count;
        workspace.SetSeriesRelations(
            tab.TabId,
            interventionOne.SeriesId,
            baseline.SeriesId,
            [generalization.SeriesId]);
        workspace.SetSeriesRelations(
            tab.TabId,
            interventionTwo.SeriesId,
            sharedBaselineSeriesId: null,
            [maintenance.SeriesId]);
        Assert.AreEqual(auditEventCount + 2, workspace.CurrentProject.Audit.Events.Count);
        Assert.AreEqual(DomainEventKind.ExportSettingsChanged, workspace.CurrentProject.Audit.Events[^1].Kind);
        SeriesRecord persistedRelationOne = workspace.CurrentProject.Panels.Single().Series.Single(
            series => series.SeriesId == interventionOneId);
        Assert.AreEqual(baselineId, persistedRelationOne.SharedBaselineSeriesId);
        CollectionAssert.AreEqual(
            new[] { generalizationId },
            persistedRelationOne.ApplicableProbeSeriesIds.ToArray());
        SeriesRecord persistedRelationTwo = workspace.CurrentProject.Panels.Single().Series.Single(
            series => series.SeriesId == interventionTwoId);
        Assert.IsNull(persistedRelationTwo.SharedBaselineSeriesId);
        CollectionAssert.AreEqual(
            new[] { maintenanceId },
            persistedRelationTwo.ApplicableProbeSeriesIds.ToArray());

        string explicitProjectPath = environment.PathFor("explicit relations.garproj");
        DomainResult<ProjectSaveReceipt> explicitSaved = await workspace.SaveProjectAsync(
            explicitProjectPath,
            CancellationToken.None);
        Assert.IsTrue(explicitSaved.IsSuccess, Format(explicitSaved.Errors));
        DomainResult<ProjectSnapshotReceipt> relationAutosave = await workspace.AutosaveAsync(
            SnapshotTrigger.ExportSettingsChanged,
            tab.TabId,
            interventionOne.SeriesId,
            CancellationToken.None);
        Assert.IsTrue(relationAutosave.IsSuccess, Format(relationAutosave.Errors));
        SeriesRecord autosavedRelation = relationAutosave.Value!.Snapshot.Panels.Single().Series.Single(
            series => series.SeriesId == interventionOneId);
        Assert.AreEqual(baselineId, autosavedRelation.SharedBaselineSeriesId);
        CollectionAssert.AreEqual(
            new[] { generalizationId },
            autosavedRelation.ApplicableProbeSeriesIds.ToArray());

        var reopenedCapture = new CapturingExportService();
        var reopened = new ManualPreviewWorkspaceService(paths, exportService: reopenedCapture);
        GraphReader.App.ViewModels.WorkspaceTabViewModel reopenedTab =
            (await reopened.OpenProjectAsync(explicitProjectPath, CancellationToken.None)).Single();
        ExportResult related = await reopened.ExportAsync(
            reopenedTab.TabId,
            environment.PathFor("explicit export"),
            CancellationToken.None);
        Assert.IsTrue(related.Succeeded, Format(related.Failures));
        ExportSeriesRelation relationOne = reopenedCapture.LastRequest!.Relations.Single(
            relation => relation.InterventionSeriesId == interventionOneId.Value);
        Assert.AreEqual(baselineId.Value, relationOne.SharedBaselineSeriesId);
        CollectionAssert.AreEqual(new[] { generalizationId.Value }, relationOne.ApplicableProbeSeriesIds.ToArray());
        ExportSeriesRelation relationTwo = reopenedCapture.LastRequest.Relations.Single(
            relation => relation.InterventionSeriesId == interventionTwoId.Value);
        Assert.IsNull(relationTwo.SharedBaselineSeriesId);
        CollectionAssert.AreEqual(new[] { maintenanceId.Value }, relationTwo.ApplicableProbeSeriesIds.ToArray());
        Assert.AreEqual(3, related.MinimalArtifacts.Single(
            artifact => artifact.InterventionSeriesId == interventionOneId.Value).Rows.Count);
        Assert.AreEqual(2, related.MinimalArtifacts.Single(
            artifact => artifact.InterventionSeriesId == interventionTwoId.Value).Rows.Count);
    }

    [TestMethod]
    public async Task ConfirmedProbeSeriesUseSemanticPhaseCodesWithoutCreatingDividers()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        ApplicationPaths paths = CreatePortablePaths(environment, "probe phase semantics");
        string imagePath = environment.PathFor("probe roles graph.png");
        WriteImage(imagePath, 100, 100);
        var workspace = new ManualPreviewWorkspaceService(paths);
        GraphReader.App.ViewModels.WorkspaceTabViewModel tab =
            (await workspace.ImportImagesAsync([imagePath], CancellationToken.None)).Single();
        Calibrate(workspace, tab.TabId);

        var intervention = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Treatment", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        var generalization = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Across setting", "○", MarkerShape.Circle, MarkerFill.Open, SemanticRole.Generalization));
        var maintenance = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Follow-up", "□", MarkerShape.Square, MarkerFill.Open, SemanticRole.Maintenance));
        _ = workspace.AddPhaseDivider(tab.TabId, 50, "b", "Treatment");

        GraphReader.App.Models.GraphPoint interventionPoint =
            workspace.AddPoint(tab.TabId, intervention.SeriesId, 70, 40);
        GraphReader.App.Models.GraphPoint reassignedPoint =
            workspace.AddPoint(tab.TabId, intervention.SeriesId, 80, 30);
        GraphReader.App.Models.GraphPoint generalizationPoint =
            workspace.AddPoint(tab.TabId, generalization.SeriesId, 75, 20);
        GraphReader.App.Models.GraphPoint maintenancePoint =
            workspace.AddPoint(tab.TabId, maintenance.SeriesId, 85, 25);

        Assert.AreEqual("b", interventionPoint.PhaseCode);
        Assert.AreEqual("g", generalizationPoint.PhaseCode);
        Assert.AreEqual("m", maintenancePoint.PhaseCode);
        Assert.HasCount(1, tab.PhaseDividers);

        workspace.ReassignPoint(tab.TabId, reassignedPoint.PointId, generalization.SeriesId);
        Assert.AreEqual("g", reassignedPoint.PhaseCode);
        workspace.ReassignPoint(tab.TabId, reassignedPoint.PointId, maintenance.SeriesId);
        Assert.AreEqual("m", reassignedPoint.PhaseCode);
        workspace.ReassignPoint(tab.TabId, reassignedPoint.PointId, intervention.SeriesId);
        Assert.AreEqual("b", reassignedPoint.PhaseCode);
        workspace.MovePoint(tab.TabId, reassignedPoint.PointId, 40, 30);
        Assert.AreEqual("a", reassignedPoint.PhaseCode);
        workspace.MovePoint(tab.TabId, reassignedPoint.PointId, 80, 30);
        Assert.AreEqual("b", reassignedPoint.PhaseCode);

        workspace.SetSeriesRelations(
            tab.TabId,
            intervention.SeriesId,
            sharedBaselineSeriesId: null,
            [generalization.SeriesId, maintenance.SeriesId]);
        string projectPath = environment.PathFor("probe roles.garproj");
        DomainResult<ProjectSaveReceipt> saved = await workspace.SaveProjectAsync(
            projectPath,
            CancellationToken.None);
        Assert.IsTrue(saved.IsSuccess, Format(saved.Errors));
        Assert.HasCount(1, tab.PhaseDividers, "Probe roles must not invent horizontal phase dividers.");

        var capture = new CapturingExportService();
        var reopened = new ManualPreviewWorkspaceService(paths, exportService: capture);
        GraphReader.App.ViewModels.WorkspaceTabViewModel reopenedTab =
            (await reopened.OpenProjectAsync(projectPath, CancellationToken.None)).Single();
        Assert.HasCount(1, reopenedTab.PhaseDividers, "Semantic probe phases must remain non-divider metadata after reopen.");
        Assert.AreEqual("g", reopenedTab.Points.Single(point => point.PointId == generalizationPoint.PointId).PhaseCode);
        Assert.AreEqual("m", reopenedTab.Points.Single(point => point.PointId == maintenancePoint.PointId).PhaseCode);
        Assert.AreEqual("b", reopenedTab.Points.Single(point => point.PointId == reassignedPoint.PointId).PhaseCode);

        ExportResult exported = await reopened.ExportAsync(
            reopenedTab.TabId,
            environment.PathFor("probe role export"),
            CancellationToken.None);
        Assert.IsTrue(exported.Succeeded, Format(exported.Failures));
        Assert.HasCount(1, exported.MinimalArtifacts);
        Assert.HasCount(4, exported.MinimalArtifacts[0].Rows);
        CollectionAssert.AreEquivalent(
            ExpectedProbePhaseCodes,
            exported.MinimalArtifacts[0].Rows.Select(static row => row.Phase).ToArray());
        ExtendedAuditRow[] probeAuditRows = exported.AuditArtifacts[0].Rows
            .Where(static row => row.Inclusion == ExportRowInclusion.ApplicableProbe)
            .ToArray();
        Assert.HasCount(2, probeAuditRows);
        CollectionAssert.AreEquivalent(
            ExpectedSemanticProbeCodes,
            probeAuditRows.Select(static row => row.Phase).ToArray());

        string[] csvLines = (await File.ReadAllTextAsync(exported.MinimalArtifacts[0].WrittenPath!))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(ExportContract.MinimalCsvHeader, csvLines[0]);
        CollectionAssert.AreEquivalent(
            ExpectedProbePhaseCodes,
            csvLines.Skip(1).Select(static line => line.Split(',')[2]).ToArray());
        Assert.HasCount(1, reopenedTab.PhaseDividers);
    }

    [TestMethod]
    public async Task SeriesRelationApiRejectsInvalidRolesCrossTabReferencesAndSelfLinks()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string firstImage = environment.PathFor("relation validation one.png");
        string secondImage = environment.PathFor("relation validation two.png");
        WriteImage(firstImage, 100, 100);
        WriteImage(secondImage, 100, 100);
        var workspace = new ManualPreviewWorkspaceService();
        IReadOnlyList<GraphReader.App.ViewModels.WorkspaceTabViewModel> tabs =
            await workspace.ImportImagesAsync([firstImage, secondImage], CancellationToken.None);
        var baseline = workspace.AddSeries(
            tabs[0].TabId,
            new ManualSeriesDefinition("Baseline", "□", MarkerShape.Square, MarkerFill.Open, SemanticRole.Baseline));
        var intervention = workspace.AddSeries(
            tabs[0].TabId,
            new ManualSeriesDefinition("Intervention", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        var maintenance = workspace.AddSeries(
            tabs[0].TabId,
            new ManualSeriesDefinition("Maintenance", "△", MarkerShape.TriangleUp, MarkerFill.Open, SemanticRole.Maintenance));
        var otherTabBaseline = workspace.AddSeries(
            tabs[1].TabId,
            new ManualSeriesDefinition("Other baseline", "○", MarkerShape.Circle, MarkerFill.Open, SemanticRole.Baseline));

        Assert.ThrowsExactly<ArgumentException>(() => workspace.SetSeriesRelations(
            tabs[0].TabId,
            baseline.SeriesId,
            sharedBaselineSeriesId: null,
            []));
        Assert.ThrowsExactly<ArgumentException>(() => workspace.SetSeriesRelations(
            tabs[0].TabId,
            intervention.SeriesId,
            maintenance.SeriesId,
            []));
        Assert.ThrowsExactly<ArgumentException>(() => workspace.SetSeriesRelations(
            tabs[0].TabId,
            intervention.SeriesId,
            sharedBaselineSeriesId: null,
            [baseline.SeriesId]));
        Assert.ThrowsExactly<ArgumentException>(() => workspace.SetSeriesRelations(
            tabs[0].TabId,
            intervention.SeriesId,
            intervention.SeriesId,
            []));
        Assert.ThrowsExactly<ArgumentException>(() => workspace.SetSeriesRelations(
            tabs[0].TabId,
            intervention.SeriesId,
            sharedBaselineSeriesId: null,
            [intervention.SeriesId]));
        Assert.ThrowsExactly<KeyNotFoundException>(() => workspace.SetSeriesRelations(
            tabs[0].TabId,
            intervention.SeriesId,
            otherTabBaseline.SeriesId,
            []));

        SeriesRecord unchanged = workspace.CurrentProject.Panels
            .Single(panel => panel.PanelId.Value.ToString("D") == tabs[0].PanelId)
            .Series.Single(series => series.SeriesId.Value.ToString("D") == intervention.SeriesId);
        Assert.IsNull(unchanged.SharedBaselineSeriesId);
        Assert.HasCount(0, unchanged.ApplicableProbeSeriesIds);

        workspace.SetSeriesRelations(
            tabs[0].TabId,
            intervention.SeriesId,
            baseline.SeriesId,
            [maintenance.SeriesId]);
        workspace.UpdateSeries(
            tabs[0].TabId,
            baseline.SeriesId,
            new ManualSeriesDefinition("Former baseline", "□", MarkerShape.Square, MarkerFill.Open, SemanticRole.Unknown));
        SeriesRecord afterBaselineRoleChange = workspace.CurrentProject.Panels
            .Single(panel => panel.PanelId.Value.ToString("D") == tabs[0].PanelId)
            .Series.Single(series => series.SeriesId.Value.ToString("D") == intervention.SeriesId);
        Assert.IsNull(afterBaselineRoleChange.SharedBaselineSeriesId);
        Assert.HasCount(1, afterBaselineRoleChange.ApplicableProbeSeriesIds);

        workspace.UpdateSeries(
            tabs[0].TabId,
            maintenance.SeriesId,
            new ManualSeriesDefinition("Former maintenance", "△", MarkerShape.TriangleUp, MarkerFill.Open, SemanticRole.Unknown));
        SeriesRecord afterProbeRoleChange = workspace.CurrentProject.Panels
            .Single(panel => panel.PanelId.Value.ToString("D") == tabs[0].PanelId)
            .Series.Single(series => series.SeriesId.Value.ToString("D") == intervention.SeriesId);
        Assert.HasCount(0, afterProbeRoleChange.ApplicableProbeSeriesIds);
    }

    [TestMethod]
    public async Task ManualPointObservationIndexTracksHorizontalOrderAfterEveryEdit()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string imagePath = environment.PathFor("point order graph.png");
        WriteImage(imagePath, 100, 100);
        var workspace = new ManualPreviewWorkspaceService();
        GraphReader.App.ViewModels.WorkspaceTabViewModel tab =
            (await workspace.ImportImagesAsync([imagePath], CancellationToken.None)).Single();
        Calibrate(workspace, tab.TabId);
        var firstSeries = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("First", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        var secondSeries = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Second", "○", MarkerShape.Circle, MarkerFill.Open, SemanticRole.Intervention));

        GraphReader.App.Models.GraphPoint right = workspace.AddPoint(tab.TabId, firstSeries.SeriesId, 80, 20);
        GraphReader.App.Models.GraphPoint left = workspace.AddPoint(tab.TabId, firstSeries.SeriesId, 20, 80);
        GraphReader.App.Models.GraphPoint sameXLowerY = workspace.AddPoint(tab.TabId, firstSeries.SeriesId, 50, 30);
        GraphReader.App.Models.GraphPoint sameXHigherY = workspace.AddPoint(tab.TabId, firstSeries.SeriesId, 50, 70);
        _ = workspace.AddPoint(tab.TabId, firstSeries.SeriesId, 50, 30);
        GraphReader.App.Models.GraphPoint secondSeriesPoint = workspace.AddPoint(tab.TabId, secondSeries.SeriesId, 70, 40);

        AssertObservationOrder(workspace, tab, firstSeries.SeriesId);
        Assert.AreEqual(1, left.ObservationIndex, "Out-of-order clicks must be reindexed by original pixel X.");
        Assert.IsTrue(sameXLowerY.ObservationIndex < sameXHigherY.ObservationIndex, "Original pixel Y must break equal-X ties.");
        Assert.AreEqual(5, right.ObservationIndex);

        workspace.MovePoint(tab.TabId, right.PointId, 15, 20);
        AssertObservationOrder(workspace, tab, firstSeries.SeriesId);
        Assert.AreEqual(1, right.ObservationIndex);
        PointRecord moved = FindPersistedPoint(workspace, right.PointId);
        Assert.AreEqual(PointXSource.Estimated, moved.XSource);
        Assert.AreEqual(1.5, moved.EstimatedXValue!.Value, 0.0001, "Estimated GraphX provenance must remain separate from observation order.");

        workspace.DeletePoint(tab.TabId, sameXHigherY.PointId);
        AssertObservationOrder(workspace, tab, firstSeries.SeriesId);

        workspace.ReassignPoint(tab.TabId, sameXLowerY.PointId, secondSeries.SeriesId);
        AssertObservationOrder(workspace, tab, firstSeries.SeriesId);
        AssertObservationOrder(workspace, tab, secondSeries.SeriesId);
        Assert.AreEqual(1, sameXLowerY.ObservationIndex);
        Assert.AreEqual(2, secondSeriesPoint.ObservationIndex);
    }

    [TestMethod]
    public async Task PointMoveAndReassignmentHistoryAppendsAndSurvivesSaveReopen()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        ApplicationPaths paths = CreatePortablePaths(environment, "point history");
        string imagePath = environment.PathFor("point history graph.png");
        WriteImage(imagePath, 100, 100);
        var workspace = new ManualPreviewWorkspaceService(paths);
        GraphReader.App.ViewModels.WorkspaceTabViewModel tab =
            (await workspace.ImportImagesAsync([imagePath], CancellationToken.None)).Single();
        Calibrate(workspace, tab.TabId);
        var sourceSeries = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Source", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        var targetSeries = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Target", "○", MarkerShape.Circle, MarkerFill.Open, SemanticRole.Intervention));
        GraphReader.App.Models.GraphPoint point = workspace.AddPoint(tab.TabId, sourceSeries.SeriesId, 20, 70);

        workspace.MovePoint(tab.TabId, point.PointId, 30, 60);
        workspace.MovePoint(tab.TabId, point.PointId, 40, 50);
        workspace.ReassignPoint(tab.TabId, point.PointId, targetSeries.SeriesId);

        PointRecord edited = FindPersistedPoint(workspace, point.PointId);
        Assert.HasCount(3, edited.ModificationHistory);
        AssertModification(edited.ModificationHistory[0], 20, 70, 2, 25, "Manual point moved");
        AssertModification(edited.ModificationHistory[1], 30, 60, 3, 37.5, "Manual point moved");
        AssertModification(
            edited.ModificationHistory[2],
            40,
            50,
            4,
            50,
            $"Manual point reassigned from series '{sourceSeries.SeriesId}' to '{targetSeries.SeriesId}'");

        string projectPath = environment.PathFor("point history.garproj");
        DomainResult<ProjectSaveReceipt> saved = await workspace.SaveProjectAsync(projectPath, CancellationToken.None);
        Assert.IsTrue(saved.IsSuccess, Format(saved.Errors));
        var reopened = new ManualPreviewWorkspaceService(paths);
        GraphReader.App.ViewModels.WorkspaceTabViewModel reopenedTab =
            (await reopened.OpenProjectAsync(projectPath, CancellationToken.None)).Single();
        PointRecord reopenedPoint = FindPersistedPoint(reopened, point.PointId);
        CollectionAssert.AreEqual(edited.ModificationHistory.ToArray(), reopenedPoint.ModificationHistory.ToArray());

        reopened.MovePoint(reopenedTab.TabId, point.PointId, 45, 45);
        PointRecord appended = FindPersistedPoint(reopened, point.PointId);
        Assert.HasCount(4, appended.ModificationHistory);
        CollectionAssert.AreEqual(
            edited.ModificationHistory.ToArray(),
            appended.ModificationHistory.Take(3).ToArray());
        AssertModification(appended.ModificationHistory[3], 40, 50, 4, 50, "Manual point moved");
    }

    [TestMethod]
    public async Task TimerAutosaveHonorsFiveMinutesAndRecoveryWritesNewFileAfterRestart()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        ApplicationPaths paths = CreatePortablePaths(environment, "timer recovery");
        string imagePath = environment.PathFor("timer graph.png");
        WriteImage(imagePath, 100, 100);
        var workspace = new ManualPreviewWorkspaceService(paths);
        GraphReader.App.ViewModels.WorkspaceTabViewModel tab =
            (await workspace.ImportImagesAsync([imagePath], CancellationToken.None)).Single();
        Calibrate(workspace, tab.TabId);
        var intervention = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition("Intervention", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        string primaryPath = environment.PathFor("timer primary.garproj");
        DomainResult<ProjectSaveReceipt> primarySaved = await workspace.SaveProjectAsync(primaryPath, CancellationToken.None);
        Assert.IsTrue(primarySaved.IsSuccess, Format(primarySaved.Errors));

        workspace.AddPoint(tab.TabId, intervention.SeriesId, 60, 40);
        DomainResult<ProjectSnapshotReceipt> eventSnapshot = await workspace.AutosaveAsync(
            SnapshotTrigger.PointEdited,
            tab.TabId,
            entityId: null,
            CancellationToken.None);
        Assert.IsTrue(eventSnapshot.IsSuccess, Format(eventSnapshot.Errors));
        DateTimeOffset lastAutosave = eventSnapshot.Value!.Snapshot.Audit.LastAutosaveUtc!.Value;

        DomainResult<ProjectSnapshotReceipt> earlyTimer = await workspace.TimerAutosaveAsync(
            lastAutosave + TimeSpan.FromMinutes(4),
            CancellationToken.None);
        Assert.IsFalse(earlyTimer.IsSuccess);
        Assert.IsTrue(earlyTimer.Errors.Any(static error => error.Code == "AUTOSAVE_NOT_DUE"));
        Assert.AreEqual(lastAutosave, workspace.CurrentProject.Audit.LastAutosaveUtc);

        DateTimeOffset dueUtc = lastAutosave + FiveMinuteAutosaveScheduler.DefaultInterval;
        DomainResult<ProjectSnapshotReceipt> dueTimer = await workspace.TimerAutosaveAsync(
            dueUtc,
            CancellationToken.None);
        Assert.IsTrue(dueTimer.IsSuccess, Format(dueTimer.Errors));
        Assert.AreEqual(SnapshotTrigger.Timer, dueTimer.Value!.Trigger);
        Assert.AreEqual(dueUtc, dueTimer.Value.Snapshot.Audit.LastAutosaveUtc);
        Assert.AreEqual(dueUtc, workspace.CurrentProject.Audit.LastAutosaveUtc);

        var restarted = new ManualPreviewWorkspaceService(paths);
        _ = await restarted.OpenProjectAsync(primaryPath, CancellationToken.None);
        string recoveredPath = environment.PathFor("timer recovered.garproj");
        DomainResult<ProjectSaveReceipt> recovered = await restarted.RecoverLatestToNewFileAsync(
            recoveredPath,
            CancellationToken.None);
        Assert.IsTrue(recovered.IsSuccess, Format(recovered.Errors));
        Assert.AreEqual(Path.GetFullPath(primaryPath), restarted.CurrentProjectPath);

        var openedRecovery = new ManualPreviewWorkspaceService(paths);
        IReadOnlyList<GraphReader.App.ViewModels.WorkspaceTabViewModel> recoveredTabs =
            await openedRecovery.OpenProjectAsync(recoveredPath, CancellationToken.None);
        Assert.HasCount(1, recoveredTabs);
        Assert.HasCount(1, recoveredTabs[0].Points);
        Assert.AreEqual(PointXSource.Estimated, openedRecovery.CurrentProject.Panels[0].Points[0].XSource);

        DomainResult<ProjectSaveReceipt> overwriteBlocked = await restarted.RecoverLatestToNewFileAsync(
            recoveredPath,
            CancellationToken.None);
        Assert.IsFalse(overwriteBlocked.IsSuccess);
        Assert.IsTrue(
            overwriteBlocked.Errors.Any(static error => error.Code == "PROJECT_TARGET_EXISTS"),
            Format(overwriteBlocked.Errors));
    }

    private static void AssertObservationOrder(
        ManualPreviewWorkspaceService workspace,
        GraphReader.App.ViewModels.WorkspaceTabViewModel tab,
        string seriesId)
    {
        GraphReader.App.Models.GraphPoint[] expected = tab.Points
            .Where(point => point.SeriesId == seriesId)
            .OrderBy(static point => point.PixelX)
            .ThenBy(static point => point.PixelY)
            .ThenBy(static point => point.PointId, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            Enumerable.Range(1, expected.Length).ToArray(),
            expected.Select(static point => point.ObservationIndex).ToArray());

        Dictionary<string, int> persisted = workspace.CurrentProject.Panels.Single(
                panel => panel.PanelId.Value.ToString("D") == tab.PanelId)
            .Points.Where(point => point.SeriesId?.Value.ToString("D") == seriesId)
            .ToDictionary(
                static point => point.PointId.Value.ToString("D"),
                static point => point.ObservationIndex,
                StringComparer.Ordinal);
        foreach (GraphReader.App.Models.GraphPoint point in expected)
        {
            Assert.AreEqual(point.ObservationIndex, persisted[point.PointId]);
        }
    }

    private static PointRecord FindPersistedPoint(ManualPreviewWorkspaceService workspace, string pointId) =>
        workspace.CurrentProject.Panels.SelectMany(static panel => panel.Points).Single(
            point => point.PointId.Value.ToString("D") == pointId);

    private static void AssertModification(
        PointModification modification,
        double pixelX,
        double pixelY,
        double graphX,
        double graphY,
        string reason)
    {
        Assert.IsNotNull(modification.PreviousPixel);
        Assert.AreEqual(pixelX, modification.PreviousPixel.X, 0.0001);
        Assert.AreEqual(pixelY, modification.PreviousPixel.Y, 0.0001);
        Assert.IsNotNull(modification.PreviousGraph);
        Assert.AreEqual(graphX, modification.PreviousGraph.X, 0.0001);
        Assert.AreEqual(graphY, modification.PreviousGraph.Y, 0.0001);
        Assert.AreEqual(reason, modification.Reason);
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

    private static string Sha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Format(IEnumerable<DomainError> errors) =>
        string.Join(" | ", errors.Select(static error => $"{error.Code}: {error.TechnicalMessage}"));

    private static string Format(IEnumerable<ExportFailure> failures) =>
        string.Join(" | ", failures.Select(static failure => $"{failure.Code}: {failure.TechnicalMessage}"));

    private static ApplicationPaths CreatePortablePaths(
        IntegrationSmokeTestEnvironment environment,
        string folderName)
    {
        string executableRoot = environment.PathFor(folderName);
        Directory.CreateDirectory(executableRoot);
        File.WriteAllText(Path.Combine(executableRoot, "portable.mode"), string.Empty);
        return ApplicationPaths.Create(executableRoot, environment.PathFor("LocalAppData"));
    }

    private static void Calibrate(ManualPreviewWorkspaceService workspace, string tabId) =>
        workspace.Calibrate(
            tabId,
            new ManualCalibrationRequest(
                new GraphReader.Axis.PixelPoint(10, 90),
                new GraphReader.Axis.PixelPoint(10, 10),
                new GraphReader.Axis.PixelPoint(90, 90),
                YMaximum: 100,
                XMaximum: 9));

    private sealed class CapturingExportService : IExportService
    {
        private readonly ExportService _inner = new();

        public ExportRequest? LastRequest { get; private set; }

        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return _inner.ExportAsync(request, cancellationToken);
        }
    }
}
