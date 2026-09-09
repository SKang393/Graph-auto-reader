// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record FrozenRealWorkflowCandidateIdentity(
    string Revision,
    string CandidateId,
    string CandidateSha256,
    string ExecutionDescriptorSha256,
    string OperatingPointIdentity,
    string ProtocolRelativePath,
    string ProtocolSha256,
    string OcrDetectionSha256,
    string OcrRecognitionSha256,
    string MarkerCenterSha256,
    string MarkerClassifierSha256);

internal sealed record FrozenRealWorkflowAdmissionResult(
    string ProtocolPath,
    string ProtocolSha256,
    string EvidencePolicySha256,
    string AcceptanceBarsSha256,
    FrozenRealWorkflowCandidateIdentity Candidate,
    string ExecutionDescriptorSha256,
    string OperatingPointIdentity,
    string Split,
    int ProjectCount,
    string AssignmentSha256,
    string SelectedInventorySha256,
    WholeWorkflowEvaluationOptions EvaluationOptions,
    double PrecisionMinimum,
    double RecallMinimum,
    IReadOnlyList<string> PrerequisiteEvidenceSha256,
    bool AggregateOnly,
    bool SealedFirstReadRequired);

/// <summary>
/// Authenticates an explicit aggregate-only real workflow protocol. This class
/// reads protocol and prerequisite evidence only. It never opens corpus data,
/// constructs a model, or grants production approval.
/// </summary>
internal static class FrozenRealWorkflowAdmission
{
    private sealed record CanonicalBars(
        double TextPrecisionMinimum,
        double TextRecallMinimum,
        double RecognitionMinimum,
        double CharacterErrorMaximum,
        double RoleMinimum,
        double ProhibitedStructureMaximum,
        double MarkerPrecisionMinimum,
        double MarkerRecallMinimum);

    internal const string PrerequisiteSchema =
        "graphreader.frozen-stage-prerequisite-aggregate.v1";
    private const string EvidencePolicyPath = "ml/policy/evidence-policy.json";
    private const string AcceptanceBarsPath = "ml/policy/acceptance-bars.json";
    private const int MaximumJsonBytes = 16 * 1024 * 1024;
    private const double NumericTolerance = 1e-12;
    private const string OperatingPointDomain =
        "graphreader.frozen-real-workflow-operating-point.v1";

    internal static FrozenRealWorkflowAdmissionResult Load(
        string repositoryRoot,
        string protocolPath,
        string expectedProtocolSha256,
        FrozenCandidateBinding candidate,
        FrozenRealCorpusSelection inventory,
        string requestedSplit,
        bool explicitOptIn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return LoadCore(
            repositoryRoot,
            protocolPath,
            expectedProtocolSha256,
            CandidateIdentity(candidate),
            inventory,
            requestedSplit,
            explicitOptIn,
            IsContinuousIntegration(),
            cancellationToken);
    }

    internal static FrozenRealWorkflowAdmissionResult LoadForTest(
        string repositoryRoot,
        string protocolPath,
        string expectedProtocolSha256,
        FrozenRealWorkflowCandidateIdentity candidate,
        FrozenRealCorpusSelection inventory,
        string requestedSplit,
        bool explicitOptIn,
        bool continuousIntegration,
        CancellationToken cancellationToken) =>
        LoadCore(repositoryRoot, protocolPath, expectedProtocolSha256, candidate,
            inventory, requestedSplit, explicitOptIn, continuousIntegration,
            cancellationToken);

    internal static void ValidatePrerequisiteForTest(
        string repositoryRoot,
        string evidencePath,
        string evidenceSha256,
        string task,
        string split,
        FrozenRealWorkflowCandidateIdentity candidate,
        string policySha256,
        string acceptanceBarsSha256,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(repositoryRoot);
        byte[] evidence = ReadReferenced(root, evidencePath, evidenceSha256, cancellationToken);
        byte[] barsBytes = ReadReferenced(
            root, AcceptanceBarsPath, acceptanceBarsSha256, cancellationToken);
        ValidatePrerequisite(
            evidence, root, task, split, candidate, policySha256,
            acceptanceBarsSha256, ValidateAcceptanceBars(barsBytes), cancellationToken);
    }

    private static FrozenRealWorkflowAdmissionResult LoadCore(
        string repositoryRoot,
        string protocolPath,
        string expectedProtocolSha256,
        FrozenRealWorkflowCandidateIdentity candidate,
        FrozenRealCorpusSelection inventory,
        string requestedSplit,
        bool explicitOptIn,
        bool continuousIntegration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(inventory);
        cancellationToken.ThrowIfCancellationRequested();
        if (!explicitOptIn || continuousIntegration)
        {
            throw new InvalidOperationException("REAL_WORKFLOW_EXPLICIT_LOCAL_OPT_IN_REQUIRED");
        }
        if (requestedSplit is not (FrozenRealCorpusInventory.RealDev or FrozenRealCorpusInventory.RealSealed) ||
            inventory.SelectedSplit != requestedSplit)
        {
            throw new InvalidDataException("REAL_WORKFLOW_SPLIT_IDENTITY_MISMATCH");
        }

        string root = Path.GetFullPath(repositoryRoot);
        string fullProtocolPath = FrozenCandidateBinding.RequireUnderRoot(root, protocolPath, "real workflow protocol");
        string expectedSha256 = FrozenCandidateBinding.RequireSha256(
            expectedProtocolSha256, nameof(expectedProtocolSha256));
        byte[] protocolBytes = ReadBounded(fullProtocolPath, cancellationToken);
        if (FrozenCandidateBinding.Hash(protocolBytes) != expectedSha256 ||
            candidate.ProtocolSha256 != expectedSha256 ||
            !candidate.ProtocolRelativePath.Equals(
                Path.GetRelativePath(root, fullProtocolPath).Replace('\\', '/'),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("REAL_WORKFLOW_PROTOCOL_BINDING_MISMATCH");
        }

        using JsonDocument protocol = ParseExact(protocolBytes);
        JsonElement document = protocol.RootElement;
        RequireProperties(document,
        [
            "evidence_policy", "hypothesis", "isolated_change", "split_identities",
            "metric", "acceptance_bar", "budget",
        ], "protocol");
        RequireText(document, "hypothesis", "protocol");
        RequireText(document, "isolated_change", "protocol");

        (string policyPath, string policySha) = ReadReference(
            document.GetProperty("evidence_policy"), "evidence policy");
        (string barsPath, string barsSha) = ReadReference(
            document.GetProperty("acceptance_bar"), "acceptance bar");
        if (policyPath != EvidencePolicyPath || barsPath != AcceptanceBarsPath)
        {
            throw new InvalidDataException("REAL_WORKFLOW_CANONICAL_POLICY_PATH_MISMATCH");
        }
        byte[] policyBytes = ReadReferenced(root, policyPath, policySha, cancellationToken);
        byte[] barsBytes = ReadReferenced(root, barsPath, barsSha, cancellationToken);
        ValidateSharedPolicy(policyBytes, requestedSplit);
        CanonicalBars bars = ValidateAcceptanceBars(barsBytes);

        JsonElement identities = document.GetProperty("split_identities");
        RequireProperties(identities,
        [
            "split", "assignment_sha256", "selected_inventory_sha256", "project_count",
            "candidate_revision", "candidate_id", "execution_descriptor_sha256",
            "operating_point_identity", "ocr_detection_sha256",
            "ocr_recognition_sha256", "marker_center_sha256", "marker_classifier_sha256",
            "prerequisites",
        ], "split identities");
        if (Text(identities, "split") != requestedSplit ||
            Sha(identities, "assignment_sha256") != inventory.AssignmentSha256 ||
            Sha(identities, "selected_inventory_sha256") != inventory.SelectedInventorySha256 ||
            Integer(identities, "project_count") != inventory.SelectedProjects.Count ||
            Text(identities, "candidate_revision") != candidate.Revision ||
            Text(identities, "candidate_id") != candidate.CandidateId ||
            Sha(identities, "execution_descriptor_sha256") != candidate.ExecutionDescriptorSha256 ||
            Sha(identities, "operating_point_identity") != candidate.OperatingPointIdentity ||
            Sha(identities, "ocr_detection_sha256") != candidate.OcrDetectionSha256 ||
            Sha(identities, "ocr_recognition_sha256") != candidate.OcrRecognitionSha256 ||
            Sha(identities, "marker_center_sha256") != candidate.MarkerCenterSha256 ||
            Sha(identities, "marker_classifier_sha256") != candidate.MarkerClassifierSha256)
        {
            throw new InvalidDataException("REAL_WORKFLOW_PROTOCOL_IDENTITY_MISMATCH");
        }
        ValidateInventoryCounts(inventory, requestedSplit);

        WholeWorkflowEvaluationOptions options = ReadMetric(document.GetProperty("metric"));
        ValidateBudget(document.GetProperty("budget"), requestedSplit);
        IReadOnlyList<string> evidence = ValidatePrerequisites(
            root,
            identities.GetProperty("prerequisites"),
            requestedSplit,
            candidate,
            policySha,
            barsSha,
            bars,
            cancellationToken);
        return new FrozenRealWorkflowAdmissionResult(
            Path.GetRelativePath(root, fullProtocolPath).Replace('\\', '/'),
            expectedSha256,
            policySha,
            barsSha,
            candidate,
            candidate.ExecutionDescriptorSha256,
            candidate.OperatingPointIdentity,
            requestedSplit,
            inventory.SelectedProjects.Count,
            inventory.AssignmentSha256,
            inventory.SelectedInventorySha256,
            options,
            bars.MarkerPrecisionMinimum,
            bars.MarkerRecallMinimum,
            evidence,
            AggregateOnly: true,
            SealedFirstReadRequired: requestedSplit == FrozenRealCorpusInventory.RealSealed);
    }

    private static IReadOnlyList<string> ValidatePrerequisites(
        string root,
        JsonElement value,
        string requestedSplit,
        FrozenRealWorkflowCandidateIdentity candidate,
        string policySha,
        string barsSha,
        CanonicalBars bars,
        CancellationToken cancellationToken)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITES_INVALID");
        }
        JsonElement[] references = value.EnumerateArray().ToArray();
        if (requestedSplit == FrozenRealCorpusInventory.RealDev)
        {
            if (references.Length != 0)
            {
                throw new InvalidDataException("REAL_DEV_CANNOT_CONSUME_SEALED_PREREQUISITES");
            }
            return Array.Empty<string>();
        }
        if (references.Length != 4)
        {
            throw new InvalidDataException("REAL_SEALED_REQUIRES_FOUR_STAGE_PREREQUISITES");
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new List<string>(references.Length);
        foreach (JsonElement reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireProperties(reference, ["task", "split", "path", "sha256"], "prerequisite reference");
            string task = Text(reference, "task");
            string split = Text(reference, "split");
            string role = task + "/" + split;
            if (!roles.Add(role) || task is not ("ocr-detection-recognition" or "marker-center") ||
                split is not ("synthetic-dev" or "synthetic-sealed"))
            {
                throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_ROLE_INVALID");
            }
            string path = Text(reference, "path");
            string sha = Sha(reference, "sha256");
            byte[] bytes = ReadReferenced(root, path, sha, cancellationToken);
            ValidatePrerequisite(bytes, root, task, split, candidate, policySha, barsSha,
                bars, cancellationToken);
            hashes.Add(sha);
        }
        string[] expectedRoles =
        [
            "marker-center/synthetic-dev", "marker-center/synthetic-sealed",
            "ocr-detection-recognition/synthetic-dev",
            "ocr-detection-recognition/synthetic-sealed",
        ];
        if (!roles.SetEquals(expectedRoles))
        {
            throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_COVERAGE_INCOMPLETE");
        }
        return new ReadOnlyCollection<string>(hashes.Order(StringComparer.Ordinal).ToArray());
    }

    private static void ValidatePrerequisite(
        byte[] bytes,
        string root,
        string expectedTask,
        string expectedSplit,
        FrozenRealWorkflowCandidateIdentity candidate,
        string policySha,
        string barsSha,
        CanonicalBars bars,
        CancellationToken cancellationToken)
    {
        using JsonDocument evidence = ParseExact(bytes);
        JsonElement value = evidence.RootElement;
        RequireProperties(value,
        [
            "schema", "task", "split", "stage_revision", "stage_candidate_id",
            "evidence_policy_sha256", "acceptance_bar_sha256", "runtime_composition_sha256",
            "operating_point_identity", "models", "source_result", "benchmarks", "parity",
            "aggregate_only", "case_level_output", "truth_rows_output", "prediction_output",
            "pixel_output",
        ], "prerequisite evidence");
        if (Text(value, "schema") != PrerequisiteSchema || Text(value, "task") != expectedTask ||
            Text(value, "split") != expectedSplit || Sha(value, "evidence_policy_sha256") != policySha ||
            Sha(value, "acceptance_bar_sha256") != barsSha ||
            !Boolean(value, "aggregate_only") || Boolean(value, "case_level_output") ||
            Boolean(value, "truth_rows_output") || Boolean(value, "prediction_output") ||
            Boolean(value, "pixel_output"))
        {
            throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_SCOPE_INVALID");
        }
        RequireText(value, "stage_revision", "prerequisite evidence");
        RequireText(value, "stage_candidate_id", "prerequisite evidence");
        if (Sha(value, "runtime_composition_sha256") != candidate.ExecutionDescriptorSha256 ||
            Sha(value, "operating_point_identity") != candidate.OperatingPointIdentity)
        {
            throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_EXECUTION_IDENTITY_MISMATCH");
        }
        JsonElement models = value.GetProperty("models");
        RequireProperties(models,
            ["ocr_detection_sha256", "ocr_recognition_sha256", "marker_center_sha256"],
            "prerequisite models");
        if (NullableSha(models, "ocr_detection_sha256") !=
                (expectedTask == "ocr-detection-recognition" ? candidate.OcrDetectionSha256 : null) ||
            NullableSha(models, "ocr_recognition_sha256") !=
                (expectedTask == "ocr-detection-recognition" ? candidate.OcrRecognitionSha256 : null) ||
            NullableSha(models, "marker_center_sha256") !=
                (expectedTask == "marker-center" ? candidate.MarkerCenterSha256 : null))
        {
            throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_MODEL_MISMATCH");
        }
        JsonElement source = value.GetProperty("source_result");
        (string sourcePath, string sourceSha) = ReadReference(source, "prerequisite source result");
        byte[] sourceBytes = ReadReferenced(root, sourcePath, sourceSha, cancellationToken);
        JsonElement parity = value.GetProperty("parity");
        JsonElement benchmarks = value.GetProperty("benchmarks");
        ValidateParity(parity);
        ValidateBenchmarks(benchmarks, expectedTask, expectedSplit, bars);
        ValidateSourceResult(
            sourceBytes, root, expectedTask, expectedSplit, candidate, value,
            benchmarks, parity, cancellationToken);
    }

    private static void ValidateSourceResult(
        byte[] sourceBytes,
        string root,
        string task,
        string split,
        FrozenRealWorkflowCandidateIdentity candidate,
        JsonElement envelope,
        JsonElement envelopeBenchmarks,
        JsonElement envelopeParity,
        CancellationToken cancellationToken)
    {
        if (task != "marker-center" || split != "synthetic-dev")
        {
            throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_SOURCE_SCHEMA_UNSUPPORTED");
        }

        using JsonDocument sourceDocument = ParseExact(sourceBytes);
        JsonElement source = sourceDocument.RootElement;
        string schema = Text(source, "schema");
        if (schema is not ("graphreader.marker-center-mask-preserving-v24-candidate.v1" or
                           "graphreader.marker-center-plot-domain-v25-candidate.v1"))
        {
            throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_SOURCE_SCHEMA_UNSUPPORTED");
        }
        if (Text(source, "task") != task ||
            Text(source, "revision") != Text(envelope, "stage_revision") ||
            Text(source, "candidate_id") != Text(envelope, "stage_candidate_id") ||
            Text(source, "status") != "dev_passed" ||
            Sha(source, "onnx_sha256") != candidate.MarkerCenterSha256 ||
            !Boolean(source, "dev_gate_passed") ||
            !Boolean(source, "synthetic_only") || Boolean(source, "private_data") ||
            Integer(source, "real_dev_reads") != 0 || Integer(source, "real_sealed_reads") != 0 ||
            Integer(source, "sealed_runs") != 0 ||
            Text(source, "onnx_provider") != "CPUExecutionProvider" ||
            Boolean(source, "production_approval") || Boolean(source, "release_eligible"))
        {
            throw new InvalidDataException("REAL_WORKFLOW_MARKER_DEV_SOURCE_SCOPE_INVALID");
        }

        (string configPath, string configSha) = (
            Text(source, "candidate_config_path"), Sha(source, "candidate_config_sha256"));
        byte[] configBytes = ReadReferenced(root, configPath, configSha, cancellationToken);
        using JsonDocument configDocument = ParseExact(configBytes);
        JsonElement config = configDocument.RootElement;
        if (Number(config, "confidence_threshold") != 0.25 ||
            Text(config, "provider") != "CPUExecutionProvider")
        {
            throw new InvalidDataException("REAL_WORKFLOW_MARKER_DEV_OPERATING_POINT_INVALID");
        }
        double parityTolerance = Number(config, "onnx_parity_tolerance");
        double parityError = Number(source, "onnx_parity_maximum_absolute_error");
        int[] configuredCounts = config.GetProperty("onnx_dynamic_candidate_counts")
            .EnumerateArray().Select(static item => item.GetInt32()).ToArray();
        JsonElement[] parityRows = source.GetProperty("onnx_dynamic_candidate_counts")
            .EnumerateArray().ToArray();
        if (configuredCounts.Length == 0 || parityRows.Length != configuredCounts.Length)
        {
            throw new InvalidDataException("REAL_WORKFLOW_MARKER_DEV_PARITY_MISMATCH");
        }
        double recomputedParityMaximum = 0;
        for (int index = 0; index < parityRows.Length; index++)
        {
            if (Integer(parityRows[index], "candidate_count") != configuredCounts[index])
            {
                throw new InvalidDataException("REAL_WORKFLOW_MARKER_DEV_PARITY_MISMATCH");
            }
            double rowError = Number(parityRows[index], "maximum_absolute_error");
            if (rowError < 0)
            {
                throw new InvalidDataException("REAL_WORKFLOW_MARKER_DEV_PARITY_MISMATCH");
            }
            recomputedParityMaximum = Math.Max(recomputedParityMaximum, rowError);
        }
        if (parityTolerance < 0 || parityError < 0 || parityError > parityTolerance ||
            Math.Abs(parityError - recomputedParityMaximum) > NumericTolerance ||
            !Boolean(envelopeParity, "passed") ||
            Math.Abs(Number(envelopeParity, "max_absolute_error") - parityError) > NumericTolerance ||
            Math.Abs(Number(envelopeParity, "tolerance") - parityTolerance) > NumericTolerance)
        {
            throw new InvalidDataException("REAL_WORKFLOW_MARKER_DEV_PARITY_MISMATCH");
        }

        JsonElement component;
        JsonElement family;
        if (schema == "graphreader.marker-center-mask-preserving-v24-candidate.v1")
        {
            if (!Boolean(source, "family_dev_gate_passed"))
            {
                throw new InvalidDataException("REAL_WORKFLOW_MARKER_DEV_SOURCE_GATE_FAILED");
            }
            component = source.GetProperty("selected");
            family = source.GetProperty("family_selected");
        }
        else
        {
            JsonElement[] componentRows = source.GetProperty("component_dev_comparisons")
                .EnumerateArray().ToArray();
            JsonElement[] familyRows = source.GetProperty("family_dev_comparisons")
                .EnumerateArray().ToArray();
            component = FindUniqueThreshold(componentRows, 0.25);
            family = FindUniqueThreshold(familyRows, 0.25);
        }
        ValidateSourceBenchmark(component, FindBenchmark(envelopeBenchmarks, "component"));
        ValidateSourceBenchmark(family, FindBenchmark(envelopeBenchmarks, "full-source-family"));
    }

    private static JsonElement FindUniqueThreshold(JsonElement[] rows, double threshold)
    {
        JsonElement[] matches = rows
            .Where(row => Math.Abs(Number(row, "threshold") - threshold) <= NumericTolerance)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("REAL_WORKFLOW_MARKER_DEV_THRESHOLD_ROW_INVALID");
        }
        return matches[0];
    }

    private static JsonElement FindBenchmark(JsonElement benchmarks, string name)
    {
        foreach (JsonElement row in benchmarks.EnumerateArray())
        {
            if (Text(row, "name") == name)
            {
                return row;
            }
        }
        throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_BENCHMARK_ROLE_INVALID");
    }

    private static void ValidateSourceBenchmark(JsonElement source, JsonElement envelope)
    {
        if (Number(source, "threshold") != 0.25)
        {
            throw new InvalidDataException("REAL_WORKFLOW_MARKER_DEV_THRESHOLD_MISMATCH");
        }
        int tp = Integer(source, "true_positives");
        int fp = Integer(source, "false_positives");
        int fn = Integer(source, "false_negatives");
        if (Integer(envelope, "truth_count") != checked(tp + fn) ||
            Integer(envelope, "true_positive") != tp ||
            Integer(envelope, "false_positive") != fp ||
            Integer(envelope, "false_negative") != fn ||
            Math.Abs(Number(envelope, "precision") - Number(source, "precision")) > NumericTolerance ||
            Math.Abs(Number(envelope, "recall") - Number(source, "recall")) > NumericTolerance ||
            Math.Abs(Number(envelope, "prohibited_structure_hit_rate") -
                     Number(source, "prohibited_structure_hit_rate")) > NumericTolerance)
        {
            throw new InvalidDataException("REAL_WORKFLOW_MARKER_DEV_METRIC_MISMATCH");
        }
    }

    private static void ValidateBenchmarks(
        JsonElement value,
        string task,
        string split,
        CanonicalBars bars)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_BENCHMARKS_INVALID");
        }
        JsonElement[] rows = value.EnumerateArray().ToArray();
        string[] expectedNames = task switch
        {
            "marker-center" when split == "synthetic-dev" => ["component", "full-source-family"],
            "marker-center" when split == "synthetic-sealed" => ["full-graph"],
            _ => ["full-source"],
        };
        if (rows.Length != expectedNames.Length)
        {
            throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_BENCHMARK_COVERAGE_INVALID");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in rows)
        {
            RequireProperties(row,
            [
                "name", "truth_count", "true_positive", "false_positive", "false_negative",
                "precision", "recall", "recognition_exact_match", "character_error_rate",
                "role_accuracy", "prohibited_structure_hit_rate",
            ], "prerequisite benchmark");
            string name = Text(row, "name");
            if (!names.Add(name))
            {
                throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_BENCHMARK_DUPLICATE");
            }
            int truth = Integer(row, "truth_count");
            int tp = Integer(row, "true_positive");
            int fp = Integer(row, "false_positive");
            int fn = Integer(row, "false_negative");
            if (truth <= 0 || tp < 0 || fp < 0 || fn < 0 || tp + fn != truth)
            {
                throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_COUNTS_INVALID");
            }
            double precision = Number(row, "precision");
            double recall = Number(row, "recall");
            double prohibited = RequiredNullableNumber(row, "prohibited_structure_hit_rate");
            double expectedPrecision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
            double expectedRecall = (double)tp / truth;
            double precisionMinimum = task == "marker-center"
                ? bars.MarkerPrecisionMinimum : bars.TextPrecisionMinimum;
            double recallMinimum = task == "marker-center"
                ? bars.MarkerRecallMinimum : bars.TextRecallMinimum;
            if (Math.Abs(precision - expectedPrecision) > NumericTolerance ||
                Math.Abs(recall - expectedRecall) > NumericTolerance ||
                precision < precisionMinimum || recall < recallMinimum)
            {
                throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_GATE_FAILED");
            }
            if (task == "ocr-detection-recognition")
            {
                double recognition = RequiredNullableNumber(row, "recognition_exact_match");
                double characterError = RequiredNullableNumber(row, "character_error_rate");
                double role = RequiredNullableNumber(row, "role_accuracy");
                if (recognition < bars.RecognitionMinimum ||
                    characterError > bars.CharacterErrorMaximum ||
                    role < bars.RoleMinimum ||
                    prohibited > bars.ProhibitedStructureMaximum)
                {
                    throw new InvalidDataException("REAL_WORKFLOW_OCR_PREREQUISITE_GATE_FAILED");
                }
            }
            else if (row.GetProperty("recognition_exact_match").ValueKind != JsonValueKind.Null ||
                     row.GetProperty("character_error_rate").ValueKind != JsonValueKind.Null ||
                     row.GetProperty("role_accuracy").ValueKind != JsonValueKind.Null)
            {
                throw new InvalidDataException("REAL_WORKFLOW_MARKER_PREREQUISITE_FIELDS_INVALID");
            }
            else if (prohibited > bars.ProhibitedStructureMaximum)
            {
                throw new InvalidDataException("REAL_WORKFLOW_MARKER_PREREQUISITE_GATE_FAILED");
            }
        }
        if (!names.SetEquals(expectedNames))
        {
            throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_BENCHMARK_ROLE_INVALID");
        }
    }

    private static void ValidateParity(JsonElement value)
    {
        RequireProperties(value, ["passed", "max_absolute_error", "tolerance"], "prerequisite parity");
        double error = Number(value, "max_absolute_error");
        double tolerance = Number(value, "tolerance");
        if (!Boolean(value, "passed") || error < 0 || tolerance < 0 || error > tolerance)
        {
            throw new InvalidDataException("REAL_WORKFLOW_PREREQUISITE_PARITY_FAILED");
        }
    }

    private static WholeWorkflowEvaluationOptions ReadMetric(JsonElement value)
    {
        RequireProperties(value,
        [
            "name", "source_pixel_match_tolerance", "graph_x_absolute_tolerance",
            "graph_y_absolute_tolerance", "integer_session_x", "full_project_denominator",
            "full_point_denominator", "series_boundaries", "aggregate_only",
            "require_in_memory_artifacts",
        ], "metric");
        if (Text(value, "name") != "whole-workflow-csv-values" ||
            Number(value, "source_pixel_match_tolerance") != 5 ||
            Number(value, "graph_x_absolute_tolerance") != 0.5 ||
            Number(value, "graph_y_absolute_tolerance") != 5 ||
            !Boolean(value, "integer_session_x") || !Boolean(value, "full_project_denominator") ||
            !Boolean(value, "full_point_denominator") || !Boolean(value, "series_boundaries") ||
            !Boolean(value, "aggregate_only") || !Boolean(value, "require_in_memory_artifacts"))
        {
            throw new InvalidDataException("REAL_WORKFLOW_METRIC_CONTRACT_INVALID");
        }
        return new WholeWorkflowEvaluationOptions(5, 0.5, 5, RequireInMemoryArtifacts: true);
    }

    private static void ValidateBudget(JsonElement value, string split)
    {
        RequireProperties(value,
        [
            "optimizer_steps", "training_use", "candidate_selection", "production_approval",
            "aggregate_only", "sealed_reads_per_candidate",
        ], "budget");
        int expectedReads = split == FrozenRealCorpusInventory.RealSealed ? 1 : 0;
        if (Integer(value, "optimizer_steps") != 0 || Boolean(value, "training_use") ||
            Boolean(value, "candidate_selection") || Boolean(value, "production_approval") ||
            !Boolean(value, "aggregate_only") || Integer(value, "sealed_reads_per_candidate") != expectedReads)
        {
            throw new InvalidDataException("REAL_WORKFLOW_BUDGET_INVALID");
        }
    }

    private static void ValidateInventoryCounts(FrozenRealCorpusSelection inventory, string split)
    {
        int expected = split == FrozenRealCorpusInventory.RealDev
            ? FrozenRealCorpusInventory.ExpectedRealDevCount
            : FrozenRealCorpusInventory.ExpectedRealSealedCount;
        if (inventory.ProjectCount != FrozenRealCorpusInventory.ExpectedProjectCount ||
            inventory.RealDevCount != FrozenRealCorpusInventory.ExpectedRealDevCount ||
            inventory.RealSealedCount != FrozenRealCorpusInventory.ExpectedRealSealedCount ||
            inventory.SelectedProjects.Count != expected)
        {
            throw new InvalidDataException("REAL_WORKFLOW_CORPUS_COUNT_MISMATCH");
        }
    }

    private static void ValidateSharedPolicy(byte[] bytes, string split)
    {
        using JsonDocument document = ParseExact(bytes);
        JsonElement splits = document.RootElement.GetProperty("splits");
        JsonElement selected = splits.GetProperty(split == FrozenRealCorpusInventory.RealDev ? "dev" : "sealed");
        bool consumes = selected.GetProperty("consumes_candidate_budget").GetBoolean();
        if (selected.GetProperty("training_permitted").GetBoolean() ||
            consumes != (split == FrozenRealCorpusInventory.RealSealed) ||
            (split == FrozenRealCorpusInventory.RealSealed &&
             selected.GetProperty("case_level_inspection").GetString() != "aggregate_only_until_retired"))
        {
            throw new InvalidDataException("REAL_WORKFLOW_SHARED_POLICY_INVALID");
        }
    }

    private static CanonicalBars ValidateAcceptanceBars(byte[] bytes)
    {
        using JsonDocument document = ParseExact(bytes);
        JsonElement tier = document.RootElement.GetProperty("tier1_reviewable_error");
        var bars = new CanonicalBars(
            tier.GetProperty("text_region_detection_precision_minimum").GetDouble(),
            tier.GetProperty("text_region_detection_recall_minimum").GetDouble(),
            tier.GetProperty("recognition_exact_match_minimum").GetDouble(),
            tier.GetProperty("character_error_rate_maximum").GetDouble(),
            tier.GetProperty("role_accuracy_minimum").GetDouble(),
            tier.GetProperty("prohibited_structure_hit_rate_maximum").GetDouble(),
            tier.GetProperty("marker_center_precision_minimum").GetDouble(),
            tier.GetProperty("marker_center_recall_minimum").GetDouble());
        double[] values =
        [
            bars.TextPrecisionMinimum, bars.TextRecallMinimum, bars.RecognitionMinimum,
            bars.CharacterErrorMaximum, bars.RoleMinimum, bars.ProhibitedStructureMaximum,
            bars.MarkerPrecisionMinimum, bars.MarkerRecallMinimum,
        ];
        if (values.Any(static value => !double.IsFinite(value) || value < 0 || value > 1))
        {
            throw new InvalidDataException("REAL_WORKFLOW_ACCEPTANCE_BAR_INVALID");
        }
        return bars;
    }

    private static FrozenRealWorkflowCandidateIdentity CandidateIdentity(
        FrozenCandidateBinding candidate)
    {
        string executionDescriptorSha256 = ComputeExecutionDescriptorSha256(
            candidate.CopyDocumentBytes());
        string operatingPointIdentity = ComputeOperatingPointIdentity(
            executionDescriptorSha256);
        return new FrozenRealWorkflowCandidateIdentity(
            candidate.Revision,
            candidate.CandidateId,
            candidate.Sha256,
            executionDescriptorSha256,
            operatingPointIdentity,
            candidate.Protocol.RelativePath.Replace('\\', '/'),
            candidate.Protocol.Sha256,
            candidate.OcrDetection.Payload.Sha256,
            candidate.OcrRecognition.Payload.Sha256,
            candidate.MarkerCenter.Payload.Sha256,
            candidate.MarkerClassifier.ModelSha256);
    }

    internal static string ComputeExecutionDescriptorSha256(byte[] candidateDocumentBytes)
    {
        ArgumentNullException.ThrowIfNull(candidateDocumentBytes);
        using JsonDocument document = ParseExact(candidateDocumentBytes);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, document.RootElement, omitRootProtocol: true);
        }
        return FrozenCandidateBinding.Hash(stream.ToArray());
    }

    internal static string ComputeOperatingPointIdentity(string executionDescriptorSha256)
    {
        string descriptor = FrozenCandidateBinding.RequireSha256(
            executionDescriptorSha256, nameof(executionDescriptorSha256));
        string value = string.Join('\n',
        [
            OperatingPointDomain,
            descriptor,
            "marker_center_threshold=0.25",
            "source_pixel_tolerance=5",
            "graph_x_tolerance=0.5",
            "graph_y_tolerance=5",
            "aggregate_only=true",
        ]);
        return FrozenCandidateBinding.Hash(Encoding.UTF8.GetBytes(value));
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement value,
        bool omitRootProtocol = false)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject()
                    .Where(property => !omitRootProtocol || property.Name != "protocol")
                    .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("REAL_WORKFLOW_CANDIDATE_DESCRIPTOR_JSON_INVALID");
        }
    }

    private static byte[] ReadReferenced(
        string root, string relativePath, string expectedSha256, CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Replace('\\', '/').Split('/').Any(
                static part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("REAL_WORKFLOW_REFERENCE_PATH_INVALID");
        }
        string path = FrozenCandidateBinding.RequireUnderRoot(root, relativePath, "real workflow reference");
        byte[] bytes = ReadBounded(path, cancellationToken);
        if (FrozenCandidateBinding.Hash(bytes) != expectedSha256)
        {
            throw new InvalidDataException("REAL_WORKFLOW_REFERENCE_CHECKSUM_MISMATCH");
        }
        return bytes;
    }

    private static byte[] ReadBounded(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumJsonBytes)
        {
            throw new InvalidDataException("REAL_WORKFLOW_JSON_SIZE_INVALID");
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        int offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("REAL_WORKFLOW_JSON_TRUNCATED");
            }
            offset += read;
        }
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("REAL_WORKFLOW_JSON_CHANGED_WHILE_READING");
        }
        return bytes;
    }

    private static JsonDocument ParseExact(byte[] bytes)
    {
        try
        {
            JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            RejectDuplicates(document.RootElement);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("REAL_WORKFLOW_JSON_INVALID", exception);
        }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("REAL_WORKFLOW_JSON_DUPLICATE_PROPERTY");
                }
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in value.EnumerateArray())
            {
                RejectDuplicates(child);
            }
        }
    }

    private static void RequireProperties(JsonElement value, string[] names, string label)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.EnumerateObject().Select(static property => property.Name)
                .Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException($"REAL_WORKFLOW_{label.Replace(' ', '_').ToUpperInvariant()}_SHAPE_INVALID");
        }
    }

    private static (string Path, string Sha256) ReadReference(JsonElement value, string label)
    {
        RequireProperties(value, ["path", "sha256"], label);
        return (Text(value, "path"), Sha(value, "sha256"));
    }

    private static string Text(JsonElement value, string property)
    {
        string? result = value.GetProperty(property).GetString();
        if (string.IsNullOrWhiteSpace(result) || result != result.Trim())
        {
            throw new InvalidDataException("REAL_WORKFLOW_TEXT_INVALID");
        }
        return result;
    }

    private static void RequireText(JsonElement value, string property, string label) =>
        _ = Text(value, property);

    private static string Sha(JsonElement value, string property) =>
        FrozenCandidateBinding.RequireSha256(Text(value, property), property);

    private static string? NullableSha(JsonElement value, string property) =>
        value.GetProperty(property).ValueKind == JsonValueKind.Null ? null : Sha(value, property);

    private static int Integer(JsonElement value, string property)
    {
        JsonElement raw = value.GetProperty(property);
        if (!raw.TryGetInt32(out int result))
        {
            throw new InvalidDataException("REAL_WORKFLOW_INTEGER_INVALID");
        }
        return result;
    }

    private static double Number(JsonElement value, string property)
    {
        double result = value.GetProperty(property).GetDouble();
        if (!double.IsFinite(result))
        {
            throw new InvalidDataException("REAL_WORKFLOW_NUMBER_INVALID");
        }
        return result;
    }

    private static double RequiredNullableNumber(JsonElement value, string property)
    {
        if (value.GetProperty(property).ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException("REAL_WORKFLOW_REQUIRED_METRIC_UNAVAILABLE");
        }
        return Number(value, property);
    }

    private static bool Boolean(JsonElement value, string property)
    {
        JsonElement raw = value.GetProperty(property);
        if (raw.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("REAL_WORKFLOW_BOOLEAN_INVALID");
        }
        return raw.GetBoolean();
    }

    internal static bool IsContinuousIntegration() =>
        IsTrue(Environment.GetEnvironmentVariable("CI")) ||
        IsTrue(Environment.GetEnvironmentVariable("TF_BUILD")) ||
        IsTrue(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    private static bool IsTrue(string? value) =>
        value is not null && (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                              value.Equals("true", StringComparison.OrdinalIgnoreCase));
}
