// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionSourceScaleOcrDetectorTests
{
    [TestMethod]
    public async Task OrdinaryImageUsesExactlyTheExistingDetectorInputAndResults()
    {
        OcrImage original = Image(1200, 350);
        OcrDetectedRegion[] expected = [Region("one", 2, 3, 18, 11)];
        var inner = new RecordingDetector((image, _) =>
        {
            Assert.AreSame(original, image);
            return expected;
        });
        var detector = new ProductionSourceScaleOcrDetector(inner);
        Assert.AreSame(expected, await detector.DetectAsync(original, CancellationToken.None));
        Assert.AreEqual(1, inner.Calls);
        StringAssert.Contains(detector.ConfigurationFingerprint, "source-scale-windows-v2");
        StringAssert.Contains(detector.ConfigurationFingerprint, inner.ConfigurationFingerprint);
    }

    [TestMethod]
    public async Task SmallInputPreservesBothPixelPlanesAndExcludesPaddingOnlyPredictions()
    {
        OcrImage original = Image(40, 20, gray: 31, bgr: 72);
        var inner = new RecordingDetector((window, _) =>
        {
            Assert.AreEqual(1200, window.Width);
            Assert.AreEqual(20, window.Height);
            Assert.AreEqual(OcrFrameTransform.Identity, window.OriginalToImage);
            for (int y = 0; y < 20; y++)
            {
                Assert.AreEqual((byte)31, window.Pixels.Span[y * window.Stride + 39]);
                Assert.AreEqual((byte)255, window.Pixels.Span[y * window.Stride + 40]);
                Assert.AreEqual((byte)72, window.BgrPixels!.Pixels.Span[y * window.BgrPixels.Stride + 119]);
                Assert.AreEqual((byte)255, window.BgrPixels.Pixels.Span[y * window.BgrPixels.Stride + 120]);
            }
            return [Region("real", 30, 5, 20, 10), Region("padding", 60, 5, 20, 10)];
        });
        IReadOnlyList<OcrDetectedRegion> regions = await new ProductionSourceScaleOcrDetector(inner)
            .DetectAsync(original, CancellationToken.None);
        Assert.HasCount(1, regions);
        Assert.AreEqual(new OcrRectangle(30, 5, 10, 10), regions[0].Polygon.Bounds);
        Assert.IsTrue(original.Pixels.Span.ToArray().All(static value => value == 31));
        Assert.IsTrue(original.BgrPixels!.Pixels.Span.ToArray().All(static value => value == 72));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OverlappingWindowsMapToOriginalAndRemoveOnlyCrossWindowDuplicates(bool horizontal)
    {
        OcrImage original = Image(horizontal ? 2000 : 600, horizontal ? 100 : 2000) with
        {
            OriginalToImage = new OcrFrameTransform(2, 2, -20, -40),
            CanonicalOriginalWidth = 2000,
            CanonicalOriginalHeight = 2000,
        };
        var inner = new RecordingDetector((window, index) =>
        {
            Assert.AreEqual(1200, window.Width);
            double x = horizontal ? 900 - index * 800 : 20;
            double y = horizontal ? 20 : 900 - index * 800;
            // Two close same-window detections must remain independently reviewable.
            return [Region("one", x, y, 40, 12, index == 0 ? .9 : .8),
                Region("two", x + 2, y + 2, 40, 12, index == 0 ? .85 : .75)];
        });
        IReadOnlyList<OcrDetectedRegion> regions = await new ProductionSourceScaleOcrDetector(inner)
            .DetectAsync(original, CancellationToken.None);
        Assert.AreEqual(2, inner.Calls);
        Assert.HasCount(2, regions);
        Assert.AreEqual(horizontal ? 460d : 20d, regions[0].Polygon.Bounds.X);
        Assert.AreEqual(horizontal ? 30d : 470d, regions[0].Polygon.Bounds.Y);
        Assert.AreEqual(20d, regions[0].Polygon.Bounds.Width);
        Assert.AreEqual(6d, regions[0].Polygon.Bounds.Height);
        Assert.HasCount(2, regions.Select(static region => region.RegionId).Distinct().ToArray());
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task InternalEdgeFragmentYieldsToCompletePeerEvenWithHigherConfidence(bool rightEdge, bool transformed)
    {
        OcrImage original = Image(1400, 100, gray: 31, bgr: 72);
        if (transformed) original = original with { OriginalToImage = new OcrFrameTransform(2, 2, -20, -40) };
        byte[] before = original.Pixels.ToArray();
        var inner = new RecordingDetector((_, index) => rightEdge
            ? index == 0 ? [Region("fragment", 1170, 20, 27, 12, .99)] : [Region("complete", 970, 22, 80, 14, .75)]
            : index == 0 ? [Region("complete", 160, 22, 80, 14, .75)] : [Region("fragment", 2, 20, 20, 12, .99)]);
        OcrDetectedRegion result = (await new ProductionSourceScaleOcrDetector(inner)
            .DetectAsync(original, CancellationToken.None)).Single();
        StringAssert.EndsWith(result.RegionId, ":complete");
        Assert.AreEqual(.75, result.DetectionConfidence);
        var expected = new OcrRectangle(rightEdge ? 1170 : 160, 22, 80, 14);
        if (transformed) expected = new OcrRectangle((expected.X + 20) / 2, 31, 40, 7);
        Assert.AreEqual(expected, result.Polygon.Bounds);
        CollectionAssert.AreEqual(before, original.Pixels.ToArray());
        Assert.IsTrue(original.BgrPixels!.Pixels.Span.ToArray().All(static value => value == 72));
    }

    [TestMethod]
    [DataRow("different-row")]
    [DataRow("tall-region")]
    [DataRow("different-orientation")]
    [DataRow("protected-context")]
    [DataRow("does-not-cross-edge")]
    public async Task EdgeFragmentRequiresCompatiblePeerThatActuallyCrossesTheWindowBoundary(string reason)
    {
        var inner = new RecordingDetector((_, index) =>
        {
            if (index == 0) return [Region("fragment", 1170, 20, 27, 12, .99)];
            OcrDetectedRegion peer = Region("peer", 970, reason == "different-row" ? 50 : 20,
                80, reason == "tall-region" ? 60 : 12, .75);
            if (reason == "different-orientation") peer = peer with { OrientationDegrees = 90 };
            if (reason == "protected-context") peer = peer with { Context = new(NumericExpected: true) };
            if (reason == "does-not-cross-edge") peer = Region("peer", 900, 20, 98, 12, .75);
            return [peer];
        });
        IReadOnlyList<OcrDetectedRegion> results = await new ProductionSourceScaleOcrDetector(inner)
            .DetectAsync(Image(1400, 100), CancellationToken.None);
        Assert.HasCount(2, results);
        Assert.IsTrue(results.Any(region => region.RegionId.EndsWith(":fragment", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CancellationStopsBeforeStartingAnotherWindow()
    {
        using var cancellation = new CancellationTokenSource();
        var inner = new RecordingDetector((_, _) => { cancellation.Cancel(); return []; });
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await new ProductionSourceScaleOcrDetector(inner).DetectAsync(Image(3000, 100), cancellation.Token));
        Assert.AreEqual(1, inner.Calls);
    }

    [TestMethod]
    public async Task InvalidOrNonOriginalInputNeverReachesInference()
    {
        var inner = new RecordingDetector((_, _) => []);
        var detector = new ProductionSourceScaleOcrDetector(inner);
        foreach (OcrImage invalid in new[]
        {
            Image(40, 20) with { SourceImage = OcrSourceImage.Enhanced },
            Image(40, 20) with { BgrPixels = null },
            Image(40, 20) with { OriginalToImage = new OcrFrameTransform(0, 1, 0, 0) },
        })
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await detector.DetectAsync(invalid, CancellationToken.None));
        }
        Assert.AreEqual(0, inner.Calls);
    }

    private static OcrImage Image(int width, int height, byte gray = 255, byte bgr = 255) =>
        new(width, height, width, Enumerable.Repeat(gray, width * height).ToArray(), OcrSourceImage.Original,
            OcrFrameTransform.Identity, CanonicalOriginalWidth: width, CanonicalOriginalHeight: height,
            BgrPixels: new OcrBgrBytePixels(width * 3, Enumerable.Repeat(bgr, width * height * 3).ToArray()));

    private static OcrDetectedRegion Region(string id, double x, double y, double width, double height,
        double confidence = .9) => new(id, OcrPolygon.FromRectangle(new OcrRectangle(x, y, width, height)), 0, confidence);

    private sealed class RecordingDetector(Func<OcrImage, int, IReadOnlyList<OcrDetectedRegion>> detect) : ITextRegionDetector
    {
        public int Calls { get; private set; }
        public string ConfigurationFingerprint => "unchanged-test-model";
        public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(OcrImage image, CancellationToken cancellationToken) =>
            ValueTask.FromResult(detect(image, Calls++));
    }
}
