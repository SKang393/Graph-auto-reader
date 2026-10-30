// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class OrchestratorCompositionSmokeTests
{
    private static readonly Guid RunId = Guid.Parse("b3000000-0000-0000-0000-000000000001");
    private static readonly Guid PdfSourceId = Guid.Parse("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid ImagePanelId = Guid.Parse("b2000000-0000-0000-0000-000000000001");
    private static readonly Guid PdfPanelId = Guid.Parse("b2000000-0000-0000-0000-000000000002");
    private static readonly WorkflowStep[] ExpectedReviewSteps =
        [WorkflowStep.Import, WorkflowStep.Prepare, WorkflowStep.Detect, WorkflowStep.Review];

    [TestMethod]
    public async Task RecordedCompositionRunsImageAndPdfPanelsOfflineThroughExport()
    {
        RecordedWorkflowData data = CreateRecordedData();
        var recorded = new TracingStages(data);
        var forbiddenProduction = new ThrowingStages();
        WorkflowOrchestrator orchestrator = WorkflowComposition.Create(
            WorkflowRuntimeEnvironment.RecordedFake,
            forbiddenProduction.ServiceSet,
            recorded.ServiceSet);
        WorkflowRunRequest request = RunRequest(enhancementEnabled: true);

        WorkflowRunResult result = await orchestrator.RunThroughReviewAsync(
            request,
            previousReview: null,
            CancellationToken.None);
        WorkflowExportResult initialExport = await orchestrator.ExportAsync(
            result.Review,
            new WorkflowExportRequest(
                Guid.Parse("b3000000-0000-0000-0000-000000000002"),
                Path.GetTempPath()),
            CancellationToken.None);
        WorkflowPoint imagePoint = result.Review.Panels.Single(panel => panel.PanelId == ImagePanelId).Points.Single();
        WorkflowReviewState correctedReview = WorkflowOrchestrator.ApplyCorrection(
            result.Review,
            new MoveWorkflowPointCorrection(
                "workflow-move-1",
                ImagePanelId,
                imagePoint.PointId,
                imagePoint.DetectionKey,
                47,
                113));
        WorkflowRunResult rerun = await orchestrator.RunThroughReviewAsync(
            request,
            correctedReview,
            CancellationToken.None);
        WorkflowExportResult correctedExport = await orchestrator.ExportAsync(
            rerun.Review,
            new WorkflowExportRequest(
                Guid.Parse("b3000000-0000-0000-0000-000000000003"),
                Path.GetTempPath()),
            CancellationToken.None);

        CollectionAssert.AreEqual(ExpectedReviewSteps, rerun.Steps.Select(static step => step.Step).ToArray());
        Assert.AreEqual(2, rerun.Review.Panels.Count);
        Assert.IsTrue(rerun.Review.Panels.All(static panel => panel.Points.Single().SourceImage == WorkflowImageVariant.Consensus));
        WorkflowPoint preserved = rerun.Review.Panels.Single(panel => panel.PanelId == ImagePanelId).Points.Single();
        Assert.AreEqual(47d, preserved.OriginalPixelX, 0d);
        Assert.AreEqual(113d, preserved.OriginalPixelY, 0d);
        Assert.IsNull(preserved.GraphX);
        Assert.IsNull(preserved.GraphY);
        Assert.AreEqual(WorkflowReviewStatus.Corrected, preserved.ReviewStatus);
        Assert.IsTrue(preserved.CorrectionIds.Contains("workflow-move-1", StringComparer.Ordinal));
        Assert.AreEqual(2, recorded.ImportCalls);
        Assert.AreEqual(4, recorded.PrepareCalls);
        Assert.AreEqual(8, recorded.DetectionCalls);
        Assert.AreEqual(1, recorded.ExportCalls);
        Assert.AreEqual(0, recorded.NetworkAccessAttemptCount);
        Assert.AreEqual(0, forbiddenProduction.TotalCalls);
        Assert.IsTrue(initialExport.Succeeded);
        Assert.AreEqual("synthetic_intervention.csv", initialExport.Artifacts.Single().FileName);
        Assert.IsFalse(correctedExport.Succeeded);
        Assert.AreEqual("WORKFLOW_RECALIBRATION_REQUIRED", correctedExport.FailureCode);
        Assert.IsTrue(correctedExport.Warnings.Single().Contains(preserved.PointId, StringComparison.Ordinal));
        Assert.IsNotNull(recorded.LastExportReview);
        Assert.IsTrue(recorded.LastExportReview.Panels.All(static panel => panel.DetectionProvenance.Count == 2));

        foreach (WorkflowReviewPanel panel in rerun.Review.Panels)
        {
            WorkflowImportedPanel recordedPanel = data.Import.Panels.Single(item => item.PanelId == panel.PanelId);
            Assert.AreEqual(recordedPanel.Original.Sha256, panel.PreparedPanel.Original.Sha256);
            Assert.AreEqual(recordedPanel.Original.Reference, panel.PreparedPanel.Original.Reference);
            Assert.AreEqual(2, panel.DetectionProvenance.Count);
            WorkflowVisionEnvelope originalProvenance = panel.DetectionProvenance.Single(envelope =>
                string.Equals(envelope.InputSha256, panel.PreparedPanel.Original.Sha256, StringComparison.OrdinalIgnoreCase));
            WorkflowVisionEnvelope enhancedProvenance = panel.DetectionProvenance.Single(envelope =>
                string.Equals(envelope.InputSha256, panel.PreparedPanel.Enhanced!.Sha256, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(RunId, originalProvenance.RunId);
            Assert.AreEqual(IntegrationSmokeIds.Project.Value, originalProvenance.ProjectId);
            Assert.AreEqual("markers", originalProvenance.Stage);
            Assert.AreEqual("1", originalProvenance.Model?.Version);
            Assert.AreEqual("original_pixels", enhancedProvenance.Transforms.Single().OutputCoordinateSpace);
            Assert.IsNotNull(enhancedProvenance.Transforms.Single().OutputToInputMatrix);
        }
    }

    [TestMethod]
    public async Task DisabledEnhancementSkipsDerivedDetectionForEveryPanel()
    {
        RecordedWorkflowData data = CreateRecordedData();
        var recorded = new TracingStages(data);
        WorkflowOrchestrator orchestrator = WorkflowComposition.Create(
            WorkflowRuntimeEnvironment.RecordedFake,
            new ThrowingStages().ServiceSet,
            recorded.ServiceSet);

        WorkflowRunResult result = await orchestrator.RunThroughReviewAsync(
            RunRequest(enhancementEnabled: false),
            previousReview: null,
            CancellationToken.None);

        Assert.AreEqual(2, recorded.DetectionCalls);
        Assert.IsTrue(recorded.DetectedVariants.All(static variant => variant == WorkflowImageVariant.Original));
        Assert.IsTrue(result.Review.Panels.All(static panel => panel.PreparedPanel.Enhanced is null));
        Assert.IsTrue(result.Review.Panels.All(static panel => panel.Points.Single().SourceImage == WorkflowImageVariant.Original));
    }

    [TestMethod]
    public async Task ImportRejectsRequestedSourceWithoutPanelsBeforePrepareOrDetect()
    {
        RecordedWorkflowData data = CreateRecordedData();
        var incompleteImport = new WorkflowImportSnapshot(
            data.Import.ProjectId,
            [data.Import.Panels.Single(panel => panel.SourceId == IntegrationSmokeIds.Source.Value)]);
        var incompleteData = new RecordedWorkflowData(
            incompleteImport,
            data.PreparedPanels,
            data.Detections,
            data.Export);
        var recorded = new TracingStages(incompleteData);
        WorkflowOrchestrator orchestrator = WorkflowComposition.Create(
            WorkflowRuntimeEnvironment.RecordedFake,
            new ThrowingStages().ServiceSet,
            recorded.ServiceSet);

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            orchestrator.RunThroughReviewAsync(
                RunRequest(enhancementEnabled: false),
                previousReview: null,
                CancellationToken.None));

        StringAssert.Contains(exception.Message, PdfSourceId.ToString("D"));
        Assert.IsFalse(exception.Message.Contains("synthetic.pdf", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, recorded.ImportCalls);
        Assert.AreEqual(0, recorded.PrepareCalls);
        Assert.AreEqual(0, recorded.DetectionCalls);
    }

    [TestMethod]
    public async Task CancellationPublishesOnlyFullyReviewedPanelCheckpoints()
    {
        RecordedWorkflowData data = CreateRecordedData();
        using var cancellation = new CancellationTokenSource();
        var recorded = new TracingStages(data, PdfPanelId, cancellation);
        WorkflowOrchestrator orchestrator = WorkflowComposition.Create(
            WorkflowRuntimeEnvironment.RecordedFake,
            new ThrowingStages().ServiceSet,
            recorded.ServiceSet);
        var checkpoints = new List<WorkflowRunResult>();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            orchestrator.RunThroughReviewAsync(
                RunRequest(enhancementEnabled: false),
                previousReview: null,
                (checkpoint, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    checkpoints.Add(checkpoint);
                    return Task.CompletedTask;
                },
                cancellation.Token));

        Assert.HasCount(1, checkpoints);
        WorkflowRunResult completed = checkpoints.Single();
        Assert.AreEqual(RunId, completed.RunId);
        Assert.HasCount(1, completed.Review.Panels);
        Assert.AreEqual(ImagePanelId, completed.Review.Panels.Single().PanelId);
        Assert.HasCount(1, completed.Review.Panels.Single().Points);
        CollectionAssert.AreEqual(
            ExpectedReviewSteps,
            completed.Steps.Select(static step => step.Step).ToArray());
        Assert.IsTrue(completed.Steps.All(static step => step.Elapsed >= TimeSpan.Zero));
        Assert.AreEqual(2, recorded.DetectionCalls);
    }

    private static WorkflowRunRequest RunRequest(bool enhancementEnabled) =>
        new(
            RunId,
            new WorkflowImportRequest(
                IntegrationSmokeIds.Project.Value,
                [
                    new WorkflowSourceRequest(IntegrationSmokeIds.Source.Value, WorkflowSourceKind.Image, @"C:\public-fixtures\synthetic.bmp"),
                    new WorkflowSourceRequest(PdfSourceId, WorkflowSourceKind.Pdf, @"C:\public-fixtures\synthetic.pdf"),
                ],
                enhancementEnabled));

    private static RecordedWorkflowData CreateRecordedData()
    {
        var imageOriginal = new WorkflowImageEvidence(
            "synthetic.bmp",
            new string('a', 64),
            120,
            200,
            WorkflowImageVariant.Original);
        var pdfOriginal = new WorkflowImageEvidence(
            "synthetic.pdf#page=1&panel=1",
            new string('c', 64),
            120,
            200,
            WorkflowImageVariant.Original);
        var imagePanel = new WorkflowImportedPanel(ImagePanelId, IntegrationSmokeIds.Source.Value, "Image panel", imageOriginal);
        var pdfPanel = new WorkflowImportedPanel(PdfPanelId, PdfSourceId, "PDF panel", pdfOriginal, pageNumber: 1);
        var import = new WorkflowImportSnapshot(IntegrationSmokeIds.Project.Value, [imagePanel, pdfPanel]);
        WorkflowPreparedPanel[] prepared =
        [
            new(imagePanel, imageOriginal, new WorkflowImageEvidence("synthetic.enhanced.png", new string('b', 64), 240, 400, WorkflowImageVariant.Enhanced)),
            new(pdfPanel, pdfOriginal, new WorkflowImageEvidence("synthetic.pdf.panel.enhanced.png", new string('d', 64), 240, 400, WorkflowImageVariant.Enhanced)),
        ];
        WorkflowDetectionBatch[] detections =
        [
            Detection(ImagePanelId, WorkflowImageVariant.Original, "image-original", "image-key", 40, 120),
            Detection(ImagePanelId, WorkflowImageVariant.Enhanced, "image-enhanced", "image-key-enhanced", 40.5, 120.5),
            Detection(PdfPanelId, WorkflowImageVariant.Original, "pdf-original", "pdf-key", 30, 90),
            Detection(PdfPanelId, WorkflowImageVariant.Enhanced, "pdf-enhanced", "pdf-key-enhanced", 30.5, 90.5),
        ];
        var export = new WorkflowExportResult(
            true,
            [new WorkflowExportArtifact("synthetic_intervention.csv", new string('e', 64), 4, null)]);
        return new RecordedWorkflowData(import, prepared, detections, export);
    }

    private static WorkflowDetectionBatch Detection(
        Guid panelId,
        WorkflowImageVariant variant,
        string pointId,
        string detectionKey,
        double x,
        double y)
    {
        bool isImagePanel = panelId == ImagePanelId;
        string inputSha256 = (isImagePanel, variant) switch
        {
            (true, WorkflowImageVariant.Original) => new string('a', 64),
            (true, WorkflowImageVariant.Enhanced) => new string('b', 64),
            (false, WorkflowImageVariant.Original) => new string('c', 64),
            _ => new string('d', 64),
        };
        WorkflowTransformProvenance[] transforms = variant == WorkflowImageVariant.Enhanced
            ?
            [
                new WorkflowTransformProvenance(
                    "recorded-enhanced-x2",
                    "enhanced_pixels",
                    "original_pixels",
                    [0.5, 0, 0, 0, 0.5, 0, 0, 0, 1],
                    [2, 0, 0, 0, 2, 0, 0, 0, 1],
                    lossy: false),
            ]
            : [];
        var envelope = new WorkflowVisionEnvelope(
            contractVersion: 1,
            RunId,
            IntegrationSmokeIds.Project.Value,
            panelId,
            stage: "markers",
            stageVersion: "recorded-1",
            inputSha256,
            new WorkflowVisionModel("recorded-marker-fake", "1", new string('f', 64), "cpu"),
            new WorkflowVisionTiming(0, 0, 0, 0),
            confidence: 0.95,
            transforms: transforms);
        return new WorkflowDetectionBatch(
            envelope,
            variant,
            [new WorkflowDetectionCandidate(
                pointId,
                detectionKey,
                x,
                y,
                0.95,
                variant,
                "●",
                "circle",
                "filled",
                "intervention",
                "b",
                3,
                42,
                "markers",
                "1")]);
    }

    private sealed class TracingStages :
        IWorkflowImportStage,
        IWorkflowPrepareStage,
        IWorkflowDetectionStage,
        IWorkflowExportStage
    {
        private readonly RecordedWorkflowStages _inner;
        private readonly Guid? _cancelBeforePanelId;
        private readonly CancellationTokenSource? _cancellation;

        public TracingStages(
            RecordedWorkflowData data,
            Guid? cancelBeforePanelId = null,
            CancellationTokenSource? cancellation = null)
        {
            _inner = new RecordedWorkflowStages(data);
            _cancelBeforePanelId = cancelBeforePanelId;
            _cancellation = cancellation;
            ServiceSet = new WorkflowServiceSet(this, this, this, this);
        }

        public WorkflowServiceSet ServiceSet { get; }

        public int ImportCalls { get; private set; }

        public int PrepareCalls { get; private set; }

        public int DetectionCalls { get; private set; }

        public int ExportCalls { get; private set; }

        public WorkflowReviewState? LastExportReview { get; private set; }

        public List<WorkflowImageVariant> DetectedVariants { get; } = [];

        public List<string> RequestedRemoteResources { get; } = [];

        public int NetworkAccessAttemptCount => RequestedRemoteResources.Count;

        public Task<WorkflowImportSnapshot> ImportAsync(
            WorkflowImportRequest request,
            CancellationToken cancellationToken)
        {
            ImportCalls++;
            return _inner.ImportAsync(request, cancellationToken);
        }

        public Task<WorkflowPreparedPanel> PrepareAsync(
            WorkflowImportedPanel panel,
            bool enhancementEnabled,
            CancellationToken cancellationToken)
        {
            PrepareCalls++;
            return _inner.PrepareAsync(panel, enhancementEnabled, cancellationToken);
        }

        public Task<WorkflowDetectionBatch> DetectAsync(
            WorkflowPreparedPanel panel,
            WorkflowImageVariant imageVariant,
            Guid runId,
            Guid projectId,
            CancellationToken cancellationToken)
        {
            DetectionCalls++;
            DetectedVariants.Add(imageVariant);
            if (_cancelBeforePanelId == panel.ImportedPanel.PanelId &&
                imageVariant == WorkflowImageVariant.Original)
            {
                _cancellation?.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return _inner.DetectAsync(panel, imageVariant, runId, projectId, cancellationToken);
        }

        public Task<WorkflowExportResult> ExportAsync(
            WorkflowReviewState review,
            WorkflowExportRequest request,
            CancellationToken cancellationToken)
        {
            ExportCalls++;
            LastExportReview = review;
            return _inner.ExportAsync(review, request, cancellationToken);
        }
    }

    private sealed class ThrowingStages :
        IWorkflowImportStage,
        IWorkflowPrepareStage,
        IWorkflowDetectionStage,
        IWorkflowExportStage
    {
        public ThrowingStages()
        {
            ServiceSet = new WorkflowServiceSet(this, this, this, this);
        }

        public WorkflowServiceSet ServiceSet { get; }

        public int TotalCalls { get; private set; }

        public Task<WorkflowImportSnapshot> ImportAsync(
            WorkflowImportRequest request,
            CancellationToken cancellationToken) =>
            Fail<WorkflowImportSnapshot>();

        public Task<WorkflowPreparedPanel> PrepareAsync(
            WorkflowImportedPanel panel,
            bool enhancementEnabled,
            CancellationToken cancellationToken) =>
            Fail<WorkflowPreparedPanel>();

        public Task<WorkflowDetectionBatch> DetectAsync(
            WorkflowPreparedPanel panel,
            WorkflowImageVariant imageVariant,
            Guid runId,
            Guid projectId,
            CancellationToken cancellationToken) =>
            Fail<WorkflowDetectionBatch>();

        public Task<WorkflowExportResult> ExportAsync(
            WorkflowReviewState review,
            WorkflowExportRequest request,
            CancellationToken cancellationToken) =>
            Fail<WorkflowExportResult>();

        private Task<T> Fail<T>()
        {
            TotalCalls++;
            return Task.FromException<T>(new AssertFailedException("Production stages must not run in recorded-fake mode."));
        }
    }
}
