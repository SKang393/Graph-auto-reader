// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;

namespace GraphReader.RealAcceptance.Ocr;

internal static partial class FrozenRealWorkflowAdmission
{
    internal const string ComposedOcrDevPrerequisiteSchema =
        "graphreader.composed-ocr-dev-prerequisite.v2";
    private static readonly string[] ComposedOcrSourceIdentityFields = ["source_identity"];

    private static void ValidateComposedOcrDevPrerequisite(
        byte[] sourceBytes, FrozenRealWorkflowCandidateIdentity candidate,
        JsonElement envelope, JsonElement benchmarks, JsonElement parity, CanonicalBars bars)
    {
        string execution = candidate.ComposedOcrExecutionSha256 ??
            throw new InvalidDataException("REAL_WORKFLOW_COMPOSED_OCR_EXECUTION_IDENTITY_REQUIRED");
        JsonElement identity = envelope.GetProperty("source_identity");
        RequireProperties(identity, ["request_sha256", "candidate_sha256"], "composed OCR source identity");
        JsonElement development = ComposedOcrDevEvidence.Validate(sourceBytes, execution,
            Sha(identity, "request_sha256"), Sha(identity, "candidate_sha256"));

        // Both sides executed the same ONNX models through identical native
        // processing code. This claims execution identity, not a fabricated
        // numerical comparison with the original training framework.
        RequireProperties(parity, ["kind", "execution_sha256"], "composed OCR execution parity");
        if (Text(parity, "kind") != "identical-native-execution" || Sha(parity, "execution_sha256") != execution)
            throw new InvalidDataException("REAL_WORKFLOW_COMPOSED_OCR_EXECUTION_PARITY_INVALID");

        ValidateBenchmarks(benchmarks, "ocr-detection-recognition", "synthetic-dev", bars, composedOcrSource: true);
        JsonElement claimed = FindBenchmark(benchmarks, "full-source");
        JsonElement metrics = development.GetProperty("metrics");
        JsonElement geometry = metrics.GetProperty("assembled_geometry");
        JsonElement full = metrics.GetProperty("full_ocr_metrics");
        foreach ((string from, string to) in new[]
        {
            ("truth_region_count", "truth_count"), ("true_positives", "true_positive"),
            ("false_positives", "false_positive"), ("false_negatives", "false_negative"),
        })
            if (Integer(geometry, from) != Integer(claimed, to))
                throw new InvalidDataException("REAL_WORKFLOW_COMPOSED_OCR_SOURCE_METRIC_MISMATCH");
        foreach ((JsonElement source, string from, string to) in new[]
        {
            (geometry, "precision", "precision"), (geometry, "recall", "recall"),
            (full, "recognition_exact_accuracy", "recognition_exact_match"),
            (full, "character_error_rate", "character_error_rate"), (full, "role_accuracy", "role_accuracy"),
        })
            if (Math.Abs(Number(source, from) - Number(claimed, to)) > NumericTolerance)
                throw new InvalidDataException("REAL_WORKFLOW_COMPOSED_OCR_SOURCE_METRIC_MISMATCH");
    }
}
