// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed class FrozenRealCorpusProject
{
    private readonly string projectPath;

    internal FrozenRealCorpusProject(
        string projectPath,
        string anonymizedCaseId,
        string anonymizedStudyId,
        string split)
    {
        this.projectPath = projectPath;
        AnonymizedCaseId = anonymizedCaseId;
        AnonymizedStudyId = anonymizedStudyId;
        Split = split;
    }

    internal string AnonymizedCaseId { get; }
    internal string AnonymizedStudyId { get; }
    internal string Split { get; }
    internal string GetProjectPath() => projectPath;
}

internal sealed class FrozenRealCorpusSelection
{
    internal FrozenRealCorpusSelection(
        string selectedSplit,
        int projectCount,
        int realDevCount,
        int realSealedCount,
        string assignmentSha256,
        string selectedInventorySha256,
        IReadOnlyList<FrozenRealCorpusProject> selectedProjects)
    {
        SelectedSplit = selectedSplit;
        ProjectCount = projectCount;
        RealDevCount = realDevCount;
        RealSealedCount = realSealedCount;
        AssignmentSha256 = assignmentSha256;
        SelectedInventorySha256 = selectedInventorySha256;
        SelectedProjects = Array.AsReadOnly(selectedProjects.ToArray());
    }

    internal string SelectedSplit { get; }
    internal int ProjectCount { get; }
    internal int RealDevCount { get; }
    internal int RealSealedCount { get; }
    internal string AssignmentSha256 { get; }
    internal string SelectedInventorySha256 { get; }
    internal IReadOnlyList<FrozenRealCorpusProject> SelectedProjects { get; }
}

internal sealed record FrozenRealCorpusExpectation(
    int ProjectCount,
    int RealDevCount,
    int RealSealedCount,
    string AssignmentSha256);

/// <summary>
/// Selects the frozen real-corpus split from file-system metadata only. This
/// type never opens a project payload and does not authorize private or sealed
/// access. The enclosing runner owns authorization and first-read accounting.
/// </summary>
internal static class FrozenRealCorpusInventory
{
    internal const int ExpectedProjectCount = 171;
    internal const int ExpectedRealDevCount = 120;
    internal const int ExpectedRealSealedCount = 51;
    internal const string FrozenAssignmentSha256 =
        "decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a";
    internal const string RealDev = "real-dev";
    internal const string RealSealed = "real-sealed";

    private static readonly FrozenRealCorpusExpectation ProductionExpectation = new(
        ExpectedProjectCount,
        ExpectedRealDevCount,
        ExpectedRealSealedCount,
        FrozenAssignmentSha256);

    internal static FrozenRealCorpusSelection Load(
        string corpusRoot,
        string selectedSplit,
        CancellationToken cancellationToken) =>
        LoadWithExpectation(corpusRoot, selectedSplit, ProductionExpectation, cancellationToken);

    internal static FrozenRealCorpusSelection LoadWithExpectation(
        string corpusRoot,
        string selectedSplit,
        FrozenRealCorpusExpectation expectation,
        CancellationToken cancellationToken)
    {
        string root = ValidateRoot(corpusRoot);
        string[] paths = EnumerateProjectPaths(root, cancellationToken);
        return Build(root, paths, selectedSplit, expectation, File.GetAttributes, cancellationToken);
    }

    internal static FrozenRealCorpusSelection BuildForTest(
        string corpusRoot,
        IReadOnlyList<string> projectPaths,
        string selectedSplit,
        FrozenRealCorpusExpectation expectation,
        Func<string, FileAttributes> getAttributes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        ArgumentNullException.ThrowIfNull(getAttributes);
        string root = ValidateRoot(corpusRoot);
        return Build(root, projectPaths, selectedSplit, expectation, getAttributes, cancellationToken);
    }

    private static FrozenRealCorpusSelection Build(
        string root,
        IReadOnlyList<string> projectPaths,
        string selectedSplit,
        FrozenRealCorpusExpectation expectation,
        Func<string, FileAttributes> getAttributes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selectedSplit is not (RealDev or RealSealed))
        {
            throw new InvalidDataException("FROZEN_REAL_CORPUS_SPLIT_UNSUPPORTED");
        }
        ValidateExpectation(expectation);

        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var records = new List<ProjectRecord>(projectPaths.Count);
        foreach (string suppliedPath in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(suppliedPath))
            {
                throw new InvalidDataException("FROZEN_REAL_CORPUS_PATH_INVALID");
            }

            string fullPath = Path.GetFullPath(suppliedPath);
            if (!uniquePaths.Add(fullPath))
            {
                throw new InvalidDataException("FROZEN_REAL_CORPUS_DUPLICATE_PATH");
            }
            string relativePath = RequireCanonicalProjectPath(root, fullPath);
            RejectReparseChain(root, fullPath, checkedPaths, getAttributes);
            string study = relativePath.Split(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)[0];
            string caseId = Hash(relativePath.Replace(Path.DirectorySeparatorChar, '/'));
            records.Add(new ProjectRecord(fullPath, relativePath, caseId, Hash(study)));
        }

        ProjectRecord[] ordered = records
            .OrderBy(static item => item.FullPath, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length != expectation.ProjectCount)
        {
            throw new InvalidDataException("FROZEN_REAL_CORPUS_PROJECT_COUNT_MISMATCH");
        }

        Dictionary<string, ProjectRecord[]> studies = ordered
            .GroupBy(static item => item.AnonymizedStudyId, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
        var sealedPaths = new HashSet<string>(StringComparer.Ordinal);
        int sealedCount = 0;
        foreach (ProjectRecord[] study in studies.Values)
        {
            if (sealedCount < expectation.RealSealedCount &&
                (sealedCount + study.Length <= expectation.RealSealedCount || sealedPaths.Count == 0))
            {
                foreach (ProjectRecord item in study)
                {
                    sealedPaths.Add(item.FullPath);
                }
                sealedCount += study.Length;
            }
        }

        var assignments = ordered.ToDictionary(
            static item => item.FullPath,
            item => sealedPaths.Contains(item.FullPath) ? RealSealed : RealDev,
            StringComparer.Ordinal);
        int actualDev = assignments.Values.Count(static split => split == RealDev);
        int actualSealed = assignments.Values.Count(static split => split == RealSealed);
        if (actualDev != expectation.RealDevCount || actualSealed != expectation.RealSealedCount ||
            actualDev + actualSealed != expectation.ProjectCount)
        {
            throw new InvalidDataException("FROZEN_REAL_CORPUS_SPLIT_COUNT_MISMATCH");
        }
        if (studies.Values.Any(study => study
                .Select(item => assignments[item.FullPath])
                .Distinct(StringComparer.Ordinal).Count() != 1))
        {
            throw new InvalidDataException("FROZEN_REAL_CORPUS_STUDY_SPLIT_CONFLICT");
        }

        string assignmentSha256 = Hash(string.Join("\n", ordered.Select(item =>
            $"{item.AnonymizedCaseId}={assignments[item.FullPath]}")));
        if (!string.Equals(assignmentSha256, expectation.AssignmentSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("FROZEN_REAL_CORPUS_ASSIGNMENT_MISMATCH");
        }

        FrozenRealCorpusProject[] selected = ordered
            .Where(item => string.Equals(assignments[item.FullPath], selectedSplit, StringComparison.Ordinal))
            .Select(item => new FrozenRealCorpusProject(
                item.FullPath,
                item.AnonymizedCaseId,
                item.AnonymizedStudyId,
                selectedSplit))
            .ToArray();
        string selectedInventorySha256 = Hash(string.Join("\n", selected.Select(item =>
            $"{item.AnonymizedCaseId}={item.Split}")));
        return new FrozenRealCorpusSelection(
            selectedSplit,
            ordered.Length,
            actualDev,
            actualSealed,
            assignmentSha256,
            selectedInventorySha256,
            new ReadOnlyCollection<FrozenRealCorpusProject>(selected));
    }

    private static string ValidateRoot(string corpusRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(corpusRoot));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("FROZEN_REAL_CORPUS_ROOT_MISSING");
        }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("FROZEN_REAL_CORPUS_REPARSE_POINT_REJECTED");
        }
        return root;
    }

    private static string[] EnumerateProjectPaths(string root, CancellationToken cancellationToken)
    {
        var projects = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         directory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("FROZEN_REAL_CORPUS_REPARSE_POINT_REJECTED");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else if (string.Equals(Path.GetExtension(entry), ".dig", StringComparison.OrdinalIgnoreCase))
                {
                    projects.Add(Path.GetFullPath(entry));
                }
            }
        }
        return projects.ToArray();
    }

    private static string RequireCanonicalProjectPath(string root, string fullPath)
    {
        string relativePath = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relativePath) || relativePath is "." or "" ||
            relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(static part => part is "" or "." or "..") ||
            !string.Equals(Path.GetExtension(relativePath), ".dig", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFullPath(Path.Combine(root, relativePath)),
                fullPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("FROZEN_REAL_CORPUS_PATH_ESCAPE");
        }
        return relativePath;
    }

    private static void RejectReparseChain(
        string root,
        string fullPath,
        HashSet<string> checkedPaths,
        Func<string, FileAttributes> getAttributes)
    {
        string? current = fullPath;
        while (current is not null)
        {
            if (checkedPaths.Add(current) && (getAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("FROZEN_REAL_CORPUS_REPARSE_POINT_REJECTED");
            }
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidDataException("FROZEN_REAL_CORPUS_PATH_ESCAPE");
    }

    private static void ValidateExpectation(FrozenRealCorpusExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        if (expectation.ProjectCount <= 0 || expectation.RealDevCount < 0 ||
            expectation.RealSealedCount <= 0 ||
            expectation.RealDevCount + expectation.RealSealedCount != expectation.ProjectCount ||
            expectation.AssignmentSha256.Length != 64 ||
            expectation.AssignmentSha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("FROZEN_REAL_CORPUS_EXPECTATION_INVALID");
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record ProjectRecord(
        string FullPath,
        string RelativePath,
        string AnonymizedCaseId,
        string AnonymizedStudyId);
}
