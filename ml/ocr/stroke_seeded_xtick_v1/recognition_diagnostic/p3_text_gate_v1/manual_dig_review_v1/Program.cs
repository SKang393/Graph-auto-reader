// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ManualDigAnchorReview;

internal static class Program
{
    private const string CatalogRelative = "artifacts/goal22-runs/manual-train-only-source-capture-20261031-v1/manifest.json";
    private const string CatalogSha256 = "a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57";
    private const string PlanRelative = "artifacts/goal22-runs/marker-train-overlay-corpus-plan-v7/plan.json";
    private const string PlanSha256 = "34431350ed5e94dce0b2debd481b855ed673fdb7839d669527db397a48b64428";
    private const string CaptureAssemblyRelative = "tools/GraphReader.ManualTrainOnlyCapture/bin/Release/net10.0-windows/GraphReader.ManualTrainOnlyCapture.dll";
    private const string CaptureAssemblySha256 = "882cd78c3a4a94ef64bab3e6da4aa0ddf917a023f6f5453980428a9ba201ccca";
    private const string OutputRelative = "artifacts/manual-dig-train-anchor-review-v1";
    private const int SampleStudyGroups = 8;
    private const BindingFlags NonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private sealed record CatalogCase(string CaseId, string StudyId, string EmbeddedImageSha256);
    private sealed record Anchor(double ScreenX, double ScreenY, double GraphX, double GraphY);
    private sealed record Sample(string SampleId, string StudyGroupId, string ImageSha256,
        int Width, int Height, string ImageFile, IReadOnlyList<Anchor> Anchors);

    private static int Main()
    {
        try
        {
            string root = FindRoot();
            string catalogPath = Path.Combine(root, CatalogRelative.Replace('/', Path.DirectorySeparatorChar));
            byte[] catalogBytes = File.ReadAllBytes(catalogPath);
            RequireHash(catalogBytes, CatalogSha256, "train-only catalog");
            using JsonDocument catalog = JsonDocument.Parse(catalogBytes);
            JsonElement manifest = catalog.RootElement;
            if (manifest.GetProperty("split").GetString() != "train" ||
                manifest.GetProperty("counts").GetProperty("train_project_payloads_opened").GetInt32() != 104 ||
                manifest.GetProperty("counts").GetProperty("development_project_payloads_opened").GetInt32() != 0 ||
                manifest.GetProperty("scope").GetProperty("sealed_payload_reads").GetInt32() != 0)
                throw new InvalidDataException("Manual catalog is not the pinned train-only capture.");

            Dictionary<string, CatalogCase> catalogCases = manifest.GetProperty("rows").EnumerateArray()
                .Select(row => new CatalogCase(row.GetProperty("caseId").GetString()!,
                    row.GetProperty("studyId").GetString()!, row.GetProperty("embeddedImageSha256").GetString()!))
                .ToDictionary(static row => row.CaseId, StringComparer.Ordinal);
            byte[] planBytes = File.ReadAllBytes(Path.Combine(root, PlanRelative.Replace('/', Path.DirectorySeparatorChar)));
            RequireHash(planBytes, PlanSha256, "train-only group plan");
            using JsonDocument plan = JsonDocument.Parse(planBytes);
            JsonElement[] buildSources = plan.RootElement.GetProperty("source_rows").EnumerateArray()
                .Where(static row => row.GetProperty("assigned_partition").GetString() == "internal_train_build")
                .OrderBy(static row => row.GetProperty("component_sha256").GetString(), StringComparer.Ordinal)
                .ToArray();
            if (buildSources.Length != 15)
                throw new InvalidDataException("Unexpected build-only group count.");
            CatalogCase[] cases = buildSources
                .Take(SampleStudyGroups)
                .Select(row => new CatalogCase(row.GetProperty("case_id").GetString()!,
                    row.GetProperty("study_id").GetString()!, row.GetProperty("image_sha256").GetString()!))
                .ToArray();
            if (cases.Length != SampleStudyGroups || cases.Select(static row => row.StudyId).Distinct().Count() != cases.Length ||
                cases.Select(static row => row.EmbeddedImageSha256).Distinct(StringComparer.Ordinal).Count() != cases.Length ||
                cases.Any(row => !catalogCases.TryGetValue(row.CaseId, out CatalogCase? catalogRow) || catalogRow != row))
                throw new InvalidDataException("Selected build examples do not match the pinned train catalog.");

            string assemblyPath = Path.Combine(root, CaptureAssemblyRelative.Replace('/', Path.DirectorySeparatorChar));
            byte[] assemblyBytes = File.ReadAllBytes(assemblyPath);
            RequireHash(assemblyBytes, CaptureAssemblySha256, "train-capture assembly");
            Assembly assembly = Assembly.LoadFrom(assemblyPath);
            Type selectorType = RequiredType(assembly, "GraphReader.RealAcceptance.Ocr.FrozenDevTrainingSelector");
            object selector = RequiredMethod(selectorType, "Load", NonPublicStatic)
                .Invoke(null, [Path.Combine(root, "data", "manual data"), CancellationToken.None])!;
            MethodInfo enumerate = RequiredMethod(selectorType, "EnumerateSelectedProjects", NonPublicInstance);
            MethodInfo requireProject = RequiredMethod(selectorType, "RequireProject", NonPublicInstance);
            var sampleByCase = cases.ToDictionary(static row => row.CaseId, StringComparer.Ordinal);
            object projectsObject = enumerate.Invoke(selector, [CancellationToken.None])!;
            if (projectsObject is not IEnumerable projects)
                throw new InvalidDataException("Frozen train selector returned an invalid project inventory.");

            Type projectType = RequiredType(assembly, "GraphReader.RealAcceptance.Ocr.FrozenRealCorpusProject");
            Type adapterType = RequiredType(assembly, "GraphReader.RealAcceptance.Ocr.EngaugeDigWholeWorkflowTruthAdapter");
            Type materializerType = RequiredType(assembly, "GraphReader.RealAcceptance.Ocr.EngaugeDevSourceMaterializer");
            MethodInfo partitionForStudy = RequiredMethod(materializerType, "PartitionForStudy", NonPublicStatic);
            MethodInfo read = adapterType.GetMethods(NonPublicStatic).Single(method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 4 && parameters[0].ParameterType == typeof(FileStream) &&
                    parameters[1].ParameterType == typeof(string) && parameters[2].ParameterType == typeof(Action) &&
                    parameters[3].ParameterType == typeof(CancellationToken);
            });
            string outputPath = Path.Combine(root, OutputRelative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(outputPath) || File.Exists(outputPath))
                throw new IOException("Train-only anchor review output already exists.");
            Directory.CreateDirectory(Path.Combine(outputPath, "images"));

            var extracted = new List<Sample>(SampleStudyGroups);
            foreach (object project in projects)
            {
                string caseId = ReadProperty<string>(projectType, project, "AnonymizedCaseId");
                if (!sampleByCase.TryGetValue(caseId, out CatalogCase? selected))
                    continue;
                string studyId = ReadProperty<string>(projectType, project, "AnonymizedStudyId");
                string split = ReadProperty<string>(projectType, project, "Split");
                if (!string.Equals(split, "real-dev", StringComparison.Ordinal) || studyId != selected.StudyId ||
                    !string.Equals(partitionForStudy.Invoke(null, [studyId])?.ToString(), "Train", StringComparison.Ordinal))
                    throw new InvalidDataException("Selected train project does not match the pinned catalog identity.");
                string sourcePath = (string)RequiredMethod(projectType, "GetProjectPath", NonPublicInstance)
                    .Invoke(project, null)!;
                object current = requireProject.Invoke(selector,
                    [sourcePath, caseId, studyId, CancellationToken.None])!;
                if (!string.Equals(ReadProperty<string>(projectType, current, "Split"), "real-dev", StringComparison.Ordinal))
                    throw new InvalidDataException("Train case failed selector revalidation.");

                using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.SequentialScan);
                Action revalidate = () => requireProject.Invoke(selector,
                    [sourcePath, caseId, studyId, CancellationToken.None]);
                object parsed = read.Invoke(null, [stream, caseId, revalidate, CancellationToken.None])!;
                Type parsedType = parsed.GetType();
                string imageSha = ReadProperty<string>(parsedType, parsed, "SourceImageSha256");
                if (!string.Equals(imageSha, selected.EmbeddedImageSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("Embedded train image hash differs from the capture catalog.");
                int width = ReadProperty<int>(parsedType, parsed, "SourceWidth");
                int height = ReadProperty<int>(parsedType, parsed, "SourceHeight");
                object anchorObject = ReadProperty<object>(parsedType, parsed, "Anchors");
                if (anchorObject is not IEnumerable anchorItems)
                    throw new InvalidDataException("Parsed train anchors are missing.");
                var anchors = anchorItems.Cast<object>().Select(item => new Anchor(
                    ReadProperty<double>(item.GetType(), item, "ScreenX"),
                    ReadProperty<double>(item.GetType(), item, "ScreenY"),
                    ReadProperty<double>(item.GetType(), item, "GraphX"),
                    ReadProperty<double>(item.GetType(), item, "GraphY"))).ToArray();
                if (anchors.Length != 3 || anchors.Any(anchor =>
                        !double.IsFinite(anchor.ScreenX) || !double.IsFinite(anchor.ScreenY) ||
                        !double.IsFinite(anchor.GraphX) || !double.IsFinite(anchor.GraphY)))
                    throw new InvalidDataException("Parsed train calibration-anchor inventory is invalid.");

                string sampleId = $"train-sample-{extracted.Count + 1:D3}";
                string imageFile = $"images/{sampleId}.png";
                byte[] imageBytes = (byte[])RequiredMethod(parsedType, "CopyImageBytes", NonPublicInstance)
                    .Invoke(parsed, null)!;
                RequireHash(imageBytes, imageSha, "embedded train image");
                File.WriteAllBytes(Path.Combine(outputPath, imageFile.Replace('/', Path.DirectorySeparatorChar)), imageBytes);
                extracted.Add(new Sample(sampleId, $"train-study-group-{extracted.Count + 1:D3}",
                    imageSha, width, height, imageFile, anchors));
                if (extracted.Count == SampleStudyGroups)
                    break;
            }
            if (extracted.Count != SampleStudyGroups)
                throw new InvalidDataException("Not all selected train examples were available through the frozen selector.");

            var aggregate = new
            {
                schema = "graphreader.manual-dig-train-anchor-review.v1",
                catalog_sha256 = CatalogSha256,
                build_group_plan_sha256 = PlanSha256,
                capture_assembly_sha256 = CaptureAssemblySha256,
                selected_train_examples = extracted.Count,
                selected_distinct_study_groups = extracted.Select(static item => item.StudyGroupId).Distinct().Count(),
                selected_distinct_embedded_images = extracted.Select(static item => item.ImageSha256).Distinct(StringComparer.Ordinal).Count(),
                anchors_per_example = 3,
                anchor_count = extracted.Sum(static item => item.Anchors.Count),
                development_payloads_opened = 0,
                sealed_payloads_opened = 0,
                inference_runs = 0,
                optimizer_steps = 0,
                source_image_hash_overlap_with_sealed = "not established by train-only capture catalog",
                samples = extracted,
            };
            File.WriteAllText(Path.Combine(outputPath, "anchor-samples.json"),
                JsonSerializer.Serialize(aggregate, IndentedJson) + "\n");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "train-only examples extracted",
                examples = extracted.Count,
                groups = aggregate.selected_distinct_study_groups,
                anchors = aggregate.anchor_count,
                output = OutputRelative,
                development_payloads_opened = 0,
                sealed_payloads_opened = 0,
                optimizer_steps = 0,
            }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"BLOCKED: {error.GetType().Name}");
            return 2;
        }
    }

    private static string FindRoot()
    {
        DirectoryInfo? current = new(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Project root not found.");
    }

    private static Type RequiredType(Assembly assembly, string fullName) =>
        assembly.GetType(fullName, throwOnError: true, ignoreCase: false)!;

    private static MethodInfo RequiredMethod(Type type, string name, BindingFlags flags) =>
        type.GetMethod(name, flags) ?? throw new MissingMethodException(type.FullName, name);

    private static T ReadProperty<T>(Type type, object instance, string name)
    {
        PropertyInfo property = type.GetProperty(name, NonPublicInstance | BindingFlags.Public)
            ?? throw new MissingMemberException(type.FullName, name);
        object? value = property.GetValue(instance);
        return value is T typed ? typed : throw new InvalidCastException($"Unexpected value type for {name}.");
    }

    private static void RequireHash(byte[] bytes, string expected, string label)
    {
        string actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"{label} hash mismatch.");
    }
}
