// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphReader.Evidence;

namespace GraphReader.RealAcceptance.Ocr;

internal static class ComposedOcrDevEvidenceSelfTest
{
    private static readonly string Execution = new('a', 64);
    private static readonly string Request = new('b', 64);
    private static readonly string Candidate = new('c', 64);

    internal static object Run()
    {
        var checks = new List<string>();
        JsonObject valid = Report();
        _ = Validate(valid);
        checks.Add("native_aggregate_counts_are_recomputed_without_admission");
        var changes = new (string Name, Action<JsonObject> Change)[]
        {
            ("old_format_requires_replay", d => d["schema"] = "graphreader.composed-ocr-memory-dev-result.v1"),
            ("failed_run", d => d["status"] = "failed"),
            ("execution_drift", d => d["execution_sha256"] = new string('d', 64)),
            ("input_drift", d => d["request_sha256"] = new string('d', 64)),
            ("candidate_drift", d => d["candidate_sha256"] = new string('d', 64)),
            ("private_access", d => d["private_reads"] = 1),
            ("sealed_access", d => d["sealed_reads"] = 1),
            ("case_output", d => d["case_output"] = true),
            ("hidden_case_field", d => d["cases"] = new JsonArray()),
            ("truth_used_by_inference", d => d["truth_consumed_by_inference"] = true),
            ("no_model_execution", d => d["model_inference"] = false),
            ("approval_claim", d => d["production_approved"] = true),
            ("admission_claim", d => d["stage_admission_granted"] = true),
            ("missing_development_split", d => d["metrics"]!.AsObject().Remove("validation")),
            ("sealed_split", d => d["metrics"]!["sealed"] = d["metrics"]!["validation"]!.DeepClone()),
            ("source_count", d => d["sources"] = 2),
            ("panel_count", d => d["metrics"]!["validation"]!["panel_count"] = 0),
            ("negative_failure_count", d => Metrics(d)["recognition_failed_region_count"] = -1),
            ("fractional_count", d => Metrics(d)["assembled_geometry"]!["true_positives"] = 2.5),
            ("negative_count", d => Metrics(d)["assembled_geometry"]!["false_positives"] = -1),
            ("overflow_count", d => Metrics(d)["assembled_geometry"]!["false_positives"] = long.MaxValue),
            ("false_precision", d => Metrics(d)["assembled_geometry"]!["precision"] = 1.0),
            ("changed_iou", d => Metrics(d)["raw_detector_geometry"]!["intersection_over_union_minimum"] = 0.1),
            ("full_geometry_mismatch", d => Full(d)["geometry_matched_region_count"] = 4),
            ("exact_above_matches", d => Full(d)["recognition_exact_count"] = 4),
            ("false_exact_rate", d => Full(d)["recognition_exact_accuracy"] = 1.0),
            ("false_role_rate", d => Full(d)["role_accuracy"] = 1.0),
            ("false_character_rate", d => Full(d)["character_error_rate"] = 0),
            ("missing_errors", d => Full(d)["character_error_count"] = 0),
            ("exact_text_with_edit_errors", d => { Full(d)["recognition_exact_count"] = 3; Full(d)["recognition_exact_accuracy"] = 0.75; }),
            ("missing_role_count", d => Full(d)["by_expected_runtime_role"]!["annotation"]!["truth_count"] = 3),
            ("negative_duration", d => d["elapsed_milliseconds"] = -1),
        };
        foreach ((string name, Action<JsonObject> change) in changes)
        {
            JsonObject altered = (JsonObject)valid.DeepClone(); change(altered);
            Reject(() => Validate(altered)); checks.Add(name + "_rejected");
        }
        string repeated = valid.ToJsonString().Replace("\"status\":\"completed\"", "\"status\":\"completed\",\"status\":\"completed\"", StringComparison.Ordinal);
        Reject(() => ComposedOcrDevEvidence.Validate(Encoding.UTF8.GetBytes(repeated), Execution, Request, Candidate));
        checks.Add("duplicate_json_property_rejected");

        var files = ComposedOcrExecutionIdentity.RequiredFiles.ToDictionary(static name => name, _ => Execution, StringComparer.Ordinal);
        string Identity(IReadOnlyDictionary<string, string> items, string detector = "", int threads = 1) =>
            ComposedOcrExecutionIdentity.Create("fixture-composition", "fixture-axis", detector.Length == 0 ? Execution : detector,
                Request, Candidate, Request, "cpu", "disabled", threads, 1, 1, 1, items);
        string baseline = Identity(files);
        if (baseline != Identity(files.Reverse().ToDictionary(static x => x.Key, static x => x.Value))) throw new InvalidDataException("File order changed identity");
        foreach (string name in ComposedOcrExecutionIdentity.RequiredFiles)
        {
            var changed = new Dictionary<string, string>(files, StringComparer.Ordinal) { [name] = Request };
            if (Identity(changed) == baseline) throw new InvalidDataException("Execution file not bound: " + name);
        }
        if (Identity(files, Request) == baseline || Identity(files, threads: 2) == baseline) throw new InvalidDataException("Model or thread configuration not bound");
        var missing = new Dictionary<string, string>(files, StringComparer.Ordinal);
        missing.Remove("GraphReader.Axis.dll");
        Reject(() => Identity(missing));
        checks.Add("all_shared_files_models_and_cpu_settings_bound_order_independently");
        return new { status = "pass", scope = "fictitious-self-test-only", checks, execution_files = files.Count,
            private_reads = 0, sealed_reads = 0, model_inference = false, stage_admission_granted = false };
    }

    private static JsonElement Validate(JsonObject value) => ComposedOcrDevEvidence.Validate(
        Encoding.UTF8.GetBytes(value.ToJsonString()), Execution, Request, Candidate);

    private static JsonNode Metrics(JsonObject value) => value["metrics"]!["validation"]!["metrics"]!;
    private static JsonNode Full(JsonObject value) => Metrics(value)["full_ocr_metrics"]!;

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("Invalid evidence was accepted");
    }

    internal static JsonObject Report()
    {
        object geometry = new { truth_region_count = 4, predicted_region_count = 4, true_positives = 3,
            false_positives = 1, false_negatives = 1, precision = 0.75, recall = 0.75, intersection_over_union_minimum = 0.5 };
        object full = new { truth_region_count = 4, predicted_region_count = 4, geometry_matched_region_count = 3,
            geometry_false_positive_count = 1, geometry_false_negative_count = 1, recognition_exact_count = 2,
            recognition_exact_accuracy = 0.5, truth_character_count = 10, matched_pair_edit_count = 1,
            unmatched_truth_deletion_edit_count = 2, unmatched_prediction_insertion_edit_count = 2,
            character_error_count = 5, character_error_rate = 0.5, role_correct_count = 3, role_accuracy = 0.75,
            by_expected_runtime_role = new { annotation = new { truth_count = 4, correct_count = 3, accuracy = 0.75 } },
            intersection_over_union_minimum = 0.5 };
        return JsonSerializer.SerializeToNode(new
        {
            schema = ComposedOcrDevEvidence.Schema, status = "completed", sources = 1,
            metrics = new { validation = new { source_count = 1, panel_count = 1,
                metrics = new { source_count = 1, raw_detector_geometry = geometry, assembled_geometry = geometry,
                    full_ocr_metrics = full, recognition_failed_region_count = 0 } } },
            elapsed_milliseconds = 1.0, request_sha256 = Request, candidate_sha256 = Candidate,
            execution_sha256 = Execution, model_inference = true, truth_consumed_by_inference = false,
            case_output = false, private_reads = 0, sealed_reads = 0, stage_admission_granted = false, production_approved = false,
        })!.AsObject();
    }
}
