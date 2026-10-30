// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GraphReader.RealAcceptance.Ocr;

internal enum EngaugeDevStudyPartition { Train, Development }

internal readonly record struct EngaugePositiveCenter(double X, double Y);

// This is private in-memory source data, not an independently approved label
// set, a training tensor, a runtime mask, or an artifact suitable for logging.
internal sealed class EngaugeDevSourceSample
{
    private readonly byte[] image;

    internal EngaugeDevSourceSample(
        string caseId, string studyId, EngaugeDevStudyPartition partition,
        string projectSha256, string imageSha256, int width, int height,
        byte[] image, IReadOnlyList<EngaugePositiveCenter> positiveCenters)
    {
        CaseId = caseId;
        StudyId = studyId;
        Partition = partition;
        ProjectSha256 = projectSha256;
        ImageSha256 = imageSha256;
        Width = width;
        Height = height;
        this.image = (byte[])image.Clone();
        PositiveCenters = Array.AsReadOnly(positiveCenters.ToArray());
    }

    internal string CaseId { get; }
    internal string StudyId { get; }
    internal EngaugeDevStudyPartition Partition { get; }
    internal string ProjectSha256 { get; }
    internal string ImageSha256 { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal IReadOnlyList<EngaugePositiveCenter> PositiveCenters { get; }
    internal byte[] CopyImageBytes() => (byte[])image.Clone();
}

internal sealed class EngaugeDevSourceSnapshot
{
    internal EngaugeDevSourceSnapshot(FrozenDevTrainingMetadata metadata, IReadOnlyList<EngaugeDevSourceSample> samples,
        int trainDuplicateImageCount, int developmentDuplicateImageCount)
    {
        AssignmentSha256 = metadata.AssignmentSha256;
        SelectedInventorySha256 = metadata.SelectedInventorySha256;
        Samples = Array.AsReadOnly(samples.ToArray());
        TrainDuplicateImageCount = trainDuplicateImageCount;
        DevelopmentDuplicateImageCount = developmentDuplicateImageCount;
        LabelsQualityApproved = false;
        SealedSourceOverlapVerified = false;
        CandidateAuthorized = false;
        RuntimeMasksAvailable = false;
    }

    internal string AssignmentSha256 { get; }
    internal string SelectedInventorySha256 { get; }
    internal IReadOnlyList<EngaugeDevSourceSample> Samples { get; }
    // Counts are additional cases beyond the first instance of each exact
    // image hash in a split. A future trainer must group these cases before
    // assigning loss weights; this materializer does not make that choice.
    internal int TrainDuplicateImageCount { get; }
    internal int DevelopmentDuplicateImageCount { get; }
    internal bool DuplicateImageGroupingRequired => TrainDuplicateImageCount + DevelopmentDuplicateImageCount != 0;
    internal bool LabelsQualityApproved { get; }
    internal bool SealedSourceOverlapVerified { get; }
    internal bool CandidateAuthorized { get; }
    internal bool RuntimeMasksAvailable { get; }
}

internal static class EngaugeDevSourceMaterializer
{
    // This fixed rule splits by study before reading any source. It is an
    // exploratory preparation rule, not authorization for an experiment.
    internal const string PartitionRule = "sha256:graphreader-engauge-real-dev-study-v1:first-byte-lt-204";
    private const string PartitionDomain = "graphreader-engauge-real-dev-study-v1\0";

    internal static EngaugeDevSourceSnapshot Read(FrozenDevTrainingSelector selector, CancellationToken token) =>
        ReadCore(selector, (project, partition, cancellation) =>
            ReadLockedSource(selector, project, partition, cancellation), token);

    // This seam is only for synthetic tests. Production uses the locked reader.
    internal static EngaugeDevSourceSnapshot ReadForTest(
        FrozenDevTrainingSelector selector,
        Func<FrozenRealCorpusProject, EngaugeDevStudyPartition, CancellationToken, EngaugeDevSourceSample> reader,
        CancellationToken token) => ReadCore(selector, reader, token);

    internal static EngaugeDevStudyPartition PartitionForStudy(string studyId)
    {
        ValidateHash(studyId);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(PartitionDomain + studyId));
        return digest[0] < 204 ? EngaugeDevStudyPartition.Train : EngaugeDevStudyPartition.Development;
    }

    private static EngaugeDevSourceSnapshot ReadCore(
        FrozenDevTrainingSelector selector,
        Func<FrozenRealCorpusProject, EngaugeDevStudyPartition, CancellationToken, EngaugeDevSourceSample> reader,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(reader);
        IReadOnlyList<FrozenRealCorpusProject> selected = selector.EnumerateSelectedProjects(token);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        var studyPartitions = new Dictionary<string, EngaugeDevStudyPartition>(StringComparer.Ordinal);
        foreach (FrozenRealCorpusProject item in selected)
        {
            token.ThrowIfCancellationRequested();
            ValidateHash(item.AnonymizedCaseId);
            ValidateHash(item.AnonymizedStudyId);
            studyPartitions[item.AnonymizedStudyId] = PartitionForStudy(item.AnonymizedStudyId);
        }
        if (!studyPartitions.ContainsValue(EngaugeDevStudyPartition.Train) ||
            !studyPartitions.ContainsValue(EngaugeDevStudyPartition.Development))
        {
            throw new InvalidDataException("ENGAUGE_DEV_SOURCE_EMPTY_PARTITION");
        }

        var result = new List<EngaugeDevSourceSample>(selected.Count);
        var imagePartitions = new Dictionary<string, EngaugeDevStudyPartition>(StringComparer.Ordinal);
        int trainDuplicates = 0;
        int developmentDuplicates = 0;
        foreach (FrozenRealCorpusProject project in selected)
        {
            token.ThrowIfCancellationRequested();
            FrozenRealCorpusProject checkedProject = selector.RequireProject(
                project.GetProjectPath(), project.AnonymizedCaseId, project.AnonymizedStudyId, token);
            EngaugeDevStudyPartition partition = studyPartitions[checkedProject.AnonymizedStudyId];
            EngaugeDevSourceSample sample;
            try
            {
                sample = reader(checkedProject, partition, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // File and parser exceptions can contain a private path or
                // label. Do not expose exception text or inner exceptions.
                throw new InvalidDataException("ENGAUGE_DEV_SOURCE_READ_FAILED");
            }
            token.ThrowIfCancellationRequested();
            _ = selector.RequireProject(project.GetProjectPath(), project.AnonymizedCaseId, project.AnonymizedStudyId, token);
            ValidateSample(sample, checkedProject, partition);
            if (imagePartitions.TryGetValue(sample.ImageSha256, out EngaugeDevStudyPartition previous))
            {
                if (previous != partition) { throw new InvalidDataException("ENGAUGE_DEV_SOURCE_IMAGE_SPLIT_OVERLAP"); }
                if (partition == EngaugeDevStudyPartition.Train) { trainDuplicates++; }
                else { developmentDuplicates++; }
            }
            imagePartitions[sample.ImageSha256] = partition;
            result.Add(sample);
        }
        _ = selector.EnumerateSelectedProjects(token);
        return new EngaugeDevSourceSnapshot(metadata, result, trainDuplicates, developmentDuplicates);
    }

    private static EngaugeDevSourceSample ReadLockedSource(
        FrozenDevTrainingSelector selector, FrozenRealCorpusProject project, EngaugeDevStudyPartition partition, CancellationToken token)
    {
        // The same handle is used for identity verification, the adapter's
        // pre-parse digest and parse, and the post-parse digest.
        using var locked = new FileStream(project.GetProjectPath(), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (locked.Length is <= 0 or > EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes)
        {
            throw new InvalidDataException("ENGAUGE_DEV_SOURCE_SIZE_INVALID");
        }
        // The callback runs after opening and before the adapter's first read.
        // Check both inventory ancestry and the opened handle's actual path.
        EngaugeDigWholeWorkflowTruth truth = EngaugeDigWholeWorkflowTruthAdapter.Read(
            locked, project.AnonymizedCaseId, () =>
            {
                _ = selector.RequireProject(project.GetProjectPath(), project.AnonymizedCaseId, project.AnonymizedStudyId, token);
                ValidateOpenedSourcePath(locked, project.GetProjectPath());
            }, token);
        locked.Position = 0;
        string after = HashStream(locked, token);
        ValidateOpenedSourcePath(locked, project.GetProjectPath());
        if (!string.Equals(truth.ProjectSha256, after, StringComparison.Ordinal))
        {
            throw new InvalidDataException("ENGAUGE_DEV_SOURCE_CHANGED");
        }
        var centers = truth.Curves.SelectMany(static curve => curve.Points)
            .Select(static point => new EngaugePositiveCenter(point.ScreenX, point.ScreenY)).ToArray();
        return new EngaugeDevSourceSample(project.AnonymizedCaseId, project.AnonymizedStudyId, partition,
            after, truth.SourceImageSha256, truth.SourceWidth, truth.SourceHeight, truth.CopyImageBytes(), centers);
    }

    internal static void ValidateOpenedSourcePath(FileStream stream, string selectedPath)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        var buffer = new char[32768];
        uint size = GetFinalPathNameByHandleW(stream.SafeFileHandle, buffer, (uint)buffer.Length, 0);
        if (size == 0 || size >= buffer.Length) { throw new InvalidDataException("ENGAUGE_DEV_SOURCE_HANDLE_PATH_UNAVAILABLE"); }
        string actual = new(buffer, 0, checked((int)size));
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            actual = @"\\" + actual[8..];
        }
        else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            actual = actual[4..];
        }
        if (!string.Equals(Path.GetFullPath(actual), Path.GetFullPath(selectedPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("ENGAUGE_DEV_SOURCE_HANDLE_PATH_MISMATCH");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint size, uint flags);

    private static string HashStream(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void ValidateSample(
        EngaugeDevSourceSample sample, FrozenRealCorpusProject project, EngaugeDevStudyPartition partition)
    {
        if (sample is null || sample.CaseId != project.AnonymizedCaseId || sample.StudyId != project.AnonymizedStudyId ||
            sample.Partition != partition || sample.Width <= 0 || sample.Height <= 0 || sample.PositiveCenters.Count == 0)
        {
            throw new InvalidDataException("ENGAUGE_DEV_SOURCE_SAMPLE_INVALID");
        }
        ValidateHash(sample.ProjectSha256);
        ValidateHash(sample.ImageSha256);
        byte[] image = sample.CopyImageBytes();
        if (image.Length == 0 || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(image)), sample.ImageSha256, StringComparison.Ordinal) ||
            sample.PositiveCenters.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X < 0 || point.Y < 0 || point.X >= sample.Width || point.Y >= sample.Height))
        {
            throw new InvalidDataException("ENGAUGE_DEV_SOURCE_SAMPLE_INVALID");
        }
    }

    private static void ValidateHash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(static c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException("ENGAUGE_DEV_SOURCE_HASH_INVALID");
        }
    }
}
