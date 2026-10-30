// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;

namespace GraphReader.RealAcceptance.Ocr;

internal static class EngaugeDevProposalPreparationSelfTest
{
    internal static async Task<object> RunAsync()
    {
        byte[] trainImage = Enumerable.Repeat((byte)11, 32 * 32).ToArray();
        byte[] developmentImage = Enumerable.Repeat((byte)22, 32 * 32).ToArray();
        var metadata = new FrozenDevTrainingMetadata(new string('a', 64), new string('b', 64), [], false, false);
        var source = new EngaugeDevSourceSnapshot(metadata,
            [Sample('1', '2', EngaugeDevStudyPartition.Train, trainImage),
             Sample('3', '4', EngaugeDevStudyPartition.Development, developmentImage)], 0, 0);
        var identity = new EngaugeDevProposalPreparationIdentity(
            new string('1', 64), new string('2', 64), new string('3', 64), new string('4', 64),
            new string('5', 64), new string('6', 64));
        int prepared = 0, consumed = 0;
        EngaugeDevProposalPreparationResult result = await EngaugeDevProposalPreparation.RunForTestAsync(
            identity, source, (group, token) =>
            {
                prepared++;
                Require(group.Partition == EngaugeDevStudyPartition.Train, "development_group_was_prepared");
                int length = checked(group.Width * group.Height);
                var artifactSeed = new float[length];
                var residualArtifact = new float[length];
                return Task.FromResult(new EngaugeDevRuntimePlanes(group.ImageSha256, group.Width, group.Height,
                    Enumerable.Repeat(0.5f, length).ToArray(), new float[length], new float[length],
                    group.PositiveCenters, artifactSeed, residualArtifact));
            }, (batch, token) =>
            {
                consumed++;
                Require(batch.ImageSha256.Length == 64 && batch.Coordinates.Count > 0 && batch.Tensor.Length > 0,
                    "positive_batch_identity_or_tensor_missing");
                return ValueTask.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        Require(prepared == 1 && consumed == 1 && result.SelectedSourceCount == 2 && result.TrainGroupCount == 1 &&
            result.DevelopmentGroupCount == 1 && result.ProcessedTrainGroupCount == 1 && result.PositiveProposalCount >= 1 &&
            result.AggregateOnly && !result.SourceImagesOutput && !result.PerCaseOutput && !result.InferenceCacheOutput &&
            !result.ProductionApproved && result.OptimizerSteps == 0 && result.SealedReads == 0,
            "aggregate_train_only_result");

        try
        {
            _ = await EngaugeDevProposalPreparation.RunForTestAsync(identity, source,
                (_, _) => throw new InvalidOperationException("private detail"), (_, _) => ValueTask.CompletedTask,
                CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("group_failure_not_rejected");
        }
        catch (InvalidDataException error) when (error.Message == "ENGAUGE_PROPOSAL_PREPARATION_GROUP_FAILED") { }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            _ = await EngaugeDevProposalPreparation.RunForTestAsync(identity, source,
                (_, _) => throw new InvalidOperationException(), (_, _) => ValueTask.CompletedTask, canceled.Token)
                .ConfigureAwait(false);
            throw new InvalidOperationException("cancellation_not_observed");
        }
        catch (OperationCanceledException) { }
        return new { status = "pass", scope = "synthetic-in-memory-only", private_payload_reads = 0,
            sealed_payload_reads = 0, model_runs = 0, optimizer_steps = 0, source_images_output = false,
            per_case_output = false, inference_cache_output = false };
    }

    private static EngaugeDevSourceSample Sample(char caseDigit, char studyDigit,
        EngaugeDevStudyPartition partition, byte[] image) => new(
            new string(caseDigit, 64), new string(studyDigit, 64), partition, new string('c', 64),
            Convert.ToHexStringLower(SHA256.HashData(image)), 32, 32, image, [new EngaugePositiveCenter(16, 16)]);

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException("ENGAUGE_PROPOSAL_PREPARATION_SELF_TEST_" + check);
    }
}
