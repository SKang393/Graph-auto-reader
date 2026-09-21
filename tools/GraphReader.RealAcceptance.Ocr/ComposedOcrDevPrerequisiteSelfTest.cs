// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GraphReader.RealAcceptance.Ocr;

internal static class ComposedOcrDevPrerequisiteSelfTest
{
    internal static IReadOnlyList<string> Run(string root, string policySha, string barsSha,
        FrozenRealWorkflowCandidateIdentity workflow)
    {
        Directory.CreateDirectory(Path.Combine(root, "composed-dev-fixtures"));
        string execution = new('a', 64), request = new('b', 64), stage = new('c', 64);
        FrozenRealWorkflowCandidateIdentity candidate = workflow with { ComposedOcrExecutionSha256 = execution };
        var checks = new List<string>();
        JsonObject report = PassingReport();
        JsonObject envelope = JsonSerializer.SerializeToNode(new
        {
            schema = FrozenRealWorkflowAdmission.ComposedOcrDevPrerequisiteSchema,
            task = "ocr-detection-recognition", split = "synthetic-dev",
            stage_revision = "fictitious-native-composed-ocr", stage_candidate_id = "fictitious-candidate",
            evidence_policy_sha256 = policySha, acceptance_bar_sha256 = barsSha,
            runtime_composition_sha256 = candidate.ExecutionDescriptorSha256,
            operating_point_identity = candidate.OperatingPointIdentity,
            models = new { ocr_detection_sha256 = candidate.OcrDetectionSha256,
                ocr_recognition_sha256 = candidate.OcrRecognitionSha256, marker_center_sha256 = (string?)null },
            source_result = new { path = "replaced-by-fixture-writer", sha256 = execution },
            source_identity = new { request_sha256 = request, candidate_sha256 = stage },
            benchmarks = new[] { new { name = "full-source", truth_count = 20, true_positive = 19, false_positive = 1,
                false_negative = 1, precision = 0.95, recall = 0.95, recognition_exact_match = 0.95,
                character_error_rate = 0.05, role_accuracy = 0.95, prohibited_structure_hit_rate = (double?)null } },
            parity = new { kind = "identical-native-execution", execution_sha256 = execution },
            aggregate_only = true, case_level_output = false, truth_rows_output = false, prediction_output = false, pixel_output = false,
        })!.AsObject();

        void Validate(string name, JsonObject source, JsonObject claim, FrozenRealWorkflowCandidateIdentity expected,
            string split = "synthetic-dev")
        {
            string sourcePath = "composed-dev-fixtures/" + name + "-source.json";
            byte[] sourceBytes = JsonSerializer.SerializeToUtf8Bytes(source);
            File.WriteAllBytes(Path.Combine(root, sourcePath), sourceBytes);
            claim["source_result"] = JsonSerializer.SerializeToNode(new { path = sourcePath, sha256 = FrozenCandidateBinding.Hash(sourceBytes) });
            string claimPath = "composed-dev-fixtures/" + name + "-claim.json";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(claim);
            File.WriteAllBytes(Path.Combine(root, claimPath), bytes);
            FrozenRealWorkflowAdmission.ValidatePrerequisiteForTest(root, claimPath, FrozenCandidateBinding.Hash(bytes),
                "ocr-detection-recognition", split, expected, policySha, barsSha, CancellationToken.None);
        }

        Validate("at-bar", report, envelope, candidate);
        checks.Add("composed_dev_authentic_source_accepted_at_existing_five_bars");
        void Reject(string name, Action<JsonObject, JsonObject> change, string? code = null,
            FrozenRealWorkflowCandidateIdentity? expected = null, string split = "synthetic-dev")
        {
            JsonObject source = (JsonObject)report.DeepClone(), claim = (JsonObject)envelope.DeepClone();
            change(source, claim);
            try { Validate(name, source, claim, expected ?? candidate, split); }
            catch (InvalidDataException failure) when (code is null || failure.Message == code)
            {
                checks.Add("composed_dev_" + name + "_rejected"); return;
            }
            throw new InvalidDataException("Composed dev prerequisite self-test failed: " + name);
        }

        Reject("failing_exact", (s, c) => { Full(s)["recognition_exact_count"] = 18; Full(s)["recognition_exact_accuracy"] = 0.9;
            Full(s)["matched_pair_edit_count"] = 1; Full(s)["unmatched_prediction_insertion_edit_count"] = 2; Benchmark(c)["recognition_exact_match"] = 0.9; },
            "REAL_WORKFLOW_OCR_PREREQUISITE_GATE_FAILED");
        Reject("hidden_failing_exact", (s, _) => { Full(s)["recognition_exact_count"] = 18; Full(s)["recognition_exact_accuracy"] = 0.9;
            Full(s)["matched_pair_edit_count"] = 1; Full(s)["unmatched_prediction_insertion_edit_count"] = 2; },
            "REAL_WORKFLOW_COMPOSED_OCR_SOURCE_METRIC_MISMATCH");
        Reject("failing_cer", (s, c) => { Full(s)["unmatched_prediction_insertion_edit_count"] = 4; Full(s)["character_error_count"] = 6; Full(s)["character_error_rate"] = 0.06; Benchmark(c)["character_error_rate"] = 0.06; },
            "REAL_WORKFLOW_OCR_PREREQUISITE_GATE_FAILED");
        Reject("failing_roles", (s, c) => { Full(s)["role_correct_count"] = 18; Full(s)["role_accuracy"] = 0.9;
            Full(s)["by_expected_runtime_role"]!["annotation"]!["correct_count"] = 18;
            Full(s)["by_expected_runtime_role"]!["annotation"]!["accuracy"] = 0.9; Benchmark(c)["role_accuracy"] = 0.9; },
            "REAL_WORKFLOW_OCR_PREREQUISITE_GATE_FAILED");
        Reject("failing_precision", (s, c) => { Geometry(s)["predicted_region_count"] = 21; Geometry(s)["false_positives"] = 2;
            Geometry(s)["precision"] = 19.0/21; Full(s)["predicted_region_count"] = 21; Full(s)["geometry_false_positive_count"] = 2;
            Benchmark(c)["false_positive"] = 2; Benchmark(c)["precision"] = 19.0/21; }, "REAL_WORKFLOW_PREREQUISITE_GATE_FAILED");
        Reject("failing_recall", (s, c) => { Geometry(s)["true_positives"] = 18; Geometry(s)["predicted_region_count"] = 18;
            Geometry(s)["false_negatives"] = 2; Geometry(s)["false_positives"] = 0; Geometry(s)["precision"] = 1; Geometry(s)["recall"] = 0.9;
            Full(s)["geometry_matched_region_count"] = 18; Full(s)["predicted_region_count"] = 18; Full(s)["geometry_false_negative_count"] = 2;
            Full(s)["geometry_false_positive_count"] = 0; Full(s)["unmatched_truth_deletion_edit_count"] = 5; Full(s)["unmatched_prediction_insertion_edit_count"] = 0;
            Full(s)["recognition_exact_count"] = 18; Full(s)["recognition_exact_accuracy"] = 0.9;
            Full(s)["role_correct_count"] = 18; Full(s)["role_accuracy"] = 0.9;
            Full(s)["by_expected_runtime_role"]!["annotation"]!["correct_count"] = 18;
            Full(s)["by_expected_runtime_role"]!["annotation"]!["accuracy"] = 0.9;
            Benchmark(c)["true_positive"] = 18; Benchmark(c)["false_negative"] = 2; Benchmark(c)["false_positive"] = 0; Benchmark(c)["precision"] = 1; Benchmark(c)["recall"] = 0.9;
            Benchmark(c)["recognition_exact_match"] = 0.9; Benchmark(c)["role_accuracy"] = 0.9; }, "REAL_WORKFLOW_PREREQUISITE_GATE_FAILED");
        Reject("fabricated_zero_attribution", (_, c) => Benchmark(c)["prohibited_structure_hit_rate"] = 0.0,
            "REAL_WORKFLOW_COMPOSED_OCR_UNMEASURED_METRIC_INVALID");
        Reject("model_drift", (_, c) => c["models"]!["ocr_detection_sha256"] = new string('9', 64));
        Reject("input_drift", (_, c) => c["source_identity"]!["request_sha256"] = new string('9', 64));
        Reject("stage_descriptor_drift", (_, c) => c["source_identity"]!["candidate_sha256"] = new string('9', 64));
        Reject("runtime_drift", (_, c) => c["runtime_composition_sha256"] = new string('9', 64));
        Reject("missing_runtime_identity", (_, _) => { }, expected: candidate with { ComposedOcrExecutionSha256 = null });
        Reject("native_parity_drift", (_, c) => c["parity"]!["execution_sha256"] = new string('9', 64));
        Reject("fictitious_numerical_parity", (_, c) => c["parity"] = JsonSerializer.SerializeToNode(new { passed = true, max_absolute_error = 0, tolerance = 0 }));
        Reject("sealed_substitution", (_, c) => c["split"] = "synthetic-sealed", split: "synthetic-sealed");
        Reject("case_disclosure", (s, _) => s["case_output"] = true);
        Reject("unbound_report_field", (s, _) => s["case_identifiers"] = new JsonArray());
        Reject("sealed_read_in_dev", (s, _) => s["sealed_reads"] = 1);

        string validSource = "composed-dev-fixtures/at-bar-source.json";
        string validSourceSha = FrozenCandidateBinding.Hash(File.ReadAllBytes(Path.Combine(root, validSource)));
        const string output = "artifacts/composed-dev-prerequisite.json";
        _ = FrozenRealWorkflowAdmission.WriteComposedOcrDevPrerequisite(root, validSource, validSourceSha,
            candidate, request, stage, output, CancellationToken.None);
        byte[] written = File.ReadAllBytes(Path.Combine(root, output));
        FrozenRealWorkflowAdmission.ValidatePrerequisiteForTest(root, output, FrozenCandidateBinding.Hash(written),
            "ocr-detection-recognition", "synthetic-dev", candidate, policySha, barsSha, CancellationToken.None);
        checks.Add("composed_dev_writer_creates_verifiable_prerequisite_from_source_counts");
        try
        {
            _ = FrozenRealWorkflowAdmission.WriteComposedOcrDevPrerequisite(root, validSource, validSourceSha,
                candidate, request, stage, output, CancellationToken.None);
            throw new InvalidDataException("Writer replaced an existing prerequisite");
        }
        catch (IOException) { }
        if (!written.SequenceEqual(File.ReadAllBytes(Path.Combine(root, output))) ||
            Directory.GetFiles(Path.Combine(root, "artifacts"), "*.tmp").Length != 0)
            throw new InvalidDataException("Writer changed existing evidence or left temporary output");
        checks.Add("composed_dev_writer_preserves_existing_evidence_and_removes_own_temporary_file");

        string failingSource = "composed-dev-fixtures/failing_exact-source.json";
        string failingSourceSha = FrozenCandidateBinding.Hash(File.ReadAllBytes(Path.Combine(root, failingSource)));
        bool rejected = false;
        try
        {
            _ = FrozenRealWorkflowAdmission.WriteComposedOcrDevPrerequisite(root, failingSource, failingSourceSha,
                candidate, request, stage, "artifacts/must-not-exist.json", CancellationToken.None);
        }
        catch (InvalidDataException failure) when (failure.Message == "REAL_WORKFLOW_OCR_PREREQUISITE_GATE_FAILED") { rejected = true; }
        if (!rejected || File.Exists(Path.Combine(root, "artifacts/must-not-exist.json")))
            throw new InvalidDataException("Writer emitted a failing prerequisite");
        checks.Add("composed_dev_writer_emits_nothing_for_failing_native_metrics");
        return checks;
    }

    private static JsonNode Metrics(JsonObject source) => source["metrics"]!["validation"]!["metrics"]!;
    private static JsonNode Geometry(JsonObject source) => Metrics(source)["assembled_geometry"]!;
    private static JsonNode Full(JsonObject source) => Metrics(source)["full_ocr_metrics"]!;
    private static JsonNode Benchmark(JsonObject claim) => claim["benchmarks"]![0]!;

    private static JsonObject PassingReport()
    {
        JsonObject source = ComposedOcrDevEvidenceSelfTest.Report();
        foreach (string name in new[] { "raw_detector_geometry", "assembled_geometry" })
        {
            JsonNode geometry = Metrics(source)[name]!;
            geometry["truth_region_count"] = 20; geometry["predicted_region_count"] = 20;
            geometry["true_positives"] = 19; geometry["precision"] = 0.95; geometry["recall"] = 0.95;
        }
        JsonNode full = Full(source);
        full["truth_region_count"] = 20; full["predicted_region_count"] = 20; full["geometry_matched_region_count"] = 19;
        full["recognition_exact_count"] = 19; full["recognition_exact_accuracy"] = 0.95;
        full["matched_pair_edit_count"] = 0; full["unmatched_prediction_insertion_edit_count"] = 3;
        full["truth_character_count"] = 100; full["character_error_rate"] = 0.05;
        full["role_correct_count"] = 19; full["role_accuracy"] = 0.95;
        full["by_expected_runtime_role"]!["annotation"]!["truth_count"] = 20;
        full["by_expected_runtime_role"]!["annotation"]!["correct_count"] = 19;
        full["by_expected_runtime_role"]!["annotation"]!["accuracy"] = 0.95;
        return source;
    }
}
