// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionOcrLocalCandidateFactoryTests
{
    [TestMethod]
    [DataRow(GraphStructureConsensusGeometry.ModelPolygon, GraphStructureModelInput.AxisMasked)]
    [DataRow(GraphStructureConsensusGeometry.MatchedComponent, GraphStructureModelInput.AxisMasked)]
    [DataRow(GraphStructureConsensusGeometry.ModelPolygon, GraphStructureModelInput.Original)]
    [DataRow(GraphStructureConsensusGeometry.InitialDbContour, GraphStructureModelInput.Original)]
    public async Task ExactPinnedPairCreatesUnapprovedAdapterThroughSharedPreflight(
        GraphStructureConsensusGeometry outputGeometry,
        GraphStructureModelInput modelInput)
    {
        string root = CreateTemporaryDirectory();
        var sessionFactory = new ShapeAwareSessionFactory();
        await using ProductionInferenceRuntimeHost host = CreateRuntimeHost(root, sessionFactory);
        try
        {
            CandidatePair pair = WriteCandidatePair(root);

            ProductionOcrAdapter adapter =
                await ProductionOcrAdapter.CreateForLocalSyntheticCandidateEvaluationAsync(
                    pair.Detection,
                    pair.Recognition,
                    host,
                    new string('d', 64),
                    CancellationToken.None,
                    outputGeometry,
                    modelInput);

            Assert.IsFalse(adapter.IsApproved);
            Assert.AreEqual(2, sessionFactory.CreatedCount);
            Assert.AreEqual(2, sessionFactory.RunCount);
            StringAssert.Contains(adapter.AdapterId,
                GraphStructureConsensusTextRegionDetector.GetCompositionVersion(outputGeometry, modelInput));
            StringAssert.Contains(adapter.AdapterId, pair.Detection.Identity.Sha256[..12]);
            StringAssert.Contains(adapter.AdapterId, pair.Recognition.Identity.Sha256[..12]);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OriginalInputRejectsCombinedGeometryChangeBeforeRuntimeInitialization()
    {
        string root = CreateTemporaryDirectory();
        var sessionFactory = new ShapeAwareSessionFactory();
        await using ProductionInferenceRuntimeHost host = CreateRuntimeHost(root, sessionFactory);
        try
        {
            CandidatePair pair = WriteCandidatePair(root);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProductionOcrAdapter.CreateForLocalSyntheticCandidateEvaluationAsync(
                    pair.Detection, pair.Recognition, host, new string('d', 64),
                    CancellationToken.None, GraphStructureConsensusGeometry.MatchedComponent,
                    GraphStructureModelInput.Original));
            Assert.IsFalse(host.IsInitialized);
            Assert.AreEqual(0, sessionFactory.CreatedCount);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitialContourRejectsMaskedInputBeforeRuntimeInitialization()
    {
        string root = CreateTemporaryDirectory();
        var sessionFactory = new ShapeAwareSessionFactory();
        await using ProductionInferenceRuntimeHost host = CreateRuntimeHost(root, sessionFactory);
        try
        {
            CandidatePair pair = WriteCandidatePair(root);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProductionOcrAdapter.CreateForLocalSyntheticCandidateEvaluationAsync(
                    pair.Detection, pair.Recognition, host, new string('d', 64),
                    CancellationToken.None, GraphStructureConsensusGeometry.InitialDbContour,
                    GraphStructureModelInput.AxisMasked));
            Assert.IsFalse(host.IsInitialized);
            Assert.AreEqual(0, sessionFactory.CreatedCount);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OriginalInputEvidenceSeparatelyIdentifiesModelAndStructuralPixels()
    {
        byte[] originalGray = [10, 20, 30, 40];
        byte[] originalBgr = [10, 10, 10, 20, 20, 20, 30, 30, 30, 40, 40, 40];
        var original = new OcrImage(2, 2, 2, originalGray, OcrSourceImage.Original,
            OcrFrameTransform.Identity, BgrPixels: new OcrBgrBytePixels(6, originalBgr));
        byte[] maskedGray = [255, 255, 255, 255];
        var masked = new OcrDetectorImage(original with { Pixels = maskedGray },
            Convert.ToHexStringLower(SHA256.HashData(maskedGray)));
        IReadOnlyList<string> warnings = ProductionOcrAdapter.DetectorInputWarnings(
            GraphStructureModelInput.Original, original, masked);
        CollectionAssert.Contains(warnings.ToArray(),
            $"ocr_detector_model_input_sha256:{Convert.ToHexStringLower(SHA256.HashData(originalGray))}");
        CollectionAssert.Contains(warnings.ToArray(),
            $"ocr_detector_model_input_bgr_sha256:{Convert.ToHexStringLower(SHA256.HashData(originalBgr))}");
        CollectionAssert.Contains(warnings.ToArray(), $"ocr_detector_structure_input_sha256:{masked.PixelSha256}");
        Assert.IsFalse(warnings.Contains("ocr_detector_axis_geometry_mask_applied", StringComparer.Ordinal));
        CollectionAssert.AreEqual(new[]
        {
            "ocr_detector_axis_geometry_mask_applied", $"ocr_detector_input_sha256:{masked.PixelSha256}",
        }, ProductionOcrAdapter.DetectorInputWarnings(GraphStructureModelInput.AxisMasked, original, masked).ToArray());
    }

    [TestMethod]
    public async Task UnknownGeometryIsRejectedBeforeRuntimeInitialization()
    {
        string root = CreateTemporaryDirectory();
        var sessionFactory = new ShapeAwareSessionFactory();
        await using ProductionInferenceRuntimeHost host = CreateRuntimeHost(root, sessionFactory);
        try
        {
            CandidatePair pair = WriteCandidatePair(root);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                ProductionOcrAdapter.CreateForLocalSyntheticCandidateEvaluationAsync(
                    pair.Detection, pair.Recognition, host, new string('d', 64),
                    CancellationToken.None, (GraphStructureConsensusGeometry)99));
            Assert.IsFalse(host.IsInitialized);
            Assert.AreEqual(0, sessionFactory.CreatedCount);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SwappedManifestCannotRelabelPinnedModelBeforeRuntimeInitialization()
    {
        string root = CreateTemporaryDirectory();
        var sessionFactory = new ShapeAwareSessionFactory();
        await using ProductionInferenceRuntimeHost host = CreateRuntimeHost(root, sessionFactory);
        try
        {
            CandidatePair pair = WriteCandidatePair(root);
            var relabeledDetection = pair.Detection with
            {
                ManifestPath = pair.Recognition.ManifestPath,
                ManifestSha256 = pair.Recognition.ManifestSha256,
            };

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionOcrAdapter.CreateForLocalSyntheticCandidateEvaluationAsync(
                    relabeledDetection,
                    pair.Recognition,
                    host,
                    new string('d', 64),
                    CancellationToken.None));

            StringAssert.Contains(exception.Message, "model_id");
            Assert.IsFalse(host.IsInitialized);
            Assert.AreEqual(0, sessionFactory.CreatedCount);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangedModelBytesAreRejectedBeforeRuntimeInitialization()
    {
        string root = CreateTemporaryDirectory();
        var sessionFactory = new ShapeAwareSessionFactory();
        await using ProductionInferenceRuntimeHost host = CreateRuntimeHost(root, sessionFactory);
        try
        {
            CandidatePair pair = WriteCandidatePair(root);
            await File.AppendAllTextAsync(pair.Detection.Identity.FilePath, "changed");

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionOcrAdapter.CreateForLocalSyntheticCandidateEvaluationAsync(
                    pair.Detection,
                    pair.Recognition,
                    host,
                    new string('d', 64),
                    CancellationToken.None));

            StringAssert.Contains(exception.Message, "pinned SHA-256");
            Assert.IsFalse(host.IsInitialized);
            Assert.AreEqual(0, sessionFactory.CreatedCount);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangedManifestBytesAreRejectedBeforeRuntimeInitialization()
    {
        string root = CreateTemporaryDirectory();
        var sessionFactory = new ShapeAwareSessionFactory();
        await using ProductionInferenceRuntimeHost host = CreateRuntimeHost(root, sessionFactory);
        try
        {
            CandidatePair pair = WriteCandidatePair(root);
            await File.AppendAllTextAsync(pair.Recognition.ManifestPath, " ");

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionOcrAdapter.CreateForLocalSyntheticCandidateEvaluationAsync(
                    pair.Detection,
                    pair.Recognition,
                    host,
                    new string('d', 64),
                    CancellationToken.None));

            StringAssert.Contains(exception.Message, "pinned SHA-256");
            Assert.IsFalse(host.IsInitialized);
            Assert.AreEqual(0, sessionFactory.CreatedCount);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangedInputOutputContractIsRejectedBeforeRuntimeInitialization()
    {
        string root = CreateTemporaryDirectory();
        var sessionFactory = new ShapeAwareSessionFactory();
        await using ProductionInferenceRuntimeHost host = CreateRuntimeHost(root, sessionFactory);
        try
        {
            CandidatePair pair = WriteCandidatePair(root, invalidDetectionOutputChannels: true);

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionOcrAdapter.CreateForLocalSyntheticCandidateEvaluationAsync(
                    pair.Detection,
                    pair.Recognition,
                    host,
                    new string('d', 64),
                    CancellationToken.None));

            StringAssert.Contains(exception.Message, "channels");
            Assert.IsFalse(host.IsInitialized);
            Assert.AreEqual(0, sessionFactory.CreatedCount);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static CandidatePair WriteCandidatePair(
        string root,
        bool invalidDetectionOutputChannels = false)
    {
        string detectionPath = Path.Combine(root, "PP-OCRv5_mobile_det.onnx");
        string recognitionPath = Path.Combine(root, "en_PP-OCRv5_mobile_rec.onnx");
        File.WriteAllBytes(detectionPath, [0x01, 0x02, 0x03]);
        File.WriteAllBytes(recognitionPath, [0x04, 0x05, 0x06]);
        string detectionSha256 = Sha256(detectionPath);
        string recognitionSha256 = Sha256(recognitionPath);
        var detectionIdentity = new ModelIdentity(
            "pp-ocrv5-mobile-det",
            "0.1.0-local-candidate",
            detectionSha256,
            detectionPath);
        var recognitionIdentity = new ModelIdentity(
            "en-ppocrv5-mobile-rec",
            "0.1.0-local-candidate",
            recognitionSha256,
            recognitionPath);
        string detectionManifestPath = WriteDetectionManifest(
            root,
            detectionIdentity,
            invalidDetectionOutputChannels);
        string recognitionManifestPath = WriteRecognitionManifest(root, recognitionIdentity);
        return new CandidatePair(
            new LocalSyntheticOcrModelDescriptor(
                detectionIdentity,
                detectionManifestPath,
                Sha256(detectionManifestPath)),
            new LocalSyntheticOcrModelDescriptor(
                recognitionIdentity,
                recognitionManifestPath,
                Sha256(recognitionManifestPath)));
    }

    private static string WriteDetectionManifest(
        string root,
        ModelIdentity identity,
        bool invalidOutputChannels)
    {
        Dictionary<string, object?> manifest = ManifestRoot(identity, "ocr_detection");
        manifest["inputs"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = "x",
                ["element_type"] = "float32",
                ["layout"] = "NCHW",
                ["shape"] = new object[] { 1, 3, "H", "W" },
                ["channels"] = new[] { "b", "g", "r" },
            },
        };
        manifest["outputs"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = "fetch_name_0",
                ["element_type"] = "float32",
                ["layout"] = "NCHW",
                ["shape"] = new object[] { 1, 1, "H", "W" },
                ["channels"] = invalidOutputChannels
                    ? new[] { "wrong_probability" }
                    : new[] { "text_probability" },
                ["activation"] = "probability",
            },
        };
        manifest["preprocessing"] = new Dictionary<string, object?>
        {
            ["channel_order"] = "BGR",
            ["channel_means"] = new[] { 0.485f, 0.456f, 0.406f },
            ["channel_scales"] = new[] { 1f / 0.229f, 1f / 0.224f, 1f / 0.225f },
            ["maximum_side_length"] = 960,
            ["dimension_multiple"] = 128,
        };
        manifest["postprocessing"] = new Dictionary<string, object?>
        {
            ["algorithm"] = "db_postprocess_v1",
            ["score_mode"] = "fast",
            ["probability_threshold"] = 0.30f,
            ["box_confidence_threshold"] = 0.60f,
            ["unclip_ratio"] = 1.5,
            ["minimum_side_length"] = 3,
            ["maximum_regions"] = 1000,
        };
        return WriteManifest(root, "detection-manifest.json", manifest);
    }

    private static string WriteRecognitionManifest(string root, ModelIdentity identity)
    {
        Dictionary<string, object?> manifest = ManifestRoot(identity, "ocr_recognition");
        manifest["inputs"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = "x",
                ["element_type"] = "float32",
                ["layout"] = "NCHW",
                ["shape"] = new object[] { "N", 3, 48, "W" },
                ["channels"] = new[] { "b", "g", "r" },
            },
        };
        manifest["outputs"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = "fetch_name_0",
                ["element_type"] = "float32",
                ["layout"] = "NTC",
                ["shape"] = new object[] { "N", "T", "C" },
                ["alphabet"] = "ab ",
                ["blank_class_index"] = 0,
            },
        };
        manifest["preprocessing"] = new Dictionary<string, object?>
        {
            ["channel_order"] = "BGR",
            ["channel_means"] = new[] { 0.5f, 0.5f, 0.5f },
            ["channel_scales"] = new[] { 2f, 2f, 2f },
            ["width_policy"] = "paddle_batch_max_wh_ratio_v1",
            ["minimum_width"] = 320,
            ["maximum_width"] = 4096,
        };
        manifest["postprocessing"] = new Dictionary<string, object?>
        {
            ["algorithm"] = "ctc_greedy_alternatives_v1",
            ["maximum_alternatives"] = 3,
        };
        return WriteManifest(root, "recognition-manifest.json", manifest);
    }

    private static Dictionary<string, object?> ManifestRoot(ModelIdentity identity, string task) =>
        new()
        {
            ["manifest_version"] = 1,
            ["model_id"] = identity.ModelId,
            ["model_version"] = identity.Version,
            ["task"] = task,
            ["source"] = new Dictionary<string, object?>
            {
                ["name"] = "test",
                ["url"] = "https://example.invalid/test",
                ["revision"] = "test",
            },
            ["license"] = new Dictionary<string, object?>
            {
                ["spdx"] = "Apache-2.0",
                ["notice_path"] = "NOTICE.txt",
                ["reviewed"] = true,
            },
            ["sha256"] = identity.Sha256,
            ["files"] = new[] { Path.GetFileName(identity.FilePath) },
            ["commercial_use"] = true,
            ["redistribution"] = true,
            ["providers"] = new[] { "cpu" },
            ["benchmarks"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["profile"] = "local-synthetic-diagnostic",
                    ["status"] = "diagnostic_only",
                    ["production_approval"] = false,
                },
            },
        };

    private static string WriteManifest(
        string root,
        string fileName,
        Dictionary<string, object?> manifest)
    {
        string path = Path.Combine(root, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        return path;
    }

    private static string Sha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static ProductionInferenceRuntimeHost CreateRuntimeHost(
        string root,
        IInferenceSessionFactory sessionFactory) =>
        new(
            new FakeExecutionProviderDiscovery("CPUExecutionProvider"),
            new WindowsExecutionProviderPolicy(),
            sessionFactory,
            new CpuThreadConfiguration(1, 1, 1),
            [InferenceProvider.Cpu],
            Path.Combine(root, "cache"),
            queueCapacity: 1,
            workerCount: 1);

    private static string CreateTemporaryDirectory()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "GraphReader.ProductionOcrLocalCandidateFactory",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed record CandidatePair(
        LocalSyntheticOcrModelDescriptor Detection,
        LocalSyntheticOcrModelDescriptor Recognition);

    private sealed class ShapeAwareSessionFactory : IInferenceSessionFactory
    {
        private int createdCount;
        private int runCount;

        public int CreatedCount => Volatile.Read(ref createdCount);

        public int RunCount => Volatile.Read(ref runCount);

        public ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref createdCount);
            return ValueTask.FromResult<IInferenceSession>(
                new ShapeAwareSession(
                    provider,
                    isDetection: model.ModelId.Contains("det", StringComparison.Ordinal),
                    () => Interlocked.Increment(ref runCount)));
        }
    }

    private sealed class ShapeAwareSession(
        InferenceProvider provider,
        bool isDetection,
        Action recordRun) : IInferenceSession
    {
        public InferenceProvider Provider { get; } = provider;

        public ValueTask<InferenceExecution> RunAsync(
            InferenceInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            recordRun();
            int outputLength = isDetection
                ? checked((int)(input.Shape[^2] * input.Shape[^1]))
                : checked((int)input.Shape[0] * 4);
            var output = new float[outputLength];
            return ValueTask.FromResult(new InferenceExecution(
                Array.AsReadOnly(output),
                Provider,
                new StageTiming(0, 0, 0, 0, 0, false, false),
                new MemoryDiagnostics(0, 0, 0, 0, outputLength)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
