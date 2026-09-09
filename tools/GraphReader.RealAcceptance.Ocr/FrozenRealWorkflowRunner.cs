// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record FrozenRealWorkflowRunnerDependencies(
    Func<string, string, CancellationToken, FrozenRealCorpusSelection> LoadInventory,
    Func<string, string, Action?, CancellationToken, EngaugeDigWholeWorkflowTruth> ReadProject,
    Func<IReadOnlyList<EngaugeDigWholeWorkflowTruth>, CancellationToken,
        IReadOnlyList<EngaugeWorkflowImageGroup>> GroupProjects,
    Func<IReadOnlyList<EngaugeWorkflowImageGroup>, WholeWorkflowEvaluationOptions,
        Func<EngaugeGroupedWorkflowImageInput, CancellationToken, Task<WholeWorkflowCaseOutput?>>,
        CancellationToken, Task<EngaugeGroupedWorkflowAggregateReport>> ExecuteGroupsAsync);

internal sealed record FrozenRealWorkflowRunnerResult(
    EngaugeGroupedWorkflowAggregateReport Aggregate,
    string CorpusContentSha256);

/// <summary>
/// Executes an already admitted image-only candidate and returns aggregate
/// metrics. Admission and the initial metadata inventory check must happen
/// before the outer caller constructs the candidate runtime.
/// </summary>
internal static class FrozenRealWorkflowRunner
{
    internal static Task<FrozenRealWorkflowRunnerResult> RunAsync(
        FrozenRealWorkflowAdmissionResult admission,
        string corpusRoot,
        Func<EngaugeGroupedWorkflowImageInput, CancellationToken, Task<WholeWorkflowCaseOutput?>> executeImageAsync,
        FrozenRealFirstReadHandshake? sealedFirstReadHandshake,
        CancellationToken cancellationToken) =>
        RunCoreAsync(admission, corpusRoot, executeImageAsync, sealedFirstReadHandshake,
            DefaultDependencies(), cancellationToken);

    internal static Task<FrozenRealWorkflowRunnerResult> RunForTestAsync(
        FrozenRealWorkflowAdmissionResult admission,
        string corpusRoot,
        Func<EngaugeGroupedWorkflowImageInput, CancellationToken, Task<WholeWorkflowCaseOutput?>> executeImageAsync,
        FrozenRealFirstReadHandshake? sealedFirstReadHandshake,
        FrozenRealWorkflowRunnerDependencies dependencies,
        CancellationToken cancellationToken) =>
        RunCoreAsync(admission, corpusRoot, executeImageAsync, sealedFirstReadHandshake,
            dependencies, cancellationToken);

    private static async Task<FrozenRealWorkflowRunnerResult> RunCoreAsync(
        FrozenRealWorkflowAdmissionResult admission,
        string corpusRoot,
        Func<EngaugeGroupedWorkflowImageInput, CancellationToken, Task<WholeWorkflowCaseOutput?>> executeImageAsync,
        FrozenRealFirstReadHandshake? sealedFirstReadHandshake,
        FrozenRealWorkflowRunnerDependencies dependencies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        ArgumentNullException.ThrowIfNull(executeImageAsync);
        ArgumentNullException.ThrowIfNull(dependencies);
        cancellationToken.ThrowIfCancellationRequested();
        if (!admission.AggregateOnly || !admission.EvaluationOptions.RequireInMemoryArtifacts ||
            admission.SealedFirstReadRequired !=
                (admission.Split == FrozenRealCorpusInventory.RealSealed) ||
            (admission.SealedFirstReadRequired != (sealedFirstReadHandshake is not null)))
        {
            throw new InvalidDataException("REAL_WORKFLOW_RUNNER_SCOPE_INVALID");
        }

        // This is the second inventory authentication. The outer caller must
        // have performed the first before constructing executeImageAsync.
        FrozenRealCorpusSelection selection = dependencies.LoadInventory(
            corpusRoot, admission.Split, cancellationToken);
        ValidateSelection(admission, selection);

        Action? beforeFirstPayloadRead = sealedFirstReadHandshake is null
            ? null
            : () => sealedFirstReadHandshake.BeforeFirstPayloadRead(cancellationToken);
        var projects = new List<EngaugeDigWholeWorkflowTruth>(selection.SelectedProjects.Count);
        foreach (FrozenRealCorpusProject project in selection.SelectedProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projects.Add(dependencies.ReadProject(
                project.GetProjectPath(),
                project.AnonymizedCaseId,
                beforeFirstPayloadRead,
                cancellationToken));
        }
        if (projects.Count != admission.ProjectCount)
        {
            throw new InvalidDataException("REAL_WORKFLOW_FULL_PROJECT_DENOMINATOR_MISSING");
        }

        // Detect metadata drift that occurred during parsing before any image
        // reaches the candidate runtime.
        FrozenRealCorpusSelection finalSelection = dependencies.LoadInventory(
            corpusRoot, admission.Split, cancellationToken);
        ValidateSelection(admission, finalSelection);
        string corpusContentSha256 = ComputeCorpusContentSha256(
            finalSelection, projects);
        if (sealedFirstReadHandshake is not null)
        {
            sealedFirstReadHandshake.ConfirmCorpusContent(
                corpusContentSha256, cancellationToken);
        }
        IReadOnlyList<EngaugeWorkflowImageGroup> groups = dependencies.GroupProjects(
            projects, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        EngaugeGroupedWorkflowAggregateReport report = await dependencies.ExecuteGroupsAsync(
            groups,
            admission.EvaluationOptions with { RequireInMemoryArtifacts = true },
            executeImageAsync,
            cancellationToken).ConfigureAwait(false);
        ValidateAggregateReport(report, admission, projects.Count);
        return new FrozenRealWorkflowRunnerResult(report, corpusContentSha256);
    }

    private static string ComputeCorpusContentSha256(
        FrozenRealCorpusSelection selection,
        List<EngaugeDigWholeWorkflowTruth> projects)
    {
        if (projects.Count != selection.SelectedProjects.Count)
        {
            throw new InvalidDataException("REAL_WORKFLOW_FULL_PROJECT_DENOMINATOR_MISSING");
        }
        var text = new StringBuilder();
        text.Append("graphreader.frozen-real-workflow-corpus-content.v1\n");
        text.Append(selection.SelectedInventorySha256).Append('\n');
        text.Append(projects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append('\n');
        for (int index = 0; index < projects.Count; index++)
        {
            FrozenRealCorpusProject selected = selection.SelectedProjects[index];
            EngaugeDigWholeWorkflowTruth parsed = projects[index];
            if (parsed.TruthCase.CaseKey != selected.AnonymizedCaseId ||
                parsed.ProjectSha256 is not { Length: 64 } ||
                parsed.ProjectSha256.Any(static character =>
                    character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            {
                throw new InvalidDataException("REAL_WORKFLOW_CORPUS_CONTENT_IDENTITY_INVALID");
            }
            text.Append(selected.AnonymizedCaseId).Append('\n');
            text.Append(parsed.ProjectSha256).Append('\n');
        }
        return FrozenCandidateBinding.Hash(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static FrozenRealWorkflowRunnerDependencies DefaultDependencies() => new(
        FrozenRealCorpusInventory.Load,
        static (path, caseKey, beforeRead, token) =>
            beforeRead is null
                ? EngaugeDigWholeWorkflowTruthAdapter.Read(path, caseKey, token)
                : EngaugeDigWholeWorkflowTruthAdapter.Read(path, caseKey, beforeRead, token),
        EngaugeDigWholeWorkflowGrouping.Build,
        EngaugeGroupedWorkflowExecutor.ExecuteAsync);

    private static void ValidateSelection(
        FrozenRealWorkflowAdmissionResult admission,
        FrozenRealCorpusSelection selection)
    {
        if (selection.SelectedSplit != admission.Split ||
            selection.SelectedProjects.Count != admission.ProjectCount ||
            selection.AssignmentSha256 != admission.AssignmentSha256 ||
            selection.SelectedInventorySha256 != admission.SelectedInventorySha256 ||
            selection.ProjectCount != FrozenRealCorpusInventory.ExpectedProjectCount ||
            selection.RealDevCount != FrozenRealCorpusInventory.ExpectedRealDevCount ||
            selection.RealSealedCount != FrozenRealCorpusInventory.ExpectedRealSealedCount)
        {
            throw new InvalidDataException("REAL_WORKFLOW_METADATA_INVENTORY_CHANGED");
        }
    }

    private static void ValidateAggregateReport(
        EngaugeGroupedWorkflowAggregateReport report,
        FrozenRealWorkflowAdmissionResult admission,
        int projectCount)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Schema != EngaugeGroupedWorkflowExecutor.ReportSchema ||
            !report.AggregateOnly || report.CaseLevelOutput || report.TruthRowsOutput ||
            report.PredictionOutput || report.PathsOutput || report.NamesOutput ||
            report.ProjectFiles != projectCount ||
            report.WorkflowSucceededProjects + report.WorkflowFailedProjects != projectCount ||
            report.Evaluation.TruthCases != report.ImageGroups ||
            report.Evaluation.CompletedCases + report.Evaluation.FailedCases != report.ImageGroups ||
            report.Evaluation.TruthPoints != report.TruthPoints)
        {
            throw new InvalidDataException("REAL_WORKFLOW_AGGREGATE_REPORT_INVALID");
        }
        if (admission.Split == FrozenRealCorpusInventory.RealSealed &&
            report.Evaluation.UnexpectedCases != 0)
        {
            throw new InvalidDataException("REAL_WORKFLOW_SEALED_UNEXPECTED_OUTPUT_REJECTED");
        }
    }
}
