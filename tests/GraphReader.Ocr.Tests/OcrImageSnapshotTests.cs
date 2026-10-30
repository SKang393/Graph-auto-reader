// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class OcrImageSnapshotTests
{
    [TestMethod]
    public async Task ImageCoordinateSpaceIsValidatedBeforeWarmOrColdCacheAndBindsAlias()
    {
        var detector = new StubTextRegionDetector([]);
        var recognizer = new StubTextRecognizer((_, _) =>
            ValueTask.FromResult<IReadOnlyList<OcrRecognition>>([]));
        var options = new OcrPipelineOptions();
        OcrRequest request = OcrTestFixtures.Request(enhanced: OcrTestFixtures.Image(OcrSourceImage.Enhanced));
        OcrRequest[] invalid =
        [
            request with { OriginalImage = request.OriginalImage with { CoordinateSpace = "derived" } },
            request with { EnhancedImage = request.EnhancedImage! with { CoordinateSpace = "derived" } },
        ];
        var warm = new OcrPipeline(detector, recognizer, new MemoryOcrResultCache(), options);
        Assert.IsTrue((await warm.RecognizeAsync(request)).Succeeded);
        Assert.IsTrue((await warm.RecognizeAsync(request)).Cache.CacheHit);
        string validAlias = OcrCacheKeyDeriver.CreateRequestAlias(request, recognizer, options, ((ITextRegionDetector)detector).ConfigurationFingerprint);
        foreach (OcrRequest candidate in invalid)
        {
            string invalidAlias = OcrCacheKeyDeriver.CreateRequestAlias(candidate, recognizer, options, ((ITextRegionDetector)detector).ConfigurationFingerprint);
            Assert.AreNotEqual(validAlias, invalidAlias);
            OcrResult warmResult = await warm.RecognizeAsync(candidate);
            var cold = new OcrPipeline(detector, recognizer, new MemoryOcrResultCache(), options);
            OcrResult coldResult = await cold.RecognizeAsync(candidate);
            Assert.AreEqual("OCR_INPUT_INVALID", warmResult.Failure?.Code);
            Assert.AreEqual("OCR_INPUT_INVALID", coldResult.Failure?.Code);
        }
    }

    [TestMethod]
    public async Task InvalidOriginalOrEnhancedTransformIsRejectedEvenForEmptySuppliedRegions()
    {
        var detector = new StubTextRegionDetector([]);
        var recognizer = new StubTextRecognizer((_, _) => ValueTask.FromResult<IReadOnlyList<OcrRecognition>>([]));
        var pipeline = new OcrPipeline(detector, recognizer, new MemoryOcrResultCache());
        OcrRequest request = OcrTestFixtures.Request([], enhanced: OcrTestFixtures.Image(OcrSourceImage.Enhanced));
        OcrFrameTransform invalid = new(0, 1, 0, 0);
        OcrRequest[] candidates =
        [
            request with { OriginalImage = request.OriginalImage with { OriginalToImage = invalid } },
            request with { EnhancedImage = request.EnhancedImage! with { OriginalToImage = invalid } },
        ];
        foreach (OcrRequest candidate in candidates)
            Assert.AreEqual("OCR_INPUT_INVALID", (await pipeline.RecognizeAsync(candidate)).Failure?.Code);
    }

    [TestMethod]
    public async Task MutablePlanesCannotChangeAliasDetectorOrRecognitionDuringCacheLookup()
    {
        const int width = 160, height = 100;
        byte[] originalGray = Filled(width * height, 10);
        byte[] originalBgr = Filled(width * height * 3, 20);
        byte[] enhancedGray = Filled(width * height, 30);
        byte[] enhancedBgr = Filled(width * height * 3, 40);
        byte[] detectorGray = Filled(width * height, 50);
        byte[] detectorBgr = Filled(width * height * 3, 60);
        OcrRequest request = MakeRequest(originalGray, originalBgr, enhancedGray, enhancedBgr, detectorGray, detectorBgr);
        OcrRequest originalCopy = MakeRequest(
            (byte[])originalGray.Clone(), (byte[])originalBgr.Clone(),
            (byte[])enhancedGray.Clone(), (byte[])enhancedBgr.Clone(),
            (byte[])detectorGray.Clone(), (byte[])detectorBgr.Clone());
        var cache = new BlockingCache();
        var detector = new StubTextRegionDetector((image, _) =>
        {
            Assert.AreEqual(50, image.Pixels.Span[0]);
            Assert.AreEqual(60, image.BgrPixels!.Pixels.Span[0]);
            return ValueTask.FromResult<IReadOnlyList<OcrDetectedRegion>>(
                [OcrTestFixtures.Region("region", 40, 30, 12, 8)]);
        });
        var cropsSeen = new List<(OcrSourceImage Source, float Gray, float Bgr)>();
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            foreach (OcrCrop crop in crops)
                cropsSeen.Add((crop.SourceImage, crop.Pixels.Span[0], crop.BgrPixels!.Pixels.Span[0]));
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(
                crops.Select(static crop => new OcrRecognition(crop.RegionId, crop.SourceImage,
                    [new OcrRecognitionAlternative("10", 0.9, crop.SourceImage)], 0)).ToArray());
        });
        var pipeline = new OcrPipeline(detector, recognizer, cache);

        Task<OcrResult> firstTask = pipeline.RecognizeAsync(request).AsTask();
        await cache.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Array.Fill(originalGray, (byte)100);
        Array.Fill(originalBgr, (byte)110);
        Array.Fill(enhancedGray, (byte)120);
        Array.Fill(enhancedBgr, (byte)130);
        Array.Fill(detectorGray, (byte)140);
        Array.Fill(detectorBgr, (byte)150);
        cache.Release.TrySetResult();
        OcrResult first = await firstTask;
        Assert.IsTrue(first.Succeeded, first.Failure?.TechnicalMessage);
        Assert.HasCount(2, cropsSeen);
        Assert.AreEqual(10f / 255, cropsSeen.Single(x => x.Source == OcrSourceImage.Original).Gray, 1e-6f);
        Assert.AreEqual(20f / 255, cropsSeen.Single(x => x.Source == OcrSourceImage.Original).Bgr, 1e-6f);
        Assert.AreEqual(30f / 255, cropsSeen.Single(x => x.Source == OcrSourceImage.Enhanced).Gray, 1e-6f);
        Assert.AreEqual(40f / 255, cropsSeen.Single(x => x.Source == OcrSourceImage.Enhanced).Bgr, 1e-6f);
        OcrResult cached = await pipeline.RecognizeAsync(originalCopy);
        Assert.IsTrue(cached.Cache.CacheHit, "The completed alias must correspond to the original copied bytes.");
        OcrResult tamperedDetector = await pipeline.RecognizeAsync(request);
        Assert.IsFalse(tamperedDetector.Succeeded);
        Assert.AreEqual("OCR_INPUT_INVALID", tamperedDetector.Failure?.Code);
        Array.Fill(detectorGray, (byte)50);
        Array.Fill(detectorBgr, (byte)60);
        OcrResult changed = await pipeline.RecognizeAsync(request);
        Assert.IsTrue(changed.Succeeded, changed.Failure?.TechnicalMessage);
        Assert.IsFalse(changed.Cache.CacheHit, "Changed recognition pixels need their own alias.");
        Assert.HasCount(4, cropsSeen);
        Assert.AreEqual(100f / 255, cropsSeen.Last(x => x.Source == OcrSourceImage.Original).Gray, 1e-6f);
        Assert.AreEqual(120f / 255, cropsSeen.Last(x => x.Source == OcrSourceImage.Enhanced).Gray, 1e-6f);
    }

    [TestMethod]
    public void SharedPlaneSegmentsAreCopiedOnlyOnceAndTrailingGrayBytesArePreserved()
    {
        byte[] gray = Filled(160 * 100 + 8, 12);
        byte[] bgr = Filled(160 * 100 * 3, 13);
        OcrRequest request = OcrTestFixtures.Request() with
        {
            OriginalImage = OcrTestFixtures.Image() with { Pixels = gray, BgrPixels = new(160 * 3, bgr) },
            EnhancedImage = OcrTestFixtures.Image(OcrSourceImage.Enhanced) with { Pixels = gray, BgrPixels = new(160 * 3, bgr) },
        };
        OcrRequest frozen = OcrRequestImageSnapshot.Create(request, CancellationToken.None).Request;
        Assert.IsTrue(frozen.OriginalImage.Pixels.Equals(frozen.EnhancedImage!.Pixels));
        Assert.IsTrue(frozen.OriginalImage.BgrPixels!.Pixels.Equals(frozen.EnhancedImage.BgrPixels!.Pixels));
        Assert.AreEqual(gray.Length, frozen.OriginalImage.Pixels.Length);
        gray[^1] = 99;
        Assert.AreEqual(12, frozen.OriginalImage.Pixels.Span[^1]);
    }

    [TestMethod]
    public async Task DualRecognizerUsesTheSameCopiedPixelsBeforeAndAfterDetectorAwait()
    {
        byte[] originalGray = Filled(160 * 100, 10);
        byte[] originalBgr = Filled(160 * 100 * 3, 20);
        byte[] enhancedGray = Filled(160 * 100, 30);
        byte[] enhancedBgr = Filled(160 * 100 * 3, 40);
        byte[] detectorGray = Filled(160 * 100, 50);
        byte[] detectorBgr = Filled(160 * 100 * 3, 60);
        OcrRequest request = MakeRequest(originalGray, originalBgr, enhancedGray, enhancedBgr, detectorGray, detectorBgr);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new StubTextRegionDetector(async (image, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            Assert.AreEqual(50, image.Pixels.Span[0]);
            Assert.AreEqual(60, image.BgrPixels!.Pixels.Span[0]);
            return [OcrTestFixtures.Region("region", 40, 30, 12, 8)];
        });
        var seen = new List<(OcrSourceImage Source, float Gray, float Bgr)>();
        StubTextRecognizer Recognizer() => new((crops, _) =>
        {
            foreach (OcrCrop crop in crops)
                seen.Add((crop.SourceImage, crop.Pixels.Span[0], crop.BgrPixels!.Pixels.Span[0]));
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(
                crops.Select(static crop => new OcrRecognition(crop.RegionId, crop.SourceImage,
                    [new OcrRecognitionAlternative("10", 0.9, crop.SourceImage)], 0)).ToArray());
        });
        var pipeline = new OcrDualRecognizerPipeline(detector, Recognizer(), new MemoryOcrResultCache(),
            new OcrPipelineOptions(), Recognizer(), new MemoryOcrResultCache(), new OcrPipelineOptions());
        Task<OcrResult> pending = pipeline.RecognizeAsync(request).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Array.Fill(originalGray, (byte)100);
        Array.Fill(originalBgr, (byte)110);
        Array.Fill(enhancedGray, (byte)120);
        Array.Fill(enhancedBgr, (byte)130);
        Array.Fill(detectorGray, (byte)140);
        Array.Fill(detectorBgr, (byte)150);
        release.TrySetResult();
        OcrResult result = await pending;
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(4, seen);
        Assert.IsTrue(seen.Where(x => x.Source == OcrSourceImage.Original)
            .All(x => Math.Abs(x.Gray - 10f / 255) < 1e-6 && Math.Abs(x.Bgr - 20f / 255) < 1e-6));
        Assert.IsTrue(seen.Where(x => x.Source == OcrSourceImage.Enhanced)
            .All(x => Math.Abs(x.Gray - 30f / 255) < 1e-6 && Math.Abs(x.Bgr - 40f / 255) < 1e-6));
    }

    [TestMethod]
    public async Task InvalidImagesAndAllocationFailureReturnFailureAndCancellationPropagates()
    {
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), new StubTextRecognizer((_, _) =>
            ValueTask.FromResult<IReadOnlyList<OcrRecognition>>([])), new MemoryOcrResultCache());
        OcrRequest request = OcrTestFixtures.Request();
        OcrResult missing = await pipeline.RecognizeAsync(request with { OriginalImage = null! });
        Assert.IsFalse(missing.Succeeded);
        Assert.AreEqual("OCR_INPUT_INVALID", missing.Failure?.Code);
        OcrResult missingHash = await pipeline.RecognizeAsync(request with { InputSha256 = null! });
        Assert.IsFalse(missingHash.Succeeded);
        Assert.AreEqual("OCR_INPUT_INVALID", missingHash.Failure?.Code);
        OcrResult invalid = await pipeline.RecognizeAsync(request with
        {
            OriginalImage = request.OriginalImage with { Stride = int.MaxValue },
        });
        Assert.IsFalse(invalid.Succeeded);
        Assert.AreEqual("OCR_INPUT_INVALID", invalid.Failure?.Code);

        using var owner = new FailingMemoryManager(160 * 100);
        ReadOnlyMemory<byte> bytes = owner.Memory;
        owner.FailReads = true;
        OcrResult noMemory = await pipeline.RecognizeAsync(request with
        {
            OriginalImage = request.OriginalImage with { Pixels = bytes },
        });
        Assert.IsFalse(noMemory.Succeeded);
        Assert.AreEqual("OCR_INPUT_INVALID", noMemory.Failure?.Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await pipeline.RecognizeAsync(request, cancellation.Token));
        using var copyingCancellation = new CancellationTokenSource();
        using var copyingOwner = new FailingMemoryManager(5_000_000);
        ReadOnlyMemory<byte> copyingBytes = copyingOwner.Memory;
        copyingOwner.CancelOnRead = copyingCancellation;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await pipeline.RecognizeAsync(request with
            {
                OriginalImage = request.OriginalImage with
                {
                    Width = 5_000, Height = 1_000, Stride = 5_000, Pixels = copyingBytes, BgrPixels = null,
                },
            }, copyingCancellation.Token));
        using var geometryCancellation = new CancellationTokenSource();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await pipeline.RecognizeAsync(request with
            {
                PhaseDividerXs = new CancellingDividers(geometryCancellation),
            }, geometryCancellation.Token));
    }

    private static byte[] Filled(int length, byte value)
    {
        byte[] result = new byte[length];
        Array.Fill(result, value);
        return result;
    }

    private static OcrRequest MakeRequest(byte[] originalGray, byte[] originalBgr,
        byte[] enhancedGray, byte[] enhancedBgr, byte[] detectorGray, byte[] detectorBgr)
    {
        OcrImage original = OcrTestFixtures.Image() with
        {
            Pixels = originalGray, BgrPixels = new(160 * 3, originalBgr),
        };
        OcrImage enhanced = OcrTestFixtures.Image(OcrSourceImage.Enhanced) with
        {
            Pixels = enhancedGray, BgrPixels = new(160 * 3, enhancedBgr),
        };
        OcrImage detector = original with { Pixels = detectorGray, BgrPixels = new(160 * 3, detectorBgr) };
        return OcrTestFixtures.Request(enhanced: enhanced) with
        {
            OriginalImage = original,
            DetectorImage = new OcrDetectorImage(detector,
                Convert.ToHexStringLower(SHA256.HashData(detectorGray)),
                Convert.ToHexStringLower(SHA256.HashData(detectorBgr))),
        };
    }

    private sealed class BlockingCache : IOcrResultCache
    {
        private readonly MemoryOcrResultCache inner = new();
        private int first = 1;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<OcrCachedPayload?> TryGetAsync(string key, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref first, 0) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await inner.TryGetAsync(key, cancellationToken);
        }
        public ValueTask PutAsync(string key, OcrCachedPayload payload, CancellationToken cancellationToken) =>
            inner.PutAsync(key, payload, cancellationToken);
    }

    private sealed class FailingMemoryManager(int length) : MemoryManager<byte>
    {
        private readonly byte[] bytes = new byte[length];
        internal bool FailReads { get; set; }
        internal CancellationTokenSource? CancelOnRead { get; set; }
        public override Span<byte> GetSpan()
        {
            if (FailReads) throw new InsufficientMemoryException("Fixture");
            CancelOnRead?.Cancel();
            return bytes;
        }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    private sealed class CancellingDividers(CancellationTokenSource cancellation) : IReadOnlyList<double>
    {
        public int Count => 1;
        public double this[int index] => index == 0 ? 1 : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<double> GetEnumerator()
        {
            cancellation.Cancel();
            yield return 1;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
