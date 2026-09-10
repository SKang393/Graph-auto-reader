// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionOriginalDbWorkflowEvidenceTests
{
    private static readonly ModelIdentity Marker = new("marker-v1", "P1", new string('a', 64), "marker.onnx");
    private static readonly ModelIdentity Classifier = new("classifier-v1", "1", new string('b', 64), "classifier.onnx");
    private static readonly string OpenCvSha256 = new('c', 64);

    [TestMethod]
    public void MatchingCurrentCompositionStopsAtUnavailableRasterApproval()
    {
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() => Validate(Candidate()));

        StringAssert.Contains(error.Message, "raster artifact algorithm is unavailable");
    }

    [TestMethod]
    [DataRow("axis_stage_version", "old-axis")]
    [DataRow("marker_center_revision", "other-marker")]
    [DataRow("marker_center_candidate_id", "P2")]
    [DataRow("marker_classifier_adapter_id", "other-classifier")]
    [DataRow("legend_adapter_id", "other-legend")]
    [DataRow("phase_adapter_id", "other-phase")]
    public void ChangedStageIdentityFailsBeforeArtifactAdmission(string field, string value)
    {
        Dictionary<string, object?> candidate = Candidate();
        ((Dictionary<string, object?>)candidate["algorithms"]!)[field] = value;

        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() => Validate(candidate));
        Assert.IsFalse(error.Message.Contains("raster artifact algorithm is unavailable", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ChangedMarkerPayloadAndProposalDomainFailClosed()
    {
        Dictionary<string, object?> candidate = Candidate();
        ((Dictionary<string, object?>)((Dictionary<string, object?>)candidate["marker_center"]!)["payload"]!)["sha256"] = new string('c', 64);
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(candidate));

        candidate = Candidate();
        ((Dictionary<string, object?>)candidate["algorithms"]!)["marker_proposal_domain"] = "axis_polygon_or_16px_v25";
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(candidate));
    }

    [TestMethod]
    public void LegacyMarkerCenterAdapterCannotSatisfyProposalEvidence()
    {
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
            ProductionOriginalDbWorkflowEvidence.Validate(
                JsonSerializer.SerializeToUtf8Bytes(Candidate()),
                new Axis(OpenCvSha256),
                new MarkerCenterAdapter($"graphreader-marker-center:{Marker.Sha256[..12]}"),
                new MarkerClassifier(),
                artifactMask: null,
                new ProductionLegendReasoningAdapter(),
                new ProductionPhaseReasoningAdapter()));

        StringAssert.Contains(error.Message, "marker-center algorithm differs");
    }

    [TestMethod]
    public void ChangedAxisRuntimeAdapterFailsClosed()
    {
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
            ProductionOriginalDbWorkflowEvidence.Validate(
                JsonSerializer.SerializeToUtf8Bytes(Candidate()),
                new Axis(new string('d', 64)),
                new MarkerCenterAdapter(),
                new MarkerClassifier(),
                artifactMask: null,
                new ProductionLegendReasoningAdapter(),
                new ProductionPhaseReasoningAdapter()));

        StringAssert.Contains(error.Message, "axis runtime differs");
    }

    private static void Validate(Dictionary<string, object?> candidate) =>
        ProductionOriginalDbWorkflowEvidence.Validate(
            JsonSerializer.SerializeToUtf8Bytes(candidate),
            new Axis(OpenCvSha256), new MarkerCenterAdapter(), new MarkerClassifier(), artifactMask: null,
            new ProductionLegendReasoningAdapter(), new ProductionPhaseReasoningAdapter());

    private static Dictionary<string, object?> Candidate() => new()
    {
        ["marker_center"] = new Dictionary<string, object?>
        {
            ["model_id"] = Marker.ModelId,
            ["version"] = Marker.Version,
            ["payload"] = new Dictionary<string, object?> { ["sha256"] = Marker.Sha256 },
        },
        ["marker_classifier"] = new Dictionary<string, object?>
        {
            ["model_id"] = Classifier.ModelId,
            ["version"] = Classifier.Version,
            ["model_sha256"] = Classifier.Sha256,
        },
        ["native_files"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["role"] = "onnxruntime",
                ["sha256"] = new string('1', 64),
            },
            new Dictionary<string, object?>
            {
                ["role"] = "opencvsharp_extern",
                ["sha256"] = OpenCvSha256,
            },
        },
        ["algorithms"] = new Dictionary<string, object?>
        {
            ["axis_stage_version"] = ProductionAxisGeometryAdapter.StageVersion,
            ["marker_center_revision"] = Marker.ModelId,
            ["marker_center_candidate_id"] = Marker.Version,
            ["marker_classifier_adapter_id"] = $"graphreader-marker-classifier:{Classifier.Sha256[..12]}",
            ["legend_adapter_id"] = $"graphreader-legends:{GraphReader.Legends.LegendReasoningContract.StageVersion}",
            ["phase_adapter_id"] = $"graphreader-phases:{GraphReader.Phases.PhaseReasoningContract.StageVersion}",
            ["artifact_algorithm_id"] = "raster-residual-artifacts",
            ["artifact_algorithm_version"] = "1",
            ["artifact_configuration_sha256"] = new string('d', 64),
            ["artifact_app_assembly_sha256"] = new string('e', 64),
            ["artifact_ocr_assembly_sha256"] = new string('f', 64),
        },
    };

    private sealed class Axis(string runtimeSha256) : IProductionAxisGeometryAdapter
    {
        public string AdapterId { get; } = $"graphreader-axis-opencv:{runtimeSha256[..12]}";
        public bool IsApproved => true;
        public Task<ProductionAxisGeometryEvidence> DetectAsync(ProductionWorkflowDetectionRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MarkerCenterAdapter(string? adapterId = null) : IProductionMarkerCenterAdapter
    {
        public string AdapterId { get; } = adapterId ?? $"graphreader-marker-center-proposal:{Marker.Sha256[..12]}";
        public bool IsApproved => true;
        public ModelIdentity Model => Marker;
        public Task<ProductionMarkerCenterEvidence> DetectAsync(ProductionWorkflowDetectionRequest request,
            MarkerImageFrame originalImage, MarkerPolygon plotPolygon, MarkerImageFrame? enhancedImage,
            IReadOnlyList<WorkflowTransformProvenance>? enhancedTransforms, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class MarkerClassifier : IProductionMarkerClassificationAdapter
    {
        public string AdapterId => $"graphreader-marker-classifier:{Classifier.Sha256[..12]}";
        public bool IsApproved => true;
        public ModelIdentity Model => Classifier;
        public Task<ProductionMarkerClassificationEvidence> ClassifyAsync(ProductionWorkflowDetectionRequest request,
            MarkerImageFrame image, IReadOnlyList<MarkerCenter> markers, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
