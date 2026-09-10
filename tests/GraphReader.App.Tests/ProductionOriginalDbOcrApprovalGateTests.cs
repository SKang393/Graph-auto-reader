// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionOriginalDbOcrApprovalGateTests
{
    private static readonly ModelIdentity Detection = new(
        "future-approved-original-db-detector", "1.0.0", new string('a', 64), "detector.onnx");
    private static readonly ModelIdentity Recognition = new(
        "future-approved-ocr-recognizer", "5.0.0", new string('b', 64), "recognizer.onnx");

    [TestMethod]
    public void ExactFullOcrDevSchemaAcceptsAllFiveCanonicalBars()
    {
        EvidenceFixture fixture = CreateEvidence();

        Validate(fixture);
    }

    [TestMethod]
    [DataRow("precision")]
    [DataRow("recall")]
    [DataRow("recognition")]
    [DataRow("character_error_rate")]
    [DataRow("role")]
    public void FullOcrDevSchemaRejectsEachFailedCanonicalBar(string failedMetric)
    {
        EvidenceFixture fixture = CreateEvidence(failedMetric);

        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture));
    }

    [TestMethod]
    public void FullOcrDevSchemaRejectsWrongEvaluatorOrCandidateIdentity()
    {
        EvidenceFixture fixture = CreateEvidence();
        JsonObject score = ParseObject(fixture.Score);
        score["inputs"]!["evaluator_sha256"] = new string('c', 64);
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture with { Score = Serialize(score) }));

        JsonObject candidate = ParseObject(fixture.Candidate);
        candidate["detector"]!["model_sha256"] = new string('d', 64);
        byte[] changedCandidate = Serialize(candidate);
        EvidenceFixture rebound = fixture with
        {
            Candidate = changedCandidate,
            CandidateSha256 = Sha256(changedCandidate),
        };
        JsonObject reboundScore = ParseObject(rebound.Score);
        reboundScore["inputs"]!["candidate"]!["sha256"] = rebound.CandidateSha256;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Validate(rebound with { Score = Serialize(reboundScore) }));
    }

    [TestMethod]
    public void FullOcrDevSchemaRejectsWeakenedCanonicalBarAndInconsistentCounts()
    {
        EvidenceFixture fixture = CreateEvidence();
        JsonObject bars = ParseObject(fixture.Bars);
        bars["tier1_reviewable_error"]!["recognition_exact_match_minimum"] = 0.90;
        byte[] weakenedBars = Serialize(bars);
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture with
        {
            Bars = weakenedBars,
            BarsSha256 = Sha256(weakenedBars),
        }));

        JsonObject score = ParseObject(fixture.Score);
        score["metrics"]!["validation"]!["matched_pair_edit_count"] = 1;
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture with { Score = Serialize(score) }));
    }

    [TestMethod]
    public void SyntheticSealedEvidenceRemainsUnavailableUntilItsExactSchemaIsImplemented()
    {
        byte[] plausible = Serialize(new Dictionary<string, object?>
        {
            ["schema"] = "graphreader.full-ocr-synthetic-sealed-score.v1",
            ["status"] = "pass",
            ["production_approval"] = true,
        });

        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
            ProductionOriginalDbOcrApprovalGate.ValidateSyntheticSealedSourceForTest(plausible));
        StringAssert.Contains(error.Message, "is not supported");
    }

    [TestMethod]
    public void FullOcrDevRejectsSelfConsistentSubsetAndChangedMatchingRule()
    {
        EvidenceFixture fixture = CreateEvidence();
        JsonObject score = ParseObject(fixture.Score);
        JsonNode raw = score["raw_detector_geometry"]!["validation"]!;
        raw["truth_region_count"] = 1;
        raw["true_positives"] = 1;
        raw["false_positives"] = 0;
        raw["false_negatives"] = 0;
        raw["predicted_region_count"] = 1;
        raw["precision"] = 1.0;
        raw["recall"] = 1.0;
        JsonNode metrics = score["metrics"]!["validation"]!;
        metrics["truth_region_count"] = 1;
        metrics["geometry_matched_region_count"] = 1;
        metrics["geometry_false_positive_count"] = 0;
        metrics["geometry_false_negative_count"] = 0;
        metrics["recognition_exact_count"] = 1;
        metrics["recognition_exact_accuracy"] = 1.0;
        metrics["role_correct_count"] = 1;
        metrics["role_accuracy"] = 1.0;
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture with { Score = Serialize(score) }));

        score = ParseObject(fixture.Score);
        score["raw_detector_geometry"]!["validation"]!["intersection_over_union_minimum"] = 0.1;
        score["metrics"]!["validation"]!["intersection_over_union_minimum"] = 0.1;
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(fixture with { Score = Serialize(score) }));
    }

    [TestMethod]
    public void ManifestContractFingerprintBindsRuntimeContractWithoutBenchmarkCycle()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "detector.json");
            Dictionary<string, object?> manifest = CreateManifest();
            File.WriteAllBytes(manifestPath, Serialize(manifest));
            string first = ProductionOriginalDbOcrApprovalGate
                .ComputeManifestContractFingerprintForTest(manifestPath, "ocr_detection");

            manifest["benchmarks"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["profile"] = ProductionOriginalDbOcrApprovalGate.Profile,
                    ["evidence_sha256"] = new string('e', 64),
                },
            };
            File.WriteAllBytes(manifestPath, Serialize(manifest));
            string benchmarkChanged = ProductionOriginalDbOcrApprovalGate
                .ComputeManifestContractFingerprintForTest(manifestPath, "ocr_detection");
            Assert.AreEqual(first, benchmarkChanged);

            ((Dictionary<string, object?>)manifest["preprocessing"]!)["maximum_side_length"] = 1920;
            File.WriteAllBytes(manifestPath, Serialize(manifest));
            string runtimeChanged = ProductionOriginalDbOcrApprovalGate
                .ComputeManifestContractFingerprintForTest(manifestPath, "ocr_detection");
            Assert.AreNotEqual(first, runtimeChanged);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Utf8BomPreservesManifestContractAndDuplicateChecks()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(root, "detector.json");
            byte[] manifest = Serialize(CreateManifest());
            File.WriteAllBytes(path, manifest);
            string expected = ProductionOriginalDbOcrApprovalGate
                .ComputeManifestContractFingerprintForTest(path, "ocr_detection");

            File.WriteAllBytes(path, [0xef, 0xbb, 0xbf, .. manifest]);
            Assert.AreEqual(expected, ProductionOriginalDbOcrApprovalGate
                .ComputeManifestContractFingerprintForTest(path, "ocr_detection"));

            File.WriteAllBytes(path,
                [0xef, 0xbb, 0xbf, .. "{\"task\":\"ocr_detection\",\"task\":\"ocr_detection\"}"u8.ToArray()]);
            Assert.ThrowsExactly<InvalidDataException>(() => ProductionOriginalDbOcrApprovalGate
                .ComputeManifestContractFingerprintForTest(path, "ocr_detection"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Validate(EvidenceFixture fixture) =>
        ProductionOriginalDbOcrApprovalGate.ValidateFullOcrDevSourceForTest(
            fixture.Score,
            fixture.Candidate,
            fixture.CandidateSha256,
            fixture.Bars,
            fixture.BarsSha256,
            Detection,
            Recognition);

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void EvaluatedRuntimeRejectsChangedOrDuplicatedShippedDependencies(bool wholeWorkflow)
    {
        string nativeSha = new('c', 64);
        JsonObject candidate = new()
        {
            ["native_files"] = new JsonArray(new JsonObject
            {
                ["role"] = "opencvsharp_extern",
                ["file"] = "runtime/OpenCvSharpExtern.dll",
                ["sha256"] = nativeSha,
            }),
            ["managed_files"] = new JsonArray(new[]
            {
                typeof(ProductionOcrAdapter).Assembly,
                typeof(GraphReader.Ocr.OcrPipeline).Assembly,
                typeof(InferenceRuntime).Assembly,
            }.Select(assembly => (JsonNode)new JsonObject
            {
                ["file"] = "runtime/" + Path.GetFileName(assembly.Location),
                ["sha256"] = Sha256(File.ReadAllBytes(assembly.Location)),
            }).ToArray()),
        };
        if (!wholeWorkflow)
        {
            candidate["native_sha256"] = nativeSha;
            candidate.Remove("native_files");
            JsonArray assemblies = candidate["managed_files"]!.AsArray();
            foreach (JsonNode? row in assemblies)
            {
                row!["path"] = row["file"]!.DeepClone();
                row.AsObject().Remove("file");
            }
            candidate.Remove("managed_files");
            candidate["execution_assemblies"] = assemblies;
        }
        ProductionOriginalDbOcrApprovalGate.ValidateEvaluatedRuntime(Serialize(candidate), nativeSha, wholeWorkflow);

        if (!wholeWorkflow)
        {
            JsonObject changedNative = ParseObject(Serialize(candidate));
            changedNative["native_sha256"] = new string('0', 64);
            Assert.ThrowsExactly<InvalidDataException>(() =>
                ProductionOriginalDbOcrApprovalGate.ValidateEvaluatedRuntime(Serialize(changedNative), nativeSha, false));
        }

        foreach (string collection in wholeWorkflow
                     ? new[] { "native_files", "managed_files" }
                     : new[] { "execution_assemblies" })
        {
            for (int index = 0; index < candidate[collection]!.AsArray().Count; index++)
            {
                JsonObject changed = ParseObject(Serialize(candidate));
                changed[collection]![index]!["sha256"] = new string('0', 64);
                Assert.ThrowsExactly<InvalidDataException>(() =>
                    ProductionOriginalDbOcrApprovalGate.ValidateEvaluatedRuntime(Serialize(changed), nativeSha, wholeWorkflow));
            }
            JsonObject duplicated = ParseObject(Serialize(candidate));
            duplicated[collection]!.AsArray().Add(duplicated[collection]![0]!.DeepClone());
            Assert.ThrowsExactly<InvalidDataException>(() =>
                ProductionOriginalDbOcrApprovalGate.ValidateEvaluatedRuntime(Serialize(duplicated), nativeSha, wholeWorkflow));
        }
    }

    [TestMethod]
    public void EvaluatedManifestContractsRejectChangedPreprocessingEvenWithSameModelPayload()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            Dictionary<string, object?> detector = CreateManifest();
            Dictionary<string, object?> recognizer = CreateManifest();
            recognizer["task"] = "ocr_recognition";
            recognizer["model_id"] = Recognition.ModelId;
            recognizer["model_version"] = Recognition.Version;
            recognizer["sha256"] = Recognition.Sha256;
            byte[] detectorBytes = Serialize(detector);
            byte[] recognizerBytes = Serialize(recognizer);
            string detectorPath = Path.Combine(root, "detector.json");
            string recognizerPath = Path.Combine(root, "recognizer.json");
            File.WriteAllBytes(detectorPath, detectorBytes);
            File.WriteAllBytes(recognizerPath, recognizerBytes);
            string detectorContract = ProductionOriginalDbOcrApprovalGate
                .ComputeManifestContractFingerprintForTest(detectorPath, "ocr_detection");
            string recognizerContract = ProductionOriginalDbOcrApprovalGate
                .ComputeManifestContractFingerprintForTest(recognizerPath, "ocr_recognition");
            foreach (bool wholeWorkflow in new[] { false, true })
            {
                JsonObject candidate = new()
                {
                    [wholeWorkflow ? "ocr_detection" : "detector"] = wholeWorkflow
                        ? new JsonObject { ["manifest"] = new JsonObject { ["sha256"] = Sha256(detectorBytes) } }
                        : new JsonObject { ["manifest_sha256"] = Sha256(detectorBytes) },
                    [wholeWorkflow ? "ocr_recognition" : "recognizer"] = wholeWorkflow
                        ? new JsonObject { ["manifest"] = new JsonObject { ["sha256"] = Sha256(recognizerBytes) } }
                        : new JsonObject { ["manifest_sha256"] = Sha256(recognizerBytes) },
                };
                ProductionOriginalDbOcrApprovalGate.ValidateEvaluatedManifestContracts(
                    Serialize(candidate), detectorBytes, recognizerBytes,
                    detectorContract, recognizerContract, wholeWorkflow);
                JsonObject changed = ParseObject(detectorBytes);
                changed["preprocessing"]!["maximum_side_length"] = 640;
                byte[] changedBytes = Serialize(changed);
                JsonNode model = candidate[wholeWorkflow ? "ocr_detection" : "detector"]!;
                if (wholeWorkflow) model["manifest"]!["sha256"] = Sha256(changedBytes);
                else model["manifest_sha256"] = Sha256(changedBytes);
                Assert.ThrowsExactly<InvalidDataException>(() =>
                    ProductionOriginalDbOcrApprovalGate.ValidateEvaluatedManifestContracts(
                        Serialize(candidate), changedBytes, recognizerBytes,
                        detectorContract, recognizerContract, wholeWorkflow));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static EvidenceFixture CreateEvidence(string? failedMetric = null)
    {
        int truePositive = failedMetric == "recall" ? 173 : 174;
        int falseNegative = 183 - truePositive;
        int falsePositive = failedMetric == "precision" ? 10 : 9;
        int predicted = truePositive + falsePositive;
        int exact = failedMetric == "recognition" ? 173 : 174;
        int role = failedMetric == "role" ? 173 : 174;
        int characterErrors = failedMetric == "character_error_rate" ? 51 : 50;

        byte[] candidate = Serialize(new Dictionary<string, object?>
        {
            ["schema"] = "graphreader.frozen-db-head-ocr-candidate.v1",
            ["composition_version"] = ProductionOcrAdapter.OriginalDbCandidateCompositionVersion,
            ["production_approved"] = false,
            ["detector"] = CandidateModel(Detection),
            ["recognizer"] = CandidateModel(Recognition),
        });
        string candidateSha = Sha256(candidate);
        byte[] bars = Serialize(new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["policy_id"] = "graphreader-goal22-tier1-v1",
            ["tier1_reviewable_error"] = new Dictionary<string, object?>
            {
                ["text_region_detection_precision_minimum"] = 0.95,
                ["text_region_detection_recall_minimum"] = 0.95,
                ["recognition_exact_match_minimum"] = 0.95,
                ["character_error_rate_maximum"] = 0.05,
                ["role_accuracy_minimum"] = 0.95,
            },
        });
        string barsSha = Sha256(bars);
        byte[] score = Serialize(new Dictionary<string, object?>
        {
            ["schema"] = ProductionOriginalDbOcrApprovalGate.FullOcrDevSchema,
            ["status"] = "diagnostic_only_unapproved",
            ["synthetic_only"] = true,
            ["private_data"] = false,
            ["sealed_data"] = false,
            ["production_approval"] = false,
            ["release_eligible"] = false,
            ["optimizer_steps"] = 0,
            ["integrity"] = new Dictionary<string, object?>
            {
                ["full_source_truth_count"] = 892,
                ["panel_count"] = 37,
                ["source_count"] = 23,
                ["failed_panels_remain_in_full_source_denominator"] = true,
                ["unmatched_predictions_count_as_character_insertions"] = true,
                ["unmatched_truths_count_as_exact_and_role_failures_and_full_text_deletions"] = true,
            },
            ["truth_isolation"] = new Dictionary<string, object?>
            {
                ["all_runtime_and_candidate_evidence_authenticated_before_truth_regeneration"] = true,
                ["runtime_received_truth"] = false,
                ["truth_regenerated_once_only_in_python_evaluator"] = true,
            },
            ["inputs"] = new Dictionary<string, object?>
            {
                ["evaluator_sha256"] = ProductionOriginalDbOcrApprovalGate.FullOcrDevEvaluatorSha256,
                ["candidate"] = new Dictionary<string, object?> { ["sha256"] = candidateSha },
            },
            ["acceptance_bar_reference"] = new Dictionary<string, object?> { ["sha256"] = barsSha },
            ["raw_detector_geometry"] = new Dictionary<string, object?>
            {
                ["validation"] = new Dictionary<string, object?>
                {
                    ["truth_region_count"] = 183,
                    ["intersection_over_union_minimum"] = 0.5,
                    ["true_positives"] = truePositive,
                    ["false_positives"] = falsePositive,
                    ["false_negatives"] = falseNegative,
                    ["predicted_region_count"] = predicted,
                    ["precision"] = Ratio(truePositive, predicted),
                    ["recall"] = Ratio(truePositive, 183),
                },
            },
            ["metrics"] = new Dictionary<string, object?>
            {
                ["validation"] = new Dictionary<string, object?>
                {
                    ["truth_region_count"] = 183,
                    ["geometry_matched_region_count"] = truePositive,
                    ["intersection_over_union_minimum"] = 0.5,
                    ["geometry_false_positive_count"] = falsePositive,
                    ["geometry_false_negative_count"] = falseNegative,
                    ["recognition_exact_count"] = exact,
                    ["recognition_exact_accuracy"] = Ratio(exact, 183),
                    ["role_correct_count"] = role,
                    ["role_accuracy"] = Ratio(role, 183),
                    ["character_error_count"] = characterErrors,
                    ["truth_character_count"] = 1019,
                    ["character_error_rate"] = Ratio(characterErrors, 1019),
                    ["matched_pair_edit_count"] = 0,
                    ["unmatched_prediction_insertion_edit_count"] = 0,
                    ["unmatched_truth_deletion_edit_count"] = characterErrors,
                },
            },
        });
        return new EvidenceFixture(score, candidate, candidateSha, bars, barsSha);
    }

    private static Dictionary<string, object?> CandidateModel(ModelIdentity identity) => new()
    {
        ["model_id"] = identity.ModelId,
        ["model_version"] = identity.Version,
        ["model_sha256"] = identity.Sha256,
    };

    private static Dictionary<string, object?> CreateManifest() => new()
    {
        ["model_id"] = Detection.ModelId,
        ["model_version"] = Detection.Version,
        ["task"] = "ocr_detection",
        ["sha256"] = Detection.Sha256,
        ["inputs"] = new object[] { new Dictionary<string, object?> { ["name"] = "x" } },
        ["outputs"] = new object[] { new Dictionary<string, object?> { ["name"] = "fetch_name_0" } },
        ["preprocessing"] = new Dictionary<string, object?>
        {
            ["channel_order"] = "BGR",
            ["maximum_side_length"] = 960,
            ["dimension_multiple"] = 128,
        },
        ["postprocessing"] = new Dictionary<string, object?> { ["algorithm"] = "db_postprocess_v1" },
        ["benchmarks"] = Array.Empty<object>(),
    };

    private static JsonObject ParseObject(byte[] bytes) =>
        JsonNode.Parse(bytes)!.AsObject();

    private static byte[] Serialize(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static double Ratio(int numerator, int denominator) => (double)numerator / denominator;

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "GraphReader.OriginalDbApprovalGate",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record EvidenceFixture(
        byte[] Score,
        byte[] Candidate,
        string CandidateSha256,
        byte[] Bars,
        string BarsSha256);
}
