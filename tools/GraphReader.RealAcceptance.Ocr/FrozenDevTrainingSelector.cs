// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.IO;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record FrozenDevTrainingCase(string CaseId, string StudyId);

internal sealed record FrozenDevTrainingMetadata(
    string AssignmentSha256,
    string SelectedInventorySha256,
    IReadOnlyList<FrozenDevTrainingCase> Cases,
    bool SealedSourceOverlapVerified,
    bool CandidateAuthorized);

/// <summary>
/// Metadata-only selection of the existing real-dev split. A training caller
/// must resolve every requested source through this instance; this neither
/// reads payloads nor authorizes a candidate, model, or optimizer run.
/// </summary>
internal sealed class FrozenDevTrainingSelector
{
    private readonly Func<CancellationToken, FrozenRealCorpusSelection> load;
    private readonly FrozenRealCorpusSelection initial;
    private readonly ReadOnlyDictionary<string, FrozenRealCorpusProject> selected;

    private FrozenDevTrainingSelector(Func<CancellationToken, FrozenRealCorpusSelection> load, CancellationToken token)
    {
        this.load = load;
        initial = Validate(load(token));
        selected = Index(initial);
    }

    internal static FrozenDevTrainingSelector Load(string corpusRoot, CancellationToken token)
    {
        string root = Path.GetFullPath(corpusRoot);
        return new(t => FrozenRealCorpusInventory.Load(root, FrozenRealCorpusInventory.RealDev, t), token);
    }

    // Test seam only. Production callers must use Load, which enforces the
    // frozen count and assignment fingerprint in FrozenRealCorpusInventory.
    internal static FrozenDevTrainingSelector CreateForTest(
        Func<CancellationToken, FrozenRealCorpusSelection> load,
        CancellationToken token) => new(load, token);

    internal FrozenDevTrainingMetadata Describe() => new(
        initial.AssignmentSha256,
        initial.SelectedInventorySha256,
        Array.AsReadOnly(initial.SelectedProjects
            .Select(static p => new FrozenDevTrainingCase(p.AnonymizedCaseId, p.AnonymizedStudyId))
            .ToArray()),
        SealedSourceOverlapVerified: false,
        CandidateAuthorized: false);

    // Returned objects stay in-process for a future loader. Only Describe()
    // may be serialized; selected source paths are private local metadata.
    internal IReadOnlyList<FrozenRealCorpusProject> EnumerateSelectedProjects(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        FrozenRealCorpusSelection current = Validate(load(token));
        ReadOnlyDictionary<string, FrozenRealCorpusProject> currentIndex = Index(current);
        if (!string.Equals(initial.AssignmentSha256, current.AssignmentSha256, StringComparison.Ordinal) ||
            !string.Equals(initial.SelectedInventorySha256, current.SelectedInventorySha256, StringComparison.Ordinal) ||
            selected.Count != currentIndex.Count || selected.Any(pair =>
                !currentIndex.TryGetValue(pair.Key, out FrozenRealCorpusProject? currentProject) ||
                !string.Equals(pair.Value.GetProjectPath(), currentProject.GetProjectPath(), StringComparison.Ordinal) ||
                !string.Equals(pair.Value.AnonymizedStudyId, currentProject.AnonymizedStudyId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("FROZEN_DEV_TRAINING_INVENTORY_CHANGED");
        }
        return Array.AsReadOnly(current.SelectedProjects.ToArray());
    }

    internal FrozenRealCorpusProject RequireProject(
        string canonicalPath,
        string caseId,
        string studyId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(canonicalPath) || !Path.IsPathFullyQualified(canonicalPath) ||
            string.IsNullOrWhiteSpace(caseId) || string.IsNullOrWhiteSpace(studyId) ||
            !selected.TryGetValue(caseId, out FrozenRealCorpusProject? original) ||
            !string.Equals(canonicalPath, original.GetProjectPath(), StringComparison.Ordinal) ||
            !string.Equals(studyId, original.AnonymizedStudyId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("FROZEN_DEV_TRAINING_SOURCE_NOT_SELECTED");
        }

        return EnumerateSelectedProjects(token).Single(project =>
            string.Equals(project.AnonymizedCaseId, caseId, StringComparison.Ordinal));
    }

    private static FrozenRealCorpusSelection Validate(FrozenRealCorpusSelection value)
    {
        if (value is null || value.SelectedSplit != FrozenRealCorpusInventory.RealDev ||
            value.SelectedProjects.Count != value.RealDevCount ||
            value.SelectedProjects.Any(static p => p.Split != FrozenRealCorpusInventory.RealDev))
        {
            throw new InvalidDataException("FROZEN_DEV_TRAINING_INVALID_SELECTION");
        }
        return value;
    }

    private static ReadOnlyDictionary<string, FrozenRealCorpusProject> Index(FrozenRealCorpusSelection selection)
    {
        var result = new Dictionary<string, FrozenRealCorpusProject>(StringComparer.Ordinal);
        foreach (FrozenRealCorpusProject project in selection.SelectedProjects)
        {
            if (!result.TryAdd(project.AnonymizedCaseId, project))
            {
                throw new InvalidDataException("FROZEN_DEV_TRAINING_DUPLICATE_CASE");
            }
        }
        return new ReadOnlyDictionary<string, FrozenRealCorpusProject>(result);
    }
}
