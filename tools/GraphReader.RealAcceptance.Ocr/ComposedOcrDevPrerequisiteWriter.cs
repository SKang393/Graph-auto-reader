// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;

namespace GraphReader.RealAcceptance.Ocr;

internal static partial class FrozenRealWorkflowAdmission
{
    internal static int RunComposedOcrDevPrerequisiteWriter(string[] args)
    {
        try
        {
            if (args.Length != 8) throw new InvalidDataException("Invalid prerequisite writer arguments");
            string root = Directory.GetCurrentDirectory();
            FrozenCandidateBinding binding = FrozenCandidateBinding.Load(root, args[3], args[4], CancellationToken.None);
            object result = WriteComposedOcrDevPrerequisite(root, args[1], args[2], CandidateIdentity(binding),
                args[5], args[6], args[7], CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("COMPOSED_OCR_DEV_PREREQUISITE_NOT_CREATED:" + exception.GetType().Name);
            return 1;
        }
    }

    internal static object WriteComposedOcrDevPrerequisite(string root, string sourcePath, string sourceSha,
        FrozenRealWorkflowCandidateIdentity candidate, string requestSha, string stageCandidateSha,
        string outputPath, CancellationToken cancellationToken)
    {
        root = Path.GetFullPath(root);
        sourceSha = FrozenCandidateBinding.RequireSha256(sourceSha, "source report hash");
        string execution = candidate.ComposedOcrExecutionSha256 ??
            throw new InvalidDataException("REAL_WORKFLOW_COMPOSED_OCR_EXECUTION_IDENTITY_REQUIRED");
        byte[] sourceBytes = ReadReferenced(root, sourcePath, sourceSha, cancellationToken);
        JsonElement development = ComposedOcrDevEvidence.Validate(sourceBytes, execution, requestSha, stageCandidateSha);
        JsonElement metrics = development.GetProperty("metrics"), geometry = metrics.GetProperty("assembled_geometry"),
            full = metrics.GetProperty("full_ocr_metrics");
        byte[] barsBytes = ReadBounded(Path.Combine(root, AcceptanceBarsPath), cancellationToken);
        string barsSha = FrozenCandidateBinding.Hash(barsBytes);
        string policySha = FrozenCandidateBinding.Hash(ReadBounded(Path.Combine(root, EvidencePolicyPath), cancellationToken));
        byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = ComposedOcrDevPrerequisiteSchema, task = "ocr-detection-recognition", split = "synthetic-dev",
            stage_revision = candidate.Revision, stage_candidate_id = candidate.CandidateId,
            evidence_policy_sha256 = policySha, acceptance_bar_sha256 = barsSha,
            runtime_composition_sha256 = candidate.ExecutionDescriptorSha256,
            operating_point_identity = candidate.OperatingPointIdentity,
            models = new { ocr_detection_sha256 = candidate.OcrDetectionSha256,
                ocr_recognition_sha256 = candidate.OcrRecognitionSha256, marker_center_sha256 = (string?)null },
            source_result = new { path = sourcePath, sha256 = sourceSha },
            source_identity = new { request_sha256 = requestSha, candidate_sha256 = stageCandidateSha },
            benchmarks = new[] { new { name = "full-source", truth_count = Integer(geometry, "truth_region_count"),
                true_positive = Integer(geometry, "true_positives"), false_positive = Integer(geometry, "false_positives"),
                false_negative = Integer(geometry, "false_negatives"), precision = Number(geometry, "precision"),
                recall = Number(geometry, "recall"), recognition_exact_match = Number(full, "recognition_exact_accuracy"),
                character_error_rate = Number(full, "character_error_rate"), role_accuracy = Number(full, "role_accuracy"),
                prohibited_structure_hit_rate = (double?)null } },
            parity = new { kind = "identical-native-execution", execution_sha256 = execution },
            aggregate_only = true, case_level_output = false, truth_rows_output = false, prediction_output = false, pixel_output = false,
        });
        // A failing source never produces a prerequisite file. Use the same
        // verifier as real admission, including the source-byte comparison.
        ValidatePrerequisite(envelope, root, "ocr-detection-recognition", "synthetic-dev", candidate,
            policySha, barsSha, ValidateAcceptanceBars(barsBytes), cancellationToken);
        string output = FrozenCandidateBinding.RequireUnderRoot(root, outputPath, "OCR prerequisite output");
        string artifactRoot = Path.Combine(root, "artifacts") + Path.DirectorySeparatorChar;
        if (!output.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OCR prerequisite output must remain under artifacts");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(envelope); stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new { status = "development_prerequisite_created",
            path = Path.GetRelativePath(root, output).Replace('\\', '/'), sha256 = FrozenCandidateBinding.Hash(envelope),
            stage_admission_granted = false, production_approved = false, private_reads = 0, sealed_reads = 0, model_inference = false };
    }
}
