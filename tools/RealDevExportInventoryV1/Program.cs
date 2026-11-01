// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.RealAcceptance.Ocr;

if (args is ["--self-test"])
{
    string folder = Path.Combine(Path.GetTempPath(), "inventory-fixture");
    string project = Path.Combine(folder, "example.dig");
    string[] paths = [Path.Combine(folder, "example.csv"), Path.Combine(folder, "example.xlsx"),
        Path.Combine(folder, "another.csv"), Path.Combine(folder, "other", "example.csv")];
    if (SameFolderExactMatches(project, paths).Count != 2 ||
        SameFolderExactMatches(Path.Combine(folder, "missing.dig"), paths).Count != 0)
        throw new InvalidOperationException("Workbook inventory matching self-test failed.");
    Console.WriteLine("REAL_DEV_EXPORT_INVENTORY_SELF_TEST_PASS");
    return 0;
}

if (args.Length != 2 || args[1] != "--explicit-opt-in" ||
    args[0] is not ("--metadata-only" or "--write-private-manifest" or "--write-private-points"))
{
    Console.Error.WriteLine("REAL_DEV_EXPORT_INVENTORY_EXPLICIT_OPT_IN_REQUIRED");
    return 2;
}

try
{
    string root = Path.GetFullPath(Environment.CurrentDirectory);
    string corpusRoot = Path.Combine(root, "data", "manual data");
    FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpusRoot, CancellationToken.None);
    IReadOnlyList<FrozenRealCorpusProject> selected = selector.EnumerateSelectedProjects(CancellationToken.None);
    FrozenRealCorpusProject[] development = selected.Where(static item =>
        SHA256.HashData(Encoding.UTF8.GetBytes("graphreader-engauge-real-dev-study-v1\0" + item.AnonymizedStudyId))[0] >= 204).ToArray();
    if (selected.Count != 120 || development.Length != 16 ||
        development.Any(static item => item.Split != FrozenRealCorpusInventory.RealDev))
        throw new InvalidDataException("LOCKED_REAL_DEV_PARTITION_MISMATCH");

    var enumeration = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };
    string[] exports = Directory.EnumerateFiles(corpusRoot, "*", enumeration)
        .Where(static path => IsExportExtension(Path.GetExtension(path))).ToArray();
    int noExact = 0;
    int exactlyOne = 0;
    int multiple = 0;
    int csvMatchedProjects = 0;
    int xlsxMatchedProjects = 0;
    var privateMatches = new List<PrivateMatch>();
    foreach (FrozenRealCorpusProject project in development)
    {
        IReadOnlyList<string> matches = SameFolderExactMatches(project.GetProjectPath(), exports);
        if (matches.Count == 0) noExact++;
        else if (matches.Count == 1) exactlyOne++;
        else multiple++;
        if (matches.Any(static path => Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))) csvMatchedProjects++;
        if (matches.Any(static path => Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))) xlsxMatchedProjects++;
        if (matches.Count == 1)
            privateMatches.Add(new PrivateMatch(project.AnonymizedCaseId, project.GetProjectPath(), matches[0]));
    }
    _ = selector.EnumerateSelectedProjects(CancellationToken.None);
    var privatePoints = new List<object>();
    if (args[0] == "--write-private-points")
    {
        foreach (PrivateMatch match in privateMatches)
        {
            FrozenRealCorpusProject original = development.Single(item => item.AnonymizedCaseId == match.case_id);
            FrozenRealCorpusProject project = selector.RequireProject(
                original.GetProjectPath(), original.AnonymizedCaseId, original.AnonymizedStudyId, CancellationToken.None);
            using var stream = new FileStream(project.GetProjectPath(), FileMode.Open, FileAccess.Read, FileShare.Read);
            EngaugeDigWholeWorkflowTruth truth = EngaugeDigWholeWorkflowTruthAdapter.Read(stream, project.AnonymizedCaseId,
                () =>
                {
                    _ = selector.RequireProject(project.GetProjectPath(), project.AnonymizedCaseId,
                        project.AnonymizedStudyId, CancellationToken.None);
                    EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, project.GetProjectPath());
                }, CancellationToken.None);
            EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, project.GetProjectPath());
            stream.Position = 0;
            if (Convert.ToHexStringLower(SHA256.HashData(stream)) != truth.ProjectSha256)
                throw new InvalidDataException("DEV_PROJECT_CHANGED_DURING_READ");
            privatePoints.Add(new
            {
                match.case_id, match.project_path, match.export_path,
                project_sha256 = truth.ProjectSha256,
                points = truth.Curves.SelectMany(static curve => curve.Points)
                    .Select(static point => new { x = point.GraphX, y = point.GraphY }).ToArray(),
            });
        }
    }
    var report = new
    {
        schema = "graphreader.real-dev-export-inventory-v1",
        selected_project_count = selected.Count,
        development_project_count = development.Length,
        no_exact_same_folder_export = noExact,
        exactly_one_same_folder_export = exactlyOne,
        multiple_same_folder_exports = multiple,
        projects_with_csv_match = csvMatchedProjects,
        projects_with_xlsx_match = xlsxMatchedProjects,
        assignment_sha256 = selector.Describe().AssignmentSha256,
        source_sha256 = HashFile(Path.Combine(root, "tools", "RealDevExportInventoryV1", "Program.cs")),
        project_sha256 = HashFile(Path.Combine(root, "tools", "RealDevExportInventoryV1", "RealDevExportInventoryV1.csproj")),
        truth_adapter_sha256 = HashFile(Path.Combine(root, "tools", "GraphReader.RealAcceptance.Ocr", "EngaugeDigWholeWorkflowTruthAdapter.cs")),
        inventory_sha256 = HashFile(Path.Combine(root, "tools", "GraphReader.RealAcceptance.Ocr", "FrozenRealCorpusInventory.cs")),
        selector_sha256 = HashFile(Path.Combine(root, "tools", "GraphReader.RealAcceptance.Ocr", "FrozenDevTrainingSelector.cs")),
        materializer_sha256 = HashFile(Path.Combine(root, "tools", "GraphReader.RealAcceptance.Ocr", "EngaugeDevSourceMaterializer.cs")),
        loaded_tool_assembly_sha256 = HashFile(System.Reflection.Assembly.GetExecutingAssembly().Location),
        loaded_export_assembly_sha256 = HashFile(typeof(GraphReader.Export.ExportPoint).Assembly.Location),
        metadata_only = args[0] != "--write-private-points",
        project_payloads_opened = args[0] == "--write-private-points",
        spreadsheet_payloads_opened = false,
        sealed_payloads_read = false,
        paths_or_case_identifiers_in_aggregate = false,
        private_path_manifest_written = args[0] == "--write-private-manifest",
        private_points_written = args[0] == "--write-private-points",
        production_approval = false,
    };
    string output = Path.Combine(root, "artifacts", "real-dev-export-inventory-v1", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(output);
    if (args[0] == "--write-private-manifest")
        File.WriteAllText(Path.Combine(output, "private-matches.json"),
            JsonSerializer.Serialize(privateMatches, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    if (args[0] == "--write-private-points")
        File.WriteAllText(Path.Combine(output, "private-points.json"),
            JsonSerializer.Serialize(privatePoints, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(output, "aggregate-report.json"),
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
        new UTF8Encoding(false));
    Console.WriteLine(JsonSerializer.Serialize(report));
    return 0;
}
catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
{
    Console.Error.WriteLine("REAL_DEV_EXPORT_INVENTORY_FAILED " + error.GetType().Name);
    return 1;
}

static bool IsExportExtension(string extension) =>
    extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
    extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
    extension.Equals(".xls", StringComparison.OrdinalIgnoreCase);

static string HashFile(string path)
{
    using FileStream input = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(input));
}

static IReadOnlyList<string> SameFolderExactMatches(string projectPath, IEnumerable<string> candidates)
{
    string? folder = Path.GetDirectoryName(projectPath);
    string stem = Path.GetFileNameWithoutExtension(projectPath);
    return candidates.Where(path => IsExportExtension(Path.GetExtension(path)) &&
            string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileNameWithoutExtension(path), stem, StringComparison.OrdinalIgnoreCase))
        .ToArray();
}

internal sealed record PrivateMatch(string case_id, string project_path, string export_path);
