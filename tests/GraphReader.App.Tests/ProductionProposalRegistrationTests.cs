// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphReader.App.Integration;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionProposalRegistrationTests
{
    [TestMethod]
    [DataRow("mask_preserving_multiradius_v24", "full_frame_v24", 0.25)]
    [DataRow("mask_preserving_multiradius_v24", "axis_polygon_or_16px_v25", 0.25)]
    [DataRow("mask_preserving_multiradius_enclosed_v1", "axis_polygon_or_16px_v25", 0.25)]
    [DataRow("mask_preserving_multiradius_enclosed_balanced_v2", "axis_polygon_or_16px_v25", 0.25)]
    [DataRow("mask_preserving_multiradius_enclosed_balanced_v2", "axis_polygon_or_16px_v25", 0.1)]
    public async Task ApprovedStoreSelectsExactProposalProfile(string algorithm, string domain, double threshold)
    {
        using var store = new TestStore(algorithm, domain, threshold);
        ResolvedProductionModel model = await store.ResolveAsync();
        await using var host = Host(store.Root);
        var availability = Availability(model);
        var result = ApplicationComposition.CreateApprovedMarkerCenterAdapter(availability, host);

        Assert.IsNull(result.Error);
        var adapter = result.Adapter as ProductionProposalMarkerCenterAdapter;
        Assert.IsNotNull(adapter);
        Assert.IsTrue(adapter.IsApproved);
        Assert.AreEqual(model.Identity, adapter.Model);
        Assert.AreEqual(domain, adapter.ProposalDomain);
        Assert.AreEqual(threshold, adapter.OperatingThreshold);
        Assert.IsTrue(ProductionProposalMarkerCenterAdapter.UsesProposalContract(model));

        // Approval of the marker payload does not approve the raster algorithm.
        var artifact = ApplicationComposition.CreateApprovedArtifactMaskAdapter(
            availability, host, adapter, ocr: null, runtimeAvailability: null);
        Assert.IsNull(artifact.Adapter);
        Assert.IsNotNull(artifact.Error);
        StringAssert.Contains(artifact.Error.TechnicalMessage!, "accepted original-image OCR workflow");
    }

    [TestMethod]
    [DataRow("center_threshold", "0.10001")]
    [DataRow("algorithm", "\"unknown\"")]
    [DataRow("ring_support_geometry", "\"one_side\"")]
    [DataRow("maximum_radius_pixels", "9")]
    public async Task UnsupportedManifestCannotInitializeRuntime(string field, string json)
    {
        using var store = new TestStore(change: manifest => manifest["postprocessing"]![field] = JsonNode.Parse(json));
        ResolvedProductionModel model = await store.ResolveAsync();
        await using var host = Host(store.Root);
        var result = ApplicationComposition.CreateApprovedMarkerCenterAdapter(Availability(model), host);
        Assert.IsNull(result.Adapter);
        Assert.IsNotNull(result.Error);
        Assert.IsFalse(host.IsInitialized);
    }

    [TestMethod]
    [DataRow("full_frame_v24")]
    [DataRow("unknown")]
    public async Task EnclosedProfileRejectsWrongDomain(string domain)
    {
        using var store = new TestStore(domain: domain);
        ResolvedProductionModel model = await store.ResolveAsync();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProductionProposalMarkerCenterAdapter.Create(model, new NoRunInference()));
    }

    [TestMethod]
    [DataRow("manifest")]
    [DataRow("payload")]
    public async Task ChangesAfterStoreResolutionFailClosed(string resource)
    {
        using var store = new TestStore();
        ResolvedProductionModel model = await store.ResolveAsync();
        File.AppendAllText(resource == "manifest" ? model.ManifestPath : model.Identity.FilePath, "tampered");
        await using var host = Host(store.Root);
        var result = ApplicationComposition.CreateApprovedMarkerCenterAdapter(Availability(model), host);
        Assert.IsNull(result.Adapter);
        Assert.IsNotNull(result.Error);
        Assert.IsFalse(host.IsInitialized);
    }

    [TestMethod]
    public async Task UnapprovedManifestNeverBecomesAResolvedProductionModel()
    {
        using var store = new TestStore(approved: false);
        ProductionModelValidationException error = await Assert.ThrowsExactlyAsync<ProductionModelValidationException>(
            async () => await store.ResolveAsync());
        Assert.AreEqual("MODEL_NOT_APPROVED", error.Code);
    }

    [TestMethod]
    public async Task MarkerApprovalCannotApproveRasterProvider()
    {
        using var store = new TestStore();
        ResolvedProductionModel model = await store.ResolveAsync();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RasterResidualArtifactMaskAdapter.CreateApproved(model, model, new string('a', 64)));
        Assert.IsFalse(new RasterResidualArtifactMaskAdapter().IsApproved);
    }

    private static ProductionModelAvailabilitySnapshot Availability(ResolvedProductionModel model) =>
        new(["marker_center"], "Isolated resolver test fixture",
            new Dictionary<string, ResolvedProductionModel> { ["marker_center"] = model });

    private static ProductionInferenceRuntimeHost Host(string root) =>
        new(new FakeExecutionProviderDiscovery("CPUExecutionProvider"), new WindowsExecutionProviderPolicy(),
            new FakeInferenceSessionFactory(), CpuThreadConfiguration.Create(1), [InferenceProvider.Cpu],
            Path.Combine(root, "cache"), 1, 1);

    private sealed class NoRunInference : IProposalMarkerInferenceRunner
    {
        public ValueTask<InferenceResponse> RunAsync(InferenceRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Factory tests must not run model inference.");
    }

    private sealed class TestStore : IDisposable
    {
        private readonly string modelId;
        private readonly string version;
        private readonly string modelRoot;

        internal TestStore(string algorithm = "mask_preserving_multiradius_enclosed_balanced_v2",
            string domain = "axis_polygon_or_16px_v25", double threshold = 0.1,
            bool approved = true, Action<JsonObject>? change = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "GraphReader.ProposalRegistration", Guid.NewGuid().ToString("N"));
            string source = Path.Combine(Root, "source");
            Directory.CreateDirectory(source);
            var descriptor = ProductionMarkerFrozenCandidateFactoryTests.WriteDescriptor(
                source, threshold, algorithm);
            modelId = descriptor.Identity.ModelId;
            version = descriptor.Identity.Version;
            modelRoot = Path.Combine(Root, "models");
            string Write(string relative, byte[] bytes)
            {
                string path = Path.Combine(modelRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                return Hash(path);
            }
            string modelPath = $"runtime/{modelId}/{version}/candidate.onnx";
            string modelSha = Write(modelPath, File.ReadAllBytes(descriptor.Identity.FilePath));
            string noticePath = $"notices/{modelId}/{version}/notice.txt";
            string noticeSha = Write(noticePath, "Apache-2.0 test fixture, no production payload."u8.ToArray());
            string evidencePath = $"evidence/{modelId}/{version}/gate.json";
            string evidenceSha = Write(evidencePath, "{\"status\":\"pass\",\"scope\":\"unit-test-only\"}"u8.ToArray());
            JsonObject manifest = JsonNode.Parse(File.ReadAllText(descriptor.ManifestPath))!.AsObject();
            manifest["manifest_version"] = 1;
            manifest["source"] = JsonSerializer.SerializeToNode(new { name = "owned test fixture", url = "local://tests", revision = "1" });
            manifest["license"] = JsonSerializer.SerializeToNode(new { spdx = "Apache-2.0", notice_path = "LICENSES/notice.txt", reviewed = true });
            manifest["commercial_use"] = true;
            manifest["redistribution"] = true;
            manifest["providers"] = new JsonArray("cpu");
            manifest["preprocessing"]!["proposal_domain"] = domain;
            manifest["benchmarks"] = JsonSerializer.SerializeToNode(new[] { new
            {
                profile = "proposal-registration-test", status = "pass", release_eligible = true,
                production_approval = approved, evidence_path = "artifacts/evidence/gate.json", evidence_sha256 = evidenceSha,
            } });
            change?.Invoke(manifest);
            string manifestPath = $"manifest/{modelId}/{version}/manifest.json";
            string manifestSha = Write(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest));
            var index = new { schema_version = 1, models = new[] { new
            {
                model_id = modelId, model_version = version,
                manifest = new { path = manifestPath, sha256 = manifestSha },
                payloads = new[] { new { declared_path = "candidate.onnx", path = modelPath, sha256 = modelSha } },
                notice = new { declared_path = "LICENSES/notice.txt", path = noticePath, sha256 = noticeSha },
                benchmark_evidence = new { declared_path = "artifacts/evidence/gate.json", path = evidencePath, sha256 = evidenceSha },
            } } };
            Write("production-model-index.json", JsonSerializer.SerializeToUtf8Bytes(index));
        }

        internal string Root { get; }
        internal Task<ResolvedProductionModel> ResolveAsync() => new ProductionModelStore(modelRoot)
            .ResolveAsync(modelId, version, InferenceProvider.Cpu, CancellationToken.None).AsTask();
        private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
