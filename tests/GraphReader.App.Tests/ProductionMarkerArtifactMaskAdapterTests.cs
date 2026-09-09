// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionMarkerArtifactMaskAdapterTests
{
    [TestMethod]
    public async Task ApprovedAdapterMapsDenseArtifactHeadAndPreservesSeedMask()
    {
        TestContextData context = CreateContext();
        var output = new float[]
        {
            0.1f, 0.1f, 0.1f, 0.1f,
            1f, 1f, 1f, 1f,
            0f, 1f, 0f, 0f,
        };
        var runner = new Runner(Success(output, InferenceProvider.Cpu));
        var adapter = CreateAdapter(isApproved: true, runner);

        ProductionArtifactMaskEvidence evidence = await adapter.DetectAsync(
            context.Request,
            context.Raster,
            context.Seed,
            CancellationToken.None);

        Assert.AreEqual(1, runner.CallCount);
        Assert.IsNotNull(runner.LastRequest);
        CollectionAssert.AreEqual(
            new long[] { 1, 3, 2, 2 },
            runner.LastRequest.Input.Shape.ToArray());
        Assert.AreEqual("marker_artifact_mask", runner.LastRequest.CacheMaterial.StageName);
        Assert.AreEqual("markers", evidence.Envelope.Stage);
        Assert.AreEqual("cpu", evidence.Envelope.Model?.Provider);
        Assert.AreEqual(context.Request.Image.Sha256, evidence.RasterSha256);
        Assert.AreEqual(1f, evidence.CopyMask().Values.Span[15]);
        Assert.AreEqual(1f, evidence.CopyMask().Values.Span[3]);
        Assert.IsTrue(evidence.CopyMask().Values.ToArray().Any(static value => value > 0));
        CollectionAssert.Contains(
            evidence.Warnings.ToArray(),
            "artifact_mask_scope:marker_center_artifact_head_full_frame_seeded_by_ocr_axis");
    }

    [TestMethod]
    public async Task UnapprovedAdapterFailsBeforeInference()
    {
        TestContextData context = CreateContext();
        var runner = new Runner(Success(new float[12], InferenceProvider.Cpu));
        var adapter = CreateAdapter(isApproved: false, runner);

        ProductionWorkflowStageException exception =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => adapter.DetectAsync(
                context.Request,
                context.Raster,
                context.Seed,
                CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionModelsUnavailable, exception.Failure.Code);
        Assert.AreEqual(0, runner.CallCount);
    }

    [TestMethod]
    public async Task CancellationStopsBeforeInference()
    {
        TestContextData context = CreateContext();
        var runner = new Runner(Success(new float[12], InferenceProvider.Cpu));
        var adapter = CreateAdapter(isApproved: true, runner);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => adapter.DetectAsync(
            context.Request,
            context.Raster,
            context.Seed,
            cancellation.Token));

        Assert.AreEqual(0, runner.CallCount);
    }

    [TestMethod]
    public async Task InvalidOutputFailsClosedWithoutArtifactEvidence()
    {
        TestContextData context = CreateContext();
        var output = new float[]
        {
            0.1f, 0.1f, 0.1f, 0.1f,
            1f, 1f, 1f, 1f,
            0f, float.NaN, 0f, 0f,
        };
        var adapter = CreateAdapter(
            isApproved: true,
            new Runner(Success(output, InferenceProvider.Cpu)));

        ProductionWorkflowStageException exception =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => adapter.DetectAsync(
                context.Request,
                context.Raster,
                context.Seed,
                CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionEvidenceRejected, exception.Failure.Code);
        StringAssert.Contains(exception.Failure.TechnicalMessage, "invalid center, radius, or artifact values");
    }

    [TestMethod]
    public async Task FakeProviderIsRejectedAfterExecution()
    {
        TestContextData context = CreateContext();
        var output = new float[]
        {
            0.1f, 0.1f, 0.1f, 0.1f,
            1f, 1f, 1f, 1f,
            0f, 0f, 0f, 0f,
        };
        var adapter = CreateAdapter(
            isApproved: true,
            new Runner(Success(output, InferenceProvider.Fake)));

        ProductionWorkflowStageException exception =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => adapter.DetectAsync(
                context.Request,
                context.Raster,
                context.Seed,
                CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionEvidenceRejected, exception.Failure.Code);
        StringAssert.Contains(exception.Failure.TechnicalMessage, "non-production provider");
    }

    [TestMethod]
    public void DirectEvidenceAcceptsTier1MetricsWithoutExactScenes()
    {
        string evidencePath = WriteGateEvidence(
            markerPrecision: 0.95,
            markerRecall: 0.95,
            prohibitedHitRate: 0.01,
            includeQualityMetrics: true);
        try
        {
            ProductionArtifactMaskGateEvidence evidence =
                ProductionMarkerArtifactMaskAdapter.ReadDirectGateEvidence(
                    evidencePath,
                    new string('a', 64));

            Assert.AreEqual(0, evidence.ExactFixtureCount);
            Assert.AreEqual(0.95, evidence.MarkerPrecision, 0.0000001);
            Assert.AreEqual(0.95, evidence.MarkerRecall, 0.0000001);
            Assert.AreEqual(0.01, evidence.ProhibitedStructureHitRate, 0.0000001);
            Assert.IsFalse(evidence.LegacyApprovalCompatibility);
        }
        finally
        {
            File.Delete(evidencePath);
        }
    }

    [TestMethod]
    public void DirectEvidenceRejectsBelowTier1Metric()
    {
        string evidencePath = WriteGateEvidence(
            markerPrecision: 0.949,
            markerRecall: 0.95,
            prohibitedHitRate: 0.01,
            includeQualityMetrics: true);
        try
        {
            InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                ProductionMarkerArtifactMaskAdapter.ReadDirectGateEvidence(
                    evidencePath,
                    new string('a', 64)));

            StringAssert.Contains(exception.Message, "shared Tier 1 bars");
        }
        finally
        {
            File.Delete(evidencePath);
        }
    }

    [TestMethod]
    public void DirectEvidenceRetainsHistoricalExactPayloadCompatibility()
    {
        string evidencePath = WriteGateEvidence(
            markerPrecision: null,
            markerRecall: null,
            prohibitedHitRate: null,
            includeQualityMetrics: false);
        try
        {
            ProductionArtifactMaskGateEvidence evidence =
                ProductionMarkerArtifactMaskAdapter.ReadDirectGateEvidence(
                    evidencePath,
                    new string('a', 64));

            Assert.IsTrue(evidence.LegacyApprovalCompatibility);
            Assert.AreEqual(1d, evidence.MarkerPrecision);
            Assert.AreEqual(1d, evidence.MarkerRecall);
            Assert.AreEqual(0d, evidence.ProhibitedStructureHitRate);
        }
        finally
        {
            File.Delete(evidencePath);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void ProductionSizeSeedFramesReuseImmutableFullResolutionPlanes()
    {
        const int width = 2048;
        const int height = 2048;
        int pixelCount = checked(width * height);
        byte[] sourceBytes = [9, 8, 7, 6];
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        var raster = new ProductionDecodedRaster(
            width,
            height,
            sha256,
            WorkflowImageVariant.Original,
            MarkerAffineTransform.Identity,
            OcrFrameTransform.Identity,
            width,
            height,
            new byte[pixelCount],
            new float[pixelCount]);
        var ocrMask = new float[pixelCount];
        var artifactMask = new float[pixelCount];
        long beforeSeed = GC.GetAllocatedBytesForCurrentThread();
        var seed = new ProductionDetectionMaskSeed(
            width,
            height,
            sha256,
            WorkflowImageVariant.Original,
            ocrMask,
            artifactMask);
        long seedAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeSeed;

        MarkerImageFrame first = seed.CreateMarkerFrame(raster);
        long before = GC.GetAllocatedBytesForCurrentThread();
        MarkerImageFrame second = seed.CreateMarkerFrame(raster);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(seedAllocated < 1_000_000, $"Seed ownership allocated {seedAllocated} bytes.");
        Assert.IsTrue(allocated < 1_000_000, $"Second full-resolution frame allocated {allocated} bytes.");
        Assert.IsTrue(MemoryMarshal.TryGetArray(first.ChannelsFirstPixels, out ArraySegment<float> firstPixels));
        Assert.IsTrue(MemoryMarshal.TryGetArray(second.ChannelsFirstPixels, out ArraySegment<float> secondPixels));
        Assert.AreSame(firstPixels.Array, secondPixels.Array);
        Assert.IsTrue(MemoryMarshal.TryGetArray(first.OcrMask.Values, out ArraySegment<float> firstOcr));
        Assert.IsTrue(MemoryMarshal.TryGetArray(second.OcrMask.Values, out ArraySegment<float> secondOcr));
        Assert.AreSame(firstOcr.Array, secondOcr.Array);
        Assert.IsTrue(MemoryMarshal.TryGetArray(first.ArtifactMask.Values, out ArraySegment<float> firstArtifact));
        Assert.IsTrue(MemoryMarshal.TryGetArray(second.ArtifactMask.Values, out ArraySegment<float> secondArtifact));
        Assert.AreSame(firstArtifact.Array, secondArtifact.Array);
    }

    private static ProductionMarkerArtifactMaskAdapter CreateAdapter(
        bool isApproved,
        IProductionArtifactMaskInferenceRunner runner) =>
        new(
            new ModelIdentity(
                "test-marker-center",
                "1.0.0",
                new string('a', 64),
                "memory:test-marker-center.onnx"),
            new ProductionArtifactMaskTensorContract(
                "image_and_masks",
                "marker_heads",
                TensorWidth: 2,
                TensorHeight: 2,
                ArtifactChannelIndex: 2,
                StageVersion: "marker-artifact-mask-v1:1.0.0",
                Timeout: TimeSpan.FromSeconds(1)),
            isApproved,
            runner);

    private static TestContextData CreateContext()
    {
        byte[] bytes = [1, 2, 3, 4];
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Guid panelId = Guid.Parse("70000000-0000-0000-0000-000000000019");
        var image = new WorkflowImageEvidence(
            "memory:artifact-mask.png",
            sha256,
            4,
            4,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(
            panelId,
            Guid.Parse("71000000-0000-0000-0000-000000000019"),
            "artifact-mask.png",
            image);
        var request = new ProductionWorkflowDetectionRequest(
            new WorkflowPreparedPanel(imported, image, enhanced: null),
            image,
            WorkflowImageVariant.Original,
            Guid.Parse("72000000-0000-0000-0000-000000000019"),
            Guid.Parse("73000000-0000-0000-0000-000000000019"),
            bytes);
        var raster = new ProductionDecodedRaster(
            4,
            4,
            sha256,
            WorkflowImageVariant.Original,
            MarkerAffineTransform.Identity,
            OcrFrameTransform.Identity,
            4,
            4,
            new byte[16],
            Enumerable.Repeat(0.5f, 16).ToArray());
        var ocrMask = new float[16];
        ocrMask[0] = 1;
        var artifactMask = new float[16];
        artifactMask[15] = 1;
        var seed = new ProductionDetectionMaskSeed(
            4,
            4,
            sha256,
            WorkflowImageVariant.Original,
            ocrMask,
            artifactMask);
        return new TestContextData(request, raster, seed);
    }

    private static InferenceResponse Success(
        float[] output,
        InferenceProvider provider) =>
        new(
            true,
            new InferenceExecution(
                output,
                provider,
                new StageTiming(0, 1, 0, 1, 0, false, false),
                new MemoryDiagnostics(0, 0, 0, 0, output.Length)),
            null,
            [new ProviderAttempt(provider, true, null)]);

    private static string WriteGateEvidence(
        double? markerPrecision,
        double? markerRecall,
        double? prohibitedHitRate,
        bool includeQualityMetrics)
    {
        string[] fixtureIds = ["fixture-01", "fixture-02", "fixture-03"];
        byte[] evaluatorBytes = File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(),
            "ml",
            "markers",
            "center",
            "artifact_mask_public_gate.py"));
        string evaluatorSha256 = Convert.ToHexStringLower(SHA256.HashData(evaluatorBytes));
        var dataset = new Dictionary<string, object?>
        {
            ["schema"] = "graphreader.marker-artifact-mask-dataset.v1",
            ["scope"] = "public_synthetic",
            ["private_data"] = false,
            ["chandler_used"] = false,
            ["seed"] = 393,
            ["fixtures"] = fixtureIds.Select(id => new Dictionary<string, object?>
            {
                ["fixture_id"] = id,
                ["family"] = id,
                ["image_sha256"] = HashText($"image:{id}"),
                ["ground_truth_sha256"] = HashText($"truth:{id}"),
            }).ToArray(),
        };
        byte[] datasetBytes = JsonSerializer.SerializeToUtf8Bytes(dataset);
        string datasetSha256 = Convert.ToHexStringLower(SHA256.HashData(datasetBytes));
        var splitSeal = new Dictionary<string, object?>
        {
            ["schema"] = "graphreader.marker-artifact-mask-split-seal.v1",
            ["profile"] = ProductionMarkerArtifactMaskAdapter.ApprovalBenchmarkProfile,
            ["sealed"] = true,
            ["selection_locked_before_inference"] = true,
            ["private_data"] = false,
            ["chandler_used"] = false,
            ["dataset_manifest_sha256"] = datasetSha256,
            ["evaluator_source_sha256"] = evaluatorSha256,
            ["fixture_count"] = fixtureIds.Length,
            ["fixture_ids"] = fixtureIds,
        };
        byte[] splitBytes = JsonSerializer.SerializeToUtf8Bytes(splitSeal);
        string splitSha256 = Convert.ToHexStringLower(SHA256.HashData(splitBytes));
        static Dictionary<string, int> Hits(int axis = 0) => new()
        {
            ["text"] = 0,
            ["axis"] = axis,
            ["tick"] = 0,
            ["divider"] = 0,
            ["bracket"] = 0,
            ["arrow_shaft"] = 0,
            ["arrowhead"] = 0,
            ["legend"] = 0,
            ["line_intersection"] = 0,
        };
        var report = new Dictionary<string, object?>
        {
            ["schema"] = "graphreader.marker-artifact-mask-gate.v1",
            ["profile"] = ProductionMarkerArtifactMaskAdapter.ApprovalBenchmarkProfile,
            ["status"] = "pass",
            ["scope"] = "public_synthetic_sealed",
            ["provider"] = "cpu",
            ["seed_mask_scope"] = ProductionMarkerArtifactMaskAdapter.SeedMaskScope,
            ["coordinate_space"] = "original_pixels",
            ["release_eligible"] = true,
            ["production_approval"] = true,
            ["private_data"] = false,
            ["chandler_used"] = false,
            ["model_sha256"] = new string('a', 64),
            ["fixture_count"] = 3,
            ["exact_fixture_count"] = includeQualityMetrics ? 0 : 3,
            ["downstream_false_positive_count"] = includeQualityMetrics ? 3 : 0,
            ["downstream_false_negative_count"] = includeQualityMetrics ? 3 : 0,
            ["downstream_duplicate_count"] = includeQualityMetrics ? 1 : 0,
            ["prohibited_structure_hits"] = Hits(axis: includeQualityMetrics ? 1 : 0),
            ["fixture_results"] = fixtureIds.Select((id, index) => new Dictionary<string, object?>
            {
                ["fixture_id"] = id,
                ["exact_count"] = !includeQualityMetrics,
                ["false_positive_count"] = includeQualityMetrics ? 1 : 0,
                ["false_negative_count"] = includeQualityMetrics ? 1 : 0,
                ["duplicate_count"] = includeQualityMetrics && index == 0 ? 1 : 0,
                ["prohibited_structure_hits"] = Hits(axis: includeQualityMetrics && index == 0 ? 1 : 0),
            }).ToArray(),
            ["reviewed_resources"] = new Dictionary<string, object?>
            {
                ["dataset_manifest"] = Embedded("application/json", datasetBytes, datasetSha256),
                ["evaluator_source"] = Embedded("text/x-python", evaluatorBytes, evaluatorSha256),
                ["split_seal"] = Embedded("application/json", splitBytes, splitSha256),
            },
        };
        if (includeQualityMetrics)
        {
            report["artifact_precision"] = markerPrecision;
            report["artifact_recall"] = markerRecall;
            report["prohibited_structure_hit_rate"] = prohibitedHitRate;
        }

        string path = Path.Combine(Path.GetTempPath(), $"graphreader-artifact-mask-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(report));
        return path;
    }

    private static Dictionary<string, object?> Embedded(
        string mediaType,
        byte[] bytes,
        string sha256) => new()
        {
            ["media_type"] = mediaType,
            ["encoding"] = "base64",
            ["sha256"] = sha256,
            ["content_base64"] = Convert.ToBase64String(bytes),
        };

    private static string HashText(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "GraphAutoReader.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed record TestContextData(
        ProductionWorkflowDetectionRequest Request,
        ProductionDecodedRaster Raster,
        ProductionDetectionMaskSeed Seed);

    private sealed class Runner(InferenceResponse response) : IProductionArtifactMaskInferenceRunner
    {
        public int CallCount { get; private set; }

        public InferenceRequest? LastRequest { get; private set; }

        public ValueTask<InferenceResponse> RunAsync(
            InferenceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            return ValueTask.FromResult(response);
        }
    }
}
