using System.Security.Cryptography;
using System.Buffers;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.ManualTrainOnlyCapture;

internal sealed record TrainOnlySourceRow(
    string caseId,
    string studyId,
    string projectSha256,
    string embeddedImageSha256,
    int imageWidth,
    int imageHeight,
    string labelingScope,
    int curvePointCount,
    IReadOnlyList<TrainOnlyPoint> curvePoints);

internal sealed record TrainOnlyPoint(double x, double y);
internal sealed record TrainOnlySourceProgress(string caseId, string stage, string? projectSha256, string? embeddedImageSha256, int? curvePointCount, string? errorType);

internal sealed record TrainOnlyCaptureResult(
    string assignmentSha256,
    string selectedInventorySha256,
    string partitionRule,
    int selectedRealDevProjectCount,
    int trainProjectCount,
    int developmentProjectCount,
    int trainProjectReadCount,
    int developmentProjectReadCount,
    int duplicateEmbeddedImageGroups,
    int duplicateEmbeddedImageRows,
    int curvePointCount,
    IReadOnlyList<TrainOnlySourceRow> rows);

internal static class TrainOnlySourceCapture
{
    internal const string ExpectedAssignmentSha256 = FrozenRealCorpusInventory.FrozenAssignmentSha256;

    public static TrainOnlyCaptureResult Capture(
        FrozenDevTrainingSelector selector,
        string projectRoot,
        CancellationToken cancellationToken,
        Action<TrainOnlySourceProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        string root = Path.GetFullPath(projectRoot);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        if (!string.Equals(metadata.AssignmentSha256, ExpectedAssignmentSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Frozen source assignment digest differs from the reviewed identity.");

        IReadOnlyList<FrozenRealCorpusProject> projects = selector.EnumerateSelectedProjects(cancellationToken);
        if (projects.Count != 120 || projects.Any(static item => item.Split != FrozenRealCorpusInventory.RealDev))
            throw new InvalidDataException("Frozen metadata inventory must contain exactly the selected real-dev projects.");
        if (projects.Select(static item => Path.GetFullPath(item.GetProjectPath()))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != projects.Count)
            throw new InvalidDataException("Frozen metadata inventory contains duplicate project paths.");

        // Partition only metadata first. Paths for development projects are never opened or hashed.
        var partitions = projects.Select(project => (Project: project, Partition: EngaugeDevSourceMaterializer.PartitionForStudy(project.AnonymizedStudyId))).ToArray();
        var train = partitions.Where(static item => item.Partition == EngaugeDevStudyPartition.Train)
            .OrderBy(static item => item.Project.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
        var developmentCount = partitions.Count(static item => item.Partition == EngaugeDevStudyPartition.Development);
        if (train.Length != 104 || developmentCount != 16 ||
            projects.Select(static item => item.AnonymizedCaseId).Distinct(StringComparer.Ordinal).Count() != projects.Count)
            throw new InvalidDataException("Frozen study partition or case identity counts differ from the reviewed 104/16 split.");

        var rows = new List<TrainOnlySourceRow>(train.Length);
        foreach (var partitioned in train)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FrozenRealCorpusProject item = partitioned.Project;
            FrozenRealCorpusProject selected = selector.RequireProject(
                item.GetProjectPath(), item.AnonymizedCaseId, item.AnonymizedStudyId, cancellationToken);
            if (selected.Split != FrozenRealCorpusInventory.RealDev ||
                !string.Equals(selected.GetProjectPath(), item.GetProjectPath(), StringComparison.Ordinal))
                throw new InvalidDataException("Selected project changed or crossed the fixed real-dev inventory.");
            progress?.Invoke(new TrainOnlySourceProgress(selected.AnonymizedCaseId, "started", null, null, null, null));
            try
            {
                TrainOnlySourceRow row = ReadTrainProject(selector, selected, root, cancellationToken);
                rows.Add(row);
                progress?.Invoke(new TrainOnlySourceProgress(row.caseId, "completed", row.projectSha256, row.embeddedImageSha256, row.curvePointCount, null));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                progress?.Invoke(new TrainOnlySourceProgress(selected.AnonymizedCaseId, "failed", null, null, null, exception.GetType().Name));
                throw;
            }
        }
        _ = selector.EnumerateSelectedProjects(cancellationToken);
        (int duplicateGroups, int duplicateRows) = CountDuplicateImages(rows);

        return new TrainOnlyCaptureResult(
            metadata.AssignmentSha256,
            metadata.SelectedInventorySha256,
            EngaugeDevSourceMaterializer.PartitionRule,
            projects.Count,
            train.Length,
            developmentCount,
            rows.Count,
            0,
            duplicateGroups,
            duplicateRows,
            rows.Sum(static row => row.curvePointCount),
            rows);
    }

    internal static TrainOnlyCaptureResult CaptureForTest(
        IReadOnlyList<(string CaseId, string StudyId, string Split)> projects,
        Func<string, TrainOnlySourceRow> trainReader,
        string assignmentSha256 = ExpectedAssignmentSha256)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(trainReader);
        if (!IsSha256(assignmentSha256) || !string.Equals(assignmentSha256, ExpectedAssignmentSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Synthetic selector assignment digest differs from the reviewed identity.");
        if (projects.Select(static item => item.CaseId).Distinct(StringComparer.Ordinal).Count() != projects.Count)
            throw new InvalidDataException("Synthetic selector contains duplicate case identities.");
        if (projects.Any(static item => item.Split != FrozenRealCorpusInventory.RealDev))
            throw new InvalidDataException("Synthetic selector includes a split outside the selected real-dev inventory.");

        var partitions = projects.Select(item => (item.CaseId, Partition: EngaugeDevSourceMaterializer.PartitionForStudy(item.StudyId))).ToArray();
        var train = partitions.Where(static item => item.Partition == EngaugeDevStudyPartition.Train).ToArray();
        var developmentCount = partitions.Length - train.Length;
        var rows = train.Select(item => trainReader(item.CaseId)).ToArray();
        (int duplicateGroups, int duplicateRows) = CountDuplicateImages(rows);
        return new TrainOnlyCaptureResult(assignmentSha256, "synthetic-test-inventory", EngaugeDevSourceMaterializer.PartitionRule,
            projects.Count, train.Length, developmentCount, rows.Length, 0, duplicateGroups, duplicateRows,
            rows.Sum(static row => row.curvePointCount), rows);
    }

    private static (int Groups, int Rows) CountDuplicateImages(IReadOnlyList<TrainOnlySourceRow> rows)
    {
        int[] duplicateGroupSizes = rows.GroupBy(static row => row.embeddedImageSha256, StringComparer.Ordinal)
            .Select(static group => group.Count()).Where(static count => count > 1).ToArray();
        return (duplicateGroupSizes.Length, duplicateGroupSizes.Sum());
    }

    private static TrainOnlySourceRow ReadTrainProject(
        FrozenDevTrainingSelector selector,
        FrozenRealCorpusProject selected,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(selected.GetProjectPath());
        string relative = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Selected train project escaped the authorized project root.");
        FrozenRealCorpusProject currentBeforeOpen = selector.RequireProject(
            selected.GetProjectPath(), selected.AnonymizedCaseId, selected.AnonymizedStudyId, cancellationToken);
        if (!string.Equals(currentBeforeOpen.GetProjectPath(), path, StringComparison.Ordinal) ||
            currentBeforeOpen.Split != FrozenRealCorpusInventory.RealDev)
            throw new InvalidDataException("Selected source changed before opening.");
        RejectReparseChain(projectRoot, path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, path);
        ValidateTrainProjectLength(stream);
        string before = HashStream(stream, cancellationToken);
        stream.Position = 0;
        EngaugeDigWholeWorkflowTruth parsed = EngaugeDigWholeWorkflowTruthAdapter.Read(
            stream,
            selected.AnonymizedCaseId,
            () =>
            {
                FrozenRealCorpusProject current = selector.RequireProject(
                    selected.GetProjectPath(), selected.AnonymizedCaseId, selected.AnonymizedStudyId, cancellationToken);
                if (!string.Equals(current.GetProjectPath(), path, StringComparison.Ordinal) ||
                    current.Split != FrozenRealCorpusInventory.RealDev)
                    throw new InvalidDataException("Selected source changed before parsing began.");
                EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, path);
            },
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = 0;
        string after = HashStream(stream, cancellationToken);
        if (!string.Equals(before, after, StringComparison.Ordinal))
            throw new InvalidDataException("Selected train project changed while it was parsed.");

        var points = parsed.Curves.SelectMany(static curve => curve.Points)
            .Select(static point => new TrainOnlyPoint(point.ScreenX, point.ScreenY)).ToArray();
        if (parsed.SourceWidth <= 0 || parsed.SourceHeight <= 0 || points.Length == 0 ||
            points.Any(static point => !double.IsFinite(point.x) || !double.IsFinite(point.y)))
            throw new InvalidDataException("Selected train project has no usable finite curve-point annotations.");

        return new TrainOnlySourceRow(
            selected.AnonymizedCaseId,
            selected.AnonymizedStudyId,
            before,
            parsed.SourceImageSha256,
            parsed.SourceWidth,
            parsed.SourceHeight,
            "curve-point-centers-only; marker-glyph-correspondence-unverified; no-negative-labels",
            points.Length,
            points);
    }

    internal static void ValidateTrainProjectLength(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek || stream.Length is <= 0 or > EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes)
            throw new InvalidDataException("Selected train project length or stream capability is unsupported.");
    }

    internal static string HashStreamForTest(Stream stream, CancellationToken cancellationToken) => HashStream(stream, cancellationToken);

    private static string HashStream(Stream stream, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            stream.Position = 0;
            int count;
            while ((count = ReadNext(stream, buffer, cancellationToken)) != 0)
                hash.AppendData(buffer, 0, count);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static int ReadNext(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return stream.Read(buffer, 0, buffer.Length);
    }

    internal static TrainOnlySourceRow ReadTrainProjectForTest(
        FrozenDevTrainingSelector selector,
        FrozenRealCorpusProject selected,
        string projectRoot,
        CancellationToken cancellationToken) => ReadTrainProject(selector, selected, projectRoot, cancellationToken);

    private static void RejectReparseChain(string root, string path)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string current = Path.GetFullPath(path);
        while (current.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Selected train project path contains a reparse point.");
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase)) return;
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("Selected train project path has no parent.");
        }
        throw new InvalidDataException("Selected train project escaped the authorized project root.");
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static c => char.IsAsciiHexDigit(c));
}
