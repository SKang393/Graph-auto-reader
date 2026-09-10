// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;

namespace GraphReader.RealAcceptance.Ocr;

internal static class V26SyntheticDevSourceAdapterSelfTest
{
    private sealed record Fixture(
        string Root,
        JsonObject Report,
        JsonDocument Context,
        FrozenRealWorkflowCandidateIdentity Candidate,
        V26SyntheticDevValidationProfile Profile,
        string SnapshotPath);

    internal static void Run()
    {
        string outer = Path.Combine(Path.GetTempPath(), "graphreader-v26-adapter-selftest",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outer);
        try
        {
            ValidateFixture(BuildFixture(Path.Combine(outer, "valid")));
            ExpectRejected(BuildFixture(Path.Combine(outer, "failed")),
                report => report["status"] = "failed_dev");
            ExpectRejected(BuildFixture(Path.Combine(outer, "incomplete")),
                report => report["dev_gate_passed"] = false);
            ExpectRejected(BuildFixture(Path.Combine(outer, "private")),
                report => report["private_data"] = true);
            ExpectRejected(BuildFixture(Path.Combine(outer, "threshold")), report =>
                report["component_dev_comparisons"]![0]!["threshold"] = 0.4);
            ExpectRejected(BuildFixture(Path.Combine(outer, "denominator")), report =>
                report["family_dev_comparisons"]![0]!["false_negatives"] = 11);
            ExpectRejected(BuildFixture(Path.Combine(outer, "selection")), report =>
                report["selection_evidence"]!["component"]!["proposed_selection_sha256"] =
                    new string('f', 64));
            ExpectRejected(BuildFixture(Path.Combine(outer, "model")), report =>
                report["onnx_sha256"] = new string('e', 64));
            ExpectRejected(BuildFixture(Path.Combine(outer, "parity")), report =>
                report["onnx_dynamic_candidate_counts"]![1]!["maximum_absolute_error"] = 0.1);
            PayloadMutationIsRejected(BuildFixture(Path.Combine(outer, "payload")));
            SnapshotMutationIsRejected(BuildFixture(Path.Combine(outer, "snapshot")));
        }
        finally
        {
            Directory.Delete(outer, recursive: true);
        }
    }

    private static void ValidateFixture(Fixture fixture)
    {
        JsonElement context = fixture.Context.RootElement;
        V26SyntheticDevSourceAdapter.ValidateForTest(
            fixture.Root,
            JsonSerializer.SerializeToUtf8Bytes(fixture.Report),
            fixture.Candidate,
            context.GetProperty("envelope"),
            context.GetProperty("benchmarks"),
            context.GetProperty("parity"),
            fixture.Profile,
            CancellationToken.None);
    }

    private static void ExpectRejected(Fixture fixture, Action<JsonObject> mutation)
    {
        mutation(fixture.Report);
        ExpectFailure(() => ValidateFixture(fixture));
        fixture.Context.Dispose();
    }

    private static void PayloadMutationIsRejected(Fixture fixture)
    {
        File.AppendAllText(Path.Combine(fixture.Root, fixture.Profile.AnnulusSelectionCache.Path),
            "changed", Encoding.UTF8);
        ExpectFailure(() => ValidateFixture(fixture));
        fixture.Context.Dispose();
    }

    private static void SnapshotMutationIsRejected(Fixture fixture)
    {
        string snapshotFullPath = Path.Combine(fixture.Root, fixture.SnapshotPath);
        JsonObject snapshot = JsonNode.Parse(File.ReadAllBytes(snapshotFullPath))!.AsObject();
        snapshot["sources"]![0]!["sha256"] = new string('a', 64);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        File.WriteAllBytes(snapshotFullPath, bytes);
        fixture.Report["training_authorization"]!["source_snapshot_sha256"] = Hash(bytes);
        ExpectFailure(() => ValidateFixture(fixture));
        fixture.Context.Dispose();
    }

    private static Fixture BuildFixture(string root)
    {
        Directory.CreateDirectory(root);
        V26BoundArtifact WritePayload(string relative, string content)
        {
            string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(full, bytes);
            return new(relative, Hash(bytes));
        }

        V26BoundArtifact baseConfig = WritePayload("payload/base-config.json", "{\"base\":1}");
        V26BoundArtifact baseResult = WritePayload("payload/base-result.json", "{\"result\":1}");
        V26BoundArtifact coverageReport = WritePayload("payload/coverage.json", "{\"coverage\":1}");
        V26BoundArtifact coverageCache = WritePayload("payload/coverage.npz", "coverage-cache");
        V26BoundArtifact annulusReport = WritePayload("payload/annulus.json", "{\"annulus\":1}");
        V26BoundArtifact annulusCache = WritePayload("payload/annulus.npz", "annulus-cache");
        V26BoundArtifact sourceA = WritePayload("sources/a.py", "a = 1\n");
        V26BoundArtifact sourceB = WritePayload("sources/b.py", "b = 2\n");
        string[] runnerPaths = [sourceA.Path, sourceB.Path];
        string bundle = Hash(Encoding.UTF8.GetBytes(string.Concat(
            runnerPaths.Order(StringComparer.Ordinal).Select(path =>
            {
                V26BoundArtifact item = path == sourceA.Path ? sourceA : sourceB;
                return $"{path}={item.Sha256}\n";
            }))));
        string protocolSha = new string('1', 64);
        string policySha = new string('2', 64);
        string componentSelection = new string('3', 64);
        string familySelection = new string('4', 64);

        JsonObject config = new()
        {
            ["schema"] = "graphreader.marker-center-annulus-reservation-v26-candidate-config.v1",
            ["task"] = "marker-center",
            ["revision"] = V26SyntheticDevSourceAdapter.Revision,
            ["candidate_id"] = V26SyntheticDevSourceAdapter.CandidateId,
            ["stage"] = "P1",
            ["expected_runner_source_bundle_sha256"] = bundle,
            ["base_v25_config"] = Reference(baseConfig),
            ["base_v25_result"] = Reference(baseResult),
            ["negative_coverage_report"] = Reference(coverageReport),
            ["negative_coverage_cache"] = Reference(coverageCache),
            ["annulus_preflight"] = Reference(annulusReport),
            ["annulus_selection_cache"] = Reference(annulusCache),
            ["recipe"] = Recipe(baseConfig.Sha256),
            ["expected_data"] = ExpectedData(),
            ["synthetic_only"] = true,
            ["private_data"] = false,
            ["sealed_data"] = false,
            ["production_approval"] = false,
            ["release_eligible"] = false,
        };
        string configPath = "config/p1.json";
        byte[] configBytes = JsonSerializer.SerializeToUtf8Bytes(config);
        string configFullPath = Path.Combine(root, configPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(configFullPath)!);
        File.WriteAllBytes(configFullPath, configBytes);
        V26BoundArtifact configArtifact = new(configPath, Hash(configBytes));
        var profile = new V26SyntheticDevValidationProfile(
            configArtifact, baseConfig, baseResult, coverageReport, coverageCache,
            annulusReport, annulusCache, bundle, 2, protocolSha, policySha,
            new string('5', 64), new string('6', 64), componentSelection, familySelection);

        string snapshotPath = "seals/source-snapshot.json";
        JsonObject snapshot = new()
        {
            ["schema_version"] = 1,
            ["captured_utc"] = "2026-09-10T00:00:00+00:00",
            ["base_commit"] = new string('a', 40),
            ["identity"] = new JsonObject
            {
                ["task"] = "marker-center", ["revision"] = V26SyntheticDevSourceAdapter.Revision,
                ["candidate_id"] = V26SyntheticDevSourceAdapter.CandidateId,
            },
            ["inline_hashes"] = new JsonObject(),
            ["sources"] = new JsonArray(
                SourceRow(sourceA.Path, sourceA.Sha256), SourceRow(sourceB.Path, sourceB.Sha256),
                SourceRow(configPath, configArtifact.Sha256),
                SourceRow("ml/markers/center/localization_confidence_v26/protocol.json", protocolSha),
                SourceRow("ml/policy/evidence-policy.json", policySha),
                SourceRow("ml/policy/acceptance-bars.json", new string('7', 64))),
            ["preregistered_ledger_entry"] = new JsonObject
            {
                ["task"] = "marker-center", ["revision"] = V26SyntheticDevSourceAdapter.Revision,
                ["authorized_candidate_id"] = V26SyntheticDevSourceAdapter.CandidateId,
                ["status"] = "candidate_1_preregistered", ["execution_authorized"] = true,
                ["experiment_budget"] = 1, ["optimizer_steps_expected"] = 12636,
                ["source_binding_mode"] = "immutable_pre_run_snapshot",
                ["protocol_path"] = "ml/markers/center/localization_confidence_v26/protocol.json",
                ["protocol_sha256"] = protocolSha,
                ["preregistered_candidate_ids"] = new JsonArray("P1"),
                ["consumed_candidate_ids"] = new JsonArray(),
                ["candidate_config_paths"] = new JsonObject { ["P1"] = configPath },
                ["candidate_config_sha256"] = new JsonObject { ["P1"] = configArtifact.Sha256 },
            },
        };
        byte[] snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        string snapshotFullPath = Path.Combine(root, snapshotPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotFullPath)!);
        File.WriteAllBytes(snapshotFullPath, snapshotBytes);

        string markerSha = new string('c', 64);
        JsonObject report = new()
        {
            ["schema"] = V26SyntheticDevSourceAdapter.SourceSchema,
            ["task"] = "marker-center", ["revision"] = V26SyntheticDevSourceAdapter.Revision,
            ["candidate_id"] = "P1", ["status"] = "dev_passed",
            ["candidate_config_path"] = configPath,
            ["candidate_config_sha256"] = configArtifact.Sha256,
            ["base_v25_config"] = Reference(baseConfig),
            ["base_v25_result"] = Reference(baseResult),
            ["annulus_preflight"] = Reference(annulusReport),
            ["annulus_selection_cache"] = Reference(annulusCache),
            ["selection_evidence"] = SelectionEvidence(componentSelection, familySelection),
            ["optimizer_steps"] = 12636,
            ["component_dev_comparisons"] = new JsonArray(Metric(2004, 1920, 80, 167)),
            ["family_dev_comparisons"] = new JsonArray(Metric(206, 196, 8, 9)),
            ["acceptance_bar"] = new JsonObject
            {
                ["proposal_recall_minimum"] = 0.95, ["precision_minimum"] = 0.95,
                ["recall_minimum"] = 0.95, ["prohibited_structure_hit_rate_maximum"] = 0.02,
            },
            ["dev_gate_passed"] = true, ["checkpoint_sha256"] = new string('d', 64),
            ["onnx_sha256"] = markerSha, ["v21_checkpoint_sha256"] = new string('5', 64),
            ["v21_onnx_sha256"] = new string('6', 64), ["onnx_provider"] = "CPUExecutionProvider",
            ["onnx_dynamic_candidate_counts"] = new JsonArray(
                ParityRow(1, 1e-7), ParityRow(8, 2e-7), ParityRow(37, 3e-7)),
            ["onnx_parity_maximum_absolute_error"] = 3e-7, ["elapsed_ms"] = 1000.0,
            ["synthetic_only"] = true, ["private_data"] = false, ["sealed_data"] = false,
            ["sealed_runs"] = 0, ["production_approval"] = false, ["release_eligible"] = false,
            ["training_authorization"] = new JsonObject
            {
                ["task"] = "marker-center", ["revision"] = V26SyntheticDevSourceAdapter.Revision,
                ["candidate_id"] = "P1", ["candidate_config_path"] = configPath,
                ["candidate_config_sha256"] = configArtifact.Sha256,
                ["runner_source_paths"] = new JsonArray(
                    runnerPaths.Select(static path => JsonValue.Create(path)).ToArray()),
                ["runner_source_bundle_sha256"] = bundle,
                ["training_budget_ledger_sha256"] = new string('8', 64),
                ["evidence_policy"] = new JsonObject
                {
                    ["path"] = "ml/policy/evidence-policy.json", ["sha256"] = policySha,
                },
                ["source_snapshot_path"] = snapshotPath,
                ["source_snapshot_sha256"] = Hash(snapshotBytes),
                ["source_binding_mode"] = "immutable_pre_run_snapshot",
                ["base_commit"] = new string('a', 40),
            },
        };
        JsonObject componentBenchmark = Benchmark("component", 2004, 1920, 80);
        JsonObject familyBenchmark = Benchmark("full-source-family", 206, 196, 8);
        JsonDocument context = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["envelope"] = new JsonObject
            {
                ["stage_revision"] = V26SyntheticDevSourceAdapter.Revision,
                ["stage_candidate_id"] = "P1",
            },
            ["benchmarks"] = new JsonArray(componentBenchmark, familyBenchmark),
            ["parity"] = new JsonObject
            {
                ["passed"] = true, ["max_absolute_error"] = 3e-7, ["tolerance"] = 1e-5,
            },
        }));
        var candidate = new FrozenRealWorkflowCandidateIdentity(
            V26SyntheticDevSourceAdapter.Revision, "P1", new string('9', 64),
            new string('a', 64), new string('b', 64), "protocol.json", new string('0', 64),
            new string('1', 64), new string('2', 64), markerSha, new string('4', 64));
        return new Fixture(root, report, context, candidate, profile, snapshotPath);
    }

    private static JsonObject Recipe(string baseConfigSha) => new()
    {
        ["base_v25_config_sha256"] = baseConfigSha,
        ["sampler"] = "fixed_truth_annulus_reservation_v1",
        ["bands_px"] = new JsonArray(new JsonArray(3.0, 5.0), new JsonArray(5.0, 8.0),
            new JsonArray(8.0, 12.0)),
        ["reservation"] = "one_closest_negative_per_scene_truth_and_band",
        ["replacement"] = new JsonArray("same_scene_and_original_stratum",
            "same_original_stratum_global", "cross_stratum_global_fallback"),
        ["seed"] = 20260903, ["epochs"] = 36, ["batch_size"] = 128,
        ["learning_rate"] = 0.001, ["weight_decay"] = 0.0001,
        ["positive_loss_weight"] = 16.0, ["hard_negative_loss_weight"] = 5.0,
        ["focal_alpha"] = 0.25, ["focal_gamma"] = 2.0,
        ["label_positive_distance_px"] = 3.0, ["confidence_threshold"] = 0.25,
        ["selection_thresholds"] = new JsonArray(0.4, 0.55, 0.7),
        ["provider"] = "CPUExecutionProvider",
        ["onnx_dynamic_candidate_counts"] = new JsonArray(1, 8, 37),
        ["onnx_parity_tolerance"] = 1e-5,
    };

    private static JsonObject ExpectedData() => new()
    {
        ["component_train"] = Counts(("scene_count", 167), ("truth_count", 2004)),
        ["family_train"] = Counts(("source_count", 20), ("panel_count", 28), ("truth_count", 500)),
        ["component_dev"] = Counts(("scene_count", 167), ("truth_count", 2004)),
        ["family_dev"] = Counts(("source_count", 3), ("panel_count", 9), ("truth_count", 206)),
        ["training_example_count"] = 44891, ["positive_example_count"] = 4081,
        ["negative_example_count"] = 40810, ["hard_negative_example_count"] = 8617,
        ["optimizer_steps"] = 12636,
    };

    private static JsonObject SelectionEvidence(string component, string family) => new()
    {
        ["component"] = Selection(new string('a', 64), component, 35838, 18284, 4876),
        ["family"] = Selection(new string('b', 64), family, 9053, 1761, 1331),
    };

    private static JsonObject Selection(
        string frozen, string proposed, int selected, int protectedRows, int changed) => new()
    {
        ["frozen_selection_sha256"] = frozen, ["proposed_selection_sha256"] = proposed,
        ["selected_rows"] = selected, ["preserved_protected_rows"] = protectedRows,
        ["added_rows"] = changed, ["displaced_rows"] = changed,
        ["row_order"] = "scene_then_ascending_proposal_index",
    };

    private static JsonObject Metric(int truth, int tp, int fp, int scenes)
    {
        int fn = truth - tp;
        double precision = (double)tp / (tp + fp);
        double recall = (double)tp / truth;
        return new JsonObject
        {
            ["threshold"] = 0.25, ["scene_count"] = scenes,
            ["proposal_true_positives"] = truth, ["proposal_recall"] = 1.0,
            ["true_positives"] = tp, ["false_positives"] = fp, ["false_negatives"] = fn,
            ["precision"] = precision, ["recall"] = recall,
            ["f1"] = 2 * precision * recall / (precision + recall), ["duplicate_count"] = 0,
            ["prohibited_structure_hits"] = 0, ["prohibited_structure_hit_rate"] = 0.0,
        };
    }

    private static JsonObject Benchmark(string name, int truth, int tp, int fp) => new()
    {
        ["name"] = name, ["truth_count"] = truth, ["true_positive"] = tp,
        ["false_positive"] = fp, ["false_negative"] = truth - tp,
        ["precision"] = (double)tp / (tp + fp), ["recall"] = (double)tp / truth,
        ["recognition_exact_match"] = null, ["character_error_rate"] = null,
        ["role_accuracy"] = null, ["prohibited_structure_hit_rate"] = 0.0,
    };

    private static JsonObject Counts(params (string Name, int Value)[] values)
    {
        var result = new JsonObject();
        foreach ((string name, int value) in values)
        {
            result[name] = value;
        }
        return result;
    }

    private static JsonObject Reference(V26BoundArtifact value) =>
        new() { ["path"] = value.Path, ["sha256"] = value.Sha256 };

    private static JsonObject SourceRow(string path, string sha) =>
        new() { ["path"] = path, ["sha256"] = sha };

    private static JsonObject ParityRow(int count, double error) =>
        new() { ["candidate_count"] = count, ["maximum_absolute_error"] = error };

    private static string Hash(byte[] bytes) => FrozenCandidateBinding.Hash(bytes);

    private static void ExpectFailure(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("EXPECTED_V26_ADAPTER_REJECTION_MISSING");
    }
}
