// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;

namespace GraphReader.RealAcceptance.Ocr;

// Contributor identifiers are stable local pseudonyms, not anonymous data.
// These objects are private in-memory inputs and are not suitable for logging.
internal sealed class EngaugeDevImageContributor
{
    internal EngaugeDevImageContributor(string caseId, string studyId, string projectSha256,
        IReadOnlyList<EngaugePositiveCenter> positiveCenters)
    {
        CaseId = caseId;
        StudyId = studyId;
        ProjectSha256 = projectSha256;
        PositiveCenters = Array.AsReadOnly(positiveCenters.Distinct()
            .OrderBy(static point => point.X).ThenBy(static point => point.Y).ToArray());
    }
    internal string CaseId { get; }
    internal string StudyId { get; }
    internal string ProjectSha256 { get; }
    internal IReadOnlyList<EngaugePositiveCenter> PositiveCenters { get; }
}

internal sealed class EngaugeDevImageGroup
{
    private readonly byte[] image;

    internal EngaugeDevImageGroup(EngaugeDevStudyPartition partition, string imageSha256, int width, int height,
        byte[] image, IReadOnlyList<EngaugePositiveCenter> positiveCenters,
        IReadOnlyList<EngaugeDevImageContributor> contributors)
    {
        Partition = partition;
        ImageSha256 = imageSha256;
        Width = width;
        Height = height;
        this.image = (byte[])image.Clone();
        PositiveCenters = Array.AsReadOnly(positiveCenters.ToArray());
        Contributors = Array.AsReadOnly(contributors.ToArray());
    }
    internal EngaugeDevStudyPartition Partition { get; }
    internal string ImageSha256 { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal IReadOnlyList<EngaugePositiveCenter> PositiveCenters { get; }
    internal IReadOnlyList<EngaugeDevImageContributor> Contributors { get; }
    internal byte[] CopyImageBytes() => (byte[])image.Clone();
}

internal sealed class EngaugeDevGroupedSnapshot
{
    internal EngaugeDevGroupedSnapshot(EngaugeDevSourceSnapshot source, IReadOnlyList<EngaugeDevImageGroup> groups)
    {
        AssignmentSha256 = source.AssignmentSha256;
        SelectedInventorySha256 = source.SelectedInventorySha256;
        Groups = Array.AsReadOnly(groups.ToArray());
        LabelsQualityApproved = false;
        SealedSourceOverlapVerified = false;
        CandidateAuthorized = false;
        RuntimeMasksAvailable = false;
        NegativeLabelsAvailable = false;
        ApproximatePointMatchingApplied = false;
    }
    internal string AssignmentSha256 { get; }
    internal string SelectedInventorySha256 { get; }
    internal IReadOnlyList<EngaugeDevImageGroup> Groups { get; }
    internal bool LabelsQualityApproved { get; }
    internal bool SealedSourceOverlapVerified { get; }
    internal bool CandidateAuthorized { get; }
    internal bool RuntimeMasksAvailable { get; }
    internal bool NegativeLabelsAvailable { get; }
    internal bool ApproximatePointMatchingApplied { get; }
}

internal static class EngaugeDevExactImageGrouping
{
    internal static EngaugeDevGroupedSnapshot Group(EngaugeDevSourceSnapshot snapshot, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var groups = new Dictionary<string, Builder>(StringComparer.Ordinal);
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (EngaugeDevSourceSample sample in snapshot.Samples)
        {
            token.ThrowIfCancellationRequested();
            if (!caseIds.Add(sample.CaseId)) { throw new InvalidDataException("ENGAUGE_GROUP_DUPLICATE_CASE"); }
            if (sample.Partition is not (EngaugeDevStudyPartition.Train or EngaugeDevStudyPartition.Development) ||
                sample.Width <= 0 || sample.Height <= 0 || sample.PositiveCenters.Count == 0)
            {
                throw new InvalidDataException("ENGAUGE_GROUP_SOURCE_INVALID");
            }
            byte[] image = sample.CopyImageBytes();
            if (image.Length == 0 || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(image)),
                    sample.ImageSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("ENGAUGE_GROUP_IMAGE_HASH_MISMATCH");
            }
            if (!groups.TryGetValue(sample.ImageSha256, out Builder? group))
            {
                group = new Builder(sample, image);
                groups.Add(sample.ImageSha256, group);
            }
            else
            {
                if (group.Partition != sample.Partition) { throw new InvalidDataException("ENGAUGE_GROUP_IMAGE_SPLIT_OVERLAP"); }
                if (group.Width != sample.Width || group.Height != sample.Height)
                {
                    throw new InvalidDataException("ENGAUGE_GROUP_DIMENSIONS_MISMATCH");
                }
                if (!group.Image.AsSpan().SequenceEqual(image))
                {
                    throw new InvalidDataException("ENGAUGE_GROUP_IMAGE_BYTES_MISMATCH");
                }
            }
            foreach (EngaugePositiveCenter center in sample.PositiveCenters)
            {
                if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) || center.X < 0 || center.Y < 0 ||
                    center.X >= sample.Width || center.Y >= sample.Height)
                {
                    throw new InvalidDataException("ENGAUGE_GROUP_CENTER_INVALID");
                }
                // Double-value equality only. No distance matching, merging of
                // near neighbors, or creation of unlabeled negative examples.
                group.Centers.Add(center);
            }
            group.Contributors.Add(new EngaugeDevImageContributor(sample.CaseId, sample.StudyId,
                sample.ProjectSha256, sample.PositiveCenters));
        }
        token.ThrowIfCancellationRequested();
        EngaugeDevImageGroup[] output = groups.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Value.Finish(pair.Key)).ToArray();
        return new EngaugeDevGroupedSnapshot(snapshot, output);
    }

    private sealed class Builder
    {
        internal Builder(EngaugeDevSourceSample sample, byte[] image)
        {
            Partition = sample.Partition;
            Width = sample.Width;
            Height = sample.Height;
            Image = image;
        }
        internal EngaugeDevStudyPartition Partition { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal byte[] Image { get; }
        internal HashSet<EngaugePositiveCenter> Centers { get; } = [];
        internal List<EngaugeDevImageContributor> Contributors { get; } = [];

        internal EngaugeDevImageGroup Finish(string imageSha256) => new(Partition, imageSha256, Width, Height, Image,
            Centers.OrderBy(static point => point.X).ThenBy(static point => point.Y).ToArray(),
            Contributors.OrderBy(static contributor => contributor.CaseId, StringComparer.Ordinal).ToArray());
    }
}
