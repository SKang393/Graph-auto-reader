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
public sealed class ProductionTiledProbabilityOcrCandidateTests
{
    [TestMethod]
    public async Task ExactManifestCreatesCpuOnlyUnapprovedOriginalGray8Candidate()
    {
        string root = CreateTemporaryDirectory();
        var sessionFactory = new RecordingSessionFactory();
        await using ProductionInferenceRuntimeHost host = CreateRuntimeHost(root, sessionFactory);
        try
        {
            CandidatePair pair = WriteCandidatePair(root);

            ProductionOcrAdapter adapter = await ProductionOcrAdapter
                .CreateForFrozenTiledProbabilityCandidateEvaluationAsync(
                    pair.Detection,
                    pair.Recognition,
                    host,
                    new string('d', 64),
                    CancellationToken.None);

            Assert.IsFalse(adapter.IsApproved);
            Assert.AreEqual("unapproved_frozen_candidate", adapter.ConfigurationScope);
            StringAssert.Contains(
                adapter.AdapterId,
                $"graphreader-ocr:{ProductionOcrAdapter.TiledProbabilityCandidateCompositionVersion}:");
            Assert.AreEqual(2, sessionFactory.CreatedCount);
            Assert.AreEqual(2, sessionFactory.RunCount);
            CollectionAssert.AreEqual(
                new long[] { 1, 1, 256, 256 },
                sessionFactory.DetectionInputShape);
            Assert.AreEqual("source_tiles", sessionFactory.DetectionInputName);
            Assert.AreEqual("text_logits", sessionFactory.DetectionOutputName);
            Assert.AreEqual(1f, sessionFactory.DetectionInputValues![0]);
            Assert.AreEqual(0f, sessionFactory.DetectionInputValues[32]);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("tile_size")]
    [DataRow("probability_threshold")]
    [DataRow("source_image")]
    [DataRow("input_shape")]
    [DataRow("extra_postprocessing_field")]
    public async Task ChangedTiledContractIsRejectedBeforeRuntimeInitialization(string mutation)
    {
        string root = CreateTemporaryDirectory();
        var sessionFactory = new RecordingSessionFactory();
        await using ProductionInferenceRuntimeHost host = CreateRuntimeHost(root, sessionFactory);
        try
        {
            CandidatePair pair = WriteCandidatePair(root, mutation);

            await Assert.ThrowsAsync<InvalidDataException>(() => ProductionOcrAdapter
                .CreateForFrozenTiledProbabilityCandidateEvaluationAsync(
                    pair.Detection,
                    pair.Recognition,
                    host,
                    new string('d', 64),
                    CancellationToken.None));

            Assert.IsFalse(host.IsInitialized);
            Assert.AreEqual(0, sessionFactory.CreatedCount);
            Assert.AreEqual(0, sessionFactory.RunCount);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task NonCpuManifestIsRejectedBeforeRuntimeInitialization()
    {
        string root = CreateTemporaryDirectory();
        var sessionFactory = new RecordingSessionFactory();
        await using ProductionInferenceRuntimeHost host = CreateRuntimeHost(root, sessionFactory);
        try
        {
            CandidatePair pair = WriteCandidatePair(root, recognitionProviders: ["cpu", "directml"]);

            await Assert.ThrowsAsync<InvalidDataException>(() => ProductionOcrAdapter
                .CreateForFrozenTiledProbabilityCandidateEvaluationAsync(
                    pair.Detection,
                    pair.Recognition,
                    host,
                    new string('d', 64),
                    CancellationToken.None));

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
    public async Task ChangedPinnedDetectionBytesAreRejectedBeforeRuntimeInitialization()
    {
        string root = CreateTemporaryDirectory();
        var sessionFactory = new RecordingSessionFactory();
        await using ProductionInferenceRuntimeHost host = CreateRuntimeHost(root, sessionFactory);
        try
        {
            CandidatePair pair = WriteCandidatePair(root);
            await File.AppendAllTextAsync(pair.Detection.Identity.FilePath, "changed");

            await Assert.ThrowsAsync<InvalidDataException>(() => ProductionOcrAdapter
                .CreateForFrozenTiledProbabilityCandidateEvaluationAsync(
                    pair.Detection,
                    pair.Recognition,
                    host,
                    new string('d', 64),
                    CancellationToken.None));

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
        string? detectionMutation = null,
        string[]? recognitionProviders = null)
    {
        string detectionPath = Path.Combine(root, "v38-detector.onnx");
        string recognitionPath = Path.Combine(root, "recognizer.onnx");
        File.WriteAllBytes(detectionPath, [0x01, 0x02, 0x03]);
        File.WriteAllBytes(recognitionPath, [0x04, 0x05, 0x06]);
        var detectionIdentity = new ModelIdentity(
            "graph-text-dice-loss-detector-v38",
            "P1",
            Sha256(detectionPath),
            detectionPath);
        var recognitionIdentity = new ModelIdentity(
            "en-ppocrv5-mobile-rec",
            "0.1.0-local-candidate",
            Sha256(recognitionPath),
            recognitionPath);
        string detectionManifest = WriteDetectionManifest(
            root,
            detectionIdentity,
            detectionMutation);
        string recognitionManifest = WriteRecognitionManifest(
            root,
            recognitionIdentity,
            recognitionProviders ?? ["cpu"]);
        return new CandidatePair(
            new FrozenCandidateOcrModelDescriptor(
                detectionIdentity,
                detectionManifest,
                Sha256(detectionManifest)),
            new FrozenCandidateOcrModelDescriptor(
                recognitionIdentity,
                recognitionManifest,
                Sha256(recognitionManifest)));
    }

    private static string WriteDetectionManifest(
        string root,
        ModelIdentity identity,
        string? mutation)
    {
        Dictionary<string, object?> manifest = ManifestRoot(identity, "ocr_detection", ["cpu"]);
        manifest["inputs"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = "source_tiles",
                ["element_type"] = "float32",
                ["layout"] = "NCHW",
                ["shape"] = mutation == "input_shape"
                    ? new object[] { "N", 1, 256, 256 }
                    : new object[] { "tile_count", 1, 256, 256 },
                ["channels"] = new[] { "gray" },
            },
        };
        manifest["outputs"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = "text_logits",
                ["element_type"] = "float32",
                ["layout"] = "NCHW",
                ["shape"] = new object[] { "tile_count", 1, 256, 256 },
                ["channels"] = new[] { "text_logit" },
                ["activation"] = "sigmoid_logit",
            },
        };
        manifest["preprocessing"] = new Dictionary<string, object?>
        {
            ["source_image"] = mutation == "source_image" ? "axis_masked_gray8" : "immutable_original_gray8",
            ["tile_order"] = "row-major",
            ["tile_size"] = mutation == "tile_size" ? 128 : 256,
            ["tile_overlap"] = 64,
            ["tile_step"] = 192,
            ["partial_tile_padding"] = "white-255-top-left-valid",
            ["normalization"] = "1-gray/255-float32",
        };
        var postprocessing = new Dictionary<string, object?>
        {
            ["algorithm"] = "v38-gray8-tiled-probability-components-v1",
            ["overlap_merge"] = "valid-region-float32-mean",
            ["probability_threshold"] = mutation == "probability_threshold" ? 0.41f : 0.4f,
            ["morphology"] = "global-binary-close-3x3-once",
            ["connectivity"] = 8,
            ["minimum_component_area"] = 8,
            ["minimum_side_length"] = 2,
            ["maximum_tile_count"] = 256,
            ["rectangle_bounds"] = "half-open-original-pixel",
        };
        if (mutation == "extra_postprocessing_field")
        {
            postprocessing["unreviewed"] = true;
        }
        manifest["postprocessing"] = postprocessing;
        return WriteManifest(root, "detection-manifest.json", manifest);
    }

    private static string WriteRecognitionManifest(
        string root,
        ModelIdentity identity,
        string[] providers)
    {
        Dictionary<string, object?> manifest = ManifestRoot(identity, "ocr_recognition", providers);
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

    private static Dictionary<string, object?> ManifestRoot(
        ModelIdentity identity,
        string task,
        string[] providers) =>
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
            ["providers"] = providers,
            ["benchmarks"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["profile"] = "frozen-candidate-diagnostic",
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
            "GraphReader.ProductionTiledProbabilityOcrCandidate",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed record CandidatePair(
        FrozenCandidateOcrModelDescriptor Detection,
        FrozenCandidateOcrModelDescriptor Recognition);

    private sealed class RecordingSessionFactory : IInferenceSessionFactory
    {
        private int createdCount;
        private int runCount;

        public int CreatedCount => Volatile.Read(ref createdCount);

        public int RunCount => Volatile.Read(ref runCount);

        public long[]? DetectionInputShape { get; private set; }

        public string? DetectionInputName { get; private set; }

        public string? DetectionOutputName { get; private set; }

        public float[]? DetectionInputValues { get; private set; }

        public ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref createdCount);
            bool isDetection = model.ModelId.Contains("detector", StringComparison.Ordinal);
            return ValueTask.FromResult<IInferenceSession>(
                new RecordingSession(
                    provider,
                    isDetection,
                    input =>
                    {
                        Interlocked.Increment(ref runCount);
                        if (isDetection)
                        {
                            DetectionInputShape = input.Shape.ToArray();
                            DetectionInputName = input.InputName;
                            DetectionOutputName = input.OutputName;
                            DetectionInputValues = input.Values.ToArray();
                        }
                    }));
        }
    }

    private sealed class RecordingSession(
        InferenceProvider provider,
        bool isDetection,
        Action<InferenceInput> recordRun) : IInferenceSession
    {
        public InferenceProvider Provider { get; } = provider;

        public ValueTask<InferenceExecution> RunAsync(
            InferenceInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            recordRun(input);
            int outputLength = isDetection
                ? checked((int)(input.Shape[0] * input.Shape[^2] * input.Shape[^1]))
                : checked((int)input.Shape[0] * 4);
            return ValueTask.FromResult(new InferenceExecution(
                Array.AsReadOnly(new float[outputLength]),
                Provider,
                new StageTiming(0, 0, 0, 0, 0, false, false),
                new MemoryDiagnostics(0, 0, 0, 0, outputLength)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
