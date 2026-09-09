// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class AdvisoryStructureExperiment
{
    internal const string ProtocolPath =
        "ml/ocr/official_bakeoff/advisory_structure_dev_protocol.json";
    internal const string ProtocolSha256 =
        "3210a4312b83531d1192f50eafe47c54407e10cd3411558a5c45dd7c709846eb";
    internal const string BaselineCandidatePath =
        "artifacts/synthetic-runtime-evidence/initial-contour-source-c96/candidate.json";
    internal const string BaselineCandidateSha256 =
        "15dc39a373942278bec23f029cb59f1f13d63b845204f1f238ae2a52e2119958";

    internal static (
        GraphStructureModelInput Input,
        string? InputProtocolSha256,
        string? GeometryProtocolSha256,
        GraphStructureConsensusAdmission Admission,
        string? AdmissionProtocolSha256) Read(
        JsonElement config,
        string split,
        string inputManifestPath,
        string inputManifestSha256,
        string repositoryRoot)
    {
        bool selected = config.TryGetProperty(
            "ocr_structure_admission", out JsonElement option);
        if (selected &&
            (option.ValueKind != JsonValueKind.String || option.GetString() != "advisory"))
        {
            throw new InvalidDataException(
                "Structure admission must be absent or exactly advisory for this experiment.");
        }

        if (!selected)
        {
            if (config.TryGetProperty("advisory_structure_protocol", out _))
            {
                throw new InvalidDataException(
                    "Advisory-structure protocol requires explicit advisory admission.");
            }

            (GraphStructureModelInput input, string? inputProtocol, string? geometryProtocol) =
                InitialContourOutputExperiment.Read(
                    config, split, inputManifestPath, inputManifestSha256, repositoryRoot);
            return (
                input,
                inputProtocol,
                geometryProtocol,
                GraphStructureConsensusAdmission.Required,
                null);
        }

        JsonElement binding = config.GetProperty("advisory_structure_protocol");
        string[] keys = binding.EnumerateObject().Select(static item => item.Name).ToArray();
        if (keys.Length != 2 || !keys.Contains("path", StringComparer.Ordinal) ||
            !keys.Contains("sha256", StringComparer.Ordinal) ||
            binding.GetProperty("path").GetString() != ProtocolPath ||
            binding.GetProperty("sha256").GetString() != ProtocolSha256)
        {
            throw new InvalidDataException(
                "Advisory structure admission requires its exact preregistered protocol.");
        }

        using JsonDocument protocolDocument = JsonDocument.Parse(ReadVerified(
            Resolve(repositoryRoot, ProtocolPath), ProtocolSha256));
        JsonElement declaration = protocolDocument.RootElement;
        JsonElement budget = declaration.GetProperty("budget");
        if (declaration.GetProperty("evidence_policy").GetString() !=
                "ml/policy/evidence-policy.json" ||
            budget.GetProperty("optimizer_steps").GetInt32() != 0 ||
            budget.GetProperty("private_reads").GetInt32() != 0 ||
            budget.GetProperty("sealed_runs").GetInt32() != 0 ||
            budget.GetProperty("production_approval").GetBoolean() ||
            budget.GetProperty("release_eligible").GetBoolean())
        {
            throw new InvalidDataException(
                "Advisory structure admission is an unapproved synthetic experiment only.");
        }

        JsonElement identities = declaration.GetProperty("split_identities");
        if (identities.GetProperty("baseline_candidate_path").GetString() !=
                BaselineCandidatePath ||
            identities.GetProperty("baseline_candidate_sha256").GetString() !=
                BaselineCandidateSha256 ||
            identities.GetProperty("baseline_protocol_path").GetString() !=
                InitialContourOutputExperiment.ProtocolPath ||
            identities.GetProperty("baseline_protocol_sha256").GetString() !=
                InitialContourOutputExperiment.ProtocolSha256)
        {
            throw new InvalidDataException(
                "Advisory structure admission does not bind the frozen initial-contour baseline.");
        }

        string selectedSplit = split switch
        {
            "train" => "train",
            "validation" => "dev",
            _ => throw new InvalidDataException(
                "Advisory structure admission requires a declared train/dev split."),
        };
        foreach (string name in new[] { "train", "dev" })
        {
            string path = Resolve(
                repositoryRoot,
                identities.GetProperty(name + "_manifest_path").GetString()!);
            string expected = identities.GetProperty(name + "_manifest_sha256").GetString()!;
            _ = ReadVerified(path, expected);
            if (name == selectedSplit &&
                (!string.Equals(
                    Path.GetFullPath(inputManifestPath),
                    path,
                    StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(inputManifestSha256, expected, StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "Advisory structure admission requires the exact complete input manifest.");
            }
        }

        byte[] baselineBytes = ReadVerified(
            Resolve(repositoryRoot, BaselineCandidatePath), BaselineCandidateSha256);
        JsonObject normalized = ValidateOnlyAdmissionChanged(config, JsonNode.Parse(baselineBytes)!);
        using JsonDocument normalizedDocument = JsonDocument.Parse(normalized.ToJsonString());
        (GraphStructureModelInput modelInput, string? inputProtocolSha256,
            string? geometryProtocolSha256) = InitialContourOutputExperiment.Read(
                normalizedDocument.RootElement,
                split,
                inputManifestPath,
                inputManifestSha256,
                repositoryRoot);
        if (modelInput != GraphStructureModelInput.Original ||
            inputProtocolSha256 != OriginalModelInputExperiment.ProtocolSha256 ||
            geometryProtocolSha256 != InitialContourOutputExperiment.ProtocolSha256)
        {
            throw new InvalidDataException(
                "Advisory structure admission must preserve the frozen original-input initial contour path.");
        }

        return (
            modelInput,
            inputProtocolSha256,
            geometryProtocolSha256,
            GraphStructureConsensusAdmission.Advisory,
            ProtocolSha256);
    }

    internal static JsonObject ValidateOnlyAdmissionChanged(JsonElement candidate, JsonNode baseline)
    {
        JsonObject normalized = JsonNode.Parse(candidate.GetRawText())!.AsObject();
        if (candidate.GetProperty("production_approved").GetBoolean() ||
            candidate.GetProperty("ocr_structure_admission").GetString() != "advisory" ||
            !normalized.Remove("ocr_structure_admission") ||
            !normalized.Remove("advisory_structure_protocol") ||
            !JsonNode.DeepEquals(normalized, baseline))
        {
            throw new InvalidDataException(
                "Only advisory structure admission and its protocol may differ from the frozen initial-contour candidate.");
        }

        return normalized;
    }

    private static string Resolve(string root, string relative)
    {
        root = Path.GetFullPath(root);
        if (Path.IsPathRooted(relative) ||
            relative.Replace('\\', '/').Split('/').Any(static part => part is "" or "." or ".."))
        {
            throw new InvalidDataException(
                "Experiment evidence requires canonical repository-relative paths.");
        }

        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Experiment evidence must remain inside the repository.");
        }

        return path;
    }

    private static byte[] ReadVerified(string path, string expected)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected)
        {
            throw new InvalidDataException(
                "Advisory-structure experiment evidence checksum mismatch.");
        }

        return bytes;
    }

    internal static void SelfTest()
    {
        JsonObject baseline = new()
        {
            ["production_approved"] = false,
            ["ocr_model_input"] = "original",
            ["ocr_output_geometry"] = "initial_db_contour",
            ["detector"] = "fixed-detector",
            ["recognizer"] = "fixed-recognizer",
        };
        JsonObject candidate = baseline.DeepClone().AsObject();
        candidate["ocr_structure_admission"] = "advisory";
        candidate["advisory_structure_protocol"] = new JsonObject
        {
            ["path"] = ProtocolPath,
            ["sha256"] = ProtocolSha256,
        };
        _ = ValidateOnlyAdmissionChanged(JsonSerializer.SerializeToElement(candidate), baseline);

        int rejected = 0;
        foreach (string key in new[]
        {
            "detector", "recognizer", "production_approved", "ocr_model_input",
            "ocr_output_geometry", "ocr_structure_admission",
        })
        {
            JsonObject changed = candidate.DeepClone().AsObject();
            changed[key] = key == "production_approved"
                ? JsonValue.Create(true)
                : JsonValue.Create("changed");
            try
            {
                _ = ValidateOnlyAdmissionChanged(
                    JsonSerializer.SerializeToElement(changed), baseline);
            }
            catch (InvalidDataException)
            {
                rejected++;
            }
        }

        if (rejected != 6)
        {
            throw new InvalidDataException(
                "Advisory-structure candidate isolation safeguards failed.");
        }

        JsonObject ordinary = new() { ["production_approved"] = false };
        foreach (JsonNode? invalidAdmission in new JsonNode?[]
        {
            JsonValue.Create("required"),
            JsonValue.Create("bogus"),
            JsonValue.Create(1),
            null,
        })
        {
            JsonObject invalid = ordinary.DeepClone().AsObject();
            invalid["ocr_structure_admission"] = invalidAdmission;
            try
            {
                _ = Read(
                    JsonSerializer.SerializeToElement(invalid),
                    "train",
                    "unused",
                    "unused",
                    "unused");
                throw new InvalidDataException(
                    "Malformed structure admission was accepted by the experiment guard.");
            }
            catch (InvalidDataException exception) when (
                exception.Message ==
                    "Structure admission must be absent or exactly advisory for this experiment.")
            {
            }
        }

        var defaults = Read(
            JsonSerializer.SerializeToElement(ordinary),
            "train",
            "unused",
            "unused",
            "unused");
        if (defaults.Input != GraphStructureModelInput.AxisMasked ||
            defaults.InputProtocolSha256 is not null ||
            defaults.GeometryProtocolSha256 is not null ||
            defaults.Admission != GraphStructureConsensusAdmission.Required ||
            defaults.AdmissionProtocolSha256 is not null)
        {
            throw new InvalidDataException(
                "Advisory-structure experiment changed the default path.");
        }

        Console.WriteLine(
            "Advisory-structure safeguards passed: six altered candidates rejected; " +
            "four malformed admissions rejected; default required admission preserved; " +
            "model runs 0; private reads 0.");
    }
}
