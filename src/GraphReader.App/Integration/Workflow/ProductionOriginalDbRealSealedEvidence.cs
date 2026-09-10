// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.Inference;

namespace GraphReader.App.Integration.Workflow;

/// <summary>
/// Validates the portable aggregate emitted by the frozen real-workflow worker.
/// This evidence covers complete workflow/export behavior. OCR-stage quality is
/// established separately because the worker aggregate exposes no OCR metrics.
/// </summary>
internal static class ProductionOriginalDbRealSealedEvidence
{
    internal const string WorkerAggregateSchema = "graphreader.engauge-grouped-workflow-aggregate.v1";
    private const string CandidateSchema = "graphreader.real-acceptance-frozen-candidate.v1";
    private const string AcceptanceBarsPath = "ml/policy/acceptance-bars.json";
    private const string EvidencePolicyPath = "ml/policy/evidence-policy.json";
    private const string OperatingPointDomain = "graphreader.frozen-real-workflow-operating-point.v1";
    private const int ExpectedProjectCount = 51;
    private const int MaximumJsonBytes = 32 * 1024 * 1024;
    private const double NumericTolerance = 1e-12;

    private static readonly HashSet<string> ExecutionFailureCodes = new(StringComparer.Ordinal)
    {
        "WORKFLOW_IMAGE_IMPORT_FAILED", "WORKFLOW_PDF_IMPORT_UNAVAILABLE",
        "WORKFLOW_PDF_IMPORT_FAILED", "WORKFLOW_PDF_PANEL_BYTES_UNAVAILABLE",
        "WORKFLOW_DETECTION_MODELS_UNAVAILABLE", "WORKFLOW_DETECTION_EVIDENCE_REJECTED",
        "WORKFLOW_REVIEW_PROJECTION_REJECTED", "WORKFLOW_RECALIBRATION_REQUIRED",
        "WORKFLOW_EXPORT_FAILED", "WORKFLOW_EXECUTION_FAILED", "WORKFLOW_OUTPUT_MISSING",
        "WORKFLOW_OUTPUT_IDENTITY_MISMATCH", "WORKFLOW_OUTPUT_INVALID",
    };

    private static readonly HashSet<string> EvaluationFailureCodes = new(StringComparer.Ordinal)
    {
        "duplicate_case_output", "unexpected_case", "missing_case_output",
        "source_identity_mismatch", "workflow_failed_without_code", "workflow_failed",
        "artifact_integrity:InvalidDataException", "artifact_integrity:IOException",
        "artifact_integrity:UnauthorizedAccessException", "artifact_integrity:JsonException",
        "artifact_integrity:DecoderFallbackException", "artifact_integrity:OverflowException",
        "artifact_integrity:ArgumentException", "artifact_integrity:NotSupportedException",
        "artifact_integrity:SecurityException",
    };

    internal static void Validate(
        byte[] workerResultBytes,
        byte[] candidateBindingBytes,
        string candidateBindingSha256,
        byte[] protocolBytes,
        string protocolSha256,
        byte[] acceptanceBarsBytes,
        string acceptanceBarsSha256,
        ModelIdentity detectionModel,
        ModelIdentity recognitionModel)
    {
        ArgumentNullException.ThrowIfNull(detectionModel);
        ArgumentNullException.ThrowIfNull(recognitionModel);
        string candidateSha = VerifyBytes(candidateBindingBytes, candidateBindingSha256,
            "real-sealed candidate binding");
        string protocolSha = VerifyBytes(protocolBytes, protocolSha256, "real-sealed protocol");
        string barsSha = VerifyBytes(acceptanceBarsBytes, acceptanceBarsSha256,
            "real-sealed acceptance bars");

        using JsonDocument candidateDocument = Parse(candidateBindingBytes, "real-sealed candidate binding");
        CandidateIdentity candidate = ValidateCandidate(
            candidateDocument.RootElement, candidateSha, detectionModel, recognitionModel);
        using JsonDocument protocolDocument = Parse(protocolBytes, "real-sealed protocol");
        ProtocolIdentity protocol = ValidateProtocol(
            protocolDocument.RootElement, protocolSha, barsSha, candidate);
        double bar = ValidateAcceptanceBars(acceptanceBarsBytes);
        using JsonDocument resultDocument = Parse(workerResultBytes, "real-sealed worker result");
        ValidateWorkerResult(resultDocument.RootElement, candidate, protocol, bar);
    }

    private static CandidateIdentity ValidateCandidate(
        JsonElement root,
        string candidateSha256,
        ModelIdentity detectionModel,
        ModelIdentity recognitionModel)
    {
        RequireProperties(root,
        [
            "schema", "candidate_id", "revision", "protocol", "runtime", "ocr_detection",
            "ocr_recognition", "marker_center", "marker_classifier", "native_files",
            "managed_files", "algorithms",
        ], "real-sealed candidate binding");
        RequireString(root, "schema", CandidateSchema, "real-sealed candidate binding");
        string candidateId = Text(root, "candidate_id", "real-sealed candidate binding");
        string revision = Text(root, "revision", "real-sealed candidate binding");
        FileReference protocol = ReadFileReference(root.GetProperty("protocol"), "candidate protocol");
        ValidateRuntime(root.GetProperty("runtime"));
        ModelReference detection = ReadModel(root.GetProperty("ocr_detection"), "ocr_detection");
        ModelReference recognition = ReadModel(root.GetProperty("ocr_recognition"), "ocr_recognition");
        ModelReference marker = ReadModel(root.GetProperty("marker_center"), "marker_center");
        RequireModel(detection, detectionModel, "real-sealed OCR detector");
        RequireModel(recognition, recognitionModel, "real-sealed OCR recognizer");
        JsonElement classifier = root.GetProperty("marker_classifier");
        RequireProperties(classifier,
        [
            "store_root", "model_id", "version", "model_sha256", "manifest_sha256",
            "notice_sha256", "benchmark_sha256", "package_index_sha256",
        ], "real-sealed marker classifier");
        string classifierSha = Sha(classifier, "model_sha256", "real-sealed marker classifier");
        ValidateFileArray(root.GetProperty("native_files"), allowRole: true, "candidate native files");
        ValidateFileArray(root.GetProperty("managed_files"), allowRole: false, "candidate managed files");
        JsonElement algorithms = root.GetProperty("algorithms");
        ValidateAlgorithms(algorithms);
        string executionDescriptor = ComputeExecutionDescriptorSha256(root);
        return new CandidateIdentity(
            candidateId,
            revision,
            candidateSha256,
            protocol,
            detection.PayloadSha256,
            recognition.PayloadSha256,
            marker.PayloadSha256,
            classifierSha,
            executionDescriptor,
            ComputeOperatingPointIdentity(executionDescriptor));
    }

    private static void ValidateRuntime(JsonElement value)
    {
        RequireProperties(value,
        [
            "execution_provider", "graph_optimization", "intra_operation_threads",
            "inter_operation_threads", "queue_capacity", "worker_count",
        ], "real-sealed candidate runtime");
        RequireString(value, "execution_provider", "cpu", "real-sealed candidate runtime");
        RequireString(value, "graph_optimization", "disabled", "real-sealed candidate runtime");
        _ = PositiveInt(value, "intra_operation_threads", "real-sealed candidate runtime");
        if (PositiveInt(value, "inter_operation_threads", "real-sealed candidate runtime") != 1)
        {
            throw new InvalidDataException("Real-sealed candidate inter-operation threads changed.");
        }
        _ = PositiveInt(value, "queue_capacity", "real-sealed candidate runtime");
        _ = PositiveInt(value, "worker_count", "real-sealed candidate runtime");
    }

    private static ModelReference ReadModel(JsonElement value, string task)
    {
        RequireProperties(value,
        ["task", "model_id", "version", "payload", "manifest", "reviewed_license_inputs"],
            $"real-sealed {task}");
        RequireString(value, "task", task, $"real-sealed {task}");
        FileReference payload = ReadFileReference(value.GetProperty("payload"), $"{task} payload");
        _ = ReadFileReference(value.GetProperty("manifest"), $"{task} manifest");
        JsonElement licenses = value.GetProperty("reviewed_license_inputs");
        if (licenses.ValueKind != JsonValueKind.Array || licenses.GetArrayLength() != 2)
        {
            throw new InvalidDataException($"Real-sealed {task} license inventory changed.");
        }
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement license in licenses.EnumerateArray())
        {
            RequireProperties(license, ["role", "declared_path", "file", "sha256"],
                $"real-sealed {task} license");
            string role = Text(license, "role", $"real-sealed {task} license");
            if (role is not ("license_text" or "notice") || !roles.Add(role))
            {
                throw new InvalidDataException($"Real-sealed {task} license roles changed.");
            }
            RequireCanonicalPath(Text(license, "declared_path", $"real-sealed {task} license"),
                $"real-sealed {task} declared license");
            RequireCanonicalPath(Text(license, "file", $"real-sealed {task} license"),
                $"real-sealed {task} license file");
            _ = Sha(license, "sha256", $"real-sealed {task} license");
        }
        return new ModelReference(
            Text(value, "model_id", $"real-sealed {task}"),
            Text(value, "version", $"real-sealed {task}"),
            payload.Sha256);
    }

    private static void RequireModel(ModelReference actual, ModelIdentity expected, string label)
    {
        if (actual.ModelId != expected.ModelId || actual.Version != expected.Version ||
            !actual.PayloadSha256.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label} identity changed.");
        }
    }

    private static void ValidateAlgorithms(JsonElement value)
    {
        string[] fields =
        [
            "axis_stage_version", "ocr_output_geometry", "ocr_composition_version",
            "artifact_algorithm_id", "artifact_algorithm_version", "artifact_configuration_sha256",
            "artifact_app_assembly_sha256", "artifact_ocr_assembly_sha256",
            "marker_center_revision", "marker_center_candidate_id", "marker_classifier_adapter_id",
            "legend_adapter_id", "phase_adapter_id",
        ];
        if (value.TryGetProperty("marker_proposal_domain", out _))
        {
            fields = [.. fields, "marker_proposal_domain"];
            string domain = Text(value, "marker_proposal_domain", "real-sealed candidate algorithms");
            if (domain is not ("full_frame_v24" or "axis_polygon_or_16px_v25"))
            {
                throw new InvalidDataException("Real-sealed marker proposal domain is unsupported.");
            }
        }
        RequireProperties(value, fields, "real-sealed candidate algorithms");
        RequireString(value, "ocr_output_geometry", "model_polygon", "real-sealed candidate algorithms");
        RequireString(value, "ocr_composition_version",
            ProductionOcrAdapter.OriginalDbCandidateCompositionVersion,
            "real-sealed candidate algorithms");
        foreach (string name in new[]
                 {
                     "axis_stage_version", "artifact_algorithm_id", "artifact_algorithm_version",
                     "marker_center_revision", "marker_center_candidate_id", "marker_classifier_adapter_id",
                     "legend_adapter_id", "phase_adapter_id",
                 })
        {
            _ = Text(value, name, "real-sealed candidate algorithms");
        }
        foreach (string name in new[]
                 {
                     "artifact_configuration_sha256", "artifact_app_assembly_sha256",
                     "artifact_ocr_assembly_sha256",
                 })
        {
            _ = Sha(value, name, "real-sealed candidate algorithms");
        }
    }

    private static ProtocolIdentity ValidateProtocol(
        JsonElement root,
        string protocolSha256,
        string acceptanceBarsSha256,
        CandidateIdentity candidate)
    {
        RequireProperties(root,
        [
            "evidence_policy", "hypothesis", "isolated_change", "split_identities",
            "metric", "acceptance_bar", "budget",
        ], "real-sealed protocol");
        _ = Text(root, "hypothesis", "real-sealed protocol");
        _ = Text(root, "isolated_change", "real-sealed protocol");
        FileReference policy = ReadPathReference(root.GetProperty("evidence_policy"), "evidence policy");
        FileReference bars = ReadPathReference(root.GetProperty("acceptance_bar"), "acceptance bars");
        if (policy.Path != EvidencePolicyPath || bars.Path != AcceptanceBarsPath ||
            !bars.Sha256.Equals(acceptanceBarsSha256, StringComparison.OrdinalIgnoreCase) ||
            !candidate.Protocol.Sha256.Equals(protocolSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Real-sealed protocol reference identity changed.");
        }

        JsonElement identities = root.GetProperty("split_identities");
        RequireProperties(identities,
        [
            "split", "assignment_sha256", "selected_inventory_sha256", "project_count",
            "candidate_revision", "candidate_id", "execution_descriptor_sha256",
            "operating_point_identity", "ocr_detection_sha256", "ocr_recognition_sha256",
            "marker_center_sha256", "marker_classifier_sha256", "prerequisites",
        ], "real-sealed protocol split identities");
        RequireString(identities, "split", "real-sealed", "real-sealed protocol split identities");
        if (PositiveInt(identities, "project_count", "real-sealed protocol split identities") !=
                ExpectedProjectCount ||
            Text(identities, "candidate_revision", "real-sealed protocol split identities") != candidate.Revision ||
            Text(identities, "candidate_id", "real-sealed protocol split identities") != candidate.CandidateId ||
            Sha(identities, "execution_descriptor_sha256", "real-sealed protocol split identities") !=
                candidate.ExecutionDescriptorSha256 ||
            Sha(identities, "operating_point_identity", "real-sealed protocol split identities") !=
                candidate.OperatingPointIdentity ||
            Sha(identities, "ocr_detection_sha256", "real-sealed protocol split identities") !=
                candidate.OcrDetectionSha256 ||
            Sha(identities, "ocr_recognition_sha256", "real-sealed protocol split identities") !=
                candidate.OcrRecognitionSha256 ||
            Sha(identities, "marker_center_sha256", "real-sealed protocol split identities") !=
                candidate.MarkerCenterSha256 ||
            Sha(identities, "marker_classifier_sha256", "real-sealed protocol split identities") !=
                candidate.MarkerClassifierSha256)
        {
            throw new InvalidDataException("Real-sealed protocol candidate identity changed.");
        }
        string assignment = Sha(identities, "assignment_sha256", "real-sealed protocol split identities");
        string inventory = Sha(identities, "selected_inventory_sha256", "real-sealed protocol split identities");
        ValidatePrerequisiteReferences(identities.GetProperty("prerequisites"));
        ValidateMetric(root.GetProperty("metric"));
        ValidateBudget(root.GetProperty("budget"));
        return new ProtocolIdentity(protocolSha256, assignment, inventory);
    }

    private static void ValidatePrerequisiteReferences(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4)
        {
            throw new InvalidDataException("Real-sealed protocol prerequisite coverage changed.");
        }
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in value.EnumerateArray())
        {
            RequireProperties(item, ["task", "split", "path", "sha256"],
                "real-sealed prerequisite reference");
            string task = Text(item, "task", "real-sealed prerequisite reference");
            string split = Text(item, "split", "real-sealed prerequisite reference");
            if (task is not ("ocr-detection-recognition" or "marker-center") ||
                split is not ("synthetic-dev" or "synthetic-sealed") ||
                !roles.Add(task + "/" + split))
            {
                throw new InvalidDataException("Real-sealed prerequisite role changed.");
            }
            RequireCanonicalPath(Text(item, "path", "real-sealed prerequisite reference"),
                "real-sealed prerequisite path");
            _ = Sha(item, "sha256", "real-sealed prerequisite reference");
        }
    }

    private static void ValidateMetric(JsonElement value)
    {
        RequireProperties(value,
        [
            "name", "source_pixel_match_tolerance", "graph_x_absolute_tolerance",
            "graph_y_absolute_tolerance", "integer_session_x", "full_project_denominator",
            "full_point_denominator", "series_boundaries", "aggregate_only",
            "require_in_memory_artifacts",
        ], "real-sealed metric");
        RequireString(value, "name", "whole-workflow-csv-values", "real-sealed metric");
        if (Number(value, "source_pixel_match_tolerance", "real-sealed metric") != 5 ||
            Number(value, "graph_x_absolute_tolerance", "real-sealed metric") != 0.5 ||
            Number(value, "graph_y_absolute_tolerance", "real-sealed metric") != 5 ||
            !Boolean(value, "integer_session_x", "real-sealed metric") ||
            !Boolean(value, "full_project_denominator", "real-sealed metric") ||
            !Boolean(value, "full_point_denominator", "real-sealed metric") ||
            !Boolean(value, "series_boundaries", "real-sealed metric") ||
            !Boolean(value, "aggregate_only", "real-sealed metric") ||
            !Boolean(value, "require_in_memory_artifacts", "real-sealed metric"))
        {
            throw new InvalidDataException("Real-sealed metric contract changed.");
        }
    }

    private static void ValidateBudget(JsonElement value)
    {
        RequireProperties(value,
        [
            "optimizer_steps", "training_use", "candidate_selection", "production_approval",
            "aggregate_only", "sealed_reads_per_candidate",
        ], "real-sealed budget");
        if (NonNegativeInt(value, "optimizer_steps", "real-sealed budget") != 0 ||
            Boolean(value, "training_use", "real-sealed budget") ||
            Boolean(value, "candidate_selection", "real-sealed budget") ||
            Boolean(value, "production_approval", "real-sealed budget") ||
            !Boolean(value, "aggregate_only", "real-sealed budget") ||
            NonNegativeInt(value, "sealed_reads_per_candidate", "real-sealed budget") != 1)
        {
            throw new InvalidDataException("Real-sealed budget contract changed.");
        }
    }

    private static double ValidateAcceptanceBars(byte[] bytes)
    {
        using JsonDocument document = Parse(bytes, "real-sealed acceptance bars");
        JsonElement root = document.RootElement;
        RequireString(root, "policy_id", "graphreader-goal22-tier1-v1", "real-sealed acceptance bars");
        if (NonNegativeInt(root, "schema_version", "real-sealed acceptance bars") != 1)
        {
            throw new InvalidDataException("Real-sealed acceptance-bars schema changed.");
        }
        JsonElement tier = Object(root, "tier1_reviewable_error", "real-sealed acceptance bars");
        double precision = Number(tier, "marker_center_precision_minimum", "real-sealed acceptance bars");
        double recall = Number(tier, "marker_center_recall_minimum", "real-sealed acceptance bars");
        if (precision != 0.95 || recall != 0.95)
        {
            throw new InvalidDataException("Real-sealed workflow bars differ from the central Tier-1 bar.");
        }
        return precision;
    }

    private static void ValidateWorkerResult(
        JsonElement root,
        CandidateIdentity candidate,
        ProtocolIdentity protocol,
        double bar)
    {
        RequireProperties(root,
        [
            "candidate_sha256", "protocol_sha256", "assignment_sha256",
            "selected_inventory_sha256", "corpus_content_sha256", "split", "aggregate",
        ], "real-sealed worker result");
        if (Sha(root, "candidate_sha256", "real-sealed worker result") != candidate.CandidateSha256 ||
            Sha(root, "protocol_sha256", "real-sealed worker result") != protocol.ProtocolSha256 ||
            Sha(root, "assignment_sha256", "real-sealed worker result") != protocol.AssignmentSha256 ||
            Sha(root, "selected_inventory_sha256", "real-sealed worker result") !=
                protocol.SelectedInventorySha256)
        {
            throw new InvalidDataException("Real-sealed worker result identity changed.");
        }
        _ = Sha(root, "corpus_content_sha256", "real-sealed worker result");
        RequireString(root, "split", "real-sealed", "real-sealed worker result");
        ValidateAggregate(Object(root, "aggregate", "real-sealed worker result"), bar);
    }

    private static void ValidateAggregate(JsonElement value, double bar)
    {
        RequireProperties(value,
        [
            "schema", "image_groups", "decoded_image_groups", "project_files",
            "workflow_invocations", "workflow_succeeded_image_groups", "workflow_failed_image_groups",
            "workflow_succeeded_projects", "workflow_failed_projects", "truth_series", "truth_points",
            "coincident_cross_project_point_pairs", "execution_failure_kinds", "evaluation",
            "aggregate_only", "case_level_output", "truth_rows_output", "prediction_output",
            "paths_output", "names_output",
        ], "real-sealed aggregate");
        RequireString(value, "schema", WorkerAggregateSchema, "real-sealed aggregate");
        int groups = PositiveInt(value, "image_groups", "real-sealed aggregate");
        int decoded = NonNegativeInt(value, "decoded_image_groups", "real-sealed aggregate");
        int projects = PositiveInt(value, "project_files", "real-sealed aggregate");
        int invocations = NonNegativeInt(value, "workflow_invocations", "real-sealed aggregate");
        int succeededGroups = NonNegativeInt(value, "workflow_succeeded_image_groups", "real-sealed aggregate");
        int failedGroups = NonNegativeInt(value, "workflow_failed_image_groups", "real-sealed aggregate");
        int succeededProjects = NonNegativeInt(value, "workflow_succeeded_projects", "real-sealed aggregate");
        int failedProjects = NonNegativeInt(value, "workflow_failed_projects", "real-sealed aggregate");
        int truthSeries = PositiveInt(value, "truth_series", "real-sealed aggregate");
        int truthPoints = PositiveInt(value, "truth_points", "real-sealed aggregate");
        _ = NonNegativeInt(value, "coincident_cross_project_point_pairs", "real-sealed aggregate");
        if (projects != ExpectedProjectCount || decoded > groups || invocations != decoded ||
            succeededGroups + failedGroups != groups || succeededProjects + failedProjects != projects ||
            !Boolean(value, "aggregate_only", "real-sealed aggregate") ||
            Boolean(value, "case_level_output", "real-sealed aggregate") ||
            Boolean(value, "truth_rows_output", "real-sealed aggregate") ||
            Boolean(value, "prediction_output", "real-sealed aggregate") ||
            Boolean(value, "paths_output", "real-sealed aggregate") ||
            Boolean(value, "names_output", "real-sealed aggregate"))
        {
            throw new InvalidDataException("Real-sealed aggregate scope or denominator changed.");
        }
        int executionFailures = FailureCounts(
            value.GetProperty("execution_failure_kinds"), ExecutionFailureCodes,
            "real-sealed execution failures");
        if (executionFailures != failedGroups)
        {
            throw new InvalidDataException("Real-sealed execution failure counts are inconsistent.");
        }
        ValidateEvaluation(Object(value, "evaluation", "real-sealed aggregate"), groups,
            truthSeries, truthPoints, bar);
    }

    private static void ValidateEvaluation(
        JsonElement value,
        int groups,
        int aggregateTruthSeries,
        int aggregateTruthPoints,
        double bar)
    {
        RequireProperties(value,
        [
            "truth_cases", "output_cases", "completed_cases", "failed_cases", "unexpected_cases",
            "integrity_failure_cases", "truth_series", "predicted_series", "matched_series",
            "truth_points", "predicted_points", "matched_points", "unique_point_value_metrics_available",
            "relational_row_metrics_available", "relational_phase_metrics_available", "actual_rows",
            "residual_artifact_rows_from_failed_cases", "expected_rows", "structurally_matched_rows",
            "correct_rows", "missing_rows", "extra_rows", "duplicate_rows", "wrong_scale_rows",
            "wrong_export_mode_rows", "wrong_phase_rows", "wrong_relation_rows",
            "unique_point_value_correct", "unique_point_value_incorrect", "unique_point_missing",
            "unique_point_extra", "unique_point_wrong_scale", "unique_point_wrong_export_mode",
            "unique_point_structural_precision", "unique_point_structural_coverage",
            "unique_point_value_precision", "unique_point_value_coverage",
            "matched_unique_point_value_accuracy", "relational_row_precision",
            "relational_row_coverage", "matched_relational_graph_value_accuracy",
            "matched_relational_phase_accuracy", "artifact_integrity_valid", "failure_kinds",
        ], "real-sealed evaluation");
        int truthCases = NonNegativeInt(value, "truth_cases", "real-sealed evaluation");
        int outputCases = NonNegativeInt(value, "output_cases", "real-sealed evaluation");
        int completedCases = NonNegativeInt(value, "completed_cases", "real-sealed evaluation");
        int failedCases = NonNegativeInt(value, "failed_cases", "real-sealed evaluation");
        int unexpectedCases = NonNegativeInt(value, "unexpected_cases", "real-sealed evaluation");
        int integrityFailures = NonNegativeInt(value, "integrity_failure_cases", "real-sealed evaluation");
        int truthSeries = PositiveInt(value, "truth_series", "real-sealed evaluation");
        int predictedSeries = NonNegativeInt(value, "predicted_series", "real-sealed evaluation");
        int matchedSeries = NonNegativeInt(value, "matched_series", "real-sealed evaluation");
        int truthPoints = PositiveInt(value, "truth_points", "real-sealed evaluation");
        int predictedPoints = NonNegativeInt(value, "predicted_points", "real-sealed evaluation");
        int matchedPoints = NonNegativeInt(value, "matched_points", "real-sealed evaluation");
        int actualRows = NonNegativeInt(value, "actual_rows", "real-sealed evaluation");
        int residualRows = NonNegativeInt(value, "residual_artifact_rows_from_failed_cases",
            "real-sealed evaluation");
        if (truthCases != groups || outputCases != groups || completedCases + failedCases != groups ||
            unexpectedCases != 0 || integrityFailures != 0 || truthSeries != aggregateTruthSeries ||
            truthPoints != aggregateTruthPoints || matchedSeries > Math.Min(truthSeries, predictedSeries) ||
            matchedPoints > Math.Min(truthPoints, predictedPoints) || actualRows < residualRows ||
            !Boolean(value, "artifact_integrity_valid", "real-sealed evaluation"))
        {
            throw new InvalidDataException("Real-sealed evaluation denominator or integrity changed.");
        }
        if (!Boolean(value, "unique_point_value_metrics_available", "real-sealed evaluation") ||
            Boolean(value, "relational_row_metrics_available", "real-sealed evaluation") ||
            Boolean(value, "relational_phase_metrics_available", "real-sealed evaluation"))
        {
            throw new InvalidDataException("Real-sealed evaluation metric availability changed.");
        }
        foreach (string nullable in new[]
                 {
                     "expected_rows", "structurally_matched_rows", "correct_rows", "missing_rows",
                     "extra_rows", "duplicate_rows", "wrong_scale_rows", "wrong_export_mode_rows",
                     "wrong_phase_rows", "wrong_relation_rows", "relational_row_precision",
                     "relational_row_coverage", "matched_relational_graph_value_accuracy",
                     "matched_relational_phase_accuracy",
                 })
        {
            RequireNull(value, nullable, "real-sealed evaluation");
        }
        int correct = RequiredNonNegativeInt(value, "unique_point_value_correct", "real-sealed evaluation");
        int incorrect = RequiredNonNegativeInt(value, "unique_point_value_incorrect", "real-sealed evaluation");
        int missing = RequiredNonNegativeInt(value, "unique_point_missing", "real-sealed evaluation");
        int extra = RequiredNonNegativeInt(value, "unique_point_extra", "real-sealed evaluation");
        int wrongScale = RequiredNonNegativeInt(value, "unique_point_wrong_scale", "real-sealed evaluation");
        int wrongMode = RequiredNonNegativeInt(value, "unique_point_wrong_export_mode", "real-sealed evaluation");
        if (correct + incorrect != matchedPoints || missing != truthPoints - matchedPoints ||
            extra != predictedPoints - matchedPoints || wrongScale > incorrect || wrongMode > incorrect)
        {
            throw new InvalidDataException("Real-sealed unique-point counts are inconsistent.");
        }
        double structuralPrecision = predictedPoints == 0 ? 0 : Ratio(matchedPoints, predictedPoints);
        double structuralCoverage = Ratio(matchedPoints, truthPoints);
        double? valuePrecision = NullableRatio(correct, predictedPoints);
        double valueCoverage = Ratio(correct, truthPoints);
        double? matchedAccuracy = NullableRatio(correct, matchedPoints);
        RequireMetric(value, "unique_point_structural_precision", structuralPrecision,
            "real-sealed evaluation");
        RequireMetric(value, "unique_point_structural_coverage", structuralCoverage,
            "real-sealed evaluation");
        RequireMetric(value, "unique_point_value_precision", valuePrecision,
            "real-sealed evaluation");
        RequireMetric(value, "unique_point_value_coverage", valueCoverage,
            "real-sealed evaluation");
        RequireMetric(value, "matched_unique_point_value_accuracy", matchedAccuracy,
            "real-sealed evaluation");
        if (structuralPrecision < bar || structuralCoverage < bar ||
            valuePrecision is null || valuePrecision.Value < bar || valueCoverage < bar ||
            matchedAccuracy is null || matchedAccuracy.Value < bar)
        {
            throw new InvalidDataException("Real-sealed whole-workflow Tier-1 gate failed.");
        }
        int evaluationFailures = FailureCounts(
            value.GetProperty("failure_kinds"), EvaluationFailureCodes,
            "real-sealed evaluation failures");
        if (evaluationFailures < failedCases)
        {
            throw new InvalidDataException("Real-sealed evaluation failure counts are inconsistent.");
        }
    }

    private static string ComputeExecutionDescriptorSha256(JsonElement candidate)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, candidate, omitRootProtocol: true);
        }
        return Hash(stream.ToArray());
    }

    private static string ComputeOperatingPointIdentity(string executionDescriptorSha256)
    {
        string value = string.Join('\n',
        [
            OperatingPointDomain,
            executionDescriptorSha256,
            "marker_center_threshold=0.25",
            "source_pixel_tolerance=5",
            "graph_x_tolerance=0.5",
            "graph_y_tolerance=5",
            "aggregate_only=true",
        ]);
        return Hash(Encoding.UTF8.GetBytes(value));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, bool omitRootProtocol = false)
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
                foreach (JsonElement item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.Number: writer.WriteRawValue(value.GetRawText()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new InvalidDataException("Real-sealed candidate JSON is invalid.");
        }
    }

    private static FileReference ReadFileReference(JsonElement value, string label)
    {
        RequireProperties(value, ["file", "sha256"], label);
        string path = Text(value, "file", label);
        RequireCanonicalPath(path, label);
        return new FileReference(path.Replace('\\', '/'), Sha(value, "sha256", label));
    }

    private static FileReference ReadPathReference(JsonElement value, string label)
    {
        RequireProperties(value, ["path", "sha256"], label);
        string path = Text(value, "path", label);
        RequireCanonicalPath(path, label);
        return new FileReference(path.Replace('\\', '/'), Sha(value, "sha256", label));
    }

    private static void ValidateFileArray(JsonElement value, bool allowRole, string label)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"{label} must be nonempty.");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in value.EnumerateArray())
        {
            RequireProperties(item, allowRole ? ["role", "file", "sha256"] : ["file", "sha256"], label);
            if (allowRole) _ = Text(item, "role", label);
            string path = Text(item, "file", label);
            RequireCanonicalPath(path, label);
            if (!paths.Add(path.Replace('\\', '/'))) throw new InvalidDataException($"{label} has a duplicate path.");
            _ = Sha(item, "sha256", label);
        }
    }

    private static int FailureCounts(JsonElement value, HashSet<string> allowed, string label)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{label} must be an object.");
        int total = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetInt32(out int count) || count < 0)
            {
                throw new InvalidDataException($"{label} contains an invalid entry.");
            }
            total = checked(total + count);
        }
        return total;
    }

    private static JsonDocument Parse(byte[] bytes, string label)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > MaximumJsonBytes)
            throw new InvalidDataException($"{label} is empty or oversized.");
        try
        {
            JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            try
            {
                RejectDuplicates(document.RootElement, label);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"{label} must be an object.");
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"{label} is invalid JSON.", error);
        }
    }

    private static void RejectDuplicates(JsonElement value, string label)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException($"{label} has duplicate fields.");
                RejectDuplicates(property.Value, label);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicates(item, label);
        }
    }

    private static void RequireProperties(JsonElement value, IEnumerable<string> expected, string label)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(static p => p.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .SequenceEqual(expected.OrderBy(static name => name, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{label} fields changed.");
        }
    }

    private static JsonElement Object(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{label} field '{name}' must be an object.");
        return value;
    }

    private static string Text(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"{label} field '{name}' must be nonempty text.");
        return value.GetString()!;
    }

    private static void RequireString(JsonElement parent, string name, string expected, string label)
    {
        if (Text(parent, name, label) != expected) throw new InvalidDataException($"{label} field '{name}' changed.");
    }

    private static string Sha(JsonElement parent, string name, string label) =>
        ValidateSha(Text(parent, name, label), $"{label} field '{name}'");

    private static string ValidateSha(string value, string label)
    {
        if (value.Length != 64 || value.Any(static character => character is not
                (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException($"{label} must be lowercase SHA-256.");
        return value;
    }

    private static int NonNegativeInt(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result) || result < 0)
            throw new InvalidDataException($"{label} field '{name}' must be a nonnegative int32.");
        return result;
    }

    private static int PositiveInt(JsonElement parent, string name, string label)
    {
        int value = NonNegativeInt(parent, name, label);
        if (value == 0) throw new InvalidDataException($"{label} field '{name}' must be positive.");
        return value;
    }

    private static int RequiredNonNegativeInt(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException($"{label} field '{name}' is required.");
        return NonNegativeInt(parent, name, label);
    }

    private static double Number(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) || !double.IsFinite(result))
            throw new InvalidDataException($"{label} field '{name}' must be finite.");
        return result;
    }

    private static bool Boolean(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"{label} field '{name}' must be boolean.");
        return value.GetBoolean();
    }

    private static void RequireNull(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException($"{label} field '{name}' must be null when unavailable.");
    }

    private static void RequireMetric(
        JsonElement parent, string name, double? expected, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
        {
            throw new InvalidDataException($"{label} field '{name}' is missing.");
        }
        if (expected is null)
        {
            if (value.ValueKind != JsonValueKind.Null)
                throw new InvalidDataException($"{label} field '{name}' must be null when undefined.");
            return;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double actual) ||
            !double.IsFinite(actual) || Math.Abs(actual - expected.Value) > NumericTolerance)
            throw new InvalidDataException($"{label} field '{name}' differs from its counts.");
    }

    private static string VerifyBytes(byte[] bytes, string expectedSha256, string label)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string expected = ValidateSha(expectedSha256, $"{label} SHA-256");
        if (bytes.Length == 0 || bytes.Length > MaximumJsonBytes || Hash(bytes) != expected)
            throw new InvalidDataException($"{label} checksum changed.");
        return expected;
    }

    private static void RequireCanonicalPath(string path, string label)
    {
        string normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(path) || normalized.Split('/').Any(static part => part is "" or "." or ".."))
            throw new InvalidDataException($"{label} must be a canonical relative path.");
    }

    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? (numerator == 0 ? 1 : 0) : (double)numerator / denominator;

    private static double? NullableRatio(int numerator, int denominator) =>
        denominator == 0 ? null : (double)numerator / denominator;

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record FileReference(string Path, string Sha256);
    private sealed record ModelReference(string ModelId, string Version, string PayloadSha256);
    private sealed record CandidateIdentity(
        string CandidateId,
        string Revision,
        string CandidateSha256,
        FileReference Protocol,
        string OcrDetectionSha256,
        string OcrRecognitionSha256,
        string MarkerCenterSha256,
        string MarkerClassifierSha256,
        string ExecutionDescriptorSha256,
        string OperatingPointIdentity);
    private sealed record ProtocolIdentity(
        string ProtocolSha256,
        string AssignmentSha256,
        string SelectedInventorySha256);
}
