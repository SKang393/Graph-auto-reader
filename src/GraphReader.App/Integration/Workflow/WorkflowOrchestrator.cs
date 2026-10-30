// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;

namespace GraphReader.App.Integration.Workflow;

public sealed class WorkflowOrchestrator
{
    private readonly WorkflowServiceSet services;

    public WorkflowOrchestrator(WorkflowServiceSet services)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public Task<WorkflowRunResult> RunThroughReviewAsync(
        WorkflowRunRequest request,
        WorkflowReviewState? previousReview,
        CancellationToken cancellationToken) =>
        RunThroughReviewAsync(
            request,
            previousReview,
            completedPanelCheckpoint: null,
            cancellationToken);

    public async Task<WorkflowRunResult> RunThroughReviewAsync(
        WorkflowRunRequest request,
        WorkflowReviewState? previousReview,
        Func<WorkflowRunResult, CancellationToken, Task>? completedPanelCheckpoint,
        CancellationToken cancellationToken)
    {
        ValidateRunRequest(request, previousReview);
        cancellationToken.ThrowIfCancellationRequested();

        var steps = new List<WorkflowStepRecord>(4);
        Stopwatch timer = Stopwatch.StartNew();
        WorkflowImportSnapshot imported = await services.Importer
            .ImportAsync(request.Import, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateImportSnapshot(request.Import, imported);
        steps.Add(new WorkflowStepRecord(WorkflowStep.Import, timer.Elapsed, imported.Panels.Count));

        timer.Restart();
        var preparedPanels = new List<WorkflowPreparedPanel>(imported.Panels.Count);
        foreach (WorkflowImportedPanel panel in imported.Panels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkflowPreparedPanel prepared = await services.Preparer
                .PrepareAsync(panel, request.Import.EnhancementEnabled, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePreparedPanel(panel, prepared, request.Import.EnhancementEnabled);
            preparedPanels.Add(prepared);
        }

        steps.Add(new WorkflowStepRecord(WorkflowStep.Prepare, timer.Elapsed, preparedPanels.Count));

        timer.Restart();
        IReadOnlyList<WorkflowCorrection> journal = previousReview?.CorrectionJournal ?? Array.Empty<WorkflowCorrection>();
        var reviewedPanels = new List<WorkflowReviewPanel>(preparedPanels.Count);
        var detectionWarnings = new List<string>();
        int detectionCount = 0;
        TimeSpan detectionElapsed = TimeSpan.Zero;
        TimeSpan reviewElapsed = TimeSpan.Zero;
        foreach (WorkflowPreparedPanel prepared in preparedPanels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkflowDetectionBatch original = await services.Detector
                .DetectAsync(
                    prepared,
                    WorkflowImageVariant.Original,
                    request.RunId,
                    request.Import.ProjectId,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDetectionBatch(
                request,
                prepared,
                original,
                WorkflowImageVariant.Original);

            WorkflowDetectionBatch? enhanced = null;
            if (request.Import.EnhancementEnabled && prepared.Enhanced is not null)
            {
                enhanced = await services.Detector
                    .DetectAsync(
                        prepared,
                        WorkflowImageVariant.Enhanced,
                        request.RunId,
                        request.Import.ProjectId,
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ValidateDetectionBatch(
                    request,
                    prepared,
                    enhanced,
                    WorkflowImageVariant.Enhanced);
            }

            IReadOnlyList<WorkflowPoint> points = OriginalEnhancedConsensus.Merge(
                original,
                enhanced,
                request.ConsensusOptions);
            timer.Stop();
            detectionElapsed += timer.Elapsed;
            var panelReviewTimer = Stopwatch.StartNew();
            WorkflowVisionEnvelope[] provenance = enhanced is null
                ? [original.Envelope]
                : [original.Envelope, enhanced.Envelope];
            var automationPanel = new WorkflowReviewPanel(prepared, points, provenance);
            WorkflowReviewPanel? previousPanel = previousReview?.Panels.SingleOrDefault(
                candidate => candidate.PanelId == automationPanel.PanelId);
            WorkflowReviewPanel reviewedPanel = ManualCorrectionOverlay.Reapply(
                automationPanel,
                previousPanel,
                journal);
            reviewedPanels.Add(reviewedPanel);
            detectionCount += original.Candidates.Count + (enhanced?.Candidates.Count ?? 0);
            detectionWarnings.AddRange(original.Warnings);
            if (enhanced is not null)
            {
                detectionWarnings.AddRange(enhanced.Warnings);
            }
            panelReviewTimer.Stop();
            reviewElapsed += panelReviewTimer.Elapsed;

            if (completedPanelCheckpoint is not null)
            {
                WorkflowCorrection[] panelCorrections = journal
                    .Where(correction => correction.PanelId == reviewedPanel.PanelId)
                    .ToArray();
                string[] checkpointWarnings = imported.Warnings
                    .Concat(prepared.Warnings)
                    .Concat(detectionWarnings)
                    .Concat(previousReview?.Warnings ?? Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var checkpointReview = new WorkflowReviewState(
                    imported.ProjectId,
                    [reviewedPanel],
                    panelCorrections,
                    checkpointWarnings);
                var checkpoint = new WorkflowRunResult(
                    request.RunId,
                    checkpointReview,
                    steps
                        .Append(new WorkflowStepRecord(
                            WorkflowStep.Detect,
                            detectionElapsed,
                            detectionCount))
                        .Append(new WorkflowStepRecord(
                            WorkflowStep.Review,
                            reviewElapsed,
                            reviewedPanel.Points.Count)));
                await completedPanelCheckpoint(checkpoint, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            timer.Restart();
        }

        steps.Add(new WorkflowStepRecord(WorkflowStep.Detect, detectionElapsed, detectionCount));

        timer.Restart();
        IEnumerable<string> warnings = imported.Warnings
            .Concat(preparedPanels.SelectMany(static panel => panel.Warnings))
            .Concat(detectionWarnings)
            .Concat(previousReview?.Warnings ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal);
        var review = new WorkflowReviewState(imported.ProjectId, reviewedPanels, journal, warnings);
        steps.Add(new WorkflowStepRecord(
            WorkflowStep.Review,
            reviewElapsed + timer.Elapsed,
            review.Panels.Sum(static panel => panel.Points.Count)));

        return new WorkflowRunResult(request.RunId, review, steps);
    }

    public static WorkflowReviewState ApplyCorrection(
        WorkflowReviewState state,
        WorkflowCorrection correction) =>
        ManualCorrectionOverlay.Apply(state, correction);

    public async Task<WorkflowExportResult> ExportAsync(
        WorkflowReviewState review,
        WorkflowExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(request);
        if (request.RunId == Guid.Empty)
        {
            throw new ArgumentException("An export run ID is required.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        string[] pointsRequiringCalibration = review.Panels
            .SelectMany(static panel => panel.Points)
            .Where(static point =>
                point.ReviewStatus != WorkflowReviewStatus.Rejected &&
                (!point.GraphX.HasValue || !point.GraphY.HasValue))
            .Select(static point => point.PointId)
            .OrderBy(static pointId => pointId, StringComparer.Ordinal)
            .ToArray();
        if (pointsRequiringCalibration.Length > 0)
        {
            return new WorkflowExportResult(
                succeeded: false,
                warnings:
                [
                    $"RECALIBRATION_REQUIRED:{string.Join(',', pointsRequiringCalibration)}",
                ],
                failureCode: "WORKFLOW_RECALIBRATION_REQUIRED");
        }

        WorkflowExportResult result = await services.Exporter
            .ExportAsync(review, request, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result ?? throw new InvalidOperationException("The export stage returned no result.");
    }

    private static void ValidateRunRequest(WorkflowRunRequest request, WorkflowReviewState? previousReview)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Import);
        if (request.RunId == Guid.Empty)
        {
            throw new ArgumentException("A run ID is required.", nameof(request));
        }

        if (previousReview is not null && previousReview.ProjectId != request.Import.ProjectId)
        {
            throw new ArgumentException("The previous review belongs to a different project.", nameof(previousReview));
        }
    }

    private static void ValidateImportSnapshot(
        WorkflowImportRequest request,
        WorkflowImportSnapshot imported)
    {
        if (imported is null)
        {
            throw new InvalidOperationException("The import stage returned no snapshot.");
        }

        if (imported.ProjectId != request.ProjectId)
        {
            throw new InvalidOperationException("The import stage changed the project ID.");
        }

        HashSet<Guid> requestedSourceIds = request.Sources
            .Select(static source => source.SourceId)
            .ToHashSet();
        if (imported.Panels.Any(panel => !requestedSourceIds.Contains(panel.SourceId)))
        {
            throw new InvalidOperationException("The import stage returned a panel for an unrequested source.");
        }

        HashSet<Guid> importedSourceIds = imported.Panels
            .Select(static panel => panel.SourceId)
            .ToHashSet();
        Guid[] missingSourceIds = request.Sources
            .Select(static source => source.SourceId)
            .Where(sourceId => !importedSourceIds.Contains(sourceId))
            .ToArray();
        if (missingSourceIds.Length > 0)
        {
            string missingIds = string.Join(", ", missingSourceIds.Select(static sourceId => sourceId.ToString("D")));
            throw new InvalidOperationException(
                $"The import stage returned no panels for requested source ID(s): {missingIds}.");
        }
    }

    private static void ValidatePreparedPanel(
        WorkflowImportedPanel imported,
        WorkflowPreparedPanel prepared,
        bool enhancementEnabled)
    {
        if (prepared is null)
        {
            throw new InvalidOperationException("The prepare stage returned no panel.");
        }

        if (prepared.ImportedPanel.PanelId != imported.PanelId)
        {
            throw new InvalidOperationException("The prepare stage changed the panel ID.");
        }

        if (!string.Equals(prepared.Original.Sha256, imported.Original.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The prepare stage changed the immutable original image.");
        }

        if (!enhancementEnabled && prepared.Enhanced is not null)
        {
            throw new InvalidOperationException("The prepare stage produced enhancement while enhancement was disabled.");
        }
    }

    private static void ValidateDetectionBatch(
        WorkflowRunRequest request,
        WorkflowPreparedPanel prepared,
        WorkflowDetectionBatch detection,
        WorkflowImageVariant expectedVariant)
    {
        if (detection is null)
        {
            throw new InvalidOperationException("The detection stage returned no batch.");
        }

        if (detection.PanelId != prepared.ImportedPanel.PanelId || detection.SourceImage != expectedVariant)
        {
            throw new InvalidOperationException("The detection stage returned a mismatched panel or image variant.");
        }

        WorkflowVisionEnvelope envelope = detection.Envelope;
        if (envelope.RunId != request.RunId || envelope.ProjectId != request.Import.ProjectId)
        {
            throw new InvalidOperationException("The detection stage changed the run or project identity.");
        }

        string expectedInputSha256 = expectedVariant == WorkflowImageVariant.Original
            ? prepared.Original.Sha256
            : prepared.Enhanced?.Sha256
                ?? throw new InvalidOperationException("Enhanced detection requires enhanced image evidence.");
        if (!string.Equals(envelope.InputSha256, expectedInputSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The detection stage changed the input image identity.");
        }

        if (expectedVariant == WorkflowImageVariant.Enhanced &&
            !envelope.Transforms.Any(static transform =>
                string.Equals(transform.OutputCoordinateSpace, "original_pixels", StringComparison.Ordinal) &&
                transform.OutputToInputMatrix is not null))
        {
            throw new InvalidOperationException("Enhanced detections require reversible original-pixel transform provenance.");
        }
    }
}
