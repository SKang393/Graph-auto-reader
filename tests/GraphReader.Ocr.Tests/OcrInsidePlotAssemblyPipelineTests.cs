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
