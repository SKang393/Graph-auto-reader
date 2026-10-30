// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;

namespace GraphReader.RealAcceptance.Ocr;

// Aggregate-only outcome. Source pixels, case identities, and tensors are not
// serialized by this orchestration layer.
internal sealed record EngaugeDevProposalPreparationResult(
    string BindingSha256,
    string ProtocolSha256,
    string AssignmentSha256,
    string SelectedInventorySha256,
    string OcrDetectionSha256,
    string OcrRecognitionSha256,
    string MarkerCenterSha256,
    string MarkerClassifierSha256,
    int SelectedSourceCount,
    int TrainGroupCount,
    int DevelopmentGroupCount,
    int ProcessedTrainGroupCount,
    long PositiveProposalCount,
    long UnmatchedPositiveCenterCount,
    bool AggregateOnly,
    bool SourceImagesOutput,
    bool PerCaseOutput,
    bool InferenceCacheOutput,
    bool ProductionApproved,
    int OptimizerSteps,
    int SealedReads);

internal sealed record EngaugeDevProposalPreparationRequest(
    string RepositoryRoot,
    string CorpusRoot,
    string CandidateBindingPath,
    string CandidateBindingSha256,
    string WorkingRoot);

// A batch belongs to one exact-image group. It has no case or study identity.
// The consumer is awaited before the next group is prepared, so a controlled
// adapter can consume one bounded in-memory tensor at a time without disk I/O.
internal sealed class EngaugeDevPositiveProposalBatch
{
    internal EngaugeDevPositiveProposalBatch(EngaugeDevPositiveProposals positive)
    {
        ImageSha256 = positive.ImageSha256;
        Tensor = positive.Tensor;
        Coordinates = positive.Coordinates;
        UnmatchedCenterCount = positive.UnmatchedCenterCount;
    }
    internal string ImageSha256 { get; }
    internal ReadOnlyMemory<float> Tensor { get; }
    internal IReadOnlyList<GraphReader.Markers.Detection.MarkerPoint> Coordinates { get; }
    internal int UnmatchedCenterCount { get; }
}

/// <summary>
/// Prepares frozen real-dev train groups for a separately controlled positive
/// sample consumer. It stays aggregate-only, invokes no marker-center or
/// classifier inference, and fails closed rather than returning partial input.
/// </summary>
internal static class EngaugeDevProposalPreparation
{
    internal static async Task<EngaugeDevProposalPreparationResult> RunAsync(
        EngaugeDevProposalPreparationRequest request,
        Func<EngaugeDevPositiveProposalBatch, CancellationToken, ValueTask> consume,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(consume);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorpusRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidateBindingPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidateBindingSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingRoot);
        token.ThrowIfCancellationRequested();

        string repositoryRoot = Path.GetFullPath(request.RepositoryRoot);
        string workingRoot = Path.GetFullPath(request.WorkingRoot);
        string relativeWorkingRoot = Path.GetRelativePath(repositoryRoot, workingRoot);
        if (relativeWorkingRoot == ".." || relativeWorkingRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relativeWorkingRoot))
            throw new InvalidDataException("ENGAUGE_PROPOSAL_PREPARATION_WORK_ROOT_OUTSIDE_PROJECT");
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(request.CorpusRoot, token);
        FrozenCandidateBinding binding = FrozenCandidateBinding.Load(
            repositoryRoot, request.CandidateBindingPath, request.CandidateBindingSha256, token);
        string runRoot = Path.Combine(workingRoot,
            "engauge-dev-proposal-preparation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);

        // aggregateOnly installs NoPersistenceStageCache. This method invokes
        // only the pre-marker preparation seam, never marker or classifier.
        FrozenCandidateWorkflowRuntime runtime;
        try
        {
            runtime = await FrozenCandidateWorkflowFactory.CreateAsync(
                repositoryRoot, runRoot, binding, token, aggregateOnly: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new InvalidDataException("ENGAUGE_PROPOSAL_PREPARATION_RUNTIME_FAILED", exception);
        }
        await using (runtime)
        {
            EngaugeDevSourceSnapshot source;
            try { source = EngaugeDevSourceMaterializer.Read(selector, token); }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            { throw new InvalidDataException("ENGAUGE_PROPOSAL_PREPARATION_SOURCE_FAILED", exception); }
            return await RunCoreAsync(Identity(binding), source,
                (group, cancellation) => EngaugeDevRuntimePlaneBridge.PrepareAsync(group, runtime, cancellation),
                consume, token).ConfigureAwait(false);
        }
    }

    // Synthetic seam only. It accepts in-memory source data and no model path.
    internal static Task<EngaugeDevProposalPreparationResult> RunForTestAsync(
        EngaugeDevProposalPreparationIdentity identity,
        EngaugeDevSourceSnapshot source,
        Func<EngaugeDevImageGroup, CancellationToken, Task<EngaugeDevRuntimePlanes>> prepare,
        Func<EngaugeDevPositiveProposalBatch, CancellationToken, ValueTask> consume,
        CancellationToken token) => RunCoreAsync(identity, source, prepare, consume, token);

    private static async Task<EngaugeDevProposalPreparationResult> RunCoreAsync(
        EngaugeDevProposalPreparationIdentity identity,
        EngaugeDevSourceSnapshot source,
        Func<EngaugeDevImageGroup, CancellationToken, Task<EngaugeDevRuntimePlanes>> prepare,
        Func<EngaugeDevPositiveProposalBatch, CancellationToken, ValueTask> consume,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(consume);
        token.ThrowIfCancellationRequested();
        if (source.LabelsQualityApproved || source.SealedSourceOverlapVerified || source.CandidateAuthorized ||
            source.RuntimeMasksAvailable)
            throw new InvalidDataException("ENGAUGE_PROPOSAL_PREPARATION_SOURCE_SCOPE_INVALID");

        EngaugeDevGroupedSnapshot grouped;
        try { grouped = EngaugeDevExactImageGrouping.Group(source, token); }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        { throw new InvalidDataException("ENGAUGE_PROPOSAL_PREPARATION_GROUPING_FAILED"); }
        if (grouped.LabelsQualityApproved || grouped.SealedSourceOverlapVerified || grouped.CandidateAuthorized ||
            grouped.RuntimeMasksAvailable || grouped.NegativeLabelsAvailable || grouped.ApproximatePointMatchingApplied)
            throw new InvalidDataException("ENGAUGE_PROPOSAL_PREPARATION_GROUP_SCOPE_INVALID");

        int trainGroups = 0, developmentGroups = 0, processed = 0;
        long proposals = 0, unmatched = 0;
        foreach (EngaugeDevImageGroup group in grouped.Groups)
        {
            token.ThrowIfCancellationRequested();
            if (group.Partition == EngaugeDevStudyPartition.Development) { developmentGroups++; continue; }
            if (group.Partition != EngaugeDevStudyPartition.Train)
                throw new InvalidDataException("ENGAUGE_PROPOSAL_PREPARATION_PARTITION_INVALID");
            trainGroups++;
            GroupTotals totals;
            try { totals = await ProcessTrainGroupAsync(group, prepare, consume, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // Do not disclose group-specific details or emit partial results.
                throw new InvalidDataException("ENGAUGE_PROPOSAL_PREPARATION_GROUP_FAILED", exception);
            }
            checked { processed++; proposals += totals.PositiveProposalCount; unmatched += totals.UnmatchedCenterCount; }
        }
        if (trainGroups == 0 || processed != trainGroups)
            throw new InvalidDataException("ENGAUGE_PROPOSAL_PREPARATION_TRAIN_GROUPS_INVALID");

        return new EngaugeDevProposalPreparationResult(
            identity.BindingSha256, identity.ProtocolSha256, source.AssignmentSha256, source.SelectedInventorySha256,
            identity.OcrDetectionSha256, identity.OcrRecognitionSha256, identity.MarkerCenterSha256,
            identity.MarkerClassifierSha256, source.Samples.Count, trainGroups, developmentGroups, processed,
            proposals, unmatched, true, false, false, false, false, 0, 0);
    }

    private static async Task<GroupTotals> ProcessTrainGroupAsync(
        EngaugeDevImageGroup group,
        Func<EngaugeDevImageGroup, CancellationToken, Task<EngaugeDevRuntimePlanes>> prepare,
        Func<EngaugeDevPositiveProposalBatch, CancellationToken, ValueTask> consume,
        CancellationToken token)
    {
        // Planes and tensor are live only while this group is being consumed.
        EngaugeDevRuntimePlanes planes = await prepare(group, token).ConfigureAwait(false);
        EngaugeDevPositiveProposals positive = EngaugeDevPositiveProposalSelector.Select(planes, group, token);
        await consume(new EngaugeDevPositiveProposalBatch(positive), token).ConfigureAwait(false);
        return new GroupTotals(positive.Coordinates.Count, positive.UnmatchedCenterCount);
    }

    private static EngaugeDevProposalPreparationIdentity Identity(FrozenCandidateBinding binding) => new(
        binding.Sha256, binding.Protocol.Sha256, binding.OcrDetection.Payload.Sha256,
        binding.OcrRecognition.Payload.Sha256, binding.MarkerCenter.Payload.Sha256,
        binding.MarkerClassifier.ModelSha256);

    private sealed record GroupTotals(int PositiveProposalCount, int UnmatchedCenterCount);
}

internal sealed record EngaugeDevProposalPreparationIdentity(
    string BindingSha256, string ProtocolSha256, string OcrDetectionSha256,
    string OcrRecognitionSha256, string MarkerCenterSha256, string MarkerClassifierSha256);
