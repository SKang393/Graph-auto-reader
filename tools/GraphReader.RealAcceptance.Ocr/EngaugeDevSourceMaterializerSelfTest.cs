// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GraphReader.RealAcceptance.Ocr;

internal static class EngaugeDevSourceMaterializerSelfTest
{
    internal static object Run()
    {
        string root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "synthetic-materializer-self-test"));
        string trainStudy = FindStudy(EngaugeDevStudyPartition.Train);
        string devStudy = FindStudy(EngaugeDevStudyPartition.Development);
        FrozenRealCorpusProject[] projects = [
            Project(root, 1, trainStudy), Project(root, 2, trainStudy), Project(root, 3, devStudy)
        ];
        FrozenRealCorpusSelection selection = Selection(projects);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.CreateForTest(_ => selection, CancellationToken.None);
        int reads = 0;
        EngaugeDevSourceSample Reader(FrozenRealCorpusProject project, EngaugeDevStudyPartition partition, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads++;
            return Sample(project, partition, (byte)reads);
        }

        EngaugeDevSourceSnapshot result = EngaugeDevSourceMaterializer.ReadForTest(selector, Reader, CancellationToken.None);
        Require(reads == 3 && result.Samples.Count == 3 &&
            result.Samples.Take(2).All(sample => sample.Partition == EngaugeDevStudyPartition.Train) &&
            result.Samples[2].Partition == EngaugeDevStudyPartition.Development &&
            !result.LabelsQualityApproved && !result.SealedSourceOverlapVerified &&
            !result.CandidateAuthorized && !result.RuntimeMasksAvailable &&
            !result.DuplicateImageGroupingRequired, "study_partition_and_limits");
        Require(EngaugeDevSourceMaterializer.PartitionForStudy(trainStudy) == EngaugeDevStudyPartition.Train &&
            EngaugeDevSourceMaterializer.PartitionForStudy(devStudy) == EngaugeDevStudyPartition.Development,
            "fixed_deterministic_partition");
        byte[] modified = result.Samples[0].CopyImageBytes();
        modified[0] ^= 1;
        Require(result.Samples[0].CopyImageBytes()[0] != modified[0] &&
            result.Samples[0].PositiveCenters.Count == 1, "snapshot_clone_and_positive_only");

        ExpectFailure(() => EngaugeDevSourceMaterializer.ReadForTest(selector,
            (project, partition, _) => Sample(project, partition, 42), CancellationToken.None),
            "ENGAUGE_DEV_SOURCE_IMAGE_SPLIT_OVERLAP");
        EngaugeDevSourceSnapshot sameSplitDuplicate = EngaugeDevSourceMaterializer.ReadForTest(selector,
            (project, partition, _) => Sample(project, partition,
                partition == EngaugeDevStudyPartition.Train ? (byte)11 : (byte)12), CancellationToken.None);
        Require(sameSplitDuplicate.TrainDuplicateImageCount == 1 &&
            sameSplitDuplicate.DevelopmentDuplicateImageCount == 0 &&
            sameSplitDuplicate.DuplicateImageGroupingRequired && !sameSplitDuplicate.CandidateAuthorized,
            "same_split_duplicates_require_grouping");
        ExpectFailure(() => EngaugeDevSourceMaterializer.ReadForTest(selector,
            (_, _, _) => throw new IOException("private source path and label"), CancellationToken.None),
            "ENGAUGE_DEV_SOURCE_READ_FAILED");
        ExpectFailure(() => selector.RequireProject(Path.Combine(root, "sealed", "source.dig"), new string('f', 64),
            new string('e', 64), CancellationToken.None), "FROZEN_DEV_TRAINING_SOURCE_NOT_SELECTED");

        int loads = 0;
        int driftReads = 0;
        FrozenDevTrainingSelector drift = FrozenDevTrainingSelector.CreateForTest(_ =>
        {
            loads++;
            return loads < 4 ? selection : Selection(projects, new string('4', 64));
        }, CancellationToken.None);
        ExpectFailure(() => EngaugeDevSourceMaterializer.ReadForTest(drift, (project, partition, _) =>
        {
            driftReads++;
            return Sample(project, partition, 1);
        }, CancellationToken.None), "FROZEN_DEV_TRAINING_INVENTORY_CHANGED");
        Require(driftReads == 1, "post_read_inventory_drift_rejected");

        using var cancellation = new CancellationTokenSource();
        int cancelledReads = 0;
        try
        {
            _ = EngaugeDevSourceMaterializer.ReadForTest(selector, (project, partition, _) =>
            {
                cancelledReads++;
                cancellation.Cancel();
                return Sample(project, partition, 4);
            }, cancellation.Token);
            throw new InvalidOperationException("ENGAUGE_DEV_SOURCE_CANCELLATION_NOT_OBSERVED");
        }
        catch (OperationCanceledException) { }
        Require(cancelledReads == 1, "cancellation_stops_before_more_reads");

        // Open a source file already in this project; no fixture payloads or
        // user data are read. The handle identifies the selected exact path.
        string sourcePath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,
            "tools", "GraphReader.RealAcceptance.Ocr", "EngaugeDevSourceMaterializerSelfTest.cs"));
        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(source, sourcePath);
            ExpectFailure(() => EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(source,
                sourcePath + ".different"), "ENGAUGE_DEV_SOURCE_HANDLE_PATH_MISMATCH");
        }
        VerifyLockedSyntheticSource(trainStudy, devStudy);

        return new
        {
            status = "pass", scope = "synthetic-in-memory-only", private_payload_reads = 0,
            sealed_payload_reads = 0, model_runs = 0, optimizer_steps = 0,
            runtime_masks_available = false, training_authorized = false,
        };
    }

    private static FrozenRealCorpusProject Project(string root, int number, string study) =>
        new(Path.Combine(root, "case" + number.ToString(CultureInfo.InvariantCulture) + ".dig"), number.ToString("x64", CultureInfo.InvariantCulture), study, FrozenRealCorpusInventory.RealDev);

    private static void VerifyLockedSyntheticSource(string trainStudy, string devStudy)
    {
        const string firstPng = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAYCAAAAAC+OKDoAAAAG0lEQVR4nGP8z4AfMBGQZxhVAAGj4QABwyMcAGFuAS8iYygSAAAAAElFTkSuQmCC";
        const string secondPng = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAYCAAAAAC+OKDoAAAAIUlEQVR4nGP8z4AfMDEMFgWMhBT8p70b8IBRBRAwPMIBAK2tAi+GNCCVAAAAAElFTkSuQmCC";
        const string axes = "<Axes>" +
            "<Point><PositionScreen X=\"2\" Y=\"2\"/><PositionGraph X=\"1\" Y=\"100\"/></Point>" +
            "<Point><PositionScreen X=\"2\" Y=\"20\"/><PositionGraph X=\"1\" Y=\"0\"/></Point>" +
            "<Point><PositionScreen X=\"25\" Y=\"20\"/><PositionGraph X=\"8\" Y=\"0\"/></Point></Axes>";
        string projectRoot = Path.GetFullPath(Environment.CurrentDirectory);
        string testRoot = Path.Combine(projectRoot, "artifacts", "test-temp");
        if (!File.Exists(Path.Combine(projectRoot, "CODEX_START_HERE.md")) || !Directory.Exists(testRoot))
        {
            throw new InvalidOperationException("ENGAUGE_DEV_SOURCE_PROJECT_TEST_ROOT_REQUIRED");
        }
        string directory = Path.GetFullPath(Path.Combine(testRoot,
            "engauge-materializer-" + Guid.NewGuid().ToString("N")));
        string first = Path.Combine(directory, "synthetic-a.dig");
        string second = Path.Combine(directory, "synthetic-b.dig");
        Directory.CreateDirectory(directory);
        try
        {
            static string Document(string png) => "<Document><SourceImage Format=\"PNG\">" + png +
                "</SourceImage>" + axes + "<Curve Name=\"synthetic\"><Point>" +
                "<PositionScreen X=\"6\" Y=\"10\"/></Point></Curve></Document>";
            File.WriteAllText(first, Document(firstPng), new UTF8Encoding(false));
            File.WriteAllText(second, Document(secondPng), new UTF8Encoding(false));
            FrozenRealCorpusProject[] projects = [
                new(first, new string('a', 64), trainStudy, FrozenRealCorpusInventory.RealDev),
                new(second, new string('b', 64), devStudy, FrozenRealCorpusInventory.RealDev),
            ];
            FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.CreateForTest(
                _ => Selection(projects), CancellationToken.None);
            using (var writer = new FileStream(first, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                ExpectFailure(() => EngaugeDevSourceMaterializer.Read(selector, CancellationToken.None),
                    "ENGAUGE_DEV_SOURCE_READ_FAILED");
            }
            EngaugeDevSourceSnapshot snapshot = EngaugeDevSourceMaterializer.Read(selector, CancellationToken.None);
            Require(snapshot.Samples.Count == 2 && !snapshot.DuplicateImageGroupingRequired &&
                snapshot.Samples[0].ProjectSha256 == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(first))) &&
                snapshot.Samples[1].ProjectSha256 == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(second))) &&
                snapshot.Samples[0].ImageSha256 == Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(firstPng))) &&
                snapshot.Samples[0].Width == 32 && snapshot.Samples[0].Height == 24 &&
                snapshot.Samples[0].PositiveCenters.SequenceEqual([new EngaugePositiveCenter(6, 10)]),
                "locked_source_hashes_and_positive_centers");
            // Materialization leaves caller-owned test files available for an
            // exclusive open, proving the source handles were released.
            using var exclusive = new FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            if (File.Exists(first)) { File.Delete(first); }
            if (File.Exists(second)) { File.Delete(second); }
            Directory.Delete(directory, recursive: false);
        }
    }

    private static EngaugeDevSourceSample Sample(FrozenRealCorpusProject project, EngaugeDevStudyPartition partition, byte value)
    {
        byte[] image = [value, 7, 9];
        return new EngaugeDevSourceSample(project.AnonymizedCaseId, project.AnonymizedStudyId, partition,
            new string('1', 64), Convert.ToHexStringLower(SHA256.HashData(image)), 10, 10,
            image, [new EngaugePositiveCenter(3.0, 4.0)]);
    }

    private static FrozenRealCorpusSelection Selection(FrozenRealCorpusProject[] projects, string? assignment = null) =>
        new(FrozenRealCorpusInventory.RealDev, projects.Length + 1, projects.Length, 1,
            assignment ?? new string('1', 64), new string('2', 64), projects);

    private static string FindStudy(EngaugeDevStudyPartition partition)
    {
        for (var value = 1; value < 10000; value++)
        {
            string id = value.ToString("x64", CultureInfo.InvariantCulture);
            if (EngaugeDevSourceMaterializer.PartitionForStudy(id) == partition) { return id; }
        }
        throw new InvalidOperationException("SYNTHETIC_STUDY_NOT_FOUND");
    }

    private static void ExpectFailure(Action action, string code)
    {
        try { action(); }
        catch (InvalidDataException exception) when (exception.Message == code) { return; }
        throw new InvalidOperationException($"{code}_NOT_REJECTED");
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) { throw new InvalidOperationException($"ENGAUGE_DEV_SOURCE_SELF_TEST_FAILED:{check}"); }
    }
}
