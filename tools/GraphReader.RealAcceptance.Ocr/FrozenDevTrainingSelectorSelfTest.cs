// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GraphReader.RealAcceptance.Ocr;

internal static class FrozenDevTrainingSelectorSelfTest
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static object Run()
    {
        // These paths are deliberately nonexistent. No file or corpus payload
        // is opened by the injected metadata-only inventory.
        string root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "synthetic-selector-self-test"));
        string devPath = Path.Combine(root, "synthetic-study-a", "synthetic-case.dig");
        string sealedPath = Path.Combine(root, "synthetic-study-b", "sealed-case.dig");
        string caseId = new('a', 64);
        string studyId = new('b', 64);
        var dev = new FrozenRealCorpusProject(devPath, caseId, studyId, FrozenRealCorpusInventory.RealDev);
        var sealedCase = new FrozenRealCorpusProject(sealedPath, new string('c', 64), new string('d', 64), FrozenRealCorpusInventory.RealSealed);
        FrozenRealCorpusSelection initial = Selection(dev);
        FrozenRealCorpusSelection current = initial;
        int metadataLoads = 0;
        var selector = FrozenDevTrainingSelector.CreateForTest(_ => { metadataLoads++; return current; }, CancellationToken.None);

        FrozenDevTrainingMetadata metadata = selector.Describe();
        string serialized = JsonSerializer.Serialize(metadata, JsonOptions);
        using JsonDocument json = JsonDocument.Parse(serialized);
        Require(metadata.Cases.Count == 1 && metadata.Cases[0] == new FrozenDevTrainingCase(caseId, studyId) &&
            !metadata.CandidateAuthorized && !metadata.SealedSourceOverlapVerified &&
            json.RootElement.GetProperty("candidate_authorized").ValueKind == JsonValueKind.False &&
            json.RootElement.GetProperty("sealed_source_overlap_verified").ValueKind == JsonValueKind.False &&
            json.RootElement.GetProperty("cases")[0].GetProperty("case_id").GetString() == caseId &&
            json.RootElement.GetProperty("cases")[0].GetProperty("study_id").GetString() == studyId &&
            !serialized.Contains(root, StringComparison.OrdinalIgnoreCase) &&
            !serialized.Contains("synthetic-study", StringComparison.OrdinalIgnoreCase), "metadata_is_anonymized");
        Require(selector.EnumerateSelectedProjects(CancellationToken.None).Single() == dev,
            "enumeration_returns_selected_project");
        Require(ReferenceEquals(selector.RequireProject(devPath, caseId, studyId, CancellationToken.None), dev) &&
            metadataLoads == 3, "selected_path_and_identifiers_are_revalidated");

        ExpectFailure(() => selector.RequireProject(sealedPath, sealedCase.AnonymizedCaseId, sealedCase.AnonymizedStudyId, CancellationToken.None),
            "FROZEN_DEV_TRAINING_SOURCE_NOT_SELECTED");
        ExpectFailure(() => selector.RequireProject(devPath, caseId, sealedCase.AnonymizedStudyId, CancellationToken.None),
            "FROZEN_DEV_TRAINING_SOURCE_NOT_SELECTED");
        ExpectFailure(() => selector.RequireProject(Path.Combine(root, "synthetic-study-a", ".", "synthetic-case.dig"), caseId, studyId, CancellationToken.None),
            "FROZEN_DEV_TRAINING_SOURCE_NOT_SELECTED");
        Require(metadataLoads == 3, "unselected_requests_rejected_before_reload");

        current = Selection(new FrozenRealCorpusProject(Path.Combine(root, "changed.dig"), caseId, studyId, FrozenRealCorpusInventory.RealDev));
        ExpectFailure(() => selector.RequireProject(devPath, caseId, studyId, CancellationToken.None),
            "FROZEN_DEV_TRAINING_INVENTORY_CHANGED");
        current = Selection(dev, assignment: new string('e', 64));
        ExpectFailure(() => selector.RequireProject(devPath, caseId, studyId, CancellationToken.None),
            "FROZEN_DEV_TRAINING_INVENTORY_CHANGED");
        ExpectFailure(() => FrozenDevTrainingSelector.CreateForTest(_ => Selection(sealedCase), CancellationToken.None),
            "FROZEN_DEV_TRAINING_INVALID_SELECTION");
        ExpectFailure(() => FrozenDevTrainingSelector.CreateForTest(_ => Selection(dev, dev), CancellationToken.None),
            "FROZEN_DEV_TRAINING_DUPLICATE_CASE");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            selector.RequireProject(devPath, caseId, studyId, cancellation.Token);
            throw new InvalidOperationException("FROZEN_DEV_TRAINING_CANCELLATION_NOT_OBSERVED");
        }
        catch (OperationCanceledException) { }

        string inventoryRoot = Path.Combine(Path.GetTempPath(), $"frozen-dev-selector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(inventoryRoot);
        try { ExerciseFrozenInventory(inventoryRoot); }
        finally { Directory.Delete(inventoryRoot); }

        return new
        {
            status = "pass",
            scope = "synthetic-metadata-only",
            sealed_payload_reads = 0,
            private_payload_reads = 0,
            optimizer_steps = 0,
            source_overlap_verified = false,
            candidate_authorized = false,
        };
    }

    private static void ExerciseFrozenInventory(string root)
    {
        // Synthetic virtual paths and attributes exercise the real assignment
        // algorithm without creating or opening any project payload.
        string[] paths = [
            Path.Combine(root, "study-alpha", "one.dig"),
            Path.Combine(root, "study-alpha", "two.dig"),
            Path.Combine(root, "study-beta", "one.dig"),
            Path.Combine(root, "study-beta", "two.dig"),
        ];
        string sealedStudy = string.CompareOrdinal(Hash("study-alpha"), Hash("study-beta")) < 0
            ? "study-alpha" : "study-beta";
        string Material(string path)
        {
            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            string study = relative.Split('/')[0];
            return $"{Hash(relative)}={(study == sealedStudy ? FrozenRealCorpusInventory.RealSealed : FrozenRealCorpusInventory.RealDev)}";
        }
        var expectation = new FrozenRealCorpusExpectation(4, 2, 2,
            Hash(string.Join("\n", paths.OrderBy(static p => p, StringComparer.Ordinal).Select(Material))));
        FrozenRealCorpusSelection LoadSynthetic(CancellationToken token) => FrozenRealCorpusInventory.BuildForTest(
            root, paths, FrozenRealCorpusInventory.RealDev, expectation, _ => FileAttributes.Normal, token);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.CreateForTest(LoadSynthetic, CancellationToken.None);
        FrozenRealCorpusSelection selection = LoadSynthetic(CancellationToken.None);
        Require(selection.SelectedProjects.Count == 2 && selection.SelectedProjects
                .Select(static p => p.AnonymizedStudyId).Distinct(StringComparer.Ordinal).Count() == 1 &&
            selection.SelectedProjects.All(p => !p.GetProjectPath().Contains(sealedStudy, StringComparison.Ordinal)),
            "frozen_inventory_excludes_whole_sealed_study");
        foreach (FrozenRealCorpusProject project in selection.SelectedProjects)
        {
            Require(selector.RequireProject(project.GetProjectPath(), project.AnonymizedCaseId,
                project.AnonymizedStudyId, CancellationToken.None).AnonymizedCaseId == project.AnonymizedCaseId,
                "actual_inventory_identity_is_resolvable");
        }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static FrozenRealCorpusSelection Selection(
        FrozenRealCorpusProject first,
        FrozenRealCorpusProject? second = null,
        string? assignment = null)
    {
        FrozenRealCorpusProject[] projects = second is null ? [first] : [first, second];
        return new FrozenRealCorpusSelection(FrozenRealCorpusInventory.RealDev, projects.Length + 1,
            projects.Length, 1, assignment ?? new string('1', 64), new string('2', 64), projects);
    }

    private static void ExpectFailure(Action action, string code)
    {
        try { action(); }
        catch (InvalidDataException exception) when (exception.Message == code) { return; }
        throw new InvalidOperationException($"{code}_NOT_REJECTED");
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) { throw new InvalidOperationException($"FROZEN_DEV_TRAINING_SELF_TEST_FAILED:{check}"); }
    }
}
