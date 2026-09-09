// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using GraphReader.App.Integration.Workflow;
using GraphReader.Ocr;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record FrozenCandidateRuntimeFileDescription(
    string Path,
    string Sha256,
    long ByteCount);

internal sealed record FrozenCandidateAxisDescription(string StageVersion);

internal sealed record FrozenCandidateOcrDescription(
    string OutputGeometry,
    string CompositionVersion);

internal sealed record FrozenCandidateArtifactDescription(
    string AlgorithmId,
    string AlgorithmVersion,
    string AppAssemblySha256,
    string ConfigurationSha256,
    string ConfigurationJson,
    JsonElement Configuration,
    string OcrAssemblySha256);

internal sealed record FrozenCandidateSemanticDescription(
    string LegendAdapterId,
    string PhaseAdapterId);

internal sealed record FrozenCandidateRuntimeDescription(
    string Schema,
    IReadOnlyList<FrozenCandidateRuntimeFileDescription> ManagedFiles,
    IReadOnlyList<FrozenCandidateRuntimeFileDescription> NativeFiles,
    FrozenCandidateAxisDescription Axis,
    FrozenCandidateOcrDescription Ocr,
    FrozenCandidateArtifactDescription Artifact,
    FrozenCandidateSemanticDescription Semantics,
    bool ModelInference,
    int PrivateReads,
    int SealedReads,
    bool ProductionApproved)
{
    internal static FrozenCandidateRuntimeDescription Create()
    {
        IReadOnlyList<FrozenCandidateRuntimeFileDescription> managed = DescribeFiles(
            FrozenCandidateBinding.ActualManagedExecutablePaths());
        IReadOnlyList<FrozenCandidateRuntimeFileDescription> native = DescribeFiles(
            FrozenCandidateBinding.ActualNativeExecutablePaths());
        var artifact = new RasterResidualArtifactMaskAdapter();
        using JsonDocument configuration = JsonDocument.Parse(artifact.ConfigurationJson);
        var legend = new ProductionLegendReasoningAdapter();
        var phases = new ProductionPhaseReasoningAdapter();

        return new FrozenCandidateRuntimeDescription(
            "graphreader.real-acceptance-frozen-candidate-runtime-description.v1",
            managed,
            native,
            new FrozenCandidateAxisDescription(ProductionAxisGeometryAdapter.StageVersion),
            new FrozenCandidateOcrDescription(
                "model_polygon",
                GraphStructureConsensusTextRegionDetector.GetCompositionVersion(
                    GraphStructureConsensusGeometry.ModelPolygon)),
            new FrozenCandidateArtifactDescription(
                artifact.Identity.AlgorithmId,
                artifact.Identity.Version,
                artifact.Identity.AssemblySha256,
                artifact.Identity.ConfigurationSha256,
                artifact.ConfigurationJson,
                configuration.RootElement.Clone(),
                artifact.OcrAssemblySha256),
            new FrozenCandidateSemanticDescription(legend.AdapterId, phases.AdapterId),
            ModelInference: false,
            PrivateReads: 0,
            SealedReads: 0,
            ProductionApproved: false);
    }

    private static FrozenCandidateRuntimeFileDescription[] DescribeFiles(
        IReadOnlyList<string> paths)
    {
        return paths.Select(static path =>
        {
            string fullPath = System.IO.Path.GetFullPath(path);
            using FileStream stream = File.OpenRead(fullPath);
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            return new FrozenCandidateRuntimeFileDescription(
                fullPath,
                sha256,
                stream.Length);
        }).ToArray();
    }
}
