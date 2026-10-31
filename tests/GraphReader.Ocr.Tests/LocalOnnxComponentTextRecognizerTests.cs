// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Collections.ObjectModel;
using GraphReader.Inference;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class LocalOnnxComponentTextRecognizerTests
{
    private const string PillowNoticeSha256 =
        "15181e7363dca9aed78b79bebebc7fde7f1814b8bd311ea3b87ae8ccadfc185b";
    private const string ComponentModelEnvironmentVariable = "GRAPHREADER_OCR_COMPONENT_V5";
    private const string ComponentModelSha256 =
        "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ComponentRecognizerEncodesGlyphGeometryAndDecodesNumericGrammar()
    {
        string directory = CreateDirectory();
        try
        {
            ModelIdentity identity = await CreateModelAsync(directory);
            var factory = new ComponentLogitSessionFactory([1, 2]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var recognizer = new LocalOnnxComponentTextRecognizer(
                runtime,
                new LocalOnnxComponentTextRecognizerOptions(identity, "0123456789.-%")
                {
                    AllowedProviders = [InferenceProvider.Cpu],
                });

            float[] pixels = Enumerable.Repeat(1f, 128 * 32).ToArray();
            Fill(pixels, 128, 20, 8, 6, 16, 0f);
            Fill(pixels, 128, 40, 8, 5, 16, 0f);
            OcrCrop crop = Crop("numeric", pixels, OcrSourceImage.Enhanced);

            IReadOnlyList<OcrRecognition> results = await recognizer.RecognizeBatchAsync(
                [crop],
                CancellationToken.None);

            Assert.HasCount(1, results);
            Assert.IsNull(results[0].Failure);
            Assert.HasCount(1, results[0].Alternatives);
            Assert.AreEqual("12", results[0].Alternatives[0].Text);
            Assert.AreEqual(OcrSourceImage.Enhanced, results[0].Alternatives[0].SourceImage);
            Assert.AreEqual(1, factory.Session.RunCount);
            CollectionAssert.AreEqual(
                new long[] { 2, 1, 24, 26 },
                factory.Session.LastInputShape.ToArray());
            Assert.HasCount(2 * 24 * 26, factory.Session.LastInputValues);
            Assert.AreEqual(0.5f, factory.Session.LastInputValues[20], 1e-6f);
            Assert.AreEqual(6f / 32f, factory.Session.LastInputValues[21], 1e-6f);
            Assert.AreEqual(15.5f / 31f, factory.Session.LastInputValues[22], 1e-6f);
            Assert.AreEqual(1f, factory.Session.LastInputValues[23], 1e-6f);
            Assert.AreEqual(0f, factory.Session.LastInputValues[24], 1e-6f);
            Assert.AreEqual(6f / 16f, factory.Session.LastInputValues[25], 1e-6f);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComponentRecognizerKeepsLeadingDecimalAcceptedBySharedNumericParser()
    {
        string directory = CreateDirectory();
        try
        {
            ModelIdentity identity = await CreateModelAsync(directory);
            const string alphabet = "0123456789.-%";
            var factory = new ComponentLogitSessionFactory([
                alphabet.IndexOf('.'),
                alphabet.IndexOf('5'),
            ]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var recognizer = new LocalOnnxComponentTextRecognizer(
                runtime,
                new LocalOnnxComponentTextRecognizerOptions(identity, alphabet)
                {
                    AllowedProviders = [InferenceProvider.Cpu],
                });

            float[] pixels = Enumerable.Repeat(1f, 128 * 32).ToArray();
            Fill(pixels, 128, 20, 13, 3, 3, 0f);
            Fill(pixels, 128, 38, 8, 6, 16, 0f);

            IReadOnlyList<OcrRecognition> results = await recognizer.RecognizeBatchAsync(
                [Crop("leading-decimal", pixels)],
                CancellationToken.None);

            Assert.HasCount(1, results);
            Assert.IsNull(results[0].Failure);
            Assert.AreEqual(".5", results[0].Alternatives.Single().Text);
            Assert.IsTrue(GraphNumericParser.IsLiteralGraphNumber(results[0].Alternatives.Single().Text));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComponentEncodingMatchesFrozenPillowV5TensorBytes()
    {
        string directory = CreateDirectory();
        try
        {
            ModelIdentity identity = await CreateModelAsync(directory);
            var factory = new ComponentLogitSessionFactory([1, 2]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var recognizer = new LocalOnnxComponentTextRecognizer(
                runtime,
                new LocalOnnxComponentTextRecognizerOptions(identity, "0123456789.-%")
                {
                    AllowedProviders = [InferenceProvider.Cpu],
                });

            float[] pixels = Enumerable.Repeat(1f, 128 * 32).ToArray();
            for (var y = 6; y < 25; y++)
            {
                for (var x = 20; x < 30; x++)
                {
                    pixels[(y * 128) + x] = (30 + ((x * 13 + y * 17) % 90)) / 255f;
                }
            }

            for (var y = 10; y < 20; y++)
            {
                for (var x = 38; x < 43; x++)
                {
                    pixels[(y * 128) + x] = (55 + ((x * 11 + y * 19) % 70)) / 255f;
                }
            }

            pixels[(5 * 128) + 90] = 0;
            IReadOnlyList<OcrRecognition> results = await recognizer.RecognizeBatchAsync(
                [Crop("pillow-v5-parity", pixels)],
                CancellationToken.None);

            Assert.HasCount(1, results);
            Assert.IsNull(results[0].Failure);
            Assert.AreEqual("12", results[0].Alternatives.Single().Text);
            Assert.AreEqual(
                "cacefe3619f6c71c6241cf1a95dae4b0b841c395520696079ed0c382897ea3e8",
                Convert.ToHexStringLower(SHA256.HashData(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                        factory.Session.LastInputValues.ToArray().AsSpan()))));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void SourceDerivedPillowKernelRetainsExactReviewedNoticeAndAuditCoverage()
    {
        string root = FindRepositoryRoot();
        string noticePath = Path.Combine(root, "LICENSES", "Pillow-12.3.0-HPND.txt");
        Assert.AreEqual(PillowNoticeSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(noticePath))));
        string notices = File.ReadAllText(Path.Combine(root, "THIRD_PARTY_NOTICES.md"));
        StringAssert.Contains(notices, "LICENSES/Pillow-12.3.0-HPND.txt");
        StringAssert.Contains(notices, "bb1d8e8ab8d29048624d96e3ee53cecf7c13d13d");
        StringAssert.Contains(notices, "808c212d03289b9ab70fad67168da1b14ed8dd74f1607c7a7278c50cbc1ae0ab");

        string audit = File.ReadAllText(Path.Combine(root, "packaging", "common", "release-audit.json"));
        StringAssert.Contains(audit, "Apache-2.0 AND OFL-1.1 AND HPND");
        StringAssert.Contains(audit, "LICENSES/Pillow-12.3.0-HPND.txt");
    }

    [TestMethod]
    public async Task StructuralRuleRejectsDividerWithoutExecutingPayload()
    {
        string directory = CreateDirectory();
        try
        {
            ModelIdentity identity = await CreateModelAsync(directory);
            var factory = new ComponentLogitSessionFactory([1]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var recognizer = new LocalOnnxComponentTextRecognizer(
                runtime,
                new LocalOnnxComponentTextRecognizerOptions(identity, "0123456789.-%")
                {
                    AllowedProviders = [InferenceProvider.Cpu],
                });
            float[] pixels = Enumerable.Repeat(1f, 128 * 32).ToArray();
            Fill(pixels, 128, 64, 4, 2, 24, 0f);

            IReadOnlyList<OcrRecognition> results = await recognizer.RecognizeBatchAsync(
                [Crop("divider", pixels)],
                CancellationToken.None);

            Assert.HasCount(1, results);
            Assert.IsNull(results[0].Failure);
            Assert.IsEmpty(results[0].Alternatives);
            Assert.AreEqual(0, factory.CreatedCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComponentRecognizerFailsClosedOnWrongOutputShape()
    {
        string directory = CreateDirectory();
        try
        {
            ModelIdentity identity = await CreateModelAsync(directory);
            var factory = new ComponentLogitSessionFactory([]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var recognizer = new LocalOnnxComponentTextRecognizer(
                runtime,
                new LocalOnnxComponentTextRecognizerOptions(identity, "0123456789.-%")
                {
                    AllowedProviders = [InferenceProvider.Cpu],
                });
            float[] pixels = Enumerable.Repeat(1f, 128 * 32).ToArray();
            Fill(pixels, 128, 20, 8, 6, 16, 0f);

            IReadOnlyList<OcrRecognition> results = await recognizer.RecognizeBatchAsync(
                [Crop("bad-output", pixels)],
                CancellationToken.None);

            Assert.AreEqual("OCR_MODEL_OUTPUT_SHAPE_MISMATCH", results[0].Failure?.Code);
            Assert.IsEmpty(results[0].Alternatives);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComponentRecognizerRejectsInvalidRuntimeContract()
    {
        string directory = CreateDirectory();
        try
        {
            ModelIdentity identity = await CreateModelAsync(directory);
            await using InferenceRuntime runtime = CreateRuntime(
                directory,
                new ComponentLogitSessionFactory([1]));

            Assert.Throws<ArgumentException>(() => new LocalOnnxComponentTextRecognizer(
                runtime,
                new LocalOnnxComponentTextRecognizerOptions(identity, "0123456789.-%")
                {
                    CanvasWidth = 127,
                }));
            Assert.Throws<ArgumentException>(() => new LocalOnnxComponentTextRecognizer(
                runtime,
                new LocalOnnxComponentTextRecognizerOptions(identity, "0123456789.-%")
                {
                    AllowedProviders = [InferenceProvider.DirectMl],
                }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExactComponentModelExecutesThroughProductionRecognizerOnCpu()
    {
        string? modelPath = Environment.GetEnvironmentVariable(ComponentModelEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            Assert.Inconclusive(
                $"Set {ComponentModelEnvironmentVariable} to the ignored V5 ONNX to run this direct payload probe.");
        }

        modelPath = Path.GetFullPath(modelPath);
        Assert.IsTrue(File.Exists(modelPath), $"OCR V5 model does not exist: {modelPath}");
        var identity = new ModelIdentity(
            "graph-numeric-component-ensemble-v5",
            "0.0.21-p1",
            ComponentModelSha256,
            modelPath);
        var registry = new OnnxSessionRegistry(
            new CpuOnlyDiscovery(),
            new WindowsExecutionProviderPolicy(),
            new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance),
            CpuThreadConfiguration.Create(1, new SingleCoreDetector()));
        await using var runtime = new InferenceRuntime(
            registry,
            new BoundedInferenceScheduler(2, 1),
            new NoOpStageCache());
        var recognizer = new LocalOnnxComponentTextRecognizer(
            runtime,
            new LocalOnnxComponentTextRecognizerOptions(identity, "0123456789.-%")
            {
                AllowedProviders = [InferenceProvider.Cpu],
                BypassCache = true,
            });
        float[] pixels = Enumerable.Repeat(1f, 128 * 32).ToArray();
        Fill(pixels, 128, 20, 8, 6, 16, 0f);
        Fill(pixels, 128, 40, 8, 5, 16, 0f);

        IReadOnlyList<OcrRecognition> results = await recognizer.RecognizeBatchAsync(
            [Crop("exact-v5-cpu", pixels)],
            CancellationToken.None);

        Assert.HasCount(1, results);
        Assert.IsNull(results[0].Failure);
        Assert.IsGreaterThan(0, results[0].InferenceMilliseconds);
        TestContext.WriteLine($"cpu_inference_ms={results[0].InferenceMilliseconds:R}");
        TestContext.WriteLine(
            "decoded=" + string.Join('|', results[0].Alternatives.Select(static value => value.Text)));
    }

    private static void Fill(
        float[] pixels,
        int stride,
        int left,
        int top,
        int width,
        int height,
        float value)
    {
        for (var y = top; y < top + height; y++)
        {
            for (var x = left; x < left + width; x++)
            {
                pixels[(y * stride) + x] = value;
            }
        }
    }

    private static OcrCrop Crop(
        string id,
        float[] pixels,
        OcrSourceImage sourceImage = OcrSourceImage.Original) =>
        new(
            id,
            sourceImage,
            128,
            32,
            pixels,
            Convert.ToHexStringLower(SHA256.HashData(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()))),
            OcrPolygon.FromRectangle(new OcrRectangle(0, 0, 128, 32)));

    private static async Task<ModelIdentity> CreateModelAsync(string directory)
    {
        string modelPath = Path.Combine(directory, "component.onnx");
        await File.WriteAllBytesAsync(modelPath, [7, 4, 4, 2]);
        return new ModelIdentity(
            "graph-numeric-component-ensemble-v5",
            "0.1.0",
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(modelPath))),
            modelPath);
    }

    private static InferenceRuntime CreateRuntime(
        string directory,
        IInferenceSessionFactory factory)
    {
        var registry = new OnnxSessionRegistry(
            new FakeExecutionProviderDiscovery("CPUExecutionProvider"),
            new WindowsExecutionProviderPolicy(),
            factory,
            CpuThreadConfiguration.Create(1));
        return new InferenceRuntime(
            registry,
            new BoundedInferenceScheduler(capacity: 2, workerCount: 1),
            new ContentAddressedStageCache(Path.Combine(directory, "cache")));
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "GraphReaderComponentOcrTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GraphAutoReader.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Graph Auto Reader repository root.");
    }

    private sealed class ComponentLogitSessionFactory : IInferenceSessionFactory
    {
        private readonly int[] classes;

        public ComponentLogitSessionFactory(int[] classes)
        {
            this.classes = classes;
        }

        public int CreatedCount { get; private set; }

        public ComponentLogitSession Session { get; private set; } = null!;

        public ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreatedCount++;
            Session = new ComponentLogitSession(provider, classes);
            return ValueTask.FromResult<IInferenceSession>(Session);
        }
    }

    private sealed class ComponentLogitSession : IInferenceSession
    {
        private readonly int[] classes;

        public ComponentLogitSession(InferenceProvider provider, int[] classes)
        {
            Provider = provider;
            this.classes = classes;
        }

        public InferenceProvider Provider { get; }

        public int RunCount { get; private set; }

        public ReadOnlyCollection<float> LastInputValues { get; private set; } = Array.AsReadOnly(Array.Empty<float>());

        public IReadOnlyList<long> LastInputShape { get; private set; } = [];

        public ValueTask<InferenceExecution> RunAsync(
            InferenceInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunCount++;
            LastInputValues = Array.AsReadOnly(input.Values.ToArray());
            LastInputShape = Array.AsReadOnly(input.Shape.ToArray());
            int glyphCount = checked((int)input.Shape[0]);
            if (classes.Length == 0)
            {
                return ValueTask.FromResult(Execution([0f]));
            }

            Assert.AreEqual(glyphCount, classes.Length);
            var output = Enumerable.Repeat(-20f, glyphCount * 14).ToArray();
            for (var glyph = 0; glyph < glyphCount; glyph++)
            {
                output[(glyph * 14) + classes[glyph]] = 20f;
            }

            return ValueTask.FromResult(Execution(output));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private InferenceExecution Execution(float[] output) =>
            new(
                Array.AsReadOnly(output),
                Provider,
                new StageTiming(0, 0.1, 0, 0.1, 0, RunCount == 1, false),
                new MemoryDiagnostics(0, 0, 0, 0, output.Length));
    }

    private sealed class CpuOnlyDiscovery : IExecutionProviderDiscovery
    {
        public IReadOnlyList<string> GetAvailableProviders() => ["CPUExecutionProvider"];
    }

    private sealed class SingleCoreDetector : IPhysicalCoreDetector
    {
        public int GetPhysicalCoreCount() => 1;
    }

    private sealed class NoOpStageCache : IStageCache
    {
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<byte[]?>(null);
        }

        public ValueTask PutAsync(
            StageCacheKey key,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
