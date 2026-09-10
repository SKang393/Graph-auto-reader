// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionOriginalDbRealSealedEvidenceTests
{
    private static readonly ModelIdentity Detection = new(
        "future-approved-original-db-detector", "1", new string('a', 64), "detector.onnx");
    private static readonly ModelIdentity Recognition = new(
        "future-approved-recognizer", "1", new string('b', 64), "recognizer.onnx");

    [TestMethod]
    public void ExactPortableWorkerEnvelopePasses()
    {
        EvidenceFixture fixture = EvidenceFixture.Create();

        Validate(fixture);
    }

    [TestMethod]
    public void CandidateAndProtocolMustRemainCryptographicallyLinked()
    {
        EvidenceFixture fixture = EvidenceFixture.Create();
        fixture.Worker["candidate_sha256"] = new string('9', 64);
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture.Snapshot()));

        fixture = EvidenceFixture.Create();
        fixture.Algorithms["ocr_composition_version"] = "unapproved-original-db-mode";
        fixture.Rebind();
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture.Snapshot()));

        fixture = EvidenceFixture.Create();
        fixture.SplitIdentities["project_count"] = 50;
        fixture.Rebind();
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture.Snapshot()));
    }

    [TestMethod]
    public void AggregateRejectsPrivacyDisclosureAndIncompleteDenominators()
    {
        EvidenceFixture fixture = EvidenceFixture.Create();
        fixture.Aggregate["paths_output"] = true;
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture.Snapshot()));

        fixture = EvidenceFixture.Create();
        fixture.Aggregate["project_files"] = 50;
        fixture.Aggregate["workflow_succeeded_projects"] = 50;
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture.Snapshot()));

        fixture = EvidenceFixture.Create();
        fixture.Evaluation["output_cases"] = 9;
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture.Snapshot()));
    }

    [TestMethod]
    public void AggregateRejectsTamperedMetricsAndFailedCanonicalBar()
    {
        EvidenceFixture fixture = EvidenceFixture.Create();
        fixture.Evaluation["unique_point_value_precision"] = 0.96;
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture.Snapshot()));

        fixture = EvidenceFixture.Create();
        fixture.Evaluation["unique_point_value_correct"] = 94;
        fixture.Evaluation["unique_point_value_incorrect"] = 1;
        fixture.Evaluation["unique_point_value_precision"] = 0.94;
        fixture.Evaluation["unique_point_value_coverage"] = 0.94;
        fixture.Evaluation["matched_unique_point_value_accuracy"] = 0.94 / 0.95;
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture.Snapshot()));
    }

    [TestMethod]
    public void UnknownFieldsAndMalformedOptionalRatiosFailClosed()
    {
        EvidenceFixture fixture = EvidenceFixture.Create();
        fixture.Worker["case_rows"] = new JsonArray();
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture.Snapshot()));

        fixture = EvidenceFixture.Create();
        fixture.Evaluation["predicted_points"] = 0;
        fixture.Evaluation["matched_points"] = 0;
        fixture.Evaluation["unique_point_value_correct"] = 0;
        fixture.Evaluation["unique_point_value_incorrect"] = 0;
        fixture.Evaluation["unique_point_missing"] = 100;
        fixture.Evaluation["unique_point_extra"] = 0;
        fixture.Evaluation["unique_point_structural_precision"] = 0.0;
        fixture.Evaluation["unique_point_structural_coverage"] = 0.0;
        fixture.Evaluation["unique_point_value_precision"] = 0.0;
        fixture.Evaluation["unique_point_value_coverage"] = 0.0;
        fixture.Evaluation["matched_unique_point_value_accuracy"] = 0.0;
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture.Snapshot()));
    }

    private static void Validate(EvidenceFixture fixture) =>
        ProductionOriginalDbRealSealedEvidence.Validate(
            fixture.WorkerBytes,
            fixture.CandidateBytes,
            fixture.CandidateSha256,
            fixture.ProtocolBytes,
            fixture.ProtocolSha256,
            fixture.BarsBytes,
            fixture.BarsSha256,
            Detection,
            Recognition);

    private sealed class EvidenceFixture
    {
        private EvidenceFixture(JsonObject candidate, JsonObject protocol, JsonObject bars, JsonObject worker)
        {
            Candidate = candidate;
            Protocol = protocol;
            Bars = bars;
            Worker = worker;
        }

        internal JsonObject Candidate { get; }
        internal JsonObject Protocol { get; }
        internal JsonObject Bars { get; }
        internal JsonObject Worker { get; }
        internal JsonObject Algorithms => RequiredObject(Candidate, "algorithms");
        internal JsonObject SplitIdentities => RequiredObject(Protocol, "split_identities");
        internal JsonObject Aggregate => RequiredObject(Worker, "aggregate");
        internal JsonObject Evaluation => RequiredObject(Aggregate, "evaluation");
        internal byte[] CandidateBytes => Serialize(Candidate);
        internal byte[] ProtocolBytes => Serialize(Protocol);
        internal byte[] BarsBytes => Serialize(Bars);
        internal byte[] WorkerBytes => Serialize(Worker);
        internal string CandidateSha256 => Hash(CandidateBytes);
        internal string ProtocolSha256 => Hash(ProtocolBytes);
        internal string BarsSha256 => Hash(BarsBytes);

        internal static EvidenceFixture Create()
        {
            JsonObject bars = new()
            {
                ["schema_version"] = 1,
                ["policy_id"] = "graphreader-goal22-tier1-v1",
                ["tier1_reviewable_error"] = new JsonObject
                {
                    ["marker_center_precision_minimum"] = 0.95,
                    ["marker_center_recall_minimum"] = 0.95,
                },
            };
            JsonObject candidate = CandidateDocument();
            JsonObject protocol = ProtocolDocument(Hash(Serialize(bars)));
            JsonObject worker = WorkerDocument();
            var fixture = new EvidenceFixture(candidate, protocol, bars, worker);
            fixture.Rebind();
            return fixture;
        }

        internal EvidenceFixture Snapshot() => new(
            (JsonObject)Candidate.DeepClone(),
            (JsonObject)Protocol.DeepClone(),
            (JsonObject)Bars.DeepClone(),
            (JsonObject)Worker.DeepClone());

        internal void Rebind()
        {
            SplitIdentities["execution_descriptor_sha256"] = ExecutionDescriptor(Candidate);
            SplitIdentities["operating_point_identity"] = OperatingPoint(
                SplitIdentities["execution_descriptor_sha256"]!.GetValue<string>());
            RequiredObject(Protocol, "acceptance_bar")["sha256"] = BarsSha256;
            string protocolSha = ProtocolSha256;
            RequiredObject(Candidate, "protocol")["sha256"] = protocolSha;
            Worker["candidate_sha256"] = CandidateSha256;
            Worker["protocol_sha256"] = protocolSha;
            Worker["assignment_sha256"] = SplitIdentities["assignment_sha256"]!.GetValue<string>();
            Worker["selected_inventory_sha256"] =
                SplitIdentities["selected_inventory_sha256"]!.GetValue<string>();
        }

        private static JsonObject CandidateDocument() => new()
        {
            ["schema"] = "graphreader.real-acceptance-frozen-candidate.v1",
            ["candidate_id"] = "future-original-db-candidate",
            ["revision"] = "future-original-db-v1",
            ["protocol"] = FileReference("evidence/protocol.json", new string('0', 64)),
            ["runtime"] = new JsonObject
            {
                ["execution_provider"] = "cpu",
                ["graph_optimization"] = "disabled",
                ["intra_operation_threads"] = 1,
                ["inter_operation_threads"] = 1,
                ["queue_capacity"] = 1,
                ["worker_count"] = 1,
            },
            ["ocr_detection"] = Model("ocr_detection", Detection, 'a'),
            ["ocr_recognition"] = Model("ocr_recognition", Recognition, 'b'),
            ["marker_center"] = Model(
                "marker_center", new ModelIdentity("marker", "1", new string('c', 64), "marker.onnx"), 'c'),
            ["marker_classifier"] = new JsonObject
            {
                ["store_root"] = "models/marker-classifier",
                ["model_id"] = "classifier",
                ["version"] = "1",
                ["model_sha256"] = new string('d', 64),
                ["manifest_sha256"] = new string('e', 64),
                ["notice_sha256"] = new string('f', 64),
                ["benchmark_sha256"] = new string('1', 64),
                ["package_index_sha256"] = new string('2', 64),
            },
            ["native_files"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "onnxruntime",
                    ["file"] = "runtime/onnxruntime.dll",
                    ["sha256"] = new string('3', 64),
                },
            },
            ["managed_files"] = new JsonArray
            {
                FileReference("runtime/GraphReader.App.dll", new string('4', 64)),
            },
            ["algorithms"] = new JsonObject
            {
                ["axis_stage_version"] = "axis-opencv-v2",
                ["ocr_output_geometry"] = "model_polygon",
                ["ocr_composition_version"] = ProductionOcrAdapter.OriginalDbCandidateCompositionVersion,
                ["artifact_algorithm_id"] = "artifact-v1",
                ["artifact_algorithm_version"] = "1",
                ["artifact_configuration_sha256"] = new string('5', 64),
                ["artifact_app_assembly_sha256"] = new string('6', 64),
                ["artifact_ocr_assembly_sha256"] = new string('7', 64),
                ["marker_center_revision"] = "marker-v1",
                ["marker_center_candidate_id"] = "marker-p1",
                ["marker_classifier_adapter_id"] = "marker-classifier-v1",
                ["legend_adapter_id"] = "legend-v1",
                ["phase_adapter_id"] = "phase-v1",
            },
        };

        private static JsonObject ProtocolDocument(string barsSha) => new()
        {
            ["evidence_policy"] = new JsonObject
            {
                ["path"] = "ml/policy/evidence-policy.json",
                ["sha256"] = new string('8', 64),
            },
            ["hypothesis"] = "A future approved original DB candidate preserves the workflow.",
            ["isolated_change"] = "Use the original-image DB detector route.",
            ["split_identities"] = new JsonObject
            {
                ["split"] = "real-sealed",
                ["assignment_sha256"] = new string('9', 64),
                ["selected_inventory_sha256"] = new string('a', 64),
                ["project_count"] = 51,
                ["candidate_revision"] = "future-original-db-v1",
                ["candidate_id"] = "future-original-db-candidate",
                ["execution_descriptor_sha256"] = new string('0', 64),
                ["operating_point_identity"] = new string('0', 64),
                ["ocr_detection_sha256"] = Detection.Sha256,
                ["ocr_recognition_sha256"] = Recognition.Sha256,
                ["marker_center_sha256"] = new string('c', 64),
                ["marker_classifier_sha256"] = new string('d', 64),
                ["prerequisites"] = new JsonArray
                {
                    Prerequisite("ocr-detection-recognition", "synthetic-dev", '1'),
                    Prerequisite("ocr-detection-recognition", "synthetic-sealed", '2'),
                    Prerequisite("marker-center", "synthetic-dev", '3'),
                    Prerequisite("marker-center", "synthetic-sealed", '4'),
                },
            },
            ["metric"] = new JsonObject
            {
                ["name"] = "whole-workflow-csv-values",
                ["source_pixel_match_tolerance"] = 5,
                ["graph_x_absolute_tolerance"] = 0.5,
                ["graph_y_absolute_tolerance"] = 5,
                ["integer_session_x"] = true,
                ["full_project_denominator"] = true,
                ["full_point_denominator"] = true,
                ["series_boundaries"] = true,
                ["aggregate_only"] = true,
                ["require_in_memory_artifacts"] = true,
            },
            ["acceptance_bar"] = new JsonObject
            {
                ["path"] = "ml/policy/acceptance-bars.json",
                ["sha256"] = barsSha,
            },
            ["budget"] = new JsonObject
            {
                ["optimizer_steps"] = 0,
                ["training_use"] = false,
                ["candidate_selection"] = false,
                ["production_approval"] = false,
                ["aggregate_only"] = true,
                ["sealed_reads_per_candidate"] = 1,
            },
        };

        private static JsonObject WorkerDocument()
        {
            JsonObject evaluation = new()
            {
                ["truth_cases"] = 10,
                ["output_cases"] = 10,
                ["completed_cases"] = 10,
                ["failed_cases"] = 0,
                ["unexpected_cases"] = 0,
                ["integrity_failure_cases"] = 0,
                ["truth_series"] = 10,
                ["predicted_series"] = 10,
                ["matched_series"] = 10,
                ["truth_points"] = 100,
                ["predicted_points"] = 100,
                ["matched_points"] = 95,
                ["unique_point_value_metrics_available"] = true,
                ["relational_row_metrics_available"] = false,
                ["relational_phase_metrics_available"] = false,
                ["actual_rows"] = 100,
                ["residual_artifact_rows_from_failed_cases"] = 0,
                ["expected_rows"] = null,
                ["structurally_matched_rows"] = null,
                ["correct_rows"] = null,
                ["missing_rows"] = null,
                ["extra_rows"] = null,
                ["duplicate_rows"] = null,
                ["wrong_scale_rows"] = null,
                ["wrong_export_mode_rows"] = null,
                ["wrong_phase_rows"] = null,
                ["wrong_relation_rows"] = null,
                ["unique_point_value_correct"] = 95,
                ["unique_point_value_incorrect"] = 0,
                ["unique_point_missing"] = 5,
                ["unique_point_extra"] = 5,
                ["unique_point_wrong_scale"] = 0,
                ["unique_point_wrong_export_mode"] = 0,
                ["unique_point_structural_precision"] = 0.95,
                ["unique_point_structural_coverage"] = 0.95,
                ["unique_point_value_precision"] = 0.95,
                ["unique_point_value_coverage"] = 0.95,
                ["matched_unique_point_value_accuracy"] = 1.0,
                ["relational_row_precision"] = null,
                ["relational_row_coverage"] = null,
                ["matched_relational_graph_value_accuracy"] = null,
                ["matched_relational_phase_accuracy"] = null,
                ["artifact_integrity_valid"] = true,
                ["failure_kinds"] = new JsonObject(),
            };
            return new JsonObject
            {
                ["candidate_sha256"] = new string('0', 64),
                ["protocol_sha256"] = new string('0', 64),
                ["assignment_sha256"] = new string('0', 64),
                ["selected_inventory_sha256"] = new string('0', 64),
                ["corpus_content_sha256"] = new string('b', 64),
                ["split"] = "real-sealed",
                ["aggregate"] = new JsonObject
                {
                    ["schema"] = ProductionOriginalDbRealSealedEvidence.WorkerAggregateSchema,
                    ["image_groups"] = 10,
                    ["decoded_image_groups"] = 10,
                    ["project_files"] = 51,
                    ["workflow_invocations"] = 10,
                    ["workflow_succeeded_image_groups"] = 10,
                    ["workflow_failed_image_groups"] = 0,
                    ["workflow_succeeded_projects"] = 51,
                    ["workflow_failed_projects"] = 0,
                    ["truth_series"] = 10,
                    ["truth_points"] = 100,
                    ["coincident_cross_project_point_pairs"] = 0,
                    ["execution_failure_kinds"] = new JsonObject(),
                    ["evaluation"] = evaluation,
                    ["aggregate_only"] = true,
                    ["case_level_output"] = false,
                    ["truth_rows_output"] = false,
                    ["prediction_output"] = false,
                    ["paths_output"] = false,
                    ["names_output"] = false,
                },
            };
        }

        private static JsonObject Model(string task, ModelIdentity identity, char hashSeed) => new()
        {
            ["task"] = task,
            ["model_id"] = identity.ModelId,
            ["version"] = identity.Version,
            ["payload"] = FileReference($"models/{task}/model.onnx", identity.Sha256),
            ["manifest"] = FileReference($"models/{task}/manifest.json", new string(hashSeed, 64)),
            ["reviewed_license_inputs"] = new JsonArray
            {
                License("license_text", $"licenses/{task}-LICENSE.txt", 'e'),
                License("notice", $"licenses/{task}-NOTICE.txt", 'f'),
            },
        };

        private static JsonObject License(string role, string path, char sha) => new()
        {
            ["role"] = role,
            ["declared_path"] = path,
            ["file"] = path,
            ["sha256"] = new string(sha, 64),
        };

        private static JsonObject Prerequisite(string task, string split, char sha) => new()
        {
            ["task"] = task,
            ["split"] = split,
            ["path"] = $"evidence/{task}-{split}.json",
            ["sha256"] = new string(sha, 64),
        };

        private static JsonObject FileReference(string path, string sha) => new()
        {
            ["file"] = path,
            ["sha256"] = sha,
        };
    }

    private static JsonObject RequiredObject(JsonObject parent, string name) =>
        parent[name] as JsonObject ?? throw new InvalidOperationException($"Fixture field '{name}' is not an object.");

    private static byte[] Serialize(JsonObject value) =>
        JsonSerializer.SerializeToUtf8Bytes(value);

    private static string ExecutionDescriptor(JsonObject candidate)
    {
        using JsonDocument document = JsonDocument.Parse(Serialize(candidate));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, document.RootElement, omitRootProtocol: true);
        }
        return Hash(stream.ToArray());
    }

    private static string OperatingPoint(string descriptorSha256) => Hash(Encoding.UTF8.GetBytes(string.Join('\n',
    [
        "graphreader.frozen-real-workflow-operating-point.v1",
        descriptorSha256,
        "marker_center_threshold=0.25",
        "source_pixel_tolerance=5",
        "graph_x_tolerance=0.5",
        "graph_y_tolerance=5",
        "aggregate_only=true",
    ])));

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
            default: throw new InvalidDataException("Fixture JSON kind is unsupported.");
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
