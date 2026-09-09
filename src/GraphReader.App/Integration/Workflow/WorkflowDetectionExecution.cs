// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;

namespace GraphReader.App.Integration.Workflow;

internal static class WorkflowDetectionExecution
{
    internal static async Task<WorkflowDetectionBatch> ExecuteAsync(
        ProductionWorkflowPanelStore panelStore,
        WorkflowPreparedPanel panel,
        WorkflowImageVariant imageVariant,
        Guid runId,
        Guid projectId,
        Func<ProductionWorkflowDetectionRequest, CancellationToken, Task<WorkflowDetectionBatch>> detect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(panelStore);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(detect);
        cancellationToken.ThrowIfCancellationRequested();
        if (imageVariant is not (WorkflowImageVariant.Original or WorkflowImageVariant.Enhanced))
        {
            throw new ArgumentOutOfRangeException(nameof(imageVariant));
        }
        if (runId == Guid.Empty || projectId == Guid.Empty)
        {
            throw new ArgumentException("Run and project IDs are required.");
        }

        ProductionPanelEvidence stored = panelStore.Get(panel.ImportedPanel.PanelId);
        WorkflowImageEvidence image;
        byte[]? bytes;
        IReadOnlyList<WorkflowTransformProvenance> transforms;
        if (imageVariant == WorkflowImageVariant.Original)
        {
            image = panel.Original;
            bytes = stored.CopyOriginalBytes();
            transforms = [];
        }
        else
        {
            image = panel.Enhanced ?? throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.EnhancementUnavailable",
                "Enhanced detection was requested without retained enhanced evidence.",
                "Run detection on the immutable original or prepare an approved derivative.");
            bytes = stored.CopyEnhancedBytes();
            transforms = stored.EnhancementTransforms;
        }

        if (bytes is null || !ChecksumMatches(bytes, image.Sha256))
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.SourceChanged",
                "Retained detection bytes do not match the selected image checksum.",
                "Re-import the source and retry detection.");
        }

        var detectionRequest = new ProductionWorkflowDetectionRequest(
            panel,
            image,
            imageVariant,
            runId,
            projectId,
            bytes,
            transforms)
        {
            DeferExportEvidenceCommit = true,
        };
        WorkflowDetectionBatch batch = await detect(
                detectionRequest,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (batch is null ||
            batch.PanelId != panel.ImportedPanel.PanelId ||
            batch.SourceImage != imageVariant ||
            batch.Envelope.RunId != runId ||
            batch.Envelope.ProjectId != projectId ||
            !string.Equals(batch.Envelope.InputSha256, image.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(batch.CoordinateSpace, "original_pixels", StringComparison.Ordinal))
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "The detection adapter returned evidence for a different run, project, panel, image, or coordinate space.",
                "Reject the adapter result and rerun after verifying model composition.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (batch.PendingExportEvidence is { } pendingExportEvidence)
        {
            panelStore.SetExportEvidence(panel.ImportedPanel.PanelId, pendingExportEvidence);
        }

        return batch;
    }

    private static bool ChecksumMatches(byte[] bytes, string expected) =>
        string.Equals(
            Convert.ToHexString(SHA256.HashData(bytes)),
            expected,
            StringComparison.OrdinalIgnoreCase);

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
