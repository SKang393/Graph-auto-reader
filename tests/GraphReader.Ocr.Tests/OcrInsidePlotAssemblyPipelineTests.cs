// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class OcrInsidePlotAssemblyPipelineTests
{
    private static readonly string[] ExpectedUnassembledRegionIds = ["word", "suffix"];

    [TestMethod]
    public async Task AssemblyIsDisabledByDefaultAndUnavailableGeometryFailsClosed()
    {
        OcrDetectedRegion word = OcrTestFixtures.Region("word", 40, 30, 12, 10);
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 54, 30, 6, 10);
        OcrRequest request = OcrTestFixtures.Request([word, suffix]);

        OcrResult disabled = await RecognizeAsync(request, enableAssembly: false);
        OcrResult unavailable = await RecognizeAsync(request, enableAssembly: true);

        Assert.HasCount(2, disabled.Regions);
        Assert.HasCount(2, unavailable.Regions);
        CollectionAssert.AreEquivalent(
            ExpectedUnassembledRegionIds,
            disabled.Regions.Select(static region => region.RegionId).ToArray());
        CollectionAssert.AreEquivalent(
            ExpectedUnassembledRegionIds,
            unavailable.Regions.Select(static region => region.RegionId).ToArray());
    }

    [TestMethod]
    public async Task ExplicitNoDividersRecognizesOneOriginalPixelUnionCrop()
    {
        OcrDetectedRegion word = OcrTestFixtures.Region("word", 40, 30, 12, 10, confidence: 0.92);
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 54, 30, 6, 10, confidence: 0.81);
        OcrRequest request = OcrTestFixtures.Request([word, suffix]) with
        {
            PhaseDividerXs = Array.Empty<double>(),
        };
        OcrCrop? observedCrop = null;
        var recognizer = new StubTextRecognizer((crops, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            observedCrop = crops.Single();
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>
            ([
                new OcrRecognition(
                    observedCrop.RegionId,
                    observedCrop.SourceImage,
                    [new OcrRecognitionAlternative("Generalization", 0.93, observedCrop.SourceImage)],
                    0.1),
            ]);
        });
        var pipeline = Pipeline(recognizer, enableAssembly: true);

        OcrResult result = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(1, result.Regions);
        Assert.IsNotNull(observedCrop);
        Assert.AreEqual(OcrSourceImage.Original, observedCrop.SourceImage);
        Assert.AreEqual(OcrContract.CoordinateSpace, result.Regions[0].CoordinateSpace);
        Assert.AreEqual(new OcrRectangle(40, 30, 20, 10), observedCrop.OriginalPolygon.Bounds);
        StringAssert.StartsWith(result.Regions[0].RegionId, "inside-plot:");
        Assert.IsTrue(result.Regions[0].Confidence is > 0 and <= 1);
    }

    [TestMethod]
    public async Task DividerGeometryPreventsPipelineUnion()
    {
        OcrRequest request = OcrTestFixtures.Request(
            [OcrTestFixtures.Region("left", 60, 30, 12, 10),
             OcrTestFixtures.Region("right", 74, 30, 6, 10)]) with
        {
            PhaseDividerXs = [73],
        };

        OcrResult result = await RecognizeAsync(request, enableAssembly: true);

        Assert.HasCount(2, result.Regions);
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(29.9)]
    [DataRow(140.1)]
    public async Task InvalidDividerGeometryReturnsStructuredInputFailure(double dividerX)
    {
        var detector = new StubTextRegionDetector([]);
        var pipeline = new OcrPipeline(
            detector,
            DynamicRecognizer(),
            new InMemoryOcrResultCache(),
            new OcrPipelineOptions { EnableInsidePlotAssembly = true });
        OcrRequest request = OcrTestFixtures.Request([]) with { PhaseDividerXs = [dividerX] };

        OcrResult result = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("OCR_INPUT_INVALID", result.Failure?.Code);
        Assert.AreEqual(0, detector.CallCount);
    }

    private static async Task<OcrResult> RecognizeAsync(OcrRequest request, bool enableAssembly)
    {
        var pipeline = Pipeline(DynamicRecognizer(), enableAssembly);
        OcrResult result = await pipeline.RecognizeAsync(request, CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        return result;
    }

    [TestMethod]
    public async Task CombinedAssemblyRecognizesBothLanesThenRefinesPixelsWithDistinctCache()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 160 * 100).ToArray();
        pixels[33 * 160 + 4] = 0;
        pixels[38 * 160 + 21] = 0;
        pixels[63 * 160 + 44] = 0;
        pixels[68 * 160 + 59] = 0;
        OcrRequest request = OcrTestFixtures.Request(
            [OcrTestFixtures.Region("participant-word", 2, 30, 12, 10),
             OcrTestFixtures.Region("participant-number", 16, 30, 8, 10),
             OcrTestFixtures.Region("plot-word", 40, 60, 12, 10),
             OcrTestFixtures.Region("plot-suffix", 54, 60, 8, 10)]) with
        {
            OriginalImage = OcrTestFixtures.Image() with { Pixels = pixels },
            PhaseDividerXs = Array.Empty<double>(),
        };
        var cropsSeen = new List<OcrCrop>();
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            cropsSeen.AddRange(crops);
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                new OcrRecognition(crop.RegionId, crop.SourceImage,
                    [new OcrRecognitionAlternative("text", 0.95, crop.SourceImage)], 0.1)).ToArray());
        });
        var options = new OcrPipelineOptions
        {
            EnableInsidePlotAssembly = true,
            EnableOriginalPixelBoundsRefinement = true,
            CropPaddingPixels = 0,
        };
        var cache = new InMemoryOcrResultCache();
        var baseline = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache, options);
        var combined = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache,
            options with { EnableParticipantLaneAssembly = true });

        OcrResult before = await baseline.RecognizeAsync(request);
        OcrResult after = await combined.RecognizeAsync(request);

        Assert.IsTrue(before.Succeeded, before.Failure?.TechnicalMessage);
        Assert.IsTrue(after.Succeeded, after.Failure?.TechnicalMessage);
        Assert.HasCount(3, before.Regions);
        Assert.HasCount(2, after.Regions);
        Assert.HasCount(5, cropsSeen);
        OcrCrop participantCrop = cropsSeen.Skip(3).Single(crop =>
            crop.RegionId.StartsWith("participant-lane:", StringComparison.Ordinal));
        OcrCrop plotCrop = cropsSeen.Skip(3).Single(crop =>
            crop.RegionId.StartsWith("inside-plot:", StringComparison.Ordinal));
        Assert.AreEqual(new OcrRectangle(4, 33, 18, 6), participantCrop.OriginalPolygon.Bounds);
        Assert.AreEqual(new OcrRectangle(44, 63, 16, 6), plotCrop.OriginalPolygon.Bounds);
        Assert.IsTrue(cropsSeen.All(crop => crop.SourceImage == OcrSourceImage.Original));
        CollectionAssert.AreEquivalent(new[] { participantCrop.RegionId, plotCrop.RegionId },
            after.Regions.Select(region => region.RegionId).ToArray());
    }

    private static OcrPipeline Pipeline(StubTextRecognizer recognizer, bool enableAssembly) =>
        new(
            new StubTextRegionDetector([]),
            recognizer,
            new InMemoryOcrResultCache(),
            new OcrPipelineOptions
            {
                EnableInsidePlotAssembly = enableAssembly,
                CropPaddingPixels = 0,
            });

    private static StubTextRecognizer DynamicRecognizer() =>
        new((crops, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                new OcrRecognition(
                    crop.RegionId,
                    crop.SourceImage,
                    [new OcrRecognitionAlternative("text", 0.95, crop.SourceImage)],
                    0.1)).ToArray());
        });
}
