// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.Inference;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class LocalOnnxTiledProbabilityTextRegionDetectorTests
{
    [TestMethod]
    public async Task DualInputUsesOriginalGray8AndMatchesPinnedPythonTileTensor()
    {
        string directory = CreateDirectory();
        try
        {
            const int width = 300;
            const int height = 200;
            byte[] originalPixels = Pattern(width, height);
            byte[] maskedPixels = Enumerable.Repeat((byte)255, width * height).ToArray();
            var factory = new TiledLogitSessionFactory(static input =>
                Enumerable.Repeat(-80f, input.Values.Length).ToArray());
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTiledProbabilityTextRegionDetector(
                runtime,
                Options(Identity(directory)));

            IReadOnlyList<OcrDetectedRegion> regions = await detector.DetectAsync(
                Image(width, height, originalPixels),
                Image(width, height, maskedPixels),
                CancellationToken.None);

            Assert.IsEmpty(regions);
            Assert.IsNotNull(factory.LastInput);
            InferenceInput input = factory.LastInput ?? throw new AssertFailedException("Inference input was not captured.");
            CollectionAssert.AreEqual(new long[] { 2, 1, 256, 256 }, input.Shape.ToArray());
            Assert.AreEqual("source_tiles", input.InputName);
            Assert.AreEqual("text_logits", input.OutputName);
            Assert.AreEqual(
                "c88e47379daf7a791aca022436e757fda48ec3da6a4d5940dd1825df79447e86",
                TensorSha256(input.Values.Span));
            Assert.AreEqual(1f - (originalPixels[0] / 255f), input.Values.Span[0], 0f);
            Assert.AreEqual(0f, input.Values.Span[200 * 256], 0f);
            Assert.AreEqual(
                1f - (originalPixels[(199 * width) + 299] / 255f),
                input.Values.Span[(256 * 256) + (199 * 256) + 255],
                0f);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task StitchingAveragesValidOverlapsThenAppliesGlobalCloseAndComponents()
    {
        string directory = CreateDirectory();
        try
        {
            const int width = 300;
            const int height = 260;
            var factory = new TiledLogitSessionFactory(input => BuildLogits(
                input,
                width,
                height,
                static (tileLeft, _, x, y) =>
                {
                    if (x is >= 198 and < 205 && y is >= 100 and < 104)
                    {
                        return tileLeft == 0 ? 0.2f : 0.8f;
                    }

                    if (x is >= 30 and < 35 && x != 32 && y is >= 20 and < 24)
                    {
                        return 0.9f;
                    }

                    if (x is >= 0 and < 2 && y is >= 0 and < 4)
                    {
                        return 0.9f;
                    }

                    if (x is >= 10 and < 12 && y is >= 10 and < 12)
                    {
                        return 0.9f;
                    }

                    return 0f;
                }));
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTiledProbabilityTextRegionDetector(
                runtime,
                Options(Identity(directory)));

            IReadOnlyList<OcrDetectedRegion> regions = await detector.DetectAsync(
                Image(width, height, new byte[width * height]),
                CancellationToken.None);

            CollectionAssert.AreEqual(
                new[]
                {
                    new OcrRectangle(0, 0, 2, 4),
                    new OcrRectangle(30, 20, 5, 4),
                    new OcrRectangle(198, 100, 7, 4),
                },
                regions.Select(static region => region.Polygon.Bounds).ToArray());
            OcrDetectedRegion overlapRegion = regions.Single(static region => region.Polygon.Bounds.X == 198);
            Assert.AreEqual(0.5d, overlapRegion.DetectionConfidence, 0.000001d);
            Assert.IsTrue(regions.All(static region =>
                region.Evidence?.Reasons.Contains("onnx_tiled_probability_text") == true));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task MapsHalfOpenComponentBoundsThroughOriginalTransform()
    {
        string directory = CreateDirectory();
        try
        {
            const int width = 300;
            const int height = 260;
            var factory = new TiledLogitSessionFactory(input => BuildLogits(
                input,
                width,
                height,
                static (_, _, x, y) =>
                    x is >= 30 and < 38 && y is >= 40 and < 44 ? 0.9f : 0f));
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTiledProbabilityTextRegionDetector(
                runtime,
                Options(Identity(directory)));
            OcrImage image = Image(width, height, new byte[width * height]) with
            {
                OriginalToImage = new OcrFrameTransform(2, 2, 10, 20),
                CanonicalOriginalWidth = 145,
                CanonicalOriginalHeight = 120,
            };

            OcrDetectedRegion region = AssertExactlyOne(
                await detector.DetectAsync(image, CancellationToken.None));

            Assert.AreEqual(new OcrRectangle(10, 10, 4, 2), region.Polygon.Bounds);
            Assert.AreEqual(OcrContract.CoordinateSpace, region.CoordinateSpace);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RejectsMisalignedDerivativeBeforeInference()
    {
        string directory = CreateDirectory();
        try
        {
            var factory = new TiledLogitSessionFactory(static input => new float[input.Values.Length]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTiledProbabilityTextRegionDetector(
                runtime,
                Options(Identity(directory)));
            OcrImage original = Image(32, 24, new byte[32 * 24]);
            OcrImage derivative = Image(31, 24, new byte[31 * 24]);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await detector.DetectAsync(original, derivative, CancellationToken.None));

            Assert.AreEqual(0, factory.RunCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RejectsUnboundedTileInventoryBeforeAllocatingInferenceTensor()
    {
        string directory = CreateDirectory();
        try
        {
            const int width = 50_000;
            var factory = new TiledLogitSessionFactory(static input => new float[input.Values.Length]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTiledProbabilityTextRegionDetector(
                runtime,
                Options(Identity(directory)));

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await detector.DetectAsync(Image(width, 1, new byte[width]), CancellationToken.None));

            Assert.AreEqual(0, factory.RunCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task CancellationBeforePreprocessingNeverRunsInference()
    {
        string directory = CreateDirectory();
        try
        {
            var factory = new TiledLogitSessionFactory(static input => new float[input.Values.Length]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTiledProbabilityTextRegionDetector(
                runtime,
                Options(Identity(directory)));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await detector.DetectAsync(
                    Image(300, 260, new byte[300 * 260]),
                    cancellation.Token));

            Assert.AreEqual(0, factory.RunCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task CancellationAfterInferenceDoesNotReturnPartiallyProcessedRegions()
    {
        string directory = CreateDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var factory = new TiledLogitSessionFactory(
                static input => Enumerable.Repeat(2f, input.Values.Length).ToArray(),
                cancellation.Cancel);
            await using InferenceRuntime runtime = CreateRuntime(
                directory,
                factory,
                new NonCancellingStageCache());
            var detector = new LocalOnnxTiledProbabilityTextRegionDetector(
                runtime,
                Options(Identity(directory)));

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await detector.DetectAsync(
                    Image(300, 260, new byte[300 * 260]),
                    cancellation.Token));

            Assert.AreEqual(1, factory.RunCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task InvalidLogitOutputFailsClosed()
    {
        string directory = CreateDirectory();
        try
        {
            var factory = new TiledLogitSessionFactory(static input =>
            {
                var output = new float[input.Values.Length];
                output[0] = float.NaN;
                return output;
            });
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTiledProbabilityTextRegionDetector(
                runtime,
                Options(Identity(directory)));

            InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await detector.DetectAsync(Image(16, 16, new byte[16 * 16]), CancellationToken.None));

            StringAssert.Contains(exception.Message, "non-finite logit");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static LocalOnnxTiledProbabilityTextRegionDetectorOptions Options(ModelIdentity model) => new(model)
    {
        AllowedProviders = [InferenceProvider.Cpu],
        BypassCache = true,
    };

    private static ModelIdentity Identity(string directory)
    {
        string path = Path.Combine(directory, "fixture-v38.onnx");
        File.WriteAllBytes(path, [3, 8, 3, 7]);
        return new ModelIdentity(
            "fixture-v38-tiled-text",
            "38.0.0",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
            path);
    }

    private static OcrImage Image(int width, int height, byte[] pixels) => new(
        width,
        height,
        width,
        pixels,
        OcrSourceImage.Original,
        OcrFrameTransform.Identity,
        CanonicalOriginalWidth: width,
        CanonicalOriginalHeight: height);

    private static byte[] Pattern(int width, int height)
    {
        var pixels = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = (byte)((x * 17 + y * 29 + 3) % 256);
            }
        }

        return pixels;
    }

    private static float[] BuildLogits(
        InferenceInput input,
        int imageWidth,
        int imageHeight,
        Func<int, int, int, int, float> probability)
    {
        int[] lefts = TileStarts(imageWidth);
        int[] tops = TileStarts(imageHeight);
        var logits = Enumerable.Repeat(-80f, input.Values.Length).ToArray();
        var tileIndex = 0;
        foreach (int top in tops)
        {
            foreach (int left in lefts)
            {
                int validWidth = Math.Min(256, imageWidth - left);
                int validHeight = Math.Min(256, imageHeight - top);
                for (var y = 0; y < validHeight; y++)
                {
                    for (var x = 0; x < validWidth; x++)
                    {
                        float value = probability(left, top, left + x, top + y);
                        if (value > 0)
                        {
                            logits[(tileIndex * 256 * 256) + (y * 256) + x] =
                                MathF.Log(value / (1f - value));
                        }
                    }
                }

                tileIndex++;
            }
        }

        return logits;
    }

    private static int[] TileStarts(int length)
    {
        if (length <= 256)
        {
            return [0];
        }

        var starts = new List<int>();
        for (var start = 0; start < length - 256 + 1; start += 192)
        {
            starts.Add(start);
        }

        int final = length - 256;
        if (starts[^1] != final)
        {
            starts.Add(final);
        }

        return starts.ToArray();
    }

    private static string TensorSha256(ReadOnlySpan<float> values)
    {
        var bytes = new byte[checked(values.Length * sizeof(float))];
        Buffer.BlockCopy(values.ToArray(), 0, bytes, 0, bytes.Length);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static OcrDetectedRegion AssertExactlyOne(IReadOnlyList<OcrDetectedRegion> regions)
    {
        Assert.HasCount(1, regions);
        return regions[0];
    }

    private static InferenceRuntime CreateRuntime(
        string directory,
        TiledLogitSessionFactory factory,
        IStageCache? cache = null)
    {
        var registry = new OnnxSessionRegistry(
            new FakeExecutionProviderDiscovery("CPUExecutionProvider"),
            new WindowsExecutionProviderPolicy(),
            factory,
            CpuThreadConfiguration.Create(1));
        return new InferenceRuntime(
            registry,
            new BoundedInferenceScheduler(capacity: 2, workerCount: 1),
            cache ?? new ContentAddressedStageCache(Path.Combine(directory, "cache")));
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "GraphReaderTiledOcrDetectorTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TiledLogitSessionFactory(
        Func<InferenceInput, float[]> outputFactory,
        Action? afterRun = null) : IInferenceSessionFactory
    {
        private readonly Func<InferenceInput, float[]> outputFactory = outputFactory;
        private readonly Action? afterRun = afterRun;

        public InferenceInput? LastInput { get; private set; }

        public int RunCount { get; private set; }

        public ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IInferenceSession>(new Session(this, provider));
        }

        private sealed class Session(
            TiledLogitSessionFactory owner,
            InferenceProvider provider) : IInferenceSession
        {
            public InferenceProvider Provider { get; } = provider;

            public ValueTask<InferenceExecution> RunAsync(
                InferenceInput input,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.LastInput = new InferenceInput(
                    input.Values.ToArray(),
                    input.Shape.ToArray(),
                    input.InputName,
                    input.OutputName);
                owner.RunCount++;
                float[] output = owner.outputFactory(input);
                owner.afterRun?.Invoke();
                return ValueTask.FromResult(new InferenceExecution(
                    Array.AsReadOnly(output),
                    Provider,
                    new StageTiming(0, 1, 0, 1, 0, owner.RunCount == 1, false),
                    new MemoryDiagnostics(0, 0, 0, 0, output.Length)));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NonCancellingStageCache : IStageCache
    {
        public ValueTask<byte[]?> TryGetAsync(
            StageCacheKey key,
            CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);

        public ValueTask PutAsync(
            StageCacheKey key,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
