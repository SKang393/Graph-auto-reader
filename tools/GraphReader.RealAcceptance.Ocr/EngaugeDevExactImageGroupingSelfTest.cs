// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;

namespace GraphReader.RealAcceptance.Ocr;

internal static class EngaugeDevExactImageGroupingSelfTest
{
    internal static object Run()
    {
        byte[] image = [7, 8, 9];
        byte[] otherImage = [4, 5, 6];
        var a = Sample('a', 'c', '1', EngaugeDevStudyPartition.Train, image, 20, 20,
            [new(2, 3), new(4, 5)]);
        var b = Sample('b', 'd', '2', EngaugeDevStudyPartition.Train, image, 20, 20,
            [new(2, 3), new(2.000000001, 3)]);
        var c = Sample('e', 'f', '3', EngaugeDevStudyPartition.Development, otherImage, 20, 20, [new(6, 7)]);
        image[0] = 0; // sample constructors must have captured private copies.

        EngaugeDevGroupedSnapshot grouped = EngaugeDevExactImageGrouping.Group(Snapshot([a, b, c]), CancellationToken.None);
        Require(grouped.Groups.Count == 2 && !grouped.LabelsQualityApproved && !grouped.SealedSourceOverlapVerified &&
            !grouped.CandidateAuthorized && !grouped.RuntimeMasksAvailable && !grouped.NegativeLabelsAvailable &&
            !grouped.ApproximatePointMatchingApplied, "group_count_and_limits");
        EngaugeDevImageGroup shared = grouped.Groups.Single(group => group.Partition == EngaugeDevStudyPartition.Train);
        Require(shared.PositiveCenters.Count == 3 && shared.PositiveCenters.Contains(new EngaugePositiveCenter(2, 3)) &&
            shared.PositiveCenters.Contains(new EngaugePositiveCenter(2.000000001, 3)) &&
            shared.PositiveCenters.Contains(new EngaugePositiveCenter(4, 5)), "exact_centers_only");
        Require(shared.Contributors.Count == 2 && shared.Contributors[0].CaseId == a.CaseId &&
            shared.Contributors[0].StudyId == a.StudyId && shared.Contributors[0].ProjectSha256 == a.ProjectSha256 &&
            shared.Contributors[1].CaseId == b.CaseId && shared.Contributors[1].StudyId == b.StudyId &&
            shared.Contributors[1].ProjectSha256 == b.ProjectSha256 &&
            shared.Contributors[0].PositiveCenters.Count == 2 && shared.Contributors[1].PositiveCenters.Count == 2 &&
            shared.Contributors[1].PositiveCenters.Contains(new EngaugePositiveCenter(2.000000001, 3)),
            "contributor_identities_and_center_provenance_retained");
        byte[] altered = shared.CopyImageBytes();
        altered[0] ^= 1;
        Require(shared.CopyImageBytes()[0] != altered[0] && a.CopyImageBytes()[0] == 7 &&
            a.PositiveCenters.Count == 2 && b.PositiveCenters.Count == 2, "source_and_output_immutable");

        var wrongDimensions = Sample('9', '8', '7', EngaugeDevStudyPartition.Train,
            [7, 8, 9], 21, 20, [new(2, 3)]);
        ExpectFailure(() => EngaugeDevExactImageGrouping.Group(Snapshot([a, wrongDimensions]), CancellationToken.None),
            "ENGAUGE_GROUP_DIMENSIONS_MISMATCH");
        var otherSplit = Sample('9', '8', '7', EngaugeDevStudyPartition.Development,
            [7, 8, 9], 20, 20, [new(2, 3)]);
        ExpectFailure(() => EngaugeDevExactImageGrouping.Group(Snapshot([a, otherSplit]), CancellationToken.None),
            "ENGAUGE_GROUP_IMAGE_SPLIT_OVERLAP");
        ExpectFailure(() => EngaugeDevExactImageGrouping.Group(Snapshot([a, a]), CancellationToken.None),
            "ENGAUGE_GROUP_DUPLICATE_CASE");
        var forgedHash = new EngaugeDevSourceSample(new string('9', 64), new string('8', 64),
            EngaugeDevStudyPartition.Train, new string('7', 64), new string('6', 64), 20, 20,
            [7, 8, 9], [new(2, 3)]);
        ExpectFailure(() => EngaugeDevExactImageGrouping.Group(Snapshot([forgedHash]), CancellationToken.None),
            "ENGAUGE_GROUP_IMAGE_HASH_MISMATCH");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = EngaugeDevExactImageGrouping.Group(Snapshot([a, b]), cancellation.Token);
            throw new InvalidOperationException("ENGAUGE_GROUP_CANCELLATION_NOT_OBSERVED");
        }
        catch (OperationCanceledException) { }
        return new
        {
            status = "pass", scope = "synthetic-in-memory-only", private_payload_reads = 0,
            sealed_payload_reads = 0, model_runs = 0, optimizer_steps = 0,
            runtime_masks_available = false, training_authorized = false,
        };
    }

    private static EngaugeDevSourceSample Sample(char caseDigit, char studyDigit, char projectDigit,
        EngaugeDevStudyPartition partition, byte[] image, int width, int height,
        EngaugePositiveCenter[] centers) =>
        new(new string(caseDigit, 64), new string(studyDigit, 64), partition, new string(projectDigit, 64),
            Convert.ToHexStringLower(SHA256.HashData(image)), width, height, image, centers);

    private static EngaugeDevSourceSnapshot Snapshot(EngaugeDevSourceSample[] samples) =>
        new(new FrozenDevTrainingMetadata(new string('1', 64), new string('2', 64), [], false, false), samples, 0, 0);

    private static void ExpectFailure(Action action, string code)
    {
        try { action(); }
        catch (InvalidDataException exception) when (exception.Message == code) { return; }
        throw new InvalidOperationException($"{code}_NOT_REJECTED");
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) { throw new InvalidOperationException($"ENGAUGE_GROUP_SELF_TEST_FAILED:{check}"); }
    }
}
