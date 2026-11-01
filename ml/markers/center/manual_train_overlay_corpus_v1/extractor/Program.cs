// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphReader.Export;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.MarkerTrainOverlayCorpus;

internal static class Program
{
    private const string Root = @"E:\Graph-auto-reader";
    private const string ProtocolRelative = "ml/markers/center/manual_train_overlay_corpus_v1/protocol.json";
    private const string PlanRelative = "artifacts/goal22-runs/marker-train-overlay-corpus-plan-v7/plan.json";
    private const string ManifestRelative = "artifacts/goal22-runs/manual-train-only-source-capture-20261031-v1/manifest.json";
    private const string CorpusRelative = "data/manual data";
    private const string ProtocolSchema = "graphreader.manual-train-marker-overlay-corpus-protocol.v1";
    private const string PlanSchema = "graphreader.manual-train-marker-overlay-corpus-plan.v1";
    private const string ManifestSchema = "graphreader.manual-train-only-source-capture.v1";
    private const string ManifestSha256 = "a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57";
    private const string PilotPlanSha256 = "eac5d357a7440537d209e2c22e2af957e06d4edc39e2036ac69b03d427a26243";
    private const string PilotPlanRelative = "artifacts/goal22-runs/marker-train-overlay-pilot-plan-v2/plan.json";
    private const int ExpectedTrainProjects = 104;
    private const int ExpectedInventoryProjects = 120;
    private const int ExpectedBuildGroups = 15;
    private const int ExpectedHoldoutGroups = 5;
    private const long MaximumExtractedSourceBytes = 1_000_000_000;
    private static string FailureStage = "argument_validation";

    private static readonly string[] BoundSources =
    [
        "Directory.Build.props",
        "Directory.Packages.props",
        "global.json",
        "ml/markers/center/manual_train_overlay_corpus_v1/extractor/GraphReader.MarkerTrainOverlayCorpus.csproj",
        "ml/markers/center/manual_train_overlay_corpus_v1/extractor/Program.cs",
        "ml/markers/center/manual_train_overlay_corpus_v1/materialize.py",
        "ml/markers/center/manual_train_overlay_corpus_v1/plan.py",
        "src/GraphReader.Export/ExportContracts.cs",
        "src/GraphReader.Export/GraphReader.Export.csproj",
        "tools/GraphReader.RealAcceptance.Ocr/EngaugeDevSourceMaterializer.cs",
        "tools/GraphReader.RealAcceptance.Ocr/EngaugeDigWholeWorkflowTruthAdapter.cs",
        "tools/GraphReader.RealAcceptance.Ocr/FrozenDevTrainingSelector.cs",
        "tools/GraphReader.RealAcceptance.Ocr/FrozenRealCorpusInventory.cs",
        "tools/GraphReader.RealAcceptance.Ocr/WholeWorkflowCsvEvaluator.cs",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 5 && args[0] == "--self-test" && args[1] == "--expected-protocol-sha256")
            {
                FailureStage = "metadata_preflight";
                Protocol protocol = LoadProtocol(args[2]);
                VerifySourceBindings(protocol);
                RuntimeBinding runtime = VerifyRuntime(protocol);
                byte[] pilotBytes = ReadPinnedFile(PilotPlanRelative, PilotPlanSha256);
                HashSet<string> pilotComponents = ValidatePilotPlan(pilotBytes);
                byte[] planBytes = ReadPinnedFile(PlanRelative, ExpectedPlanFromArgument(args, protocol));
                Plan plan = ParsePlan(planBytes, protocol, pilotComponents);
                ValidateTrainManifest(ReadPinnedFile(ManifestRelative, ManifestSha256), plan, pilotComponents);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "preflight_passed_no_source_payloads_opened",
                    source_count = plan.Sources.Count,
                    build_groups = plan.Sources.Count(static source => source.Partition == "internal_train_build"),
                    holdout_groups = plan.Sources.Count(static source => source.Partition == "internal_train_holdout"),
                    runtime,
                    development_payload_reads = 0,
                    sealed_payload_reads = 0,
                    optimizer_steps = 0,
                    training_admitted = false,
                }, JsonOptions));
                return 0;
            }

            if (args.Length == 7 && args[0] == "--extract" && args[1] == "--expected-protocol-sha256" &&
                args[3] == "--expected-plan-sha256" && args[5] == "--output-dir")
            {
                Extract(args[2], args[4], args[6]);
                return 0;
            }

            Console.Error.WriteLine("Usage: --self-test --expected-protocol-sha256 <sha256> --expected-plan-sha256 <sha256> | --extract --expected-protocol-sha256 <sha256> --expected-plan-sha256 <sha256> --output-dir <fresh-project-relative-directory>");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            string safeCode = SafeFailureCode(exception.Message);
            Console.Error.WriteLine($"BLOCKED: {exception.GetType().Name}; stage={FailureStage}{(safeCode.Length == 0 ? string.Empty : $"; code={safeCode}")}");
            return 2;
        }
    }

    // The self-test receives the plan pin as a fourth logical argument to keep
    // the CLI explicit without reading any source project payload.
    private static string ExpectedPlanFromArgument(string[] args, Protocol protocol)
    {
        _ = protocol;
        if (args.Length != 5 || args[3] != "--expected-plan-sha256" || !IsSha256(args[4]))
            throw new ArgumentException("Self-test also requires --expected-plan-sha256 <sha256>.");
        return args[4];
    }

    private static void Extract(string expectedProtocolSha256, string expectedPlanSha256, string requestedOutput)
    {
        FailureStage = "protocol_and_runtime_pins";
        Protocol protocol = LoadProtocol(expectedProtocolSha256);
        VerifySourceBindings(protocol);
        RuntimeBinding runtimeBefore = VerifyRuntime(protocol);

        FailureStage = "plan_and_train_metadata";
        byte[] pilotBytes = ReadPinnedFile(PilotPlanRelative, PilotPlanSha256);
        HashSet<string> pilotComponents = ValidatePilotPlan(pilotBytes);
        byte[] planBytes = ReadPinnedFile(PlanRelative, expectedPlanSha256);
        Plan plan = ParsePlan(planBytes, protocol, pilotComponents);
        byte[] manifestBytes = ReadPinnedFile(ManifestRelative, ManifestSha256);
        ValidateTrainManifest(manifestBytes, plan, pilotComponents);

        FailureStage = "output_path_and_space_preflight";
        string output = ResolveNewRunDirectory(requestedOutput);
        string volumeRoot = Path.GetPathRoot(output) ?? throw new InvalidDataException("OUTPUT_VOLUME_MISSING");
        if (new DriveInfo(volumeRoot).AvailableFreeSpace < MaximumExtractedSourceBytes)
            throw new IOException("INSUFFICIENT_SOURCE_EXTRACTION_FREE_SPACE");
        string corpusPath = ResolveExisting(CorpusRelative, directory: true);
        FailureStage = "train_inventory_metadata_selection";
        var selector = FrozenDevTrainingSelector.Load(corpusPath, CancellationToken.None);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        if (metadata.Cases.Count != ExpectedInventoryProjects || metadata.CandidateAuthorized ||
            metadata.SealedSourceOverlapVerified || metadata.AssignmentSha256 != FrozenRealCorpusInventory.FrozenAssignmentSha256)
            throw new InvalidDataException("FROZEN_TRAIN_INVENTORY_SCOPE_MISMATCH");

        IReadOnlyList<FrozenRealCorpusProject> selected = selector.EnumerateSelectedProjects(CancellationToken.None);
        if (selected.Count != ExpectedInventoryProjects || selected.Any(static project => project.Split != FrozenRealCorpusInventory.RealDev))
            throw new InvalidDataException("FROZEN_REAL_DEV_METADATA_SELECTION_MISMATCH");
        var byCase = selected.ToDictionary(static project => project.AnonymizedCaseId, StringComparer.Ordinal);
        foreach (PlanSource source in plan.Sources)
        {
            if (!byCase.TryGetValue(source.CaseId, out FrozenRealCorpusProject? project) ||
                project.Split != FrozenRealCorpusInventory.RealDev || project.AnonymizedStudyId != source.StudyId ||
                EngaugeDevSourceMaterializer.PartitionForStudy(project.AnonymizedStudyId) != EngaugeDevStudyPartition.Train)
                throw new InvalidDataException("PLAN_SOURCE_NOT_IN_AUTHORIZED_TRAIN_PARTITION");
            _ = selector.RequireProject(project.GetProjectPath(), source.CaseId, source.StudyId, CancellationToken.None);
        }

        FailureStage = "output_reservation";
        Directory.CreateDirectory(output);
        RejectReparseChain(output, allowMissingLeaf: false);
        using (new FileStream(Path.Combine(output, ".output-reservation"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        string attemptPath = Path.Combine(output, "source-attempt.jsonl");
        var successful = new List<ExtractedSource>(plan.Sources.Count);
        var failures = new List<FailedSource>();
        long extractedBytes = 0;

        FailureStage = "train_source_payload_extraction";
        foreach (PlanSource source in plan.Sources)
        {
            AppendAttempt(attemptPath, new { event_name = "source_started", source_index = source.SourceIndex });
            try
            {
                FrozenRealCorpusProject project = byCase[source.CaseId];
                ExtractedSource extracted = ExtractOne(project, source, output, MaximumExtractedSourceBytes - extractedBytes);
                successful.Add(extracted);
                extractedBytes += extracted.ByteLength;
                AppendAttempt(attemptPath, new { event_name = "source_completed", source_index = source.SourceIndex, extracted_png_sha256 = extracted.ExtractedPngSha256, byte_length = extracted.ByteLength });
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                failures.Add(new FailedSource(source.SourceIndex, exception.GetType().Name));
                AppendAttempt(attemptPath, new { event_name = "source_failed", source_index = source.SourceIndex, error_type = exception.GetType().Name });
            }
        }

        FailureStage = "post_extraction_revalidation_and_receipt";
        AppendAttempt(attemptPath, new { event_name = "sources_complete", source_count = plan.Sources.Count });
        RuntimeBinding runtimeAfter = VerifyRuntime(protocol);
        VerifySourceBindings(protocol);
        if (runtimeAfter != runtimeBefore || HashFile(ResolveExisting(ProtocolRelative)) != expectedProtocolSha256 ||
            HashFile(ResolveExisting(PlanRelative)) != expectedPlanSha256 || HashFile(ResolveExisting(ManifestRelative)) != ManifestSha256 ||
            HashFile(ResolveExisting(PilotPlanRelative)) != PilotPlanSha256)
            throw new InvalidDataException("BOUND_INPUT_OR_RUNTIME_CHANGED_DURING_EXTRACTION");

        bool complete = failures.Count == 0 && successful.Count == plan.Sources.Count;
        var receipt = new ExtractionReceipt(
            "graphreader.manual-train-marker-overlay-corpus-source-extraction.v1",
            complete ? "train_only_source_group_pngs_extracted_not_materialized" : "train_only_source_group_extraction_incomplete",
            expectedPlanSha256,
            expectedProtocolSha256,
            ManifestSha256,
            PilotPlanSha256,
            HashFile(attemptPath),
            metadata.AssignmentSha256,
            metadata.SelectedInventorySha256,
            plan.Sources.Count,
            successful.Count,
            failures.Count,
            2 * plan.Sources.Count + 1,
            extractedBytes,
            0,
            0,
            0,
            false,
            runtimeAfter,
            successful,
            failures);
        string receiptPath = Path.Combine(output, "source-receipt.json");
        WriteNewJson(receiptPath, receipt);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = receipt.Status,
            selected_sources = receipt.SelectedSources,
            extracted_sources = receipt.ExtractedSources,
            failed_sources = receipt.FailedSources,
            source_receipt_sha256 = HashFile(receiptPath),
            development_payload_reads = 0,
            sealed_payload_reads = 0,
            optimizer_steps = 0,
            training_admitted = false,
        }, JsonOptions));
    }

    private static Protocol LoadProtocol(string expectedSha256)
    {
        if (!IsSha256(expectedSha256)) throw new ArgumentException("Expected protocol SHA-256 is invalid.");
        byte[] bytes = ReadPinnedFile(ProtocolRelative, expectedSha256);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        RequireKeys(root, "schema", "status", "input_manifest", "input_manifest_sha256", "pilot_plan", "pilot_plan_sha256", "plan",
            "expected_train_projects", "expected_source_components", "excluded_examined_pilot_components", "grouping", "selection", "rendering", "scope", "source_files", "runtime_assemblies");
        if (root.GetProperty("schema").GetString() != ProtocolSchema ||
            root.GetProperty("status").GetString() != "train-only-source-grouped-overlay-expansion-unapproved" ||
            root.GetProperty("input_manifest").GetString() != ManifestRelative || root.GetProperty("input_manifest_sha256").GetString() != ManifestSha256 ||
            root.GetProperty("pilot_plan").GetString() != PilotPlanRelative || root.GetProperty("pilot_plan_sha256").GetString() != PilotPlanSha256 ||
            root.GetProperty("plan").GetString() != PlanRelative ||
            root.GetProperty("expected_train_projects").GetInt32() != ExpectedTrainProjects ||
            root.GetProperty("expected_source_components").GetInt32() != 22 ||
            root.GetProperty("excluded_examined_pilot_components").GetInt32() != 2)
            throw new InvalidDataException("PROTOCOL_SOURCE_OR_SCOPE_IDENTITY_MISMATCH");

        JsonElement scope = root.GetProperty("scope");
        RequireKeys(scope, "development_payload_reads", "sealed_payload_reads", "optimizer_steps", "training_admitted", "full_image_false_positive_precision", "historical_independence");
        if (scope.GetProperty("development_payload_reads").GetInt32() != 0 || scope.GetProperty("sealed_payload_reads").GetInt32() != 0 ||
            scope.GetProperty("optimizer_steps").GetInt32() != 0 || scope.GetProperty("training_admitted").GetBoolean() ||
            scope.GetProperty("full_image_false_positive_precision").GetString() != "not measured" || scope.GetProperty("historical_independence").GetString() != "unverified")
            throw new InvalidDataException("PROTOCOL_SCOPE_OR_LIMITATION_MISMATCH");

        JsonElement grouping = root.GetProperty("grouping");
        if (grouping.GetProperty("component_rule").GetString() != "connected components over study id and exact embedded-image SHA-256" ||
            grouping.GetProperty("component_domain").GetString() != "graphreader.marker-train-overlay-pilot-v1" ||
            grouping.GetProperty("partition_domain").GetString() != "graphreader.manual-train-overlay-corpus-v1" ||
            grouping.GetProperty("official_dev_or_historical_independence_claim").GetBoolean())
            throw new InvalidDataException("PROTOCOL_GROUPING_LIMITATION_MISMATCH");

        JsonElement selection = root.GetProperty("selection");
        if (selection.GetProperty("max_source_groups").GetInt32() != 20 || !selection.GetProperty("one_source_per_component").GetBoolean() ||
            !selection.GetProperty("pilot_components_excluded_from_generation").GetBoolean() ||
            selection.GetProperty("minimum_anchor_margin_px").GetInt32() != 64 || selection.GetProperty("max_source_pixels").GetInt32() != 40_000_000 ||
            selection.GetProperty("max_total_source_png_bytes").GetInt64() != MaximumExtractedSourceBytes)
            throw new InvalidDataException("PROTOCOL_SELECTION_RULE_MISMATCH");

        JsonElement rendering = root.GetProperty("rendering");
        if (rendering.GetProperty("patch_width_px").GetInt32() != 112 || rendering.GetProperty("patch_height_px").GetInt32() != 80 ||
            rendering.GetProperty("native_content_labeling").GetString() != "unknown_everywhere" ||
            rendering.GetProperty("outside_patch_pixels").GetString() != "must remain identical after RGBA decode")
            throw new InvalidDataException("PROTOCOL_RENDER_SCOPE_MISMATCH");
        ValidateRecipeSchema(rendering.GetProperty("recipes"));
        string[] recipeIds = rendering.GetProperty("recipes").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString() ?? string.Empty).ToArray();

        var sourceFiles = ReadHashMap(root.GetProperty("source_files"));
        var runtime = ReadHashMap(root.GetProperty("runtime_assemblies"));
        if (!sourceFiles.Keys.Order(StringComparer.Ordinal).SequenceEqual(BoundSources.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            !runtime.Keys.Order(StringComparer.Ordinal).SequenceEqual(["GraphReader.Export", "GraphReader.MarkerTrainOverlayCorpus"], StringComparer.Ordinal))
            throw new InvalidDataException("PROTOCOL_SOURCE_OR_RUNTIME_CLOSURE_MISMATCH");
        return new Protocol(expectedSha256, sourceFiles, runtime, recipeIds);
    }

    private static void ValidateRecipeSchema(JsonElement recipes)
    {
        if (recipes.ValueKind != JsonValueKind.Array || recipes.GetArrayLength() != 10)
            throw new InvalidDataException("PROTOCOL_RECIPE_COUNT_MISMATCH");
        string[] ids = recipes.EnumerateArray().Select(item => item.GetProperty("id").GetString() ?? string.Empty).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != 10 || ids.Count(static id => id.StartsWith("filled_", StringComparison.Ordinal) || id.StartsWith("open_", StringComparison.Ordinal)) != 5)
            throw new InvalidDataException("PROTOCOL_RECIPE_IDENTITIES_MISMATCH");
    }

    private static Plan ParsePlan(byte[] bytes, Protocol protocol, HashSet<string> pilotComponents)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        RequireKeys(root, "schema", "status", "protocol_sha256", "source_capture_manifest_sha256", "pilot_plan_sha256", "source_project_count",
            "source_component_count", "excluded_pilot_component_count", "accounting", "split_claim", "source_rows", "recipes", "limitations");
        if (root.GetProperty("schema").GetString() != PlanSchema || root.GetProperty("status").GetString() != "frozen_train_only_expansion_plan_unmaterialized" ||
            root.GetProperty("protocol_sha256").GetString() != protocol.Sha256 || root.GetProperty("source_capture_manifest_sha256").GetString() != ManifestSha256 ||
            root.GetProperty("pilot_plan_sha256").GetString() != PilotPlanSha256 || root.GetProperty("source_project_count").GetInt32() != ExpectedTrainProjects ||
            root.GetProperty("source_component_count").GetInt32() != 22 || root.GetProperty("excluded_pilot_component_count").GetInt32() != 2)
            throw new InvalidDataException("PLAN_SCHEMA_OR_BINDING_MISMATCH");
        JsonElement accounting = root.GetProperty("accounting");
        RequireKeys(accounting, "pilot_previously_examined", "no_safe_anchor", "selected_build", "selected_holdout");
        if (accounting.GetProperty("pilot_previously_examined").GetInt32() != 2 || accounting.GetProperty("no_safe_anchor").GetInt32() != 0 ||
            accounting.GetProperty("selected_build").GetInt32() != ExpectedBuildGroups || accounting.GetProperty("selected_holdout").GetInt32() != ExpectedHoldoutGroups)
            throw new InvalidDataException("PLAN_GROUP_ACCOUNTING_MISMATCH");
        string? claim = root.GetProperty("split_claim").GetString();
        if (claim is null || !claim.Contains("not official dev", StringComparison.Ordinal) || !claim.Contains("historical independence unverified", StringComparison.Ordinal))
            throw new InvalidDataException("PLAN_SPLIT_CLAIM_OVERSTATED");

        var sources = new List<PlanSource>();
        foreach (JsonElement row in root.GetProperty("source_rows").EnumerateArray())
        {
            RequireKeys(row, "source_index", "assigned_partition", "component_sha256", "case_id", "study_id", "project_sha256", "image_sha256",
                "image_width", "image_height", "curve_point_count", "anchor_source_point", "anchor_pixel");
            RequireKeys(row.GetProperty("anchor_source_point"), "x", "y");
            RequireKeys(row.GetProperty("anchor_pixel"), "x", "y");
            JsonElement anchor = row.GetProperty("anchor_source_point");
            JsonElement pixel = row.GetProperty("anchor_pixel");
            sources.Add(new PlanSource(
                row.GetProperty("source_index").GetInt32(),
                row.GetProperty("assigned_partition").GetString() ?? string.Empty,
                GetString(row, "component_sha256"), GetString(row, "case_id"), GetString(row, "study_id"),
                GetString(row, "project_sha256"), GetString(row, "image_sha256"),
                row.GetProperty("image_width").GetInt32(), row.GetProperty("image_height").GetInt32(),
                row.GetProperty("curve_point_count").GetInt32(),
                anchor.GetProperty("x").GetDouble(), anchor.GetProperty("y").GetDouble(),
                pixel.GetProperty("x").GetInt32(), pixel.GetProperty("y").GetInt32()));
        }
        string[] recipeIds = root.GetProperty("recipes").EnumerateArray().Select(item => item.GetProperty("id").GetString() ?? string.Empty).ToArray();
        if (!recipeIds.SequenceEqual(protocol.RecipeIds, StringComparer.Ordinal))
            throw new InvalidDataException("PLAN_RECIPE_IDENTITIES_MISMATCH");
        if (sources.Count != 20 || sources.Select(static source => source.SourceIndex).Where((value, index) => value != index).Any() ||
            sources.Count(static source => source.Partition == "internal_train_build") != ExpectedBuildGroups ||
            sources.Count(static source => source.Partition == "internal_train_holdout") != ExpectedHoldoutGroups ||
            sources.Any(source => !IsSha256(source.ComponentSha256) || !IsSha256(source.CaseId) || !IsSha256(source.StudyId) ||
                !IsSha256(source.ProjectSha256) || !IsSha256(source.ImageSha256) || source.ImageWidth < 160 || source.ImageHeight < 128 ||
                source.ImageWidth * (long)source.ImageHeight > 40_000_000 || source.CurvePointCount <= 0 ||
                !double.IsFinite(source.AnchorX) || !double.IsFinite(source.AnchorY) || source.AnchorX < 0 || source.AnchorY < 0 ||
                Math.Floor(source.AnchorX + 0.5) != source.AnchorPixelX || Math.Floor(source.AnchorY + 0.5) != source.AnchorPixelY ||
                source.AnchorPixelX < 64 || source.AnchorPixelY < 64 || source.AnchorPixelX >= source.ImageWidth - 64 || source.AnchorPixelY >= source.ImageHeight - 64))
            throw new InvalidDataException("PLAN_SOURCE_ROW_INVALID");
        if (sources.Select(static source => source.ComponentSha256).Distinct(StringComparer.Ordinal).Count() != sources.Count ||
            sources.Select(static source => source.ImageSha256).Distinct(StringComparer.Ordinal).Count() != sources.Count ||
            sources.Select(static source => source.CaseId).Distinct(StringComparer.Ordinal).Count() != sources.Count ||
            sources.Any(source => pilotComponents.Contains(source.ComponentSha256)))
            throw new InvalidDataException("PLAN_SOURCE_GROUPS_NOT_DISJOINT");
        return new Plan(sources);
    }

    private static void ValidateTrainManifest(byte[] bytes, Plan plan, HashSet<string> pilotComponents)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.GetProperty("schema").GetString() != ManifestSchema || root.GetProperty("status").GetString() != "completed_train_only_source_capture_unapproved" ||
            root.GetProperty("split").GetString() != "train")
            throw new InvalidDataException("TRAIN_SOURCE_MANIFEST_SCOPE_MISMATCH");
        JsonElement counts = root.GetProperty("counts");
        JsonElement scope = root.GetProperty("scope");
        JsonElement labeling = root.GetProperty("labeling");
        if (counts.GetProperty("train_projects").GetInt32() != ExpectedTrainProjects || counts.GetProperty("train_project_payloads_opened").GetInt32() != ExpectedTrainProjects ||
            counts.GetProperty("development_project_payloads_opened").GetInt32() != 0 || scope.GetProperty("development_payload_reads").GetInt32() != 0 ||
            scope.GetProperty("sealed_payload_reads").GetInt32() != 0 || scope.GetProperty("optimizer_steps").GetInt32() != 0 || scope.GetProperty("training_admitted").GetBoolean() ||
            labeling.GetProperty("scope").GetString() != "curve-point-centers-only; marker-glyph-correspondence-unverified" ||
            labeling.GetProperty("negative_labels_available").GetBoolean())
            throw new InvalidDataException("TRAIN_SOURCE_MANIFEST_LABEL_OR_SCOPE_MISMATCH");
        JsonElement[] rows = root.GetProperty("rows").EnumerateArray().ToArray();
        if (rows.Length != ExpectedTrainProjects || rows.Select(row => GetString(row, "caseId")).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw new InvalidDataException("TRAIN_SOURCE_MANIFEST_ROW_COUNT_MISMATCH");
        Dictionary<string, string> componentByCase = BuildComponentMap(rows);
        if (componentByCase.Values.Distinct(StringComparer.Ordinal).Count() != 22 ||
            componentByCase.Values.Intersect(pilotComponents, StringComparer.Ordinal).Count() != 2)
            throw new InvalidDataException("TRAIN_COMPONENT_OR_PILOT_GROUP_COUNT_MISMATCH");
        var byCase = rows.ToDictionary(row => GetString(row, "caseId"), StringComparer.Ordinal);
        foreach (PlanSource source in plan.Sources)
        {
            if (!byCase.TryGetValue(source.CaseId, out JsonElement row) || GetString(row, "studyId") != source.StudyId ||
                GetString(row, "projectSha256") != source.ProjectSha256 || GetString(row, "embeddedImageSha256") != source.ImageSha256 ||
                row.GetProperty("imageWidth").GetInt32() != source.ImageWidth || row.GetProperty("imageHeight").GetInt32() != source.ImageHeight ||
                row.GetProperty("curvePointCount").GetInt32() != source.CurvePointCount ||
                componentByCase[source.CaseId] != source.ComponentSha256 || pilotComponents.Contains(componentByCase[source.CaseId]) ||
                ComputePartition(componentByCase[source.CaseId]) != source.Partition ||
                row.GetProperty("curvePoints").GetArrayLength() != source.CurvePointCount ||
                !row.GetProperty("curvePoints").EnumerateArray().Any(point =>
                    Math.Floor(point.GetProperty("x").GetDouble() + 0.5) == source.AnchorPixelX &&
                    Math.Floor(point.GetProperty("y").GetDouble() + 0.5) == source.AnchorPixelY))
                throw new InvalidDataException("PLAN_AND_TRAIN_MANIFEST_IDENTITY_MISMATCH");
        }
    }

    private static Dictionary<string, string> BuildComponentMap(JsonElement[] rows)
    {
        int[] parents = Enumerable.Range(0, rows.Length).ToArray();
        int Find(int value)
        {
            while (parents[value] != value)
            {
                parents[value] = parents[parents[value]];
                value = parents[value];
            }
            return value;
        }
        void Union(int left, int right)
        {
            int leftRoot = Find(left);
            int rightRoot = Find(right);
            if (leftRoot != rightRoot) parents[Math.Max(leftRoot, rightRoot)] = Math.Min(leftRoot, rightRoot);
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < rows.Length; index++)
        {
            foreach (string key in new[] { "studyId", "embeddedImageSha256" })
            {
                string value = GetString(rows[index], key);
                if (seen.TryGetValue(key + ":" + value, out int prior)) Union(index, prior);
                else seen.Add(key + ":" + value, index);
            }
        }
        return rows.Select((row, index) => (row, root: Find(index)))
            .GroupBy(static item => item.root)
            .SelectMany(group =>
            {
                JsonElement[] members = group.Select(static item => item.row).ToArray();
                string signature = string.Join("\n", members.Select(row =>
                    $"{GetString(row, "studyId")}:{GetString(row, "embeddedImageSha256")}:{GetString(row, "caseId")}")
                    .Order(StringComparer.Ordinal));
                string component = HashBytes(Encoding.ASCII.GetBytes("graphreader.marker-train-overlay-pilot-v1\0" + signature));
                return members.Select(row => (Case: GetString(row, "caseId"), Component: component));
            })
            .ToDictionary(static item => item.Case, static item => item.Component, StringComparer.Ordinal);
    }

    private static string ComputePartition(string component)
    {
        byte[] digest = SHA256.HashData(Encoding.ASCII.GetBytes("graphreader.manual-train-overlay-corpus-v1\0" + component));
        return digest[0] < 64 ? "internal_train_holdout" : "internal_train_build";
    }

    private static HashSet<string> ValidatePilotPlan(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.GetProperty("schema").GetString() != "graphreader.manual-train-marker-overlay-pilot-plan.v1" ||
            root.GetProperty("source_capture_manifest_sha256").GetString() != ManifestSha256 || root.GetProperty("sources").GetArrayLength() != 2)
            throw new InvalidDataException("PILOT_EXCLUSION_PLAN_SCOPE_MISMATCH");
        HashSet<string> pilotComponents = root.GetProperty("sources").EnumerateArray()
            .Select(row => GetString(row, "component_sha256")).ToHashSet(StringComparer.Ordinal);
        if (pilotComponents.Count != 2) throw new InvalidDataException("PILOT_COMPONENT_IDENTITIES_NOT_UNIQUE");
        return pilotComponents;
    }

    private static ExtractedSource ExtractOne(FrozenRealCorpusProject project, PlanSource source, string destination, long remainingBytes)
    {
        string path = Path.GetFullPath(project.GetProjectPath());
        RejectReparseChain(path, allowMissingLeaf: false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, path);
        if (stream.Length is <= 0 or > EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes)
            throw new InvalidDataException("TRAIN_PROJECT_LENGTH_UNSUPPORTED");
        string before = HashStream(stream);
        if (before != source.ProjectSha256) throw new InvalidDataException("TRAIN_PROJECT_HASH_MISMATCH");
        stream.Position = 0;
        EngaugeDigWholeWorkflowTruth truth = EngaugeDigWholeWorkflowTruthAdapter.Read(
            stream, source.CaseId, () => EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, path), CancellationToken.None);
        byte[] png = truth.CopyImageBytes();
        stream.Position = 0;
        string after = HashStream(stream);
        if (before != after || truth.ProjectSha256 != source.ProjectSha256 || truth.SourceImageSha256 != source.ImageSha256 ||
            truth.SourceWidth != source.ImageWidth || truth.SourceHeight != source.ImageHeight)
            throw new InvalidDataException("TRAIN_PROJECT_OR_EMBEDDED_IMAGE_IDENTITY_MISMATCH");
        if (HashBytes(png) != source.ImageSha256) throw new InvalidDataException("EXTRACTED_PNG_BYTES_DO_NOT_MATCH_SOURCE_HASH");
        if (png.LongLength > remainingBytes) throw new InvalidDataException("SOURCE_EXTRACTION_BYTE_BUDGET_EXCEEDED");
        var points = truth.Curves.SelectMany(static curve => curve.Points).ToArray();
        if (points.Length != source.CurvePointCount || !points.Any(point => Math.Floor(point.ScreenX + 0.5) == source.AnchorPixelX && Math.Floor(point.ScreenY + 0.5) == source.AnchorPixelY))
            throw new InvalidDataException("TRAIN_ANCHOR_NOT_PRESENT_IN_AUTHENTICATED_CURVES");
        string filename = $"source-{source.SourceIndex:D3}.png";
        string outputPath = Path.Combine(destination, filename);
        RejectReparseChain(outputPath, allowMissingLeaf: true);
        using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
        {
            output.Write(png);
            output.Flush(true);
        }
        return new ExtractedSource(source.SourceIndex, source.Partition, source.ComponentSha256, source.CaseId, source.StudyId,
            source.ProjectSha256, source.ImageSha256, HashBytes(png), source.ImageWidth, source.ImageHeight, png.Length, filename, points.Length);
    }

    private static void VerifySourceBindings(Protocol protocol)
    {
        foreach ((string relative, string expected) in protocol.SourceFiles.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            RequireHash(ResolveExisting(relative), expected, "source binding");
    }

    private static RuntimeBinding VerifyRuntime(Protocol protocol)
    {
        Assembly executing = Assembly.GetExecutingAssembly();
        Assembly export = typeof(ExportMode).Assembly;
        string executingPath = Path.GetFullPath(executing.Location);
        string exportPath = Path.GetFullPath(export.Location);
        string expectedDirectory = Path.GetFullPath(Path.Combine(Root, "ml", "markers", "center", "manual_train_overlay_corpus_v1", "extractor", "bin", "Release", "net10.0-windows"));
        if (!string.Equals(Path.GetDirectoryName(executingPath), expectedDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(exportPath), expectedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EXECUTING_RUNTIME_PATH_MISMATCH");
        RejectReparseChain(executingPath, allowMissingLeaf: false);
        RejectReparseChain(exportPath, allowMissingLeaf: false);
        string extractorSha = HashFile(executingPath);
        string exportSha = HashFile(exportPath);
        if (protocol.RuntimeAssemblies["GraphReader.MarkerTrainOverlayCorpus"] != extractorSha || protocol.RuntimeAssemblies["GraphReader.Export"] != exportSha)
            throw new InvalidDataException("RUNTIME_ASSEMBLY_HASH_MISMATCH");
        return new RuntimeBinding(exportSha, extractorSha);
    }

    private static void WriteNewJson<T>(string path, T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static void AppendAttempt(string path, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.WriteByte((byte)'\n');
        stream.Flush(true);
    }

    private static byte[] ReadPinnedFile(string relative, string expectedSha256)
    {
        string path = ResolveExisting(relative);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 256L * 1024 * 1024) throw new InvalidDataException("BOUND_INPUT_LENGTH_UNSUPPORTED");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (!string.Equals(HashBytes(bytes), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("BOUND_INPUT_HASH_MISMATCH");
        return bytes;
    }

    private static string ResolveExisting(string relative, bool directory = false)
    {
        if (!IsRelativeProjectPath(relative)) throw new InvalidDataException("PROJECT_PATH_NOT_RELATIVE");
        string full = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        EnsureUnderRoot(full);
        RejectReparseChain(full, allowMissingLeaf: false);
        if (directory ? !Directory.Exists(full) : !File.Exists(full)) throw new FileNotFoundException("BOUND_PROJECT_PATH_MISSING");
        return full;
    }

    private static string ResolveNewRunDirectory(string relative)
    {
        if (!IsRelativeProjectPath(relative) || !relative.StartsWith("artifacts/goal22-runs/", StringComparison.Ordinal))
            throw new InvalidDataException("OUTPUT_SCOPE_REJECTED");
        string full = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        EnsureUnderRoot(full);
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("OUTPUT_ALREADY_EXISTS");
        RejectReparseChain(full, allowMissingLeaf: true);
        return full;
    }

    private static void RejectReparseChain(string target, bool allowMissingLeaf)
    {
        string full = Path.GetFullPath(target);
        EnsureUnderRoot(full);
        string current = Path.GetPathRoot(full) ?? throw new InvalidDataException("PATH_VOLUME_MISSING");
        string[] parts = full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            bool exists = File.Exists(current) || Directory.Exists(current);
            if (!exists)
            {
                if (allowMissingLeaf && index == parts.Length - 1) continue;
                throw new FileNotFoundException("PATH_COMPONENT_MISSING");
            }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("PATH_REPARSE_POINT_REJECTED");
        }
    }

    private static void EnsureUnderRoot(string full)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PATH_OUTSIDE_E_PROJECT");
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string HashStream(Stream stream)
    {
        stream.Position = 0;
        string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        stream.Position = 0;
        return hash;
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void RequireHash(string path, string expected, string label)
    {
        if (!IsSha256(expected) || !string.Equals(HashFile(path), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} hash differs.");
    }

    private static string GetString(JsonElement element, string property) =>
        element.GetProperty(property).GetString() ?? throw new InvalidDataException("JSON_STRING_FIELD_MISSING");

    private static string SafeFailureCode(string message) => message.Length is > 0 and <= 96 &&
        message.All(static character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')
            ? message
            : string.Empty;

    private static SortedDictionary<string, string> ReadHashMap(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("PROTOCOL_HASH_MAP_INVALID");
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string hash = property.Value.GetString() ?? string.Empty;
            if (!IsSha256(hash) || !values.TryAdd(property.Name, hash)) throw new InvalidDataException("PROTOCOL_HASH_MAP_INVALID");
        }
        return values;
    }

    private static void RequireKeys(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("JSON_FIELDS_DIFFER_FROM_FROZEN_SCHEMA");
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static bool IsRelativeProjectPath(string value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Contains(':') &&
        !value.Split('/', '\\').Any(static part => part is "" or "." or "..");

    private sealed record Protocol(string Sha256, SortedDictionary<string, string> SourceFiles, SortedDictionary<string, string> RuntimeAssemblies, IReadOnlyList<string> RecipeIds);
    private sealed record Plan(IReadOnlyList<PlanSource> Sources);
    private sealed record PlanSource(int SourceIndex, string Partition, string ComponentSha256, string CaseId, string StudyId,
        string ProjectSha256, string ImageSha256, int ImageWidth, int ImageHeight, int CurvePointCount,
        double AnchorX, double AnchorY, int AnchorPixelX, int AnchorPixelY);
    private sealed record RuntimeBinding(
        [property: JsonPropertyName("ExportAssemblySha256")] string ExportAssemblySha256,
        [property: JsonPropertyName("ExtractorAssemblySha256")] string ExtractorAssemblySha256);
    private sealed record ExtractedSource(int SourceIndex, string AssignedPartition, string ComponentSha256, string CaseId, string StudyId,
        string ProjectSha256, string ImageSha256, string ExtractedPngSha256, int Width, int Height, int ByteLength, string Path, int CurvePointCount);
    private sealed record FailedSource(int SourceIndex, string ErrorType);
    private sealed record ExtractionReceipt(string Schema, string Status, string PlanSha256, string ProtocolSha256, string SourceManifestSha256,
        string PilotPlanSha256, string CaptureAttemptSha256, string AssignmentSha256, string SelectedInventorySha256,
        int SelectedSources, int ExtractedSources, int FailedSources, int CaptureAttemptEventCount,
        long ExtractedImageBytes, int DevelopmentPayloadReads, int SealedPayloadReads, int OptimizerSteps, bool TrainingAdmitted,
        RuntimeBinding Runtime, IReadOnlyList<ExtractedSource> Sources, IReadOnlyList<FailedSource> Failures);
}
