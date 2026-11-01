using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.MarkerTrainOverlayPilot;

internal static class Program
{
    private const string ProjectRoot = @"E:\Graph-auto-reader";
    private const string PlanRelativePath = "artifacts/goal22-runs/marker-train-overlay-pilot-plan-v2/plan.json";
    private const string ProtocolRelativePath = "ml/markers/center/manual_train_overlay_pilot_v1/protocol.json";
    private const string SourceManifestRelativePath = "artifacts/goal22-runs/manual-train-only-source-capture-20261031-v1/manifest.json";
    private const string ProtocolSchema = "graphreader.manual-train-marker-overlay-pilot-protocol.v1";
    private const string PlanSchema = "graphreader.manual-train-marker-overlay-pilot-plan.v1";
    private const string ManifestSha256 = "a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 7 && args[0] == "--extract" && args[1] == "--expected-protocol-sha256" && args[3] == "--expected-plan-sha256" && args[5] == "--output-dir")
            {
                string root = Path.GetFullPath(ProjectRoot);
                string planPath = ResolveProjectFile(root, PlanRelativePath);
                string protocolPath = ResolveProjectFile(root, ProtocolRelativePath);
                using var protocolLease = new FileStream(protocolPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.SequentialScan);
                byte[] protocolBytes = ReadAll(protocolLease);
                string protocolHash = HashBytes(protocolBytes);
                ParseAndVerifyProtocol(root, args[2], protocolBytes, protocolHash);
                VerifySourceBindings(root, protocolBytes);
                using var planLease = new FileStream(planPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.SequentialScan);
                byte[] planBytes = ReadAll(planLease);
                RequireHash(planBytes, args[4], "pilot plan");
                string manifestPath = ResolveProjectFile(root, SourceManifestRelativePath);
                RequireHash(manifestPath, ManifestSha256, "authorized train-only source manifest");
                Plan plan = LoadPlan(planBytes, protocolHash);
                string outputDirectory = ResolveNewOutputDirectory(root, args[6]);
                RuntimeBinding runtimeBefore = BindRuntime(root);
                SelectorResult selected = LoadSelectedTrainProjects(root, plan);

                Directory.CreateDirectory(outputDirectory);
                RejectReparseChain(root, outputDirectory, allowMissingLeaf: false);
                string reservation = Path.Combine(outputDirectory, ".pilot-output-reservation");
                using (new FileStream(reservation, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                string attemptPath = Path.Combine(outputDirectory, "source-attempt.jsonl");
                var results = new List<ExtractedImage>(plan.sources.Count);
                for (int index = 0; index < plan.sources.Count; index++)
                {
                    PlanSource source = plan.sources[index];
                    FrozenRealCorpusProject project = selected.ByCaseId[source.case_id];
                    AppendAttempt(attemptPath, new { event_name = "source_started", source_index = index });
                    try
                    {
                        ExtractedImage result = ExtractOne(root, project, source, index, outputDirectory);
                        results.Add(result);
                        AppendAttempt(attemptPath, new { event_name = "source_completed", source_index = index, result.extracted_png_sha256, result.byte_length });
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                    {
                        AppendAttempt(attemptPath, new { event_name = "source_failed", source_index = index, error_type = exception.GetType().Name });
                        throw;
                    }
                }
                AppendAttempt(attemptPath, new { event_name = "sources_complete", source_count = results.Count });
                RuntimeBinding runtimeAfter = BindRuntime(root);
                if (!Equals(runtimeBefore, runtimeAfter)) throw new InvalidDataException("Runtime identity changed during extraction.");
                if (!string.Equals(HashStream(protocolLease), protocolHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(HashStream(planLease), args[4], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Protocol or plan changed during source extraction.");

                var receipt = new ExtractionReceipt(
                    "graphreader.manual-train-marker-overlay-pilot-source-extraction.v1",
                    "train_only_source_pngs_extracted_overlay_not_yet_rendered",
                    HashFile(planPath), protocolHash, ManifestSha256, HashFile(attemptPath),
                    selected.AssignmentSha256, selected.SelectedInventorySha256,
                    results.Count, 2 * results.Count + 1, 0, 0, 0, false, runtimeAfter, results);
                string receiptPath = Path.Combine(outputDirectory, "source-receipt.json");
                await WriteNewJsonAsync(receiptPath, receipt, CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "source_pngs_extracted",
                    sources = results.Count,
                    development_payload_reads = 0,
                    sealed_payload_reads = 0,
                    optimizer_steps = 0,
                    training_admitted = false,
                    receipt_sha256 = HashFile(receiptPath),
                }, JsonOptions));
                return 0;
            }

            Console.Error.WriteLine("Usage: --extract --expected-protocol-sha256 <sha> --expected-plan-sha256 <sha> --output-dir <new-project-relative-directory>");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine($"BLOCKED: {exception.GetType().Name}");
            return 2;
        }
    }

    private static void ParseAndVerifyProtocol(string root, string expected, byte[] bytes, string actual)
    {
        if (!IsSha256(expected)) throw new InvalidDataException("External protocol SHA-256 is invalid.");
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Protocol digest mismatch.");
        using JsonDocument json = JsonDocument.Parse(bytes);
        JsonElement value = json.RootElement;
        RequireKeys(value, "schema", "status", "input_manifest", "input_manifest_sha256", "expected_train_projects", "grouping", "rendering", "scope", "source_files", "runtime_assemblies");
        if (value.GetProperty("schema").GetString() != ProtocolSchema ||
            value.GetProperty("status").GetString() != "train-only-feasibility-pilot-unapproved" ||
            value.GetProperty("input_manifest").GetString() != SourceManifestRelativePath ||
            value.GetProperty("input_manifest_sha256").GetString() != ManifestSha256 ||
            value.GetProperty("expected_train_projects").GetInt32() != 104)
            throw new InvalidDataException("Protocol schema or train-only source identity differs.");
        JsonElement scope = value.GetProperty("scope");
        RequireKeys(scope, "development_payload_reads", "sealed_payload_reads", "optimizer_steps", "training_admitted", "full_image_false_positive_precision", "historical_independence");
        if (scope.GetProperty("development_payload_reads").GetInt32() != 0 || scope.GetProperty("sealed_payload_reads").GetInt32() != 0 ||
            scope.GetProperty("optimizer_steps").GetInt32() != 0 || scope.GetProperty("training_admitted").GetBoolean())
            throw new InvalidDataException("Protocol permits an out-of-scope read or optimizer step.");
        if (scope.GetProperty("full_image_false_positive_precision").GetString() != "not measured" ||
            scope.GetProperty("historical_independence").GetString() != "unverified")
            throw new InvalidDataException("Protocol overstates evaluation or independence.");
        JsonElement grouping = value.GetProperty("grouping");
        RequireKeys(grouping, "rule", "partition", "domain");
        if (grouping.GetProperty("rule").GetString() != "connected components over study id and exact embedded-image SHA-256" ||
            grouping.GetProperty("partition").GetString() != "assigned signature only; one component per internal train feasibility partition" ||
            grouping.GetProperty("domain").GetString() != "graphreader.marker-train-overlay-pilot-v1")
            throw new InvalidDataException("Protocol grouping contract differs.");
        JsonElement rendering = value.GetProperty("rendering");
        RequireKeys(rendering, "patch", "positive", "distractor", "rounding", "native_content");
        if (rendering.GetProperty("patch").GetString() != "opaque white 72x56 pixel rectangle centered on selected curve point" ||
            rendering.GetProperty("positive").GetString() != "filled black circle radius 4 pixels centered at rounded curve point" ||
            rendering.GetProperty("distractor").GetString() != "black horizontal line 11 pixels long, offset (+22,+12) from positive center" ||
            rendering.GetProperty("rounding").GetString() != "floor(value + 0.5) for nonnegative screen coordinates" ||
            rendering.GetProperty("native_content").GetString() != "unknown, including inside and outside patch; no native negatives inferred")
            throw new InvalidDataException("Protocol rendering contract differs.");
        JsonElement runtime = value.GetProperty("runtime_assemblies");
        RequireKeys(runtime, "GraphReader.Export", "GraphReader.MarkerTrainOverlayPilot");
        RuntimeBinding runtimeActual = BindRuntime(root);
        if (runtime.GetProperty("GraphReader.Export").GetString() != runtimeActual.ExportAssemblySha256 ||
            runtime.GetProperty("GraphReader.MarkerTrainOverlayPilot").GetString() != runtimeActual.ExtractorAssemblySha256)
            throw new InvalidDataException("Runtime assemblies differ from their external protocol pins.");
        string expectedExecutable = Path.Combine(root, "ml", "markers", "center", "manual_train_overlay_pilot_v1", "extractor", "bin", "Release", "net10.0-windows", "GraphReader.MarkerTrainOverlayPilot.dll");
        string executingPath = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
        RejectReparseChain(root, executingPath, allowMissingLeaf: false);
        if (!string.Equals(executingPath, Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(HashFile(executingPath), runtimeActual.ExtractorAssemblySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Executing assembly location or bytes differ from the pinned runtime.");
    }

    private static void VerifySourceBindings(string root, byte[] protocolBytes)
    {
        using JsonDocument json = JsonDocument.Parse(protocolBytes);
        JsonElement bindings = json.RootElement.GetProperty("source_files");
        foreach (JsonProperty binding in bindings.EnumerateObject())
        {
            if (!IsRelativeProjectPath(binding.Name) || !IsSha256(binding.Value.GetString() ?? string.Empty))
                throw new InvalidDataException("Protocol source binding is invalid.");
            string path = ResolveProjectFile(root, binding.Name);
            RequireHash(path, binding.Value.GetString()!, "source binding");
        }
        string[] expected =
        [
            "Directory.Build.props",
            "Directory.Packages.props",
            "global.json",
            "src/GraphReader.Export/ExportContracts.cs",
            "src/GraphReader.Export/GraphReader.Export.csproj",
            "ml/markers/center/manual_train_overlay_pilot_v1/extractor/GraphReader.MarkerTrainOverlayPilot.csproj",
            "ml/markers/center/manual_train_overlay_pilot_v1/extractor/Program.cs",
            "ml/markers/center/manual_train_overlay_pilot_v1/plan.py",
            "ml/markers/center/manual_train_overlay_pilot_v1/render.py",
            "tools/GraphReader.RealAcceptance.Ocr/EngaugeDevSourceMaterializer.cs",
            "tools/GraphReader.RealAcceptance.Ocr/EngaugeDigWholeWorkflowTruthAdapter.cs",
            "tools/GraphReader.RealAcceptance.Ocr/FrozenDevTrainingSelector.cs",
            "tools/GraphReader.RealAcceptance.Ocr/FrozenRealCorpusInventory.cs",
            "tools/GraphReader.RealAcceptance.Ocr/WholeWorkflowCsvEvaluator.cs",
        ];
        if (!bindings.EnumerateObject().Select(static item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Protocol source binding set differs from the executable closure.");
    }

    private static Plan LoadPlan(byte[] bytes, string expectedProtocolHash)
    {
        using JsonDocument json = JsonDocument.Parse(bytes);
        JsonElement value = json.RootElement;
        RequireKeys(value, "schema", "status", "protocol_sha256", "source_capture_manifest_sha256", "source_capture_label_scope", "source_project_count",
            "study_image_connected_component_count", "partition_counts", "split_claim", "render_recipe", "sources", "limitations");
        if (value.GetProperty("schema").GetString() != PlanSchema || value.GetProperty("status").GetString() != "frozen_plan_not_yet_materialized" ||
            value.GetProperty("protocol_sha256").GetString() != expectedProtocolHash ||
            value.GetProperty("source_capture_manifest_sha256").GetString() != ManifestSha256 || value.GetProperty("source_project_count").GetInt32() != 104 ||
            value.GetProperty("source_capture_label_scope").GetString() != "curve-point-centers-only; marker-glyph-correspondence-unverified; no-negative-labels")
            throw new InvalidDataException("Plan is not the frozen 104-source train-only pilot plan.");
        JsonElement sourcesElement = value.GetProperty("sources");
        foreach (JsonElement source in sourcesElement.EnumerateArray())
        {
            RequireKeys(source, "assigned_partition", "component_sha256", "case_id", "study_id", "project_sha256", "image_sha256", "image_width", "image_height",
                "anchor_pixel", "anchor_source_point", "distractor_pixel", "curve_point_count");
            RequireKeys(source.GetProperty("anchor_pixel"), "x", "y");
            RequireKeys(source.GetProperty("anchor_source_point"), "x", "y");
            RequireKeys(source.GetProperty("distractor_pixel"), "x", "y");
        }
        PlanSource[] sources = JsonSerializer.Deserialize<PlanSource[]>(sourcesElement.GetRawText(), JsonOptions) ?? throw new InvalidDataException("Plan sources are missing.");
        if (sources.Length != 2 || sources.Select(static source => source.case_id).Distinct(StringComparer.Ordinal).Count() != 2 ||
            sources.Select(static source => source.study_id).Distinct(StringComparer.Ordinal).Count() != 2 ||
            sources.Select(static source => source.component_sha256).Distinct(StringComparer.Ordinal).Count() != 2 ||
            sources.Select(static source => source.image_sha256).Distinct(StringComparer.Ordinal).Count() != 2 ||
            !sources.Select(static source => source.assigned_partition).ToHashSet(StringComparer.Ordinal).SetEquals(["internal_train_build", "internal_train_holdout"]))
            throw new InvalidDataException("Pilot plan selections are not disjoint across assigned source groups.");
        foreach (PlanSource source in sources)
        {
            if (!IsSha256(source.case_id) || !IsSha256(source.study_id) || !IsSha256(source.project_sha256) || !IsSha256(source.image_sha256) ||
                !IsSha256(source.component_sha256) || source.image_width < 120 || source.image_height < 120 ||
                source.anchor_pixel.x < 40 || source.anchor_pixel.y < 40 || source.anchor_pixel.x >= source.image_width - 40 || source.anchor_pixel.y >= source.image_height - 40 ||
                !double.IsFinite(source.anchor_source_point.x) || !double.IsFinite(source.anchor_source_point.y) ||
                Math.Floor(source.anchor_source_point.x + 0.5) != source.anchor_pixel.x || Math.Floor(source.anchor_source_point.y + 0.5) != source.anchor_pixel.y ||
                source.distractor_pixel.x != source.anchor_pixel.x + 22 || source.distractor_pixel.y != source.anchor_pixel.y + 12 || source.curve_point_count <= 0)
                throw new InvalidDataException("Pilot plan source row is invalid.");
        }
        return new Plan(sources);
    }

    private static SelectorResult LoadSelectedTrainProjects(string root, Plan plan)
    {
        string corpus = Path.Combine(root, "data", "manual data");
        RejectReparseChain(root, corpus, allowMissingLeaf: false);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpus, CancellationToken.None);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        if (metadata.Cases.Count != 120 || metadata.CandidateAuthorized || metadata.SealedSourceOverlapVerified || metadata.AssignmentSha256 != FrozenRealCorpusInventory.FrozenAssignmentSha256)
            throw new InvalidDataException("Frozen metadata selector scope or assignment differs.");
        IReadOnlyList<FrozenRealCorpusProject> selected = selector.EnumerateSelectedProjects(CancellationToken.None);
        if (selected.Count != 120 || selected.Count(static project => project.Split == FrozenRealCorpusInventory.RealDev) != 120)
            throw new InvalidDataException("Frozen inventory selection is not the expected real-dev inventory.");
        var byCase = selected.ToDictionary(static project => project.AnonymizedCaseId, StringComparer.Ordinal);
        foreach (PlanSource source in plan.sources)
        {
            if (!byCase.TryGetValue(source.case_id, out FrozenRealCorpusProject? project) || project.Split != FrozenRealCorpusInventory.RealDev ||
                project.AnonymizedStudyId != source.study_id || EngaugeDevSourceMaterializer.PartitionForStudy(project.AnonymizedStudyId) != EngaugeDevStudyPartition.Train)
                throw new InvalidDataException("Plan source is not a selected train study.");
            _ = selector.RequireProject(project.GetProjectPath(), source.case_id, source.study_id, CancellationToken.None);
        }
        return new SelectorResult(selector, byCase, metadata.AssignmentSha256, metadata.SelectedInventorySha256);
    }

    private static ExtractedImage ExtractOne(string root, FrozenRealCorpusProject selected, PlanSource source, int index, string outputDirectory)
    {
        string path = Path.GetFullPath(selected.GetProjectPath());
        RejectReparseChain(root, path, allowMissingLeaf: false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, path);
        if (stream.Length is <= 0 or > EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes) throw new InvalidDataException("Selected source project length is unsupported.");
        string projectBefore = HashStream(stream);
        if (projectBefore != source.project_sha256) throw new InvalidDataException("Selected project SHA differs from the frozen plan.");
        stream.Position = 0;
        EngaugeDigWholeWorkflowTruth truth = EngaugeDigWholeWorkflowTruthAdapter.Read(
            stream, source.case_id, () => EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, path), CancellationToken.None);
        byte[] png = truth.CopyImageBytes();
        stream.Position = 0;
        string projectAfter = HashStream(stream);
        if (projectBefore != projectAfter || truth.ProjectSha256 != source.project_sha256 || truth.SourceImageSha256 != source.image_sha256 ||
            truth.SourceWidth != source.image_width || truth.SourceHeight != source.image_height)
            throw new InvalidDataException("Project or embedded image identity differs from the frozen plan.");
        int points = truth.Curves.Sum(static curve => curve.Points.Count);
        if (points != source.curve_point_count || !truth.Curves.SelectMany(static curve => curve.Points)
                .Any(point => Math.Floor(point.ScreenX + 0.5) == source.anchor_pixel.x && Math.Floor(point.ScreenY + 0.5) == source.anchor_pixel.y))
            throw new InvalidDataException("Engauge point evidence differs from the frozen anchor.");
        string outputPath = Path.Combine(outputDirectory, $"source-{index:02d}.png");
        RejectReparseChain(root, outputPath, allowMissingLeaf: true);
        using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
        {
            output.Write(png);
            output.Flush(true);
        }
        return new ExtractedImage(index, source.assigned_partition, source.case_id, source.study_id, projectBefore,
            source.image_sha256, HashBytes(png), source.image_width, source.image_height, png.Length, Path.GetFileName(outputPath), points);
    }

    private static RuntimeBinding BindRuntime(string root)
    {
        string export = Path.Combine(root, "src", "GraphReader.Export", "bin", "Release", "net10.0", "GraphReader.Export.dll");
        string assembly = Path.Combine(root, "ml", "markers", "center", "manual_train_overlay_pilot_v1", "extractor", "bin", "Release", "net10.0-windows", "GraphReader.MarkerTrainOverlayPilot.dll");
        return new RuntimeBinding(HashFile(ResolveProjectFile(root, Path.GetRelativePath(root, export).Replace('\\', '/'))), HashFile(ResolveProjectFile(root, Path.GetRelativePath(root, assembly).Replace('\\', '/'))));
    }

    private static string ResolveNewOutputDirectory(string root, string relative)
    {
        if (!IsRelativeProjectPath(relative)) throw new InvalidDataException("Output path must be project-relative.");
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(full) || File.Exists(full))
            throw new InvalidDataException("Output path is not a fresh project-local directory.");
        RejectReparseChain(root, full, allowMissingLeaf: true);
        return full;
    }

    private static string ResolveProjectFile(string root, string relative)
    {
        if (!IsRelativeProjectPath(relative)) throw new InvalidDataException("Bound input path is not project-relative.");
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bound input escaped project root.");
        RejectReparseChain(root, full, allowMissingLeaf: false);
        return full;
    }

    private static void RejectReparseChain(string root, string target, bool allowMissingLeaf)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullTarget = Path.GetFullPath(target);
        if (!fullTarget.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !string.Equals(fullTarget, fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Path escaped project root.");
        string current = Path.GetPathRoot(fullTarget) ?? throw new InvalidDataException("Path has no volume root.");
        foreach (string part in fullTarget[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            bool exists = File.Exists(current) || Directory.Exists(current);
            if (!exists && allowMissingLeaf && string.Equals(current, fullTarget, StringComparison.OrdinalIgnoreCase)) continue;
            if (!exists) throw new FileNotFoundException("Bound path component is missing.", current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Path contains a reparse point.");
        }
    }

    private static async Task WriteNewJsonAsync<T>(string path, T value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
        stream.Flush(true);
    }

    private static void AppendAttempt(string path, object record)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(record);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(json);
        stream.WriteByte((byte)'\n');
        stream.Flush(true);
    }

    private static string HashStream(Stream stream)
    {
        stream.Position = 0;
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static byte[] ReadAll(Stream stream)
    {
        stream.Position = 0;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        stream.Position = 0;
        return memory.ToArray();
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void RequireHash(byte[] bytes, string expected, string label)
    {
        if (!IsSha256(expected) || !string.Equals(HashBytes(bytes), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} hash differs.");
    }
    private static void RequireHash(string path, string expected, string label)
    {
        if (!IsSha256(expected) || HashFile(path) != expected) throw new InvalidDataException($"{label} hash differs.");
    }
    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static bool IsRelativeProjectPath(string value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Contains(':') && !value.Split('/', '\\').Any(static part => part is "" or "." or "..");
    private static void RequireKeys(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("JSON object fields differ from the frozen schema.");
    }

    private sealed record Plan(IReadOnlyList<PlanSource> sources);
    private sealed record PlanSource(string assigned_partition, string component_sha256, string case_id, string study_id, string project_sha256, string image_sha256,
        int image_width, int image_height, Pixel anchor_pixel, Point anchor_source_point, Pixel distractor_pixel, int curve_point_count);
    private sealed record Pixel(int x, int y);
    private sealed record Point(double x, double y);
    private sealed record SelectorResult(FrozenDevTrainingSelector Selector, IReadOnlyDictionary<string, FrozenRealCorpusProject> ByCaseId, string AssignmentSha256, string SelectedInventorySha256);
    private sealed record RuntimeBinding(string ExportAssemblySha256, string ExtractorAssemblySha256);
    private sealed record ExtractedImage(int index, string assigned_partition, string case_id, string study_id, string project_sha256, string image_sha256,
        string extracted_png_sha256, int width, int height, int byte_length, string path, int curve_point_count);
    private sealed record ExtractionReceipt(string schema, string status, string plan_sha256, string protocol_sha256, string source_manifest_sha256,
        string capture_attempt_sha256, string assignment_sha256, string selected_inventory_sha256, int extracted_sources, int capture_attempt_event_count,
        int development_payload_reads, int sealed_payload_reads,
        int optimizer_steps, bool training_admitted, RuntimeBinding runtime, IReadOnlyList<ExtractedImage> sources);
}
