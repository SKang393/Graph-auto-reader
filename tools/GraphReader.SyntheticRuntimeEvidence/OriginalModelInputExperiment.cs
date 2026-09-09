// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class OriginalModelInputExperiment
{
    internal const string ProtocolPath =
        "ml/ocr/official_bakeoff/original_model_input_dev_protocol.json";
    internal const string ProtocolSha256 =
        "ecba1f8ca3c1625c0b47deb6847a7fe9ea9d90e595b7ae8fbd582b39d9c10706";

    internal static (GraphStructureModelInput Input, string? ProtocolSha256) Read(
        JsonElement config,
        string split,
        string inputManifestPath,
        string inputManifestSha256,
        string repositoryRoot)
    {
        if (!config.TryGetProperty("ocr_model_input", out JsonElement option))
        {
            if (config.TryGetProperty("model_input_protocol", out _))
            {
                throw new InvalidDataException("An input protocol requires its explicit model-input experiment.");
            }
            return (GraphStructureModelInput.AxisMasked, null);
        }
        if (option.GetString() != "original")
        {
            throw new InvalidDataException("Only the preregistered original model-input experiment is supported.");
        }
        JsonElement binding = config.GetProperty("model_input_protocol");
        string[] keys = binding.EnumerateObject().Select(static property => property.Name).ToArray();
        if (keys.Length != 2 || !keys.Contains("path", StringComparer.Ordinal) ||
            !keys.Contains("sha256", StringComparer.Ordinal) ||
            binding.GetProperty("sha256").GetString() != ProtocolSha256 ||
            binding.GetProperty("path").GetString() != ProtocolPath)
        {
            throw new InvalidDataException("The model-input experiment requires its exact reviewed protocol.");
        }
        byte[] protocolBytes = ReadVerified(ResolveDeclaredPath(repositoryRoot, ProtocolPath), ProtocolSha256);
        using JsonDocument protocol = JsonDocument.Parse(protocolBytes);
        JsonElement declaration = protocol.RootElement;
        if (declaration.GetProperty("evidence_policy").GetString() != "ml/policy/evidence-policy.json" ||
            declaration.GetProperty("budget").GetProperty("sealed_runs").GetInt32() != 0)
        {
            throw new InvalidDataException("Model-input experimentation is synthetic train/dev only.");
        }
        JsonElement splits = declaration.GetProperty("split_identities");
        string splitKey = split switch
        {
            "train" => "train_manifest",
            "validation" => "dev_manifest",
            _ => throw new InvalidDataException("Unsupported model-input experiment split."),
        };
        foreach (string key in new[] { "train_manifest", "dev_manifest" })
        {
            JsonElement manifest = splits.GetProperty(key);
            _ = ReadVerified(
                ResolveDeclaredPath(repositoryRoot, manifest.GetProperty("path").GetString()!),
                manifest.GetProperty("sha256").GetString()!);
        }
        RequireSelectedManifestPath(
            repositoryRoot, inputManifestPath,
            splits.GetProperty(splitKey).GetProperty("path").GetString()!);
        if (splits.GetProperty(splitKey).GetProperty("sha256").GetString() != inputManifestSha256)
        {
            throw new InvalidDataException("The model-input experiment does not bind this complete input manifest.");
        }
        JsonElement baselineBinding = splits.GetProperty("baseline_candidate");
        byte[] baseline = ReadVerified(
            ResolveDeclaredPath(repositoryRoot, baselineBinding.GetProperty("path").GetString()!),
            baselineBinding.GetProperty("sha256").GetString()!);
        ValidateOnlyInputChanged(config, JsonNode.Parse(baseline)!);
        return (GraphStructureModelInput.Original, ProtocolSha256);
    }

    internal static void ValidateOnlyInputChanged(JsonElement candidate, JsonNode baseline)
    {
        JsonObject normalized = JsonNode.Parse(candidate.GetRawText())!.AsObject();
        if (candidate.GetProperty("production_approved").GetBoolean() ||
            candidate.GetProperty("ocr_model_input").GetString() != "original" ||
            !normalized.Remove("ocr_model_input") || !normalized.Remove("model_input_protocol") ||
            !JsonNode.DeepEquals(normalized, baseline))
        {
            throw new InvalidDataException("Only the original model input and its protocol binding may differ from the frozen baseline.");
        }
    }

    private static string ResolveDeclaredPath(string repositoryRoot, string relative)
    {
        string root = Path.GetFullPath(repositoryRoot);
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) ||
            !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Experiment declarations must stay inside their repository root.");
        }
        return path;
    }

    private static void RequireSelectedManifestPath(string repositoryRoot, string supplied, string declared)
    {
        if (!string.Equals(Path.GetFullPath(supplied), ResolveDeclaredPath(repositoryRoot, declared), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The model-input experiment requires the exact declared manifest path.");
        }
    }

    private static byte[] ReadVerified(string path, string expectedSha256)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Model-input experiment evidence checksum mismatch.");
        }
        return bytes;
    }

    internal static void SelfTest()
    {
        JsonObject baseline = new()
        {
            ["production_approved"] = false,
            ["detector"] = new JsonObject { ["sha256"] = "fixed-detector" },
            ["recognizer"] = new JsonObject { ["sha256"] = "fixed-recognizer" },
        };
        JsonObject candidate = baseline.DeepClone().AsObject();
        candidate["ocr_model_input"] = "original";
        candidate["model_input_protocol"] = new JsonObject { ["path"] = "fixture", ["sha256"] = ProtocolSha256 };
        ValidateOnlyInputChanged(JsonSerializer.SerializeToElement(candidate), baseline);
        int rejected = 0;
        foreach (string key in new[] { "detector", "recognizer", "production_approved", "ocr_output_geometry", "ocr_model_input" })
        {
            JsonObject changed = candidate.DeepClone().AsObject();
            changed[key] = key == "production_approved" ? JsonValue.Create(true) : JsonValue.Create("changed");
            try
            {
                ValidateOnlyInputChanged(JsonSerializer.SerializeToElement(changed), baseline);
            }
            catch (InvalidDataException)
            {
                rejected++;
            }
        }
        if (rejected != 5)
        {
            throw new InvalidDataException("Model-input experiment mutation checks failed.");
        }
        (GraphStructureModelInput input, string? protocol) = Read(
            JsonSerializer.SerializeToElement(baseline), "train", "unused", "unused", "unused");
        if (input != GraphStructureModelInput.AxisMasked || protocol is not null)
        {
            throw new InvalidDataException("Default model-input behavior changed.");
        }
        int pathRejections = 0;
        string root = Path.GetFullPath(".");
        try
        {
            RequireSelectedManifestPath(root, Path.Combine(root, "copied.json"), "declared.json");
        }
        catch (InvalidDataException)
        {
            pathRejections++;
        }
        foreach (string relative in new[] { "../foreign.json", Path.Combine(root, "absolute.json") })
        {
            try
            {
                _ = ResolveDeclaredPath(root, relative);
            }
            catch (InvalidDataException)
            {
                pathRejections++;
            }
        }
        if (pathRejections != 3)
        {
            throw new InvalidDataException("Model-input experiment path checks failed.");
        }
        Console.WriteLine("Original-model-input safeguards passed: 5 altered candidates and 3 foreign paths rejected; default preserved; model runs 0; private reads 0.");
    }
}
