// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class OfficialHeadTensorCaptureSelfTest
{
    public static object Run()
    {
        string legacy = Request(
            "graphreader.official-head-tensor-capture-request.v1",
            "project-owned-synthetic-train-dev-model-free",
            Splits(5, 1),
            Splits(28, 9));
        string supplemental = Request(
            "graphreader.supplemental-official-head-tensor-capture-request.v1",
            "project-owned-synthetic-train-only-supplemental-model-free",
            Splits(1, 0),
            Splits(6, 0));

        OfficialHeadTensorCapture.ValidateContractForSelfTest(
            legacy, OfficialHeadTensorCapture.LegacyCommand);
        OfficialHeadTensorCapture.ValidateContractForSelfTest(
            supplemental, OfficialHeadTensorCapture.SupplementalCommand);
        OfficialHeadTensorCapture.ValidateCandidateBindingForSelfTest(
            OfficialHeadTensorCapture.OfficialCandidateSha,
            OfficialHeadTensorCapture.SupplementalCommand);
        RequireRejected(legacy, OfficialHeadTensorCapture.SupplementalCommand);
        RequireRejected(supplemental, OfficialHeadTensorCapture.LegacyCommand);
        RequireRejected(
            Request(
                "graphreader.supplemental-official-head-tensor-capture-request.v1",
                "project-owned-synthetic-train-only-supplemental-model-free",
                Splits(1, 0),
                Splits(5, 1)),
            OfficialHeadTensorCapture.SupplementalCommand);
        RequireRejected(
            Request(
                "graphreader.official-head-tensor-capture-request.v1",
                "project-owned-synthetic-train-dev-model-free",
                Splits(6, 0),
                Splits(28, 9)),
            OfficialHeadTensorCapture.LegacyCommand);
        RequireCandidateRejected(
            new string('0', 64),
            OfficialHeadTensorCapture.SupplementalCommand);

        return new
        {
            Status = "passed",
            ModelLoaded = false,
            LegacyReports = 6,
            LegacyTrainPanels = 28,
            LegacyValidationPanels = 9,
            SupplementalReports = 1,
            SupplementalTrainPanels = 6,
            SupplementalValidationPanels = 0,
            CrossProfileRejections = 2,
            SplitCountRejections = 2,
            CandidateBindingRejections = 1,
        };
    }

    private static object[] Splits(int train, int validation) =>
        Enumerable.Repeat("train", train)
            .Concat(Enumerable.Repeat("validation", validation))
            .Select(static split => (object)new { split })
            .ToArray();

    private static string Request(
        string schema,
        string scope,
        object[] reports,
        object[] panels) => JsonSerializer.Serialize(new
        {
            schema,
            scope,
            synthetic_only = true,
            private_data = false,
            sealed_data = false,
            truth_included = false,
            model_inference = false,
            training_input_ready = false,
            production_approved = false,
            capture_source = new { },
            assemblies = Array.Empty<object>(),
            binding = new { },
            candidate = new { },
            detector = new { },
            native = new { },
            license_inputs = Array.Empty<object>(),
            maximum_side_length = 960,
            dimension_multiple = 128,
            detector_configuration_fingerprint =
                "7a8eb59f3b6980096e80247a6b195e25b3e0b887240e700aaf77cf5a1d64bc81",
            reports,
            panels,
        });

    private static void RequireRejected(string request, string command)
    {
        try
        {
            OfficialHeadTensorCapture.ValidateContractForSelfTest(request, command);
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidDataException("Tensor capture profile mismatch was accepted.");
    }

    private static void RequireCandidateRejected(string candidateSha256, string command)
    {
        try
        {
            OfficialHeadTensorCapture.ValidateCandidateBindingForSelfTest(
                candidateSha256, command);
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidDataException("Tensor capture candidate mismatch was accepted.");
    }
}
