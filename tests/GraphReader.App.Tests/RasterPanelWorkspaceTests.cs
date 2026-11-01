// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Localization;
using GraphReader.App.Models;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;
using GraphReader.Domain;
using GraphReader.Export;
using GraphReader.Imaging;
using GraphReader.SuperResolution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class RasterPanelWorkspaceTests
{
    private static readonly string[] RequiredStages = ["axis", "ocr", "markers", "legends", "phases"];

    [TestMethod]
    public async Task MultiPanelImageSharesSourceAndRoundTripsCropLocalWorkspaceCoordinates()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = Path.Combine(directory.Path, "stacked.png");
        byte[] sourceBytes = CreateStackedGraphPng(640, 900);
        await File.WriteAllBytesAsync(imagePath, sourceBytes);
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        string projectPath = Path.Combine(directory.Path, "stacked.garproj");

        var workspace = new ManualPreviewWorkspaceService();
        WorkspaceTabViewModel[] tabs = (await workspace.ImportImagesAsync(
            [imagePath],
            CancellationToken.None)).ToArray();

        Assert.IsGreaterThan(1, tabs.Length);
        Assert.HasCount(1, workspace.CurrentProject.Sources);
        Assert.AreEqual(sourceSha256, workspace.CurrentProject.Sources.Single().Sha256);
        Assert.AreEqual(tabs.Length, workspace.CurrentProject.Panels.Count);
        Assert.AreEqual(1, workspace.CurrentProject.Panels.Select(static panel => panel.SourceId).Distinct().Count());
        Assert.IsTrue(workspace.CurrentProject.Panels.All(static panel =>
            panel.Transforms.Count(transform => transform.Kind == TransformKind.Crop) == 1));

        PanelRecord selectedPanel = workspace.CurrentProject.Panels.First(panel => panel.Crop.Y > 0);
        WorkspaceTabViewModel selectedTab = tabs.Single(tab =>
            string.Equals(tab.PanelId, selectedPanel.PanelId.Value.ToString("D"), StringComparison.OrdinalIgnoreCase));
        double localX = 20;
        double localY = 30;
        double dividerX = selectedTab.PixelWidth / 2d;
        _ = workspace.Calibrate(
            selectedTab.TabId,
            new ManualCalibrationRequest(
                new GraphReader.Axis.PixelPoint(10, selectedTab.PixelHeight - 10),
                new GraphReader.Axis.PixelPoint(10, 10),
                new GraphReader.Axis.PixelPoint(selectedTab.PixelWidth - 10, selectedTab.PixelHeight - 10),
                YMaximum: 100,
                XMaximum: 20));
        SeriesCardViewModel series = workspace.AddSeries(
            selectedTab.TabId,
            new ManualSeriesDefinition(
                "Baseline",
                "●",
                MarkerShape.Circle,
                MarkerFill.Filled,
                SemanticRole.Baseline));
        GraphReader.App.Models.GraphPoint point = workspace.AddPoint(
            selectedTab.TabId,
            series.SeriesId,
            localX,
            localY);
        workspace.MovePoint(selectedTab.TabId, point.PointId, localX + 5, localY + 5);
        _ = workspace.AddPhaseDivider(selectedTab.TabId, dividerX, "b", "Intervention");

        PanelRecord persisted = workspace.CurrentProject.Panels.Single(panel => panel.PanelId == selectedPanel.PanelId);
        PointRecord persistedPoint = persisted.Points.Single(item =>
            item.PointId.Value.ToString("D") == point.PointId);
        Assert.AreEqual(localX + 5 + persisted.Crop.X, persistedPoint.OriginalPixel.X, 0);
        Assert.AreEqual(localY + 5 + persisted.Crop.Y, persistedPoint.OriginalPixel.Y, 0);
        Assert.AreEqual(localX + persisted.Crop.X, persistedPoint.ModificationHistory.Single().PreviousPixel!.X, 0);
        Assert.AreEqual(localY + persisted.Crop.Y, persistedPoint.ModificationHistory.Single().PreviousPixel!.Y, 0);
        Assert.AreEqual(10 + persisted.Crop.X, persisted.Calibration!.Anchors[0].Screen.X, 0);
        Assert.AreEqual(selectedTab.PixelHeight - 10 + persisted.Crop.Y, persisted.Calibration.Anchors[0].Screen.Y, 0);
        Assert.AreEqual(persisted.Crop.X, persisted.Phases[0].ScreenXMin, 0);
        Assert.AreEqual(dividerX + persisted.Crop.X, persisted.Phases[0].ScreenXMax, 0);
        AuditEvent moveAudit = workspace.CurrentProject.Audit.Events.Last(auditEvent =>
            auditEvent.Details is JsonElement details &&
            details.TryGetProperty("kind", out JsonElement kind) &&
            kind.GetString() == "production_point_moved");
        JsonElement moveDetails = moveAudit.Details!.Value;
        Assert.AreEqual("original_pixels", moveDetails.GetProperty("coordinate_space").GetString());
        Assert.AreEqual(
            persisted.Transforms.Single(transform => transform.Kind == TransformKind.Crop)
                .TransformId.Value.ToString("D"),
            moveDetails.GetProperty("crop_transform_id").GetString());
        double movedSourceX = moveDetails.GetProperty("original_pixel_x").GetDouble();
        double movedSourceY = moveDetails.GetProperty("original_pixel_y").GetDouble();
        Assert.AreEqual(localX + 5 + persisted.Crop.X, movedSourceX, 0);
        Assert.AreEqual(localY + 5 + persisted.Crop.Y, movedSourceY, 0);
        GraphReader.Domain.PixelPoint replayed = ManualPreviewWorkspaceService.MapPersistedAuditPointToPanel(
            persisted,
            moveDetails,
            movedSourceX,
            movedSourceY);
        Assert.AreEqual(localX + 5, replayed.X, 0);
        Assert.AreEqual(localY + 5, replayed.Y, 0);
        JsonElement legacyDetails = JsonSerializer.SerializeToElement(new
        {
            kind = "production_point_moved",
            original_pixel_x = localX,
            original_pixel_y = localY,
        });
        GraphReader.Domain.PixelPoint legacyReplayed = ManualPreviewWorkspaceService.MapPersistedAuditPointToPanel(
            persisted,
            legacyDetails,
            localX,
            localY);
        Assert.AreEqual(localX, legacyReplayed.X, 0);
        Assert.AreEqual(localY, legacyReplayed.Y, 0);

        DomainResult<ProjectSaveReceipt> saved = await workspace.SaveProjectAsync(
            projectPath,
            CancellationToken.None);
        Assert.IsTrue(saved.IsSuccess, string.Join(" | ", saved.Errors.Select(static error => error.TechnicalMessage)));

        var reopened = new ManualPreviewWorkspaceService();
        WorkspaceTabViewModel[] reopenedTabs = (await reopened.OpenProjectAsync(
            projectPath,
            CancellationToken.None)).ToArray();
        WorkspaceTabViewModel reopenedTab = reopenedTabs.Single(tab => tab.PanelId == selectedTab.PanelId);
        GraphReader.App.Models.GraphPoint reopenedPoint = reopenedTab.Points.Single(item => item.PointId == point.PointId);
        Assert.AreEqual(localX + 5, reopenedPoint.PixelX, 0);
        Assert.AreEqual(localY + 5, reopenedPoint.PixelY, 0);
        Assert.AreEqual(10, reopenedTab.Calibration!.Session1Y0.X, 0);
        Assert.AreEqual(selectedTab.PixelHeight - 10, reopenedTab.Calibration.Session1Y0.Y, 0);
        Assert.AreEqual(dividerX, reopenedTab.PhaseDividers.Single().OriginalX, 0);
        CollectionAssert.AreEqual(
            persisted.Transforms.Single(transform => transform.Kind == TransformKind.Crop).Matrix3x3.ToArray(),
            reopened.CurrentProject.Panels.Single(panel => panel.PanelId == persisted.PanelId)
                .Transforms.Single(transform => transform.Kind == TransformKind.Crop).Matrix3x3.ToArray());
    }

    [TestMethod]
    public async Task CancelledFinalSourceImportDoesNotReplaceCurrentWorkspace()
    {
        using var directory = new TemporaryDirectory();
        string currentImage = Path.Combine(directory.Path, "current.png");
        string targetImage = Path.Combine(directory.Path, "target.png");
        await File.WriteAllBytesAsync(currentImage, CreatePlainPng(96, 64));
        await File.WriteAllBytesAsync(targetImage, CreatePlainPng(80, 60));
        string targetProjectPath = Path.Combine(directory.Path, "target.garproj");

        var importer = new CancelAfterSuccessfulImportService();
        var workspace = new ManualPreviewWorkspaceService(imageImportService: importer);
        WorkspaceTabViewModel currentTab = (await workspace.ImportImagesAsync(
            [currentImage], CancellationToken.None)).Single();
        _ = workspace.Calibrate(currentTab.TabId, new ManualCalibrationRequest(
            new Axis.PixelPoint(5, 55),
            new Axis.PixelPoint(5, 5),
            new Axis.PixelPoint(75, 55),
            YMaximum: 100,
            XMaximum: 7));
        SeriesCardViewModel series = workspace.AddSeries(currentTab.TabId,
            new ManualSeriesDefinition("Manual", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        GraphReader.App.Models.GraphPoint point = workspace.AddPoint(currentTab.TabId, series.SeriesId, 30, 25);
        Guid currentProjectId = workspace.CurrentProject.ProjectId.Value;
        string[] currentTabIds = workspace.CreateWorkspace().Select(static tab => tab.TabId).ToArray();

        var targetWorkspace = new ManualPreviewWorkspaceService();
        _ = await targetWorkspace.ImportImagesAsync([targetImage], CancellationToken.None);
        DomainResult<ProjectSaveReceipt> saved = await targetWorkspace.SaveProjectAsync(
            targetProjectPath,
            CancellationToken.None);
        Assert.IsTrue(saved.IsSuccess, string.Join(" | ", saved.Errors.Select(static error => error.TechnicalMessage)));

        using var cancellation = new CancellationTokenSource();
        importer.CancellationAfterNextSuccessfulImport = cancellation;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            workspace.OpenProjectAsync(targetProjectPath, cancellation.Token));

        Assert.AreEqual(currentProjectId, workspace.CurrentProject.ProjectId.Value);
        CollectionAssert.AreEqual(currentTabIds, workspace.CreateWorkspace().Select(static tab => tab.TabId).ToArray());
        Assert.IsTrue(workspace.CreateWorkspace().Single().Points.Any(item => item.PointId == point.PointId));
    }

    [TestMethod]
    public async Task ReviewPanelIdentityDisambiguatesIdenticalCropImages()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = Path.Combine(directory.Path, "repeated.png");
        await File.WriteAllBytesAsync(imagePath, CreateRepeatedGraphPng());
        var workspace = new ProjectionWorkspace();
        WorkspaceTabViewModel[] tabs = (await workspace.ImportImagesAsync(
            [imagePath],
            CancellationToken.None)).ToArray();
        IGrouping<string?, WorkspaceTabViewModel>? repeated = tabs
            .GroupBy(static tab => tab.SourceSha256, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        Assert.IsNotNull(repeated, "The repeated graph fixture must produce at least two byte-identical panel crops.");

        var store = new ProductionWorkflowPanelStore();
        WorkflowImportSnapshot imported = await new ProductionWorkflowImportStage(store, new ImageImportService())
            .ImportAsync(
                workspace.CreateImportRequest(),
                CancellationToken.None);
        Guid targetId = Guid.Parse(repeated.First().PanelId!);
        WorkflowImportedPanel target = imported.Panels.Single(panel => panel.PanelId == targetId);
        var reviewPanel = new WorkflowReviewPanel(
            new WorkflowPreparedPanel(target, target.Original, enhanced: null),
            points: []);
        ProductionReviewProjectionResult result = workspace.Project(
            new WorkflowRunResult(
                Guid.NewGuid(),
                new WorkflowReviewState(workspace.CurrentProject.ProjectId.Value, [reviewPanel]),
                steps: []),
            store);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Failure!.TechnicalMessage, "no retained exact production projection evidence");
        Assert.IsFalse(result.Failure.TechnicalMessage.Contains("matched 2", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProductionProjectionPersistsEveryPixelGeometryInSourceSpaceExactlyOnce()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = Path.Combine(directory.Path, "projection.png");
        await File.WriteAllBytesAsync(imagePath, CreateStackedGraphPng(640, 900));
        var workspace = new ProjectionWorkspace();
        WorkspaceTabViewModel[] tabs = (await workspace.ImportImagesAsync(
            [imagePath],
            CancellationToken.None)).ToArray();
        WorkspaceTabViewModel tab = tabs.First(candidate =>
            workspace.CurrentProject.Panels.Single(panel => panel.PanelId.Value == Guid.Parse(candidate.PanelId!)).Crop.Y > 0);
        PanelRecord seeded = workspace.CurrentProject.Panels.Single(panel => panel.PanelId.Value == Guid.Parse(tab.PanelId!));

        var store = new ProductionWorkflowPanelStore();
        WorkflowImportSnapshot imported = await new ProductionWorkflowImportStage(store, new ImageImportService())
            .ImportAsync(
                new WorkflowImportRequest(
                    workspace.CurrentProject.ProjectId.Value,
                    [new WorkflowSourceRequest(
                        workspace.CurrentProject.Sources.Single().SourceId.Value,
                        WorkflowSourceKind.Image,
                        imagePath)],
                    enhancementEnabled: false),
                CancellationToken.None);
        WorkflowImportedPanel workflowPanel = imported.Panels.Single(panel => panel.PanelId == Guid.Parse(tab.PanelId!));
        Guid pointId = Guid.NewGuid();
        Guid markerId = Guid.NewGuid();
        Guid seriesId = Guid.NewGuid();
        Guid phase1Id = Guid.NewGuid();
        Guid phase2Id = Guid.NewGuid();
        double dividerX = tab.PixelWidth / 2d;
        var calibration = new CalibrationRecord(
            CalibrationId.New(),
            GraphReader.Domain.CalibrationStatus.Valid,
            [
                new GraphReader.Domain.CalibrationAnchor(GraphReader.Domain.CalibrationAnchorKind.Session1Y0,
                    new GraphReader.Domain.PixelPoint(10, tab.PixelHeight - 10), new GraphReader.Domain.GraphPoint(1, 0), 0.98, null),
                new GraphReader.Domain.CalibrationAnchor(GraphReader.Domain.CalibrationAnchorKind.Session1Ymax,
                    new GraphReader.Domain.PixelPoint(10, 10), new GraphReader.Domain.GraphPoint(1, 100), 0.98, null),
                new GraphReader.Domain.CalibrationAnchor(GraphReader.Domain.CalibrationAnchorKind.SessionmaxY0,
                    new GraphReader.Domain.PixelPoint(tab.PixelWidth - 10, tab.PixelHeight - 10), new GraphReader.Domain.GraphPoint(20, 0), 0.98, null),
            ],
            new SessionLatticeRecord(10, 8, 1, 20, 0.97, "fixture"),
            true,
            0.98,
            []);
        PhaseRecord[] phases =
        [
            new(PhaseId.FromGuid(phase1Id), 1, "a", GraphReader.Domain.PhaseNormalizedType.Baseline, "Baseline",
                0, dividerX, null, PhaseId.FromGuid(phase2Id), 0.96, PhaseSource.Ocr, false),
            new(PhaseId.FromGuid(phase2Id), 2, "b", GraphReader.Domain.PhaseNormalizedType.Intervention, "Intervention",
                dividerX, tab.PixelWidth, PhaseId.FromGuid(phase2Id), null, 0.96, PhaseSource.Ocr, false),
        ];
        var series = new SeriesRecord(
            SeriesId.FromGuid(seriesId), "●", MarkerShape.Circle, MarkerFill.Filled, "Series", SemanticRole.Intervention,
            "Series", [PointId.FromGuid(pointId)], 0.95, null, [], false);
        var modification = new PointModification(
            AuditEventId.New(), DateTimeOffset.UtcNow, new GraphReader.Domain.PixelPoint(17, 27),
            new GraphReader.Domain.GraphPoint(2, 3), "fixture");
        var point = new PointRecord(
            PointId.FromGuid(pointId), MarkerId.FromGuid(markerId), SeriesId.FromGuid(seriesId), PhaseId.FromGuid(phase1Id),
            new GraphReader.Domain.PixelPoint(20, 30), 2, 42, 1, 2, 2, PointXSource.Printed,
            0.94, 0.93, 0.92, "markers", "fixture", ReviewStatus.Unreviewed, [modification]);
        var ocr = new OcrEvidence(
            OcrRegionId.New(),
            [new GraphReader.Domain.PixelPoint(30, 40), new GraphReader.Domain.PixelPoint(50, 40),
             new GraphReader.Domain.PixelPoint(50, 55), new GraphReader.Domain.PixelPoint(30, 55)],
            "label", [], OcrRole.Annotation, 0.91, SourceImageKind.Original, ReviewStatus.Unreviewed);
        var marker = new MarkerRecord(
            MarkerId.FromGuid(markerId), new GraphReader.Domain.PixelPoint(20, 30), 4, MarkerShape.Circle,
            MarkerFill.Filled, "●", 0.01, 0.95, 0.94, 0.93, null, SeriesId.FromGuid(seriesId),
            SourceImageKind.Original, ReviewStatus.Unreviewed);
        Guid filteredMarkerId = Guid.NewGuid();
        MarkerRecord filteredMarker = marker with
        {
            MarkerId = MarkerId.FromGuid(filteredMarkerId),
            Center = new GraphReader.Domain.PixelPoint(65, 75),
            CandidateSeriesId = null,
            ArtifactProbability = 0.8,
            ReviewStatus = ReviewStatus.Rejected,
        };
        var preparation = new TransformRecord(
            TransformId.New(), TransformKind.Scale, CoordinateSpace.OriginalPixels, CoordinateSpace.EnhancedPixels,
            [2, 0, 0, 0, 2, 0, 0, 0, 1], [0.5, 0, 0, 0, 0.5, 0, 0, 0, 1],
            JsonSerializer.SerializeToElement(new { scale = 2 }), false);
        var projection = new ProductionPanelProjectionEvidence(
            calibration, phases, [series], [point], [preparation], [ocr], [marker, filteredMarker], "P1");
        var exportEvidence = new ProductionPanelExportEvidence(
            new ExportCalibration(ExportCalibrationStatus.Valid, true, true, true, 1, 0.98),
            phases.Select(phase => new ExportPhase(
                phase.PhaseId.Value, phase.Order, phase.Code,
                phase.Order == 1 ? ExportPhaseType.Baseline : ExportPhaseType.Intervention,
                phase.LabelText, phase.ScreenXMin, phase.ScreenXMax, phase.Confidence)),
            [new ExportSeries(seriesId, "●", "Series", ExportSeriesRole.Intervention, [pointId], 0.95, "Series")],
            [],
            [new ProductionPointExportEvidence(pointId, markerId, 1, 2, 2, ExportXValueSource.Printed, 0.94, 0.93)],
            [],
            "P1",
            projectionEvidence: projection);
        store.SetExportEvidence(workflowPanel.PanelId, exportEvidence);
        var workflowPoint = new WorkflowPoint(
            pointId.ToString("D"), "fixture:point", 20, 30, 0.92, WorkflowImageVariant.Original,
            WorkflowReviewStatus.Unreviewed, "●", "Circle", "Filled", seriesId.ToString("D"), phase1Id.ToString("D"),
            2, 42, "markers", "fixture", false);
        var reviewPanel = new WorkflowReviewPanel(
            new WorkflowPreparedPanel(workflowPanel, workflowPanel.Original, null), [workflowPoint]);
        var run = new WorkflowRunResult(Guid.NewGuid(),
            new WorkflowReviewState(workspace.CurrentProject.ProjectId.Value, [reviewPanel]), []);

        ProductionReviewProjectionResult first = workspace.Project(run, store);
        Assert.IsTrue(first.Succeeded, first.Failure?.TechnicalMessage);
        Assert.AreEqual(1, first.ProjectedPointCount);
        PanelRecord originalPanel = workspace.CurrentProject.Panels.Single(panel => panel.PanelId == seeded.PanelId);
        PanelRecord linkedPanel = originalPanel with
        {
            Points = [originalPanel.Points.Single() with { MarkerId = MarkerId.FromGuid(filteredMarkerId) }],
        };
        ProjectDocument linkedProject = workspace.CurrentProject with
        {
            Panels = workspace.CurrentProject.Panels.Select(panel => panel.PanelId == seeded.PanelId ? linkedPanel : panel).ToArray(),
        };
        Assert.IsEmpty(WorkspaceReviewProjectionService.ProjectFilteredMarkers(tab, linkedProject),
            "A marker already linked to a point is not an unresolved candidate.");
        AssertProjectionCoordinates(workspace.CurrentProject.Panels.Single(panel => panel.PanelId == seeded.PanelId), seeded.Crop,
            pointId, markerId, tab.PixelWidth, tab.PixelHeight);
        ProductionPanelProjectionEvidence localEvidence = store.Get(workflowPanel.PanelId).ExportEvidence!.ProjectionEvidence!;
        Assert.AreEqual(20, localEvidence.Points.Single().OriginalPixel.X, 0);
        Assert.AreEqual(30, localEvidence.Points.Single().OriginalPixel.Y, 0);

        StaTestHost.Run(() =>
        {
            using var viewModel = new MainWindowViewModel(workspace);
            viewModel.SelectedTab = tab;
            FilteredMarkerCandidateViewModel candidate = viewModel.FilteredMarkers.Single();
            Assert.AreEqual(filteredMarkerId.ToString("D"), candidate.MarkerId);
            Assert.AreEqual(65, candidate.PixelX, 0);
            Assert.AreEqual(75, candidate.PixelY, 0);
            viewModel.SelectedFilteredMarker = candidate;
            Assert.AreEqual(65, viewModel.Magnifier.PixelPosition.X, 0);
            Assert.AreEqual(75, viewModel.Magnifier.PixelPosition.Y, 0);
            Assert.HasCount(1, tab.Points, "Inspection must not create an exportable point.");
            ReviewIssueViewModel issue = viewModel.ReviewIssues.Single(item =>
                item.IssueId == $"point-unreviewed-{pointId:D}");
            viewModel.SelectedReviewIssue = issue;
            Assert.IsNull(viewModel.SelectedFilteredMarker);
            tab.IsDirty = false;
            Assert.IsTrue(viewModel.AcceptReviewPointCommand.CanExecute(null));
            viewModel.AcceptReviewPointCommand.Execute(null);
            Assert.IsTrue(tab.IsDirty);
            Assert.AreEqual(ReviewStatus.Accepted, workspace.CurrentProject.Panels
                .Single(panel => panel.PanelId == seeded.PanelId).Points.Single().ReviewStatus);
            Assert.IsFalse(viewModel.ReviewIssues.Any(item => item.IssueId == issue.IssueId));
            viewModel.UndoCommand.Execute(null);
            Assert.IsFalse(tab.IsDirty);
            Assert.AreEqual(ReviewStatus.Unreviewed, workspace.CurrentProject.Panels
                .Single(panel => panel.PanelId == seeded.PanelId).Points.Single().ReviewStatus);
            viewModel.RedoCommand.Execute(null);
            Assert.IsTrue(tab.IsDirty);
            Assert.AreEqual(ReviewStatus.Accepted, workspace.CurrentProject.Panels
                .Single(panel => panel.PanelId == seeded.PanelId).Points.Single().ReviewStatus);
            viewModel.AddPoint(seriesId.ToString("D"));
            viewModel.UndoCommand.Execute(null);
            Assert.AreEqual(ReviewStatus.Accepted, workspace.CurrentProject.Panels
                .Single(panel => panel.PanelId == seeded.PanelId).Points.Single().ReviewStatus);
        });

        ProductionReviewProjectionResult second = workspace.Project(run, store);
        Assert.IsTrue(second.Succeeded, second.Failure?.TechnicalMessage);
        PanelRecord repeatedPanel = workspace.CurrentProject.Panels.Single(panel => panel.PanelId == seeded.PanelId);
        AssertProjectionCoordinates(repeatedPanel, seeded.Crop,
            pointId, markerId, tab.PixelWidth, tab.PixelHeight);
        Assert.AreEqual(PhaseId.FromGuid(phase1Id), repeatedPanel.Points.Single().PhaseId);
        Assert.AreEqual(ReviewStatus.Accepted, repeatedPanel.Points.Single().ReviewStatus);
        Assert.IsTrue(repeatedPanel.Phases.Any(phase => phase.PhaseId.Value == phase1Id));

        string projectPath = Path.Combine(directory.Path, "projection-roundtrip.garproj");
        DomainResult<ProjectSaveReceipt> saved = await workspace.SaveProjectAsync(projectPath, CancellationToken.None);
        Assert.IsTrue(saved.IsSuccess, string.Join(" | ", saved.Errors.Select(static error => error.TechnicalMessage)));
        var reopened = new ManualPreviewWorkspaceService();
        _ = await reopened.OpenProjectAsync(projectPath, CancellationToken.None);
        PanelRecord reopenedPanel = reopened.CurrentProject.Panels.Single(panel => panel.PanelId == seeded.PanelId);
        FilteredMarkerCandidateViewModel reopenedCandidate = WorkspaceReviewProjectionService
            .ProjectFilteredMarkers(reopened.CreateWorkspace().Single(item => item.PanelId == tab.PanelId), reopened.CurrentProject).Single();
        Assert.AreEqual(filteredMarkerId.ToString("D"), reopenedCandidate.MarkerId);
        Assert.AreEqual(65, reopenedCandidate.PixelX, 0);
        Assert.AreEqual(75, reopenedCandidate.PixelY, 0);
        WorkspaceTabViewModel reopenedTab = reopened.CreateWorkspace().Single(item => item.PanelId == tab.PanelId);
        reopened.MovePoint(reopenedTab.TabId, pointId.ToString("D"), 64.5, 75);
        Assert.Throws<InvalidOperationException>(() => reopened.PromoteFilteredMarker(
            reopenedTab.TabId, filteredMarkerId.ToString("D"), seriesId.ToString("D")));
        reopened.MovePoint(reopenedTab.TabId, pointId.ToString("D"), 63.9, 75);
        GraphReader.App.Models.GraphPoint adjacent = reopened.PromoteFilteredMarker(
            reopenedTab.TabId, filteredMarkerId.ToString("D"), seriesId.ToString("D"));
        Assert.AreEqual(65, adjacent.PixelX, 0);
        Assert.AreEqual(ReviewStatus.Accepted, reopenedPanel.Points.Single().ReviewStatus);
        Assert.AreEqual(PhaseId.FromGuid(phase1Id), reopenedPanel.Points.Single().PhaseId);
        Assert.IsTrue(reopenedPanel.Phases.Any(phase => phase.PhaseId.Value == phase1Id));
        AssertProjectionCoordinates(reopenedPanel, seeded.Crop,
            pointId, markerId, tab.PixelWidth, tab.PixelHeight);

        var approvedStages = RequiredStages
            .Select(stage => new AutomaticStageStatus(stage, AutomaticStageState.Approved, "Fixture"))
            .ToArray();
        var uiProduction = new ProductionWorkspaceService(automaticStages: approvedStages);
        WorkspaceTabViewModel uiTab = (await uiProduction.OpenProjectAsync(projectPath, CancellationToken.None))
            .Single(item => item.PanelId == tab.PanelId);
        StaTestHost.Run(() =>
        {
            using var viewModel = new MainWindowViewModel(uiProduction,
                new LocalizationService(new System.Windows.ResourceDictionary()));
            viewModel.SelectedTab = uiTab;
            Assert.IsTrue(viewModel.AutoDetectCommand.CanExecute(null));
            viewModel.SelectedFilteredMarker = viewModel.FilteredMarkers.Single();
            viewModel.SelectedSeriesId = seriesId.ToString("D");
            viewModel.PromoteFilteredMarkerCommand.Execute(null);
            Assert.IsTrue(viewModel.AutoDetectCommand.CanExecute(null));
            viewModel.UndoCommand.Execute(null);
            Assert.IsTrue(viewModel.AutoDetectCommand.CanExecute(null));
            viewModel.RedoCommand.Execute(null);
            Assert.IsTrue(viewModel.AutoDetectCommand.CanExecute(null));
        });

        string promotedPointId = string.Empty;
        StaTestHost.Run(() =>
        {
            using var viewModel = new MainWindowViewModel(workspace);
            viewModel.SelectedTab = tab;
            viewModel.SelectedFilteredMarker = viewModel.FilteredMarkers.Single();
            viewModel.SelectedSeriesId = null;
            Assert.IsFalse(viewModel.PromoteFilteredMarkerCommand.CanExecute(null));
            viewModel.SelectedSeriesId = seriesId.ToString("D");
            Assert.IsTrue(viewModel.PromoteFilteredMarkerCommand.CanExecute(null));
            viewModel.PromoteFilteredMarkerCommand.Execute(null);
            PanelRecord panel = workspace.CurrentProject.Panels.Single(item => item.PanelId == seeded.PanelId);
            PointRecord promoted = panel.Points.Single(item => item.PointId.Value != pointId);
            promotedPointId = promoted.PointId.Value.ToString("D");
            Assert.AreEqual(MarkerId.FromGuid(filteredMarkerId), promoted.MarkerId);
            Assert.AreEqual("manual", promoted.SourceStage);
            Assert.AreEqual(ReviewStatus.Corrected, promoted.ReviewStatus);
            Assert.AreEqual(PointXSource.Estimated, promoted.XSource);
            Assert.IsEmpty(viewModel.FilteredMarkers);
            Assert.IsFalse(viewModel.PromoteFilteredMarkerCommand.CanExecute(null));

            viewModel.UndoCommand.Execute(null);
            Assert.HasCount(1, workspace.CurrentProject.Panels.Single(item => item.PanelId == seeded.PanelId).Points);
            Assert.HasCount(1, viewModel.FilteredMarkers);
            viewModel.RedoCommand.Execute(null);
            PointRecord restored = workspace.CurrentProject.Panels.Single(item => item.PanelId == seeded.PanelId)
                .Points.Single(item => item.PointId.Value.ToString("D") == promotedPointId);
            Assert.AreEqual(MarkerId.FromGuid(filteredMarkerId), restored.MarkerId);
            Assert.AreEqual(ReviewStatus.Corrected, restored.ReviewStatus);
            Assert.IsEmpty(viewModel.FilteredMarkers);
        });
        store.SetExportEvidence(workflowPanel.PanelId, exportEvidence);
        ProductionReviewProjectionResult replayed = workspace.Project(run, store);
        Assert.IsTrue(replayed.Succeeded, replayed.Failure?.TechnicalMessage);
        Assert.HasCount(2, workspace.CurrentProject.Panels.Single(item => item.PanelId == seeded.PanelId).Points);
        string promotedProjectPath = Path.Combine(directory.Path, "projection-promoted.garproj");
        DomainResult<ProjectSaveReceipt> promotedSave = await workspace.SaveProjectAsync(promotedProjectPath, CancellationToken.None);
        Assert.IsTrue(promotedSave.IsSuccess, string.Join(" | ", promotedSave.Errors.Select(static error => error.TechnicalMessage)));
        var promotedReopen = new ProjectionWorkspace();
        _ = await promotedReopen.OpenProjectAsync(promotedProjectPath, CancellationToken.None);
        PointRecord reopenedPromoted = promotedReopen.CurrentProject.Panels.Single(item => item.PanelId == seeded.PanelId)
            .Points.Single(item => item.PointId.Value.ToString("D") == promotedPointId);
        Assert.AreEqual(MarkerId.FromGuid(filteredMarkerId), reopenedPromoted.MarkerId);
        Assert.AreEqual(ReviewStatus.Corrected, reopenedPromoted.ReviewStatus);
        Assert.IsTrue(promotedReopen.CurrentProject.Audit.Events.Any(entry =>
            entry.Details is { ValueKind: JsonValueKind.Object } details &&
            details.TryGetProperty("point_id", out JsonElement auditPoint) && auditPoint.GetString() == promotedPointId &&
            details.TryGetProperty("promoted_marker_id", out JsonElement auditMarker) &&
            auditMarker.GetString() == filteredMarkerId.ToString("D")));
        store.SetExportEvidence(workflowPanel.PanelId, exportEvidence);
        ProductionReviewProjectionResult reopenedReplay = promotedReopen.Project(run, store);
        Assert.IsTrue(reopenedReplay.Succeeded, reopenedReplay.Failure?.TechnicalMessage);
        Assert.HasCount(2, promotedReopen.CurrentProject.Panels.Single(item => item.PanelId == seeded.PanelId).Points);

        Guid automaticPointId = Guid.NewGuid();
        PointRecord automaticPoint = point with
        {
            PointId = PointId.FromGuid(automaticPointId), MarkerId = MarkerId.FromGuid(filteredMarkerId),
            OriginalPixel = filteredMarker.Center, ObservationIndex = 2, ModificationHistory = [],
        };
        SeriesRecord detectedSeries = series with { PointIds = [PointId.FromGuid(pointId), PointId.FromGuid(automaticPointId)] };
        WorkflowPoint automaticWorkflowPoint = workflowPoint with
        {
            PointId = automaticPointId.ToString("D"), DetectionKey = "fixture:promoted-marker",
            OriginalPixelX = 65, OriginalPixelY = 75,
        };
        var exactProjection = new ProductionPanelProjectionEvidence(calibration, phases, [detectedSeries],
            [point, automaticPoint], [preparation], [ocr], [marker, filteredMarker], "P1");
        var exactExport = new ProductionPanelExportEvidence(exportEvidence.Calibration, exportEvidence.Phases,
            [new ExportSeries(seriesId, "●", "Series", ExportSeriesRole.Intervention, [pointId, automaticPointId], 0.95, "Series")],
            [], [exportEvidence.Points.Single(), new ProductionPointExportEvidence(automaticPointId, filteredMarkerId,
                2, 2, 2, ExportXValueSource.Printed, 0.94, 0.93)], [], "P1", projectionEvidence: exactProjection);
        var exactPanel = new WorkflowReviewPanel(reviewPanel.PreparedPanel, [workflowPoint, automaticWorkflowPoint]);
        var exactRun = new WorkflowRunResult(Guid.NewGuid(),
            new WorkflowReviewState(workspace.CurrentProject.ProjectId.Value, [exactPanel]), []);
        store.SetExportEvidence(workflowPanel.PanelId, exactExport);
        ProductionReviewProjectionResult exactReplay = promotedReopen.Project(exactRun, store);
        Assert.IsTrue(exactReplay.Succeeded, exactReplay.Failure?.TechnicalMessage);
        PointRecord[] finalPoints = promotedReopen.CurrentProject.Panels.Single(item => item.PanelId == seeded.PanelId).Points.ToArray();
        Assert.HasCount(2, finalPoints);
        Assert.IsFalse(finalPoints.Any(item => item.PointId.Value == automaticPointId));
        Assert.IsTrue(finalPoints.Any(item => item.PointId.Value.ToString("D") == promotedPointId &&
            item.MarkerId == MarkerId.FromGuid(filteredMarkerId) && item.SourceStage == "manual"));

        var missingProjection = new ProductionPanelProjectionEvidence(calibration, phases, [series], [point],
            [preparation], [ocr], [marker], "P1");
        var missingExport = new ProductionPanelExportEvidence(exportEvidence.Calibration, exportEvidence.Phases,
            exportEvidence.Series, exportEvidence.Relations, exportEvidence.Points, exportEvidence.Provenance,
            "P1", projectionEvidence: missingProjection);
        store.SetExportEvidence(workflowPanel.PanelId, missingExport);
        ProjectDocument beforeConflict = promotedReopen.CurrentProject;
        ProductionReviewProjectionResult missingReplay = promotedReopen.Project(run, store);
        Assert.IsFalse(missingReplay.Succeeded);
        Assert.AreEqual(LocalizationKeys.PromotedMarkerAnalysisBlocked, missingReplay.Failure!.UserMessageKey);
        Assert.AreSame(beforeConflict, promotedReopen.CurrentProject);

        MarkerRecord overlappingMarker = filteredMarker with
        {
            MarkerId = MarkerId.New(), Center = new GraphReader.Domain.PixelPoint(65.5, 75),
        };
        PointRecord overlappingPoint = automaticPoint with
        {
            MarkerId = overlappingMarker.MarkerId, OriginalPixel = overlappingMarker.Center,
        };
        var overlapProjection = new ProductionPanelProjectionEvidence(calibration, phases, [detectedSeries],
            [point, overlappingPoint], [preparation], [ocr], [marker, filteredMarker, overlappingMarker], "P1");
        var overlapExport = new ProductionPanelExportEvidence(exactExport.Calibration, exactExport.Phases,
            exactExport.Series, exactExport.Relations, exactExport.Points, exactExport.Provenance,
            "P1", projectionEvidence: overlapProjection);
        store.SetExportEvidence(workflowPanel.PanelId, overlapExport);
        ProductionReviewProjectionResult overlapReplay = promotedReopen.Project(exactRun, store);
        Assert.IsFalse(overlapReplay.Succeeded);
        Assert.AreEqual(LocalizationKeys.PromotedMarkerAnalysisBlocked, overlapReplay.Failure!.UserMessageKey);
        Assert.AreSame(beforeConflict, promotedReopen.CurrentProject);

        var duplicateProjection = new ProductionPanelProjectionEvidence(calibration, phases, [series], [point],
            [preparation], [ocr], [marker, filteredMarker, filteredMarker], "P1");
        var duplicateExport = new ProductionPanelExportEvidence(exportEvidence.Calibration, exportEvidence.Phases,
            exportEvidence.Series, exportEvidence.Relations, exportEvidence.Points, exportEvidence.Provenance,
            "P1", projectionEvidence: duplicateProjection);
        store.SetExportEvidence(workflowPanel.PanelId, duplicateExport);
        ProductionReviewProjectionResult duplicateReplay = promotedReopen.Project(run, store);
        Assert.IsFalse(duplicateReplay.Succeeded);
        Assert.AreEqual(LocalizationKeys.PromotedMarkerAnalysisBlocked, duplicateReplay.Failure!.UserMessageKey);
        Assert.AreSame(beforeConflict, promotedReopen.CurrentProject);
    }

    [TestMethod]
    public async Task LegacySingleFullImageProjectReopensWithoutRasterCropTransform()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = Path.Combine(directory.Path, "legacy.png");
        byte[] sourceBytes = CreatePlainPng(96, 64);
        await File.WriteAllBytesAsync(imagePath, sourceBytes);
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        string projectPath = Path.Combine(directory.Path, "legacy.garproj");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProjectDocument project = ProjectDocument.Create("0.0.1", now);
        SourceId sourceId = SourceId.New();
        PanelId legacyPanelId = PanelId.New();
        project = project with
        {
            Sources =
            [
                new SourceReference(
                    sourceId,
                    SourceKind.Image,
                    "legacy.png",
                    imagePath,
                    sourceSha256,
                    ArticleMetadata: null),
            ],
            Panels =
            [
                new PanelRecord(
                    legacyPanelId,
                    sourceId,
                    PageNumber: null,
                    "Legacy panel",
                    Participant: null,
                    new CropRectangle(0, 0, 96, 64),
                    Transforms:
                    [
                        new TransformRecord(
                            TransformId.New(),
                            TransformKind.Crop,
                            CoordinateSpace.OriginalPixels,
                            CoordinateSpace.PanelPixels,
                            [1, 0, 0, 0, 1, 0, 0, 0, 1],
                            [1, 0, 0, 0, 1, 0, 0, 0, 1],
                            JsonSerializer.SerializeToElement(new { legacy = true }),
                            Lossy: false),
                    ],
                    Enhancement: null,
                    Calibration: null,
                    OcrRegions: [],
                    Markers: [],
                    Series: [],
                    Points: [],
                    Phases: [],
                    ExportSettings: null,
                    Validation: null),
            ],
        };
        var store = new ProjectFileStore();
        DomainResult<ProjectSaveReceipt> saved = await store.SaveAsync(project, projectPath, CancellationToken.None);
        Assert.IsTrue(saved.IsSuccess, string.Join(" | ", saved.Errors.Select(static error => error.TechnicalMessage)));

        var workspace = new ManualPreviewWorkspaceService(projectFileStore: store);
        WorkspaceTabViewModel reopened = (await workspace.OpenProjectAsync(
            projectPath,
            CancellationToken.None)).Single();

        Assert.AreEqual(legacyPanelId.Value.ToString("D"), reopened.PanelId);
        Assert.AreEqual(96, reopened.PixelWidth);
        Assert.AreEqual(64, reopened.PixelHeight);
        Assert.HasCount(1, workspace.CurrentProject.Panels.Single().Transforms);
        Assert.IsTrue(workspace.CurrentProject.Panels.Single().Transforms[0].Parameters.GetProperty("legacy").GetBoolean());
    }

    [TestMethod]
    public async Task InvalidFullBoundsRasterProvenanceDoesNotReplaceOpenWorkspace()
    {
        using var directory = new TemporaryDirectory();
        string retainedPath = Path.Combine(directory.Path, "retained.png");
        string currentPath = Path.Combine(directory.Path, "current.png");
        await File.WriteAllBytesAsync(retainedPath, CreatePlainPng(96, 64));
        await File.WriteAllBytesAsync(currentPath, CreatePlainPng(112, 72));

        var sourceWorkspace = new ManualPreviewWorkspaceService();
        _ = await sourceWorkspace.ImportImagesAsync([retainedPath], CancellationToken.None);
        PanelRecord fullPanel = sourceWorkspace.CurrentProject.Panels.Single();
        Assert.AreEqual(new CropRectangle(0, 0, 96, 64), fullPanel.Crop);
        ProjectDocument invalid = sourceWorkspace.CurrentProject with
        {
            Panels =
            [
                fullPanel with
                {
                    Transforms = fullPanel.Transforms.Select(transform => transform with { Lossy = true }).ToArray(),
                },
            ],
        };
        string projectPath = Path.Combine(directory.Path, "invalid.garproj");
        var projectStore = new ProjectFileStore();
        DomainResult<ProjectSaveReceipt> saved = await projectStore.SaveAsync(invalid, projectPath, CancellationToken.None);
        Assert.IsTrue(saved.IsSuccess, string.Join(" | ", saved.Errors.Select(static error => error.TechnicalMessage)));

        var workspace = new ManualPreviewWorkspaceService(projectFileStore: projectStore);
        WorkspaceTabViewModel currentTab = (await workspace.ImportImagesAsync(
            [currentPath], CancellationToken.None)).Single();
        ProjectId currentProjectId = workspace.CurrentProject.ProjectId;
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.OpenProjectAsync(
            projectPath, CancellationToken.None));

        Assert.AreEqual(currentProjectId, workspace.CurrentProject.ProjectId);
        Assert.HasCount(1, workspace.CreateWorkspace());
        Assert.AreSame(currentTab, workspace.CreateWorkspace().Single());
    }

    [TestMethod]
    public async Task EnhancementReceivesChecksumBoundPanelCropInsteadOfFullSource()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = Path.Combine(directory.Path, "enhance-source.png");
        byte[] sourceBytes = CreateStackedGraphPng(640, 900);
        await File.WriteAllBytesAsync(imagePath, sourceBytes);
        var enhancement = new CapturingEnhancementService();
        var model = new EnhancementModel(
            "fixture-model", "1", new string('a', 64), "fixture", "fixture", "MIT", "NOTICE", []);
        var resolution = new RealEsrganBackendResolution(
            RealEsrganBackendAvailability.AvailableForLocalEvaluationOnly,
            model,
            Configuration: null,
            enhancement,
            Diagnostic: null,
            ReleaseEligible: false);
        var workspace = new ManualPreviewWorkspaceService(
            applicationPaths: new TestApplicationPaths(directory.Path),
            enhancementResolver: _ => Task.FromResult(resolution));
        WorkspaceTabViewModel tab = (await workspace.ImportImagesAsync(
            [imagePath], CancellationToken.None)).First(candidate => candidate.PixelHeight < 900);

        WorkspaceEnhancementResult result = await workspace.EnhanceAsync(tab.TabId, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(enhancement.Request);
        Assert.AreNotEqual(Path.GetFullPath(imagePath), enhancement.Request.InputPath);
        Assert.AreEqual(tab.PixelWidth, enhancement.Request.SourceDimensions.Width);
        Assert.AreEqual(tab.PixelHeight, enhancement.Request.SourceDimensions.Height);
        byte[] materialized = await File.ReadAllBytesAsync(enhancement.Request.InputPath);
        Assert.AreEqual(tab.SourceSha256, Convert.ToHexStringLower(SHA256.HashData(materialized)));
        Assert.IsFalse(sourceBytes.SequenceEqual(materialized));
        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(imagePath));
    }

    private static void AssertProjectionCoordinates(
        PanelRecord panel,
        CropRectangle crop,
        Guid pointId,
        Guid markerId,
        int panelWidth,
        int panelHeight)
    {
        Assert.AreEqual("P1", panel.Participant);
        Assert.AreEqual(crop, panel.Crop);
        PointRecord point = panel.Points.Single(item => item.PointId.Value == pointId);
        Assert.AreEqual(20 + crop.X, point.OriginalPixel.X, 0);
        Assert.AreEqual(30 + crop.Y, point.OriginalPixel.Y, 0);
        Assert.AreEqual(2, point.GraphX);
        Assert.AreEqual(42, point.GraphY);
        Assert.AreEqual(17 + crop.X, point.ModificationHistory.Single().PreviousPixel!.X, 0);
        Assert.AreEqual(27 + crop.Y, point.ModificationHistory.Single().PreviousPixel!.Y, 0);
        Assert.AreEqual(2, point.ModificationHistory.Single().PreviousGraph!.X, 0);
        Assert.AreEqual(3, point.ModificationHistory.Single().PreviousGraph!.Y, 0);
        Assert.AreEqual(10 + crop.X, panel.Calibration!.Anchors[0].Screen.X, 0);
        Assert.AreEqual(panelHeight - 10 + crop.Y, panel.Calibration.Anchors[0].Screen.Y, 0);
        Assert.AreEqual(10 + crop.X, panel.Calibration.SessionLattice!.Session1PixelX, 0);
        Assert.HasCount(2, panel.Phases);
        Assert.AreEqual(crop.X, panel.Phases[0].ScreenXMin, 0);
        Assert.AreEqual(panelWidth / 2d + crop.X, panel.Phases[0].ScreenXMax, 0);
        Assert.AreEqual(panelWidth / 2d + crop.X, panel.Phases[1].ScreenXMin, 0);
        Assert.AreEqual(panelWidth + crop.X, panel.Phases[1].ScreenXMax, 0);
        Assert.AreEqual(30 + crop.X, panel.OcrRegions.Single().Polygon[0].X, 0);
        Assert.AreEqual(40 + crop.Y, panel.OcrRegions.Single().Polygon[0].Y, 0);
        MarkerRecord marker = panel.Markers.Single(item => item.MarkerId.Value == markerId);
        Assert.AreEqual(20 + crop.X, marker.Center.X, 0);
        Assert.AreEqual(30 + crop.Y, marker.Center.Y, 0);
        Assert.HasCount(2, panel.Transforms);
        TransformRecord preparation = panel.Transforms.Single(transform => transform.Kind == TransformKind.Scale);
        Assert.AreEqual(CoordinateSpace.PanelPixels, preparation.SourceSpace);
        Assert.AreEqual(CoordinateSpace.EnhancedPixels, preparation.TargetSpace);
    }

    private static byte[] CreateStackedGraphPng(int width, int height)
    {
        byte[] pixels = CreateWhiteScanlines(width, height);
        DrawRepeatedGraphs(pixels, width, height, [270, 530, 790]);
        return EncodeGrayscalePng(width, height, pixels);
    }

    private static byte[] CreateRepeatedGraphPng()
    {
        const int width = 640;
        const int height = 1500;
        byte[] pixels = CreateWhiteScanlines(width, height);
        DrawRepeatedGraphs(pixels, width, height, [250, 500, 750, 1000, 1250]);
        return EncodeGrayscalePng(width, height, pixels);
    }

    private static void DrawRepeatedGraphs(
        byte[] pixels,
        int width,
        int height,
        IEnumerable<int> baselines)
    {
        foreach (int baseline in baselines)
        {
            DrawHorizontal(pixels, width, height, 80, 580, baseline, 2);
            DrawVertical(pixels, width, height, 80, baseline - 180, baseline, 2);
            DrawHorizontal(pixels, width, height, 120, 220, baseline - 60, 1);
            DrawHorizontal(pixels, width, height, 220, 340, baseline - 110, 1);
            DrawHorizontal(pixels, width, height, 340, 460, baseline - 80, 1);
            DrawVertical(pixels, width, height, 300, baseline - 150, baseline, 1);
        }
    }

    private static byte[] CreatePlainPng(int width, int height) =>
        EncodeGrayscalePng(width, height, CreateWhiteScanlines(width, height));

    private static byte[] CreateWhiteScanlines(int width, int height)
    {
        byte[] scanlines = new byte[height * (width + 1)];
        Array.Fill(scanlines, byte.MaxValue);
        for (var y = 0; y < height; y++)
        {
            scanlines[y * (width + 1)] = 0;
        }

        return scanlines;
    }

    private static void DrawHorizontal(
        byte[] scanlines,
        int width,
        int height,
        int xMinimum,
        int xMaximum,
        int y,
        int thickness)
    {
        for (var offset = 0; offset < thickness; offset++)
        {
            int row = Math.Clamp(y + offset, 0, height - 1);
            for (int x = Math.Max(0, xMinimum); x <= Math.Min(width - 1, xMaximum); x++)
            {
                scanlines[(row * (width + 1)) + x + 1] = 0;
            }
        }
    }

    private static void DrawVertical(
        byte[] scanlines,
        int width,
        int height,
        int x,
        int yMinimum,
        int yMaximum,
        int thickness)
    {
        for (var offset = 0; offset < thickness; offset++)
        {
            int column = Math.Clamp(x + offset, 0, width - 1);
            for (int y = Math.Max(0, yMinimum); y <= Math.Min(height - 1, yMaximum); y++)
            {
                scanlines[(y * (width + 1)) + column + 1] = 0;
            }
        }
    }

    private static byte[] EncodeGrayscalePng(int width, int height, byte[] scanlines)
    {
        byte[] compressed;
        using (var compressedStream = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(scanlines);
            }

            compressed = compressedStream.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 0;
        WritePngChunk(png, "IHDR", header);
        WritePngChunk(png, "IDAT", compressed);
        WritePngChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WritePngChunk(Stream target, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        target.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        target.Write(typeBytes);
        target.Write(data);
        byte[] checksumInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(checksumInput, 0);
        data.CopyTo(checksumInput.AsSpan(typeBytes.Length));
        Span<byte> checksum = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ComputePngCrc32(checksumInput));
        target.Write(checksum);
    }

    private static uint ComputePngCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                uint mask = unchecked((uint)-(int)(crc & 1));
                crc = (crc >> 1) ^ (0xedb88320u & mask);
            }
        }

        return ~crc;
    }

    private sealed class ProjectionWorkspace : ManualPreviewWorkspaceService
    {
        public WorkflowImportRequest CreateImportRequest() =>
            CreateProductionWorkflowImportRequest(enhancementEnabled: false);

        public ProductionReviewProjectionResult Project(
            WorkflowRunResult result,
            ProductionWorkflowPanelStore store) =>
            ProjectProductionReview(result, store);
    }

    private sealed class CancelAfterSuccessfulImportService : IImageImportService
    {
        private readonly ImageImportService inner = new();

        public CancellationTokenSource? CancellationAfterNextSuccessfulImport { get; set; }

        public async Task<ImageImportResult> ImportAsync(string path, CancellationToken cancellationToken)
        {
            ImageImportResult result = await inner.ImportAsync(path, cancellationToken);
            if (result.IsSuccess && CancellationAfterNextSuccessfulImport is { } cancellation)
            {
                CancellationAfterNextSuccessfulImport = null;
                cancellation.Cancel();
            }
            return result;
        }

        public Task<BatchImportResult> ImportBatchAsync(
            IEnumerable<string> paths,
            CancellationToken cancellationToken) => inner.ImportBatchAsync(paths, cancellationToken);
    }

    private sealed class CapturingEnhancementService : IEnhancementService
    {
        public EnhancementRequest? Request { get; private set; }

        public Task<EnhancementResult> EnhanceAsync(
            EnhancementRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(new EnhancementResult(
                EnhancementStatus.Failed,
                OutputPath: null,
                new EnhancementDiagnostic(EnhancementFailureCode.ProcessFailed, "fixture failure"),
                Envelope: null,
                MayContinueUnenhanced: true));
        }
    }

    private sealed class TestApplicationPaths(string root) : IApplicationPaths
    {
        public DistributionMode Mode => DistributionMode.Portable;
        public string SettingsRoot => System.IO.Path.Combine(root, "Settings");
        public string CacheRoot => System.IO.Path.Combine(root, "Cache");
        public string LogsRoot => System.IO.Path.Combine(root, "Logs");
        public string AutosaveRoot => System.IO.Path.Combine(root, "Autosave");
        public string RecoveryRoot => System.IO.Path.Combine(root, "Recovery");
        public string ModelRoot => System.IO.Path.Combine(root, "Models");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "graph-reader-raster-workspace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
