// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.Inference;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class GraphStructureConsensusInitialDbContourTests
{
    [TestMethod]
    public async Task InitialContourPairsWithExpandedPolygonThenSubstitutesGeometry()
    {
        OcrPolygon initial = OcrPolygon.FromRectangle(new OcrRectangle(10, 10, 5, 6));
        OcrDetectedRegion model = Region(
            "raw-model",
            new OcrRectangle(10, 10, 20, 10),
            0.91,
            ModelEvidence(),
            orientationDegrees: 7);
        var atomicModel = CreateAtomicDetector([model], [Contour(model, initial)], "atomic-model", inputFill: 41);
        OcrDetectedRegion component = Region(
            "structure-component",
            new OcrRectangle(25, 10, 5, 10),
            0.72,
            CandidateEvidence(),
            orientationDegrees: -4);
        var structure = new FixedDetector([component], "structure");
        var detector = InitialDetector(atomicModel, structure);
        OcrImage original = Image(fill: 41);
        OcrImage masked = Image(fill: 239);

        OcrDetectedRegion result = AssertExactlyOne(await detector.DetectAsync(
            original,
            masked,
            CancellationToken.None));

        Assert.AreSame(initial, result.Polygon);
        Assert.AreEqual(7, result.OrientationDegrees);
        Assert.AreEqual(model.DetectionConfidence, result.DetectionConfidence);
        Assert.AreEqual(1, atomicModel.AtomicCallCount);
        Assert.AreEqual(0, atomicModel.StandardCallCount);
        Assert.AreSame(original, atomicModel.LastImage);
        Assert.AreSame(masked, structure.LastImage);
        Assert.AreNotEqual(model.RegionId, result.RegionId);
        Assert.IsTrue(Guid.TryParseExact(result.RegionId, "D", out _));
        CollectionAssert.Contains(
            result.Evidence!.Reasons.ToArray(),
            "consensus_geometry:initial_db_contour");
        CollectionAssert.Contains(
            result.Evidence.Reasons.ToArray(),
            "consensus_model_region_id:raw-model");
        CollectionAssert.Contains(
            result.Evidence.Reasons.ToArray(),
            "consensus_component_region_id:structure-component");
    }

    [TestMethod]
    public async Task ExistingOriginalModelGeometryRemainsRawAndUsesStandardDetectorPath()
    {
        OcrPolygon initial = OcrPolygon.FromRectangle(new OcrRectangle(11, 11, 6, 5));
        OcrDetectedRegion model = Region(
            "raw-model",
            new OcrRectangle(10, 10, 20, 10),
            0.91,
            ModelEvidence());
        var atomicModel = CreateAtomicDetector([model], [Contour(model, initial)], "atomic-model", inputFill: 17);
        var structure = new FixedDetector([
            Region("component", new OcrRectangle(12, 10, 8, 8), 0.8, CandidateEvidence()),
        ], "structure");
        var detector = new GraphStructureConsensusTextRegionDetector(
            atomicModel,
            structure,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                ModelInput = GraphStructureModelInput.Original,
                OutputGeometry = GraphStructureConsensusGeometry.ModelPolygon,
            });

        OcrDetectedRegion result = AssertExactlyOne(await detector.DetectAsync(
            Image(fill: 17),
            Image(fill: 223),
            CancellationToken.None));

        Assert.AreEqual("raw-model", result.RegionId);
        Assert.AreSame(model.Polygon, result.Polygon);
        Assert.AreEqual(1, atomicModel.StandardCallCount);
        Assert.AreEqual(0, atomicModel.AtomicCallCount);
        CollectionAssert.AreEqual(CandidateEvidence().Reasons.ToArray(), result.Evidence!.Reasons.ToArray());
    }

    [TestMethod]
    public void InitialContourRequiresOriginalInputAndAtomicDbCapability()
    {
        var model = CreateAtomicDetector([], [], "atomic", inputFill: 0);
        var structure = new FixedDetector([], "structure");

        Assert.ThrowsExactly<ArgumentException>(() =>
            new GraphStructureConsensusTextRegionDetector(
                model,
                structure,
                new GraphStructureConsensusTextRegionDetectorOptions
                {
                    OutputGeometry = GraphStructureConsensusGeometry.InitialDbContour,
                }));
        Assert.ThrowsExactly<ArgumentException>(() =>
            InitialDetector(new FixedDetector([], "plain"), structure));
        Assert.ThrowsExactly<ArgumentException>(() =>
            InitialDetector(new AtomicDetector([], EmptyGeometry(), "unsupported", supports: false), structure));
    }

    [TestMethod]
    public void InitialContourHasDistinctCompositionAndConfigurationIdentity()
    {
        var model = CreateAtomicDetector([], [], "atomic-model", inputFill: 0);
        var structure = new FixedDetector([], "structure-model");
        var initial = InitialDetector(model, structure);
        var previousOriginal = new GraphStructureConsensusTextRegionDetector(
            model,
            structure,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                ModelInput = GraphStructureModelInput.Original,
            });

        Assert.AreEqual(
            GraphStructureConsensusTextRegionDetector.InitialDbContourOriginalModelInputCompositionVersion,
            GraphStructureConsensusTextRegionDetector.GetCompositionVersion(
                GraphStructureConsensusGeometry.InitialDbContour,
                GraphStructureModelInput.Original));
        Assert.AreEqual(
            "graph-structure-consensus-original-model-input-v1:0.5:0.45:model=atomic-model:candidate=structure-model",
            previousOriginal.ConfigurationFingerprint);
        Assert.AreEqual(
            "graph-structure-consensus-initial-db-contour-original-model-input-v1:0.5:0.45:model=atomic-model:candidate=structure-model",
            initial.ConfigurationFingerprint);
        Assert.AreNotEqual(previousOriginal.ConfigurationFingerprint, initial.ConfigurationFingerprint);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            GraphStructureConsensusTextRegionDetector.GetCompositionVersion(
                GraphStructureConsensusGeometry.InitialDbContour));
    }

    [TestMethod]
    public async Task InvalidAtomicMappingsFailBeforeStructureDetection()
    {
        OcrDetectedRegion model = Region(
            "raw-model",
            new OcrRectangle(10, 10, 20, 10),
            0.91,
            ModelEvidence());
        OcrPolygon validInitial = OcrPolygon.FromRectangle(new OcrRectangle(11, 11, 6, 5));
        OcrPolygon mismatchedExpanded = OcrPolygon.FromRectangle(new OcrRectangle(10, 10, 19, 10));
        var degenerateInitial = new OcrPolygon([
            new OcrPoint(1, 1),
            new OcrPoint(2, 2),
            new OcrPoint(3, 3),
            new OcrPoint(4, 4),
        ]);
        OcrDbAcceptedContourGeometry[] valid = [Contour(model, validInitial)];
        OcrDbAcceptedContourGeometry[][] invalidMappings =
        [
            [],
            [valid[0] with { ReturnedRegionId = "foreign-model" }],
            [valid[0] with { ExpandedPolygon = mismatchedExpanded }],
            [valid[0] with { InitialPolygon = degenerateInitial }],
            [valid[0] with { DetectionConfidence = 0.90 }],
            [valid[0] with { InkDensity = 0.24 }],
        ];

        foreach (OcrDbAcceptedContourGeometry[] mappings in invalidMappings)
        {
            var structure = new FixedDetector([], "structure");
            var detector = InitialDetector(CreateAtomicDetector([model], mappings, "atomic", inputFill: 1), structure);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await detector.DetectAsync(Image(1), Image(2), CancellationToken.None));
            Assert.AreEqual(0, structure.CallCount);
        }
    }

    [TestMethod]
    public void AtomicResultDefensivelyFreezesRegionAndGeometryCollections()
    {
        OcrDetectedRegion model = Region(
            "raw-model",
            new OcrRectangle(10, 10, 20, 10),
            0.91,
            ModelEvidence());
        var regions = new List<OcrDetectedRegion> { model };
        var contours = new List<OcrDbAcceptedContourGeometry>
        {
            Contour(model, OcrPolygon.FromRectangle(new OcrRectangle(11, 11, 6, 5))),
        };
        var geometry = Geometry(contours);
        var result = new OcrAtomicDbDetection(regions, geometry);
        regions.Clear();
        contours.Clear();

        Assert.HasCount(1, result.Regions);
        Assert.HasCount(1, result.Geometry.AcceptedContours);
        Assert.IsFalse(result.Regions is ICollection<OcrDetectedRegion> { IsReadOnly: false });
        Assert.IsTrue(((IList<OcrDbAcceptedContourGeometry>)result.Geometry.AcceptedContours).IsReadOnly);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<OcrDbAcceptedContourGeometry>)result.Geometry.AcceptedContours)
                .Add(Contour(model, model.Polygon)));
    }

    [TestMethod]
    public async Task LocalDbDetectorReturnsBothGeometriesFromOneInferenceWithoutObserver()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "atomic-db.onnx");
        await File.WriteAllBytesAsync(modelPath, [4, 8, 1, 6]);
        try
        {
            const int width = 32;
            const int height = 32;
            var output = new float[width * height];
            FillRectangle(output, width, 8, 8, 14, 14, 0.9f);
            var factory = new CountingProbabilityMapSessionFactory(output);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(runtime, DbOptions(Identity(modelPath)));

            OcrAtomicDbDetection result = await detector.DetectWithAtomicDbGeometryAsync(
                Image(width, height, fill: 0),
                CancellationToken.None);

            Assert.IsTrue(detector.SupportsAtomicDbGeometry);
            Assert.AreEqual(1, factory.RunCount);
            OcrDetectedRegion region = AssertExactlyOne(result.Regions);
            OcrDbAcceptedContourGeometry contour = AssertExactlyOne(result.Geometry.AcceptedContours);
            Assert.AreEqual(region.RegionId, contour.ReturnedRegionId);
            Assert.AreSame(region.Polygon, contour.ExpandedPolygon);
            Assert.AreEqual(new OcrRectangle(8, 8, 5, 5), contour.InitialPolygon.Bounds);
            Assert.AreEqual(new OcrRectangle(6, 6, 9, 9), contour.ExpandedPolygon.Bounds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LocalDenseDetectorRejectsAtomicGeometryBeforeInference()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "dense.onnx");
        await File.WriteAllBytesAsync(modelPath, [1, 2, 3, 4]);
        try
        {
            var factory = new CountingProbabilityMapSessionFactory(new float[32 * 32]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with
                {
                    PostprocessAlgorithm = OcrDetectionPostprocessAlgorithm.DenseProbabilityComponentsV1,
                });

            Assert.IsFalse(detector.SupportsAtomicDbGeometry);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await detector.DetectWithAtomicDbGeometryAsync(Image(32, 32, 0), CancellationToken.None));
            Assert.AreEqual(0, factory.RunCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static GraphStructureConsensusTextRegionDetector InitialDetector(
        ITextRegionDetector model,
        ITextRegionDetector structure) => new(
            model,
            structure,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                ModelInput = GraphStructureModelInput.Original,
                OutputGeometry = GraphStructureConsensusGeometry.InitialDbContour,
            });

    private static AtomicDetector CreateAtomicDetector(
        IReadOnlyList<OcrDetectedRegion> regions,
        IReadOnlyList<OcrDbAcceptedContourGeometry> contours,
        string fingerprint,
        byte inputFill) => new(regions, Geometry(contours, inputFill), fingerprint, supports: true);

    private static OcrDbGeometryObservation Geometry(
        IReadOnlyList<OcrDbAcceptedContourGeometry> contours,
        byte inputFill = 0) => new(
            Convert.ToHexStringLower(SHA256.HashData(
                Enumerable.Repeat(inputFill, 100 * 60).ToArray())),
            100,
            60,
            100,
            60,
            contours);

    private static OcrDbGeometryObservation EmptyGeometry() => Geometry([]);

    private static OcrDbAcceptedContourGeometry Contour(
        OcrDetectedRegion region,
        OcrPolygon initial) => new(
            region.RegionId,
            initial,
            region.Polygon,
            region.DetectionConfidence,
            region.Evidence!.InkDensity);

    private static OcrDetectedRegion Region(
        string id,
        OcrRectangle bounds,
        double confidence,
        OcrRegionEvidence evidence,
        double orientationDegrees = 0) => new(
            id,
            OcrPolygon.FromRectangle(bounds),
            orientationDegrees,
            confidence,
            Evidence: evidence);

    private static OcrRegionEvidence ModelEvidence() => new(
        ComponentCount: 1,
        InkDensity: 0.25,
        TextLikelihood: 0.91,
        StructureLikelihood: 0.09,
        LikelyGraphStructure: false,
        Reasons: Array.AsReadOnly(["onnx_db_text_probability"]));

    private static OcrRegionEvidence CandidateEvidence() => new(
        ComponentCount: 2,
        InkDensity: 0.30,
        TextLikelihood: 0.92,
        StructureLikelihood: 0.08,
        LikelyGraphStructure: false,
        Reasons: Array.AsReadOnly(["text_candidate"]));

    private static OcrImage Image(byte fill) => Image(100, 60, fill);

    private static OcrImage Image(int width, int height, byte fill) => new(
        width,
        height,
        width,
        Enumerable.Repeat(fill, checked(width * height)).ToArray(),
        OcrSourceImage.Original,
        OcrFrameTransform.Identity,
        CanonicalOriginalWidth: width,
        CanonicalOriginalHeight: height);

    private static T AssertExactlyOne<T>(IReadOnlyList<T> values)
    {
        Assert.HasCount(1, values);
        return values[0];
    }

    private static LocalOnnxTextRegionDetectorOptions DbOptions(ModelIdentity model) => new(model)
    {
        MaximumSideLength = 32,
        DimensionMultiple = 1,
        InputChannels = 3,
        PostprocessAlgorithm = OcrDetectionPostprocessAlgorithm.DbPostprocessV1,
        ProbabilityThreshold = 0.30f,
        BoxConfidenceThreshold = 0.60f,
        UnclipRatio = 1.5,
        MinimumComponentArea = 3,
        MinimumSideLength = 2,
        MaximumRegions = 20,
        AllowedProviders = [InferenceProvider.Cpu],
        BypassCache = false,
    };

    private static ModelIdentity Identity(string path) => new(
        "fixture-atomic-db",
        "1.0.0",
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
        path);

    private static string CreateDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "GraphReaderOcrInitialDbContourTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static InferenceRuntime CreateRuntime(
        string directory,
        CountingProbabilityMapSessionFactory factory)
    {
        var registry = new OnnxSessionRegistry(
            new FakeExecutionProviderDiscovery("CPUExecutionProvider"),
            new WindowsExecutionProviderPolicy(),
            factory,
            CpuThreadConfiguration.Create(1));
        return new InferenceRuntime(
            registry,
            new BoundedInferenceScheduler(capacity: 2, workerCount: 1),
            new ContentAddressedStageCache(Path.Combine(directory, "cache")));
    }

    private static void FillRectangle(
        float[] values,
        int width,
        int left,
        int top,
        int right,
        int bottom,
        float value)
    {
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                values[(y * width) + x] = value;
            }
        }
    }

    private sealed class AtomicDetector(
        IReadOnlyList<OcrDetectedRegion> regions,
        OcrDbGeometryObservation geometry,
        string fingerprint,
        bool supports) : IAtomicDbGeometryTextRegionDetector
    {
        public bool SupportsAtomicDbGeometry { get; } = supports;

        public int StandardCallCount { get; private set; }

        public int AtomicCallCount { get; private set; }

        public OcrImage? LastImage { get; private set; }

        public string ConfigurationFingerprint => fingerprint;

        public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
            OcrImage image,
            CancellationToken cancellationToken)
        {
            StandardCallCount++;
            LastImage = image;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(regions);
        }

        public ValueTask<OcrAtomicDbDetection> DetectWithAtomicDbGeometryAsync(
            OcrImage image,
            CancellationToken cancellationToken)
        {
            AtomicCallCount++;
            LastImage = image;
            cancellationToken.ThrowIfCancellationRequested();
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
            CallCount++;
            LastImage = image;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(regions);
        }
    }

    private sealed class CountingProbabilityMapSessionFactory(float[] output) : IInferenceSessionFactory
    {
        private readonly float[] output = (float[])output.Clone();

        public int RunCount { get; private set; }

        public ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IInferenceSession>(new Session(this, output, provider));
        }

        private sealed class Session(
            CountingProbabilityMapSessionFactory owner,
            float[] output,
            InferenceProvider provider) : IInferenceSession
        {
            private readonly float[] output = (float[])output.Clone();

            public InferenceProvider Provider { get; } = provider;

            public ValueTask<InferenceExecution> RunAsync(
                InferenceInput input,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.RunCount++;
                return ValueTask.FromResult(new InferenceExecution(
                    Array.AsReadOnly((float[])output.Clone()),
                    Provider,
                    new StageTiming(0, 1, 0, 1, 0, owner.RunCount == 1, false),
                    new MemoryDiagnostics(0, 0, 0, 0, output.Length)));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
