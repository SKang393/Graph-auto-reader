// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.RealAcceptance.Ocr;

internal static class Program
{
    private const string Root = @"E:\Graph-auto-reader";
    private const string Corpus = "data/manual data";
    private const string Output = "artifacts/manual-axis-ocr-diagnostic-v1/dev-axis-capture-v1";
    private const string TrainManifest = "artifacts/goal22-runs/manual-train-only-source-capture-20261031-v1/manifest.json";
    private const string TrainManifestHash = "a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static int Main(string[] args)
    {
        try
        {
            Require(args.Length is 1 or 3, "Arguments differ.");
            Require(args[0] is "--metadata" or "--capture" or "--truth", "Arguments differ.");
            Require(args[0] == "--truth" ? args.Length == 3 : args.Length == 1, "Arguments differ.");
            FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(Full(Corpus), CancellationToken.None);
            FrozenDevTrainingMetadata metadata = selector.Describe();
            FrozenRealCorpusProject[] projects = selector.EnumerateSelectedProjects(CancellationToken.None)
                .Where(static project => EngaugeDevSourceMaterializer.PartitionForStudy(project.AnonymizedStudyId) == EngaugeDevStudyPartition.Development)
                .OrderBy(static project => project.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
            Require(metadata.Cases.Count == 120 && projects.Length == 16 &&
                projects.All(static project => project.Split == FrozenRealCorpusInventory.RealDev), "Frozen development selection differs.");
            if (args[0] == "--metadata")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { status = "metadata-only", development_projects = projects.Length,
                    development_studies = projects.Select(static item => item.AnonymizedStudyId).Distinct(StringComparer.Ordinal).Count(),
                    project_payload_reads = 0, sealed_payload_reads = 0, optimizer_steps = 0 }));
                return 0;
            }
            if (args[0] == "--capture") Capture(selector, projects, metadata);
            else CaptureTruth(selector, projects, metadata, args[1], args[2]);
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            // Parser and filesystem exceptions can contain private study paths.
            Console.Error.WriteLine($"BLOCKED: {error.GetType().Name}; development capture did not complete.");
            return 1;
        }
    }

    private static void Capture(FrozenDevTrainingSelector selector, FrozenRealCorpusProject[] projects, FrozenDevTrainingMetadata metadata)
    {
        string destination = Full(Output);
        Require(!Directory.Exists(destination) && !File.Exists(destination), "New capture directory required.");
        RejectReparse(Path.GetDirectoryName(destination)!);
        byte[] trainBytes = ReadPinned(Full(TrainManifest), TrainManifestHash);
        using JsonDocument train = JsonDocument.Parse(trainBytes);
        Require(train.RootElement.GetProperty("split").GetString() == "train", "Training metadata differs.");
        var trainImages = train.RootElement.GetProperty("rows").EnumerateArray()
            .Select(static item => item.GetProperty("embeddedImageSha256").GetString()!).ToHashSet(StringComparer.Ordinal);
        Directory.CreateDirectory(destination);
        string imageDirectory = Path.Combine(destination, "images");
        Directory.CreateDirectory(imageDirectory);
        var images = new Dictionary<string, ImageRow>(StringComparer.Ordinal);
        var cases = new List<CaseRow>();
        foreach (FrozenRealCorpusProject project in projects)
        {
            EngaugeDigWholeWorkflowTruth truth = ReadProject(selector, project);
            if (!images.TryGetValue(truth.SourceImageSha256, out ImageRow? image))
            {
                string id = $"dev-image-{images.Count + 1:D3}";
                string name = $"source-{images.Count + 1:D3}.png";
                byte[] png = truth.CopyImageBytes();
                Require(Hash(png) == truth.SourceImageSha256, "Image digest differs.");
                using (var file = new FileStream(Path.Combine(imageDirectory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    file.Write(png);
                image = new ImageRow(id, $"{Output}/images/{name}", truth.SourceImageSha256,
                    truth.SourceWidth, truth.SourceHeight, trainImages.Contains(truth.SourceImageSha256));
                images.Add(image.image_sha256, image);
            }
            Require(image.width == truth.SourceWidth && image.height == truth.SourceHeight, "Duplicate image dimensions differ.");
            cases.Add(new CaseRow(project.AnonymizedCaseId, project.AnonymizedStudyId, image.id, truth.ProjectSha256));
        }
        Require(cases.Count == 16 && images.Count is > 0 and <= 16, "Development capture count differs.");
        string manifestPath = Path.Combine(destination, "manifest.json");
        WriteNew(manifestPath, new { schema = "graphreader.manual-dev-axis-input.v1", source_split = "internal_dev",
            assignment_sha256 = metadata.AssignmentSha256, inventory_sha256 = metadata.SelectedInventorySha256,
            train_manifest_sha256 = TrainManifestHash, rows = images.Values.ToArray(), cases,
            project_payload_reads = cases.Count, sealed_payload_reads = 0, optimizer_steps = 0, labels_exported = false });
        Console.WriteLine(JsonSerializer.Serialize(new { status = "captured", cases = cases.Count, images = images.Count,
            overlapping_train_images = images.Values.Count(static row => row.train_overlap),
            source_studies = projects.Select(static item => item.AnonymizedStudyId).Distinct(StringComparer.Ordinal).Count(),
            manifest_sha256 = Hash(File.ReadAllBytes(manifestPath)), sealed_payload_reads = 0, optimizer_steps = 0 }));
    }

    private static void CaptureTruth(FrozenDevTrainingSelector selector, FrozenRealCorpusProject[] projects,
        FrozenDevTrainingMetadata metadata, string manifestHash, string predictionHash)
    {
        Require(IsHash(manifestHash) && IsHash(predictionHash), "Expected hashes required.");
        string destination = Full(Output);
        RejectReparse(destination);
        byte[] manifestBytes = ReadPinned(Path.Combine(destination, "manifest.json"), manifestHash);
        string predictionPath = Full("artifacts/manual-axis-ocr-diagnostic-v1/tick-rank-dev-v1.json");
        byte[] predictionBytes = ReadPinned(predictionPath, predictionHash);
        using JsonDocument manifest = JsonDocument.Parse(manifestBytes);
        using JsonDocument prediction = JsonDocument.Parse(predictionBytes);
        Require(manifest.RootElement.GetProperty("schema").GetString() == "graphreader.manual-dev-axis-input.v1" &&
            manifest.RootElement.GetProperty("source_split").GetString() == "internal_dev" &&
            manifest.RootElement.GetProperty("assignment_sha256").GetString() == metadata.AssignmentSha256 &&
            manifest.RootElement.GetProperty("inventory_sha256").GetString() == metadata.SelectedInventorySha256 &&
            prediction.RootElement.GetProperty("split").GetString() == "dev" &&
            !prediction.RootElement.GetProperty("truth_read").GetBoolean(), "Prediction or source identity differs.");
        var savedCases = manifest.RootElement.GetProperty("cases").EnumerateArray()
            .ToDictionary(static row => row.GetProperty("case_id").GetString()!, StringComparer.Ordinal);
        var savedImages = manifest.RootElement.GetProperty("rows").EnumerateArray()
            .ToDictionary(static row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
        HashSet<string> predictedImages = prediction.RootElement.GetProperty("rows").EnumerateArray()
            .Select(static row => row.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        Require(savedCases.Count == projects.Length && projects.All(project => savedCases.ContainsKey(project.AnonymizedCaseId)),
            "Development case identities differ.");
        Require(predictedImages.SetEquals(savedImages.Keys), "Prediction image identities differ.");
        var rows = new List<object>();
        foreach (FrozenRealCorpusProject project in projects)
        {
            JsonElement saved = savedCases[project.AnonymizedCaseId];
            Require(saved.GetProperty("study_id").GetString() == project.AnonymizedStudyId, "Study identity differs.");
            EngaugeDigWholeWorkflowTruth truth = ReadProject(selector, project);
            string imageId = saved.GetProperty("image_id").GetString()!;
            Require(saved.GetProperty("project_sha256").GetString() == truth.ProjectSha256 &&
                savedImages.TryGetValue(imageId, out JsonElement image) &&
                image.GetProperty("image_sha256").GetString() == truth.SourceImageSha256, "Project or image bytes changed.");
            rows.Add(new { case_id = project.AnonymizedCaseId, image_id = imageId,
                anchors = truth.Anchors });
        }
        string truthPath = Path.Combine(destination, "anchors.json");
        WriteNew(truthPath, new { schema = "graphreader.manual-dev-axis-anchors.v1", source_split = "internal_dev",
            manifest_sha256 = manifestHash, predictions_sha256 = predictionHash, rows,
            labels_quality_approved = false, sealed_payload_reads = 0, optimizer_steps = 0 });
        Console.WriteLine(JsonSerializer.Serialize(new { status = "anchors-captured-after-prediction", cases = rows.Count,
            truth_sha256 = Hash(File.ReadAllBytes(truthPath)), sealed_payload_reads = 0, optimizer_steps = 0 }));
    }

    private static EngaugeDigWholeWorkflowTruth ReadProject(FrozenDevTrainingSelector selector, FrozenRealCorpusProject project)
    {
        FrozenRealCorpusProject current = selector.RequireProject(project.GetProjectPath(), project.AnonymizedCaseId,
            project.AnonymizedStudyId, CancellationToken.None);
        string path = Path.GetFullPath(current.GetProjectPath());
        RejectReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, path);
        Require(stream.Length is > 0 and <= EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes, "Project length differs.");
        string before = Convert.ToHexStringLower(SHA256.HashData(stream));
        stream.Position = 0;
        EngaugeDigWholeWorkflowTruth truth = EngaugeDigWholeWorkflowTruthAdapter.Read(stream, project.AnonymizedCaseId,
            () => EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, path), CancellationToken.None);
        stream.Position = 0;
        Require(before == Convert.ToHexStringLower(SHA256.HashData(stream)) && truth.ProjectSha256 == before,
            "Project identity changed during capture.");
        return truth;
    }

    private static string Full(string relative)
    {
        string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(full.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Path escaped project root.");
        return full;
    }

    private static void RejectReparse(string path)
    {
        string current = Path.GetPathRoot(path)!;
        foreach (string part in Path.GetRelativePath(current, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse path denied.");
        }
    }

    private static byte[] ReadPinned(string path, string expected)
    {
        RejectReparse(path);
        byte[] bytes = File.ReadAllBytes(path);
        Require(Hash(bytes) == expected, "File digest differs.");
        return bytes;
    }

    private static void WriteNew<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, JsonOptions);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool IsHash(string value) => value.Length == 64 && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private sealed record ImageRow(string id, string image_path, string image_sha256, int width, int height, bool train_overlap);
    private sealed record CaseRow(string case_id, string study_id, string image_id, string project_sha256);
}
