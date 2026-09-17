// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;
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
