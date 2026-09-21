// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class OcrDetectionObservationTests
{
    private static StubTextRecognizer Recognizer() => new((crops, _) =>
        ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
            new OcrRecognition(crop.RegionId, crop.SourceImage,
                [new OcrRecognitionAlternative("Baseline", 0.95, crop.SourceImage)], 0.1)).ToArray()));

    [TestMethod]
    public async Task ObservedRequestsCaptureActualDetectionDespiteWarmResultCache()
    {
        OcrDetectedRegion raw = OcrTestFixtures.Region("text", 45, 40, 20, 10);
        var detector = new StubTextRegionDetector([raw]);
        var recognizer = Recognizer();
        var cache = new InMemoryOcrResultCache();
        var options = new OcrPipelineOptions();
        OcrRequest request = OcrTestFixtures.Request();
        var normal = new OcrPipeline(detector, recognizer, cache, options);
        OcrResult baseline = await normal.RecognizeAsync(request);
        OcrResult cached = await normal.RecognizeAsync(request);
        Assert.IsTrue(cached.Cache.CacheHit);
        Assert.AreEqual(1, detector.CallCount);

        var observations = new List<OcrDetectionObservation>();
        var observed = new OcrPipeline(detector, recognizer, cache, options, observations.Add);
        OcrResult first = await observed.RecognizeAsync(request);
        OcrResult second = await observed.RecognizeAsync(request);
        Assert.IsTrue(first.Succeeded, first.Failure?.TechnicalMessage);
        Assert.IsTrue(second.Succeeded, second.Failure?.TechnicalMessage);
        Assert.AreEqual(3, detector.CallCount);
        Assert.HasCount(2, observations);
        Assert.AreEqual(JsonSerializer.Serialize(baseline.Regions), JsonSerializer.Serialize(first.Regions));
        Assert.AreEqual(JsonSerializer.Serialize(first.Regions), JsonSerializer.Serialize(second.Regions));
        foreach (OcrDetectionObservation observation in observations)
        {
            Assert.AreEqual(request.PanelId, observation.PanelId);
            Assert.AreEqual(request.ProjectId, observation.ProjectId);
            Assert.AreEqual(request.InputSha256, observation.InputSha256);
            Assert.AreEqual(request.OriginalImage.Width, observation.Width);
            Assert.IsFalse(observation.SuppliedRegions);
            CollectionAssert.AreEqual(new[] { raw }, observation.RawDetectorRegions.ToArray());
            Assert.IsTrue(((IList<OcrDetectedRegion>)observation.RawDetectorRegions).IsReadOnly);
        }
    }

    [TestMethod]
    public async Task SuppliedRegionsAreExplicitAndCannotPretendToBeModelOutput()
    {
        OcrDetectedRegion raw = OcrTestFixtures.Region("text", 45, 40, 20, 10);
        var detector = new StubTextRegionDetector([]);
        OcrDetectionObservation? observation = null;
        var pipeline = new OcrPipeline(detector, Recognizer(), new InMemoryOcrResultCache(),
            new OcrPipelineOptions(), value => observation = value);
        OcrResult result = await pipeline.RecognizeAsync(OcrTestFixtures.Request([raw]));
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.AreEqual(0, detector.CallCount);
        Assert.IsNotNull(observation);
        Assert.IsTrue(observation.SuppliedRegions);
    }

    [TestMethod]
    public async Task ObservationFailureCannotYieldSuccessfulEvidence()
    {
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([OcrTestFixtures.Region("text", 45, 40, 20, 10)]),
            Recognizer(), new InMemoryOcrResultCache(), new OcrPipelineOptions(),
            _ => throw new InvalidOperationException("capture_failed"));
        OcrResult result = await pipeline.RecognizeAsync(OcrTestFixtures.Request());
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("OCR_REGION_DETECTION_FAILED", result.Failure?.Code);
        StringAssert.Contains(result.Failure!.TechnicalMessage, "capture_failed");
    }
}
