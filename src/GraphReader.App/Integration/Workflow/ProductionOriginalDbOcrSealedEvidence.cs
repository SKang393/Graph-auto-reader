// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GraphReader.App.Integration.Workflow;

internal static partial class ProductionOriginalDbOcrApprovalGate
{
    // Direct producer: ml/policy/ocr_sealed_evaluation.py. Metric semantics and
    // transport bounds: ml/policy/ocr_sealed_transport.py. These are format
    // identities, not additional acceptance thresholds.
    internal const string SyntheticSealedSchema = "graphreader.original-db-ocr-sealed-evaluation.v1";
    private const string SealedScope = "goal22.full-ocr.five-axis-family.real-range.v1";
    private const string SealedCoverageSha256 =
        "26ab0e017ccc6dc17d26effe11b1fb40f440ed4471e896c1afac3c8cc96999c4";
    private const string SealedPolicySha256 =
        "4dc18136c284b0b1805d3a3b22a9197ad06e6a41f4e43b4e1d4d9245b97e0aed";

    internal static void ValidateSyntheticSealedSourceForTest(
        byte[] outcome, byte[] request, byte[] preflight,
        string candidateSha256, string devScoreSha256, byte[] acceptanceBars) =>
        ValidateSyntheticSealedSource(outcome, request, preflight,
            ValidateSha(candidateSha256, "sealed candidate"),
            ValidateSha(devScoreSha256, "sealed development score"),
            Convert.ToHexStringLower(SHA256.HashData(acceptanceBars)),
            ReadAcceptanceBars(acceptanceBars));

    private static void ValidateSyntheticSealedSource(
        byte[] outcomeBytes, byte[] requestBytes, byte[] preflightBytes,
        string candidateSha256, string devScoreSha256, string barsSha256, CanonicalBars bars)
    {
        const string label = "full OCR synthetic-sealed evidence";
        using JsonDocument outcomeDocument = Parse(outcomeBytes, label);
        JsonElement outcome = outcomeDocument.RootElement;
        if (RequireText(outcome, "schema", label) != SyntheticSealedSchema)
            throw new InvalidDataException("Full OCR synthetic-sealed source schema is not supported.");
        RequireSealedFields(outcome, label,
            "schema", "status", "read_status", "split", "acceptance_scope",
            "admission_binding_sha256", "attempt_id", "candidate_sha256", "runtime_identity_sha256",
            "full_ocr_score_sha256", "acceptance_bars_sha256", "metric_reference_sha256",
            "coverage_protocol_sha256", "preflight_binding_sha256", "request", "aggregate",
            "bar_verdicts", "transport", "failure_code", "disclosures", "aggregate_only",
            "case_output", "truth_rows_output", "prediction_output", "pixel_output", "production_approved");
        RequireString(outcome, "status", "pass", label);
        RequireString(outcome, "read_status", "confirmed", label);
        RequireString(outcome, "split", "sealed", label);
        RequireString(outcome, "acceptance_scope", SealedScope, label);
        RequireBoolean(outcome, "aggregate_only", true, label);
        foreach (string flag in new[]
                 { "case_output", "truth_rows_output", "prediction_output", "pixel_output", "production_approved" })
            RequireBoolean(outcome, flag, false, label);
        JsonElement disclosures = outcome.GetProperty("disclosures");
        if (outcome.GetProperty("failure_code").ValueKind != JsonValueKind.Null ||
            disclosures.ValueKind != JsonValueKind.Array || disclosures.GetArrayLength() != 0)
            throw new InvalidDataException("Synthetic-sealed evidence contains a failure or disclosure.");

        RequireSha(outcome, "candidate_sha256", candidateSha256, label);
        RequireSha(outcome, "full_ocr_score_sha256", devScoreSha256, label);
        RequireSha(outcome, "acceptance_bars_sha256", barsSha256, label);
        RequireSha(outcome, "metric_reference_sha256", FullOcrDevEvaluatorSha256, label);
        RequireSha(outcome, "coverage_protocol_sha256", SealedCoverageSha256, label);
        string runtimeSha = ValidateSha(RequireText(outcome, "runtime_identity_sha256", label), label);
        string admissionSha = ValidateSha(RequireText(outcome, "admission_binding_sha256", label), label);
        string preflightSha = Convert.ToHexStringLower(SHA256.HashData(preflightBytes));
        RequireSha(outcome, "attempt_id", preflightSha, label);
        RequireSha(outcome, "preflight_binding_sha256", preflightSha, label);

        using JsonDocument preflightDocument = Parse(preflightBytes, label);
        JsonElement preflight = preflightDocument.RootElement;
        RequireSealedFields(preflight, label,
            "schema", "registry_sha256", "set_id", "full_ocr_score_sha256", "candidate_sha256",
            "runtime_identity_sha256", "acceptance_bars_sha256", "metric_reference_sha256",
            "acceptance_scope", "coverage_protocol_sha256", "gate_identity_sha256",
            "gate_binding_sha256", "training_binding_sha256", "evidence_policy");
        RequireString(preflight, "schema", "graphreader.original-db-ocr-sealed-preflight-binding.v1", label);
        RequireString(preflight, "acceptance_scope", SealedScope, label);
        foreach (string field in new[]
                 { "candidate_sha256", "full_ocr_score_sha256", "acceptance_bars_sha256",
                   "metric_reference_sha256", "coverage_protocol_sha256" })
            RequireSha(preflight, field, RequireText(outcome, field, label), label);
        RequireSha(preflight, "runtime_identity_sha256", runtimeSha, label);
        foreach (string field in new[]
                 { "registry_sha256", "set_id", "gate_identity_sha256", "gate_binding_sha256", "training_binding_sha256" })
            _ = ValidateSha(RequireText(preflight, field, label), label);
        JsonElement policy = RequireObject(preflight, "evidence_policy", label);
        RequireSealedFields(policy, label, "path", "schema_version", "policy_revision", "sha256");
        RequireString(policy, "path", "ml/policy/evidence-policy.json", label);
        RequireInt32(policy, "schema_version", 1, label);
        RequireString(policy, "policy_revision", "2026-08-19", label);
        RequireSha(policy, "sha256", SealedPolicySha256, label);

        JsonElement requestReference = RequireObject(outcome, "request", label);
        RequireSealedFields(requestReference, label, "path", "sha256");
        _ = RequireText(requestReference, "path", label);
        RequireSha(requestReference, "sha256", Convert.ToHexStringLower(SHA256.HashData(requestBytes)), label);
        using JsonDocument requestDocument = Parse(requestBytes, label);
        JsonElement request = requestDocument.RootElement;
        RequireSealedFields(request, label,
            "schema", "acceptance_scope", "split", "attempt_id", "admission_binding_sha256",
            "set_id", "candidate_path", "candidate_sha256", "archive_path", "archive_sha256",
            "archive_manifest_sha256", "source_count", "coverage_protocol_sha256");
        RequireString(request, "schema", "graphreader.original-db-ocr-sealed-worker-request.v1", label);
        RequireString(request, "acceptance_scope", SealedScope, label);
        RequireString(request, "split", "sealed", label);
        RequireSha(request, "attempt_id", preflightSha, label);
        RequireSha(request, "admission_binding_sha256", admissionSha, label);
        RequireSha(request, "set_id", RequireText(preflight, "set_id", label), label);
        RequireSha(request, "candidate_sha256", candidateSha256, label);
        RequireSha(request, "coverage_protocol_sha256", SealedCoverageSha256, label);
        foreach (string field in new[] { "archive_sha256", "archive_manifest_sha256" })
            _ = ValidateSha(RequireText(request, field, label), label);
        // Paths are provenance metadata only. Never reopen a development path or
        // a sealed archive from a production evidence bundle.
        _ = RequireText(request, "candidate_path", label);
        _ = RequireText(request, "archive_path", label);
        int sources = RequirePositiveInt32(request, "source_count", label);
        if (sources > 128)
            throw new InvalidDataException("Synthetic-sealed source inventory exceeds the producer bound.");

        ValidateSealedTransport(RequireObject(outcome, "transport", label));
        JsonElement aggregate = RequireObject(outcome, "aggregate", label);
        RequireSealedFields(aggregate, label, "source_count", "panel_count", "metrics");
        RequireInt32(aggregate, "source_count", sources, label);
        if (RequirePositiveInt32(aggregate, "panel_count", label) < sources)
            throw new InvalidDataException("Synthetic-sealed panel inventory omits sources.");
        ValidateSealedMetrics(RequireObject(aggregate, "metrics", label), sources, bars);
        JsonElement verdicts = RequireObject(outcome, "bar_verdicts", label);
        string[] metrics =
        [ "text_region_detection_precision", "text_region_detection_recall", "recognition_exact_match",
          "character_error_rate", "role_accuracy" ];
        RequireSealedFields(verdicts, label, metrics);
        foreach (string metric in metrics)
            RequireBoolean(verdicts, metric, true, label);
    }

    private static void ValidateSealedMetrics(JsonElement metrics, int sources, CanonicalBars bars)
    {
        const string label = "full OCR synthetic-sealed metrics";
        RequireSealedFields(metrics, label, "raw_detector_geometry", "successfully_recognized_region_geometry",
            "recognition_failures", "full_ocr_metrics");
        GeometryInventory ReadGeometry(string field)
        {
            JsonElement value = RequireObject(metrics, field, label);
            RequireSealedFields(value, label, "truth_region_count", "predicted_region_count", "true_positives",
                "false_positives", "false_negatives", "precision", "recall", "intersection_over_union_minimum");
            return ReadGeometryInventory(value, label);
        }
        GeometryInventory raw = ReadGeometry("raw_detector_geometry");
        GeometryInventory recognized = ReadGeometry("successfully_recognized_region_geometry");
        JsonElement failures = RequireObject(metrics, "recognition_failures", label);
        RequireSealedFields(failures, label, "raw_regions_without_successful_recognition");
        int failed = RequireNonNegativeInt32(failures, "raw_regions_without_successful_recognition", label);
        int maximumRegions = Math.Min(16384, sources * 1024);
        if (raw.Truth != recognized.Truth || raw.Truth > maximumRegions || raw.Predicted > maximumRegions ||
            recognized.Predicted > raw.Predicted || recognized.TruePositive > raw.TruePositive ||
            failed != raw.Predicted - recognized.Predicted)
            throw new InvalidDataException("Synthetic-sealed recognized inventory or failure accounting changed.");

        JsonElement full = RequireObject(metrics, "full_ocr_metrics", label);
        RequireSealedFields(full, label,
            "truth_region_count", "predicted_region_count", "geometry_matched_region_count",
            "geometry_false_positive_count", "geometry_false_negative_count", "recognition_exact_count",
            "recognition_exact_accuracy", "truth_character_count", "matched_pair_edit_count",
            "unmatched_truth_deletion_edit_count", "unmatched_prediction_insertion_edit_count",
            "character_error_count", "character_error_rate", "role_correct_count", "role_accuracy",
            "by_expected_runtime_role", "intersection_over_union_minimum");
        RequireInt32(full, "truth_region_count", recognized.Truth, label);
        RequireInt32(full, "predicted_region_count", recognized.Predicted, label);
        RequireInt32(full, "geometry_matched_region_count", recognized.TruePositive, label);
        RequireInt32(full, "geometry_false_positive_count", recognized.FalsePositive, label);
        RequireInt32(full, "geometry_false_negative_count", recognized.FalseNegative, label);
        RequireMetric(full, "intersection_over_union_minimum", 0.5, label);
        int exact = RequireNonNegativeInt32(full, "recognition_exact_count", label);
        int roleCorrect = RequireNonNegativeInt32(full, "role_correct_count", label);
        long characters = RequireSealedCount(full, "truth_character_count");
        long matchedEdits = RequireSealedCount(full, "matched_pair_edit_count");
        long deletedEdits = RequireSealedCount(full, "unmatched_truth_deletion_edit_count");
        long insertedEdits = RequireSealedCount(full, "unmatched_prediction_insertion_edit_count");
        long errors = RequireSealedCount(full, "character_error_count");
        if (exact > recognized.TruePositive || roleCorrect > recognized.TruePositive || characters < raw.Truth ||
            deletedEdits < recognized.FalseNegative || (decimal)matchedEdits + deletedEdits + insertedEdits != errors)
            throw new InvalidDataException("Synthetic-sealed recognition or character inventory is inconsistent.");
        double recognition = Ratio(exact, raw.Truth);
        double roleAccuracy = Ratio(roleCorrect, raw.Truth);
        double characterError = (double)errors / characters;
        RequireMetric(full, "recognition_exact_accuracy", recognition, label);
        RequireMetric(full, "role_accuracy", roleAccuracy, label);
        RequireMetric(full, "character_error_rate", characterError, label);

        JsonElement roles = RequireObject(full, "by_expected_runtime_role", label);
        string[] names = [ "annotation", "axistitle", "legendtext", "participant", "phaseheading", "xtick", "ytick" ];
        RequireSealedFields(roles, label, names);
        long roleTruthTotal = 0;
        long roleCorrectTotal = 0;
        foreach (string name in names)
        {
            JsonElement row = RequireObject(roles, name, label);
            RequireSealedFields(row, label, "truth_count", "correct_count", "accuracy");
            int truth = RequirePositiveInt32(row, "truth_count", label);
            int correct = RequireNonNegativeInt32(row, "correct_count", label);
            if (correct > truth)
                throw new InvalidDataException("Synthetic-sealed per-role counts are inconsistent.");
            RequireMetric(row, "accuracy", Ratio(correct, truth), label);
            roleTruthTotal += truth;
            roleCorrectTotal += correct;
        }
        if (roleTruthTotal != raw.Truth || roleCorrectTotal != roleCorrect)
            throw new InvalidDataException("Synthetic-sealed per-role totals differ from full OCR totals.");
        if (raw.Precision < bars.TextPrecisionMinimum || raw.Recall < bars.TextRecallMinimum ||
            recognition < bars.RecognitionMinimum || characterError > bars.CharacterErrorMaximum ||
            roleAccuracy < bars.RoleMinimum)
            throw new InvalidDataException("Full OCR synthetic-sealed canonical acceptance gate failed.");
    }

    private static void ValidateSealedTransport(JsonElement transport)
    {
        const string label = "full OCR synthetic-sealed transport";
        RequireSealedFields(transport, label, "stderr_sha256", "stderr_byte_count", "elapsed_seconds");
        int count = RequireNonNegativeInt32(transport, "stderr_byte_count", label);
        string digest = ValidateSha(RequireText(transport, "stderr_sha256", label), label);
        // This is the producer's complete safe-channel allowlist. Arbitrary
        // stderr cannot inherit aggregate-only classification from a flag.
        string[] safe = [ "", "ORIGINAL_DB_SEALED_WORKER_CANCELLED\n", "ORIGINAL_DB_SEALED_WORKER_CANCELLED\r\n",
            "ORIGINAL_DB_SEALED_WORKER_FAILED\n", "ORIGINAL_DB_SEALED_WORKER_FAILED\r\n" ];
        if (!safe.Select(Encoding.UTF8.GetBytes).Any(bytes => bytes.Length == count &&
                Convert.ToHexStringLower(SHA256.HashData(bytes)) == digest) ||
            RequireFinite(transport, "elapsed_seconds", label) < 0)
            throw new InvalidDataException("Synthetic-sealed transport is not verified aggregate-only output.");
    }

    private static long RequireSealedCount(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long result) || result < 0)
            throw new InvalidDataException("Synthetic-sealed character count must be a nonnegative int64.");
        return result;
    }

    private static void RequireSealedFields(JsonElement value, string label, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !new HashSet<string>(value.EnumerateObject().Select(static item => item.Name), StringComparer.Ordinal)
                .SetEquals(names))
            throw new InvalidDataException($"{label} contains missing or unsupported fields.");
    }
}
