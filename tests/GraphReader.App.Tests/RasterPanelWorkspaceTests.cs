// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
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
        var preparation = new TransformRecord(
            TransformId.New(), TransformKind.Scale, CoordinateSpace.OriginalPixels, CoordinateSpace.EnhancedPixels,
            [2, 0, 0, 0, 2, 0, 0, 0, 1], [0.5, 0, 0, 0, 0.5, 0, 0, 0, 1],
            JsonSerializer.SerializeToElement(new { scale = 2 }), false);
        var projection = new ProductionPanelProjectionEvidence(
            calibration, phases, [series], [point], [preparation], [ocr], [marker], "P1");
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
        AssertProjectionCoordinates(workspace.CurrentProject.Panels.Single(panel => panel.PanelId == seeded.PanelId), seeded.Crop,
            pointId, markerId, tab.PixelWidth, tab.PixelHeight);
        ProductionPanelProjectionEvidence localEvidence = store.Get(workflowPanel.PanelId).ExportEvidence!.ProjectionEvidence!;
        Assert.AreEqual(20, localEvidence.Points.Single().OriginalPixel.X, 0);
        Assert.AreEqual(30, localEvidence.Points.Single().OriginalPixel.Y, 0);

        ProductionReviewProjectionResult second = workspace.Project(run, store);
        Assert.IsTrue(second.Succeeded, second.Failure?.TechnicalMessage);
        PanelRecord repeatedPanel = workspace.CurrentProject.Panels.Single(panel => panel.PanelId == seeded.PanelId);
        AssertProjectionCoordinates(repeatedPanel, seeded.Crop,
            pointId, markerId, tab.PixelWidth, tab.PixelHeight);
        Assert.AreEqual(PhaseId.FromGuid(phase1Id), repeatedPanel.Points.Single().PhaseId);
        Assert.IsTrue(repeatedPanel.Phases.Any(phase => phase.PhaseId.Value == phase1Id));

        string projectPath = Path.Combine(directory.Path, "projection-roundtrip.garproj");
        DomainResult<ProjectSaveReceipt> saved = await workspace.SaveProjectAsync(projectPath, CancellationToken.None);
        Assert.IsTrue(saved.IsSuccess, string.Join(" | ", saved.Errors.Select(static error => error.TechnicalMessage)));
        var reopened = new ManualPreviewWorkspaceService();
        _ = await reopened.OpenProjectAsync(projectPath, CancellationToken.None);
        PanelRecord reopenedPanel = reopened.CurrentProject.Panels.Single(panel => panel.PanelId == seeded.PanelId);
        Assert.AreEqual(PhaseId.FromGuid(phase1Id), reopenedPanel.Points.Single().PhaseId);
        Assert.IsTrue(reopenedPanel.Phases.Any(phase => phase.PhaseId.Value == phase1Id));
        AssertProjectionCoordinates(reopenedPanel, seeded.Crop,
            pointId, markerId, tab.PixelWidth, tab.PixelHeight);
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
