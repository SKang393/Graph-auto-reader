// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GraphReader.RealAcceptance.Ocr;

internal static class FrozenRealCorpusInventorySelfTest
{
    internal static object Run()
    {
        string root = Path.Combine(Path.GetTempPath(), $"graphreader-real-inventory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        try
        {
            string[] paths = CreateFixture(root);
            FrozenRealCorpusExpectation expectation = ReferenceExpectation(root, paths, sealedTarget: 4);
            Dictionary<string, string> assignments = ReferenceAssignments(root, paths, sealedTarget: 4);
            string[] sealedPaths = assignments
                .Where(static item => item.Value == FrozenRealCorpusInventory.RealSealed)
                .Select(static item => item.Key)
                .ToArray();

            var sealedLocks = sealedPaths.Select(path => new FileStream(
                path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)).ToArray();
            try
            {
                FrozenRealCorpusSelection dev = FrozenRealCorpusInventory.LoadWithExpectation(
                    root,
                    FrozenRealCorpusInventory.RealDev,
                    expectation,
                    CancellationToken.None);
                Require(dev.ProjectCount == 12 && dev.RealDevCount == 8 && dev.RealSealedCount == 4 &&
                    dev.SelectedProjects.Count == 8 &&
                    dev.SelectedProjects.All(static item => item.Split == FrozenRealCorpusInventory.RealDev) &&
                    dev.SelectedProjects.All(item => assignments[item.GetProjectPath()] == FrozenRealCorpusInventory.RealDev),
                    "excluded_sealed_payloads_are_never_opened");
                checks.Add("excluded_sealed_payloads_are_never_opened");
                Require(dev.SelectedProjects.Select(static item => item.AnonymizedCaseId)
                        .Distinct(StringComparer.Ordinal).Count() == dev.SelectedProjects.Count &&
                    dev.SelectedProjects.All(static item => item.AnonymizedCaseId.Length == 64) &&
                    dev.AssignmentSha256 == expectation.AssignmentSha256 &&
                    dev.SelectedInventorySha256.Length == 64,
                    "anonymized_selected_inventory_is_immutable_and_bound");
                checks.Add("anonymized_selected_inventory_is_immutable_and_bound");
            }
            finally
            {
                foreach (FileStream stream in sealedLocks)
                {
                    stream.Dispose();
                }
            }

            FrozenRealCorpusSelection sealedSelection = FrozenRealCorpusInventory.LoadWithExpectation(
                root,
                FrozenRealCorpusInventory.RealSealed,
                expectation,
                CancellationToken.None);
            Require(sealedSelection.SelectedProjects.Count == 4 &&
                sealedSelection.SelectedProjects.Select(static item => item.AnonymizedStudyId)
                    .Distinct(StringComparer.Ordinal).Count() == 2,
                "whole_studies_are_assigned_to_one_split");
            checks.Add("whole_studies_are_assigned_to_one_split");

            string renamed = Path.Combine(Path.GetDirectoryName(paths[0])!, "changed-case.dig");
            File.Move(paths[0], renamed);
            ExpectFailure(
                () => FrozenRealCorpusInventory.LoadWithExpectation(
                    root, FrozenRealCorpusInventory.RealDev, expectation, CancellationToken.None),
                "FROZEN_REAL_CORPUS_ASSIGNMENT_MISMATCH");
            checks.Add("same_count_assignment_drift_rejected");
            File.Move(renamed, paths[0]);

            File.Delete(paths[^1]);
            ExpectFailure(
                () => FrozenRealCorpusInventory.LoadWithExpectation(
                    root, FrozenRealCorpusInventory.RealDev, expectation, CancellationToken.None),
                "FROZEN_REAL_CORPUS_PROJECT_COUNT_MISMATCH");
            checks.Add("partial_inventory_rejected");
            File.WriteAllBytes(paths[^1], Encoding.ASCII.GetBytes("opaque-project-011"));

            string[] duplicate = paths.ToArray();
            duplicate[^1] = duplicate[0];
            ExpectFailure(
                () => FrozenRealCorpusInventory.BuildForTest(
                    root, duplicate, FrozenRealCorpusInventory.RealDev, expectation,
                    File.GetAttributes, CancellationToken.None),
                "FROZEN_REAL_CORPUS_DUPLICATE_PATH");
            checks.Add("duplicate_inventory_rejected");

            string outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.dig");
            File.WriteAllBytes(outside, [1]);
            try
            {
                string[] escaped = paths.ToArray();
                escaped[^1] = outside;
                ExpectFailure(
                    () => FrozenRealCorpusInventory.BuildForTest(
                        root, escaped, FrozenRealCorpusInventory.RealDev, expectation,
                        File.GetAttributes, CancellationToken.None),
                    "FROZEN_REAL_CORPUS_PATH_ESCAPE");
                checks.Add("path_escape_rejected");
            }
            finally
            {
                File.Delete(outside);
            }

            ExpectFailure(
                () => FrozenRealCorpusInventory.BuildForTest(
                    root,
                    paths,
                    FrozenRealCorpusInventory.RealDev,
                    expectation,
                    path => string.Equals(path, paths[3], StringComparison.OrdinalIgnoreCase)
                        ? FileAttributes.ReparsePoint
                        : File.GetAttributes(path),
                    CancellationToken.None),
                "FROZEN_REAL_CORPUS_REPARSE_POINT_REJECTED");
            checks.Add("reparse_target_rejected");

            ExpectFailure(
                () => FrozenRealCorpusInventory.LoadWithExpectation(
                    root, "dev", expectation, CancellationToken.None),
                "FROZEN_REAL_CORPUS_SPLIT_UNSUPPORTED");
            checks.Add("arbitrary_split_rejected");

            Require(FrozenRealCorpusInventory.ExpectedProjectCount == 171 &&
                FrozenRealCorpusInventory.ExpectedRealDevCount == 120 &&
                FrozenRealCorpusInventory.ExpectedRealSealedCount == 51 &&
                FrozenRealCorpusInventory.FrozenAssignmentSha256 ==
                    "decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a",
                "production_assignment_constants_are_frozen");
            checks.Add("production_assignment_constants_are_frozen");

            return new
            {
                status = "pass",
                checks,
                check_count = checks.Count,
                private_payload_reads = 0,
                sealed_payload_reads = 0,
                model_runs = 0,
                authorization_created = false,
            };
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string[] CreateFixture(string root)
    {
        string[] studies = ["study-alpha", "study-beta", "study-gamma", "study-delta", "study-epsilon", "study-zeta"];
        var paths = new List<string>();
        int ordinal = 0;
        foreach (string study in studies)
        {
            string directory = Path.Combine(root, study);
            Directory.CreateDirectory(directory);
            for (int index = 0; index < 2; index++)
            {
                string path = Path.Combine(directory, $"case-{ordinal:D3}.dig");
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes($"opaque-project-{ordinal:D3}"));
                paths.Add(path);
                ordinal++;
            }
        }
        return paths.ToArray();
    }

    private static FrozenRealCorpusExpectation ReferenceExpectation(
        string root,
        string[] paths,
        int sealedTarget)
    {
        Dictionary<string, string> assignments = ReferenceAssignments(root, paths, sealedTarget);
        string[] ordered = paths.Select(Path.GetFullPath)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        string material = string.Join("\n", ordered.Select(path =>
            $"{Hash(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))}={assignments[path]}"));
        int sealedCount = assignments.Values.Count(static split => split == FrozenRealCorpusInventory.RealSealed);
        return new FrozenRealCorpusExpectation(
            paths.Length,
            paths.Length - sealedCount,
            sealedCount,
            Hash(material));
    }

    private static Dictionary<string, string> ReferenceAssignments(
        string root,
        string[] paths,
        int sealedTarget)
    {
        string[] ordered = paths.Select(Path.GetFullPath)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        IGrouping<string, string>[] studies = ordered
            .GroupBy(path => Hash(Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]), StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToArray();
        var sealedPaths = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        foreach (IGrouping<string, string> study in studies)
        {
            if (count < sealedTarget && (count + study.Count() <= sealedTarget || sealedPaths.Count == 0))
            {
                foreach (string path in study)
                {
                    sealedPaths.Add(path);
                }
                count += study.Count();
            }
        }
        return ordered.ToDictionary(
            static path => path,
            path => sealedPaths.Contains(path)
                ? FrozenRealCorpusInventory.RealSealed
                : FrozenRealCorpusInventory.RealDev,
            StringComparer.Ordinal);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ExpectFailure(Action action, string code)
    {
        try
        {
            action();
        }
        catch (InvalidDataException exception) when (exception.Message == code)
        {
            return;
        }
        throw new InvalidOperationException($"{code}_NOT_REJECTED");
    }

    private static void Require(bool condition, string check)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FROZEN_REAL_CORPUS_INVENTORY_SELF_TEST_FAILED:{check}");
        }
    }
}
