// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class OfficialHeadTensorCaptureSelfTest
{
    private static readonly int[] MarkerStyleTestCrop = [5, 7, 180, 80];
    private static readonly int[] MarkerStyleOutOfBoundsTestCrop = [30, 7, 180, 80];
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
        string markerStyle = Request(
            "graphreader.marker-style-official-head-tensor-capture-request.v1",
            "project-owned-synthetic-train-only-marker-style-model-free",
            Splits(1, 0), Splits(9, 0));

        OfficialHeadTensorCapture.ValidateContractForSelfTest(
            legacy, OfficialHeadTensorCapture.LegacyCommand);
        OfficialHeadTensorCapture.ValidateContractForSelfTest(
            supplemental, OfficialHeadTensorCapture.SupplementalCommand);
        OfficialHeadTensorCapture.ValidateContractForSelfTest(
            markerStyle, OfficialHeadTensorCapture.MarkerStyleCommand);
        OfficialHeadTensorCapture.ValidateCandidateBindingForSelfTest(
            OfficialHeadTensorCapture.OfficialCandidateSha,
            OfficialHeadTensorCapture.SupplementalCommand);
        OfficialHeadTensorCapture.ValidateCandidateBindingForSelfTest(
            OfficialHeadTensorCapture.MarkerStyleCandidateSha,
            OfficialHeadTensorCapture.MarkerStyleCommand);
        RequireRejected(legacy, OfficialHeadTensorCapture.SupplementalCommand);
        RequireRejected(supplemental, OfficialHeadTensorCapture.LegacyCommand);
        RequireRejected(markerStyle, OfficialHeadTensorCapture.SupplementalCommand);
        RequireRejected(supplemental, OfficialHeadTensorCapture.MarkerStyleCommand);
        RequireRejected(legacy, OfficialHeadTensorCapture.MarkerStyleCommand);
        foreach ((string request, string command, int train, int validation) in new[]
        {
            (markerStyle, OfficialHeadTensorCapture.MarkerStyleCommand, 6, 0),
            (markerStyle, OfficialHeadTensorCapture.MarkerStyleCommand, 8, 1),
            (supplemental, OfficialHeadTensorCapture.SupplementalCommand, 9, 0),
            (legacy, OfficialHeadTensorCapture.LegacyCommand, 9, 0),
        })
        {
            JsonNode altered = JsonNode.Parse(request)!;
            altered["panels"] = JsonSerializer.SerializeToNode(Splits(train, validation));
            RequireRejected(altered.ToJsonString(), command);
        }
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
        RequireCandidateRejected(new string('0', 64), OfficialHeadTensorCapture.MarkerStyleCommand);
        RequireCandidateRejected(OfficialHeadTensorCapture.OfficialCandidateSha, OfficialHeadTensorCapture.MarkerStyleCommand);
        RequireCandidateRejected(OfficialHeadTensorCapture.MarkerStyleCandidateSha, OfficialHeadTensorCapture.SupplementalCommand);
        ValidateMarkerStyleSources();
        ValidateManifestPathBoundaries();

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
            MarkerStyleTrainPanels = 9,
            MarkerStyleValidationPanels = 0,
            CrossProfileRejections = 5,
            SplitCountRejections = 6,
            CandidateBindingRejections = 4,
            MarkerStyleSourceBindingRejections = 4,
            ManifestPathBoundaryRejections = 4,
        };
    }

    private static void ValidateManifestPathBoundaries()
    {
        // Pure path fixtures: sibling metadata is never searched or loaded here.
        string root = Path.GetFullPath("profile-worktree");
        string metadata = Path.Combine(Path.GetDirectoryName(root)!, "profile-metadata", "detector.json");
        string resolved = OfficialHeadTensorCapture.ResolveDetectorManifestPathForSelfTest(
            metadata, metadata, root, OfficialHeadTensorCapture.MarkerStyleCommand);
        if (resolved != metadata)
        {
            throw new InvalidDataException("Marker-style manifest path was not resolved exactly.");
        }
        foreach (string command in new[] { OfficialHeadTensorCapture.LegacyCommand, OfficialHeadTensorCapture.SupplementalCommand })
        {
            RequireManifestPathRejected(metadata, metadata, root, command);
            string relative = Path.Combine("models", "detector.json");
            _ = OfficialHeadTensorCapture.ResolveDetectorManifestPathForSelfTest(
                relative, Path.Combine(root, relative), root, command);
        }
        RequireManifestPathRejected(Path.Combine(root, "wrong.json"), metadata, root, OfficialHeadTensorCapture.MarkerStyleCommand);
        RequireManifestPathRejected("detector.json", metadata, root, OfficialHeadTensorCapture.MarkerStyleCommand);
    }

    private static void RequireManifestPathRejected(string requested, string candidate, string root, string command)
    {
        try
        {
            _ = OfficialHeadTensorCapture.ResolveDetectorManifestPathForSelfTest(requested, candidate, root, command);
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidDataException("Detector manifest path boundary mismatch was accepted.");
    }

    private static void ValidateMarkerStyleSources()
    {
        string[] hashes = Enumerable.Range(1, 9).Select(static value => value.ToString("x64", CultureInfo.InvariantCulture)).ToArray();
        var fixture = JsonSerializer.SerializeToNode(new
        {
            schema = "graphreader.ocr-marker-style-train-fixture.v1",
            scope = "owned-synthetic-train-only",
            optimizer_steps = 0, sealed_reads = 0, private_reads = 0,
            model_inference_runs = 0, production_approved = false,
            style_schedule = Enumerable.Range(0, 27).ToArray(),
            sources = hashes.Select(static sha => new { split = "train", image = new { sha256 = sha } }),
        })!;
        var manifest = JsonSerializer.SerializeToNode(new
        {
            schema = "graphreader.synthetic-runtime-raster-inputs.v1",
            source = "project-owned-synthetic-five-axis-family-v1", split = "train",
            contains_truth = false, contains_precomputed_masks = false,
            images = hashes.Select(static sha => new { split = "train", image_sha256 = sha, width = 200, height = 100 }),
        })!;
        var request = JsonSerializer.SerializeToNode(new
        {
            panels = hashes.Select(static sha => new { split = "train", source_sha256 = sha, crop = MarkerStyleTestCrop }),
        })!;
        OfficialHeadTensorCapture.ValidateMarkerStyleInventoryForSelfTest(
            fixture.ToJsonString(), manifest.ToJsonString(), request.ToJsonString());
        foreach (Action<JsonNode, JsonNode, JsonNode> mutate in new Action<JsonNode, JsonNode, JsonNode>[]
        {
            static (f, _, _) => f["sources"]![0]!["image"]!["sha256"] = new string('0', 64),
            static (_, m, _) => m["images"]![0]!["split"] = "dev",
            static (_, _, r) => r["panels"]![1]!["source_sha256"] = r["panels"]![0]!["source_sha256"]!.GetValue<string>(),
            static (_, _, r) => r["panels"]![0]!["crop"] = JsonSerializer.SerializeToNode(MarkerStyleOutOfBoundsTestCrop),
        })
        {
            JsonNode f = fixture.DeepClone(), m = manifest.DeepClone(), r = request.DeepClone();
            mutate(f, m, r);
            try
            {
                OfficialHeadTensorCapture.ValidateMarkerStyleInventoryForSelfTest(f.ToJsonString(), m.ToJsonString(), r.ToJsonString());
            }
            catch (InvalidDataException)
            {
                continue;
            }
            throw new InvalidDataException("Marker-style source binding mismatch was accepted.");
        }
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
