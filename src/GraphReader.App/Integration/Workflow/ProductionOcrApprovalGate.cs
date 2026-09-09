// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using System.IO.Compression;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.Inference;

namespace GraphReader.App.Integration.Workflow;

internal sealed record ProductionOcrGateEvidence(
    string EvaluatorSourceSha256,
    string SealedSplitSha256,
    string PredictionsSha256,
    string RuntimeResultsSha256,
    double ValidationExactMatch,
    double ValidationCer,
    double ValidationRoleAccuracy,
    double SealedTestExactMatch,
    double SealedTestCer,
    double SealedTestRoleAccuracy,
    double OnnxMaxAbsError,
    double DetectionExactRate,
    int DuplicateRegionCount,
    int ExclusionFalseRegionCount,
    int MarkerCreationCount);

internal static class ProductionOcrApprovalGate
{
    internal const string FrozenEvaluatorSourceSha256 =
        "cc354ec53e4d0ecc5eab7dcf6243e5538e39f043057a949fd3d6ce84a83d50ee";
    internal const string FrozenWorkflowSourceSha256 =
        "65de1c76288c2cd9646386afb941bf641a1a87c5abfec5d76b6cb0f7a818c992";
    private const string CompositionId = "graph-structure-consensus-v1";
    private const int MaximumResourceBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> AllowedRoles = new(StringComparer.Ordinal)
    {
        "x_tick", "y_tick", "phase_header", "annotation", "participant", "other",
    };
    private static readonly HashSet<string> RequiredFamilies = new(StringComparer.Ordinal)
    {
        "integer", "decimal", "negative", "percentage", "ambiguity",
    };
    private static readonly string[] RequiredDirectEvidenceFields =
    [
        "exact_fixture_bytes",
        "exact_masked_detector_bytes",
        "detector_input_and_output_tensors",
        "recognizer_input_and_output_tensors",
        "model_and_workflow_sha256",
        "provider_identity",
        "independent_marker_stage_run",
        "aggregate_metrics_rederived_by_csharp_gate",
    ];

    internal static ProductionOcrGateEvidence Validate(
        ResolvedProductionModel detectionModel,
        ResolvedProductionModel recognitionModel)
    {
        ArgumentNullException.ThrowIfNull(detectionModel);
        ArgumentNullException.ThrowIfNull(recognitionModel);
        VerifyChecksum(
            detectionModel.BenchmarkEvidencePath,
            detectionModel.BenchmarkEvidenceSha256,
            "OCR detection benchmark evidence");
        VerifyChecksum(
            recognitionModel.BenchmarkEvidencePath,
            recognitionModel.BenchmarkEvidenceSha256,
            "OCR recognition benchmark evidence");
        if (!string.Equals(
                detectionModel.BenchmarkEvidenceSha256,
                recognitionModel.BenchmarkEvidenceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "OCR detection and recognition must bind the same checksum-exact public gate report.");
        }

        string alphabet = ReadRecognitionAlphabet(recognitionModel.ManifestPath);
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(detectionModel.BenchmarkEvidencePath),
            new JsonDocumentOptions { MaxDepth = 64 });
        JsonElement root = document.RootElement;
        RejectDuplicatePropertyNames(root, "OCR public gate evidence");
        RequireString(
            root,
            "schema",
            "graphreader.ocr-structure-consensus-production-gate.v1",
            "OCR public gate evidence");
        RequireString(root, "profile", ProductionOcrAdapter.ApprovalBenchmarkProfile, "OCR public gate evidence");
        RequireString(root, "composition_id", CompositionId, "OCR public gate evidence");
        RequireString(root, "status", "pass", "OCR public gate evidence");
        RequireString(root, "scope", "public_synthetic_sealed", "OCR public gate evidence");
        RequireBoolean(root, "release_eligible", true, "OCR public gate evidence");
        RequireBoolean(root, "production_approval", true, "OCR public gate evidence");
        RequireBoolean(root, "private_data", false, "OCR public gate evidence");
        RequireBoolean(root, "chandler_used", false, "OCR public gate evidence");
        RequireBoolean(root, "marker_creation_evaluated", true, "OCR public gate evidence");
        RequireString(root, "provider", "cpu", "OCR public gate evidence");
        RequireString(root, "coordinate_space", "original_pixels", "OCR public gate evidence");
        RequireMatchingSha256(
            root,
            "detection_model_sha256",
            detectionModel.Identity.Sha256,
            "OCR public gate evidence");
        RequireMatchingSha256(
            root,
            "recognition_model_sha256",
            recognitionModel.Identity.Sha256,
            "OCR public gate evidence");

        JsonElement resources = RequiredObject(root, "reviewed_resources", "OCR public gate evidence");
        EmbeddedResource protocol = ReadEmbeddedResource(
            resources,
            "gate_protocol",
            "application/json");
        EmbeddedResource evaluator = ReadEmbeddedResource(
            resources,
            "evaluator_source",
            "text/x-python");
        EmbeddedResource workflow = ReadEmbeddedResource(
            resources,
            "workflow_source",
            "text/x-python");
        EmbeddedResource split = ReadEmbeddedResource(resources, "sealed_split", "application/json");
        EmbeddedResource fixtureArchive = ReadEmbeddedResource(
            resources,
            "fixture_archive",
            "application/zip");
        EmbeddedResource corePredictions = ReadEmbeddedResource(
            resources,
            "core_predictions",
            "application/json");
        EmbeddedResource predictions = ReadEmbeddedResource(resources, "predictions", "application/json");
        EmbeddedResource runtime = ReadEmbeddedResource(resources, "runtime_results", "application/json");
        EmbeddedResource markerCreation = ReadEmbeddedResource(
            resources,
            "marker_creation_results",
            "application/json");
        if (!string.Equals(
                evaluator.Sha256,
                FrozenEvaluatorSourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "OCR public gate evidence does not contain the frozen reviewed evaluator source.");
        }
        if (!string.Equals(
                workflow.Sha256,
                FrozenWorkflowSourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "OCR public gate evidence does not contain the frozen reviewed execution workflow.");
        }
        RequireMatchingSha256(root, "workflow_source_sha256", workflow.Sha256, "OCR public gate evidence");
        RequireMatchingSha256(root, "protocol_sha256", protocol.Sha256, "OCR public gate evidence");
        RequireMatchingSha256(root, "fixture_archive_sha256", fixtureArchive.Sha256, "OCR public gate evidence");
        RequireMatchingSha256(root, "core_predictions_sha256", corePredictions.Sha256, "OCR public gate evidence");

        ValidateGateProtocol(
            protocol.Bytes,
            evaluator.Sha256,
            workflow.Sha256,
            split.Sha256,
            fixtureArchive.Sha256);
        Dictionary<string, SplitCase> cases = ReadSplit(
            split.Bytes,
            protocol.Sha256,
            evaluator.Sha256,
            fixtureArchive,
            alphabet);
        Dictionary<string, Prediction> core = ReadPredictions(
            corePredictions.Bytes,
            "graphreader.ocr-structure-consensus-core-predictions.v1",
            split.Sha256,
            fixtureArchive.Sha256,
            expectedCorePredictionsSha256: null,
            detectionModel.Identity.Sha256,
            recognitionModel.Identity.Sha256,
            alphabet,
            requireMarkerCount: false);
        Dictionary<string, Prediction> predicted = ReadPredictions(
            predictions.Bytes,
            "graphreader.ocr-structure-consensus-predictions.v1",
            split.Sha256,
            fixtureArchive.Sha256,
            corePredictions.Sha256,
            detectionModel.Identity.Sha256,
            recognitionModel.Identity.Sha256,
            alphabet,
            requireMarkerCount: true);
        if (!cases.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(predicted.Keys) ||
            !cases.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(core.Keys))
        {
            throw new InvalidDataException(
                "OCR prediction record IDs do not match the frozen sealed split.");
        }
        ValidatePredictionBindings(cases, core, predicted);
        Dictionary<string, int> markerCounts = ReadMarkerCreationResults(
            markerCreation.Bytes,
            split.Sha256,
            corePredictions.Sha256,
            detectionModel.Identity.Sha256,
            recognitionModel.Identity.Sha256,
            cases);
        foreach ((string caseId, int markerCount) in markerCounts)
        {
            if (predicted[caseId].MarkerCreationCount != markerCount)
            {
                throw new InvalidDataException(
                    "OCR predictions do not match the independently composed marker-stage evidence.");
            }
        }

        PartitionMetrics validation = EvaluatePartition(cases, predicted, "validation");
        PartitionMetrics sealedTest = EvaluatePartition(cases, predicted, "sealed_test");
        int detectionExact = cases.Count(pair =>
            predicted[pair.Key].DetectedRegionCount == pair.Value.ExpectedRegionCount &&
            predicted[pair.Key].FalseRegionCount == 0);
        double detectionExactRate = detectionExact / (double)cases.Count;
        int markerCreationCount = markerCounts.Values.Sum();
        double onnxMaxAbsError = ReadRuntimeResults(
            runtime.Bytes,
            evaluator.Sha256,
            workflow.Sha256,
            split.Sha256,
            fixtureArchive.Sha256,
            corePredictions.Sha256,
            predictions.Sha256,
            detectionModel.Identity.Sha256,
            recognitionModel.Identity.Sha256);
        int duplicateRegionCount = predicted.Values.Sum(static value => value.DuplicateRegionCount);
        int exclusionFalseRegionCount = cases.Sum(pair =>
            pair.Value.Kind == "exclusion" ? predicted[pair.Key].FalseRegionCount : 0);
        var evidence = new ProductionOcrGateEvidence(
            evaluator.Sha256,
            split.Sha256,
            predictions.Sha256,
            runtime.Sha256,
            validation.ExactMatch,
            validation.Cer,
            validation.RoleAccuracy,
            sealedTest.ExactMatch,
            sealedTest.Cer,
            sealedTest.RoleAccuracy,
            onnxMaxAbsError,
            detectionExactRate,
            duplicateRegionCount,
            exclusionFalseRegionCount,
            markerCreationCount);
        ValidateThresholds(evidence);
        MatchReportedMetrics(root, evidence);
        RequireApprovalBenchmark(detectionModel, evidence);
        RequireApprovalBenchmark(recognitionModel, evidence);
        return evidence;
    }

    private static void ValidateGateProtocol(
        byte[] bytes,
        string evaluatorSha256,
        string workflowSha256,
        string splitSha256,
        string fixtureArchiveSha256)
    {
        using JsonDocument document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        RejectDuplicatePropertyNames(root, "OCR gate protocol");
        RequireString(
            root,
            "schema",
            "graphreader.ocr-structure-consensus-gate-protocol.v1",
            "OCR gate protocol");
        RequireString(root, "profile", ProductionOcrAdapter.ApprovalBenchmarkProfile, "OCR gate protocol");
        RequireString(
            root,
            "status",
            "frozen_before_fixture_generation_and_inference",
            "OCR gate protocol");
        RequireString(root, "scope", "public_synthetic", "OCR gate protocol");
        RequireBoolean(root, "private_data", false, "OCR gate protocol");
        RequireBoolean(root, "chandler_used", false, "OCR gate protocol");
        RequireBoolean(root, "selection_locked_before_inference", true, "OCR gate protocol");
        RequireMatchingSha256(
            root,
            "metrics_evaluator_sha256",
            evaluatorSha256,
            "OCR gate protocol");
        RequireMatchingSha256(
            root,
            "execution_workflow_sha256",
            workflowSha256,
            "OCR gate protocol");

        JsonElement prior = RequiredObject(
            root,
            "prior_exposed_split_forbidden",
            "OCR gate protocol");
        string exposedSplit = RequireSha256(prior, "split_sha256", "OCR gate protocol");
        string exposedArchive = RequireSha256(
            prior,
            "fixture_archive_sha256",
            "OCR gate protocol");
        if (string.Equals(exposedSplit, splitSha256, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exposedArchive, fixtureArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("OCR gate reused the previously exposed fixture split.");
        }

        JsonElement candidate = RequiredObject(root, "candidate", "OCR gate protocol");
        RequireString(candidate, "composition_id", CompositionId, "OCR gate protocol candidate");
        JsonElement consensus = RequiredObject(
            candidate,
            "structure_consensus",
            "OCR gate protocol candidate");
        RequireExactDouble(consensus, "minimum_overlap_coefficient", 0.50, "OCR gate protocol");
        RequireExactDouble(consensus, "minimum_text_likelihood", 0.45, "OCR gate protocol");
        RequireExactDouble(consensus, "truth_match_iou_minimum", 0.50, "OCR gate protocol");
        RequireExactDouble(consensus, "duplicate_region_iou_minimum", 0.70, "OCR gate protocol");
        RequireBoolean(
            consensus,
            "one_model_region_per_structure_candidate",
            true,
            "OCR gate protocol");
        RequireBoolean(
            consensus,
            "candidate_may_replace_model_region",
            false,
            "OCR gate protocol");
        RequireBoolean(
            consensus,
            "explicit_non_structure_evidence_required",
            true,
            "OCR gate protocol");

        JsonElement thresholds = RequiredObject(root, "thresholds", "OCR gate protocol");
        RequireExactDouble(thresholds, "validation_exact_match_minimum", 0.90, "OCR gate protocol");
        RequireExactDouble(thresholds, "validation_cer_maximum", 0.05, "OCR gate protocol");
        RequireExactDouble(thresholds, "validation_role_accuracy_minimum", 0.90, "OCR gate protocol");
        RequireExactDouble(thresholds, "sealed_test_exact_match_minimum", 0.90, "OCR gate protocol");
        RequireExactDouble(thresholds, "sealed_test_cer_maximum", 0.05, "OCR gate protocol");
        RequireExactDouble(thresholds, "sealed_test_role_accuracy_minimum", 0.90, "OCR gate protocol");
        RequireExactDouble(thresholds, "detection_exact_rate", 1.0, "OCR gate protocol");
        RequireExactDouble(
            thresholds,
            "onnx_parity_maximum_absolute_error",
            0.0001,
            "OCR gate protocol");
        if (RequiredNonNegativeInt32(thresholds, "duplicate_region_count", "OCR gate protocol") != 0 ||
            RequiredNonNegativeInt32(thresholds, "exclusion_false_region_count", "OCR gate protocol") != 0 ||
            RequiredNonNegativeInt32(thresholds, "marker_creation_count", "OCR gate protocol") != 0)
        {
            throw new InvalidDataException("OCR gate protocol zero-count thresholds changed.");
        }

        JsonElement direct = RequiredObject(root, "direct_evidence", "OCR gate protocol");
        foreach (string propertyName in RequiredDirectEvidenceFields)
        {
            RequireBoolean(direct, propertyName, true, "OCR gate protocol direct evidence");
        }

        JsonElement budget = RequiredObject(root, "experiment_budget", "OCR gate protocol");
        if (RequiredPositiveInt32(budget, "fixture_freezes", "OCR gate protocol") != 1 ||
            RequiredPositiveInt32(budget, "official_composition_evaluations", "OCR gate protocol") != 1 ||
            RequiredNonNegativeInt32(budget, "split_regeneration_after_inference", "OCR gate protocol") != 0 ||
            RequiredNonNegativeInt32(budget, "threshold_changes_after_inference", "OCR gate protocol") != 0 ||
            RequiredNonNegativeInt32(budget, "workflow_changes_after_inference", "OCR gate protocol") != 0)
        {
            throw new InvalidDataException("OCR gate protocol experiment budget changed.");
        }

        JsonElement reviewedSources = RequiredObject(
            root,
            "reviewed_source_sha256",
            "OCR gate protocol");
        RequireSha256(
            reviewedSources,
            "src/GraphReader.App/Integration/Workflow/ProductionOcrApprovalGate.cs",
            "OCR gate protocol reviewed sources");
        RequireMatchingSha256(
            reviewedSources,
            "ml/ocr/official_bakeoff/structure_consensus_evaluate.py",
            workflowSha256,
            "OCR gate protocol reviewed sources");
        RequireMatchingSha256(
            reviewedSources,
            "ml/ocr/production_gate.py",
            evaluatorSha256,
            "OCR gate protocol reviewed sources");
    }

    private static Dictionary<string, SplitCase> ReadSplit(
        byte[] bytes,
        string protocolSha256,
        string evaluatorSha256,
        EmbeddedResource fixtureArchive,
        string alphabet)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        RejectDuplicatePropertyNames(root, "OCR sealed split");
        RequireString(
            root,
            "schema",
            "graphreader.ocr-structure-consensus-split.v1",
            "OCR sealed split");
        RequireString(root, "profile", ProductionOcrAdapter.ApprovalBenchmarkProfile, "OCR sealed split");
        RequireString(root, "scope", "public_synthetic", "OCR sealed split");
        RequireBoolean(root, "sealed", true, "OCR sealed split");
        RequireBoolean(root, "selection_locked_before_inference", true, "OCR sealed split");
        RequireBoolean(root, "private_data", false, "OCR sealed split");
        RequireBoolean(root, "chandler_used", false, "OCR sealed split");
        RequireMatchingSha256(root, "protocol_sha256", protocolSha256, "OCR sealed split");
        RequireMatchingSha256(root, "evaluator_source_sha256", evaluatorSha256, "OCR sealed split");
        RequireMatchingSha256(
            root,
            "workflow_source_sha256",
            FrozenWorkflowSourceSha256,
            "OCR sealed split");
        RequireMatchingSha256(
            root,
            "fixture_archive_sha256",
            fixtureArchive.Sha256,
            "OCR sealed split");

        var result = new Dictionary<string, SplitCase>(StringComparer.Ordinal);
        var families = new HashSet<string>(StringComparer.Ordinal);
        int validationText = 0;
        int sealedText = 0;
        int validationExclusions = 0;
        int sealedExclusions = 0;
        foreach (JsonElement item in RequiredArray(root, "cases", "OCR sealed split").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Every OCR sealed case must be an object.");
            }

            string caseId = RequiredString(item, "case_id", "OCR sealed case");
            string partition = RequiredString(item, "partition", "OCR sealed case");
            string kind = RequiredString(item, "kind", "OCR sealed case");
            string family = RequiredString(item, "family", "OCR sealed case");
            string display = RequiredStringAllowEmpty(item, "display_text", "OCR sealed case");
            string truth = RequiredStringAllowEmpty(item, "truth_text", "OCR sealed case");
            string role = RequiredString(item, "truth_role", "OCR sealed case");
            string sourcePath = RequiredSafeArchivePath(item, "source_path", "OCR sealed case");
            string sourceSha256 = RequireSha256(item, "source_sha256", "OCR sealed case");
            int sourceWidth = RequiredPositiveInt32(item, "source_width", "OCR sealed case");
            int sourceHeight = RequiredPositiveInt32(item, "source_height", "OCR sealed case");
            string sourceBgrSha256 = RequireSha256(item, "source_bgr_sha256", "OCR sealed case");
            string detectorImageBgrSha256 = RequireSha256(
                item,
                "detector_image_bgr_sha256",
                "OCR sealed case");
            IReadOnlyList<MaskRectangle> maskRectangles = ReadMaskRectangles(
                item,
                sourceWidth,
                sourceHeight);
            EvidenceBounds? truthBounds = ReadOptionalBounds(
                item,
                "truth_bbox",
                sourceWidth,
                sourceHeight,
                "OCR sealed case");
            int expectedRegions = RequiredNonNegativeInt32(
                item,
                "expected_region_count",
                "OCR sealed case");
            if (partition is not ("validation" or "sealed_test") ||
                kind is not ("text" or "exclusion") ||
                !AllowedRoles.Contains(role) ||
                display.Length > 64 ||
                truth.Length > 64 ||
                display.Any(character => !alphabet.Contains(character)) ||
                truth.Any(character => !alphabet.Contains(character)) ||
                !result.TryAdd(caseId, new SplitCase(
                    partition,
                    kind,
                    family,
                    display,
                    truth,
                    role,
                    expectedRegions,
                    sourcePath,
                    sourceSha256,
                    sourceWidth,
                    sourceHeight,
                    sourceBgrSha256,
                    detectorImageBgrSha256,
                    maskRectangles,
                    truthBounds)))
            {
                throw new InvalidDataException("OCR sealed cases contain invalid or duplicate values.");
            }

            if (kind == "text")
            {
                if (display.Length == 0 || truth.Length == 0 || expectedRegions != 1 ||
                    truthBounds is null ||
                    !TruthMatchesFamily(display, truth, family))
                {
                    throw new InvalidDataException(
                        "OCR text cases require nonempty truth and exactly one expected region.");
                }

                families.Add(family);
                if (partition == "validation")
                {
                    validationText++;
                }
                else
                {
                    sealedText++;
                }
            }
            else
            {
                if (display.Length != 0 || truth.Length != 0 || truthBounds is not null ||
                    role != "other" || expectedRegions != 0)
                {
                    throw new InvalidDataException(
                        "OCR exclusion cases require empty truth, role other, and zero expected regions.");
                }

                if (partition == "validation")
                {
                    validationExclusions++;
                }
                else
                {
                    sealedExclusions++;
                }
            }
        }

        if (validationText != 200 || sealedText != 200 ||
            validationExclusions != 50 || sealedExclusions != 50 ||
            !RequiredFamilies.IsSubsetOf(families))
        {
            throw new InvalidDataException(
                "OCR sealed split requires 200 text and 50 exclusion cases per partition plus every numeric ambiguity family.");
        }

        ValidateFixtureArchive(fixtureArchive.Bytes, result);
        return result;
    }

    private static void ValidateFixtureArchive(
        byte[] archiveBytes,
        IReadOnlyDictionary<string, SplitCase> cases)
    {
        Dictionary<string, SplitCase> expected = cases.Values.ToDictionary(
            static item => item.SourcePath,
            StringComparer.Ordinal);
        if (expected.Count != cases.Count)
        {
            throw new InvalidDataException("OCR sealed cases must use unique fixture paths.");
        }

        using var stream = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string path = ValidateSafeArchivePath(entry.FullName, "OCR fixture archive entry");
            if (!seen.Add(path) || !expected.TryGetValue(path, out SplitCase? fixture) ||
                entry.Length <= 0 || entry.Length > MaximumResourceBytes)
            {
                throw new InvalidDataException(
                    "OCR fixture archive contains an unexpected, duplicate, empty, or oversized entry.");
            }

            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumResourceBytes)
            {
                throw new InvalidDataException("OCR fixture archive expands beyond the reviewed size limit.");
            }

            using Stream input = entry.Open();
            using var output = new MemoryStream(checked((int)entry.Length));
            input.CopyTo(output);
            byte[] bytes = output.ToArray();
            if (bytes.LongLength != entry.Length || !string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    fixture.SourceSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "OCR fixture archive bytes do not match the frozen split.");
            }

            ValidateFixturePixels(bytes, fixture);
        }

        if (!expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(seen))
        {
            throw new InvalidDataException(
                "OCR fixture archive does not exactly cover the frozen split.");
        }
    }

    private static void ValidateFixturePixels(byte[] pngBytes, SplitCase fixture)
    {
        using var stream = new MemoryStream(pngBytes, writable: false);
        BitmapDecoder decoder;
        try
        {
            decoder = new PngBitmapDecoder(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or IOException)
        {
            throw new InvalidDataException("OCR fixture archive contains an invalid PNG.", exception);
        }

        if (decoder.Frames.Count != 1)
        {
            throw new InvalidDataException("OCR fixture PNG must contain exactly one frame.");
        }

        BitmapSource frame = decoder.Frames[0];
        if (frame.PixelWidth != fixture.SourceWidth || frame.PixelHeight != fixture.SourceHeight)
        {
            throw new InvalidDataException("OCR fixture dimensions do not match the frozen split.");
        }

        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        int stride = checked(fixture.SourceWidth * 3);
        var sourceBgr = new byte[checked(stride * fixture.SourceHeight)];
        converted.CopyPixels(sourceBgr, stride, 0);
        if (!HashEquals(sourceBgr, fixture.SourceBgrSha256))
        {
            throw new InvalidDataException("OCR fixture decoded BGR pixels changed.");
        }

        byte[] detectorBgr = sourceBgr.ToArray();
        foreach (MaskRectangle rectangle in fixture.MaskRectangles)
        {
            for (int y = rectangle.Top; y < rectangle.Bottom; y++)
            {
                int row = checked(y * stride);
                for (int x = rectangle.Left; x < rectangle.Right; x++)
                {
                    int offset = checked(row + (x * 3));
                    detectorBgr[offset] = byte.MaxValue;
                    detectorBgr[offset + 1] = byte.MaxValue;
                    detectorBgr[offset + 2] = byte.MaxValue;
                }
            }
        }

        if (!HashEquals(detectorBgr, fixture.DetectorImageBgrSha256))
        {
            throw new InvalidDataException(
                "OCR checksum-bound mask geometry does not reproduce the exact detector input pixels.");
        }
    }

    private static bool HashEquals(byte[] bytes, string expected) =>
        string.Equals(
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            expected,
            StringComparison.OrdinalIgnoreCase);

    private static ReadOnlyCollection<MaskRectangle> ReadMaskRectangles(
        JsonElement item,
        int width,
        int height)
    {
        JsonElement values = RequiredArray(item, "mask_rectangles", "OCR sealed case");
        var result = new List<MaskRectangle>();
        foreach (JsonElement value in values.EnumerateArray())
        {
            string kind = RequiredString(value, "kind", "OCR mask rectangle");
            int left = RequiredNonNegativeInt32(value, "left", "OCR mask rectangle");
            int top = RequiredNonNegativeInt32(value, "top", "OCR mask rectangle");
            int right = RequiredPositiveInt32(value, "right", "OCR mask rectangle");
            int bottom = RequiredPositiveInt32(value, "bottom", "OCR mask rectangle");
            if (kind is not ("x_axis" or "y_axis" or "x_tick" or "y_tick" or "phase_divider") ||
                left >= right || top >= bottom || right > width || bottom > height)
            {
                throw new InvalidDataException("OCR mask rectangle is outside the frozen source.");
            }

            result.Add(new MaskRectangle(kind, left, top, right, bottom));
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException("OCR sealed case requires checksum-bound mask geometry.");
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private static EvidenceBounds? ReadOptionalBounds(
        JsonElement parent,
        string propertyName,
        int width,
        int height,
        string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new InvalidDataException($"{label} field '{propertyName}' is missing.");
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4)
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be four bounds values or null.");
        }

        double[] values = value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out double number)
                ? number
                : double.NaN)
            .ToArray();
        var bounds = new EvidenceBounds(values[0], values[1], values[2], values[3]);
        if (!bounds.IsValid || bounds.Left < 0 || bounds.Top < 0 ||
            bounds.Right > width || bounds.Bottom > height)
        {
            throw new InvalidDataException($"{label} field '{propertyName}' is outside the source.");
        }

        return bounds;
    }

    private static string RequiredSafeArchivePath(
        JsonElement parent,
        string propertyName,
        string label) =>
        ValidateSafeArchivePath(RequiredString(parent, propertyName, label), label);

    private static string ValidateSafeArchivePath(string value, string label)
    {
        string[] segments = value.Split('/');
        if (value.Length > 240 || value.StartsWith('/') ||
            value.Contains('\\') || value.Contains(':') ||
            segments.Length != 2 || segments[0] != "assets" ||
            segments.Any(static segment => string.IsNullOrWhiteSpace(segment) || segment is "." or "..") ||
            !segments[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label} contains an unsafe OCR fixture path.");
        }

        return value;
    }

    private static void ValidatePredictionBindings(
        IReadOnlyDictionary<string, SplitCase> cases,
        IReadOnlyDictionary<string, Prediction> core,
        IReadOnlyDictionary<string, Prediction> final)
    {
        foreach ((string caseId, SplitCase fixture) in cases)
        {
            Prediction coreValue = core[caseId];
            Prediction finalValue = final[caseId];
            bool changed = !string.Equals(
                    fixture.SourceSha256,
                    coreValue.SourceSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    fixture.SourceSha256,
                    finalValue.SourceSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    fixture.SourceBgrSha256,
                    coreValue.SourceBgrSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    fixture.SourceBgrSha256,
                    finalValue.SourceBgrSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    fixture.DetectorImageBgrSha256,
                    coreValue.DetectorImageBgrSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    fixture.DetectorImageBgrSha256,
                    finalValue.DetectorImageBgrSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(coreValue.Text, finalValue.Text, StringComparison.Ordinal) ||
                !string.Equals(coreValue.Role, finalValue.Role, StringComparison.Ordinal) ||
                coreValue.DetectedRegionCount != finalValue.DetectedRegionCount ||
                coreValue.FalseRegionCount != finalValue.FalseRegionCount ||
                coreValue.DuplicateRegionCount != finalValue.DuplicateRegionCount ||
                coreValue.RecognizerExecuted != finalValue.RecognizerExecuted ||
                !coreValue.ModelRegions.SequenceEqual(finalValue.ModelRegions) ||
                !coreValue.StructureCandidates.SequenceEqual(finalValue.StructureCandidates) ||
                !coreValue.ConsensusMatches.SequenceEqual(finalValue.ConsensusMatches) ||
                !coreValue.FinalRegions.SequenceEqual(finalValue.FinalRegions) ||
                !TensorEquals(coreValue.DetectorInput, finalValue.DetectorInput) ||
                !TensorEquals(coreValue.DetectorOutput, finalValue.DetectorOutput) ||
                !TensorEquals(coreValue.RecognizerInput, finalValue.RecognizerInput) ||
                !TensorEquals(coreValue.RecognizerOutput, finalValue.RecognizerOutput) ||
                coreValue.RecognizerExecuted != (coreValue.RecognizerInput is not null &&
                    coreValue.RecognizerOutput is not null) ||
                coreValue.RecognizerExecuted != (coreValue.DetectedRegionCount == 1);
            if (changed)
            {
                throw new InvalidDataException(
                    "OCR core and final predictions are not bound to identical fixture and tensor evidence.");
            }

            ValidateStructureConsensus(fixture, coreValue);
        }
    }

    private static void ValidateStructureConsensus(SplitCase fixture, Prediction prediction)
    {
        EvidenceCandidate[] eligible = prediction.StructureCandidates
            .Where(static candidate =>
                !candidate.LikelyGraphStructure && candidate.TextLikelihood >= 0.45)
            .ToArray();
        ConsensusCandidate[] candidates = prediction.ModelRegions
            .SelectMany(model => eligible.Select(candidate => new ConsensusCandidate(
                model,
                candidate,
                OverlapCoefficient(model.Bounds, candidate.Bounds))))
            .Where(static candidate => candidate.OverlapCoefficient >= 0.50)
            .OrderByDescending(static candidate => candidate.Model.Confidence)
            .ThenByDescending(static candidate => candidate.OverlapCoefficient)
            .ThenByDescending(static candidate => candidate.Candidate.TextLikelihood)
            .ThenBy(static candidate => candidate.Model.RegionId, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Candidate.RegionId, StringComparer.Ordinal)
            .ToArray();
        var usedModels = new HashSet<string>(StringComparer.Ordinal);
        var usedCandidates = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<ConsensusMatch>();
        var finalRegions = new List<EvidenceRegion>();
        foreach (ConsensusCandidate candidate in candidates)
        {
            if (usedModels.Contains(candidate.Model.RegionId) ||
                usedCandidates.Contains(candidate.Candidate.RegionId))
            {
                continue;
            }

            usedModels.Add(candidate.Model.RegionId);
            usedCandidates.Add(candidate.Candidate.RegionId);
            matches.Add(new ConsensusMatch(
                candidate.Model.RegionId,
                candidate.Candidate.RegionId,
                candidate.OverlapCoefficient));
            finalRegions.Add(candidate.Model);
        }

        EvidenceRegion[] orderedFinal = finalRegions
            .OrderBy(static region => region.Bounds.Top)
            .ThenBy(static region => region.Bounds.Left)
            .ThenBy(static region => region.RegionId, StringComparer.Ordinal)
            .ToArray();
        if (!matches.SequenceEqual(prediction.ConsensusMatches) ||
            !orderedFinal.SequenceEqual(prediction.FinalRegions))
        {
            throw new InvalidDataException(
                "OCR prediction structure-consensus records do not reproduce the frozen one-to-one composition.");
        }

        int detected = 0;
        if (fixture.TruthBounds is not null &&
            orderedFinal.Any(region => IntersectionOverUnion(region.Bounds, fixture.TruthBounds) >= 0.50))
        {
            detected = 1;
        }

        int falseRegions = orderedFinal.Length - detected;
        int duplicates = 0;
        for (int first = 0; first < orderedFinal.Length; first++)
        {
            for (int second = first + 1; second < orderedFinal.Length; second++)
            {
                if (IntersectionOverUnion(
                        orderedFinal[first].Bounds,
                        orderedFinal[second].Bounds) >= 0.70)
                {
                    duplicates++;
                }
            }
        }

        if (prediction.DetectedRegionCount != detected ||
            prediction.FalseRegionCount != falseRegions ||
            prediction.DuplicateRegionCount != duplicates ||
            prediction.RecognizerExecuted != (detected == 1))
        {
            throw new InvalidDataException(
                "OCR prediction counts do not match the checksum-bound structure-consensus geometry.");
        }
    }

    private static double OverlapCoefficient(EvidenceBounds left, EvidenceBounds right)
    {
        double width = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));
        double height = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top));
        double denominator = Math.Min(left.Area, right.Area);
        return denominator <= 0 ? 0 : (width * height) / denominator;
    }

    private static double IntersectionOverUnion(EvidenceBounds left, EvidenceBounds right)
    {
        double width = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));
        double height = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top));
        double intersection = width * height;
        double union = left.Area + right.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static bool TensorEquals(TensorEvidence? left, TensorEvidence? right) =>
        left is null
            ? right is null
            : right is not null &&
              string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase) &&
              string.Equals(left.Dtype, right.Dtype, StringComparison.Ordinal) &&
              left.Shape.SequenceEqual(right.Shape);

    private static TensorEvidence ReadTensorEvidence(
        JsonElement parent,
        string propertyName,
        string label)
    {
        JsonElement value = RequiredObject(parent, propertyName, label);
        string sha256 = RequireSha256(value, "sha256", $"{label} {propertyName}");
        RequireString(value, "dtype", "float32", $"{label} {propertyName}");
        int[] shape = RequiredArray(value, "shape", $"{label} {propertyName}")
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int dimension)
                ? dimension
                : 0)
            .ToArray();
        long elementCount = 1;
        if (shape.Length is < 1 or > 4 || shape.Any(static dimension => dimension <= 0))
        {
            throw new InvalidDataException($"{label} tensor '{propertyName}' has an invalid shape.");
        }

        foreach (int dimension in shape)
        {
            elementCount = checked(elementCount * dimension);
        }

        if (elementCount > 100_000_000)
        {
            throw new InvalidDataException($"{label} tensor '{propertyName}' is unreasonably large.");
        }

        return new TensorEvidence(sha256.ToLowerInvariant(), "float32", Array.AsReadOnly(shape));
    }

    private static TensorEvidence? ReadOptionalTensorEvidence(
        JsonElement parent,
        string propertyName,
        string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new InvalidDataException($"{label} field '{propertyName}' is missing.");
        }

        return value.ValueKind == JsonValueKind.Null
            ? null
            : ReadTensorEvidence(parent, propertyName, label);
    }

    private static Dictionary<string, int> ReadMarkerCreationResults(
        byte[] bytes,
        string splitSha256,
        string corePredictionsSha256,
        string detectionModelSha256,
        string recognitionModelSha256,
        IReadOnlyDictionary<string, SplitCase> cases)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        RejectDuplicatePropertyNames(root, "OCR marker creation evidence");
        RequireString(
            root,
            "schema",
            "graphreader.ocr-structure-consensus-marker-results.v1",
            "OCR marker creation evidence");
        RequireString(root, "profile", ProductionOcrAdapter.ApprovalBenchmarkProfile, "OCR marker creation evidence");
        RequireString(root, "provider", "cpu", "OCR marker creation evidence");
        RequireString(root, "stage", "markers", "OCR marker creation evidence");
        RequireString(
            root,
            "composition_id",
            "production-ocr-to-marker-composed-v1",
            "OCR marker creation evidence");
        string runId = RequiredString(root, "run_id", "OCR marker creation evidence");
        if (!Guid.TryParseExact(runId, "D", out Guid parsedRunId) || parsedRunId == Guid.Empty ||
            !string.Equals(parsedRunId.ToString("D"), runId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("OCR marker creation evidence run_id is not canonical.");
        }

        _ = RequiredString(root, "marker_model_id", "OCR marker creation evidence");
        RequireSha256(root, "marker_model_sha256", "OCR marker creation evidence");
        RequireMatchingSha256(root, "sealed_split_sha256", splitSha256, "OCR marker creation evidence");
        RequireMatchingSha256(
            root,
            "ocr_core_predictions_sha256",
            corePredictionsSha256,
            "OCR marker creation evidence");
        RequireMatchingSha256(
            root,
            "detection_model_sha256",
            detectionModelSha256,
            "OCR marker creation evidence");
        RequireMatchingSha256(
            root,
            "recognition_model_sha256",
            recognitionModelSha256,
            "OCR marker creation evidence");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement item in RequiredArray(root, "records", "OCR marker creation evidence").EnumerateArray())
        {
            string caseId = RequiredString(item, "case_id", "OCR marker creation record");
            string sourceSha256 = RequireSha256(item, "source_sha256", "OCR marker creation record");
            string detectorImageBgrSha256 = RequireSha256(
                item,
                "detector_image_bgr_sha256",
                "OCR marker creation record");
            int count = RequiredNonNegativeInt32(item, "marker_creation_count", "OCR marker creation record");
            if (!cases.TryGetValue(caseId, out SplitCase? fixture) ||
                !string.Equals(sourceSha256, fixture.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    detectorImageBgrSha256,
                    fixture.DetectorImageBgrSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !counts.TryAdd(caseId, count))
            {
                throw new InvalidDataException(
                    "OCR marker creation evidence contains invalid, changed, or duplicate fixture records.");
            }
        }

        if (!cases.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(counts.Keys))
        {
            throw new InvalidDataException(
                "OCR marker creation evidence does not cover the complete frozen split.");
        }

        return counts;
    }

    private static bool TruthMatchesFamily(string display, string truth, string family) => family switch
    {
        "integer" => display == truth && truth.All(char.IsAsciiDigit),
        "decimal" => truth.Count(static character => character == '.') == 1 &&
            display == truth && truth.Where(static character => character != '.').All(char.IsAsciiDigit),
        "negative" => truth.Length > 1 && truth[0] == '-' &&
            display == truth && truth[1..].All(char.IsAsciiDigit),
        "percentage" => truth.Length > 1 && truth[^1] == '%' &&
            display == truth && truth[..^1].All(char.IsAsciiDigit),
        "ambiguity" => display != truth &&
            display.Any(static character => character is 'O' or 'o' or 'l' or 'I') &&
            IsNormalizedNumericTruth(truth),
        _ => false,
    };

    private static bool IsNormalizedNumericTruth(string truth) =>
        truth.All(char.IsAsciiDigit) ||
        (truth.Count(static character => character == '.') == 1 &&
         truth.Where(static character => character != '.').All(char.IsAsciiDigit)) ||
        (truth.Length > 1 && truth[0] == '-' && truth[1..].All(char.IsAsciiDigit)) ||
        (truth.Length > 1 && truth[^1] == '%' && truth[..^1].All(char.IsAsciiDigit));

    private static Dictionary<string, Prediction> ReadPredictions(
        byte[] bytes,
        string expectedSchema,
        string splitSha256,
        string fixtureArchiveSha256,
        string? expectedCorePredictionsSha256,
        string detectionModelSha256,
        string recognitionModelSha256,
        string alphabet,
        bool requireMarkerCount)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        RejectDuplicatePropertyNames(root, "OCR predictions");
        RequireString(root, "schema", expectedSchema, "OCR predictions");
        RequireString(root, "profile", ProductionOcrAdapter.ApprovalBenchmarkProfile, "OCR predictions");
        RequireString(root, "provider", "cpu", "OCR predictions");
        RequireString(root, "composition_id", CompositionId, "OCR predictions");
        RequireMatchingSha256(root, "sealed_split_sha256", splitSha256, "OCR predictions");
        RequireMatchingSha256(
            root,
            "fixture_archive_sha256",
            fixtureArchiveSha256,
            "OCR predictions");
        if (expectedCorePredictionsSha256 is not null)
        {
            RequireMatchingSha256(
                root,
                "core_predictions_sha256",
                expectedCorePredictionsSha256,
                "OCR predictions");
        }
        RequireMatchingSha256(root, "detection_model_sha256", detectionModelSha256, "OCR predictions");
        RequireMatchingSha256(root, "recognition_model_sha256", recognitionModelSha256, "OCR predictions");
        var result = new Dictionary<string, Prediction>(StringComparer.Ordinal);
        foreach (JsonElement item in RequiredArray(root, "records", "OCR predictions").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Every OCR prediction must be an object.");
            }

            string caseId = RequiredString(item, "case_id", "OCR prediction");
            string sourceSha256 = RequireSha256(item, "source_sha256", "OCR prediction");
            string sourceBgrSha256 = RequireSha256(item, "source_bgr_sha256", "OCR prediction");
            string detectorImageBgrSha256 = RequireSha256(
                item,
                "detector_image_bgr_sha256",
                "OCR prediction");
            RequireString(item, "composition_id", CompositionId, "OCR prediction");
            string text = RequiredStringAllowEmpty(item, "predicted_text", "OCR prediction");
            string role = RequiredString(item, "predicted_role", "OCR prediction");
            IReadOnlyList<EvidenceRegion> modelRegions = ReadEvidenceRegions(
                item,
                "model_regions",
                "OCR prediction");
            IReadOnlyList<EvidenceCandidate> structureCandidates = ReadEvidenceCandidates(item);
            IReadOnlyList<ConsensusMatch> consensusMatches = ReadConsensusMatches(item);
            IReadOnlyList<EvidenceRegion> finalRegions = ReadEvidenceRegions(
                item,
                "final_regions",
                "OCR prediction");
            TensorEvidence detectorInput = ReadTensorEvidence(
                item,
                "detector_input_tensor",
                "OCR prediction");
            TensorEvidence detectorOutput = ReadTensorEvidence(
                item,
                "detector_output_tensor",
                "OCR prediction");
            bool recognizerExecuted = RequiredBoolean(
                item,
                "recognizer_executed",
                "OCR prediction");
            TensorEvidence? recognizerInput = ReadOptionalTensorEvidence(
                item,
                "recognizer_input_tensor",
                "OCR prediction");
            TensorEvidence? recognizerOutput = ReadOptionalTensorEvidence(
                item,
                "recognizer_output_tensor",
                "OCR prediction");
            if (recognizerExecuted != (recognizerInput is not null && recognizerOutput is not null))
            {
                throw new InvalidDataException(
                    "OCR prediction recognizer execution does not match its tensor evidence.");
            }

            int markerCreationCount = requireMarkerCount
                ? RequiredNonNegativeInt32(item, "marker_creation_count", "OCR prediction")
                : 0;
            if (text.Length > 64 ||
                text.Any(character => !alphabet.Contains(character)) ||
                !AllowedRoles.Contains(role) ||
                !result.TryAdd(caseId, new Prediction(
                    sourceSha256,
                    sourceBgrSha256,
                    detectorImageBgrSha256,
                    text,
                    role,
                    RequiredNonNegativeInt32(item, "detected_region_count", "OCR prediction"),
                    RequiredNonNegativeInt32(item, "false_region_count", "OCR prediction"),
                    RequiredNonNegativeInt32(item, "duplicate_region_count", "OCR prediction"),
                    markerCreationCount,
                    modelRegions,
                    structureCandidates,
                    consensusMatches,
                    finalRegions,
                    detectorInput,
                    detectorOutput,
                    recognizerExecuted,
                    recognizerInput,
                    recognizerOutput)))
            {
                throw new InvalidDataException("OCR predictions contain invalid or duplicate values.");
            }
        }

        return result;
    }

    private static ReadOnlyCollection<EvidenceRegion> ReadEvidenceRegions(
        JsonElement parent,
        string propertyName,
        string label)
    {
        var regions = new List<EvidenceRegion>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in RequiredArray(parent, propertyName, label).EnumerateArray())
        {
            string regionId = RequiredString(item, "region_id", $"{label} {propertyName}");
            EvidenceBounds bounds = ReadRequiredBounds(item, "bounds", $"{label} {propertyName}");
            double confidence = RequiredFiniteDouble(item, "confidence", $"{label} {propertyName}");
            if (confidence is < 0 or > 1 || !identifiers.Add(regionId))
            {
                throw new InvalidDataException($"{label} field '{propertyName}' contains invalid regions.");
            }

            regions.Add(new EvidenceRegion(regionId, bounds, confidence));
        }

        return Array.AsReadOnly(regions.ToArray());
    }

    private static ReadOnlyCollection<EvidenceCandidate> ReadEvidenceCandidates(JsonElement parent)
    {
        var candidates = new List<EvidenceCandidate>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in RequiredArray(
                     parent,
                     "structure_candidates",
                     "OCR prediction").EnumerateArray())
        {
            string regionId = RequiredString(item, "region_id", "OCR structure candidate");
            EvidenceBounds bounds = ReadRequiredBounds(item, "bounds", "OCR structure candidate");
            double textLikelihood = RequiredFiniteDouble(
                item,
                "text_likelihood",
                "OCR structure candidate");
            double structureLikelihood = RequiredFiniteDouble(
                item,
                "structure_likelihood",
                "OCR structure candidate");
            bool likelyGraphStructure = RequiredBoolean(
                item,
                "likely_graph_structure",
                "OCR structure candidate");
            int componentCount = RequiredPositiveInt32(
                item,
                "component_count",
                "OCR structure candidate");
            if (textLikelihood is < 0 or > 1 || structureLikelihood is < 0 or > 1 ||
                !identifiers.Add(regionId))
            {
                throw new InvalidDataException("OCR structure candidate evidence is invalid.");
            }

            candidates.Add(new EvidenceCandidate(
                regionId,
                bounds,
                textLikelihood,
                structureLikelihood,
                likelyGraphStructure,
                componentCount));
        }

        return Array.AsReadOnly(candidates.ToArray());
    }

    private static ReadOnlyCollection<ConsensusMatch> ReadConsensusMatches(JsonElement parent)
    {
        var matches = new List<ConsensusMatch>();
        foreach (JsonElement item in RequiredArray(
                     parent,
                     "consensus_matches",
                     "OCR prediction").EnumerateArray())
        {
            string modelId = RequiredString(item, "model_region_id", "OCR consensus match");
            string candidateId = RequiredString(item, "candidate_region_id", "OCR consensus match");
            double overlap = RequiredFiniteDouble(
                item,
                "overlap_coefficient",
                "OCR consensus match");
            if (overlap is < 0 or > 1)
            {
                throw new InvalidDataException("OCR consensus overlap coefficient is invalid.");
            }

            matches.Add(new ConsensusMatch(modelId, candidateId, overlap));
        }

        return Array.AsReadOnly(matches.ToArray());
    }

    private static EvidenceBounds ReadRequiredBounds(
        JsonElement parent,
        string propertyName,
        string label)
    {
        EvidenceBounds? bounds = ReadOptionalBounds(
            parent,
            propertyName,
            int.MaxValue,
            int.MaxValue,
            label);
        return bounds ?? throw new InvalidDataException(
            $"{label} field '{propertyName}' must not be null.");
    }

    private static double ReadRuntimeResults(
        byte[] bytes,
        string evaluatorSha256,
        string workflowSha256,
        string splitSha256,
        string fixtureArchiveSha256,
        string corePredictionsSha256,
        string predictionsSha256,
        string detectionModelSha256,
        string recognitionModelSha256)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        RejectDuplicatePropertyNames(root, "OCR runtime results");
        RequireString(
            root,
            "schema",
            "graphreader.ocr-structure-consensus-runtime-results.v1",
            "OCR runtime results");
        RequireString(root, "profile", ProductionOcrAdapter.ApprovalBenchmarkProfile, "OCR runtime results");
        RequireString(root, "provider", "cpu", "OCR runtime results");
        RequireString(root, "composition_id", CompositionId, "OCR runtime results");
        RequireBoolean(root, "detection_executed", true, "OCR runtime results");
        RequireBoolean(root, "recognition_executed", true, "OCR runtime results");
        RequireMatchingSha256(root, "evaluator_source_sha256", evaluatorSha256, "OCR runtime results");
        RequireMatchingSha256(root, "sealed_split_sha256", splitSha256, "OCR runtime results");
        RequireMatchingSha256(
            root,
            "core_predictions_sha256",
            corePredictionsSha256,
            "OCR runtime results");
        RequireMatchingSha256(root, "predictions_sha256", predictionsSha256, "OCR runtime results");
        RequireMatchingSha256(root, "detection_model_sha256", detectionModelSha256, "OCR runtime results");
        RequireMatchingSha256(root, "recognition_model_sha256", recognitionModelSha256, "OCR runtime results");
        JsonElement provenance = RequiredObject(
            root,
            "execution_provenance",
            "OCR runtime results");
        RequireMatchingSha256(
            provenance,
            "fixture_archive_sha256",
            fixtureArchiveSha256,
            "OCR execution provenance");
        RequireMatchingSha256(
            provenance,
            "workflow_source_sha256",
            workflowSha256,
            "OCR execution provenance");
        RequireSha256(provenance, "conversion_report_sha256", "OCR execution provenance");
        RequireSha256(provenance, "python_executable_sha256", "OCR execution provenance");
        RequireString(provenance, "python_implementation", "CPython", "OCR execution provenance");
        _ = RequiredString(provenance, "python_version", "OCR execution provenance");
        _ = RequiredString(provenance, "onnxruntime_version", "OCR execution provenance");
        _ = RequiredString(provenance, "numpy_version", "OCR execution provenance");
        _ = RequiredString(provenance, "pillow_version", "OCR execution provenance");
        _ = RequiredString(provenance, "opencv_version", "OCR execution provenance");
        string[] providers = RequiredArray(
                provenance,
                "onnxruntime_providers",
                "OCR execution provenance")
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : string.Empty)
            .ToArray();
        if (!providers.Contains("CPUExecutionProvider", StringComparer.Ordinal) ||
            providers.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException(
                "OCR execution provenance must identify the CPUExecutionProvider.");
        }
        double detectionMaximum = MaximumAbsoluteError(
            RequiredArray(root, "detection_parity", "OCR runtime results"),
            "OCR detection parity");
        double recognitionMaximum = MaximumAbsoluteError(
            RequiredArray(root, "recognition_parity", "OCR runtime results"),
            "OCR recognition parity");
        return Math.Max(detectionMaximum, recognitionMaximum);
    }

    private static double MaximumAbsoluteError(JsonElement values, string label)
    {
        JsonElement[] pairs = values.EnumerateArray().ToArray();
        if (pairs.Length < 16)
        {
            throw new InvalidDataException($"{label} requires at least 16 direct value pairs.");
        }

        double maximum = 0;
        foreach (JsonElement pair in pairs)
        {
            double reference = RequiredFiniteDouble(pair, "reference", label);
            double onnx = RequiredFiniteDouble(pair, "onnx", label);
            maximum = Math.Max(maximum, Math.Abs(reference - onnx));
        }

        return maximum;
    }

    private static PartitionMetrics EvaluatePartition(
        IReadOnlyDictionary<string, SplitCase> cases,
        IReadOnlyDictionary<string, Prediction> predictions,
        string partition)
    {
        KeyValuePair<string, SplitCase>[] selected = cases
            .Where(pair => pair.Value.Partition == partition && pair.Value.Kind == "text")
            .ToArray();
        long truthCharacters = selected.Sum(pair => pair.Value.TruthText.Length);
        if (selected.Length == 0 || truthCharacters == 0)
        {
            throw new InvalidDataException($"OCR partition '{partition}' has no evaluable text.");
        }

        int exact = selected.Count(pair =>
            string.Equals(pair.Value.TruthText, predictions[pair.Key].Text, StringComparison.Ordinal));
        int roles = selected.Count(pair =>
            string.Equals(pair.Value.TruthRole, predictions[pair.Key].Role, StringComparison.Ordinal));
        long edits = selected.Sum(pair =>
            EditDistance(pair.Value.TruthText, predictions[pair.Key].Text));
        return new PartitionMetrics(
            exact / (double)selected.Length,
            edits / (double)truthCharacters,
            roles / (double)selected.Length);
    }

    private static int EditDistance(string expected, string actual)
    {
        var previous = Enumerable.Range(0, actual.Length + 1).ToArray();
        var current = new int[actual.Length + 1];
        for (int expectedIndex = 1; expectedIndex <= expected.Length; expectedIndex++)
        {
            current[0] = expectedIndex;
            for (int actualIndex = 1; actualIndex <= actual.Length; actualIndex++)
            {
                current[actualIndex] = Math.Min(
                    Math.Min(current[actualIndex - 1] + 1, previous[actualIndex] + 1),
                    previous[actualIndex - 1] +
                    (expected[expectedIndex - 1] == actual[actualIndex - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[actual.Length];
    }

    private static void ValidateThresholds(ProductionOcrGateEvidence evidence)
    {
        if (evidence.ValidationExactMatch < 0.90 || evidence.ValidationCer > 0.05 ||
            evidence.ValidationRoleAccuracy < 0.90 || evidence.SealedTestExactMatch < 0.90 ||
            evidence.SealedTestCer > 0.05 || evidence.SealedTestRoleAccuracy < 0.90 ||
            evidence.OnnxMaxAbsError > 1e-4 || evidence.DetectionExactRate != 1 ||
            evidence.DuplicateRegionCount != 0 || evidence.ExclusionFalseRegionCount != 0 ||
            evidence.MarkerCreationCount != 0)
        {
            throw new InvalidDataException(
                "OCR direct resources do not meet the fixed production approval thresholds.");
        }
    }

    private static void MatchReportedMetrics(JsonElement root, ProductionOcrGateEvidence evidence)
    {
        RequireExactDouble(root, "validation_exact_match", evidence.ValidationExactMatch, "OCR public gate evidence");
        RequireExactDouble(root, "validation_cer", evidence.ValidationCer, "OCR public gate evidence");
        RequireExactDouble(root, "validation_role_accuracy", evidence.ValidationRoleAccuracy, "OCR public gate evidence");
        RequireExactDouble(root, "sealed_test_exact_match", evidence.SealedTestExactMatch, "OCR public gate evidence");
        RequireExactDouble(root, "sealed_test_cer", evidence.SealedTestCer, "OCR public gate evidence");
        RequireExactDouble(root, "sealed_test_role_accuracy", evidence.SealedTestRoleAccuracy, "OCR public gate evidence");
        RequireExactDouble(root, "onnx_max_abs_error", evidence.OnnxMaxAbsError, "OCR public gate evidence");
        RequireExactDouble(root, "detection_exact_rate", evidence.DetectionExactRate, "OCR public gate evidence");
        if (RequiredNonNegativeInt32(root, "duplicate_region_count", "OCR public gate evidence") !=
            evidence.DuplicateRegionCount ||
            RequiredNonNegativeInt32(root, "exclusion_false_region_count", "OCR public gate evidence") !=
            evidence.ExclusionFalseRegionCount)
        {
            throw new InvalidDataException(
                "OCR public gate structure-exclusion counts do not match direct predictions.");
        }

        if (RequiredNonNegativeInt32(root, "marker_creation_count", "OCR public gate evidence") !=
            evidence.MarkerCreationCount)
        {
            throw new InvalidDataException(
                "OCR public gate marker creation count does not match direct predictions.");
        }

        RequireMatchingSha256(root, "evaluator_source_sha256", evidence.EvaluatorSourceSha256, "OCR public gate evidence");
        RequireMatchingSha256(root, "sealed_split_sha256", evidence.SealedSplitSha256, "OCR public gate evidence");
        RequireMatchingSha256(root, "predictions_sha256", evidence.PredictionsSha256, "OCR public gate evidence");
        RequireMatchingSha256(root, "runtime_results_sha256", evidence.RuntimeResultsSha256, "OCR public gate evidence");
    }

    private static void RequireApprovalBenchmark(
        ResolvedProductionModel model,
        ProductionOcrGateEvidence evidence)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(model.ManifestPath));
        JsonElement[] approvals = RequiredArray(
                document.RootElement,
                "benchmarks",
                $"OCR model '{model.Identity.ModelId}'")
            .EnumerateArray()
            .Where(static benchmark => benchmark.ValueKind == JsonValueKind.Object &&
                benchmark.TryGetProperty("production_approval", out JsonElement approval) &&
                approval.ValueKind == JsonValueKind.True)
            .ToArray();
        if (approvals.Length != 1)
        {
            throw new InvalidDataException(
                $"OCR model '{model.Identity.ModelId}' must contain one production approval benchmark.");
        }

        JsonElement selected = approvals[0];
        string label = $"OCR model '{model.Identity.ModelId}' benchmark";
        RequireString(selected, "profile", ProductionOcrAdapter.ApprovalBenchmarkProfile, label);
        RequireString(selected, "status", "pass", label);
        RequireBoolean(selected, "release_eligible", true, label);
        RequireMatchingSha256(selected, "evidence_sha256", model.BenchmarkEvidenceSha256, label);
        RequireMatchingSha256(selected, "evaluator_source_sha256", evidence.EvaluatorSourceSha256, label);
        RequireMatchingSha256(selected, "sealed_split_sha256", evidence.SealedSplitSha256, label);
        RequireMatchingSha256(selected, "predictions_sha256", evidence.PredictionsSha256, label);
        RequireMatchingSha256(selected, "runtime_results_sha256", evidence.RuntimeResultsSha256, label);
        RequireExactDouble(selected, "sealed_test_exact_match", evidence.SealedTestExactMatch, label);
        RequireExactDouble(selected, "sealed_test_cer", evidence.SealedTestCer, label);
        RequireExactDouble(selected, "onnx_max_abs_error", evidence.OnnxMaxAbsError, label);
    }

    private static string ReadRecognitionAlphabet(string manifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement outputs = RequiredArray(document.RootElement, "outputs", "OCR recognition manifest");
        if (outputs.GetArrayLength() != 1 || outputs[0].ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("OCR recognition manifest requires one output.");
        }

        return RequiredString(outputs[0], "alphabet", "OCR recognition output");
    }

    private static EmbeddedResource ReadEmbeddedResource(
        JsonElement resources,
        string propertyName,
        string mediaType)
    {
        JsonElement resource = RequiredObject(resources, propertyName, "OCR reviewed resources");
        RequireString(resource, "media_type", mediaType, $"OCR resource '{propertyName}'");
        RequireString(resource, "encoding", "base64", $"OCR resource '{propertyName}'");
        string sha256 = RequireSha256(resource, "sha256", $"OCR resource '{propertyName}'");
        string content = RequiredString(resource, "content_base64", $"OCR resource '{propertyName}'");
        if (content.Length > ((MaximumResourceBytes + 2) / 3 * 4) + 4)
        {
            throw new InvalidDataException($"OCR resource '{propertyName}' exceeds the size limit.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(content);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"OCR resource '{propertyName}' is not valid base64.", exception);
        }

        if (bytes.Length == 0 || bytes.Length > MaximumResourceBytes ||
            !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"OCR resource '{propertyName}' failed checksum validation.");
        }

        return new EmbeddedResource(sha256.ToLowerInvariant(), bytes);
    }

    private static JsonElement RequiredObject(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be an object.");
        }

        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be an array.");
        }

        return value;
    }

    private static string RequiredString(JsonElement parent, string propertyName, string label)
    {
        string value = RequiredStringAllowEmpty(parent, propertyName, label);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be nonempty.");
        }

        return value;
    }

    private static string RequiredStringAllowEmpty(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be a string.");
        }

        return value.GetString()!;
    }

    private static void RequireString(
        JsonElement parent,
        string propertyName,
        string expected,
        string label)
    {
        string actual = RequiredString(parent, propertyName, label);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' must be '{expected}', found '{actual}'.");
        }
    }

    private static void RequireBoolean(
        JsonElement parent,
        string propertyName,
        bool expected,
        string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            value.GetBoolean() != expected)
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' must be {expected.ToString().ToLowerInvariant()}.");
        }
    }

    private static bool RequiredBoolean(
        JsonElement parent,
        string propertyName,
        string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static int RequiredNonNegativeInt32(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result) || result < 0)
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be a nonnegative int32.");
        }

        return result;
    }

    private static int RequiredPositiveInt32(JsonElement parent, string propertyName, string label)
    {
        int result = RequiredNonNegativeInt32(parent, propertyName, label);
        if (result <= 0)
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be a positive int32.");
        }

        return result;
    }

    private static double RequiredFiniteDouble(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) || !double.IsFinite(result))
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be finite.");
        }

        return result;
    }

    private static void RequireExactDouble(
        JsonElement parent,
        string propertyName,
        double expected,
        string label)
    {
        double actual = RequiredFiniteDouble(parent, propertyName, label);
        if (Math.Abs(actual - expected) > 1e-12)
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' does not match metrics derived from direct resources.");
        }
    }

    private static string RequireSha256(JsonElement parent, string propertyName, string label)
    {
        string value = RequiredString(parent, propertyName, label);
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be SHA-256.");
        }

        return value;
    }

    private static void RequireMatchingSha256(
        JsonElement parent,
        string propertyName,
        string expected,
        string label)
    {
        string actual = RequireSha256(parent, propertyName, label);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' does not match its checksum-bound resource.");
        }
    }

    private static void RejectDuplicatePropertyNames(JsonElement element, string label)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"{label} contains duplicate property '{property.Name}'.");
                }

                RejectDuplicatePropertyNames(property.Value, label);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicatePropertyNames(item, label);
            }
        }
    }

    private static void VerifyChecksum(string path, string expectedSha256, string label)
    {
        if (!File.Exists(path) || !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The checksum-resolved {label} is missing or changed.");
        }
    }

    private sealed record EmbeddedResource(string Sha256, byte[] Bytes);

    private sealed record SplitCase(
        string Partition,
        string Kind,
        string Family,
        string DisplayText,
        string TruthText,
        string TruthRole,
        int ExpectedRegionCount,
        string SourcePath,
        string SourceSha256,
        int SourceWidth,
        int SourceHeight,
        string SourceBgrSha256,
        string DetectorImageBgrSha256,
        IReadOnlyList<MaskRectangle> MaskRectangles,
        EvidenceBounds? TruthBounds);

    private sealed record Prediction(
        string SourceSha256,
        string SourceBgrSha256,
        string DetectorImageBgrSha256,
        string Text,
        string Role,
        int DetectedRegionCount,
        int FalseRegionCount,
        int DuplicateRegionCount,
        int MarkerCreationCount,
        IReadOnlyList<EvidenceRegion> ModelRegions,
        IReadOnlyList<EvidenceCandidate> StructureCandidates,
        IReadOnlyList<ConsensusMatch> ConsensusMatches,
        IReadOnlyList<EvidenceRegion> FinalRegions,
        TensorEvidence DetectorInput,
        TensorEvidence DetectorOutput,
        bool RecognizerExecuted,
        TensorEvidence? RecognizerInput,
        TensorEvidence? RecognizerOutput);

    private sealed record TensorEvidence(
        string Sha256,
        string Dtype,
        IReadOnlyList<int> Shape);

    private sealed record MaskRectangle(
        string Kind,
        int Left,
        int Top,
        int Right,
        int Bottom);

    private sealed record EvidenceBounds(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;

        public double Height => Bottom - Top;

        public double Area => Width * Height;

        public bool IsValid =>
            double.IsFinite(Left) && double.IsFinite(Top) &&
            double.IsFinite(Right) && double.IsFinite(Bottom) &&
            Right > Left && Bottom > Top;
    }

    private sealed record EvidenceRegion(
        string RegionId,
        EvidenceBounds Bounds,
        double Confidence);

    private sealed record EvidenceCandidate(
        string RegionId,
        EvidenceBounds Bounds,
        double TextLikelihood,
        double StructureLikelihood,
        bool LikelyGraphStructure,
        int ComponentCount);

    private sealed record ConsensusMatch(
        string ModelRegionId,
        string CandidateRegionId,
        double OverlapCoefficient);

    private sealed record ConsensusCandidate(
        EvidenceRegion Model,
        EvidenceCandidate Candidate,
        double OverlapCoefficient);

    private sealed record PartitionMetrics(double ExactMatch, double Cer, double RoleAccuracy);
}
