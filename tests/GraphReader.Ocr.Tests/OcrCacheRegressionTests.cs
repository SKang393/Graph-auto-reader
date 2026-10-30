// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections;
using System.Security.Cryptography;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class OcrCacheRegressionTests
{
    private static readonly string[] ExpectedWarnings = ["initial_warning"];

    [TestMethod]
    public async Task PublicMemoryCacheDefensivelyFreezesCallerOwnedCollections()
    {
        var regions = new List<OcrRegion>
        {
            Region("first", "10"),
        };
        var masks = new List<OcrMask>
        {
            new(
                "first",
                OcrPolygon.FromRectangle(new OcrRectangle(10, 10, 12, 6)),
                0.9),
        };
        var warnings = new List<string> { "initial_warning" };
        var payload = new OcrCachedPayload(regions, masks, 0.9, warnings, 1, 1, "content-key");
        var cache = new MemoryOcrResultCache();

        await cache.PutAsync("cache-key", payload, CancellationToken.None);
        regions.Add(Region("injected", "999"));
        masks.Clear();
        warnings[0] = "mutated_warning";
        OcrCachedPayload? restored = await cache.TryGetAsync("cache-key", CancellationToken.None);

        Assert.IsNotNull(restored);
        Assert.HasCount(1, restored.Regions);
        Assert.AreEqual("first", restored.Regions[0].RegionId);
        Assert.HasCount(1, restored.Masks);
        CollectionAssert.AreEqual(ExpectedWarnings, restored.Warnings.ToArray());
        Assert.AreNotSame(payload, restored);
    }

    [TestMethod]
    public async Task PublicRecognitionCacheDefensivelyFreezesCallerOwnedAlternatives()
    {
        var alternatives = new List<OcrRecognitionAlternative>
        {
            new("10", 0.9, OcrSourceImage.Original),
        };
        var recognitions = new List<OcrRecognition>
        {
            new("first", OcrSourceImage.Original, alternatives, 0.1),
        };
        IOcrResultCache cache = new MemoryOcrResultCache();

        await cache.PutRecognitionAsync(
            "recognition-key",
            new OcrRecognitionCachePayload(recognitions),
            CancellationToken.None);
        alternatives.Add(new OcrRecognitionAlternative("100", 0.99, OcrSourceImage.Original));
        recognitions.Clear();
        OcrRecognitionCachePayload? restored = await cache.TryGetRecognitionAsync(
            "recognition-key",
            CancellationToken.None);

        Assert.IsNotNull(restored);
        Assert.HasCount(1, restored.Recognitions);
        Assert.HasCount(1, restored.Recognitions[0].Alternatives);
        Assert.AreEqual("10", restored.Recognitions[0].Alternatives[0].Text);
    }

    [TestMethod]
    public void HollowMarkerRepairDetectorAndStageRevisionInvalidatePriorCacheKeys()
    {
        var detector = new ConnectedComponentTextRegionDetector();
        var recognizer = new StubTextRecognizer(
            new Dictionary<(string RegionId, OcrSourceImage Source), IReadOnlyList<OcrRecognitionAlternative>>());
        var options = new OcrPipelineOptions();
        OcrRequest request = OcrTestFixtures.Request([]);
        const string priorDetectorFingerprint = "cc-v2:2:0.15:0.2:2.5:0.35:auto";

        string repairedKey = OcrCacheKeyDeriver.CreateRequestAlias(
            request,
            recognizer,
            options,
            detector.ConfigurationFingerprint);
        string priorDetectorKey = OcrCacheKeyDeriver.CreateRequestAlias(
            request,
            recognizer,
            options,
            priorDetectorFingerprint);
        string priorStageKey = OcrCacheKeyDeriver.CreateRequestAlias(
            request,
            recognizer,
            options with { StageVersion = "0.2.0" },
            detector.ConfigurationFingerprint);

        Assert.IsTrue(detector.ConfigurationFingerprint.StartsWith("cc-v3:", StringComparison.Ordinal));
        Assert.AreEqual("0.3.0", options.StageVersion);
        Assert.AreNotEqual(priorDetectorKey, repairedKey);
        Assert.AreNotEqual(priorStageKey, repairedKey);
    }

    [TestMethod]
    public void DetectorOnlyDerivativeChecksumInvalidatesRequestAlias()
    {
        var recognizer = new StubTextRecognizer(
            new Dictionary<(string RegionId, OcrSourceImage Source), IReadOnlyList<OcrRecognitionAlternative>>());
        OcrRequest baseline = OcrTestFixtures.Request();
        OcrImage firstImage = baseline.OriginalImage with
        {
            Pixels = new byte[checked(baseline.OriginalImage.Width * baseline.OriginalImage.Height)],
        };
        OcrImage secondImage = firstImage with
        {
            Pixels = Enumerable.Repeat(
                (byte)1,
                checked(firstImage.Width * firstImage.Height)).ToArray(),
        };
        OcrRequest first = baseline with
        {
            DetectorImage = DetectorImage(firstImage),
        };
        OcrRequest second = baseline with
        {
            DetectorImage = DetectorImage(secondImage),
        };

        string firstKey = OcrCacheKeyDeriver.CreateRequestAlias(
            first,
            recognizer,
            new OcrPipelineOptions(),
            "detector-v1");
        string secondKey = OcrCacheKeyDeriver.CreateRequestAlias(
            second,
            recognizer,
            new OcrPipelineOptions(),
            "detector-v1");

        Assert.AreNotEqual(firstKey, secondKey);
    }

    [TestMethod]
    public void ColorOnlyChangeInvalidatesRequestAlias()
    {
        var recognizer = new StubTextRecognizer(
            new Dictionary<(string RegionId, OcrSourceImage Source), IReadOnlyList<OcrRecognitionAlternative>>());
        OcrRequest baseline = OcrTestFixtures.Request();
        int pixelCount = checked(baseline.OriginalImage.Width * baseline.OriginalImage.Height);
        OcrImage firstImage = baseline.OriginalImage with
        {
            BgrPixels = new OcrBgrBytePixels(
                baseline.OriginalImage.Width * 3,
                new byte[pixelCount * 3]),
        };
        byte[] changed = new byte[pixelCount * 3];
        changed[0] = 1;
        OcrImage secondImage = firstImage with
        {
            BgrPixels = new OcrBgrBytePixels(baseline.OriginalImage.Width * 3, changed),
        };
        OcrRequest first = baseline with { OriginalImage = firstImage };
        OcrRequest second = baseline with { OriginalImage = secondImage };

        string firstKey = OcrCacheKeyDeriver.CreateRequestAlias(
            first, recognizer, new OcrPipelineOptions(), "detector-v1");
        string secondKey = OcrCacheKeyDeriver.CreateRequestAlias(
            second, recognizer, new OcrPipelineOptions(), "detector-v1");

        Assert.AreNotEqual(firstKey, secondKey);
    }

    [TestMethod]
    public void ComponentCropCompositionInvalidatesRequestAlias()
    {
        var recognizer = new StubTextRecognizer(
            new Dictionary<(string RegionId, OcrSourceImage Source), IReadOnlyList<OcrRecognitionAlternative>>());
        OcrRequest request = OcrTestFixtures.Request();
        var baseline = new OcrPipelineOptions();
        var component = baseline with
        {
            CropVerticalContentPaddingRatio = 0.25,
            CropPaddingValue = 1f,
        };

        string baselineKey = OcrCacheKeyDeriver.CreateRequestAlias(
            request, recognizer, baseline, "component-detector-v1");
        string componentKey = OcrCacheKeyDeriver.CreateRequestAlias(
            request, recognizer, component, "component-detector-v1");

        Assert.AreNotEqual(baselineKey, componentKey);
    }

    [TestMethod]
    public void TallRegionOrientationInferencePolicyInvalidatesPriorCacheKeys()
    {
        var recognizer = new StubTextRecognizer(
            new Dictionary<(string RegionId, OcrSourceImage Source), IReadOnlyList<OcrRecognitionAlternative>>());
        OcrRequest request = OcrTestFixtures.Request();
        var inferred = new OcrPipelineOptions();
        OcrPipelineOptions detectorPreserved = inferred with
        {
            InferVerticalOrientationForTallRegions = false,
        };

        string inferredKey = OcrCacheKeyDeriver.CreateRequestAlias(
            request, recognizer, inferred, "detector-v1");
        string detectorPreservedKey = OcrCacheKeyDeriver.CreateRequestAlias(
            request, recognizer, detectorPreserved, "detector-v1");

        Assert.AreNotEqual(inferredKey, detectorPreservedKey);
    }

    [TestMethod]
    public void ParticipantLaneAssemblyInvalidatesDisabledRequestAlias()
    {
        var recognizer = new StubTextRecognizer(
            new Dictionary<(string RegionId, OcrSourceImage Source), IReadOnlyList<OcrRecognitionAlternative>>());
        OcrRequest request = OcrTestFixtures.Request();
        var baseline = new OcrPipelineOptions();
        OcrPipelineOptions assembled = baseline with { EnableParticipantLaneAssembly = true };

        string baselineKey = OcrCacheKeyDeriver.CreateRequestAlias(
            request, recognizer, baseline, "original-db-head-candidate-v1");
        string assembledKey = OcrCacheKeyDeriver.CreateRequestAlias(
            request, recognizer, assembled, "original-db-head-candidate-v1");
        string candidateKey = OcrCacheKeyDeriver.CreateRequestAlias(
            request, recognizer, assembled, "original-db-head-participant-lane-v1");

        Assert.AreNotEqual(baselineKey, assembledKey);
        Assert.AreNotEqual(assembledKey, candidateKey);
        Assert.AreEqual(
            baselineKey,
            OcrCacheKeyDeriver.CreateRequestAlias(
                request, recognizer, new OcrPipelineOptions(), "original-db-head-candidate-v1"));
    }

    [TestMethod]
    public void InsidePlotAssemblyAndDividerGeometrySeparateRequestAliases()
    {
        var recognizer = new StubTextRecognizer(
            new Dictionary<(string RegionId, OcrSourceImage Source), IReadOnlyList<OcrRecognitionAlternative>>());
        var enabled = new OcrPipelineOptions { EnableInsidePlotAssembly = true };
        OcrRequest unavailable = OcrTestFixtures.Request();
        OcrRequest measuredNone = unavailable with { PhaseDividerXs = Array.Empty<double>() };
        OcrRequest dividers = unavailable with { PhaseDividerXs = [90, 70] };
        OcrRequest reordered = unavailable with { PhaseDividerXs = [70, 90] };

        string disabledKey = OcrCacheKeyDeriver.CreateRequestAlias(
            unavailable, recognizer, new OcrPipelineOptions(), "detector-v1");
        string unavailableKey = OcrCacheKeyDeriver.CreateRequestAlias(
            unavailable, recognizer, enabled, "detector-v1");
        string measuredNoneKey = OcrCacheKeyDeriver.CreateRequestAlias(
            measuredNone, recognizer, enabled, "detector-v1");
        string dividerKey = OcrCacheKeyDeriver.CreateRequestAlias(
            dividers, recognizer, enabled, "detector-v1");
        string reorderedKey = OcrCacheKeyDeriver.CreateRequestAlias(
            reordered, recognizer, enabled, "detector-v1");

        Assert.AreNotEqual(disabledKey, unavailableKey);
        Assert.AreNotEqual(unavailableKey, measuredNoneKey);
        Assert.AreNotEqual(measuredNoneKey, dividerKey);
        Assert.AreEqual(dividerKey, reorderedKey);
        Assert.AreEqual(disabledKey, OcrCacheKeyDeriver.CreateRequestAlias(
            dividers, recognizer, new OcrPipelineOptions(), "detector-v1"));
    }

    [TestMethod]
    public void TickRecoveryAxisStrokeEvidenceSeparatesRequestAliasesWithoutOrderSensitivity()
    {
        var recognizer = new StubTextRecognizer(
            new Dictionary<(string RegionId, OcrSourceImage Source), IReadOnlyList<OcrRecognitionAlternative>>());
        var options = new OcrPipelineOptions { EnableTickLaneRecovery = true };
        OcrRequest unavailable = OcrTestFixtures.Request();
        OcrXAxisTickStroke first = Stroke("x-1", 30);
        OcrXAxisTickStroke second = Stroke("x-2", 60);
        OcrRequest seeded = unavailable with { XAxisTickStrokes = [first, second] };
        OcrRequest reordered = unavailable with { XAxisTickStrokes = [second, first] };
        OcrRequest moved = unavailable with { XAxisTickStrokes = [first with { Center = new OcrPoint(31, 40) }, second] };

        string unavailableKey = OcrCacheKeyDeriver.CreateRequestAlias(unavailable, recognizer, options, "detector-v1");
        string seededKey = OcrCacheKeyDeriver.CreateRequestAlias(seeded, recognizer, options, "detector-v1");
        string reorderedKey = OcrCacheKeyDeriver.CreateRequestAlias(reordered, recognizer, options, "detector-v1");
        string movedKey = OcrCacheKeyDeriver.CreateRequestAlias(moved, recognizer, options, "detector-v1");

        Assert.AreNotEqual(unavailableKey, seededKey);
        Assert.AreEqual(seededKey, reorderedKey);
        Assert.AreNotEqual(seededKey, movedKey);

        static OcrXAxisTickStroke Stroke(string id, double x) => new(
            id, new OcrPoint(x, 40), new OcrPoint(x, 39), new OcrPoint(x, 44), 0.9, [id]);
    }

    [TestMethod]
    public async Task WarmResultCacheCannotAcceptSuppliedRegionsInWrongCoordinateSpace()
    {
        var widths = new List<int>();
        var recognizer = WidthRecorder(widths);
        var cache = new InMemoryOcrResultCache();
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache);
        OcrDetectedRegion validRegion = OcrTestFixtures.Region("tick", 20, 40, 12, 8);
        OcrRequest validRequest = OcrTestFixtures.Request([validRegion]);

        OcrResult initial = await pipeline.RecognizeAsync(validRequest);
        OcrResult cached = await pipeline.RecognizeAsync(validRequest);
        OcrRequest invalidRequest = validRequest with
        {
            DetectedRegions = [validRegion with { CoordinateSpace = "enhanced_pixels" }],
        };
        int readsBeforeInvalidRequest = cache.ReadCount;
        OcrResult invalid = await pipeline.RecognizeAsync(invalidRequest);

        Assert.IsTrue(initial.Succeeded, initial.Failure?.TechnicalMessage);
        Assert.IsTrue(cached.Cache.CacheHit);
        Assert.IsFalse(invalid.Succeeded);
        Assert.AreEqual("OCR_REGION_DETECTION_FAILED", invalid.Failure?.Code);
        Assert.IsFalse(invalid.Cache.CacheHit);
        Assert.AreEqual(readsBeforeInvalidRequest, cache.ReadCount);
        Assert.HasCount(1, widths);
    }

    [TestMethod]
    public async Task InvalidSuppliedRegionsAreRejectedBeforeCacheLookup()
    {
        var cache = new InMemoryOcrResultCache();
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), WidthRecorder([]), cache);
        OcrDetectedRegion region = OcrTestFixtures.Region("tick", 20, 40, 12, 8);
        OcrRequest[] invalidRequests =
        [
            OcrTestFixtures.Request([null!]),
            OcrTestFixtures.Request([region with { Polygon = null! }]),
            OcrTestFixtures.Request([region, region]),
            OcrTestFixtures.Request([region with { DetectionConfidence = double.NaN }]),
            OcrTestFixtures.Request([region with { OrientationDegrees = double.PositiveInfinity }]),
            OcrTestFixtures.Request([region with { Polygon = new OcrPolygon([new(1, 1), new(1, 2), new(1, 3)]) }]),
            OcrTestFixtures.Request([region with { Polygon = new OcrPolygon([
                new(-double.MaxValue, 1), new(double.MaxValue, 1), new(double.MaxValue, 2)]) }]),
        ];

        foreach (OcrRequest request in invalidRequests)
        {
            OcrResult result = await pipeline.RecognizeAsync(request);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("OCR_REGION_DETECTION_FAILED", result.Failure?.Code);
        }
        Assert.AreEqual(0, cache.ReadCount);
    }

    [TestMethod]
    public async Task CancellationDuringGeometrySnapshotPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        OcrRequest request = OcrTestFixtures.Request(new CancellingRegions(cancellation));
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), WidthRecorder([]), new InMemoryOcrResultCache());

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await pipeline.RecognizeAsync(request, cancellation.Token));
    }

    [TestMethod]
    public async Task CallerMutationDuringCacheLookupCannotChangeTheRecognizedRegions()
    {
        OcrDetectedRegion original = OcrTestFixtures.Region("tick", 20, 40, 12, 8);
        OcrDetectedRegion moved = OcrTestFixtures.Region("tick", 80, 40, 12, 8);
        var supplied = new List<OcrDetectedRegion> { original };
        OcrRequest request = OcrTestFixtures.Request(supplied);
        var cache = new BlockingResultCache();
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), WidthRecorder([]), cache);

        Task<OcrResult> firstTask = pipeline.RecognizeAsync(request).AsTask();
        await cache.LookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        supplied[0] = moved;
        cache.ReleaseLookup.TrySetResult();
        OcrResult first = await firstTask;
        OcrResult cached = await pipeline.RecognizeAsync(OcrTestFixtures.Request([original]));

        Assert.IsTrue(first.Succeeded, first.Failure?.TechnicalMessage);
        Assert.AreEqual(original.Polygon.Bounds, first.Regions.Single().Polygon.Bounds);
        Assert.IsTrue(cached.Cache.CacheHit);
        Assert.AreEqual(original.Polygon.Bounds, cached.Regions.Single().Polygon.Bounds);
    }

    [TestMethod]
    public async Task CallerMutationDuringCacheLookupCannotChangeDividerAssembly()
    {
        var dividers = new List<double> { 73 };
        OcrRequest request = OcrTestFixtures.Request([
            OcrTestFixtures.Region("left", 60, 30, 12, 10),
            OcrTestFixtures.Region("right", 74, 30, 6, 10),
        ]) with { PhaseDividerXs = dividers };
        var cache = new BlockingResultCache();
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), WidthRecorder([]), cache,
            new OcrPipelineOptions { EnableInsidePlotAssembly = true });

        Task<OcrResult> firstTask = pipeline.RecognizeAsync(request).AsTask();
        await cache.LookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dividers[0] = 100;
        cache.ReleaseLookup.TrySetResult();
        OcrResult first = await firstTask;
        OcrResult cached = await pipeline.RecognizeAsync(request with { PhaseDividerXs = [73] });

        Assert.IsTrue(first.Succeeded, first.Failure?.TechnicalMessage);
        Assert.HasCount(2, first.Regions);
        Assert.IsTrue(cached.Cache.CacheHit);
        Assert.HasCount(2, cached.Regions);
    }

    [TestMethod]
    public async Task CallerMutationDuringCacheLookupCannotChangeTickStrokeEvidence()
    {
        var supporting = new List<string> { "measured-tick" };
        var strokes = new List<OcrXAxisTickStroke>
        {
            new("tick-1", new(45, 85), new(45, 84), new(45, 90), 0.9, supporting),
        };
        OcrRequest request = OcrTestFixtures.Request([]) with { XAxisTickStrokes = strokes };
        var cache = new BlockingResultCache();
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), WidthRecorder([]), cache,
            new OcrPipelineOptions { EnableTickLaneRecovery = true });
        OcrTickLaneDiagnostic? observation = null;

        Task<OcrResult> firstTask = pipeline.RecognizeWithTickLaneDiagnosticsAsync(
            request, diagnostic => observation = diagnostic).AsTask();
        await cache.LookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        supporting.Clear();
        strokes.Clear();
        cache.ReleaseLookup.TrySetResult();
        OcrResult first = await firstTask;

        Assert.IsTrue(first.Succeeded, first.Failure?.TechnicalMessage);
        Assert.IsNotNull(observation);
        Assert.AreEqual(1, observation.SuppliedStrokeCount);
        Assert.AreEqual(1, observation.UsableStrokeCount);
        Assert.IsTrue(observation.RecoveryAnalysisPerformed);
    }

    [TestMethod]
    public async Task ChangingWidthModeDoesNotReuseEarlierFixedWidthResult()
    {
        var widths = new List<int>();
        var recognizer = WidthRecorder(widths);
        var cache = new InMemoryOcrResultCache();
        var detector = new StubTextRegionDetector([]);
        var options = new OcrPipelineOptions { CropPaddingPixels = 0, MaximumCropWidth = 512 };
        OcrRequest request = OcrTestFixtures.Request([OcrTestFixtures.Region("wide", 20, 40, 120, 8)]);

        OcrResult fixedWidth = await new OcrPipeline(detector, recognizer, cache, options).RecognizeAsync(request);
        OcrResult dynamicWidth = await new OcrPipeline(detector, recognizer, cache,
            options with { CropWidthMode = OcrCropWidthMode.PaddleBatchMaximumAspectRatio }).RecognizeAsync(request);

        Assert.IsTrue(fixedWidth.Succeeded, fixedWidth.Failure?.TechnicalMessage);
        Assert.IsTrue(dynamicWidth.Succeeded, dynamicWidth.Failure?.TechnicalMessage);
        Assert.IsFalse(dynamicWidth.Cache.CacheHit);
        Assert.HasCount(2, widths);
        Assert.AreEqual(128, widths[0]);
        Assert.AreEqual(480, widths[1]);
        Assert.AreNotEqual(fixedWidth.Cache.CacheKey, dynamicWidth.Cache.CacheKey);
    }

    [TestMethod]
    public async Task TighterWidthBoundCannotBeBypassedByWarmResultCache()
    {
        var widths = new List<int>();
        var recognizer = WidthRecorder(widths);
        var cache = new InMemoryOcrResultCache();
        var detector = new StubTextRegionDetector([]);
        var options = new OcrPipelineOptions { CropPaddingPixels = 0, MaximumCropWidth = 512,
            CropWidthMode = OcrCropWidthMode.PaddleBatchMaximumAspectRatio };
        OcrRequest request = OcrTestFixtures.Request([OcrTestFixtures.Region("wide", 20, 40, 120, 8)]);

        OcrResult allowed = await new OcrPipeline(detector, recognizer, cache, options).RecognizeAsync(request);
        OcrResult bounded = await new OcrPipeline(detector, recognizer, cache,
            options with { MaximumCropWidth = 256 }).RecognizeAsync(request);

        Assert.IsTrue(allowed.Succeeded, allowed.Failure?.TechnicalMessage);
        Assert.IsFalse(bounded.Succeeded);
        Assert.AreEqual("OCR_CROP_PREPROCESS_FAILED", bounded.Failure?.Code);
        Assert.IsFalse(bounded.Cache.CacheHit);
        Assert.HasCount(1, widths);
        Assert.AreEqual(480, widths[0]);
    }

    private static StubTextRecognizer WidthRecorder(List<int> widths) => new((crops, _) =>
    {
        widths.AddRange(crops.Select(static crop => crop.Width));
        return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(static crop => new OcrRecognition(
            crop.RegionId, crop.SourceImage, [new("Sessions", 0.95, crop.SourceImage)], 0.1)).ToArray());
    });

    private sealed class BlockingResultCache : IOcrResultCache
    {
        private readonly MemoryOcrResultCache inner = new();
        private int blockFirst = 1;
        internal TaskCompletionSource LookupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseLookup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<OcrCachedPayload?> TryGetAsync(string key, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref blockFirst, 0) == 1)
            {
                LookupStarted.TrySetResult();
                await ReleaseLookup.Task.WaitAsync(cancellationToken);
            }
            return await inner.TryGetAsync(key, cancellationToken);
        }

        public ValueTask PutAsync(string key, OcrCachedPayload payload, CancellationToken cancellationToken) =>
            inner.PutAsync(key, payload, cancellationToken);
    }

    private sealed class CancellingRegions(CancellationTokenSource cancellation) : IReadOnlyList<OcrDetectedRegion>
    {
        public int Count => 1;
        public OcrDetectedRegion this[int index] => throw new NotSupportedException();

        public IEnumerator<OcrDetectedRegion> GetEnumerator()
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static OcrDetectorImage DetectorImage(OcrImage image) => new(
        image,
        Convert.ToHexStringLower(SHA256.HashData(image.Pixels.Span)));

    private static OcrRegion Region(string id, string text) =>
        new(
            id,
            OcrPolygon.FromRectangle(new OcrRectangle(10, 10, 12, 6)),
            text,
            [new OcrRecognitionAlternative(text, 0.9, OcrSourceImage.Original)],
            OcrTextRole.Other,
            0.9,
            OcrSourceImage.Original,
            OcrReviewStatus.Unreviewed);
}
