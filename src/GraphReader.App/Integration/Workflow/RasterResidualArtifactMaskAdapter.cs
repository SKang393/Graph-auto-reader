// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>
/// Local candidate boundary for the raster algorithm. This adapter cannot be
/// approved by its caller and is not registered in Production composition.
/// </summary>
internal sealed class RasterResidualArtifactMaskAdapter : IProductionArtifactMaskAdapter
{
    private readonly RasterResidualArtifactMaskProvider provider = new();

    public RasterResidualArtifactMaskAdapter()
    {
        string appAssemblySha = Hash(File.ReadAllBytes(typeof(RasterResidualArtifactMaskProvider).Assembly.Location));
        OcrAssemblySha256 = Hash(File.ReadAllBytes(typeof(RasterPreOcrStructuralProbabilityProvider).Assembly.Location));
        ConfigurationJson = JsonSerializer.Serialize(new
        {
            algorithm = "raster-residual-artifacts",
            version = "1",
            parameters = RasterResidualArtifactMaskProvider.ConfigurationFingerprint,
            dependencies = new[] { new { assembly = "GraphReader.Ocr", sha256 = OcrAssemblySha256 } },
        });
        Identity = new ProductionArtifactAlgorithmEvidence(
            "raster-residual-artifacts", "1", appAssemblySha,
            Hash(Encoding.UTF8.GetBytes(ConfigurationJson)));
    }

    public string AdapterId => Identity.StageVersion;
    public bool IsApproved => false;
    public ProductionArtifactAlgorithmEvidence Identity { get; }
    public string ConfigurationJson { get; }
    public string OcrAssemblySha256 { get; }

    public Task<ProductionArtifactMaskEvidence> DetectAsync(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster,
        ProductionDetectionMaskSeed seed,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Raster residual analysis requires the actual axis and OCR context.");

    public async Task<ProductionArtifactMaskEvidence> DetectAsync(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster,
        ProductionDetectionMaskSeed seed,
        ProductionAxisGeometryEvidence axisEvidence,
        IReadOnlyList<ProductionOcrModelEvidence> ocrModelEvidence,
        OcrResult ocrResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ocrModelEvidence);
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        RasterResidualArtifactMaskResult result = await provider.AnalyzeAsync(
            raster, axisEvidence, ocrResult, seed, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        string[] warnings = result.Warnings.Concat(new[]
        {
            Identity.AssemblyWarning,
            Identity.ConfigurationWarning,
            $"artifact_algorithm_ocr_dependency_sha256:{OcrAssemblySha256}",
            "artifact_candidate_confidence_uncalibrated",
        }).Concat(Enum.GetValues<RasterResidualArtifactCategory>().Select(category =>
            $"artifact_candidate_region_count:{category}:{result.Regions.Count(region => region.Category == category)}"))
            .ToArray();
        var envelope = new WorkflowVisionEnvelope(
            1, request.RunId, request.ProjectId, request.Panel.ImportedPanel.PanelId,
            "markers", Identity.StageVersion, raster.InputSha256, model: null,
            new WorkflowVisionTiming(0, null, timer.Elapsed.TotalMilliseconds, timer.Elapsed.TotalMilliseconds),
            confidence: 0, warnings, request.Transforms);
        return new ProductionArtifactMaskEvidence(
            result.Width, result.Height, raster.InputSha256, raster.Variant,
            envelope, result.Mask.ToArray(), warnings, Identity);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
