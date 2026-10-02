// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphReader.App.Integration.Workflow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionOriginalDbOcrSealedEvidenceTests
{
    [TestMethod]
    public void ProducerFixtureAcceptsExactlyCanonicalBarsWithARecognitionFailureAndNoArchiveAccess()
    {
        Fixture fixture = Create();
        Assert.AreEqual("not-present/fabricated-archive.zip", Parse(fixture.Request)["archive_path"]!.GetValue<string>());
        Assert.AreEqual(1, Metrics(Parse(fixture.Outcome))["recognition_failures"]!["raw_regions_without_successful_recognition"]!.GetValue<int>());
        Validate(fixture);
    }

    [TestMethod]
    public void AggregateRoleBarDoesNotInventAPerRoleThreshold()
    {
        Fixture fixture = Create();
        JsonObject outcome = Parse(fixture.Outcome);
        JsonNode roles = Full(outcome)["by_expected_runtime_role"]!;
        roles["annotation"]!["correct_count"] = 18;
        roles["annotation"]!["accuracy"] = 0.90;
        roles["xtick"]!["correct_count"] = 20;
        roles["xtick"]!["accuracy"] = 1.0;
        Validate(fixture with { Outcome = Serialize(outcome) });
    }

    [TestMethod]
    [DataRow("precision")]
    [DataRow("recall")]
    [DataRow("recognition")]
    [DataRow("character_error_rate")]
    [DataRow("role")]
    public void RecomputesEachCanonicalBarDespiteClaimedPass(string metric)
    {
        Fixture fixture = Create();
        JsonObject outcome = Parse(fixture.Outcome);
        JsonNode metrics = Metrics(outcome);
        JsonNode full = Full(outcome);
        switch (metric)
        {
            case "precision":
                metrics["raw_detector_geometry"]!["predicted_region_count"] = 141;
                metrics["raw_detector_geometry"]!["false_positives"] = 8;
                metrics["raw_detector_geometry"]!["precision"] = 133.0 / 141;
                metrics["recognition_failures"]!["raw_regions_without_successful_recognition"] = 2;
                break;
            case "recall":
                foreach (string field in new[] { "raw_detector_geometry", "successfully_recognized_region_geometry" })
                {
                    metrics[field]!["truth_region_count"] = 141;
                    metrics[field]!["false_negatives"] = 8;
                    metrics[field]!["recall"] = 133.0 / 141;
                }
                full["truth_region_count"] = 141;
                full["geometry_false_negative_count"] = 8;
                full["recognition_exact_accuracy"] = 133.0 / 141;
                full["role_accuracy"] = 133.0 / 141;
                full["unmatched_truth_deletion_edit_count"] = 8;
                full["unmatched_prediction_insertion_edit_count"] = 6;
                full["by_expected_runtime_role"]!["annotation"]!["truth_count"] = 21;
                full["by_expected_runtime_role"]!["annotation"]!["accuracy"] = 19.0 / 21;
                break;
            case "recognition":
                full["recognition_exact_count"] = 132;
                full["recognition_exact_accuracy"] = 132.0 / 140;
                break;
            case "character_error_rate":
                full["unmatched_prediction_insertion_edit_count"] = 7;
                full["character_error_count"] = 15;
                full["character_error_rate"] = 15.0 / 280;
                break;
            case "role":
                full["role_correct_count"] = 132;
                full["role_accuracy"] = 132.0 / 140;
                full["by_expected_runtime_role"]!["annotation"]!["correct_count"] = 18;
                full["by_expected_runtime_role"]!["annotation"]!["accuracy"] = 0.90;
                break;
        }
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
            Validate(fixture with { Outcome = Serialize(outcome) }));
        StringAssert.Contains(error.Message, "canonical acceptance gate failed");
    }

    [TestMethod]
    [DataRow("graphreader.full-ocr-synthetic-sealed-score.v1")]
    [DataRow("graphreader.composed-ocr-sealed-evaluation.v1")]
    public void RejectsUnreviewedSchema(string schema)
    {
        Fixture fixture = Create();
        JsonObject outcome = Parse(fixture.Outcome);
        outcome["schema"] = schema;
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
            Validate(fixture with { Outcome = Serialize(outcome) }));
        StringAssert.Contains(error.Message, "is not supported");
    }

    [TestMethod]
    [DataRow("status", "fail")]
    [DataRow("read_status", "unconfirmed")]
    [DataRow("split", "dev")]
    [DataRow("acceptance_scope", "different")]
    [DataRow("candidate_sha256", "hash")]
    [DataRow("full_ocr_score_sha256", "hash")]
    [DataRow("runtime_identity_sha256", "hash")]
    [DataRow("acceptance_bars_sha256", "hash")]
    [DataRow("metric_reference_sha256", "hash")]
    [DataRow("coverage_protocol_sha256", "hash")]
    [DataRow("preflight_binding_sha256", "hash")]
    [DataRow("attempt_id", "hash")]
    [DataRow("admission_binding_sha256", "hash")]
    [DataRow("failure_code", "worker_failed")]
    public void RejectsWrongOutcomeStatusAndBindings(string field, string value)
    {
        Fixture fixture = Create();
        JsonObject outcome = Parse(fixture.Outcome);
        outcome[field] = value == "hash" ? new string('a', 64) : value;
        Reject(fixture with { Outcome = Serialize(outcome) });
    }

    [TestMethod]
    [DataRow("case_output")]
    [DataRow("truth_rows_output")]
    [DataRow("prediction_output")]
    [DataRow("pixel_output")]
    [DataRow("production_approved")]
    [DataRow("aggregate_only")]
    public void RejectsUnsafeFlags(string flag)
    {
        Fixture fixture = Create();
        JsonObject outcome = Parse(fixture.Outcome);
        outcome[flag] = flag != "aggregate_only";
        Reject(fixture with { Outcome = Serialize(outcome) });
    }

    [TestMethod]
    public void RejectsUndeclaredFieldsAtEveryAggregateLevelAndDuplicateProperties()
    {
        Fixture fixture = Create();
        foreach (string path in new[] { "", "aggregate", "aggregate.metrics", "aggregate.metrics.full_ocr_metrics",
                     "aggregate.metrics.raw_detector_geometry", "aggregate.metrics.recognition_failures",
                     "aggregate.metrics.full_ocr_metrics.by_expected_runtime_role.annotation", "transport", "bar_verdicts" })
        {
            JsonObject outcome = Parse(fixture.Outcome);
            JsonNode row = outcome;
            foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries)) row = row[part]!;
            row["truth_rows"] = new JsonArray("fabricated forbidden payload");
            Reject(fixture with { Outcome = Serialize(outcome) });
        }
        JsonObject disclosed = Parse(fixture.Outcome);
        disclosed["disclosures"] = new JsonArray("prediction");
        Reject(fixture with { Outcome = Serialize(disclosed) });
        string json = Encoding.UTF8.GetString(fixture.Outcome);
        Reject(fixture with { Outcome = Encoding.UTF8.GetBytes(json.Replace("\"status\":\"pass\"", "\"status\":\"pass\",\"status\":\"pass\"", StringComparison.Ordinal)) });
    }

    [TestMethod]
    public void RejectsCorruptedEmbeddedRequestOrPreflightAndReboundIdentityMismatches()
    {
        Fixture fixture = Create();
        Reject(fixture with { Request = [.. fixture.Request, (byte)' '] });
        Reject(fixture with { Preflight = [.. fixture.Preflight, (byte)' '] });
        foreach (string field in new[] { "candidate_sha256", "admission_binding_sha256", "set_id",
                     "coverage_protocol_sha256", "attempt_id" })
        {
            JsonObject request = Parse(fixture.Request);
            request[field] = new string('a', 64);
            Reject(RebindRequest(fixture, request));
        }
        foreach (string field in new[] { "candidate_sha256", "full_ocr_score_sha256", "runtime_identity_sha256",
                     "acceptance_bars_sha256", "metric_reference_sha256", "coverage_protocol_sha256" })
        {
            JsonObject preflight = Parse(fixture.Preflight);
            preflight[field] = new string('a', 64);
            Reject(RebindPreflight(fixture, preflight));
        }
        JsonObject policyChanged = Parse(fixture.Preflight);
        policyChanged["evidence_policy"]!["sha256"] = new string('a', 64);
        Reject(RebindPreflight(fixture, policyChanged));
    }

    [TestMethod]
    public void RejectsSubsetInventoryAndInconsistentMetricArithmetic()
    {
        Fixture fixture = Create();
        foreach ((string path, int value) in new[]
                 {
                     ("source_count", 1), ("panel_count", 1),
                     ("metrics.recognition_failures.raw_regions_without_successful_recognition", 0),
                     ("metrics.raw_detector_geometry.true_positives", 132),
                     ("metrics.raw_detector_geometry.intersection_over_union_minimum", 0),
                     ("metrics.full_ocr_metrics.predicted_region_count", 140),
                     ("metrics.full_ocr_metrics.recognition_exact_count", 134),
                     ("metrics.full_ocr_metrics.truth_character_count", 0),
                     ("metrics.full_ocr_metrics.character_error_count", 0),
                     ("metrics.full_ocr_metrics.unmatched_truth_deletion_edit_count", 0),
                     ("metrics.full_ocr_metrics.by_expected_runtime_role.annotation.correct_count", 20),
                     ("metrics.full_ocr_metrics.by_expected_runtime_role.annotation.truth_count", 0),
                 })
        {
            JsonObject outcome = Parse(fixture.Outcome);
            JsonNode row = outcome["aggregate"]!;
            string[] parts = path.Split('.');
            foreach (string part in parts[..^1]) row = row[part]!;
            row[parts[^1]] = value;
            Reject(fixture with { Outcome = Serialize(outcome) });
        }
        JsonObject badVerdict = Parse(fixture.Outcome);
        badVerdict["bar_verdicts"]!["role_accuracy"] = false;
        Reject(fixture with { Outcome = Serialize(badVerdict) });
        JsonObject excessive = Parse(fixture.Request);
        excessive["source_count"] = 129;
        Reject(RebindRequest(fixture, excessive));
    }

    [TestMethod]
    public void CharacterArithmeticDoesNotOverflowAndSupportsProducerInt64Counts()
    {
        Fixture fixture = Create();
        JsonObject outcome = Parse(fixture.Outcome);
        Full(outcome)["truth_character_count"] = 3_000_000_000L;
        Full(outcome)["character_error_rate"] = 14.0 / 3_000_000_000L;
        Validate(fixture with { Outcome = Serialize(outcome) });
        Full(outcome)["matched_pair_edit_count"] = long.MaxValue;
        Full(outcome)["character_error_count"] = long.MaxValue;
        Reject(fixture with { Outcome = Serialize(outcome) });
    }

    [TestMethod]
    public void RejectsUnclassifiedStderrAndInconsistentSafeChannelHash()
    {
        Fixture fixture = Create();
        JsonObject outcome = Parse(fixture.Outcome);
        outcome["transport"]!["stderr_sha256"] = Hash("fabricated arbitrary output"u8.ToArray());
        outcome["transport"]!["stderr_byte_count"] = 27;
        Reject(fixture with { Outcome = Serialize(outcome) });
        outcome = Parse(fixture.Outcome);
        outcome["transport"]!["stderr_byte_count"] = 1;
        Reject(fixture with { Outcome = Serialize(outcome) });
        outcome = Parse(fixture.Outcome);
        outcome["transport"]!["elapsed_seconds"] = -1;
        Reject(fixture with { Outcome = Serialize(outcome) });
    }

    [TestMethod]
    public void RejectsWeakenedBarsEvenWhenAllHashesAreRebound()
    {
        Fixture fixture = Create();
        JsonObject bars = Parse(fixture.Bars);
        bars["tier1_reviewable_error"]!["recognition_exact_match_minimum"] = 0.90;
        byte[] bytes = Serialize(bars);
        JsonObject outcome = Parse(fixture.Outcome);
        outcome["acceptance_bars_sha256"] = Hash(bytes);
        JsonObject preflight = Parse(fixture.Preflight);
        preflight["acceptance_bars_sha256"] = Hash(bytes);
        Reject(RebindPreflight(fixture with { Bars = bytes, Outcome = Serialize(outcome) }, preflight));
    }

    private static Fixture RebindRequest(Fixture fixture, JsonObject request)
    {
        byte[] bytes = Serialize(request);
        JsonObject outcome = Parse(fixture.Outcome);
        outcome["request"]!["sha256"] = Hash(bytes);
        return fixture with { Request = bytes, Outcome = Serialize(outcome) };
    }

    private static Fixture RebindPreflight(Fixture fixture, JsonObject preflight)
    {
        byte[] bytes = Serialize(preflight);
        JsonObject outcome = Parse(fixture.Outcome);
        outcome["attempt_id"] = Hash(bytes);
        outcome["preflight_binding_sha256"] = Hash(bytes);
        JsonObject request = Parse(fixture.Request);
        request["attempt_id"] = Hash(bytes);
        return RebindRequest(fixture with { Preflight = bytes, Outcome = Serialize(outcome) }, request);
    }

    private static void Reject(Fixture fixture) => Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture));
    private static void Validate(Fixture fixture) => ProductionOriginalDbOcrApprovalGate.ValidateSyntheticSealedSourceForTest(
        fixture.Outcome, fixture.Request, fixture.Preflight, fixture.CandidateSha, fixture.DevSha, fixture.Bars);
    private static JsonNode Metrics(JsonObject outcome) => outcome["aggregate"]!["metrics"]!;
    private static JsonNode Full(JsonObject outcome) => Metrics(outcome)["full_ocr_metrics"]!;
    private static JsonObject Parse(byte[] bytes) => JsonNode.Parse(bytes)!.AsObject();
    private static byte[] Serialize(JsonNode value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Fixture Create()
    {
        using Stream stream = typeof(ProductionOriginalDbOcrSealedEvidenceTests).Assembly.GetManifestResourceStream(
            "GraphReader.App.Tests.Fixtures.original-db-sealed-evidence.json")!;
        JsonObject fixture = JsonNode.Parse(stream)!.AsObject();
        byte[] Decode(string name) => Convert.FromBase64String(fixture[name]!.GetValue<string>());
        return new(Serialize(fixture["outcome"]!), Decode("request_base64"), Decode("preflight_base64"),
            Decode("acceptance_bars_base64"), fixture["candidate_sha256"]!.GetValue<string>(),
            fixture["dev_score_sha256"]!.GetValue<string>());
    }

    private sealed record Fixture(byte[] Outcome, byte[] Request, byte[] Preflight, byte[] Bars, string CandidateSha, string DevSha);
}
