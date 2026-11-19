// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class TickLaneXIntervalRecoveryTests
{
    private static readonly OcrRectangle Plot = new(40, 20, 200, 80);
    private static readonly int[] TickXs = [60, 100, 140, 180, 220];
    private static readonly string[] UntrustedReads = ["day", "7X"];
    private static readonly string[] ConflictingAnchorReads = ["12", "72"];

    [TestMethod]
    public async Task ProposesSeparatedMultiDigitLabelInDistinctTickGapFromOriginalPixels()
    {
        Graph graph = BuildGraph(includeTicks: true, includeMiddleLabel: true);
        byte[] original = graph.Image.Pixels.ToArray();

        IReadOnlyList<OcrDetectedRegion> recovered = await TickLaneTextRegionRecovery.FindAsync(
            graph.Image,
            graph.Detected,
            graph.Recognized,
            Plot,
            graph.TickStrokes,
            CancellationToken.None);

        OcrDetectedRegion crop = AssertExactlyOne(recovered.Where(region =>
            region.RegionId.StartsWith("tick-lane-x-interval:", StringComparison.Ordinal)).ToArray());
        Assert.AreEqual(new OcrRectangle(112, 112, 12, 8), crop.Polygon.Bounds);
        Assert.AreEqual(OcrContract.CoordinateSpace, crop.CoordinateSpace);
        Assert.AreEqual(0d, crop.OrientationDegrees);
        Assert.IsNull(crop.Context, "Geometry recovery must not invent an OCR role hint or numeric value.");
        CollectionAssert.AreEqual(original, graph.Image.Pixels.ToArray(), "Recovery must preserve immutable source pixels.");
    }

    [TestMethod]
    [DataRow(true, 1)]
    [DataRow(false, 0)]
    public async Task DistinguishesDetectorBoxEdgeAngleFromObservedTextDirection(bool fromBoxEdge, int expectedCrops)
    {
        Graph graph = BuildGraph(includeTicks: true, includeMiddleLabel: true);
        OcrDetectedRegion[] detected = [graph.Detected[0], graph.Detected[1] with
        {
            OrientationDegrees = 45,
            OrientationFromBoxLongEdge = fromBoxEdge,
        }];
        var regions = await TickLaneTextRegionRecovery.FindAsync(graph.Image, detected, graph.Recognized, Plot);
        Assert.HasCount(expectedCrops, IntervalCrops(regions));
    }

    [TestMethod]
    public async Task TicklessPixelsDoNotAuthorizeXIntervalCrops()
    {
        Graph graph = BuildGraph(includeTicks: false, includeMiddleLabel: true);

        IReadOnlyList<OcrDetectedRegion> recovered = await TickLaneTextRegionRecovery.FindAsync(
            graph.Image, graph.Detected, graph.Recognized, Plot, graph.TickStrokes, CancellationToken.None);

        Assert.IsEmpty(IntervalCrops(recovered));
    }

    [TestMethod]
    public async Task OneAnchorOrUnattachedLabelCannotEstablishIntervalRecovery()
    {
        Graph graph = BuildGraph(includeTicks: true, includeMiddleLabel: true);
        IReadOnlyList<OcrDetectedRegion> oneAnchor = await TickLaneTextRegionRecovery.FindAsync(
            graph.Image,
            [graph.Detected[0]],
            [graph.Recognized[0]],
            Plot,
            graph.TickStrokes,
            CancellationToken.None);

        Assert.IsEmpty(IntervalCrops(oneAnchor));

        Graph unattached = BuildGraph(includeTicks: true, includeMiddleLabel: true, omitAnchorTickSupport: true);
        IReadOnlyList<OcrDetectedRegion> unsupported = await TickLaneTextRegionRecovery.FindAsync(
            unattached.Image, unattached.Detected, unattached.Recognized, Plot,
            unattached.TickStrokes, CancellationToken.None);

        Assert.IsEmpty(IntervalCrops(unsupported));
    }

    [TestMethod]
    public async Task ProseCrossingSeveralTickIntervalsIsNotSplitIntoNumericCrops()
    {
        Graph graph = BuildGraph(includeTicks: true, includeMiddleLabel: false, addCrossingProse: true);

        IReadOnlyList<OcrDetectedRegion> recovered = await TickLaneTextRegionRecovery.FindAsync(
            graph.Image, graph.Detected, graph.Recognized, Plot, graph.TickStrokes, CancellationToken.None);

        Assert.IsEmpty(IntervalCrops(recovered));
        Assert.IsEmpty(recovered.Where(region => region.RegionId.StartsWith(
            "tick-lane-x-interval:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task WideInterGlyphGapIsNotInventedAsOneMissingNumericLabel()
    {
        Graph graph = BuildGraph(includeTicks: true, includeMiddleLabel: true, interGlyphGap: 6);

        IReadOnlyList<OcrDetectedRegion> recovered = await TickLaneTextRegionRecovery.FindAsync(
            graph.Image, graph.Detected, graph.Recognized, Plot, graph.TickStrokes, CancellationToken.None);

        Assert.IsEmpty(IntervalCrops(recovered));
    }

    [TestMethod]
    public async Task PipelineRecognizesRecoveredPixelsWithoutInventingNumericRole()
    {
        foreach (string reading in UntrustedReads)
        {
            Graph graph = BuildGraph(includeTicks: true, includeMiddleLabel: true);
            OcrRequest request = OcrTestFixtures.Request(graph.Detected) with
            {
                OriginalImage = graph.Image,
                PlotBounds = Plot,
                XAxisTickStrokes = graph.TickStrokes,
            };
            var recognizer = new StubTextRecognizer((crops, _) => ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(
                crops.Select(crop => new OcrRecognition(crop.RegionId, crop.SourceImage,
                    crop.RegionId.StartsWith("tick-lane-x-interval:", StringComparison.Ordinal)
                        ? [new OcrRecognitionAlternative(reading, 0.99, crop.SourceImage)]
                        : crop.RegionId == "anchor-left"
                            ? [new OcrRecognitionAlternative("12", 0.8, crop.SourceImage),
                                new OcrRecognitionAlternative("72", 0.8, crop.SourceImage)]
                            : [new OcrRecognitionAlternative("34", 0.95, crop.SourceImage)],
                    0.1)).ToArray()));
            var pipeline = new OcrPipeline(new StubTextRegionDetector([]), recognizer,
                new InMemoryOcrResultCache(), new OcrPipelineOptions { EnableTickLaneRecovery = true });

            OcrResult result = await pipeline.RecognizeAsync(request, CancellationToken.None);

            Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
            OcrRegion recovered = AssertExactlyOne(result.Regions.Where(region =>
                region.RegionId.StartsWith("tick-lane-x-interval:", StringComparison.Ordinal)).ToArray());
            Assert.AreEqual(reading, recovered.Text);
            Assert.AreNotEqual(OcrTextRole.XTick, recovered.Role,
                "Pixel recovery proposes a crop only; OCR must recognize a literal before numeric role assignment.");
            OcrRegion conflict = result.Regions.Single(region => region.RegionId == "anchor-left");
            CollectionAssert.AreEquivalent(ConflictingAnchorReads,
                conflict.Alternatives.Select(alternative => alternative.Text).ToArray());
        }
    }

    [TestMethod]
    public async Task ExistingOverlappingDetectorReadingOwnsItsPixelsAndIsPreserved()
    {
        Graph graph = BuildGraph(includeTicks: true, includeMiddleLabel: true);
        OcrDetectedRegion overlapping = Region("original-overlap", new OcrRectangle(112, 112, 12, 8));
        OcrDetectedRegion[] detected = [.. graph.Detected, overlapping];
        OcrRegion[] recognized = [.. graph.Recognized, Recognized(overlapping, "70")];

        IReadOnlyList<OcrDetectedRegion> recovered = await TickLaneTextRegionRecovery.FindAsync(
            graph.Image, detected, recognized, Plot, graph.TickStrokes, CancellationToken.None);

        Assert.IsEmpty(IntervalCrops(recovered));
        Assert.IsTrue(detected.Any(region => region.RegionId == "original-overlap"),
            "The original detection remains available to the caller; interval recovery must not replace it.");
        Assert.AreEqual("70", recognized.Single(region => region.RegionId == "original-overlap").Text);

        OcrRequest request = OcrTestFixtures.Request(detected) with
        {
            OriginalImage = graph.Image,
            PlotBounds = Plot,
            XAxisTickStrokes = graph.TickStrokes,
        };
        var recognizer = new StubTextRecognizer((crops, _) => ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(
            crops.Select(crop => new OcrRecognition(crop.RegionId, crop.SourceImage,
                crop.RegionId switch
                {
                    "anchor-left" => [new OcrRecognitionAlternative("12", 0.8, crop.SourceImage),
                        new OcrRecognitionAlternative("72", 0.8, crop.SourceImage)],
                    "anchor-right" => [new OcrRecognitionAlternative("34", 0.95, crop.SourceImage)],
                    "original-overlap" => [new OcrRecognitionAlternative("70", 0.96, crop.SourceImage)],
                    _ => [],
                }, 0.1)).ToArray()));
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), recognizer,
            new InMemoryOcrResultCache(), new OcrPipelineOptions { EnableTickLaneRecovery = true });

        OcrResult pipelineResult = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.IsTrue(pipelineResult.Succeeded, pipelineResult.Failure?.TechnicalMessage);
        Assert.IsEmpty(pipelineResult.Regions.Where(region => region.RegionId.StartsWith(
            "tick-lane-x-interval:", StringComparison.Ordinal)));
        Assert.AreEqual("70", pipelineResult.Regions.Single(region => region.RegionId == "original-overlap").Text);
        CollectionAssert.AreEquivalent(ConflictingAnchorReads, pipelineResult.Regions
            .Single(region => region.RegionId == "anchor-left")
            .Alternatives.Select(alternative => alternative.Text).ToArray());
    }

    private static Graph BuildGraph(
        bool includeTicks,
        bool includeMiddleLabel,
        bool addCrossingProse = false,
        bool omitAnchorTickSupport = false,
        int interGlyphGap = 2)
    {
        const int width = 280;
        const int height = 150;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        void Ink(int x, int y) => pixels[y * width + x] = 0;

        if (includeTicks)
        {
            // A continuous plot rail, with short outward strokes and a blank
            // row before labels. No text or expected values are encoded here.
            for (int x = 40; x <= 240; x++) Ink(x, 100);
            foreach (int x in TickXs)
            {
                if (omitAnchorTickSupport && x is 60 or 100 or 140 or 180) continue;
                for (int y = 100; y <= 105; y++) Ink(x, y);
            }
        }

        // Each literal crop has two disconnected glyph-like strokes around
        // the midpoint of its own adjacent tick pair. The unsupplied label is
        // likewise bounded by different adjacent strokes.
        DrawPair(72, interGlyphGap);
        if (includeMiddleLabel) DrawPair(112, interGlyphGap);
        DrawPair(152, interGlyphGap);

        if (addCrossingProse)
        {
            // A continuous horizontal phrase crosses multiple tick intervals;
            // it must not be broken into extra numeric regions by the gaps.
            for (int x = 88; x <= 192; x++)
            {
                Ink(x, 114);
                Ink(x, 118);
            }
            for (int y = 114; y <= 118; y++)
            {
                Ink(88, y);
                Ink(192, y);
            }
        }

        void DrawPair(int left, int gap)
        {
            for (int y = 112; y < 120; y++)
            {
                for (int x = left; x < left + 5; x++) Ink(x, y);
                int secondGlyphLeft = left + 5 + gap;
                for (int x = secondGlyphLeft; x < secondGlyphLeft + 5; x++) Ink(x, y);
            }
        }

        var image = new OcrImage(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
        OcrDetectedRegion[] detected =
        [
            Region("anchor-left", new OcrRectangle(72, 112, 16, 8)),
            Region("anchor-right", new OcrRectangle(152, 112, 16, 8)),
        ];
        OcrRegion[] recognized =
        [
            Recognized(detected[0], "12"),
            Recognized(detected[1], "34"),
        ];
        OcrXAxisTickStroke[] strokes = TickXs.Select((x, index) => new OcrXAxisTickStroke(
            "tick-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new OcrPoint(x, 100),
            new OcrPoint(x, 100),
            new OcrPoint(x, 105),
            0.9,
            ["owned-control-axis-stroke"])).ToArray();
        return new Graph(image, detected, recognized, strokes);
    }

    private static OcrDetectedRegion Region(string id, OcrRectangle bounds) => new(
        id, OcrPolygon.FromRectangle(bounds), 0, 0.9);

    private static OcrRegion Recognized(OcrDetectedRegion detected, string text) => new(
        detected.RegionId,
        detected.Polygon,
        text,
        [],
        OcrTextRole.XTick,
        0.99,
        OcrSourceImage.Original,
        OcrReviewStatus.Unreviewed);

    private static OcrDetectedRegion[] IntervalCrops(IEnumerable<OcrDetectedRegion> regions) =>
        regions.Where(region => region.RegionId.StartsWith(
            "tick-lane-x-interval:", StringComparison.Ordinal)).ToArray();

    private static T AssertExactlyOne<T>(T[] values)
    {
        Assert.HasCount(1, values);
        return values[0];
    }

    private sealed record Graph(
        OcrImage Image,
        OcrDetectedRegion[] Detected,
        OcrRegion[] Recognized,
        OcrXAxisTickStroke[] TickStrokes);
}
