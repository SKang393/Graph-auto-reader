// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using GraphReader.App.Integration.Workflow;

namespace GraphReader.RealAcceptance.Ocr;

/// <summary>
/// Connects an image-only evaluation callback to the actual desktop workflow.
/// Candidate authentication and private/sealed authorization belong to the
/// enclosing runner before it creates a runtime or opens an input project.
/// This adapter has no access to reference anchors, points, curves or labels.
/// </summary>
internal sealed class FrozenCandidateGroupedWorkflowAdapter
{
    private readonly WorkflowOrchestrator workflow;
    private readonly string outputRoot;
    private readonly Guid projectId;

    internal FrozenCandidateGroupedWorkflowAdapter(
        WorkflowOrchestrator workflow,
        string repositoryRoot,
        string newOutputRoot,
        string candidateIdentity)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(newOutputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateIdentity);
        string privateRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "artifacts", "private-acceptance"));
        outputRoot = Path.GetFullPath(newOutputRoot);
        if (!outputRoot.StartsWith(privateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            throw new InvalidOperationException("GROUPED_WORKFLOW_NEW_PRIVATE_OUTPUT_REQUIRED");
        }
        this.workflow = workflow;
        projectId = ProductionWorkflowPanelStore.CreateStableId(
            "grouped-frozen-workflow-project-v1", candidateIdentity);
        Directory.CreateDirectory(outputRoot);
    }

    internal async Task<WholeWorkflowCaseOutput?> ExecuteAsync(
        EngaugeGroupedWorkflowImageInput image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] sourceBytes = image.CopyImageBytes();
        string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        if (!string.Equals(actualSha256, image.SourceSha256, StringComparison.Ordinal))
        {
            return Failure(image, "GROUPED_WORKFLOW_SOURCE_CHECKSUM_MISMATCH");
        }
        string caseRoot = Path.Combine(outputRoot, actualSha256);
        if (Directory.Exists(caseRoot) || File.Exists(caseRoot))
        {
            return Failure(image, "GROUPED_WORKFLOW_SOURCE_ALREADY_EXECUTED");
        }
        Directory.CreateDirectory(caseRoot);
        string sourcePath = Path.Combine(caseRoot, "source.png");
        await using (var writer = new FileStream(sourcePath, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await writer.WriteAsync(sourceBytes, cancellationToken).ConfigureAwait(false);
        }
        using var sourceLock = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(sourceLock)),
                actualSha256, StringComparison.Ordinal))
        {
            return Failure(image, "GROUPED_WORKFLOW_SNAPSHOT_CHECKSUM_MISMATCH");
        }
        Guid sourceId = ProductionWorkflowPanelStore.CreateStableId(
            "grouped-frozen-workflow-source-v1", projectId.ToString("D"), actualSha256);
        Guid runId = ProductionWorkflowPanelStore.CreateStableId(
            "grouped-frozen-workflow-run-v1", projectId.ToString("D"), sourceId.ToString("D"));
        var request = new WorkflowRunRequest(runId,
            new WorkflowImportRequest(projectId,
                [new WorkflowSourceRequest(sourceId, WorkflowSourceKind.Image, sourcePath)],
                enhancementEnabled: false));
        WorkflowRunResult detected = await workflow.RunThroughReviewAsync(
            request, previousReview: null, cancellationToken).ConfigureAwait(false);
        if (detected.Review.CorrectionJournal.Count != 0)
        {
            return Failure(image, "GROUPED_WORKFLOW_UNEXPECTED_REVIEW_CORRECTION");
        }
        WorkflowExportResult exported = await workflow.ExportAsync(detected.Review,
            new WorkflowExportRequest(
                ProductionWorkflowPanelStore.CreateStableId("grouped-frozen-workflow-export-v1", runId.ToString("D")),
                Path.Combine(caseRoot, "export")), cancellationToken).ConfigureAwait(false);
        sourceLock.Position = 0;
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(sourceLock)),
                actualSha256, StringComparison.Ordinal))
        {
            return Failure(image, "GROUPED_WORKFLOW_SOURCE_CHANGED");
        }
        WholeWorkflowCsvArtifact[] artifacts = exported.Artifacts.Select(artifact => new WholeWorkflowCsvArtifact(
            artifact.FileName, artifact.Sha256, artifact.RowCount, artifact.WrittenPath ?? string.Empty)).ToArray();
        return new WholeWorkflowCaseOutput(image.CaseKey, actualSha256,
            exported.Succeeded, exported.Succeeded ? null : EngaugeGroupedWorkflowExecutor.ExportFailed, artifacts);
    }

    private static WholeWorkflowCaseOutput Failure(EngaugeGroupedWorkflowImageInput image, string code) =>
        new(image.CaseKey, image.SourceSha256, false, code, []);
}
