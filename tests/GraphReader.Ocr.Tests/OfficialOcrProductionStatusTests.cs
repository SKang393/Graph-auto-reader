// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class OfficialOcrProductionStatusTests
{
    private const string DetectionSha256 =
        "d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb";
    private const string RecognitionSha256 =
        "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743";
    private const string LicenseSha256 =
        "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4";
    private const string NoticeSha256 =
        "8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729";
    private const string StructureConsensusExecutionSourceCommit =
        "7fa6abee5deaf7c17ad19169928290b96a65ce2a";
    private static readonly Dictionary<string, string> ReviewedSourcesChangedAfterConsumedRun =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/GraphReader.Ocr/LocalOnnxTextRegionDetector.cs"] =
                "c17cbd77bc646f7646f2f3f60b2120be735201b79c0b32a48318a30464b0aa38",
            ["src/GraphReader.App/Integration/Workflow/ProductionOcrAdapter.cs"] =
                "e57550f89e7eff4656ea6b9d74f0f2f0473da852471c8dcfa9647bb2e9f9e1fd",
            // Goal 22 commit 48c25d6 repaired current raster preprocessing.
            // Keep the consumed run bound to its exact historical source;
            // that failed run does not approve the repaired implementation.
            ["src/GraphReader.App/Integration/Workflow/ProductionRasterFrameDecoder.cs"] =
                "29e93889ca0da9f98d2070aaa8a22d24d6b9975a7bb873a3c82832f8eff1a931",
        };

    [TestMethod]
    public void FailedOfficialCandidatesRemainUnmanifestedUntilEveryGatePasses()
    {
        string root = FindRepositoryRoot();
        string manifestDirectory = Path.Combine(root, "models", "manifest", "ocr");
        string[] manifests = Directory.GetFiles(manifestDirectory, "*.json", SearchOption.TopDirectoryOnly);

        Assert.AreEqual(
            0,
            manifests.Length,
            "A tracked OCR JSON manifest was added before the official pair completed its direct " +
            "public, sealed, notice, model-store, and packaging gates. Update this intentional " +
            "fail-closed lock only with the complete approval evidence.");

        string audit = File.ReadAllText(Path.Combine(
            manifestDirectory,
            "PP_OCRV5_OFFICIAL_ARCHIVE_AUDIT.md"));
        string promotion = File.ReadAllText(Path.Combine(
            manifestDirectory,
            "OFFICIAL_OCR_PRODUCTION_PROMOTION.md"));
        string licensePath = Path.Combine(
            root,
            "LICENSES",
            "PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt");
        string noticePath = Path.Combine(
            root,
            "LICENSES",
            "PaddlePaddle-PP-OCRv5-Models-Notice.txt");

        StringAssert.Contains(audit, DetectionSha256);
        StringAssert.Contains(audit, RecognitionSha256);
        StringAssert.Contains(audit, "benchmark ran and failed every");
        StringAssert.Contains(audit, "detection exact count `18/220`");
        StringAssert.Contains(audit, LicenseSha256);
        StringAssert.Contains(audit, NoticeSha256);
        StringAssert.Contains(promotion, "intentional fail-closed state");
        StringAssert.Contains(promotion, "status=fail");
        StringAssert.Contains(promotion, "cannot satisfy the strengthened gate");
        StringAssert.Contains(promotion, "probability_with_1e-5_clamp");
        StringAssert.Contains(promotion, "consumed 500-case attempt");
        StringAssert.Contains(promotion, "production_approval = true");
        StringAssert.Contains(promotion, "It must not emit release artifacts.");
        Assert.AreEqual(LicenseSha256, Sha256(licensePath));
        Assert.AreEqual(NoticeSha256, Sha256(noticePath));
    }

    [TestMethod]
    public void StructureConsensusRunRemainsSourceBoundAndRecordedAsFailedClosed()
    {
        string root = FindRepositoryRoot();
        string protocolPath = Path.Combine(
            root,
            "ml",
            "ocr",
            "official_bakeoff",
            "STRUCTURE_CONSENSUS_GATE_PROTOCOL.json");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(protocolPath));
        System.Text.Json.JsonElement protocol = document.RootElement;

        Assert.AreEqual(
            "frozen_before_fixture_generation_and_inference",
            protocol.GetProperty("status").GetString());
        Assert.AreEqual(
            ProductionOcrProfile,
            protocol.GetProperty("profile").GetString());
        Assert.IsFalse(protocol.GetProperty("private_data").GetBoolean());
        Assert.IsFalse(protocol.GetProperty("chandler_used").GetBoolean());
        Assert.AreEqual(
            "graph-structure-consensus-v1",
            protocol.GetProperty("candidate").GetProperty("composition_id").GetString());
        Assert.AreEqual(
            "1fc3b2e72f89cbfb0d8854ec8701368e7ae764cbd5c6fef17b7e497d06ec9f09",
            protocol.GetProperty("prior_exposed_split_forbidden")
                .GetProperty("split_sha256")
                .GetString());
        Assert.AreEqual(
            1,
            protocol.GetProperty("experiment_budget")
                .GetProperty("official_composition_evaluations")
                .GetInt32());
        Assert.AreEqual(
            0,
            protocol.GetProperty("experiment_budget")
                .GetProperty("workflow_changes_after_inference")
                .GetInt32());

        foreach (System.Text.Json.JsonProperty source in
            protocol.GetProperty("reviewed_source_sha256").EnumerateObject())
        {
            string sourcePath = Path.Combine(root, source.Name.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(sourcePath), $"Preregistered OCR source is missing: {source.Name}");
            string protocolSha256 = source.Value.GetString()!;
            if (ReviewedSourcesChangedAfterConsumedRun.TryGetValue(
                source.Name,
                out string? historicalSha256))
            {
                Assert.AreEqual(
                    historicalSha256,
                    protocolSha256,
                    $"The consumed gate's historical source hash changed: {source.Name}");
            }
            else
            {
                Assert.AreEqual(
                    protocolSha256,
                    Sha256(sourcePath),
                    $"Preregistered OCR source changed without an immutable post-run binding: {source.Name}");
            }
        }

        string readme = File.ReadAllText(Path.Combine(
            root,
            "ml",
            "ocr",
            "official_bakeoff",
            "README.md"));
        StringAssert.Contains(readme, "The authoritative 500-case split was frozen once before inference");
        StringAssert.Contains(readme, StructureConsensusExecutionSourceCommit);
        StringAssert.Contains(
            readme,
            "8685a3dfcb8212f612115c20d0f70437e0738fa1c4d86743cfd0e50bc5a41a8d");
        StringAssert.Contains(readme, "frozen and checksum-bound");
        StringAssert.Contains(readme, "The single authorized official composition execution then failed closed");
        StringAssert.Contains(readme, "BLOCKED: Detector output is not a probability tensor.");
        StringAssert.Contains(readme, "must not be rerun, repaired, or tuned");
        StringAssert.Contains(readme, "probability_with_1e-5_clamp");
        StringAssert.Contains(readme, "disjoint split, a new frozen protocol");
        StringAssert.Contains(readme, "release authorization therefore remain false");

        string evaluationRequirements = File.ReadAllText(Path.Combine(
            root,
            "ml",
            "ocr",
            "official_bakeoff",
            "requirements-structure-consensus.txt"));
        StringAssert.Contains(evaluationRequirements, "-r requirements-conversion.txt");
        StringAssert.Contains(evaluationRequirements, "opencv-python-headless==4.10.0.84");
        StringAssert.Contains(
            evaluationRequirements,
            "afcf28bd1209dd58810d33defb622b325d3cbe49dcd7a43a902982c33e5fad05");
    }

    [TestMethod]
    public void BoundedV2ConsumesOneVerifiedPublicEvaluationAndRemainsUnapproved()
    {
        string root = FindRepositoryRoot();
        string protocolPath = Path.Combine(
            root,
            "ml",
            "ocr",
            "official_bakeoff",
            "STRUCTURE_CONSENSUS_V2_GATE_PROTOCOL.json");
        string evaluationConfigPath = Path.Combine(
            root,
            "ml",
            "ocr",
            "official_bakeoff",
            "STRUCTURE_CONSENSUS_V2_EVALUATION_CONFIG.json");
        string resultPath = Path.Combine(
            root,
            "ml",
            "ocr",
            "official_bakeoff",
            "STRUCTURE_CONSENSUS_V2_RESULT.json");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(protocolPath));
        System.Text.Json.JsonElement protocol = document.RootElement;

        Assert.AreEqual(
            "frozen_before_fixture_generation_and_inference",
            protocol.GetProperty("status").GetString());
        Assert.AreEqual(
            "bounded_probability_runtime_activation",
            protocol.GetProperty("defect_class").GetString());
        Assert.AreEqual(
            "probability_with_1e-5_clamp",
            protocol.GetProperty("candidate").GetProperty("output_activation").GetString());
        Assert.AreEqual(
            2,
            protocol.GetProperty("prior_exposed_splits_forbidden").GetArrayLength());
        Assert.AreEqual(
            1,
            protocol.GetProperty("experiment_budget")
                .GetProperty("official_composition_evaluations")
                .GetInt32());
        Assert.IsTrue(File.Exists(evaluationConfigPath));
        using System.Text.Json.JsonDocument configDocument = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(evaluationConfigPath));
        System.Text.Json.JsonElement config = configDocument.RootElement;
        Assert.AreEqual("authorized_after_single_freeze", config.GetProperty("status").GetString());
        Assert.AreEqual(
            "a7f407aa47e406348e1173ce0b30b3ef1d98a7ae1ec314deb618012f5127f998",
            config.GetProperty("sealed_split_sha256").GetString());
        Assert.AreEqual(
            "a1f978cf1154154bf72e1130bd943618dd0847f046fe56deb52e19466799361d",
            config.GetProperty("fixture_archive_sha256").GetString());
        Assert.AreEqual(
            "3cd3033acc80dd9362f2fdfc828c882dd4cca40d9f76e409b758ec6cf6c94d34",
            config.GetProperty("source_inventory_sha256").GetString());
        Assert.AreEqual(0, config.GetProperty("public_official_model_evaluations_completed").GetInt32());
        Assert.IsFalse(config.GetProperty("production_approval").GetBoolean());
        Assert.IsFalse(config.GetProperty("release_eligible").GetBoolean());

        using System.Text.Json.JsonDocument resultDocument = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(resultPath));
        System.Text.Json.JsonElement result = resultDocument.RootElement;
        Assert.AreEqual("fail", result.GetProperty("status").GetString());
        Assert.AreEqual(1, result.GetProperty("evaluation_count").GetInt32());
        Assert.IsFalse(result.GetProperty("rerun_permitted").GetBoolean());
        Assert.IsFalse(result.GetProperty("production_approval").GetBoolean());
        Assert.IsFalse(result.GetProperty("release_eligible").GetBoolean());
        Assert.IsFalse(result.GetProperty("private_data").GetBoolean());
        Assert.IsFalse(result.GetProperty("chandler_used").GetBoolean());
        Assert.AreEqual(
            "fbd0d960a9a996bbf2dbaba28d004234118bab4ecbf556d8a25e0a2dfde54d10",
            result.GetProperty("report_sha256").GetString());
        Assert.AreEqual(
            "e9aff70383e4ea30bec62fedd6c64483d103b0467518d07e07a34c77e02498ca",
            result.GetProperty("result_seal_sha256").GetString());
        Assert.AreEqual(0.205, result.GetProperty("metrics").GetProperty("validation_exact_match").GetDouble());
        Assert.AreEqual(10, result.GetProperty("metrics").GetProperty("exclusion_false_region_count").GetInt32());
        Assert.AreEqual(
            1,
            result.GetProperty("bounded_activation").GetProperty("clamped_value_count").GetInt32());

        string readme = File.ReadAllText(Path.Combine(
            root,
            "ml",
            "ocr",
            "official_bakeoff",
            "README.md"));
        StringAssert.Contains(
            readme,
            "0ee2ec0ef4a9f2f7f7f373da7389b84513f254c8642e4ddd5fd5427518d5e133");
        StringAssert.Contains(readme, "single V2 fixture freeze produced 500 new public synthetic cases");
        StringAssert.Contains(readme, "single authorized official CPU execution is consumed");
        StringAssert.Contains(readme, "Production approval and release eligibility remain false");
        StringAssert.Contains(readme, "Chandler and every private image remain prohibited");
    }

    private const string ProductionOcrProfile =
        "graphreader-ocr-structure-consensus-public-gate-v1";

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GraphAutoReader.slnx")) &&
                File.Exists(Path.Combine(directory.FullName, "contracts", "model-manifest.schema.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Graph Auto Reader repository root.");
    }

    private static string Sha256(string path) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
}
