// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;
using GraphReader.Evidence;

namespace GraphReader.RealAcceptance.Ocr;

/// <summary>
/// Checks native development evidence against the actual workflow runtime.
/// This is compatibility validation, not stage admission or model approval.
/// </summary>
internal static class ComposedOcrDevEvidence
{
    internal const string Schema = "graphreader.composed-ocr-memory-dev-result.v2";

    internal static int RunCommand(string[] args)
    {
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(Run(args)));
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("COMPOSED_OCR_DEV_COMPATIBILITY_FAILED:" + exception.GetType().Name);
            return 1;
        }
    }

    internal static object Run(string[] args)
    {
        if (args.Length != 7) Fail("ARGUMENTS");
        string root = Directory.GetCurrentDirectory();
        FrozenCandidateBinding binding = FrozenCandidateBinding.Load(root, args[3], args[4], CancellationToken.None);
        string path = FrozenCandidateBinding.RequireUnderRoot(root, args[1], "composed OCR development report");
        if (new FileInfo(path).Length is <= 0 or > 16 * 1024 * 1024) Fail("SIZE");
        byte[] bytes = File.ReadAllBytes(path);
        if (FrozenCandidateBinding.Hash(bytes) != FrozenCandidateBinding.RequireSha256(args[2], "report hash")) Fail("CHECKSUM");
        string execution = ExecutionIdentity(binding);
        JsonElement development = Validate(bytes, execution, args[5], args[6]);
        return new
        {
            status = "compatible_development_evidence", scope = "owned-synthetic-development-runtime-compatibility",
            execution_sha256 = execution, development,
            accuracy_gate_evaluated = false, stage_admission_granted = false, production_approved = false,
            private_reads = 0, sealed_reads = 0, model_inference = false,
        };
    }

    internal static string ExecutionIdentity(FrozenCandidateBinding binding) =>
        ComposedOcrExecutionIdentity.Create(binding.Algorithms.OcrCompositionVersion,
            binding.Algorithms.AxisStageVersion, binding.OcrDetection.Payload.Sha256,
            binding.OcrDetection.Manifest.Sha256, binding.OcrRecognition.Payload.Sha256,
            binding.OcrRecognition.Manifest.Sha256, binding.Runtime.ExecutionProvider,
            binding.Runtime.GraphOptimization, binding.Runtime.IntraOperationThreads,
            binding.Runtime.InterOperationThreads, binding.Runtime.WorkerCount,
            binding.Runtime.QueueCapacity, binding.ManagedFiles.Concat(binding.NativeFiles)
                .ToDictionary(static file => Path.GetFileName(file.RelativePath), static file => file.Sha256,
                    StringComparer.Ordinal));

    internal static JsonElement Validate(byte[] bytes, string expectedExecutionIdentity,
        string expectedRequestSha256, string expectedStageCandidateSha256)
    {
        if (bytes.Length is <= 0 or > 16 * 1024 * 1024) Fail("SIZE");
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement report = document.RootElement;
        RejectDuplicateProperties(report);
        string[] fields = ["schema", "status", "sources", "metrics", "elapsed_milliseconds", "request_sha256",
            "candidate_sha256", "execution_sha256", "model_inference", "truth_consumed_by_inference", "case_output",
            "private_reads", "sealed_reads", "stage_admission_granted", "production_approved"];
        RequireFields(report, fields);
        if (report.GetProperty("schema").GetString() != Schema || report.GetProperty("status").GetString() != "completed" ||
            !report.GetProperty("model_inference").GetBoolean() || report.GetProperty("truth_consumed_by_inference").GetBoolean() ||
            report.GetProperty("case_output").GetBoolean() || report.GetProperty("private_reads").GetInt32() != 0 ||
            report.GetProperty("sealed_reads").GetInt32() != 0 || report.GetProperty("stage_admission_granted").GetBoolean() ||
            report.GetProperty("production_approved").GetBoolean()) Fail("SCOPE");
        foreach ((string field, string expected) in new[] { ("execution_sha256", expectedExecutionIdentity),
                     ("request_sha256", expectedRequestSha256), ("candidate_sha256", expectedStageCandidateSha256) })
        {
            _ = FrozenCandidateBinding.RequireSha256(expected, field);
            if (report.GetProperty(field).GetString() != expected) Fail("IDENTITY");
        }
        _ = Number(report, "elapsed_milliseconds");
        long sources = Count(report, "sources");
        if (sources == 0) Fail("COUNTS");
        JsonElement splits = report.GetProperty("metrics");
        if (splits.ValueKind != JsonValueKind.Object || !splits.TryGetProperty("validation", out _)) Fail("SPLIT");
        long observed = 0;
        foreach (JsonProperty split in splits.EnumerateObject())
        {
            if (split.Name is not ("train" or "validation")) Fail("SPLIT");
            JsonElement corpus = split.Value;
            RequireFields(corpus, ["source_count", "panel_count", "metrics"]);
            long count = Count(corpus, "source_count");
            if (count == 0 || Count(corpus, "panel_count") < count) Fail("COUNTS");
            observed = checked(observed + count);
            JsonElement metrics = corpus.GetProperty("metrics");
            RequireFields(metrics, ["source_count", "raw_detector_geometry", "assembled_geometry", "full_ocr_metrics", "recognition_failed_region_count"]);
            if (Count(metrics, "source_count") != count) Fail("COUNTS");
            _ = Count(metrics, "recognition_failed_region_count");
            ValidateGeometry(metrics.GetProperty("raw_detector_geometry"));
            ValidateGeometry(metrics.GetProperty("assembled_geometry"));
            ValidateFull(metrics.GetProperty("full_ocr_metrics"), metrics.GetProperty("assembled_geometry"));
            if (Count(metrics.GetProperty("raw_detector_geometry"), "truth_region_count") !=
                Count(metrics.GetProperty("assembled_geometry"), "truth_region_count")) Fail("COUNTS");
        }
        if (sources != observed) Fail("COUNTS");
        return splits.GetProperty("validation").Clone();
    }

    private static void ValidateGeometry(JsonElement metrics)
    {
        RequireFields(metrics, ["truth_region_count", "predicted_region_count", "true_positives", "false_positives",
            "false_negatives", "precision", "recall", "intersection_over_union_minimum"]);
        long truth = Count(metrics, "truth_region_count"), predicted = Count(metrics, "predicted_region_count"),
            tp = Count(metrics, "true_positives"), fp = Count(metrics, "false_positives"), fn = Count(metrics, "false_negatives");
        if (truth <= 0 || checked(tp + fn) != truth || checked(tp + fp) != predicted) Fail("COUNTS");
        Equal(Number(metrics, "precision"), predicted == 0 ? 0 : (double)tp / predicted);
        Equal(Number(metrics, "recall"), (double)tp / truth);
        Equal(Number(metrics, "intersection_over_union_minimum"), 0.5);
    }

    private static void ValidateFull(JsonElement full, JsonElement geometry)
    {
        RequireFields(full, ["truth_region_count", "predicted_region_count", "geometry_matched_region_count",
            "geometry_false_positive_count", "geometry_false_negative_count", "recognition_exact_count",
            "recognition_exact_accuracy", "truth_character_count", "matched_pair_edit_count",
            "unmatched_truth_deletion_edit_count", "unmatched_prediction_insertion_edit_count", "character_error_count",
            "character_error_rate", "role_correct_count", "role_accuracy", "by_expected_runtime_role", "intersection_over_union_minimum"]);
        foreach ((string first, string second) in new[] { ("truth_region_count", "truth_region_count"),
            ("predicted_region_count", "predicted_region_count"), ("geometry_matched_region_count", "true_positives"),
            ("geometry_false_positive_count", "false_positives"), ("geometry_false_negative_count", "false_negatives") })
            if (Count(full, first) != Count(geometry, second)) Fail("COUNTS");
        long truth = Count(full, "truth_region_count"), matches = Count(full, "geometry_matched_region_count"),
            exact = Count(full, "recognition_exact_count"), roles = Count(full, "role_correct_count"),
            characters = Count(full, "truth_character_count"), edits = Count(full, "character_error_count");
        if (exact > matches || roles > matches || characters == 0 || edits != checked(Count(full, "matched_pair_edit_count") +
            Count(full, "unmatched_truth_deletion_edit_count") + Count(full, "unmatched_prediction_insertion_edit_count"))) Fail("COUNTS");
        long matchedEdits = Count(full, "matched_pair_edit_count");
        if (matchedEdits < matches - exact || (exact == matches && matchedEdits != 0) ||
            (Count(full, "geometry_false_negative_count") == 0 && Count(full, "unmatched_truth_deletion_edit_count") != 0) ||
            (Count(full, "geometry_false_positive_count") == 0 && Count(full, "unmatched_prediction_insertion_edit_count") != 0)) Fail("COUNTS");
        Equal(Number(full, "recognition_exact_accuracy"), (double)exact / truth);
        Equal(Number(full, "role_accuracy"), (double)roles / truth);
        Equal(Number(full, "character_error_rate"), (double)edits / characters);
        Equal(Number(full, "intersection_over_union_minimum"), 0.5);
        long roleTruth = 0, roleCorrect = 0;
        foreach (JsonProperty role in full.GetProperty("by_expected_runtime_role").EnumerateObject())
        {
            if (role.Name is not ("annotation" or "axistitle" or "legendtext" or "participant" or "phaseheading" or "xtick" or "ytick" or "unknown")) Fail("ROLE");
            RequireFields(role.Value, ["truth_count", "correct_count", "accuracy"]);
            long total = Count(role.Value, "truth_count"), correct = Count(role.Value, "correct_count");
            if (total <= 0 || correct > total) Fail("COUNTS");
            Equal(Number(role.Value, "accuracy"), (double)correct / total);
            roleTruth = checked(roleTruth + total); roleCorrect = checked(roleCorrect + correct);
        }
        if (roleTruth != truth || roleCorrect != roles) Fail("COUNTS");
    }

    private static long Count(JsonElement value, string name)
    {
        if (!value.GetProperty(name).TryGetInt64(out long count) || count < 0 || count > int.MaxValue) Fail("COUNTS");
        return count;
    }

    private static double Number(JsonElement value, string name)
    {
        if (!value.GetProperty(name).TryGetDouble(out double number) || !double.IsFinite(number) || number < 0) Fail("NUMBER");
        return number;
    }

    private static void Equal(double observed, double expected)
    {
        if (Math.Abs(observed - expected) > 1e-12) Fail("METRIC");
    }

    private static void RequireFields(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(static p => p.Name)
                .Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal))) Fail("SHAPE");
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) Fail("DUPLICATE");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void Fail(string code) => throw new InvalidDataException("COMPOSED_OCR_DEV_EVIDENCE_" + code + "_INVALID");
}
