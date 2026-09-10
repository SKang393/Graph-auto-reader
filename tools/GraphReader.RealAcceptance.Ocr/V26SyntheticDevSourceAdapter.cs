// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record V26BoundArtifact(string Path, string Sha256);

internal sealed record V26SyntheticDevValidationProfile(
    V26BoundArtifact CandidateConfig,
    V26BoundArtifact BaseV25Config,
    V26BoundArtifact BaseV25Result,
    V26BoundArtifact NegativeCoverageReport,
    V26BoundArtifact NegativeCoverageCache,
    V26BoundArtifact AnnulusPreflight,
    V26BoundArtifact AnnulusSelectionCache,
    string RunnerSourceBundleSha256,
    int RunnerSourceCount,
    string ProtocolSha256,
    string EvidencePolicySha256,
    string V21CheckpointSha256,
    string V21OnnxSha256,
    string ComponentSelectionSha256,
    string FamilySelectionSha256);

/// <summary>
/// Authenticates the train/dev-only V26 candidate report before it can be used
/// as marker-center synthetic-dev prerequisite evidence. It does not inspect
/// real data, authorize a model, or alter the shared acceptance bars.
/// </summary>
internal static class V26SyntheticDevSourceAdapter
{
    internal const string SourceSchema =
        "graphreader.marker-center-annulus-reservation-v26-candidate.v1";
    internal const string Revision = "marker-center-annulus-reservation-v26";
    internal const string CandidateId = "P1";

    private const string Task = "marker-center";
    private const string ConfigSchema =
        "graphreader.marker-center-annulus-reservation-v26-candidate-config.v1";
    private const string ProtocolPath =
        "ml/markers/center/localization_confidence_v26/protocol.json";
    private const string PolicyPath = "ml/policy/evidence-policy.json";
    private const string BarsPath = "ml/policy/acceptance-bars.json";
    private const int MaximumJsonBytes = 16 * 1024 * 1024;
    private const double NumericTolerance = 1e-12;

    private static readonly V26SyntheticDevValidationProfile ProductionProfile = new(
        new("ml/markers/center/localization_confidence_v26/training/p1.json",
            "31f8fee2ecb4fc01b2c1234761423dcbb668e98c90157abd4e1179c6749236db"),
        new("ml/markers/center/plot_domain_v25/training/p1.json",
            "e84acb31ca4df3204d3894e893b31ba7fede55aa91a6c885f791eee05b7fe9b2"),
        new("ml/markers/center/plot_domain_v25/P1_RESULT.json",
            "fb64443005b6ca4c26d4e5f1d8b37d8882a93a186d9b2b92fffc476a8c823a63"),
        new("artifacts/goal22-runs/marker-v26-target-preflight/negative-coverage-v1/coverage.json",
            "921e6020831055cde224729954a863c23b6804bee232a9431c773bce660ed1d5"),
        new("artifacts/goal22-runs/marker-v26-target-preflight/negative-coverage-v1/frozen-v25-train-proposal-metadata.npz",
            "5b8afc209e1c2a6f849ef044056b46903c79692d7264a724651a3a158369576b"),
        new("artifacts/goal22-runs/marker-v26-target-preflight/annulus-reservation-v1/preflight.json",
            "1bcece9f371b97c0e38679eb433259894faa2ef5dbad146bef3cd02319bc810b"),
        new("artifacts/goal22-runs/marker-v26-target-preflight/annulus-reservation-v1/proposed-selection-metadata.npz",
            "4a03c08d1566a1f587910ea2fd9c49069d850c62a558b15f614af1f01c93fa8e"),
        "d94b5dfba37e2958a445df5d385d0d96786153ae2ab2804eff8449f4bb55165a",
        71,
        "410eec2b6936a08bab8bcba341aaf04c6e9ac661215169d74efb57c33358fb7a",
        "4dc18136c284b0b1805d3a3b22a9197ad06e6a41f4e43b4e1d4d9245b97e0aed",
        "ba9722ebd3091c91749c175607a480500d3b651e6719d19b69c7e48cee4ef6c9",
        "0b413db48f8e6707ee5ec99afff4cd8ec3d25c6b8a8d9f165bd416deb4578a38",
        "c184a97ae7ad0292361e62a4aaed5a54d8c1ae1bdb0ab3daab9ddeb8d46b1af0",
        "239b3f2aa14eb9636964c22083e722aba76bd70623a2bbd75362e1db96c527e3");

    internal static bool IsSupportedSchema(string schema) =>
        string.Equals(schema, SourceSchema, StringComparison.Ordinal);

    internal static void Validate(
        string repositoryRoot,
        byte[] sourceBytes,
        FrozenRealWorkflowCandidateIdentity candidate,
        JsonElement envelope,
        JsonElement envelopeBenchmarks,
        JsonElement envelopeParity,
        CancellationToken cancellationToken) =>
        ValidateCore(repositoryRoot, sourceBytes, candidate, envelope,
            envelopeBenchmarks, envelopeParity, ProductionProfile, cancellationToken);

    internal static void ValidateForTest(
        string repositoryRoot,
        byte[] sourceBytes,
        FrozenRealWorkflowCandidateIdentity candidate,
        JsonElement envelope,
        JsonElement envelopeBenchmarks,
        JsonElement envelopeParity,
        V26SyntheticDevValidationProfile profile,
        CancellationToken cancellationToken) =>
        ValidateCore(repositoryRoot, sourceBytes, candidate, envelope,
            envelopeBenchmarks, envelopeParity, profile, cancellationToken);

    private static void ValidateCore(
        string repositoryRoot,
        byte[] sourceBytes,
        FrozenRealWorkflowCandidateIdentity candidate,
        JsonElement envelope,
        JsonElement envelopeBenchmarks,
        JsonElement envelopeParity,
        V26SyntheticDevValidationProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(sourceBytes);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(repositoryRoot);

        using JsonDocument reportDocument = ParseExact(sourceBytes);
        JsonElement report = reportDocument.RootElement;
        RequireProperties(report,
        [
            "schema", "task", "revision", "candidate_id", "status",
            "candidate_config_path", "candidate_config_sha256", "base_v25_config",
            "base_v25_result", "annulus_preflight", "annulus_selection_cache",
            "selection_evidence", "optimizer_steps", "component_dev_comparisons",
            "family_dev_comparisons", "acceptance_bar", "dev_gate_passed",
            "checkpoint_sha256", "onnx_sha256", "v21_checkpoint_sha256",
            "v21_onnx_sha256", "onnx_provider", "onnx_dynamic_candidate_counts",
            "onnx_parity_maximum_absolute_error", "elapsed_ms", "synthetic_only",
            "private_data", "sealed_data", "sealed_runs", "production_approval",
            "release_eligible", "training_authorization",
        ], "V26 source report");
        if (Text(report, "schema") != SourceSchema || Text(report, "task") != Task ||
            Text(report, "revision") != Revision || Text(report, "candidate_id") != CandidateId ||
            Text(envelope, "stage_revision") != Revision ||
            Text(envelope, "stage_candidate_id") != CandidateId ||
            candidate.Revision != Revision || candidate.CandidateId != CandidateId ||
            Text(report, "status") != "dev_passed" || !Boolean(report, "dev_gate_passed") ||
            Text(report, "onnx_provider") != "CPUExecutionProvider" ||
            Sha(report, "onnx_sha256") != candidate.MarkerCenterSha256 ||
            Sha(report, "v21_checkpoint_sha256") != profile.V21CheckpointSha256 ||
            Sha(report, "v21_onnx_sha256") != profile.V21OnnxSha256 ||
            Integer(report, "optimizer_steps") != 12636 ||
            !Boolean(report, "synthetic_only") || Boolean(report, "private_data") ||
            Boolean(report, "sealed_data") || Integer(report, "sealed_runs") != 0 ||
            Boolean(report, "production_approval") || Boolean(report, "release_eligible"))
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_SOURCE_SCOPE_INVALID");
        }
        _ = Sha(report, "checkpoint_sha256");
        double elapsed = Number(report, "elapsed_ms");
        if (elapsed < 0)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_RUNTIME_INVALID");
        }

        ValidateReference(report.GetProperty("base_v25_config"), profile.BaseV25Config,
            root, cancellationToken);
        ValidateReference(report.GetProperty("base_v25_result"), profile.BaseV25Result,
            root, cancellationToken);
        ValidateReference(report.GetProperty("annulus_preflight"), profile.AnnulusPreflight,
            root, cancellationToken);
        ValidateReference(report.GetProperty("annulus_selection_cache"),
            profile.AnnulusSelectionCache, root, cancellationToken);

        string configPath = Text(report, "candidate_config_path");
        string configSha = Sha(report, "candidate_config_sha256");
        if (configPath != profile.CandidateConfig.Path || configSha != profile.CandidateConfig.Sha256)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_CONFIG_BINDING_MISMATCH");
        }
        byte[] configBytes = ReadReferenced(root, configPath, configSha, cancellationToken);
        using JsonDocument configDocument = ParseExact(configBytes);
        ValidateConfig(configDocument.RootElement, profile, root, cancellationToken);
        ValidateSelectionEvidence(report.GetProperty("selection_evidence"), profile);
        ValidateParity(report, configDocument.RootElement, envelopeParity);

        JsonElement component = FindUniqueThreshold(
            report.GetProperty("component_dev_comparisons"), 0.25);
        JsonElement family = FindUniqueThreshold(
            report.GetProperty("family_dev_comparisons"), 0.25);
        ValidateMetric(component, 2004, 167, FindBenchmark(envelopeBenchmarks, "component"));
        ValidateMetric(family, 206, 9, FindBenchmark(envelopeBenchmarks, "full-source-family"));
        ValidateAcceptanceBar(report.GetProperty("acceptance_bar"));
        ValidateAuthorization(report.GetProperty("training_authorization"), root, profile,
            configSha, cancellationToken);
    }

    private static void ValidateConfig(
        JsonElement config,
        V26SyntheticDevValidationProfile profile,
        string root,
        CancellationToken cancellationToken)
    {
        RequireProperties(config,
        [
            "schema", "task", "revision", "candidate_id", "stage",
            "expected_runner_source_bundle_sha256", "base_v25_config", "base_v25_result",
            "negative_coverage_report", "negative_coverage_cache", "annulus_preflight",
            "annulus_selection_cache", "recipe", "expected_data", "synthetic_only",
            "private_data", "sealed_data", "production_approval", "release_eligible",
        ], "V26 config");
        if (Text(config, "schema") != ConfigSchema || Text(config, "task") != Task ||
            Text(config, "revision") != Revision || Text(config, "candidate_id") != CandidateId ||
            Text(config, "stage") != "P1" ||
            Sha(config, "expected_runner_source_bundle_sha256") != profile.RunnerSourceBundleSha256 ||
            !Boolean(config, "synthetic_only") || Boolean(config, "private_data") ||
            Boolean(config, "sealed_data") || Boolean(config, "production_approval") ||
            Boolean(config, "release_eligible"))
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_CONFIG_SCOPE_INVALID");
        }
        ValidateReference(config.GetProperty("base_v25_config"), profile.BaseV25Config,
            root, cancellationToken);
        ValidateReference(config.GetProperty("base_v25_result"), profile.BaseV25Result,
            root, cancellationToken);
        ValidateReference(config.GetProperty("negative_coverage_report"),
            profile.NegativeCoverageReport, root, cancellationToken);
        ValidateReference(config.GetProperty("negative_coverage_cache"),
            profile.NegativeCoverageCache, root, cancellationToken);
        ValidateReference(config.GetProperty("annulus_preflight"), profile.AnnulusPreflight,
            root, cancellationToken);
        ValidateReference(config.GetProperty("annulus_selection_cache"),
            profile.AnnulusSelectionCache, root, cancellationToken);
        ValidateRecipe(config.GetProperty("recipe"), profile);
        ValidateExpectedData(config.GetProperty("expected_data"));
    }

    private static void ValidateRecipe(JsonElement recipe, V26SyntheticDevValidationProfile profile)
    {
        RequireProperties(recipe,
        [
            "base_v25_config_sha256", "sampler", "bands_px", "reservation", "replacement",
            "seed", "epochs", "batch_size", "learning_rate", "weight_decay",
            "positive_loss_weight", "hard_negative_loss_weight", "focal_alpha", "focal_gamma",
            "label_positive_distance_px", "confidence_threshold", "selection_thresholds",
            "provider", "onnx_dynamic_candidate_counts", "onnx_parity_tolerance",
        ], "V26 recipe");
        if (Sha(recipe, "base_v25_config_sha256") != profile.BaseV25Config.Sha256 ||
            Text(recipe, "sampler") != "fixed_truth_annulus_reservation_v1" ||
            Text(recipe, "reservation") != "one_closest_negative_per_scene_truth_and_band" ||
            Integer(recipe, "seed") != 20260903 || Integer(recipe, "epochs") != 36 ||
            Integer(recipe, "batch_size") != 128 || Number(recipe, "learning_rate") != 0.001 ||
            Number(recipe, "weight_decay") != 0.0001 || Number(recipe, "positive_loss_weight") != 16 ||
            Number(recipe, "hard_negative_loss_weight") != 5 || Number(recipe, "focal_alpha") != 0.25 ||
            Number(recipe, "focal_gamma") != 2 || Number(recipe, "label_positive_distance_px") != 3 ||
            Number(recipe, "confidence_threshold") != 0.25 ||
            Text(recipe, "provider") != "CPUExecutionProvider" ||
            Number(recipe, "onnx_parity_tolerance") != 1e-5)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_RECIPE_INVALID");
        }
        RequireNumericRows(recipe.GetProperty("bands_px"), [[3, 5], [5, 8], [8, 12]],
            "REAL_WORKFLOW_V26_BANDS_INVALID");
        RequireStringArray(recipe.GetProperty("replacement"),
        [
            "same_scene_and_original_stratum", "same_original_stratum_global",
            "cross_stratum_global_fallback",
        ], "REAL_WORKFLOW_V26_REPLACEMENT_INVALID");
        RequireNumbers(recipe.GetProperty("selection_thresholds"), [0.4, 0.55, 0.7],
            "REAL_WORKFLOW_V26_SELECTION_THRESHOLDS_INVALID");
        RequireIntegers(recipe.GetProperty("onnx_dynamic_candidate_counts"), [1, 8, 37],
            "REAL_WORKFLOW_V26_PARITY_COUNTS_INVALID");
    }

    private static void ValidateExpectedData(JsonElement value)
    {
        RequireProperties(value,
        [
            "component_train", "family_train", "component_dev", "family_dev",
            "training_example_count", "positive_example_count", "negative_example_count",
            "hard_negative_example_count", "optimizer_steps",
        ], "V26 expected data");
        ValidateCounts(value.GetProperty("component_train"),
            new Dictionary<string, int> { ["scene_count"] = 167, ["truth_count"] = 2004 });
        ValidateCounts(value.GetProperty("component_dev"),
            new Dictionary<string, int> { ["scene_count"] = 167, ["truth_count"] = 2004 });
        ValidateCounts(value.GetProperty("family_train"), new Dictionary<string, int>
        {
            ["source_count"] = 20, ["panel_count"] = 28, ["truth_count"] = 500,
        });
        ValidateCounts(value.GetProperty("family_dev"), new Dictionary<string, int>
        {
            ["source_count"] = 3, ["panel_count"] = 9, ["truth_count"] = 206,
        });
        if (Integer(value, "training_example_count") != 44891 ||
            Integer(value, "positive_example_count") != 4081 ||
            Integer(value, "negative_example_count") != 40810 ||
            Integer(value, "hard_negative_example_count") != 8617 ||
            Integer(value, "optimizer_steps") != 12636)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_EXPECTED_COUNTS_INVALID");
        }
    }

    private static void ValidateSelectionEvidence(
        JsonElement value, V26SyntheticDevValidationProfile profile)
    {
        RequireProperties(value, ["component", "family"], "V26 selection evidence");
        ValidateSelection(value.GetProperty("component"), profile.ComponentSelectionSha256,
            selected: 35838, protectedRows: 18284, changedRows: 4876);
        ValidateSelection(value.GetProperty("family"), profile.FamilySelectionSha256,
            selected: 9053, protectedRows: 1761, changedRows: 1331);
    }

    private static void ValidateSelection(
        JsonElement value, string proposedSha, int selected, int protectedRows, int changedRows)
    {
        RequireProperties(value,
        [
            "frozen_selection_sha256", "proposed_selection_sha256", "selected_rows",
            "preserved_protected_rows", "added_rows", "displaced_rows", "row_order",
        ], "V26 selection");
        _ = Sha(value, "frozen_selection_sha256");
        if (Sha(value, "proposed_selection_sha256") != proposedSha ||
            Integer(value, "selected_rows") != selected ||
            Integer(value, "preserved_protected_rows") != protectedRows ||
            Integer(value, "added_rows") != changedRows || Integer(value, "displaced_rows") != changedRows ||
            Text(value, "row_order") != "scene_then_ascending_proposal_index")
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_SELECTION_INVALID");
        }
    }

    private static void ValidateMetric(
        JsonElement source, int truthCount, int sceneCount, JsonElement envelope)
    {
        if (Number(source, "threshold") != 0.25 || Integer(source, "scene_count") != sceneCount)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_OPERATING_POINT_INVALID");
        }
        int proposalTp = NonnegativeInteger(source, "proposal_true_positives");
        int tp = NonnegativeInteger(source, "true_positives");
        int fp = NonnegativeInteger(source, "false_positives");
        int fn = NonnegativeInteger(source, "false_negatives");
        int prohibitedHits = NonnegativeInteger(source, "prohibited_structure_hits");
        _ = NonnegativeInteger(source, "duplicate_count");
        double proposalRecall = Number(source, "proposal_recall");
        double precision = Number(source, "precision");
        double recall = Number(source, "recall");
        double f1 = Number(source, "f1");
        double prohibitedRate = Number(source, "prohibited_structure_hit_rate");
        int predictionCount = checked(tp + fp);
        double expectedPrecision = predictionCount == 0 ? 0 : (double)tp / predictionCount;
        double expectedRecall = (double)tp / truthCount;
        double expectedProposalRecall = (double)proposalTp / truthCount;
        double expectedF1 = expectedPrecision + expectedRecall == 0 ? 0 :
            2 * expectedPrecision * expectedRecall / (expectedPrecision + expectedRecall);
        double expectedProhibited = predictionCount == 0 ? 0 :
            (double)prohibitedHits / predictionCount;
        if (checked(tp + fn) != truthCount || proposalTp > truthCount ||
            Math.Abs(proposalRecall - expectedProposalRecall) > NumericTolerance ||
            Math.Abs(precision - expectedPrecision) > NumericTolerance ||
            Math.Abs(recall - expectedRecall) > NumericTolerance ||
            Math.Abs(f1 - expectedF1) > NumericTolerance ||
            Math.Abs(prohibitedRate - expectedProhibited) > NumericTolerance ||
            proposalRecall < 0.95 || precision < 0.95 || recall < 0.95 ||
            prohibitedRate > 0.02)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_METRIC_INVALID");
        }
        if (Integer(envelope, "truth_count") != truthCount ||
            Integer(envelope, "true_positive") != tp ||
            Integer(envelope, "false_positive") != fp ||
            Integer(envelope, "false_negative") != fn ||
            Math.Abs(Number(envelope, "precision") - precision) > NumericTolerance ||
            Math.Abs(Number(envelope, "recall") - recall) > NumericTolerance ||
            Math.Abs(RequiredNullableNumber(envelope, "prohibited_structure_hit_rate") - prohibitedRate) >
                NumericTolerance)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_ENVELOPE_METRIC_MISMATCH");
        }
    }

    private static void ValidateAcceptanceBar(JsonElement value)
    {
        RequireProperties(value,
        [
            "proposal_recall_minimum", "precision_minimum", "recall_minimum",
            "prohibited_structure_hit_rate_maximum",
        ], "V26 acceptance bar");
        if (Number(value, "proposal_recall_minimum") != 0.95 ||
            Number(value, "precision_minimum") != 0.95 ||
            Number(value, "recall_minimum") != 0.95 ||
            Number(value, "prohibited_structure_hit_rate_maximum") != 0.02)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_ACCEPTANCE_BAR_INVALID");
        }
    }

    private static void ValidateParity(
        JsonElement report, JsonElement config, JsonElement envelope)
    {
        double tolerance = Number(config.GetProperty("recipe"), "onnx_parity_tolerance");
        int[] configured = config.GetProperty("recipe").GetProperty("onnx_dynamic_candidate_counts")
            .EnumerateArray().Select(ExactInteger).ToArray();
        JsonElement rowsValue = report.GetProperty("onnx_dynamic_candidate_counts");
        if (rowsValue.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_PARITY_INVALID");
        }
        JsonElement[] rows = rowsValue.EnumerateArray().ToArray();
        if (rows.Length != configured.Length)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_PARITY_INVALID");
        }
        double maximum = 0;
        for (int index = 0; index < rows.Length; index++)
        {
            RequireProperties(rows[index], ["candidate_count", "maximum_absolute_error"],
                "V26 parity row");
            double error = Number(rows[index], "maximum_absolute_error");
            if (Integer(rows[index], "candidate_count") != configured[index] || error < 0)
            {
                throw new InvalidDataException("REAL_WORKFLOW_V26_PARITY_INVALID");
            }
            maximum = Math.Max(maximum, error);
        }
        double reported = Number(report, "onnx_parity_maximum_absolute_error");
        if (Math.Abs(reported - maximum) > NumericTolerance || reported > tolerance ||
            !Boolean(envelope, "passed") ||
            Math.Abs(Number(envelope, "max_absolute_error") - reported) > NumericTolerance ||
            Math.Abs(Number(envelope, "tolerance") - tolerance) > NumericTolerance)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_PARITY_INVALID");
        }
    }

    private static void ValidateAuthorization(
        JsonElement value,
        string root,
        V26SyntheticDevValidationProfile profile,
        string configSha,
        CancellationToken cancellationToken)
    {
        RequireProperties(value,
        [
            "task", "revision", "candidate_id", "candidate_config_path",
            "candidate_config_sha256", "runner_source_paths", "runner_source_bundle_sha256",
            "training_budget_ledger_sha256", "evidence_policy", "source_snapshot_path",
            "source_snapshot_sha256", "source_binding_mode", "base_commit",
        ], "V26 training authorization");
        if (Text(value, "task") != Task || Text(value, "revision") != Revision ||
            Text(value, "candidate_id") != CandidateId ||
            Text(value, "candidate_config_path") != profile.CandidateConfig.Path ||
            Sha(value, "candidate_config_sha256") != configSha ||
            Sha(value, "runner_source_bundle_sha256") != profile.RunnerSourceBundleSha256 ||
            Text(value, "source_binding_mode") != "immutable_pre_run_snapshot" ||
            !IsLowerHex(Text(value, "base_commit"), 40))
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_AUTHORIZATION_INVALID");
        }
        _ = Sha(value, "training_budget_ledger_sha256");
        JsonElement policy = value.GetProperty("evidence_policy");
        if (Text(policy, "path") != PolicyPath || Sha(policy, "sha256") != profile.EvidencePolicySha256)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_AUTHORIZATION_POLICY_INVALID");
        }

        string[] runnerPaths = ReadTextArray(value.GetProperty("runner_source_paths"));
        if (runnerPaths.Length != profile.RunnerSourceCount ||
            !runnerPaths.SequenceEqual(runnerPaths.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            runnerPaths.Distinct(StringComparer.Ordinal).Count() != runnerPaths.Length)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_RUNNER_SOURCE_LIST_INVALID");
        }
        string snapshotPath = Text(value, "source_snapshot_path");
        string snapshotSha = Sha(value, "source_snapshot_sha256");
        byte[] snapshotBytes = ReadReferenced(root, snapshotPath, snapshotSha, cancellationToken);
        using JsonDocument snapshotDocument = ParseExact(snapshotBytes);
        ValidateSnapshot(snapshotDocument.RootElement, runnerPaths, profile, configSha);
    }

    private static void ValidateSnapshot(
        JsonElement snapshot,
        string[] runnerPaths,
        V26SyntheticDevValidationProfile profile,
        string configSha)
    {
        JsonElement identity = snapshot.GetProperty("identity");
        if (Integer(snapshot, "schema_version") != 1 || Text(identity, "task") != Task ||
            Text(identity, "revision") != Revision || Text(identity, "candidate_id") != CandidateId ||
            !IsLowerHex(Text(snapshot, "base_commit"), 40))
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_SNAPSHOT_IDENTITY_INVALID");
        }
        JsonElement sourceRows = snapshot.GetProperty("sources");
        if (sourceRows.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_SNAPSHOT_SOURCES_INVALID");
        }
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement row in sourceRows.EnumerateArray())
        {
            RequireProperties(row, ["path", "sha256"], "V26 snapshot source");
            string path = SafeRelativePath(Text(row, "path"));
            if (!sources.TryAdd(path, Sha(row, "sha256")))
            {
                throw new InvalidDataException("REAL_WORKFLOW_V26_SNAPSHOT_SOURCE_DUPLICATE");
            }
        }
        var bundle = new StringBuilder();
        foreach (string path in runnerPaths)
        {
            if (!sources.TryGetValue(path, out string? sha))
            {
                throw new InvalidDataException("REAL_WORKFLOW_V26_SNAPSHOT_SOURCE_MISSING");
            }
            bundle.Append(path).Append('=').Append(sha).Append('\n');
        }
        string computedBundle = FrozenCandidateBinding.Hash(Encoding.UTF8.GetBytes(bundle.ToString()));
        if (computedBundle != profile.RunnerSourceBundleSha256 ||
            !sources.TryGetValue(profile.CandidateConfig.Path, out string? observedConfig) ||
            observedConfig != configSha ||
            !sources.TryGetValue(ProtocolPath, out string? observedProtocol) ||
            observedProtocol != profile.ProtocolSha256 ||
            !sources.TryGetValue(PolicyPath, out string? observedPolicy) ||
            observedPolicy != profile.EvidencePolicySha256 || !sources.ContainsKey(BarsPath))
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_SNAPSHOT_BINDING_INVALID");
        }
        JsonElement preregistration = snapshot.GetProperty("preregistered_ledger_entry");
        if (Text(preregistration, "task") != Task || Text(preregistration, "revision") != Revision ||
            Text(preregistration, "authorized_candidate_id") != CandidateId ||
            Text(preregistration, "status") != "candidate_1_preregistered" ||
            !Boolean(preregistration, "execution_authorized") ||
            Integer(preregistration, "experiment_budget") != 1 ||
            Integer(preregistration, "optimizer_steps_expected") != 12636 ||
            Text(preregistration, "source_binding_mode") != "immutable_pre_run_snapshot" ||
            Text(preregistration, "protocol_path") != ProtocolPath ||
            Sha(preregistration, "protocol_sha256") != profile.ProtocolSha256)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_PREREGISTRATION_INVALID");
        }
        RequireStringArray(preregistration.GetProperty("preregistered_candidate_ids"),
            [CandidateId], "REAL_WORKFLOW_V26_PREREGISTRATION_INVALID");
        if (preregistration.GetProperty("consumed_candidate_ids").GetArrayLength() != 0 ||
            Text(preregistration.GetProperty("candidate_config_paths"), CandidateId) !=
                profile.CandidateConfig.Path ||
            Sha(preregistration.GetProperty("candidate_config_sha256"), CandidateId) != configSha)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_PREREGISTRATION_INVALID");
        }
    }

    private static void ValidateReference(
        JsonElement value, V26BoundArtifact expected, string root,
        CancellationToken cancellationToken)
    {
        RequireProperties(value, ["path", "sha256"], "V26 reference");
        string path = Text(value, "path");
        string sha = Sha(value, "sha256");
        if (path != expected.Path || sha != expected.Sha256)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_REFERENCE_IDENTITY_MISMATCH");
        }
        HashReferenced(root, path, sha, cancellationToken);
    }

    private static JsonElement FindUniqueThreshold(JsonElement rows, double threshold)
    {
        if (rows.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_METRICS_INVALID");
        }
        JsonElement[] matches = rows.EnumerateArray()
            .Where(row => Math.Abs(Number(row, "threshold") - threshold) <= NumericTolerance)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_THRESHOLD_ROW_INVALID");
        }
        return matches[0];
    }

    private static JsonElement FindBenchmark(JsonElement rows, string name)
    {
        if (rows.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_ENVELOPE_BENCHMARKS_INVALID");
        }
        JsonElement[] matches = rows.EnumerateArray()
            .Where(row => Text(row, "name") == name).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_ENVELOPE_BENCHMARK_INVALID");
        }
        return matches[0];
    }

    private static void ValidateCounts(JsonElement value, IReadOnlyDictionary<string, int> expected)
    {
        RequireProperties(value, expected.Keys.ToArray(), "V26 split counts");
        if (expected.Any(pair => Integer(value, pair.Key) != pair.Value))
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_EXPECTED_COUNTS_INVALID");
        }
    }

    private static void RequireNumericRows(JsonElement value, double[][] expected, string code)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(code);
        }
        JsonElement[] rows = value.EnumerateArray().ToArray();
        if (rows.Length != expected.Length)
        {
            throw new InvalidDataException(code);
        }
        for (int row = 0; row < rows.Length; row++)
        {
            double[] actual = rows[row].EnumerateArray().Select(ExactNumber).ToArray();
            if (!actual.SequenceEqual(expected[row]))
            {
                throw new InvalidDataException(code);
            }
        }
    }

    private static void RequireNumbers(JsonElement value, double[] expected, string code)
    {
        if (value.ValueKind != JsonValueKind.Array ||
            !value.EnumerateArray().Select(ExactNumber).SequenceEqual(expected))
        {
            throw new InvalidDataException(code);
        }
    }

    private static void RequireIntegers(JsonElement value, int[] expected, string code)
    {
        if (value.ValueKind != JsonValueKind.Array ||
            !value.EnumerateArray().Select(ExactInteger).SequenceEqual(expected))
        {
            throw new InvalidDataException(code);
        }
    }

    private static void RequireStringArray(JsonElement value, string[] expected, string code)
    {
        if (value.ValueKind != JsonValueKind.Array ||
            !value.EnumerateArray().Select(item => item.GetString()).SequenceEqual(expected))
        {
            throw new InvalidDataException(code);
        }
    }

    private static string[] ReadTextArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_TEXT_ARRAY_INVALID");
        }
        return value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("REAL_WORKFLOW_V26_TEXT_ARRAY_INVALID");
            }
            return SafeRelativePath(item.GetString()!);
        }).ToArray();
    }

    private static byte[] ReadReferenced(
        string root, string relativePath, string expectedSha, CancellationToken cancellationToken)
    {
        string path = ResolveReference(root, relativePath);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumJsonBytes)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_JSON_SIZE_INVALID");
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (FrozenCandidateBinding.Hash(bytes) != expectedSha)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_REFERENCE_CHECKSUM_MISMATCH");
        }
        return bytes;
    }

    private static void HashReferenced(
        string root, string relativePath, string expectedSha, CancellationToken cancellationToken)
    {
        string path = ResolveReference(root, relativePath);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var algorithm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            algorithm.AppendData(buffer, 0, read);
        }
        string actual = Convert.ToHexStringLower(algorithm.GetHashAndReset());
        if (actual != expectedSha)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_REFERENCE_CHECKSUM_MISMATCH");
        }
    }

    private static string ResolveReference(string root, string relativePath)
    {
        string safe = SafeRelativePath(relativePath);
        return FrozenCandidateBinding.RequireUnderRoot(root, safe, "V26 synthetic-dev reference");
    }

    private static string SafeRelativePath(string value)
    {
        string normalized = value.Replace('\\', '/');
        if (Path.IsPathRooted(value) || normalized.Split('/').Any(
                static part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_REFERENCE_PATH_INVALID");
        }
        return normalized;
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
            throw new InvalidDataException("REAL_WORKFLOW_V26_JSON_INVALID", exception);
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
                    throw new InvalidDataException("REAL_WORKFLOW_V26_JSON_DUPLICATE_PROPERTY");
                }
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                RejectDuplicates(item);
            }
        }
    }

    private static void RequireProperties(JsonElement value, string[] names, string label)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException($"REAL_WORKFLOW_{label.Replace(' ', '_').ToUpperInvariant()}_SHAPE_INVALID");
        }
    }

    private static string Text(JsonElement value, string property)
    {
        JsonElement raw = value.GetProperty(property);
        string? text = raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
        if (string.IsNullOrWhiteSpace(text) || text != text.Trim())
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_TEXT_INVALID");
        }
        return text;
    }

    private static string Sha(JsonElement value, string property) =>
        FrozenCandidateBinding.RequireSha256(Text(value, property), property);

    private static int Integer(JsonElement value, string property) =>
        ExactInteger(value.GetProperty(property));

    private static int NonnegativeInteger(JsonElement value, string property)
    {
        int result = Integer(value, property);
        if (result < 0)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_INTEGER_INVALID");
        }
        return result;
    }

    private static int ExactInteger(JsonElement value)
    {
        if (!value.TryGetInt32(out int result))
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_INTEGER_INVALID");
        }
        return result;
    }

    private static double Number(JsonElement value, string property) =>
        ExactNumber(value.GetProperty(property));

    private static double ExactNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) ||
            !double.IsFinite(result))
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_NUMBER_INVALID");
        }
        return result;
    }

    private static double RequiredNullableNumber(JsonElement value, string property)
    {
        if (value.GetProperty(property).ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_REQUIRED_METRIC_UNAVAILABLE");
        }
        return Number(value, property);
    }

    private static bool Boolean(JsonElement value, string property)
    {
        JsonElement raw = value.GetProperty(property);
        if (raw.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("REAL_WORKFLOW_V26_BOOLEAN_INVALID");
        }
        return raw.GetBoolean();
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
