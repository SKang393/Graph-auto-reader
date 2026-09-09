// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class InitialContourOutputExperiment
{
    internal const string ProtocolPath =
        "ml/ocr/official_bakeoff/initial_contour_output_dev_protocol.json";
    internal const string ProtocolSha256 =
        "8e2305ac8632e0bbf675bd36cc25129ba12285cd772cded326560624915abdfb";

    internal static (GraphStructureModelInput Input, string? InputProtocolSha256,
        string? GeometryProtocolSha256) Read(
        JsonElement config, string split, string inputManifestPath,
        string inputManifestSha256, string repositoryRoot)
    {
        bool selected = config.TryGetProperty("ocr_output_geometry", out JsonElement option) &&
            option.ValueKind == JsonValueKind.String && option.GetString() == "initial_db_contour";
        if (!selected)
        {
            if (config.TryGetProperty("initial_contour_protocol", out _))
            {
                throw new InvalidDataException("Initial-contour protocol requires its explicit geometry.");
            }
            (GraphStructureModelInput input, string? protocol) = OriginalModelInputExperiment.Read(
                config, split, inputManifestPath, inputManifestSha256, repositoryRoot);
            return (input, protocol, null);
        }

        JsonElement binding = config.GetProperty("initial_contour_protocol");
        string[] keys = binding.EnumerateObject().Select(static item => item.Name).ToArray();
        if (keys.Length != 2 || !keys.Contains("path", StringComparer.Ordinal) ||
            !keys.Contains("sha256", StringComparer.Ordinal) ||
            binding.GetProperty("path").GetString() != ProtocolPath ||
            binding.GetProperty("sha256").GetString() != ProtocolSha256)
        {
            throw new InvalidDataException("Initial-contour output requires its exact preregistered protocol.");
        }
        using JsonDocument protocolDocument = JsonDocument.Parse(ReadVerified(
            Resolve(repositoryRoot, ProtocolPath), ProtocolSha256));
        JsonElement declaration = protocolDocument.RootElement;
        JsonElement budget = declaration.GetProperty("budget");
        if (declaration.GetProperty("evidence_policy").GetString() != "ml/policy/evidence-policy.json" ||
            budget.GetProperty("optimizer_steps").GetInt32() != 0 ||
            budget.GetProperty("private_reads").GetInt32() != 0 ||
            budget.GetProperty("sealed_runs").GetInt32() != 0 ||
            budget.GetProperty("production_approval").GetBoolean())
        {
            throw new InvalidDataException("Initial-contour output is an unapproved synthetic experiment only.");
        }
        JsonElement splits = declaration.GetProperty("split_identities");
        string selectedSplit = split switch
        {
            "train" => "train",
            "validation" => "dev",
            _ => throw new InvalidDataException("Initial-contour output requires a declared train/dev split."),
        };
        foreach (string name in new[] { "train", "dev" })
        {
            string path = Resolve(repositoryRoot, splits.GetProperty(name + "_manifest_path").GetString()!);
            string expected = splits.GetProperty(name + "_manifest_sha256").GetString()!;
            _ = ReadVerified(path, expected);
            if (name == selectedSplit &&
                (!string.Equals(Path.GetFullPath(inputManifestPath), path, StringComparison.OrdinalIgnoreCase) ||
                 inputManifestSha256 != expected))
            {
                throw new InvalidDataException("Initial-contour output requires the exact complete input manifest.");
            }
        }
        byte[] baselineBytes = ReadVerified(
            Resolve(repositoryRoot, splits.GetProperty("baseline_candidate_path").GetString()!),
            splits.GetProperty("baseline_candidate_sha256").GetString()!);
        ValidateOnlyGeometryChanged(config, JsonNode.Parse(baselineBytes)!);
        using JsonDocument baseline = JsonDocument.Parse(baselineBytes);
        (GraphStructureModelInput originalInput, string? originalProtocol) =
            OriginalModelInputExperiment.Read(
                baseline.RootElement, split, inputManifestPath, inputManifestSha256, repositoryRoot);
        if (originalInput != GraphStructureModelInput.Original ||
            originalProtocol != OriginalModelInputExperiment.ProtocolSha256 ||
            splits.GetProperty("original_input_protocol_path").GetString() != OriginalModelInputExperiment.ProtocolPath ||
            splits.GetProperty("original_input_protocol_sha256").GetString() != originalProtocol)
        {
            throw new InvalidDataException("Initial-contour output must preserve the frozen original-input baseline.");
        }
        return (originalInput, originalProtocol, ProtocolSha256);
    }

    internal static void ValidateOnlyGeometryChanged(JsonElement candidate, JsonNode baseline)
    {
        JsonObject normalized = JsonNode.Parse(candidate.GetRawText())!.AsObject();
        if (candidate.GetProperty("production_approved").GetBoolean() ||
            candidate.GetProperty("ocr_output_geometry").GetString() != "initial_db_contour" ||
            !normalized.Remove("ocr_output_geometry") || !normalized.Remove("initial_contour_protocol") ||
            !JsonNode.DeepEquals(normalized, baseline))
        {
            throw new InvalidDataException("Only initial post-pair contour geometry and its protocol may differ from the frozen original-input candidate.");
        }
    }

    private static string Resolve(string root, string relative)
    {
        root = Path.GetFullPath(root);
        if (Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Any(static part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("Experiment evidence requires canonical repository-relative paths.");
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
            throw new InvalidDataException("Initial-contour experiment evidence checksum mismatch.");
        }
        return bytes;
    }

    internal static void SelfTest()
    {
        JsonObject baseline = new()
        {
            ["production_approved"] = false,
            ["ocr_model_input"] = "original",
            ["model_input_protocol"] = new JsonObject { ["sha256"] = "original-protocol" },
            ["detector"] = "fixed-detector",
            ["recognizer"] = "fixed-recognizer",
        };
        JsonObject candidate = baseline.DeepClone().AsObject();
        candidate["ocr_output_geometry"] = "initial_db_contour";
        candidate["initial_contour_protocol"] = new JsonObject { ["path"] = ProtocolPath, ["sha256"] = ProtocolSha256 };
        ValidateOnlyGeometryChanged(JsonSerializer.SerializeToElement(candidate), baseline);
        int rejected = 0;
        foreach (string key in new[] { "detector", "recognizer", "production_approved", "ocr_model_input", "model_input_protocol", "ocr_output_geometry" })
        {
            JsonObject changed = candidate.DeepClone().AsObject();
            changed[key] = key == "production_approved" ? JsonValue.Create(true) : JsonValue.Create("changed");
            try { ValidateOnlyGeometryChanged(JsonSerializer.SerializeToElement(changed), baseline); }
            catch (InvalidDataException) { rejected++; }
        }
        if (rejected != 6)
        {
            throw new InvalidDataException("Initial-contour candidate isolation safeguards failed.");
        }
        string root = Path.GetFullPath(".");
        foreach (string path in new[] { "../foreign.json", "a/../foreign.json", Path.Combine(root, "absolute.json") })
        {
            try { _ = Resolve(root, path); }
            catch (InvalidDataException) { rejected++; }
        }
        if (rejected != 9)
        {
            throw new InvalidDataException("Initial-contour path safeguards failed.");
        }
        JsonObject ordinary = new() { ["production_approved"] = false };
        var defaults = Read(JsonSerializer.SerializeToElement(ordinary), "train", "unused", "unused", "unused");
        if (defaults.Input != GraphStructureModelInput.AxisMasked || defaults.InputProtocolSha256 is not null ||
            defaults.GeometryProtocolSha256 is not null)
        {
            throw new InvalidDataException("Initial-contour experiment changed the default path.");
        }
        Console.WriteLine("Initial-contour safeguards passed: six altered candidates and three unsafe paths rejected; default preserved; model runs 0; private reads 0.");
    }
}
