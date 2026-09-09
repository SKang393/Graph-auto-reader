// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class GraphStructureAdvisoryInitialDbContourTests
{
    [TestMethod]
    public async Task AdvisoryEmitsEveryAtomicInitialContourDespiteStructureAdmissionFailures()
    {
        OcrDetectedRegion high = Region(
            "model-high",
            new OcrRectangle(10, 10, 20, 10),
            confidence: 0.91,
            orientationDegrees: 7);
        OcrDetectedRegion competing = Region(
            "model-competing",
            new OcrRectangle(20, 10, 20, 10),
            confidence: 0.81,
            orientationDegrees: -3);
        OcrDetectedRegion graphOnly = Region(
            "model-graph-only",
            new OcrRectangle(60, 10, 15, 10),
            confidence: 0.73,
            orientationDegrees: 11);
        OcrPolygon highInitial = OcrPolygon.FromRectangle(new OcrRectangle(11, 11, 5, 6));
        OcrPolygon competingInitial = OcrPolygon.FromRectangle(new OcrRectangle(22, 11, 6, 6));
        OcrPolygon graphInitial = OcrPolygon.FromRectangle(new OcrRectangle(62, 11, 5, 6));
        var atomic = Atomic(
            [graphOnly, competing, high],
            [
                Contour(graphOnly, graphInitial),
                Contour(competing, competingInitial),
                Contour(high, highInitial),
            ],
            inputFill: 31);
        OcrDetectedRegion sharedCandidate = Candidate(
            "shared-component",
            new OcrRectangle(20, 10, 10, 10),
            textLikelihood: 0.92,
            graph: false);
        OcrDetectedRegion graphCandidate = Candidate(
            "graph-component",
            new OcrRectangle(60, 10, 15, 10),
            textLikelihood: 0.02,
            graph: true);
        var structure = new FixedDetector([sharedCandidate, graphCandidate], "structure");
        var detector = Advisory(atomic, structure);
        OcrImage original = Image(31);
        OcrImage masked = Image(241);

        IReadOnlyList<OcrDetectedRegion> output = await detector.DetectAsync(
            original,
            masked,
            CancellationToken.None);
        double[] expectedConfidences = [0.91, 0.81, 0.73];
        double[] expectedOrientations = [7, -3, 11];

        Assert.HasCount(3, output);
        CollectionAssert.AreEqual(
            new[] { highInitial, competingInitial, graphInitial },
            output.Select(static region => region.Polygon).ToArray());
        CollectionAssert.AreEqual(
            expectedConfidences,
            output.Select(static region => region.DetectionConfidence).ToArray());
        CollectionAssert.AreEqual(
            expectedOrientations,
            output.Select(static region => region.OrientationDegrees).ToArray());
        Assert.AreSame(original, atomic.LastImage);
        Assert.AreSame(masked, structure.LastImage);
        Assert.AreEqual(1, atomic.AtomicCallCount);
        Assert.AreEqual(0, atomic.StandardCallCount);
        Assert.AreEqual(1, structure.CallCount);

        OcrDetectedRegion associated = output[0];
        Assert.AreEqual(1, associated.Evidence!.ComponentCount);
        Assert.AreEqual(0.91, associated.Evidence.TextLikelihood);
        CollectionAssert.Contains(
            associated.Evidence.Reasons.ToArray(),
            "consensus_component_region_id:shared-component");
        Assert.IsFalse(output[1].Evidence!.Reasons.Any(static reason =>
            reason.StartsWith("consensus_component_region_id:", StringComparison.Ordinal)));
        Assert.IsFalse(output[2].Evidence!.Reasons.Any(static reason =>
            reason.StartsWith("consensus_component_region_id:", StringComparison.Ordinal)));
        Assert.IsTrue(output.All(static region => region.Evidence!.Reasons.Contains(
            "consensus_admission:advisory",
            StringComparer.Ordinal)));
        Assert.AreEqual(3, output.Select(static region => region.RegionId).Distinct().Count());
        Assert.IsTrue(output.All(static region => Guid.TryParseExact(region.RegionId, "D", out _)));
    }

    [TestMethod]
    public async Task AdvisoryIdentityDoesNotDependOnStructureAssociationAvailability()
    {
        OcrDetectedRegion model = Region(
            "model",
            new OcrRectangle(10, 10, 20, 10),
            confidence: 0.87,
            orientationDegrees: 5);
        OcrPolygon initial = OcrPolygon.FromRectangle(new OcrRectangle(12, 11, 7, 6));
        OcrDbAcceptedContourGeometry contour = Contour(model, initial);
        var withAssociation = Advisory(
            Atomic([model], [contour], inputFill: 17),
            new FixedDetector([
                Candidate(
                    "component",
                    new OcrRectangle(10, 10, 20, 10),
                    textLikelihood: 0.95,
                    graph: false),
            ], "structure-associated"));
        var withoutAssociation = Advisory(
            Atomic([model], [contour], inputFill: 17),
            new FixedDetector([], "structure-empty"));
        var required = new GraphStructureConsensusTextRegionDetector(
            Atomic([model], [contour], inputFill: 17),
            new FixedDetector([
                Candidate(
                    "component",
                    new OcrRectangle(10, 10, 20, 10),
                    textLikelihood: 0.95,
                    graph: false),
            ], "structure-required"),
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                ModelInput = GraphStructureModelInput.Original,
                OutputGeometry = GraphStructureConsensusGeometry.InitialDbContour,
            });

        OcrDetectedRegion associated = AssertExactlyOne(await withAssociation.DetectAsync(
            Image(17),
            Image(229),
            CancellationToken.None));
        OcrDetectedRegion unassociated = AssertExactlyOne(await withoutAssociation.DetectAsync(
            Image(17),
            Image(229),
            CancellationToken.None));
        OcrDetectedRegion requiredOutput = AssertExactlyOne(await required.DetectAsync(
            Image(17),
            Image(229),
            CancellationToken.None));

        Assert.AreEqual(associated.RegionId, unassociated.RegionId);
        Assert.AreNotEqual(requiredOutput.RegionId, associated.RegionId);
        Assert.AreSame(initial, associated.Polygon);
        Assert.AreSame(initial, unassociated.Polygon);
        Assert.AreEqual(model.DetectionConfidence, associated.DetectionConfidence);
        Assert.AreEqual(model.DetectionConfidence, unassociated.DetectionConfidence);
        Assert.AreEqual(model.OrientationDegrees, associated.OrientationDegrees);
        Assert.AreEqual(model.OrientationDegrees, unassociated.OrientationDegrees);
        CollectionAssert.Contains(
            associated.Evidence!.Reasons.ToArray(),
            "consensus_component_region_id:component");
        Assert.IsFalse(unassociated.Evidence!.Reasons.Any(static reason =>
            reason.StartsWith("consensus_component_region_id:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RequiredInitialContourStillSuppressesUnmatchedAtomicContours()
    {
        OcrDetectedRegion matched = Region(
            "matched-model",
            new OcrRectangle(10, 10, 20, 10),
            confidence: 0.91);
        OcrDetectedRegion unmatched = Region(
            "unmatched-model",
            new OcrRectangle(50, 10, 20, 10),
            confidence: 0.88);
        OcrPolygon matchedInitial = OcrPolygon.FromRectangle(new OcrRectangle(11, 11, 5, 6));
        OcrPolygon unmatchedInitial = OcrPolygon.FromRectangle(new OcrRectangle(51, 11, 5, 6));
        var atomic = Atomic(
            [matched, unmatched],
            [Contour(matched, matchedInitial), Contour(unmatched, unmatchedInitial)],
            inputFill: 9);
        var detector = new GraphStructureConsensusTextRegionDetector(
            atomic,
            new FixedDetector([
                Candidate(
                    "component",
                    new OcrRectangle(10, 10, 20, 10),
                    textLikelihood: 0.9,
                    graph: false),
            ], "structure"),
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                ModelInput = GraphStructureModelInput.Original,
                OutputGeometry = GraphStructureConsensusGeometry.InitialDbContour,
            });

        OcrDetectedRegion output = AssertExactlyOne(await detector.DetectAsync(
            Image(9),
            Image(222),
            CancellationToken.None));

        Assert.AreSame(matchedInitial, output.Polygon);
        Assert.AreEqual(
            "graph-structure-consensus-initial-db-contour-original-model-input-v1:0.5:0.45:model=atomic:candidate=structure",
            detector.ConfigurationFingerprint);
    }

    [TestMethod]
    public void AdvisoryHasDistinctIdentityAndFailsClosedOutsidePreregisteredMode()
    {
        var atomic = Atomic([], [], inputFill: 0);
        var structure = new FixedDetector([], "structure");
        var advisory = Advisory(atomic, structure);
        var required = new GraphStructureConsensusTextRegionDetector(
            atomic,
            structure,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                ModelInput = GraphStructureModelInput.Original,
                OutputGeometry = GraphStructureConsensusGeometry.InitialDbContour,
            });

        Assert.AreEqual(
            GraphStructureConsensusTextRegionDetector.AdvisoryInitialDbContourOriginalModelInputCompositionVersion,
            GraphStructureConsensusTextRegionDetector.GetCompositionVersion(
                GraphStructureConsensusGeometry.InitialDbContour,
                GraphStructureModelInput.Original,
                GraphStructureConsensusAdmission.Advisory));
        Assert.AreEqual(
            "graph-structure-consensus-advisory-initial-db-contour-original-model-input-v1:0.5:0.45:model=atomic:candidate=structure",
            advisory.ConfigurationFingerprint);
        Assert.AreEqual(
            "graph-structure-consensus-initial-db-contour-original-model-input-v1:0.5:0.45:model=atomic:candidate=structure",
            required.ConfigurationFingerprint);
        Assert.AreNotEqual(required.ConfigurationFingerprint, advisory.ConfigurationFingerprint);

        Assert.ThrowsExactly<ArgumentException>(() => new GraphStructureConsensusTextRegionDetector(
            atomic,
            structure,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                ModelInput = GraphStructureModelInput.Original,
                OutputGeometry = GraphStructureConsensusGeometry.ModelPolygon,
                Admission = GraphStructureConsensusAdmission.Advisory,
            }));
        Assert.ThrowsExactly<ArgumentException>(() => new GraphStructureConsensusTextRegionDetector(
            atomic,
            structure,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                OutputGeometry = GraphStructureConsensusGeometry.InitialDbContour,
                Admission = GraphStructureConsensusAdmission.Advisory,
            }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GraphStructureConsensusTextRegionDetector(
            atomic,
            structure,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                Admission = (GraphStructureConsensusAdmission)99,
            }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            GraphStructureConsensusTextRegionDetector.GetCompositionVersion(
                GraphStructureConsensusGeometry.InitialDbContour,
                GraphStructureModelInput.Original,
                (GraphStructureConsensusAdmission)99));
    }

    private static GraphStructureConsensusTextRegionDetector Advisory(
        IAtomicDbGeometryTextRegionDetector model,
        ITextRegionDetector structure) => new(
            model,
            structure,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                ModelInput = GraphStructureModelInput.Original,
                OutputGeometry = GraphStructureConsensusGeometry.InitialDbContour,
                Admission = GraphStructureConsensusAdmission.Advisory,
            });

    private static AtomicDetector Atomic(
        IReadOnlyList<OcrDetectedRegion> regions,
        IReadOnlyList<OcrDbAcceptedContourGeometry> contours,
        byte inputFill) => new(
            regions,
            new OcrDbGeometryObservation(
                Convert.ToHexStringLower(SHA256.HashData(
                    Enumerable.Repeat(inputFill, 100 * 60).ToArray())),
                100,
                60,
                100,
                60,
                contours));

    private static OcrDetectedRegion Region(
        string id,
        OcrRectangle bounds,
        double confidence,
        double orientationDegrees = 0) => new(
            id,
            OcrPolygon.FromRectangle(bounds),
            orientationDegrees,
            confidence,
            Evidence: new OcrRegionEvidence(
                ComponentCount: 1,
                InkDensity: 0.25,
                TextLikelihood: confidence,
                StructureLikelihood: 1 - confidence,
                LikelyGraphStructure: false,
                Reasons: Array.AsReadOnly(["onnx_db_text_probability"])));

    private static OcrDetectedRegion Candidate(
        string id,
        OcrRectangle bounds,
        double textLikelihood,
        bool graph) => new(
            id,
            OcrPolygon.FromRectangle(bounds),
            OrientationDegrees: -17,
            DetectionConfidence: 0.99,
            Evidence: new OcrRegionEvidence(
                ComponentCount: 7,
                InkDensity: 0.75,
                TextLikelihood: textLikelihood,
                StructureLikelihood: 1 - textLikelihood,
                LikelyGraphStructure: graph,
                Reasons: Array.AsReadOnly([graph ? "graph_structure" : "text_candidate"])));

    private static OcrDbAcceptedContourGeometry Contour(
        OcrDetectedRegion region,
        OcrPolygon initial) => new(
            region.RegionId,
            initial,
            region.Polygon,
            region.DetectionConfidence,
            region.Evidence!.InkDensity);

    private static OcrImage Image(byte fill) => new(
        100,
        60,
        100,
        Enumerable.Repeat(fill, 100 * 60).ToArray(),
        OcrSourceImage.Original,
        OcrFrameTransform.Identity,
        CanonicalOriginalWidth: 100,
        CanonicalOriginalHeight: 60);

    private static T AssertExactlyOne<T>(IReadOnlyList<T> values)
    {
        Assert.HasCount(1, values);
        return values[0];
    }

    private sealed class AtomicDetector(
        IReadOnlyList<OcrDetectedRegion> regions,
        OcrDbGeometryObservation geometry) : IAtomicDbGeometryTextRegionDetector
    {
        public bool SupportsAtomicDbGeometry => true;

        public int StandardCallCount { get; private set; }

        public int AtomicCallCount { get; private set; }

        public OcrImage? LastImage { get; private set; }

        public string ConfigurationFingerprint => "atomic";

        public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
            OcrImage image,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StandardCallCount++;
            LastImage = image;
            return ValueTask.FromResult(regions);
        }

        public ValueTask<OcrAtomicDbDetection> DetectWithAtomicDbGeometryAsync(
            OcrImage image,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AtomicCallCount++;
            LastImage = image;
            return ValueTask.FromResult(new OcrAtomicDbDetection(regions, geometry));
        }
    }

    private sealed class FixedDetector(
        IReadOnlyList<OcrDetectedRegion> regions,
        string fingerprint) : ITextRegionDetector
    {
        public int CallCount { get; private set; }

        public OcrImage? LastImage { get; private set; }

        public string ConfigurationFingerprint => fingerprint;

        public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
            OcrImage image,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastImage = image;
            return ValueTask.FromResult(regions);
        }
    }
}
