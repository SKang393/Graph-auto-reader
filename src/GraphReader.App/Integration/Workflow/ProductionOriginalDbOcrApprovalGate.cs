// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Inference;

namespace GraphReader.App.Integration.Workflow;

/// <summary>
/// Portable production-evidence boundary for the original-image PP-OCRv5 DB
/// composition. The gate deliberately supports only evidence schemas whose
/// direct source semantics have been reviewed in this executable.
/// </summary>
internal static class ProductionOriginalDbOcrApprovalGate
{
    internal const string Schema = "graphreader.ocr-original-db-production-gate.v1";
    internal const string Profile = "ocr-original-db-production-gate-v1";
    internal const string FullOcrDevSchema = "graphreader.full-ocr-candidate-score.v2";
    internal const string FullOcrDevEvaluatorSha256 =
        "8b879664c33e83f2aaec0e637f4f0f4df0f9701fe04ac43867672dde2e662656";
    private const int MaximumEvidenceBytes = 32 * 1024 * 1024;
    private const double NumericTolerance = 1e-12;

    internal static bool UsesProfile(
        ResolvedProductionModel detectionModel,
        ResolvedProductionModel recognitionModel)
    {
        ArgumentNullException.ThrowIfNull(detectionModel);
        ArgumentNullException.ThrowIfNull(recognitionModel);
        bool detection = ManifestUsesProfile(detectionModel.ManifestPath);
        bool recognition = ManifestUsesProfile(recognitionModel.ManifestPath);
        if (detection != recognition)
        {
            throw new InvalidDataException(
                "The OCR detector and recognizer select different production approval profiles.");
        }

        if (detection && (!string.Equals(
                detectionModel.BenchmarkEvidenceSha256,
                recognitionModel.BenchmarkEvidenceSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFullPath(detectionModel.BenchmarkEvidencePath),
                Path.GetFullPath(recognitionModel.BenchmarkEvidencePath),
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The original-DB OCR pair must select one checksum-exact production gate.");
        }

        return detection;
    }

    internal static void Validate(
        ResolvedProductionModel detectionModel,
        ResolvedProductionModel recognitionModel,
        string reviewedOpenCvRuntimeSha256)
    {
        ArgumentNullException.ThrowIfNull(detectionModel);
        ArgumentNullException.ThrowIfNull(recognitionModel);
        if (!UsesProfile(detectionModel, recognitionModel))
        {
            throw new InvalidDataException("The OCR pair does not select the original-DB production gate.");
        }

        VerifyFile(
            detectionModel.BenchmarkEvidencePath,
            detectionModel.BenchmarkEvidenceSha256,
            "original-DB OCR production gate");
        byte[] gateBytes = ReadBounded(detectionModel.BenchmarkEvidencePath, "original-DB OCR production gate");
        using JsonDocument document = Parse(gateBytes, "original-DB OCR production gate");
        JsonElement root = document.RootElement;
        RequireString(root, "schema", Schema, "original-DB OCR production gate");
        RequireString(root, "profile", Profile, "original-DB OCR production gate");
        RequireString(root, "composition_id", ProductionOcrAdapter.OriginalDbCandidateCompositionVersion,
            "original-DB OCR production gate");
        RequireString(root, "status", "pass", "original-DB OCR production gate");
        RequireString(root, "scope", "synthetic-dev-sealed-and-real-sealed-aggregate",
            "original-DB OCR production gate");
        RequireBoolean(root, "release_eligible", true, "original-DB OCR production gate");
        RequireBoolean(root, "production_approval", true, "original-DB OCR production gate");
        RequireBoolean(root, "private_data", false, "original-DB OCR production gate");
        RequireBoolean(root, "case_level_output", false, "original-DB OCR production gate");
        RequireBoolean(root, "truth_rows_output", false, "original-DB OCR production gate");
        RequireBoolean(root, "prediction_output", false, "original-DB OCR production gate");
        RequireBoolean(root, "pixel_output", false, "original-DB OCR production gate");
        RequireString(root, "provider", "cpu", "original-DB OCR production gate");
        RequireString(root, "coordinate_space", "original_pixels", "original-DB OCR production gate");
        RequireString(root, "detector_input", "original_bgr", "original-DB OCR production gate");
        RequireInt32(root, "detector_maximum_side_length", 960, "original-DB OCR production gate");
        RequireInt32(root, "detector_dimension_multiple", 128, "original-DB OCR production gate");

        RequireModel(root, "detection_model", detectionModel.Identity);
        RequireModel(root, "recognition_model", recognitionModel.Identity);
        string detectorContract = ComputeManifestContractFingerprint(
            detectionModel.ManifestPath, "ocr_detection");
        string recognizerContract = ComputeManifestContractFingerprint(
            recognitionModel.ManifestPath, "ocr_recognition");
        RequireSha(root, "detector_contract_sha256", detectorContract,
            "original-DB OCR production gate");
        RequireSha(root, "recognizer_contract_sha256", recognizerContract,
            "original-DB OCR production gate");

        RequireApprovalBenchmark(detectionModel, detectorContract, recognizerContract);
        RequireApprovalBenchmark(recognitionModel, detectorContract, recognizerContract);

        JsonElement resources = RequireObject(root, "reviewed_resources", "original-DB OCR production gate");
        EmbeddedResource bars = ReadEmbedded(resources, "acceptance_bars", "application/json");
        EmbeddedResource devScore = ReadEmbedded(resources, "synthetic_dev_score", "application/json");
        EmbeddedResource devCandidate = ReadEmbedded(resources, "synthetic_dev_candidate", "application/json");
        EmbeddedResource devDetectorManifest = ReadEmbedded(resources, "synthetic_dev_detector_manifest", "application/json");
        EmbeddedResource devRecognizerManifest = ReadEmbedded(resources, "synthetic_dev_recognizer_manifest", "application/json");
        EmbeddedResource sealedScore = ReadEmbedded(resources, "synthetic_sealed_score", "application/json");
        EmbeddedResource realResult = ReadEmbedded(resources, "real_sealed_result", "application/json");
        EmbeddedResource realCandidate = ReadEmbedded(resources, "real_sealed_candidate_binding", "application/json");
        EmbeddedResource realProtocol = ReadEmbedded(resources, "real_sealed_protocol", "application/json");
        EmbeddedResource realDetectorManifest = ReadEmbedded(resources, "real_sealed_detector_manifest", "application/json");
        EmbeddedResource realRecognizerManifest = ReadEmbedded(resources, "real_sealed_recognizer_manifest", "application/json");

        CanonicalBars canonical = ReadAcceptanceBars(bars.Bytes);
        ValidateEvaluatedManifestContracts(devCandidate.Bytes,
            devDetectorManifest.Bytes, devRecognizerManifest.Bytes,
            detectorContract, recognizerContract, wholeWorkflow: false);
        ValidateEvaluatedRuntime(devCandidate.Bytes, reviewedOpenCvRuntimeSha256, wholeWorkflow: false);
        ValidateFullOcrDevSource(
            devScore.Bytes,
            devCandidate.Bytes,
            devCandidate.Sha256,
            bars.Sha256,
            detectionModel.Identity,
            recognitionModel.Identity,
            canonical);
        ValidateEvaluatedManifestContracts(realCandidate.Bytes,
            realDetectorManifest.Bytes, realRecognizerManifest.Bytes,
            detectorContract, recognizerContract, wholeWorkflow: true);
        ValidateEvaluatedRuntime(realCandidate.Bytes, reviewedOpenCvRuntimeSha256);
        ProductionOriginalDbRealSealedEvidence.Validate(
            realResult.Bytes, realCandidate.Bytes, realCandidate.Sha256,
            realProtocol.Bytes, realProtocol.Sha256, bars.Bytes, bars.Sha256,
            detectionModel.Identity, recognitionModel.Identity);
        ValidateEvaluatedRuntimeDependencies(realCandidate.Bytes);
        ValidateSyntheticSealedSource(sealedScore.Bytes);
    }

    internal static void ValidateWorkflow(
        ResolvedProductionModel detectionModel,
        IProductionAxisGeometryAdapter axis,
        IProductionMarkerCenterAdapter markerCenter,
        IProductionMarkerClassificationAdapter markerClassifier,
        IProductionArtifactMaskAdapter? artifactMask,
        IProductionLegendReasoningAdapter legend,
        IProductionPhaseReasoningAdapter phase)
    {
        VerifyFile(detectionModel.BenchmarkEvidencePath, detectionModel.BenchmarkEvidenceSha256,
            "original-DB OCR production gate");
        using JsonDocument document = Parse(ReadBounded(detectionModel.BenchmarkEvidencePath,
            "original-DB OCR production gate"), "original-DB OCR production gate");
        JsonElement resources = RequireObject(document.RootElement, "reviewed_resources",
            "original-DB OCR production gate");
        EmbeddedResource candidate = ReadEmbedded(resources, "real_sealed_candidate_binding", "application/json");
        ProductionOriginalDbWorkflowEvidence.Validate(candidate.Bytes,
            axis, markerCenter, markerClassifier, artifactMask, legend, phase);
    }

    private static void ValidateEvaluatedRuntimeDependencies(byte[] candidateBytes)
    {
        using JsonDocument document = Parse(candidateBytes, "evaluated OCR runtime dependencies");
        JsonElement candidate = document.RootElement;
        ProductionOriginalDbRuntimeFileDescriptor[] ReadFiles(string name) =>
            candidate.GetProperty(name).EnumerateArray().Select(row =>
                new ProductionOriginalDbRuntimeFileDescriptor(
                    RequireText(row, "file", "evaluated runtime dependency"),
                    RequireText(row, "sha256", "evaluated runtime dependency"))).ToArray();
        _ = ProductionOriginalDbRuntimeFiles.Validate(
            Path.GetDirectoryName(typeof(ProductionOcrAdapter).Assembly.Location)!,
            ReadFiles("managed_files"), ReadFiles("native_files"));
    }

    internal static void ValidateFullOcrDevSourceForTest(
        byte[] scoreBytes,
        byte[] candidateBytes,
        string candidateSha256,
        byte[] acceptanceBarsBytes,
        string acceptanceBarsSha256,
        ModelIdentity detectionModel,
        ModelIdentity recognitionModel)
    {
        ValidateFullOcrDevSource(
            scoreBytes,
            candidateBytes,
            ValidateSha(candidateSha256, "candidate SHA-256"),
            ValidateSha(acceptanceBarsSha256, "acceptance-bars SHA-256"),
            detectionModel,
            recognitionModel,
            ReadAcceptanceBars(acceptanceBarsBytes));
    }

    internal static void ValidateSyntheticSealedSourceForTest(byte[] bytes) =>
        ValidateSyntheticSealedSource(bytes);

    internal static string ComputeManifestContractFingerprintForTest(string path, string task) =>
        ComputeManifestContractFingerprint(path, task);

    internal static void ValidateEvaluatedManifestContracts(
        byte[] candidateBytes, byte[] detectorManifestBytes, byte[] recognizerManifestBytes,
        string detectorContract, string recognizerContract, bool wholeWorkflow)
    {
        using JsonDocument document = Parse(candidateBytes, "evaluated OCR candidate");
        JsonElement candidate = document.RootElement;
        foreach ((string task, string field, byte[] bytes, string expectedContract) in new[]
        {
            ("ocr_detection", wholeWorkflow ? "ocr_detection" : "detector", detectorManifestBytes, detectorContract),
            ("ocr_recognition", wholeWorkflow ? "ocr_recognition" : "recognizer", recognizerManifestBytes, recognizerContract),
        })
        {
            JsonElement model = RequireObject(candidate, field, "evaluated OCR candidate");
            string manifestSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (wholeWorkflow)
            {
                RequireSha(RequireObject(model, "manifest", "evaluated OCR model"), "sha256",
                    manifestSha, "evaluated OCR manifest");
            }
            else
            {
                RequireSha(model, "manifest_sha256", manifestSha, "evaluated OCR manifest");
            }
            if (!string.Equals(ComputeManifestContractFingerprint(bytes, task), expectedContract,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Evaluated OCR runtime contract differs from Production.");
            }
        }
    }

    internal static void ValidateEvaluatedRuntime(
        byte[] candidateBytes, string openCvSha256, bool wholeWorkflow = true)
    {
        using JsonDocument document = Parse(candidateBytes, "evaluated OCR runtime");
        JsonElement candidate = document.RootElement;
        string expectedNative = ValidateSha(openCvSha256, "Production OpenCV runtime");
        if (wholeWorkflow)
        {
            JsonElement[] native = candidate.GetProperty("native_files").EnumerateArray()
                .Where(static row => row.GetProperty("role").GetString() == "opencvsharp_extern")
                .ToArray();
            if (native.Length != 1)
                throw new InvalidDataException("Evaluated OCR runtime requires one OpenCV identity.");
            RequireSha(native[0], "sha256", expectedNative, "evaluated OCR runtime");
        }
        else
        {
            RequireSha(candidate, "native_sha256", expectedNative, "evaluated OCR runtime");
        }
        JsonElement[] managed = candidate.GetProperty(
            wholeWorkflow ? "managed_files" : "execution_assemblies").EnumerateArray().ToArray();
        foreach (System.Reflection.Assembly assembly in new[]
        {
            typeof(ProductionOcrAdapter).Assembly,
            typeof(GraphReader.Ocr.OcrPipeline).Assembly,
            typeof(InferenceRuntime).Assembly,
        })
        {
            string name = Path.GetFileName(assembly.Location);
            JsonElement[] matches = managed.Where(row => string.Equals(
                Path.GetFileName(row.GetProperty(wholeWorkflow ? "file" : "path").GetString()), name,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("Evaluated OCR runtime assembly identity is missing or duplicated.");
            RequireSha(matches[0], "sha256",
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                "evaluated OCR runtime assembly");
        }
    }

    private static void ValidateFullOcrDevSource(
        byte[] scoreBytes,
        byte[] candidateBytes,
        string candidateSha256,
        string acceptanceBarsSha256,
        ModelIdentity detectionModel,
        ModelIdentity recognitionModel,
        CanonicalBars bars)
    {
        using JsonDocument scoreDocument = Parse(scoreBytes, "full OCR synthetic-dev score");
        JsonElement score = scoreDocument.RootElement;
        RequireString(score, "schema", FullOcrDevSchema, "full OCR synthetic-dev score");
        JsonElement roleMapping = RequireObject(score, "role_mapping", "full OCR synthetic-dev score");
        RequireString(roleMapping, "revision", "condition-caption-is-phase-heading-v2",
            "full OCR synthetic-dev role mapping");
        JsonElement canonicalRoles = RequireObject(roleMapping,
            "generator_to_serialized_runtime_ocr_role", "full OCR synthetic-dev role mapping");
        RequireString(canonicalRoles, "condition_label", "phaseheading",
            "full OCR synthetic-dev condition-caption semantics");
        RequireString(score, "status", "diagnostic_only_unapproved", "full OCR synthetic-dev score");
        RequireBoolean(score, "synthetic_only", true, "full OCR synthetic-dev score");
        RequireBoolean(score, "private_data", false, "full OCR synthetic-dev score");
        RequireBoolean(score, "sealed_data", false, "full OCR synthetic-dev score");
        RequireBoolean(score, "production_approval", false, "full OCR synthetic-dev score");
        RequireBoolean(score, "release_eligible", false, "full OCR synthetic-dev score");
        RequireInt32(score, "optimizer_steps", 0, "full OCR synthetic-dev score");
        JsonElement integrity = RequireObject(score, "integrity", "full OCR synthetic-dev score");
        RequireInt32(integrity, "full_source_truth_count", 892, "full OCR synthetic-dev inventory");
        RequireInt32(integrity, "panel_count", 37, "full OCR synthetic-dev inventory");
        RequireInt32(integrity, "source_count", 23, "full OCR synthetic-dev inventory");
        RequireBoolean(integrity, "failed_panels_remain_in_full_source_denominator", true,
            "full OCR synthetic-dev inventory");
        RequireBoolean(integrity, "unmatched_predictions_count_as_character_insertions", true,
            "full OCR synthetic-dev inventory");
        RequireBoolean(integrity, "unmatched_truths_count_as_exact_and_role_failures_and_full_text_deletions", true,
            "full OCR synthetic-dev inventory");

        JsonElement truthIsolation = RequireObject(score, "truth_isolation", "full OCR synthetic-dev score");
        RequireBoolean(truthIsolation, "all_runtime_and_candidate_evidence_authenticated_before_truth_regeneration",
            true, "full OCR synthetic-dev score truth isolation");
        RequireBoolean(truthIsolation, "runtime_received_truth", false,
            "full OCR synthetic-dev score truth isolation");
        RequireBoolean(truthIsolation, "truth_regenerated_once_only_in_python_evaluator", true,
            "full OCR synthetic-dev score truth isolation");

        JsonElement inputs = RequireObject(score, "inputs", "full OCR synthetic-dev score");
        RequireSha(inputs, "evaluator_sha256", FullOcrDevEvaluatorSha256,
            "full OCR synthetic-dev inputs");
        JsonElement candidateReference = RequireObject(inputs, "candidate", "full OCR synthetic-dev inputs");
        RequireSha(candidateReference, "sha256", candidateSha256, "full OCR synthetic-dev candidate");
        ValidateCandidate(candidateBytes, detectionModel, recognitionModel);

        JsonElement barReference = RequireObject(score, "acceptance_bar_reference", "full OCR synthetic-dev score");
        RequireSha(barReference, "sha256", acceptanceBarsSha256, "full OCR synthetic-dev acceptance bar");
        JsonElement raw = RequireObject(score, "raw_detector_geometry", "full OCR synthetic-dev score");
        JsonElement rawValidation = RequireObject(raw, "validation", "full OCR synthetic-dev raw geometry");
        JsonElement metrics = RequireObject(score, "metrics", "full OCR synthetic-dev score");
        JsonElement validation = RequireObject(metrics, "validation", "full OCR synthetic-dev metrics");

        int truth = RequirePositiveInt32(rawValidation, "truth_region_count", "full OCR synthetic-dev raw geometry");
        RequireInt32(rawValidation, "truth_region_count", 183, "full OCR synthetic-dev raw geometry");
        RequireMetric(rawValidation, "intersection_over_union_minimum", 0.5,
            "full OCR synthetic-dev raw geometry");
        RequireMetric(validation, "intersection_over_union_minimum", 0.5,
            "full OCR synthetic-dev metrics");
        int truePositive = RequireNonNegativeInt32(rawValidation, "true_positives", "full OCR synthetic-dev raw geometry");
        int falsePositive = RequireNonNegativeInt32(rawValidation, "false_positives", "full OCR synthetic-dev raw geometry");
        int falseNegative = RequireNonNegativeInt32(rawValidation, "false_negatives", "full OCR synthetic-dev raw geometry");
        int predicted = RequireNonNegativeInt32(rawValidation, "predicted_region_count", "full OCR synthetic-dev raw geometry");
        if (truePositive + falseNegative != truth || truePositive + falsePositive != predicted)
        {
            throw new InvalidDataException("Full OCR synthetic-dev geometry counts are inconsistent.");
        }

        double precision = Ratio(truePositive, truePositive + falsePositive);
        double recall = Ratio(truePositive, truth);
        RequireMetric(rawValidation, "precision", precision, "full OCR synthetic-dev raw geometry");
        RequireMetric(rawValidation, "recall", recall, "full OCR synthetic-dev raw geometry");
        if (precision < bars.TextPrecisionMinimum || recall < bars.TextRecallMinimum)
        {
            throw new InvalidDataException("Full OCR synthetic-dev detection gate failed.");
        }

        if (RequirePositiveInt32(validation, "truth_region_count", "full OCR synthetic-dev metrics") != truth ||
            RequireNonNegativeInt32(validation, "geometry_matched_region_count", "full OCR synthetic-dev metrics") != truePositive ||
            RequireNonNegativeInt32(validation, "geometry_false_positive_count", "full OCR synthetic-dev metrics") != falsePositive ||
            RequireNonNegativeInt32(validation, "geometry_false_negative_count", "full OCR synthetic-dev metrics") != falseNegative)
        {
            throw new InvalidDataException("Full OCR synthetic-dev text metrics do not use the raw geometry denominator.");
        }

        int exact = RequireNonNegativeInt32(validation, "recognition_exact_count", "full OCR synthetic-dev metrics");
        int roleCorrect = RequireNonNegativeInt32(validation, "role_correct_count", "full OCR synthetic-dev metrics");
        int characterErrors = RequireNonNegativeInt32(validation, "character_error_count", "full OCR synthetic-dev metrics");
        int truthCharacters = RequirePositiveInt32(validation, "truth_character_count", "full OCR synthetic-dev metrics");
        RequireInt32(validation, "truth_character_count", 1019, "full OCR synthetic-dev metrics");
        if (exact > truePositive || roleCorrect > truePositive)
        {
            throw new InvalidDataException(
                "Full OCR synthetic-dev recognition counts exceed geometry-matched regions.");
        }

        int matchedEdits = RequireNonNegativeInt32(validation, "matched_pair_edit_count",
            "full OCR synthetic-dev metrics");
        int insertionEdits = RequireNonNegativeInt32(validation, "unmatched_prediction_insertion_edit_count",
            "full OCR synthetic-dev metrics");
        int deletionEdits = RequireNonNegativeInt32(validation, "unmatched_truth_deletion_edit_count",
            "full OCR synthetic-dev metrics");
        if (checked(matchedEdits + insertionEdits + deletionEdits) != characterErrors)
        {
            throw new InvalidDataException("Full OCR synthetic-dev character-error counts are inconsistent.");
        }

        double recognition = Ratio(exact, truth);
        double role = Ratio(roleCorrect, truth);
        double characterErrorRate = Ratio(characterErrors, truthCharacters);
        RequireMetric(validation, "recognition_exact_accuracy", recognition, "full OCR synthetic-dev metrics");
        RequireMetric(validation, "role_accuracy", role, "full OCR synthetic-dev metrics");
        RequireMetric(validation, "character_error_rate", characterErrorRate, "full OCR synthetic-dev metrics");
        if (recognition < bars.RecognitionMinimum || role < bars.RoleMinimum ||
            characterErrorRate > bars.CharacterErrorMaximum)
        {
            throw new InvalidDataException("Full OCR synthetic-dev recognition or role gate failed.");
        }
    }

    private static void ValidateCandidate(
        byte[] bytes,
        ModelIdentity detectionModel,
        ModelIdentity recognitionModel)
    {
        using JsonDocument document = Parse(bytes, "full OCR synthetic-dev candidate");
        JsonElement candidate = document.RootElement;
        RequireString(candidate, "schema", "graphreader.frozen-db-head-ocr-candidate.v1",
            "full OCR synthetic-dev candidate");
        RequireString(candidate, "composition_version", ProductionOcrAdapter.OriginalDbCandidateCompositionVersion,
            "full OCR synthetic-dev candidate");
        RequireBoolean(candidate, "production_approved", false, "full OCR synthetic-dev candidate");
        JsonElement detector = RequireObject(candidate, "detector", "full OCR synthetic-dev candidate");
        JsonElement recognizer = RequireObject(candidate, "recognizer", "full OCR synthetic-dev candidate");
        RequireCandidateModel(detector, detectionModel, "full OCR synthetic-dev detector");
        RequireCandidateModel(recognizer, recognitionModel, "full OCR synthetic-dev recognizer");
    }

    private static void ValidateSyntheticSealedSource(byte[] bytes)
    {
        using JsonDocument document = Parse(bytes, "full OCR synthetic-sealed score");
        string schema = RequireText(document.RootElement, "schema", "full OCR synthetic-sealed score");
        throw new InvalidDataException(
            $"Full OCR synthetic-sealed source schema '{schema}' is not supported by this production gate.");
    }

    private static CanonicalBars ReadAcceptanceBars(byte[] bytes)
    {
        using JsonDocument document = Parse(bytes, "OCR acceptance bars");
        JsonElement root = document.RootElement;
        RequireInt32(root, "schema_version", 1, "OCR acceptance bars");
        RequireString(root, "policy_id", "graphreader-goal22-tier1-v1", "OCR acceptance bars");
        JsonElement tier = RequireObject(root, "tier1_reviewable_error", "OCR acceptance bars");
        var bars = new CanonicalBars(
            RequireFinite(tier, "text_region_detection_precision_minimum", "OCR acceptance bars"),
            RequireFinite(tier, "text_region_detection_recall_minimum", "OCR acceptance bars"),
            RequireFinite(tier, "recognition_exact_match_minimum", "OCR acceptance bars"),
            RequireFinite(tier, "character_error_rate_maximum", "OCR acceptance bars"),
            RequireFinite(tier, "role_accuracy_minimum", "OCR acceptance bars"));
        if (bars != new CanonicalBars(0.95, 0.95, 0.95, 0.05, 0.95))
        {
            throw new InvalidDataException("OCR acceptance bars differ from the canonical product thresholds.");
        }
        return bars;
    }

    private static string ComputeManifestContractFingerprint(string path, string expectedTask)
    {
        byte[] bytes = ReadBounded(path, $"{expectedTask} manifest");
        return ComputeManifestContractFingerprint(bytes, expectedTask);
    }

    private static string ComputeManifestContractFingerprint(byte[] bytes, string expectedTask)
    {
        using JsonDocument document = Parse(bytes, $"{expectedTask} manifest");
        JsonElement root = document.RootElement;
        RequireString(root, "task", expectedTask, $"{expectedTask} manifest");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (string name in new[]
                     {
                         "model_id", "model_version", "task", "sha256", "inputs", "outputs",
                         "preprocessing", "postprocessing",
                     })
            {
                if (!root.TryGetProperty(name, out JsonElement value))
                {
                    throw new InvalidDataException($"{expectedTask} manifest is missing '{name}'.");
                }
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static bool ManifestUsesProfile(string manifestPath)
    {
        using JsonDocument document = Parse(ReadBounded(manifestPath, "OCR manifest"), "OCR manifest");
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("benchmarks", out JsonElement benchmarks) ||
            benchmarks.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        JsonElement[] approved = benchmarks.EnumerateArray()
            .Where(static value => value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("production_approval", out JsonElement flag) &&
                flag.ValueKind == JsonValueKind.True)
            .ToArray();
        if (approved.Length > 1)
        {
            throw new InvalidDataException("An OCR manifest contains multiple production approval benchmarks.");
        }
        return approved.Length == 1 &&
            approved[0].TryGetProperty("profile", out JsonElement profile) &&
            profile.ValueKind == JsonValueKind.String &&
            string.Equals(profile.GetString(), Profile, StringComparison.Ordinal);
    }

    private static void RequireApprovalBenchmark(
        ResolvedProductionModel model,
        string detectorContract,
        string recognizerContract)
    {
        using JsonDocument document = Parse(ReadBounded(model.ManifestPath, "OCR manifest"), "OCR manifest");
        JsonElement[] approved = document.RootElement.GetProperty("benchmarks").EnumerateArray()
            .Where(static value => value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("production_approval", out JsonElement flag) &&
                flag.ValueKind == JsonValueKind.True)
            .ToArray();
        if (approved.Length != 1)
        {
            throw new InvalidDataException("An original-DB OCR manifest requires one production approval benchmark.");
        }
        JsonElement benchmark = approved[0];
        RequireString(benchmark, "profile", Profile, "original-DB OCR manifest benchmark");
        RequireString(benchmark, "status", "pass", "original-DB OCR manifest benchmark");
        RequireBoolean(benchmark, "release_eligible", true, "original-DB OCR manifest benchmark");
        RequireSha(benchmark, "evidence_sha256", model.BenchmarkEvidenceSha256,
            "original-DB OCR manifest benchmark");
        RequireString(benchmark, "composition_id", ProductionOcrAdapter.OriginalDbCandidateCompositionVersion,
            "original-DB OCR manifest benchmark");
        RequireSha(benchmark, "detector_contract_sha256", detectorContract,
            "original-DB OCR manifest benchmark");
        RequireSha(benchmark, "recognizer_contract_sha256", recognizerContract,
            "original-DB OCR manifest benchmark");
    }

    private static void RequireModel(JsonElement root, string propertyName, ModelIdentity expected)
    {
        JsonElement value = RequireObject(root, propertyName, "original-DB OCR production gate");
        RequireString(value, "model_id", expected.ModelId, propertyName);
        RequireString(value, "version", expected.Version, propertyName);
        RequireSha(value, "payload_sha256", expected.Sha256, propertyName);
    }

    private static void RequireCandidateModel(JsonElement value, ModelIdentity expected, string label)
    {
        RequireString(value, "model_id", expected.ModelId, label);
        RequireString(value, "model_version", expected.Version, label);
        RequireSha(value, "model_sha256", expected.Sha256, label);
    }

    private static EmbeddedResource ReadEmbedded(JsonElement resources, string name, string mediaType)
    {
        JsonElement resource = RequireObject(resources, name, "original-DB OCR reviewed resources");
        RequireString(resource, "media_type", mediaType, $"OCR resource '{name}'");
        RequireString(resource, "encoding", "base64", $"OCR resource '{name}'");
        string sha = ValidateSha(RequireText(resource, "sha256", $"OCR resource '{name}'"),
            $"OCR resource '{name}' SHA-256");
        string content = RequireText(resource, "content_base64", $"OCR resource '{name}'");
        if (content.Length > ((MaximumEvidenceBytes + 2) / 3 * 4) + 4)
        {
            throw new InvalidDataException($"OCR resource '{name}' exceeds the evidence size limit.");
        }
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(content);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException($"OCR resource '{name}' is not valid base64.", error);
        }
        if (bytes.Length == 0 || bytes.Length > MaximumEvidenceBytes ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"OCR resource '{name}' failed checksum validation.");
        }
        return new EmbeddedResource(sha, bytes);
    }

    private static byte[] ReadBounded(string path, string label)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > MaximumEvidenceBytes)
        {
            throw new InvalidDataException($"The checksum-resolved {label} is missing or exceeds the size limit.");
        }
        return File.ReadAllBytes(file.FullName);
    }

    private static JsonDocument Parse(byte[] bytes, string label)
    {
        try
        {
            // File hashes bind the original bytes, including any UTF-8 BOM.
            // Match the existing manifest readers' support for BOM-prefixed files.
            ReadOnlyMemory<byte> json = bytes.AsMemory();
            if (json.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            {
                json = json[3..];
            }
            JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            RejectDuplicates(document.RootElement, label);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new InvalidDataException($"{label} must be a JSON object.");
            }
            return document;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"{label} is invalid JSON.", error);
        }
    }

    private static void RejectDuplicates(JsonElement value, string label)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException($"{label} contains duplicate property '{property.Name}'.");
                }
                RejectDuplicates(property.Value, label);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                RejectDuplicates(item, label);
            }
        }
    }

    private static JsonElement RequireObject(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} field '{name}' must be an object.");
        }
        return value;
    }

    private static string RequireText(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{label} field '{name}' must be nonempty text.");
        }
        return value.GetString()!;
    }

    private static void RequireString(JsonElement parent, string name, string expected, string label)
    {
        if (!string.Equals(RequireText(parent, name, label), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{label} field '{name}' changed.");
        }
    }

    private static void RequireBoolean(JsonElement parent, string name, bool expected, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || value.GetBoolean() != expected)
        {
            throw new InvalidDataException($"{label} field '{name}' changed.");
        }
    }

    private static int RequireNonNegativeInt32(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result) || result < 0)
        {
            throw new InvalidDataException($"{label} field '{name}' must be a nonnegative int32.");
        }
        return result;
    }

    private static int RequirePositiveInt32(JsonElement parent, string name, string label)
    {
        int value = RequireNonNegativeInt32(parent, name, label);
        if (value == 0)
        {
            throw new InvalidDataException($"{label} field '{name}' must be positive.");
        }
        return value;
    }

    private static void RequireInt32(JsonElement parent, string name, int expected, string label)
    {
        if (RequireNonNegativeInt32(parent, name, label) != expected)
        {
            throw new InvalidDataException($"{label} field '{name}' changed.");
        }
    }

    private static double RequireFinite(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) || !double.IsFinite(result))
        {
            throw new InvalidDataException($"{label} field '{name}' must be finite.");
        }
        return result;
    }

    private static void RequireMetric(JsonElement parent, string name, double expected, string label)
    {
        if (Math.Abs(RequireFinite(parent, name, label) - expected) > NumericTolerance)
        {
            throw new InvalidDataException($"{label} field '{name}' differs from its counts.");
        }
    }

    private static void RequireSha(JsonElement parent, string name, string expected, string label)
    {
        string actual = ValidateSha(RequireText(parent, name, label), $"{label} field '{name}'");
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label} field '{name}' differs from its bound identity.");
        }
    }

    private static string ValidateSha(string value, string label)
    {
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{label} must be SHA-256.");
        }
        return value.ToLowerInvariant();
    }

    private static void VerifyFile(string path, string expectedSha, string label)
    {
        byte[] bytes = ReadBounded(path, label);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expectedSha,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The checksum-resolved {label} changed.");
        }
    }

    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? 0 : (double)numerator / denominator;

    private sealed record EmbeddedResource(string Sha256, byte[] Bytes);
    private sealed record CanonicalBars(
        double TextPrecisionMinimum,
        double TextRecallMinimum,
        double RecognitionMinimum,
        double CharacterErrorMaximum,
        double RoleMinimum);
}
