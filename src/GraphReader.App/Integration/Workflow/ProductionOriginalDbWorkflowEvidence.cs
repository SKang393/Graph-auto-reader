// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;

namespace GraphReader.App.Integration.Workflow;

/// <summary>
/// Confirms that the adapters selected by normal Production composition match
/// the complete workflow used by the real aggregate evaluation. The current
/// model artifact provider cannot satisfy the evaluated raster-algorithm slot.
/// </summary>
internal static class ProductionOriginalDbWorkflowEvidence
{
    internal static void Validate(
        byte[] candidateBytes,
        IProductionAxisGeometryAdapter axis,
        IProductionMarkerCenterAdapter markerCenter,
        IProductionMarkerClassificationAdapter markerClassifier,
        IProductionArtifactMaskAdapter? artifactMask,
        IProductionLegendReasoningAdapter legend,
        IProductionPhaseReasoningAdapter phase)
    {
        ArgumentNullException.ThrowIfNull(candidateBytes);
        ArgumentNullException.ThrowIfNull(axis);
        ArgumentNullException.ThrowIfNull(markerCenter);
        ArgumentNullException.ThrowIfNull(markerClassifier);
        ArgumentNullException.ThrowIfNull(legend);
        ArgumentNullException.ThrowIfNull(phase);
        if (!axis.IsApproved || !markerCenter.IsApproved || !markerClassifier.IsApproved ||
            !legend.IsApproved || !phase.IsApproved)
        {
            throw new InvalidDataException("The evaluated workflow requires approved Production adapters.");
        }

        using JsonDocument document = JsonDocument.Parse(candidateBytes,
            new JsonDocumentOptions { MaxDepth = 64 });
        JsonElement root = document.RootElement;
        JsonElement algorithms = Object(root, "algorithms");
        Require(algorithms, "axis_stage_version", ProductionAxisGeometryAdapter.StageVersion);
        string openCvSha256 = NativeShaForRole(root, "opencvsharp_extern");
        if (axis.AdapterId != $"graphreader-axis-opencv:{openCvSha256[..12].ToLowerInvariant()}")
        {
            throw new InvalidDataException("The evaluated axis runtime differs from Production.");
        }

        Require(algorithms, "marker_center_revision", markerCenter.Model.ModelId);
        Require(algorithms, "marker_center_candidate_id", markerCenter.Model.Version);
        Require(algorithms, "marker_classifier_adapter_id", markerClassifier.AdapterId);
        Require(algorithms, "legend_adapter_id", legend.AdapterId);
        Require(algorithms, "phase_adapter_id", phase.AdapterId);

        JsonElement marker = Object(root, "marker_center");
        Require(marker, "model_id", markerCenter.Model.ModelId);
        Require(marker, "version", markerCenter.Model.Version);
        RequireSha(Object(marker, "payload"), "sha256", markerCenter.Model.Sha256);
        JsonElement classifier = Object(root, "marker_classifier");
        Require(classifier, "model_id", markerClassifier.Model.ModelId);
        Require(classifier, "version", markerClassifier.Model.Version);
        RequireSha(classifier, "model_sha256", markerClassifier.Model.Sha256);

        string expectedDomain = markerCenter.AdapterId.EndsWith(":plot-domain-v25", StringComparison.Ordinal)
            ? "axis_polygon_or_16px_v25"
            : "full_frame_v24";
        string expectedMarkerAdapterId = string.Concat(
            $"graphreader-marker-center-proposal:{markerCenter.Model.Sha256[..12].ToLowerInvariant()}",
            expectedDomain == "axis_polygon_or_16px_v25" ? ":plot-domain-v25" : string.Empty);
        if (markerCenter.AdapterId != expectedMarkerAdapterId)
        {
            throw new InvalidDataException("The evaluated marker-center algorithm differs from Production.");
        }

        string domain = algorithms.TryGetProperty("marker_proposal_domain", out JsonElement domainValue)
            ? Text(domainValue, "marker_proposal_domain")
            : "full_frame_v24";
        if (domain != expectedDomain)
        {
            throw new InvalidDataException("The evaluated marker proposal domain differs from Production.");
        }

        if (artifactMask is not RasterResidualArtifactMaskAdapter raster ||
            !raster.IsApproved ||
            !Matches(algorithms, "artifact_algorithm_id", raster.Identity.AlgorithmId) ||
            !Matches(algorithms, "artifact_algorithm_version", raster.Identity.Version) ||
            !Sha(algorithms, "artifact_configuration_sha256").Equals(
                raster.Identity.ConfigurationSha256, StringComparison.OrdinalIgnoreCase) ||
            !Sha(algorithms, "artifact_app_assembly_sha256").Equals(
                raster.Identity.AssemblySha256, StringComparison.OrdinalIgnoreCase) ||
            !Sha(algorithms, "artifact_ocr_assembly_sha256").Equals(
                raster.OcrAssemblySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The evaluated raster artifact algorithm is unavailable as an approved Production adapter.");
        }
    }

    private static bool Matches(JsonElement parent, string name, string expected) =>
        parent.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() == expected;

    private static string NativeShaForRole(JsonElement root, string expectedRole)
    {
        if (!root.TryGetProperty("native_files", out JsonElement files) ||
            files.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Workflow candidate field 'native_files' must be an array.");
        }

        string? result = null;
        foreach (JsonElement file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object ||
                !file.TryGetProperty("role", out JsonElement role) ||
                role.ValueKind != JsonValueKind.String ||
                role.GetString() != expectedRole)
            {
                continue;
            }

            if (result is not null)
                throw new InvalidDataException($"Workflow candidate native role '{expectedRole}' must be unique.");
            result = Sha(file, "sha256");
        }

        return result ?? throw new InvalidDataException(
            $"Workflow candidate native role '{expectedRole}' is missing.");
    }

    private static JsonElement Object(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Workflow candidate field '{name}' must be an object.");
        return value;
    }

    private static void Require(JsonElement parent, string name, string expected)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            Text(value, name) != expected)
            throw new InvalidDataException($"Evaluated workflow identity '{name}' differs from Production.");
    }

    private static void RequireSha(JsonElement parent, string name, string expected)
    {
        if (!Sha(parent, name).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Evaluated workflow identity '{name}' differs from Production.");
    }

    private static string Sha(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
            throw new InvalidDataException($"Workflow candidate field '{name}' is missing.");
        string result = Text(value, name);
        if (result.Length != 64 || result.Any(static character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"Workflow candidate field '{name}' must be SHA-256.");
        return result;
    }

    private static string Text(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Workflow candidate field '{name}' must be nonempty text.");
        return value.GetString()!;
    }
}
