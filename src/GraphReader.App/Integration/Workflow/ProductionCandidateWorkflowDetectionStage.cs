// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.App.Integration.Workflow;

/// <summary>
/// Runs a checksum-bound candidate without making it production-approved.
/// This type is internal so normal application composition cannot select it.
/// </summary>
internal sealed class ProductionCandidateWorkflowDetectionStage : IWorkflowDetectionStage
{
    private readonly ProductionWorkflowPanelStore panelStore;
    private readonly ProductionAutomaticDetectionAdapter candidateAdapter;

    internal ProductionCandidateWorkflowDetectionStage(
        ProductionWorkflowPanelStore panelStore,
        ProductionAutomaticDetectionAdapter candidateAdapter)
    {
        this.panelStore = panelStore ?? throw new ArgumentNullException(nameof(panelStore));
        this.candidateAdapter = candidateAdapter ?? throw new ArgumentNullException(nameof(candidateAdapter));
        if (candidateAdapter.IsApproved)
        {
            throw new ArgumentException(
                "Candidate workflow execution requires an explicitly unapproved adapter.",
                nameof(candidateAdapter));
        }
    }

    public Task<WorkflowDetectionBatch> DetectAsync(
        WorkflowPreparedPanel panel,
        WorkflowImageVariant imageVariant,
        Guid runId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (candidateAdapter.IsApproved)
        {
            throw Failure(
                "The candidate adapter changed approval state after composition.");
        }
        if (imageVariant != WorkflowImageVariant.Original)
        {
            throw Failure("Candidate workflow evaluation requires the immutable original image.");
        }

        return WorkflowDetectionExecution.ExecuteAsync(
            panelStore,
            panel,
            imageVariant,
            runId,
            projectId,
            candidateAdapter.DetectForCandidateEvaluationAsync,
            cancellationToken);
    }

    private static ProductionWorkflowStageException Failure(string technicalMessage) =>
        new(new ProductionWorkflowFailure(
            ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
            "Errors.DetectionEvidenceRejected",
            technicalMessage,
            Recoverable: true,
            "Recreate the unapproved candidate composition from its frozen binding."));
}
