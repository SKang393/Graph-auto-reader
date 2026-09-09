// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Ocr;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class GraphStructureConsensusTextRegionDetectorTests
{
    [TestMethod]
    public async Task RejectedUsedCandidateDoesNotConsumeModelBeforeItsNextMatch()
    {
        OcrRegionEvidence firstEvidence = Evidence(3, 0.9, 0.1, false);
        OcrRegionEvidence secondEvidence = Evidence(3, 0.8, 0.1, false);
        var detector = new GraphStructureConsensusTextRegionDetector(
            new FixedDetector([
                Region("first-model", new OcrRectangle(10, 10, 10, 10), 0.99),
                Region("second-model", new OcrRectangle(10, 10, 20, 10), 0.9),
            ], "model"),
            new FixedDetector([
                Region("first-candidate", new OcrRectangle(10, 10, 10, 10), 0.9, firstEvidence),
                Region("second-candidate", new OcrRectangle(20, 10, 10, 10), 0.8, secondEvidence),
            ], "candidate"));

        IReadOnlyList<OcrDetectedRegion> result = await detector.DetectAsync(Image(), CancellationToken.None);

        Assert.HasCount(2, result);
        Assert.AreSame(firstEvidence, result.Single(region => region.RegionId == "first-model").Evidence);
        Assert.AreSame(secondEvidence, result.Single(region => region.RegionId == "second-model").Evidence);
    }

    [TestMethod]
    public async Task KeepsOneHighestConfidenceModelRegionPerCredibleTextCandidate()
    {
        OcrDetectedRegion textCandidate = Region(
            "candidate-text",
            new OcrRectangle(9, 9, 18, 13),
            0.75,
            Evidence(componentCount: 3, textLikelihood: 0.86, structureLikelihood: 0.08, graph: false));
        OcrDetectedRegion graphCandidate = Region(
            "candidate-graph",
            new OcrRectangle(58, 8, 16, 16),
            0.80,
            Evidence(componentCount: 1, textLikelihood: 0.12, structureLikelihood: 0.95, graph: true));
        var detector = new GraphStructureConsensusTextRegionDetector(
            new FixedDetector([
                Region("duplicate-low", new OcrRectangle(11, 10, 12, 10), 0.70),
                Region("text-high", new OcrRectangle(10, 10, 13, 10), 0.94),
                Region("graph", new OcrRectangle(60, 10, 10, 10), 0.99),
                Region("unmatched", new OcrRectangle(82, 42, 8, 8), 0.99),
            ], "model"),
            new FixedDetector([textCandidate, graphCandidate], "candidate"));

        IReadOnlyList<OcrDetectedRegion> result = await detector.DetectAsync(
            Image(),
            CancellationToken.None);

        Assert.HasCount(1, result);
        Assert.AreEqual("text-high", result[0].RegionId);
        Assert.AreSame(textCandidate.Evidence, result[0].Evidence);
    }

    [TestMethod]
    public async Task RejectsCandidateWithoutExplicitStructureEvidence()
    {
        var detector = new GraphStructureConsensusTextRegionDetector(
            new FixedDetector([Region("model", new OcrRectangle(10, 10, 10, 10), 0.9)], "model"),
            new FixedDetector([Region("candidate", new OcrRectangle(10, 10, 10, 10), 0.9)], "candidate"));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await detector.DetectAsync(Image(), CancellationToken.None));
    }

    [TestMethod]
    public async Task DoesNotSubstituteCandidateWhenModelReturnsNoRegion()
    {
        var detector = new GraphStructureConsensusTextRegionDetector(
            new FixedDetector([], "model"),
            new FixedDetector([
                Region(
                    "candidate",
                    new OcrRectangle(10, 10, 10, 10),
                    0.9,
                    Evidence(2, 0.9, 0.1, false)),
            ], "candidate"));

        IReadOnlyList<OcrDetectedRegion> result = await detector.DetectAsync(
            Image(),
            CancellationToken.None);

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task RejectsOverlapBelowFrozenCoefficient()
    {
        var detector = new GraphStructureConsensusTextRegionDetector(
            new FixedDetector([Region("model", new OcrRectangle(10, 10, 10, 10), 0.9)], "model"),
            new FixedDetector([
                Region(
                    "candidate",
                    new OcrRectangle(19, 10, 10, 10),
                    0.9,
                    Evidence(2, 0.9, 0.1, false)),
            ], "candidate"));

        IReadOnlyList<OcrDetectedRegion> result = await detector.DetectAsync(
            Image(),
            CancellationToken.None);

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task RealStructureCandidateDetectorRejectsRepeatedMarkersButKeepsGlyphLine()
    {
        OcrImage markerImage = Draw(
            static pixels =>
            {
                Fill(pixels, 80, 10, 12, 7, 7);
                Fill(pixels, 80, 24, 12, 7, 7);
            });
        var markerConsensus = new GraphStructureConsensusTextRegionDetector(
            new FixedDetector([
                Region("model-markers", new OcrRectangle(9, 11, 23, 9), 0.95),
            ], "model"),
            new ConnectedComponentTextRegionDetector(
                new ConnectedComponentTextRegionDetectorOptions { ForegroundThreshold = 128 }));

        IReadOnlyList<OcrDetectedRegion> markerResult = await markerConsensus.DetectAsync(
            markerImage,
            CancellationToken.None);

        Assert.IsEmpty(markerResult);

        OcrImage glyphImage = Draw(
            static pixels =>
            {
                Fill(pixels, 80, 10, 10, 2, 8);
                Fill(pixels, 80, 15, 10, 2, 8);
            });
        var glyphConsensus = new GraphStructureConsensusTextRegionDetector(
            new FixedDetector([
                Region("model-glyphs", new OcrRectangle(9, 9, 9, 10), 0.92),
            ], "model"),
            new ConnectedComponentTextRegionDetector(
                new ConnectedComponentTextRegionDetectorOptions { ForegroundThreshold = 128 }));

        IReadOnlyList<OcrDetectedRegion> glyphResult = await glyphConsensus.DetectAsync(
            glyphImage,
            CancellationToken.None);

        Assert.HasCount(1, glyphResult);
        OcrRegionEvidence glyphEvidence = glyphResult[0].Evidence ??
            throw new AssertFailedException("Consensus output omitted structure evidence.");
        Assert.IsFalse(glyphEvidence.LikelyGraphStructure);
        Assert.IsGreaterThanOrEqualTo(0.45, glyphEvidence.TextLikelihood);
    }

    [TestMethod]
    public void ConfigurationFingerprintBindsBothDetectorsAndThresholds()
    {
        var detector = new GraphStructureConsensusTextRegionDetector(
            new FixedDetector([], "model-fingerprint"),
            new FixedDetector([], "candidate-fingerprint"),
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                MinimumOverlapCoefficient = 0.60,
                MinimumTextLikelihood = 0.55,
            });

        StringAssert.Contains(detector.ConfigurationFingerprint, "graph-structure-consensus-v1:0.6:0.55");
        StringAssert.Contains(detector.ConfigurationFingerprint, "model=model-fingerprint");
        StringAssert.Contains(detector.ConfigurationFingerprint, "candidate=candidate-fingerprint");
    }

    [TestMethod]
    public async Task MatchedComponentGeometryUsesMatchedPolygonOrientationAndSourceIdentityEvidence()
    {
        OcrDetectedRegion model = Region(
            "model-source",
            new OcrRectangle(10, 10, 20, 14),
            0.91,
            orientationDegrees: 17);
        OcrDetectedRegion candidate = Region(
            "component-source",
            new OcrRectangle(12, 11, 9, 8),
            0.72,
            Evidence(3, 0.92, 0.08, false),
            orientationDegrees: -6);
        var detector = new GraphStructureConsensusTextRegionDetector(
            new FixedDetector([model], "model"),
            new FixedDetector([candidate], "candidate"),
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                OutputGeometry = GraphStructureConsensusGeometry.MatchedComponent,
            });

        OcrDetectedRegion result = AssertExactlyOne(
            await detector.DetectAsync(Image(), CancellationToken.None));

        Assert.AreSame(candidate.Polygon, result.Polygon);
        Assert.AreEqual(candidate.OrientationDegrees, result.OrientationDegrees);
        Assert.AreEqual(model.DetectionConfidence, result.DetectionConfidence);
        Assert.IsTrue(Guid.TryParseExact(result.RegionId, "D", out _));
        Assert.AreNotEqual(model.RegionId, result.RegionId);
        Assert.AreNotEqual(candidate.RegionId, result.RegionId);
        Assert.IsNotNull(result.Evidence);
        CollectionAssert.Contains(result.Evidence.Reasons.ToArray(), "consensus_model_region_id:model-source");
        CollectionAssert.Contains(result.Evidence.Reasons.ToArray(), "consensus_component_region_id:component-source");
    }

    [TestMethod]
    public async Task MatchedComponentIdentityDeterministicallyBindsBothSourceIdsAndPolygon()
    {
        async Task<string> DetectId(string modelId, string componentId, OcrRectangle componentBounds)
        {
            var detector = new GraphStructureConsensusTextRegionDetector(
                new FixedDetector([Region(modelId, new OcrRectangle(10, 10, 20, 14), 0.91)], "model"),
                new FixedDetector([
                    Region(componentId, componentBounds, 0.72, Evidence(3, 0.92, 0.08, false)),
                ], "candidate"),
                new GraphStructureConsensusTextRegionDetectorOptions
                {
                    OutputGeometry = GraphStructureConsensusGeometry.MatchedComponent,
                });
            return AssertExactlyOne(await detector.DetectAsync(Image(), CancellationToken.None)).RegionId;
        }

        string expected = await DetectId("model-a", "component-a", new OcrRectangle(12, 11, 9, 8));

        Assert.AreEqual(expected, await DetectId("model-a", "component-a", new OcrRectangle(12, 11, 9, 8)));
        Assert.AreNotEqual(expected, await DetectId("model-b", "component-a", new OcrRectangle(12, 11, 9, 8)));
        Assert.AreNotEqual(expected, await DetectId("model-a", "component-b", new OcrRectangle(12, 11, 9, 8)));
        Assert.AreNotEqual(expected, await DetectId("model-a", "component-a", new OcrRectangle(12, 11, 8, 8)));
    }

    [TestMethod]
    public void GeometryModePreservesDefaultFingerprintAndRejectsUnknownValues()
    {
        var model = new FixedDetector([], "model-fingerprint");
        var candidate = new FixedDetector([], "candidate-fingerprint");
        var existing = new GraphStructureConsensusTextRegionDetector(model, candidate);
        var componentGeometry = new GraphStructureConsensusTextRegionDetector(
            model,
            candidate,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                OutputGeometry = GraphStructureConsensusGeometry.MatchedComponent,
            });

        Assert.AreEqual(
            "graph-structure-consensus-v1:0.5:0.45:model=model-fingerprint:candidate=candidate-fingerprint",
            existing.ConfigurationFingerprint);
        Assert.AreEqual(
            "graph-structure-consensus-component-geometry-v1:0.5:0.45:model=model-fingerprint:candidate=candidate-fingerprint",
            componentGeometry.ConfigurationFingerprint);
        Assert.AreEqual(
            GraphStructureConsensusTextRegionDetector.CompositionVersion,
            GraphStructureConsensusTextRegionDetector.GetCompositionVersion(
                GraphStructureConsensusGeometry.ModelPolygon));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            GraphStructureConsensusTextRegionDetector.GetCompositionVersion(
                (GraphStructureConsensusGeometry)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new GraphStructureConsensusTextRegionDetector(
                model,
                candidate,
                new GraphStructureConsensusTextRegionDetectorOptions
                {
                    OutputGeometry = (GraphStructureConsensusGeometry)99,
                }));
    }

    [TestMethod]
    public async Task CancellationStopsBeforeEitherDetectorRuns()
    {
        var model = new FixedDetector([], "model");
        var candidate = new FixedDetector([], "candidate");
        var detector = new GraphStructureConsensusTextRegionDetector(model, candidate);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await detector.DetectAsync(Image(), cancellation.Token));
        Assert.AreEqual(0, model.CallCount);
        Assert.AreEqual(0, candidate.CallCount);
    }

    private static OcrImage Image() => new(
        100,
        60,
        100,
        new byte[6000],
        OcrSourceImage.Original,
        OcrFrameTransform.Identity,
        CanonicalOriginalWidth: 100,
        CanonicalOriginalHeight: 60);

    private static OcrImage Draw(Action<byte[]> draw)
    {
        var pixels = Enumerable.Repeat((byte)255, 80 * 40).ToArray();
        draw(pixels);
        return new OcrImage(
            80,
            40,
            80,
            pixels,
            OcrSourceImage.Original,
            OcrFrameTransform.Identity,
            CanonicalOriginalWidth: 80,
            CanonicalOriginalHeight: 40);
    }

    private static void Fill(
        byte[] pixels,
        int stride,
        int left,
        int top,
        int width,
        int height)
    {
        for (int y = top; y < top + height; y++)
        {
            for (int x = left; x < left + width; x++)
            {
                pixels[(y * stride) + x] = 0;
            }
        }
    }

    private static OcrDetectedRegion Region(
        string id,
        OcrRectangle bounds,
        double confidence,
        OcrRegionEvidence? evidence = null,
        double orientationDegrees = 0) => new(
            id,
            OcrPolygon.FromRectangle(bounds),
            orientationDegrees,
            confidence,
            Evidence: evidence);

    private static OcrDetectedRegion AssertExactlyOne(IReadOnlyList<OcrDetectedRegion> regions)
    {
        Assert.HasCount(1, regions);
        return regions[0];
    }

    private static OcrRegionEvidence Evidence(
        int componentCount,
        double textLikelihood,
        double structureLikelihood,
        bool graph) => new(
            componentCount,
            InkDensity: 0.25,
            textLikelihood,
            structureLikelihood,
            graph,
            graph ? ["graph_structure"] : ["text_candidate"]);

    private sealed class FixedDetector(
        IReadOnlyList<OcrDetectedRegion> regions,
        string fingerprint) : ITextRegionDetector
    {
        public int CallCount { get; private set; }

        public string ConfigurationFingerprint => fingerprint;

        public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
            OcrImage image,
            CancellationToken cancellationToken)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(regions);
        }
    }
}
