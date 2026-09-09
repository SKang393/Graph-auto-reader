// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GraphReader.RealAcceptance.Ocr;

internal static class FrozenRealWorkflowAdmissionSelfTest
{
    private static readonly string ExecutionDescriptorSha256 = new('4', 64);
    private static readonly string OperatingPointIdentity = new('5', 64);
    private static readonly int[] ParityCandidateCounts = [1, 8, 37];

    internal static object Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "graphreader-real-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            (string policySha, string barsSha) = WritePolicies(root);
            var checks = new List<string>();
            FrozenRealCorpusSelection devInventory = Inventory(FrozenRealCorpusInventory.RealDev, 120);
            (string devPath, string devSha, FrozenRealWorkflowCandidateIdentity devCandidate) =
                WriteProtocol(root, FrozenRealCorpusInventory.RealDev, devInventory, policySha, barsSha, []);
            FrozenRealWorkflowAdmissionResult dev = FrozenRealWorkflowAdmission.LoadForTest(
                root, devPath, devSha, devCandidate, devInventory,
                FrozenRealCorpusInventory.RealDev, explicitOptIn: true,
                continuousIntegration: false, CancellationToken.None);
            Require(!dev.SealedFirstReadRequired && dev.ProjectCount == 120 &&
                    dev.PrerequisiteEvidenceSha256.Count == 0 && dev.AggregateOnly &&
                    dev.EvaluationOptions.RequireInMemoryArtifacts,
                "real_dev_admission_is_aggregate_and_budget_free");
            checks.Add("real_dev_admission_is_aggregate_and_budget_free");

            ExpectFailure<InvalidOperationException>(() => FrozenRealWorkflowAdmission.LoadForTest(
                root, devPath, devSha, devCandidate, devInventory,
                FrozenRealCorpusInventory.RealDev, explicitOptIn: false,
                continuousIntegration: false, CancellationToken.None));
            ExpectFailure<InvalidOperationException>(() => FrozenRealWorkflowAdmission.LoadForTest(
                root, devPath, devSha, devCandidate, devInventory,
                FrozenRealCorpusInventory.RealDev, explicitOptIn: true,
                continuousIntegration: true, CancellationToken.None));
            checks.Add("missing_opt_in_and_ci_rejected");

            ExpectFailure<InvalidDataException>(() => FrozenRealWorkflowAdmission.LoadForTest(
                root, devPath, "0" + devSha[1..], devCandidate, devInventory,
                FrozenRealCorpusInventory.RealDev, true, false, CancellationToken.None));
            ExpectFailure<InvalidDataException>(() => FrozenRealWorkflowAdmission.LoadForTest(
                root, devPath, devSha, devCandidate with { MarkerCenterSha256 = "9" + devCandidate.MarkerCenterSha256[1..] },
                devInventory, FrozenRealCorpusInventory.RealDev, true, false, CancellationToken.None));
            ExpectFailure<InvalidDataException>(() => FrozenRealWorkflowAdmission.LoadForTest(
                root, devPath, devSha, devCandidate with
                {
                    ExecutionDescriptorSha256 = "8" + devCandidate.ExecutionDescriptorSha256[1..],
                }, devInventory, FrozenRealCorpusInventory.RealDev, true, false, CancellationToken.None));
            checks.Add("protocol_model_and_execution_identity_drift_rejected");

            byte[] descriptorA = JsonSerializer.SerializeToUtf8Bytes(new
            {
                protocol = new { path = "protocol-a.json", sha256 = new string('1', 64) },
                runtime = new { workers = 1 },
                algorithms = new { ocr = "atomic" },
            });
            byte[] descriptorB = JsonSerializer.SerializeToUtf8Bytes(new
            {
                algorithms = new { ocr = "atomic" },
                runtime = new { workers = 1 },
                protocol = new { path = "protocol-b.json", sha256 = new string('2', 64) },
            });
            byte[] descriptorDrift = JsonSerializer.SerializeToUtf8Bytes(new
            {
                protocol = new { path = "protocol-a.json", sha256 = new string('1', 64) },
                runtime = new { workers = 2 },
                algorithms = new { ocr = "atomic" },
            });
            Require(
                FrozenRealWorkflowAdmission.ComputeExecutionDescriptorSha256(descriptorA) ==
                FrozenRealWorkflowAdmission.ComputeExecutionDescriptorSha256(descriptorB) &&
                FrozenRealWorkflowAdmission.ComputeExecutionDescriptorSha256(descriptorA) !=
                FrozenRealWorkflowAdmission.ComputeExecutionDescriptorSha256(descriptorDrift),
                "execution_descriptor_excludes_only_protocol_and_binds_runtime");
            checks.Add("execution_descriptor_excludes_only_protocol_and_binds_runtime");

            FrozenRealCorpusSelection sealedInventory = Inventory(FrozenRealCorpusInventory.RealSealed, 51);
            object[] prerequisites = WritePrerequisites(root, policySha, barsSha);
            (string sealedPath, string sealedSha, FrozenRealWorkflowCandidateIdentity sealedCandidate) =
                WriteProtocol(root, FrozenRealCorpusInventory.RealSealed, sealedInventory,
                    policySha, barsSha, prerequisites);
            JsonElement markerDevReference = JsonSerializer.SerializeToElement(prerequisites[0]);
            FrozenRealWorkflowAdmission.ValidatePrerequisiteForTest(
                root,
                markerDevReference.GetProperty("path").GetString()!,
                markerDevReference.GetProperty("sha256").GetString()!,
                "marker-center", "synthetic-dev", sealedCandidate,
                policySha, barsSha, CancellationToken.None);
            checks.Add("known_marker_dev_source_metrics_are_recomputed_and_bound");

            ExpectFailureCode<InvalidDataException>(
                () => FrozenRealWorkflowAdmission.LoadForTest(
                    root, sealedPath, sealedSha, sealedCandidate, sealedInventory,
                    FrozenRealCorpusInventory.RealSealed, true, false, CancellationToken.None),
                "REAL_WORKFLOW_PREREQUISITE_SOURCE_SCHEMA_UNSUPPORTED");
            checks.Add("sealed_unknown_source_schemas_fail_closed");

            object[] incomplete = prerequisites[..^1];
            (string incompletePath, string incompleteSha, FrozenRealWorkflowCandidateIdentity incompleteCandidate) =
                WriteProtocol(root, FrozenRealCorpusInventory.RealSealed, sealedInventory,
                    policySha, barsSha, incomplete, "incomplete.json");
            ExpectFailure<InvalidDataException>(() => FrozenRealWorkflowAdmission.LoadForTest(
                root, incompletePath, incompleteSha, incompleteCandidate, sealedInventory,
                FrozenRealCorpusInventory.RealSealed, true, false, CancellationToken.None));
            checks.Add("incomplete_sealed_prerequisite_coverage_rejected");

            string evidencePath = ((JsonElement)JsonSerializer.SerializeToElement(prerequisites[0])).GetProperty("path").GetString()!;
            File.AppendAllText(Path.Combine(root, evidencePath), " ");
            ExpectFailure<InvalidDataException>(() => FrozenRealWorkflowAdmission.LoadForTest(
                root, sealedPath, sealedSha, sealedCandidate, sealedInventory,
                FrozenRealCorpusInventory.RealSealed, true, false, CancellationToken.None));
            checks.Add("prerequisite_source_bytes_are_checksum_bound");

            (string driftEvidencePath, string driftEvidenceSha) =
                WriteMarkerDevMetricDrift(root, markerDevReference);
            ExpectFailureCode<InvalidDataException>(
                () => FrozenRealWorkflowAdmission.ValidatePrerequisiteForTest(
                    root, driftEvidencePath, driftEvidenceSha,
                    "marker-center", "synthetic-dev", sealedCandidate,
                    policySha, barsSha, CancellationToken.None),
                "REAL_WORKFLOW_MARKER_DEV_METRIC_MISMATCH");
            checks.Add("hash_matching_envelope_with_drifted_source_metrics_rejected");

            (string prohibitedEvidencePath, string prohibitedEvidenceSha) =
                WriteMarkerDevProhibitedRateDrift(root, markerDevReference);
            ExpectFailureCode<InvalidDataException>(
                () => FrozenRealWorkflowAdmission.ValidatePrerequisiteForTest(
                    root, prohibitedEvidencePath, prohibitedEvidenceSha,
                    "marker-center", "synthetic-dev", sealedCandidate,
                    policySha, barsSha, CancellationToken.None),
                "REAL_WORKFLOW_MARKER_PREREQUISITE_GATE_FAILED");
            checks.Add("self_consistent_marker_prohibited_rate_above_canonical_bar_rejected");

            return new
            {
                status = "pass",
                checks,
                private_corpus_access = false,
                sealed_corpus_access = false,
                model_inference = false,
            };
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (string PolicySha, string BarsSha) WritePolicies(string root)
    {
        byte[] policy = JsonSerializer.SerializeToUtf8Bytes(new
        {
            splits = new
            {
                dev = new
                {
                    training_permitted = false,
                    case_level_inspection = "unrestricted",
                    consumes_candidate_budget = false,
                },
                @sealed = new
                {
                    training_permitted = false,
                    case_level_inspection = "aggregate_only_until_retired",
                    consumes_candidate_budget = true,
                },
            },
        });
        byte[] bars = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tier1_reviewable_error = new
            {
                text_region_detection_recall_minimum = 0.95,
                text_region_detection_precision_minimum = 0.95,
                recognition_exact_match_minimum = 0.95,
                character_error_rate_maximum = 0.05,
                role_accuracy_minimum = 0.95,
                prohibited_structure_hit_rate_maximum = 0.02,
                marker_center_recall_minimum = 0.95,
                marker_center_precision_minimum = 0.95,
            },
        });
        string policyPath = Path.Combine(root, "ml", "policy", "evidence-policy.json");
        string barsPath = Path.Combine(root, "ml", "policy", "acceptance-bars.json");
        Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
        File.WriteAllBytes(policyPath, policy);
        File.WriteAllBytes(barsPath, bars);
        return (FrozenCandidateBinding.Hash(policy), FrozenCandidateBinding.Hash(bars));
    }

    private static object[] WritePrerequisites(string root, string policySha, string barsSha)
    {
        Directory.CreateDirectory(Path.Combine(root, "evidence"));
        var references = new List<object>();
        foreach ((string Task, string Split) role in new[]
        {
            ("marker-center", "synthetic-dev"),
            ("marker-center", "synthetic-sealed"),
            ("ocr-detection-recognition", "synthetic-dev"),
            ("ocr-detection-recognition", "synthetic-sealed"),
        })
        {
            object[] benchmarks = role.Task == "marker-center" && role.Split == "synthetic-dev"
                ? [Benchmark("component", marker: true), Benchmark("full-source-family", marker: true)]
                : role.Task == "marker-center"
                    ? [Benchmark("full-graph", marker: true)]
                : [Benchmark("full-source", marker: false)];
            (string sourcePath, string sourceSha) = role == ("marker-center", "synthetic-dev")
                ? WriteMarkerDevSource(root)
                : WriteUnsupportedSource(root, role.Task, role.Split);
            var models = new Dictionary<string, object?>
            {
                ["ocr_detection_sha256"] = role.Task == "ocr-detection-recognition" ? new string('a', 64) : null,
                ["ocr_recognition_sha256"] = role.Task == "ocr-detection-recognition" ? new string('b', 64) : null,
                ["marker_center_sha256"] = role.Task == "marker-center" ? new string('c', 64) : null,
            };
            byte[] evidence = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = FrozenRealWorkflowAdmission.PrerequisiteSchema,
                task = role.Task,
                split = role.Split,
                stage_revision = "fixture-stage-revision",
                stage_candidate_id = "fixture-stage-candidate",
                evidence_policy_sha256 = policySha,
                acceptance_bar_sha256 = barsSha,
                runtime_composition_sha256 = ExecutionDescriptorSha256,
                operating_point_identity = OperatingPointIdentity,
                models,
                source_result = new { path = sourcePath, sha256 = sourceSha },
                benchmarks,
                parity = new { passed = true, max_absolute_error = 0.0, tolerance = 1e-5 },
                aggregate_only = true,
                case_level_output = false,
                truth_rows_output = false,
                prediction_output = false,
                pixel_output = false,
            });
            string file = $"evidence/{role.Task}-{role.Split}.json";
            File.WriteAllBytes(Path.Combine(root, file), evidence);
            references.Add(new { task = role.Task, split = role.Split, path = file, sha256 = FrozenCandidateBinding.Hash(evidence) });
        }
        return references.ToArray();
    }

    private static (string Path, string Sha) WriteMarkerDevSource(string root)
    {
        string configPath = "evidence/marker-dev-config.json";
        byte[] config = JsonSerializer.SerializeToUtf8Bytes(new
        {
            confidence_threshold = 0.25,
            onnx_parity_tolerance = 1e-5,
            onnx_dynamic_candidate_counts = ParityCandidateCounts,
            provider = "CPUExecutionProvider",
        });
        File.WriteAllBytes(Path.Combine(root, configPath), config);
        byte[] source = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "graphreader.marker-center-plot-domain-v25-candidate.v1",
            task = "marker-center",
            revision = "fixture-stage-revision",
            candidate_id = "fixture-stage-candidate",
            status = "dev_passed",
            candidate_config_path = configPath,
            candidate_config_sha256 = FrozenCandidateBinding.Hash(config),
            dev_gate_passed = true,
            component_dev_comparisons = new[] { SourceBenchmark() },
            family_dev_comparisons = new[] { SourceBenchmark() },
            onnx_sha256 = new string('c', 64),
            onnx_provider = "CPUExecutionProvider",
            onnx_dynamic_candidate_counts = new[]
            {
                new { candidate_count = 1, maximum_absolute_error = 0.0 },
                new { candidate_count = 8, maximum_absolute_error = 0.0 },
                new { candidate_count = 37, maximum_absolute_error = 0.0 },
            },
            onnx_parity_maximum_absolute_error = 0.0,
            synthetic_only = true,
            private_data = false,
            real_dev_reads = 0,
            real_sealed_reads = 0,
            sealed_runs = 0,
            production_approval = false,
            release_eligible = false,
        });
        string path = "evidence/marker-center-synthetic-dev-source.json";
        File.WriteAllBytes(Path.Combine(root, path), source);
        return (path, FrozenCandidateBinding.Hash(source));
    }

    private static (string Path, string Sha) WriteUnsupportedSource(
        string root, string task, string split)
    {
        byte[] source = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "fixture-aggregate-source.v1",
            task,
            split,
        });
        string path = $"evidence/{task}-{split}-source.json";
        File.WriteAllBytes(Path.Combine(root, path), source);
        return (path, FrozenCandidateBinding.Hash(source));
    }

    private static object SourceBenchmark() => new
    {
        threshold = 0.25,
        true_positives = 19,
        false_positives = 1,
        false_negatives = 1,
        precision = 0.95,
        recall = 0.95,
        prohibited_structure_hit_rate = 0.02,
    };

    private static object Benchmark(string name, bool marker) => new
    {
        name,
        truth_count = 20,
        true_positive = 19,
        false_positive = 1,
        false_negative = 1,
        precision = 0.95,
        recall = 0.95,
        recognition_exact_match = marker ? (double?)null : 0.95,
        character_error_rate = marker ? (double?)null : 0.05,
        role_accuracy = marker ? (double?)null : 0.95,
        prohibited_structure_hit_rate = 0.02,
    };

    private static (string Path, string Sha, FrozenRealWorkflowCandidateIdentity Candidate) WriteProtocol(
        string root,
        string split,
        FrozenRealCorpusSelection inventory,
        string policySha,
        string barsSha,
        object[] prerequisites,
        string name = "protocol.json")
    {
        int sealedReads = split == FrozenRealCorpusInventory.RealSealed ? 1 : 0;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            evidence_policy = new { path = "ml/policy/evidence-policy.json", sha256 = policySha },
            hypothesis = "fixture hypothesis",
            isolated_change = "fixture isolated workflow composition",
            split_identities = new
            {
                split,
                assignment_sha256 = inventory.AssignmentSha256,
                selected_inventory_sha256 = inventory.SelectedInventorySha256,
                project_count = inventory.SelectedProjects.Count,
                candidate_revision = "fixture-revision",
                candidate_id = "fixture-candidate",
                execution_descriptor_sha256 = ExecutionDescriptorSha256,
                operating_point_identity = OperatingPointIdentity,
                ocr_detection_sha256 = new string('a', 64),
                ocr_recognition_sha256 = new string('b', 64),
                marker_center_sha256 = new string('c', 64),
                marker_classifier_sha256 = new string('d', 64),
                prerequisites,
            },
            metric = new
            {
                name = "whole-workflow-csv-values",
                source_pixel_match_tolerance = 5,
                graph_x_absolute_tolerance = 0.5,
                graph_y_absolute_tolerance = 5,
                integer_session_x = true,
                full_project_denominator = true,
                full_point_denominator = true,
                series_boundaries = true,
                aggregate_only = true,
                require_in_memory_artifacts = true,
            },
            acceptance_bar = new { path = "ml/policy/acceptance-bars.json", sha256 = barsSha },
            budget = new
            {
                optimizer_steps = 0,
                training_use = false,
                candidate_selection = false,
                production_approval = false,
                aggregate_only = true,
                sealed_reads_per_candidate = sealedReads,
            },
        });
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        string hash = FrozenCandidateBinding.Hash(bytes);
        return (name, hash, new FrozenRealWorkflowCandidateIdentity(
            "fixture-revision", "fixture-candidate", new string('e', 64),
            ExecutionDescriptorSha256, OperatingPointIdentity, name, hash,
            new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64)));
    }

    private static (string Path, string Sha) WriteMarkerDevMetricDrift(
        string root, JsonElement originalReference)
    {
        string originalEvidencePath = originalReference.GetProperty("path").GetString()!;
        using JsonDocument evidenceDocument = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, originalEvidencePath)));
        Dictionary<string, object?> evidence = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            evidenceDocument.RootElement.GetRawText())!;
        JsonElement sourceReference = evidenceDocument.RootElement.GetProperty("source_result");
        string originalSourcePath = sourceReference.GetProperty("path").GetString()!;
        using JsonDocument sourceDocument = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, originalSourcePath)));
        Dictionary<string, object?> source = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            sourceDocument.RootElement.GetRawText())!;
        JsonElement[] rows = sourceDocument.RootElement.GetProperty("component_dev_comparisons")
            .EnumerateArray().ToArray();
        Dictionary<string, object?> row = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            rows[0].GetRawText())!;
        row["false_positives"] = 2;
        source["component_dev_comparisons"] = new object[] { row };
        string driftSourcePath = "evidence/marker-center-synthetic-dev-drift-source.json";
        byte[] sourceBytes = JsonSerializer.SerializeToUtf8Bytes(source);
        File.WriteAllBytes(Path.Combine(root, driftSourcePath), sourceBytes);
        evidence["source_result"] = new
        {
            path = driftSourcePath,
            sha256 = FrozenCandidateBinding.Hash(sourceBytes),
        };
        string driftEvidencePath = "evidence/marker-center-synthetic-dev-drift.json";
        byte[] evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(evidence);
        File.WriteAllBytes(Path.Combine(root, driftEvidencePath), evidenceBytes);
        return (driftEvidencePath, FrozenCandidateBinding.Hash(evidenceBytes));
    }

    private static (string Path, string Sha) WriteMarkerDevProhibitedRateDrift(
        string root, JsonElement originalReference)
    {
        string originalEvidencePath = originalReference.GetProperty("path").GetString()!;
        JsonObject evidence = JsonNode.Parse(
            File.ReadAllBytes(Path.Combine(root, originalEvidencePath)))!.AsObject();
        string originalSourcePath = evidence["source_result"]!["path"]!.GetValue<string>();
        JsonObject source = JsonNode.Parse(
            File.ReadAllBytes(Path.Combine(root, originalSourcePath)))!.AsObject();
        source["component_dev_comparisons"]![0]!["prohibited_structure_hit_rate"] = 0.03;
        source["family_dev_comparisons"]![0]!["prohibited_structure_hit_rate"] = 0.03;
        string driftSourcePath = "evidence/marker-center-synthetic-dev-prohibited-source.json";
        byte[] sourceBytes = JsonSerializer.SerializeToUtf8Bytes(source);
        File.WriteAllBytes(Path.Combine(root, driftSourcePath), sourceBytes);
        evidence["source_result"] = JsonSerializer.SerializeToNode(new
        {
            path = driftSourcePath,
            sha256 = FrozenCandidateBinding.Hash(sourceBytes),
        });
        foreach (JsonNode? benchmark in evidence["benchmarks"]!.AsArray())
        {
            benchmark!["prohibited_structure_hit_rate"] = 0.03;
        }
        string driftEvidencePath = "evidence/marker-center-synthetic-dev-prohibited.json";
        byte[] evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(evidence);
        File.WriteAllBytes(Path.Combine(root, driftEvidencePath), evidenceBytes);
        return (driftEvidencePath, FrozenCandidateBinding.Hash(evidenceBytes));
    }

    private static FrozenRealCorpusSelection Inventory(string split, int count)
    {
        FrozenRealCorpusProject[] projects = Enumerable.Range(0, count)
            .Select(index => new FrozenRealCorpusProject(
                $"fixture-{index:D3}.dig", $"case-{index:D3}", $"study-{index:D3}", split))
            .ToArray();
        return new FrozenRealCorpusSelection(split, 171, 120, 51,
            new string('1', 64), new string('2', 64), projects);
    }

    private static void Require(bool condition, string code)
    {
        if (!condition)
        {
            throw new InvalidOperationException(code);
        }
    }

    private static void ExpectFailure<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException("EXPECTED_REAL_ADMISSION_FAILURE_MISSING");
    }

    private static void ExpectFailureCode<T>(Action action, string code) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception) when (exception.Message == code)
        {
            return;
        }
        throw new InvalidOperationException("EXPECTED_REAL_ADMISSION_FAILURE_CODE_MISSING");
    }
}
