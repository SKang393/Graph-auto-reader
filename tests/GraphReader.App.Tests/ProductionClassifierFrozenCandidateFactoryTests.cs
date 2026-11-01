// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionClassifierFrozenCandidateFactoryTests
{
    private static readonly string[] OutputOrder =
        ["shape_probabilities[9]", "fill_probabilities[3]", "artifact_probability[1]", "l2_normalized_embedding[12]"];
    private static readonly string[] ShapeOrder =
        ["circle", "square", "triangle_up", "triangle_down", "diamond", "star", "asterisk", "cross", "other"];
    private static readonly string[] FillOrder = ["filled", "open", "unknown"];

    [TestMethod]
    public async Task CandidateRequiresExplicitEntryAndPreservesOriginalPixelContentBounds()
    {
        using var directory = new TemporaryDirectory();
        var service = new RecordingService();
        ProductionMarkerClassificationAdapter adapter = ProductionMarkerClassificationAdapter.CreateForFrozenCandidateEvaluation(
            WriteDescriptor(directory.Path), service);
        Assert.IsFalse(adapter.IsApproved);
        ProductionWorkflowDetectionRequest request = Request();
        var frame = new MarkerImageFrame(32, 32, 1, new float[1024], MarkerSourceImage.Original,
            MarkerAffineTransform.Identity, MarkerMask.Empty(32, 32), MarkerMask.Empty(32, 32));
        await Assert.ThrowsExactlyAsync<ProductionWorkflowStageException>(() =>
            adapter.ClassifyAsync(request, frame, [], CancellationToken.None));
        Assert.AreEqual(0, service.Calls);
        var bounds = new Dictionary<string, MarkerRectangle> { ["legend"] = new(1, 2, 3, 4) };
        ProductionMarkerClassificationEvidence result = await ((IProductionCandidateMarkerClassificationAdapter)adapter)
            .ClassifyForCandidateEvaluationAsync(request, frame, [], bounds, CancellationToken.None);
        Assert.AreEqual(1, service.Calls);
        Assert.AreEqual(bounds["legend"], service.LastRequest!.Options.OriginalPixelContentBounds!["legend"]);
        Assert.AreEqual(2.25, service.LastRequest.Options.PatchRadiusScale);
        Assert.AreEqual("original_pixels", result.Envelope.CoordinateSpace);
        Assert.IsFalse(adapter.IsApproved);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CandidateRejectsClassificationBoundToChangedMarker(bool missingMarkerId)
    {
        using var directory = new TemporaryDirectory();
        var service = new RecordingService(marker => missingMarkerId
            ? marker with { MarkerId = null! }
            : marker with { Center = new MarkerPoint(marker.Center.X + 1, marker.Center.Y) });
        ProductionMarkerClassificationAdapter adapter = ProductionMarkerClassificationAdapter.CreateForFrozenCandidateEvaluation(
            WriteDescriptor(directory.Path), service);
        ProductionWorkflowDetectionRequest request = Request();
        var frame = new MarkerImageFrame(32, 32, 1, new float[1024], MarkerSourceImage.Original,
            MarkerAffineTransform.Identity, MarkerMask.Empty(32, 32), MarkerMask.Empty(32, 32));
        MarkerCenter marker = new(
            "marker-1",
            new MarkerPoint(8, 12),
            3,
            0,
            0.9,
            MarkerSourceImage.Original);

        ProductionWorkflowStageException exception = await Assert.ThrowsExactlyAsync<ProductionWorkflowStageException>(() =>
            ((IProductionCandidateMarkerClassificationAdapter)adapter)
                .ClassifyForCandidateEvaluationAsync(
                    request,
                    frame,
                    [marker],
                    new Dictionary<string, MarkerRectangle>(),
                    CancellationToken.None));
        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionEvidenceRejected, exception.Failure.Code);
    }

    [TestMethod]
    public void CandidateRejectsChangedPayloadAndApprovalClaim()
    {
        using var directory = new TemporaryDirectory();
        FrozenCandidateMarkerClassifierDescriptor descriptor = WriteDescriptor(directory.Path);
        File.AppendAllText(descriptor.Identity.FilePath, "changed");
        Assert.ThrowsExactly<InvalidDataException>(() => ProductionMarkerClassificationAdapter.CreateForFrozenCandidateEvaluation(
            descriptor, new RecordingService()));
        descriptor = WriteDescriptor(directory.Path, productionApproved: true);
        Assert.ThrowsExactly<InvalidDataException>(() => ProductionMarkerClassificationAdapter.CreateForFrozenCandidateEvaluation(
            descriptor, new RecordingService()));
    }

    [TestMethod]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    [DataRow(false, false, false)]
    public void CandidateApprovalSpellingsCannotClaimProduction(bool approved, bool approval, bool rejected)
    {
        using var directory = new TemporaryDirectory();
        FrozenCandidateMarkerClassifierDescriptor descriptor = WriteDescriptor(directory.Path,
            productionApproved: approved, productionApproval: approval);
        if (rejected)
            Assert.ThrowsExactly<InvalidDataException>(() => ProductionMarkerClassificationAdapter.CreateForFrozenCandidateEvaluation(
                descriptor, new RecordingService()));
        else
            Assert.IsFalse(ProductionMarkerClassificationAdapter.CreateForFrozenCandidateEvaluation(
                descriptor, new RecordingService()).IsApproved);
    }

    [TestMethod]
    public void CandidateApprovalClaimsMayBeAbsent()
    {
        using var directory = new TemporaryDirectory();
        FrozenCandidateMarkerClassifierDescriptor descriptor = WriteDescriptor(directory.Path,
            productionApproved: null, productionApproval: null);
        Assert.IsFalse(ProductionMarkerClassificationAdapter.CreateForFrozenCandidateEvaluation(
            descriptor, new RecordingService()).IsApproved);
    }

    [TestMethod]
    public void CandidateApprovalClaimsRejectMalformedValuesAndMixedClaims()
    {
        using var directory = new TemporaryDirectory();
        foreach (string name in new[] { "production_approval", "production_approved" })
        foreach (string value in new[] { "\"false\"", "0", "null", "{}", "[]", "true" })
        {
            FrozenCandidateMarkerClassifierDescriptor descriptor = WithBenchmarks(directory.Path, $"[{{\"{name}\":{value}}}]");
            Assert.ThrowsExactly<InvalidDataException>(() => ProductionMarkerClassificationAdapter.CreateForFrozenCandidateEvaluation(
                descriptor, new RecordingService()), $"{name}: {value}");
        }
        foreach (string json in new[]
                 {
                     "[{\"production_approval\":false,\"production_approved\":true}]",
                     "[{\"production_approval\":true,\"production_approved\":false}]",
                     "[{\"production_approval\":true,\"production_approved\":true}]",
                     "[{\"production_approval\":false,\"production_approved\":null}]",
                     "[{\"production_approval\":[],\"production_approved\":false}]",
                     "[{\"production_approval\":{},\"production_approved\":\"false\"}]",
                     "[{\"production_approval\":true,\"production_approval\":false}]",
                     "[{\"production_approved\":false,\"production_approved\":false}]",
                 })
            Assert.ThrowsExactly<InvalidDataException>(() => ProductionMarkerClassificationAdapter.CreateForFrozenCandidateEvaluation(
                WithBenchmarks(directory.Path, json), new RecordingService()), json);
        Assert.IsFalse(ProductionMarkerClassificationAdapter.CreateForFrozenCandidateEvaluation(
            WithBenchmarks(directory.Path, "[{\"production_approval\":false,\"production_approved\":false}]"),
            new RecordingService()).IsApproved);
        Assert.ThrowsExactly<InvalidDataException>(() => ProductionMarkerClassificationAdapter.CreateForFrozenCandidateEvaluation(
            WithBenchmarks(directory.Path, "[{\"production_approval\":true}]", duplicateBenchmarks: true),
            new RecordingService()));
    }

    private static FrozenCandidateMarkerClassifierDescriptor WithBenchmarks(string directory, string benchmarks, bool duplicateBenchmarks = false)
    {
        FrozenCandidateMarkerClassifierDescriptor descriptor = WriteDescriptor(directory);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(descriptor.ManifestPath));
        using JsonDocument replacement = JsonDocument.Parse(benchmarks);
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in manifest.RootElement.EnumerateObject())
            {
                if (property.NameEquals("benchmarks"))
                {
                    writer.WritePropertyName("benchmarks");
                    replacement.RootElement.WriteTo(writer);
                    if (duplicateBenchmarks)
                    {
                        writer.WritePropertyName("benchmarks");
                        writer.WriteStartArray();
                        writer.WriteEndArray();
                    }
                }
                else property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        File.WriteAllBytes(descriptor.ManifestPath, bytes.ToArray());
        return descriptor with { ManifestSha256 = Hash(descriptor.ManifestPath) };
    }

    [TestMethod]
    public void CandidateRejectsWrongShapeAndWrongIdentity()
    {
        using var directory = new TemporaryDirectory();
        FrozenCandidateMarkerClassifierDescriptor descriptor = WriteDescriptor(directory.Path, inputWidth: 33);
        Assert.ThrowsExactly<InvalidDataException>(() => ProductionMarkerClassificationAdapter.CreateForFrozenCandidateEvaluation(
            descriptor, new RecordingService()));
        descriptor = WriteDescriptor(directory.Path) with { Identity = new ModelIdentity("different", "0.0.1",
            descriptor.Identity.Sha256, descriptor.Identity.FilePath) };
        Assert.ThrowsExactly<InvalidDataException>(() => ProductionMarkerClassificationAdapter.CreateForFrozenCandidateEvaluation(
            descriptor, new RecordingService()));
    }

    private static FrozenCandidateMarkerClassifierDescriptor WriteDescriptor(string directory,
        bool? productionApproved = false, int inputWidth = 32, bool? productionApproval = null)
    {
        string payload = Path.Combine(directory, "candidate.onnx");
        File.WriteAllText(payload, "owned fake classifier payload");
        string payloadSha = Hash(payload);
        string manifest = Path.Combine(directory, "manifest.json");
        var benchmark = new Dictionary<string, bool>();
        if (productionApproved.HasValue) benchmark["production_approved"] = productionApproved.Value;
        if (productionApproval.HasValue) benchmark["production_approval"] = productionApproval.Value;
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            task = "marker_classifier", model_id = "candidate", model_version = "0.0.1", sha256 = payloadSha,
            inputs = new[] { new { name = "marker_patch", shape = new object[] { "N", 1, 32, inputWidth } } },
            outputs = new[] { new { name = "classification_probabilities", shape = new object[] { "N", 25 },
                order = OutputOrder } },
            preprocessing = new { normalization_mean = 0, normalization_scale = 1 },
            postprocessing = new { shape_and_fill_separate = true,
                shape_order = ShapeOrder, fill_order = FillOrder },
            benchmarks = new[] { benchmark },
        }));
        return new(new ModelIdentity("candidate", "0.0.1", payloadSha, payload), manifest, Hash(manifest));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static ProductionWorkflowDetectionRequest Request()
    {
        byte[] bytes = [1, 2, 3];
        var image = new WorkflowImageEvidence("candidate", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            32, 32, WorkflowImageVariant.Original);
        var panel = new WorkflowImportedPanel(Guid.NewGuid(), Guid.NewGuid(), "candidate", image);
        return new(new WorkflowPreparedPanel(panel, image, null), image, WorkflowImageVariant.Original,
            Guid.NewGuid(), Guid.NewGuid(), bytes);
    }

    private sealed class RecordingService : IMarkerClassificationService
    {
        private readonly Func<MarkerCenter, MarkerCenter>? mapMarker;

        internal RecordingService(Func<MarkerCenter, MarkerCenter>? mapMarker = null) => this.mapMarker = mapMarker;

        internal int Calls { get; private set; }
        internal MarkerClassificationRequest? LastRequest { get; private set; }

        public ValueTask<MarkerClassificationResult> ClassifyAsync(MarkerClassificationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastRequest = request;
            ClassifiedMarker[] markers = request.Markers.Select(marker =>
            {
                MarkerSymbolDescriptor symbol = MarkerSymbolMap.Describe(MarkerShape.Circle, MarkerFill.Filled);
                return new ClassifiedMarker(
                    mapMarker?.Invoke(marker) ?? marker,
                    MarkerShape.Circle,
                    MarkerFill.Filled,
                    symbol.Symbol,
                    symbol.AccessibleName,
                    0,
                    0.9,
                    0.9,
                    [1f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            }).ToArray();
            return ValueTask.FromResult(new MarkerClassificationResult(1, "fixture", request.ProjectId, request.PanelId,
                "markers", request.Options.StageVersion, request.InputSha256, "original_pixels", markers,
                new MarkerClassificationTiming(0, 0, 0, 0), 1, [], [],
                new MarkerClassificationModelReport(request.Model.ModelId, request.Model.Version, request.Model.Sha256,
                    InferenceProvider.Cpu), null));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("graphreader-classifier-candidate-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
