// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.App.Integration.Workflow;

public interface IProductionWorkflowDetectionAdapter
{
    string AdapterId { get; }

    bool IsApproved { get; }

    Task<WorkflowDetectionBatch> DetectAsync(
        ProductionWorkflowDetectionRequest request,
        CancellationToken cancellationToken);
}

public sealed class ProductionWorkflowDetectionRequest
{
    private readonly byte[] imageBytes;

    public ProductionWorkflowDetectionRequest(
        WorkflowPreparedPanel panel,
        WorkflowImageEvidence image,
        WorkflowImageVariant imageVariant,
        Guid runId,
        Guid projectId,
        byte[] imageBytes,
        IEnumerable<WorkflowTransformProvenance>? transforms = null)
    {
        Panel = panel ?? throw new ArgumentNullException(nameof(panel));
        Image = image ?? throw new ArgumentNullException(nameof(image));
        ImageVariant = imageVariant;
        RunId = runId;
        ProjectId = projectId;
        ArgumentNullException.ThrowIfNull(imageBytes);
        this.imageBytes = (byte[])imageBytes.Clone();
        Transforms = Array.AsReadOnly((transforms ?? []).ToArray());
    }

    public WorkflowPreparedPanel Panel { get; }

    public WorkflowImageEvidence Image { get; }

    public WorkflowImageVariant ImageVariant { get; }

    public Guid RunId { get; }

    public Guid ProjectId { get; }

    public IReadOnlyList<WorkflowTransformProvenance> Transforms { get; }

    internal bool DeferExportEvidenceCommit { get; init; }

    public byte[] CopyImageBytes() => (byte[])imageBytes.Clone();
}

public sealed class ProductionWorkflowDetectionStage : IWorkflowDetectionStage
{
    private readonly ProductionWorkflowPanelStore panelStore;
    private readonly IProductionWorkflowDetectionAdapter? adapter;

    public ProductionWorkflowDetectionStage(
        ProductionWorkflowPanelStore panelStore,
        IProductionWorkflowDetectionAdapter? adapter = null)
    {
        this.panelStore = panelStore ?? throw new ArgumentNullException(nameof(panelStore));
        this.adapter = adapter;
    }

    public Task<WorkflowDetectionBatch> DetectAsync(
        WorkflowPreparedPanel panel,
        WorkflowImageVariant imageVariant,
        Guid runId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(panel);
        cancellationToken.ThrowIfCancellationRequested();
        if (imageVariant is not (WorkflowImageVariant.Original or WorkflowImageVariant.Enhanced))
        {
            throw new ArgumentOutOfRangeException(nameof(imageVariant));
        }

        if (runId == Guid.Empty || projectId == Guid.Empty)
        {
            throw new ArgumentException("Run and project IDs are required.");
        }

        if (adapter is null || !adapter.IsApproved)
        {
            string adapterStatus = adapter is null
                ? "No production detection adapter is configured."
                : $"Detection adapter '{adapter.AdapterId}' is not approved.";
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
                "Errors.ModelNotFound",
                adapterStatus,
                "Install checksum-verified approved production models or continue in manual mode.");
        }

        return WorkflowDetectionExecution.ExecuteAsync(
            panelStore,
            panel,
            imageVariant,
            runId,
            projectId,
            adapter.DetectAsync,
            cancellationToken);
    }

    private static ProductionWorkflowStageException Failure(
        string code,
        string userMessageKey,
        string technicalMessage,
        string suggestedAction) =>
        new(new ProductionWorkflowFailure(
            code,
            userMessageKey,
            technicalMessage,
            Recoverable: true,
            suggestedAction));
}
