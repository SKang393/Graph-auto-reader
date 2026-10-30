// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.RealAcceptance.Ocr;

/// <summary>Aggregate-only description; never serializes source records.</summary>
internal static class EngaugeDevSourceAudit
{
    internal static object Describe(EngaugeDevSourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new
        {
            status = "pass",
            assignment_sha256 = snapshot.AssignmentSha256,
            selected_inventory_sha256 = snapshot.SelectedInventorySha256,
            partition_rule = EngaugeDevSourceMaterializer.PartitionRule,
            train_projects = snapshot.Samples.Count(static x => x.Partition == EngaugeDevStudyPartition.Train),
            development_projects = snapshot.Samples.Count(static x => x.Partition == EngaugeDevStudyPartition.Development),
            train_points = snapshot.Samples.Where(static x => x.Partition == EngaugeDevStudyPartition.Train)
                .Sum(static x => (long)x.PositiveCenters.Count),
            development_points = snapshot.Samples.Where(static x => x.Partition == EngaugeDevStudyPartition.Development)
                .Sum(static x => (long)x.PositiveCenters.Count),
            train_duplicate_images = snapshot.TrainDuplicateImageCount,
            development_duplicate_images = snapshot.DevelopmentDuplicateImageCount,
            duplicate_image_grouping_required = snapshot.DuplicateImageGroupingRequired,
            labels_quality_approved = snapshot.LabelsQualityApproved,
            sealed_source_overlap_verified = snapshot.SealedSourceOverlapVerified,
            runtime_masks_available = snapshot.RuntimeMasksAvailable,
            candidate_authorized = snapshot.CandidateAuthorized,
            model_inference = false,
            optimizer_steps = 0,
        };
    }
}
