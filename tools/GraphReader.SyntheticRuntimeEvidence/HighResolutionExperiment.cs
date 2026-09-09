// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class HighResolutionExperiment
{
    internal const string ProtocolPath =
        "ml/ocr/official_bakeoff/high_resolution_dev_protocol.json";
    internal const string ProtocolSha256 =
        "91ec2252f7e51d215fcd8e05335de9d786665d29e1361f1d3a904d5c7bda0ea8";
    internal const int SelectedMaximumSideLength = 1920;

    private const string BaselineCandidatePath =
        "artifacts/synthetic-runtime-evidence/advisory-structure-source-c96/candidate.json";
    private const string BaselineCandidateSha256 =
        "eed14ce421d56b8251213043337b2867ab7f96410404544341e512b27ffabbdd";
    private const string BaselineProtocolPath =
        "ml/ocr/official_bakeoff/advisory_structure_dev_protocol.json";
    private const string BaselineProtocolSha256 =
        "3210a4312b83531d1192f50eafe47c54407e10cd3411558a5c45dd7c709846eb";
    private const string BaselineScorePath =
        "artifacts/synthetic-runtime-evidence/advisory-structure-score-run1.json";
    private const string BaselineScoreSha256 =
        "a3c2d913245acba7fbb5c17eac29adf96fc3368583af4c08f6fda548c2f2d154";
    private const string TrainManifestPath =
        "artifacts/synthetic-runtime-evidence/inputs-train393-layout2/input-manifest.json";
    private const string TrainManifestSha256 =
        "3a76a405d8e8eb2512988aba877a6d3cecb9975494c36e403344a197d84d0d37";
    private const string DevManifestPath =
        "artifacts/synthetic-runtime-evidence/inputs-dev393-layout2/input-manifest.json";
    private const string DevManifestSha256 =
        "197ac4eda3157f0883b80bb135e13ed41165eac23f0d758309a4823125b865b7";

    internal static (
        string NormalizedCandidateJson,
        int? MaximumSideLength,
        string? ProtocolPath,
        string? ProtocolSha256) Read(
        JsonElement config,
        string split,
        string inputManifestPath,
        string inputManifestSha256,
        string repositoryRoot)
    {
        if (config.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("High-resolution experiment candidate must be an object.");
        }

        RequireUniqueKeys(config, "high-resolution candidate");
        bool hasMaximum = config.TryGetProperty(
            "ocr_detector_maximum_side_length", out JsonElement maximum);
        bool hasProtocol = config.TryGetProperty(
            "detector_resolution_protocol", out JsonElement protocolBinding);
        if (!hasMaximum && !hasProtocol)
        {
            return (config.GetRawText(), null, null, null);
        }
        if (!hasMaximum || !hasProtocol ||
            maximum.ValueKind != JsonValueKind.Number ||
            maximum.GetRawText() != "1920" ||
            !maximum.TryGetInt32(out int maximumSideLength) ||
            maximumSideLength != SelectedMaximumSideLength)
        {
            throw new InvalidDataException(
                "Detector maximum side must be absent or exactly the preregistered integer 1920 with its protocol.");
        }

        RequireExactKeys(
            protocolBinding,
            "detector-resolution protocol binding",
            "path",
            "sha256");
        if (protocolBinding.GetProperty("path").ValueKind != JsonValueKind.String ||
            protocolBinding.GetProperty("sha256").ValueKind != JsonValueKind.String ||
            protocolBinding.GetProperty("path").GetString() != ProtocolPath ||
            protocolBinding.GetProperty("sha256").GetString() != ProtocolSha256)
        {
            throw new InvalidDataException(
                "High resolution requires its exact preregistered protocol binding.");
        }

        byte[] protocolBytes = ReadVerified(
            Resolve(repositoryRoot, ProtocolPath), ProtocolSha256,
            "high-resolution protocol");
        using JsonDocument protocolDocument = JsonDocument.Parse(protocolBytes);
        JsonElement protocol = protocolDocument.RootElement;
        ValidateProtocol(protocol, repositoryRoot);

        JsonElement identities = protocol.GetProperty("split_identities");
        string selectedSplit = split switch
        {
            "train" => "train",
            "validation" => "dev",
            _ => throw new InvalidDataException(
                "High-resolution experiment requires a declared train/dev split."),
        };
        foreach (string name in new[] { "train", "dev" })
        {
            string declaredPath = identities.GetProperty(name + "_manifest_path").GetString()!;
            string declaredSha = identities.GetProperty(name + "_manifest_sha256").GetString()!;
            string resolvedPath = Resolve(repositoryRoot, declaredPath);
            _ = ReadVerified(resolvedPath, declaredSha, $"{name} input manifest");
            if (name == selectedSplit &&
                (!string.Equals(
                    Path.GetFullPath(inputManifestPath),
                    resolvedPath,
                    StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(inputManifestSha256, declaredSha, StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "High-resolution experiment requires the exact complete input manifest.");
            }
        }

        byte[] baselineBytes = ReadVerified(
            Resolve(repositoryRoot, BaselineCandidatePath), BaselineCandidateSha256,
            "advisory baseline candidate");
        JsonObject normalized = JsonNode.Parse(config.GetRawText())!.AsObject();
        if (!config.TryGetProperty("production_approved", out JsonElement approval) ||
            approval.ValueKind != JsonValueKind.False ||
            !normalized.Remove("ocr_detector_maximum_side_length") ||
            !normalized.Remove("detector_resolution_protocol") ||
            !JsonNode.DeepEquals(normalized, JsonNode.Parse(baselineBytes)))
        {
            throw new InvalidDataException(
                "Only detector maximum side 1920 and its protocol may differ from the frozen advisory candidate.");
        }

        return (
            Encoding.UTF8.GetString(baselineBytes),
            SelectedMaximumSideLength,
            ProtocolPath,
            ProtocolSha256);
    }

    private static void ValidateProtocol(JsonElement protocol, string repositoryRoot)
    {
        RequireExactKeys(
            protocol,
            "high-resolution protocol",
            "evidence_policy",
            "hypothesis",
            "isolated_change",
            "split_identities",
            "metric",
            "acceptance_bar",
            "budget");
        if (protocol.GetProperty("evidence_policy").GetString() !=
            "ml/policy/evidence-policy.json")
        {
            throw new InvalidDataException(
                "High-resolution experiment does not bind the shared evidence policy.");
        }

        JsonElement budget = protocol.GetProperty("budget");
        RequireExactKeys(
            budget,
            "high-resolution budget",
            "synthetic_train_dev_runs",
            "optimizer_steps",
            "private_reads",
            "sealed_runs",
            "production_approval",
            "release_eligible");
        if (budget.GetProperty("synthetic_train_dev_runs").GetString() != "unlimited" ||
            budget.GetProperty("optimizer_steps").GetInt32() != 0 ||
            budget.GetProperty("private_reads").GetInt32() != 0 ||
            budget.GetProperty("sealed_runs").GetInt32() != 0 ||
            budget.GetProperty("production_approval").GetBoolean() ||
            budget.GetProperty("release_eligible").GetBoolean())
        {
            throw new InvalidDataException(
                "High-resolution experiment budget permits synthetic train/dev diagnosis only.");
        }

        JsonElement identities = protocol.GetProperty("split_identities");
        RequireExactKeys(
            identities,
            "high-resolution split identities",
            "baseline_candidate_path",
            "baseline_candidate_sha256",
            "baseline_protocol_path",
            "baseline_protocol_sha256",
            "baseline_score_path",
            "baseline_score_sha256",
            "train_manifest_path",
            "train_manifest_sha256",
            "dev_manifest_path",
            "dev_manifest_sha256",
            "train_source_count",
            "train_panel_count",
            "train_truth_count",
            "dev_source_count",
            "dev_panel_count",
            "dev_truth_count");
        if (identities.GetProperty("baseline_candidate_path").GetString() != BaselineCandidatePath ||
            identities.GetProperty("baseline_candidate_sha256").GetString() != BaselineCandidateSha256 ||
            identities.GetProperty("baseline_protocol_path").GetString() != BaselineProtocolPath ||
            identities.GetProperty("baseline_protocol_sha256").GetString() != BaselineProtocolSha256 ||
            identities.GetProperty("baseline_score_path").GetString() != BaselineScorePath ||
            identities.GetProperty("baseline_score_sha256").GetString() != BaselineScoreSha256 ||
            identities.GetProperty("train_manifest_path").GetString() != TrainManifestPath ||
            identities.GetProperty("train_manifest_sha256").GetString() != TrainManifestSha256 ||
            identities.GetProperty("dev_manifest_path").GetString() != DevManifestPath ||
            identities.GetProperty("dev_manifest_sha256").GetString() != DevManifestSha256 ||
            identities.GetProperty("train_source_count").GetInt32() != 4 ||
            identities.GetProperty("train_panel_count").GetInt32() != 4 ||
            identities.GetProperty("train_truth_count").GetInt32() != 146 ||
            identities.GetProperty("dev_source_count").GetInt32() != 3 ||
            identities.GetProperty("dev_panel_count").GetInt32() != 9 ||
            identities.GetProperty("dev_truth_count").GetInt32() != 183)
        {
            throw new InvalidDataException(
                "High-resolution experiment identities differ from the reviewed protocol.");
        }

        _ = ReadVerified(
            Resolve(repositoryRoot, BaselineProtocolPath), BaselineProtocolSha256,
            "advisory baseline protocol");
        _ = ReadVerified(
            Resolve(repositoryRoot, BaselineScorePath), BaselineScoreSha256,
            "advisory baseline score");
    }

    private static void RequireUniqueKeys(JsonElement value, string label)
    {
        string[] keys = value.EnumerateObject().Select(static property => property.Name).ToArray();
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
        {
            throw new InvalidDataException($"{label} contains duplicate properties.");
        }
    }

    private static void RequireExactKeys(
        JsonElement value,
        string label,
        params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} must be an object.");
        }
        string[] actual = value.EnumerateObject().Select(static property => property.Name).ToArray();
        if (actual.Length != expected.Length ||
            actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            expected.Except(actual, StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException($"{label} properties differ from the reviewed contract.");
        }
    }

    private static string Resolve(string root, string relative)
    {
        root = Path.GetFullPath(root);
        if (Path.IsPathRooted(relative) ||
            relative.Replace('\\', '/').Split('/').Any(static part => part is "" or "." or ".."))
        {
            throw new InvalidDataException(
                "High-resolution evidence requires canonical repository-relative paths.");
        }
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "High-resolution evidence must remain inside the repository.");
        }
        return path;
    }

    private static byte[] ReadVerified(string path, string expectedSha256, string label)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                expectedSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{label} checksum mismatch.");
        }
        return bytes;
    }

    internal static void SelfTest()
    {
        string root = Path.GetFullPath(".");
        byte[] baselineBytes = ReadVerified(
            Resolve(root, BaselineCandidatePath), BaselineCandidateSha256,
            "advisory baseline candidate");
        JsonObject candidate = JsonNode.Parse(baselineBytes)!.AsObject();
        candidate["ocr_detector_maximum_side_length"] = SelectedMaximumSideLength;
        candidate["detector_resolution_protocol"] = new JsonObject
        {
            ["path"] = ProtocolPath,
            ["sha256"] = ProtocolSha256,
        };
        JsonElement selected = JsonSerializer.SerializeToElement(candidate);

        foreach ((string split, string manifestPath, string manifestSha) in new[]
        {
            ("train", TrainManifestPath, TrainManifestSha256),
            ("validation", DevManifestPath, DevManifestSha256),
        })
        {
            var actual = Read(
                selected,
                split,
                Resolve(root, manifestPath),
                manifestSha,
                root);
            if (actual.NormalizedCandidateJson != Encoding.UTF8.GetString(baselineBytes) ||
                actual.MaximumSideLength != SelectedMaximumSideLength ||
                actual.ProtocolPath != ProtocolPath ||
                actual.ProtocolSha256 != ProtocolSha256)
            {
                throw new InvalidDataException(
                    "High-resolution experiment did not return its exact reviewed selection.");
            }
        }

        using JsonDocument baselineDocument = JsonDocument.Parse(baselineBytes);
        string baselineRaw = baselineDocument.RootElement.GetRawText();
        var defaults = Read(
            baselineDocument.RootElement,
            "train",
            "unused",
            "unused",
            "unused");
        if (defaults.NormalizedCandidateJson != baselineRaw ||
            defaults.MaximumSideLength is not null ||
            defaults.ProtocolPath is not null ||
            defaults.ProtocolSha256 is not null)
        {
            throw new InvalidDataException(
                "High-resolution experiment changed a candidate without its explicit fields.");
        }

        var invalidCandidates = new List<JsonObject>();
        JsonObject wrongMaximum = candidate.DeepClone().AsObject();
        wrongMaximum["ocr_detector_maximum_side_length"] = 960;
        invalidCandidates.Add(wrongMaximum);
        JsonObject stringMaximum = candidate.DeepClone().AsObject();
        stringMaximum["ocr_detector_maximum_side_length"] = "1920";
        invalidCandidates.Add(stringMaximum);
        JsonObject missingMaximum = candidate.DeepClone().AsObject();
        missingMaximum.Remove("ocr_detector_maximum_side_length");
        invalidCandidates.Add(missingMaximum);
        JsonObject missingProtocol = candidate.DeepClone().AsObject();
        missingProtocol.Remove("detector_resolution_protocol");
        invalidCandidates.Add(missingProtocol);
        JsonObject wrongProtocol = candidate.DeepClone().AsObject();
        wrongProtocol["detector_resolution_protocol"]!["sha256"] = "0" + ProtocolSha256[1..];
        invalidCandidates.Add(wrongProtocol);
        JsonObject extraProtocolField = candidate.DeepClone().AsObject();
        extraProtocolField["detector_resolution_protocol"]!["extra"] = true;
        invalidCandidates.Add(extraProtocolField);
        JsonObject changedDetector = candidate.DeepClone().AsObject();
        changedDetector["detector"] = "changed";
        invalidCandidates.Add(changedDetector);
        JsonObject approved = candidate.DeepClone().AsObject();
        approved["production_approved"] = true;
        invalidCandidates.Add(approved);
        JsonObject extraCandidateField = candidate.DeepClone().AsObject();
        extraCandidateField["unreviewed"] = true;
        invalidCandidates.Add(extraCandidateField);

        int rejected = 0;
        foreach (JsonObject invalid in invalidCandidates)
        {
            if (Rejects(() => Read(
                    JsonSerializer.SerializeToElement(invalid),
                    "train",
                    Resolve(root, TrainManifestPath),
                    TrainManifestSha256,
                    root)))
            {
                rejected++;
            }
        }
        if (Rejects(() => Read(
                selected,
                "train",
                Resolve(root, DevManifestPath),
                TrainManifestSha256,
                root)))
        {
            rejected++;
        }
        if (Rejects(() => Read(
                selected,
                "train",
                Resolve(root, TrainManifestPath),
                DevManifestSha256,
                root)))
        {
            rejected++;
        }
        if (Rejects(() => Read(
                selected,
                "sealed",
                Resolve(root, TrainManifestPath),
                TrainManifestSha256,
                root)))
        {
            rejected++;
        }
        if (rejected != invalidCandidates.Count + 3)
        {
            throw new InvalidDataException(
                "High-resolution experiment mutation safeguards failed.");
        }

        Console.WriteLine(
            "High-resolution safeguards passed: exact train/dev selection accepted; " +
            $"{rejected} malformed or altered candidates rejected; default preserved; " +
            "model runs 0; private reads 0.");
    }

    private static bool Rejects(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }
}
