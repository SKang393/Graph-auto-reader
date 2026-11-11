// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.Axis;
using GraphReader.Domain;
using GraphReader.Export;
using GraphReader.Imaging;
using GraphReader.SuperResolution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class ImagePersistenceExportSmokeTests
{
    private static readonly string[] ExpectedExportPhases = ["a", "b", "g1"];
    private static readonly int[] ExpectedBatchIndexes = [0, 1, 2];

    [TestMethod]
    public async Task AdjacentColumnDoesNotExportAnotherColumnsPrintedSession()
    {
        var lattice = SessionLattice.Fit(new SessionLatticeRequest
        {
            PrintedTicks = [new("one", 10.1, 1), new("three", 13.3, 3)],
            MarkerColumns = [new(10), new(11.6), new(13.2)],
        });
        Assert.AreEqual(CalibrationValidity.Valid, lattice.Validity);
        SessionXEvidence middle = lattice.Assignments.Single(point => point.Ordinal == 2);
        Assert.IsNull(middle.PrintedX);
        Assert.AreEqual(2d, middle.EstimatedX);

        using var environment = new IntegrationSmokeTestEnvironment();
        string sourcePath = environment.WriteBmp("session-evidence.bmp", 10, 20, 30);
        ProjectDocument project = IntegrationSmokeProjectFactory.Create(
            sourcePath, IntegrationSmokeProjectFactory.Sha256(await File.ReadAllBytesAsync(sourcePath)));
        ExportRequest original = CreateExportRequest(project.Panels.Single(), environment.PathFor("exports"));
        ExportPoint[] points = original.Points.Select(point => point.PointId == IntegrationSmokeIds.InterventionPoint.Value
            ? point with
            {
                PrintedXValue = middle.PrintedX,
                EstimatedXValue = middle.EstimatedX,
                XSource = ExportXValueSource.Estimated,
                GraphX = middle.PrintedX ?? middle.EstimatedX,
            }
            : point).ToArray();
        var request = new ExportRequest(original.RunId, original.ProjectId, original.PanelId, original.OutputDirectory,
            original.Participant, original.Mode, original.AuditMode, original.Operation, original.Calibration,
            original.SessionOriginPolicy, original.Phases, original.Series, points, original.Relations);

        ExportResult result = await new ExportService().ExportAsync(request, CancellationToken.None);
        Assert.IsTrue(result.Succeeded, Format(result.Failures));
        ExtendedAuditRow row = result.AuditArtifacts.Single().Rows.Single(
            item => item.PointId == IntegrationSmokeIds.InterventionPoint.Value);
        Assert.AreEqual(2d, row.XValue);
        Assert.AreEqual(ExportXValueSource.Estimated, row.XSource);
    }

    [TestMethod]
    public async Task ImageWorkflowPreservesOriginalAndExportsCorrectedAuditableRows()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string sourcePath = environment.WriteBmp("synthetic graph.bmp", blue: 23, green: 97, red: 181);
        byte[] originalBefore = await File.ReadAllBytesAsync(sourcePath);
        string originalHash = Convert.ToHexStringLower(SHA256.HashData(originalBefore));

        ImageImportResult imported = await new ImageImportService().ImportAsync(sourcePath, CancellationToken.None);
        Assert.IsTrue(imported.IsSuccess, imported.Error?.TechnicalMessage);
        Assert.AreEqual(originalHash, imported.Image!.Sha256);

        SessionFirstCalibrationResult calibration = RobustCalibration.FitSessionFirst(AutoCalibrationRequest());
        Assert.AreEqual(CalibrationValidity.Valid, calibration.Validity);
        Assert.IsTrue(calibration.Anchors.All(static anchor => anchor.IsExact));

        EnhancementTransform enhancementTransform = EnhancementTransform.CreateScale2();
        EnhancementConsensusResult consensus = EnhancementConsensus.Compare(
            [new EnhancementEvidencePoint("marker-1", new EnhancementPoint(40, 120), 0.98)],
            [new EnhancementEvidencePoint("marker-1", new EnhancementPoint(80, 240), 0.97)],
            enhancementTransform,
            maximumDisplacementPixels: 0.25);
        Assert.IsFalse(consensus.RequiresReview);
        Assert.AreEqual(1d, consensus.ConfidenceMultiplier, 0d);

        ProjectDocument project = IntegrationSmokeProjectFactory.Create(sourcePath, originalHash);
        string projectPath = environment.PathFor("projects", "workflow.garproj");
        DomainResult<ProjectSaveReceipt> saved = await new ProjectFileStore().SaveNewAsync(project, projectPath);
        Assert.IsTrue(saved.IsSuccess, Format(saved.Errors));
        DomainResult<ProjectDocument> reopened = await new ProjectFileStore().LoadAsync(projectPath);
        Assert.IsTrue(reopened.IsSuccess, Format(reopened.Errors));

        PanelRecord panel = reopened.Value!.Panels.Single();
        PointRecord corrected = panel.Points.Single(point => point.PointId == IntegrationSmokeIds.InterventionPoint);
        Assert.AreEqual(ReviewStatus.Corrected, corrected.ReviewStatus);
        Assert.AreEqual("user_corrected_marker", corrected.ModificationHistory.Single().Reason);
        Assert.IsTrue(panel.Phases.Single(phase => phase.PhaseId == IntegrationSmokeIds.InterventionPhase).UserConfirmed);

        string exportRoot = environment.PathFor("exports");
        ExportResult exported = await new ExportService().ExportAsync(
            CreateExportRequest(panel, exportRoot),
            CancellationToken.None);
        Assert.IsTrue(exported.Succeeded, Format(exported.Failures));
        MinimalCsvArtifact minimal = exported.MinimalArtifacts.Single();
        CollectionAssert.AreEqual(
            ExpectedExportPhases,
            minimal.Rows.Select(static row => row.Phase).ToArray());
        ExtendedAuditArtifact audit = exported.AuditArtifacts.Single();
        Assert.AreEqual(3, audit.Rows.Count);
        Assert.AreEqual(
            ExportReviewStatus.Corrected,
            audit.Rows.Single(row => row.PointId == IntegrationSmokeIds.InterventionPoint.Value).ReviewStatus);
        Assert.IsTrue(audit.Rows.Any(static row => row.Inclusion == ExportRowInclusion.SharedBaseline));
        Assert.IsTrue(audit.Rows.Any(static row => row.Inclusion == ExportRowInclusion.ApplicableProbe));

        byte[] originalAfter = await File.ReadAllBytesAsync(sourcePath);
        CollectionAssert.AreEqual(originalBefore, originalAfter);
        CollectionAssert.AreEqual(originalBefore, imported.Image.OriginalBytes.Copy());
        Assert.AreEqual(originalHash, Convert.ToHexStringLower(SHA256.HashData(originalAfter)));
    }

    [TestMethod]
    public async Task BatchImportPreservesInputOrderAndIdentifiesContentDuplicates()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string first = environment.WriteBmp("01.bmp", 0, 0, 0);
        string second = environment.WriteBmp("02.bmp", 255, 255, 255);
        string duplicate = environment.PathFor("03 duplicate.bmp");
        File.Copy(first, duplicate);

        BatchImportResult result = await new ImageImportService().ImportBatchAsync(
            [first, second, duplicate],
            CancellationToken.None);

        Assert.AreEqual(3, result.SuccessfulCount);
        Assert.AreEqual(1, result.DuplicateCount);
        CollectionAssert.AreEqual(ExpectedBatchIndexes, result.Items.Select(static item => item.InputIndex).ToArray());
        Assert.AreEqual(0, result.Items[2].Image!.DuplicateOfInputIndex);
        Assert.AreNotEqual(result.Items[0].Image!.Sha256, result.Items[1].Image!.Sha256);
    }

    [TestMethod]
    public async Task ManualCalibrationAndCorrectionsSurviveSaveReopen()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string sourcePath = environment.WriteBmp("manual.bmp", 10, 20, 30);
        byte[] bytes = await File.ReadAllBytesAsync(sourcePath);
        ProjectDocument project = IntegrationSmokeProjectFactory.Create(
            sourcePath,
            IntegrationSmokeProjectFactory.Sha256(bytes),
            userConfirmedCalibration: true);

        string path = environment.PathFor("manual.garproj");
        DomainResult<ProjectSaveReceipt> saved = await new ProjectFileStore().SaveNewAsync(project, path);
        Assert.IsTrue(saved.IsSuccess, Format(saved.Errors));
        DomainResult<ProjectDocument> loaded = await new ProjectFileStore().LoadAsync(path);
        Assert.IsTrue(loaded.IsSuccess, Format(loaded.Errors));

        PanelRecord panel = loaded.Value!.Panels.Single();
        Assert.IsTrue(panel.Calibration!.UserConfirmed);
        Assert.AreEqual("manual", panel.Calibration.SessionLattice!.Source);
        Assert.IsTrue(panel.Phases.Where(static phase => phase.Source == PhaseSource.Manual).All(static phase => phase.UserConfirmed));
        Assert.AreEqual(
            ReviewStatus.Corrected,
            panel.Markers.Single(marker => marker.MarkerId == IntegrationSmokeIds.InterventionMarker).ReviewStatus);
        Assert.AreEqual(1, panel.Points.Single(point => point.PointId == IntegrationSmokeIds.InterventionPoint).ModificationHistory.Count);
    }

    [TestMethod]
    public async Task AutosaveDiscoveryAndRecoveryKeepOriginalProjectUntouched()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string sourcePath = environment.WriteBmp("recovery.bmp", 50, 60, 70);
        ProjectDocument original = IntegrationSmokeProjectFactory.Create(
            sourcePath,
            IntegrationSmokeProjectFactory.Sha256(await File.ReadAllBytesAsync(sourcePath)));
        string projectPath = environment.PathFor("original.garproj");
        var store = new ProjectFileStore();
        DomainResult<ProjectSaveReceipt> saved = await store.SaveNewAsync(original, projectPath);
        Assert.IsTrue(saved.IsSuccess, Format(saved.Errors));
        byte[] originalProjectBytes = await File.ReadAllBytesAsync(projectPath);

        string autosaveRoot = environment.PathFor("Autosave");
        var snapshots = new ProjectSnapshotService(autosaveRoot);
        DomainResult<ProjectSnapshotReceipt> snapshot = await snapshots.SaveEventSnapshotAsync(
            original,
            SnapshotTrigger.PointEdited,
            IntegrationSmokeIds.CreatedUtc.AddMinutes(10),
            IntegrationSmokeIds.Panel,
            IntegrationSmokeIds.InterventionPoint.Value.ToString("D"));
        Assert.IsTrue(snapshot.IsSuccess, Format(snapshot.Errors));

        var recovery = new ProjectRecoveryService(store);
        DomainResult<RecoveryDiscoveryReport> discovered = await recovery.DiscoverAsync(
            autosaveRoot,
            original,
            projectPath);
        Assert.IsTrue(discovered.IsSuccess, Format(discovered.Errors));
        RecoveryCandidate candidate = discovered.Value!.Candidates.Single();
        Assert.AreEqual(RecoveryRecommendation.RestoreRecommended, candidate.Recommendation);

        string recoveredPath = environment.PathFor("recovered.garproj");
        DomainResult<ProjectSaveReceipt> recovered = await recovery.RecoverToNewFileAsync(
            candidate.AutosavePath,
            recoveredPath);
        Assert.IsTrue(recovered.IsSuccess, Format(recovered.Errors));
        CollectionAssert.AreEqual(originalProjectBytes, await File.ReadAllBytesAsync(projectPath));
        DomainResult<ProjectDocument> recoveredProject = await store.LoadAsync(recoveredPath);
        Assert.IsTrue(recoveredProject.IsSuccess, Format(recoveredProject.Errors));
        Assert.IsTrue(recoveredProject.Value!.Audit.Events.Any(static item => item.Kind == DomainEventKind.PointEdited));
    }

    private static SessionFirstCalibrationRequest AutoCalibrationRequest() =>
        new()
        {
            YTicks =
            [
                new NumericTickEvidence("y20", 250, 20),
                new NumericTickEvidence("y40", 200, 40),
                new NumericTickEvidence("y60", 150, 60),
                new NumericTickEvidence("y80", 100, 80),
                new NumericTickEvidence("y100", 50, 100),
            ],
            PrintedXTicks =
            [
                new PrintedXTickEvidence("x1", 100, 1),
                new PrintedXTickEvidence("x6", 225, 6),
                new PrintedXTickEvidence("x11", 350, 11),
                new PrintedXTickEvidence("x16", 475, 16),
                new PrintedXTickEvidence("x21", 600, 21),
            ],
            Lattice = new SessionLatticeRequest
            {
                PrintedTicks =
                [
                    new PrintedXTickEvidence("x1", 100, 1),
                    new PrintedXTickEvidence("x6", 225, 6),
                    new PrintedXTickEvidence("x11", 350, 11),
                    new PrintedXTickEvidence("x16", 475, 16),
                    new PrintedXTickEvidence("x21", 600, 21),
                ],
            },
            YMaximum = 100,
            XMaximum = 21,
        };

    private static ExportRequest CreateExportRequest(PanelRecord panel, string outputRoot)
    {
        ExportPhase[] phases = panel.Phases.Select(phase => new ExportPhase(
            phase.PhaseId.Value,
            phase.Order,
            phase.Code,
            phase.NormalizedType switch
            {
                GraphReader.Domain.PhaseNormalizedType.Baseline => ExportPhaseType.Baseline,
                GraphReader.Domain.PhaseNormalizedType.Intervention => ExportPhaseType.Intervention,
                GraphReader.Domain.PhaseNormalizedType.Maintenance => ExportPhaseType.Maintenance,
                GraphReader.Domain.PhaseNormalizedType.Generalization => ExportPhaseType.Generalization,
                _ => ExportPhaseType.Unknown,
            },
            phase.LabelText,
            phase.ScreenXMin,
            phase.ScreenXMax,
            phase.Confidence)).ToArray();
        ExportSeries[] series = panel.Series.Select(item => new ExportSeries(
            item.SeriesId.Value,
            item.Symbol,
            item.DisplayName,
            item.SemanticRole switch
            {
                SemanticRole.Baseline => ExportSeriesRole.Baseline,
                SemanticRole.Intervention => ExportSeriesRole.Intervention,
                SemanticRole.Maintenance => ExportSeriesRole.Maintenance,
                SemanticRole.Generalization => ExportSeriesRole.Generalization,
                _ => ExportSeriesRole.Unknown,
            },
            item.PointIds.Select(static id => id.Value),
            item.Confidence,
            item.LegendText)).ToArray();
        ExportPoint[] points = panel.Points.Select(point => new ExportPoint(
            point.PointId.Value,
            point.MarkerId?.Value,
            point.SeriesId!.Value.Value,
            point.PhaseId!.Value.Value,
            new ExportPixelPoint(point.OriginalPixel.X, point.OriginalPixel.Y),
            point.GraphX,
            point.GraphY,
            point.ObservationIndex,
            point.PrintedXValue,
            point.EstimatedXValue,
            point.XSource switch
            {
                PointXSource.Printed => ExportXValueSource.Printed,
                PointXSource.Estimated => ExportXValueSource.Estimated,
                PointXSource.ObservationOrder => ExportXValueSource.ObservationOrder,
                _ => ExportXValueSource.Unknown,
            },
            point.XConfidence,
            point.YConfidence,
            point.PointConfidence,
            point.ReviewStatus switch
            {
                ReviewStatus.Accepted => ExportReviewStatus.Accepted,
                ReviewStatus.Corrected => ExportReviewStatus.Corrected,
                ReviewStatus.Rejected => ExportReviewStatus.Rejected,
                _ => ExportReviewStatus.Unreviewed,
            },
            point.SourceStage,
            point.ModelVersion)).ToArray();
        SeriesRecord intervention = panel.Series.Single(item => item.SeriesId == IntegrationSmokeIds.InterventionSeries);

        return new ExportRequest(
            Guid.Parse("a0000000-0000-0000-0000-000000000001"),
            IntegrationSmokeIds.Project.Value,
            panel.PanelId.Value,
            outputRoot,
            panel.Participant!,
            ExportMode.PrintedSession,
            ExportAuditMode.ExtendedCsv,
            ExportOperation.Preview,
            new ExportCalibration(ExportCalibrationStatus.Valid, true, true, true, 1, 1),
            ExportSessionOriginPolicy.Default,
            phases,
            series,
            points,
            [new ExportSeriesRelation(
                IntegrationSmokeIds.InterventionSeries.Value,
                IntegrationSmokeIds.BaselineSeries.Value,
                intervention.ApplicableProbeSeriesIds.Select(static id => id.Value))]);
    }

    private static string Format(IEnumerable<DomainError> errors) =>
        string.Join(" | ", errors.Select(static error => $"{error.Code}: {error.TechnicalMessage}"));

    private static string Format(IEnumerable<ExportFailure> failures) =>
        string.Join(" | ", failures.Select(static error => $"{error.Code}: {error.TechnicalMessage}"));
}
