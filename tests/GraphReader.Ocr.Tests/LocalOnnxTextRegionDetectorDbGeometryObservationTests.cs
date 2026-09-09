// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.Inference;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class LocalOnnxTextRegionDetectorDbGeometryObservationTests
{
    [TestMethod]
    public async Task ObserverRecordsOnlyAcceptedContoursWithoutChangingReturnedRegions()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "db-geometry-observer.onnx");
        await File.WriteAllBytesAsync(modelPath, [4, 8, 1, 6]);
        try
        {
            const int width = 32;
            const int height = 32;
            var output = new float[width * height];
            FillRectangle(output, width, 8, 8, 14, 14, 0.9f);
            FillRectangle(output, width, 20, 20, 26, 26, 0.55f);
            await using InferenceRuntime runtime = CreateRuntime(directory, output);
            LocalOnnxTextRegionDetectorOptions baselineOptions = Options(Identity(modelPath));
            var observations = new List<OcrDbGeometryObservation>();
            var baseline = new LocalOnnxTextRegionDetector(runtime, baselineOptions);
            var observed = new LocalOnnxTextRegionDetector(
                runtime,
                baselineOptions with { DbGeometryObserver = observations.Add });
            OcrImage image = Image(width, height);

            IReadOnlyList<OcrDetectedRegion> expected = await baseline.DetectAsync(
                image,
                CancellationToken.None);
            IReadOnlyList<OcrDetectedRegion> actual = await observed.DetectAsync(
                image,
                CancellationToken.None);

            Assert.AreEqual(baseline.ConfigurationFingerprint, observed.ConfigurationFingerprint);
            Assert.HasCount(1, expected);
            Assert.HasCount(1, actual);
            Assert.AreEqual(expected[0].RegionId, actual[0].RegionId);
            CollectionAssert.AreEqual(
                expected[0].Polygon.Points.ToArray(),
                actual[0].Polygon.Points.ToArray());
            Assert.AreEqual(expected[0].OrientationDegrees, actual[0].OrientationDegrees);
            Assert.AreEqual(expected[0].DetectionConfidence, actual[0].DetectionConfidence);
            OcrRegionEvidence expectedEvidence = expected[0].Evidence ??
                throw new AssertFailedException("Baseline DB region evidence is missing.");
            OcrRegionEvidence actualEvidence = actual[0].Evidence ??
                throw new AssertFailedException("Observed DB region evidence is missing.");
            Assert.AreEqual(expectedEvidence.InkDensity, actualEvidence.InkDensity);
            Assert.AreEqual(expectedEvidence.TextLikelihood, actualEvidence.TextLikelihood);
            Assert.AreEqual(expectedEvidence.StructureLikelihood, actualEvidence.StructureLikelihood);
            Assert.AreEqual(expectedEvidence.LikelyGraphStructure, actualEvidence.LikelyGraphStructure);
            CollectionAssert.AreEqual(
                expectedEvidence.Reasons.ToArray(),
                actualEvidence.Reasons.ToArray());
            OcrDbGeometryObservation batch = AssertExactlyOne(observations);
            Assert.AreEqual(
                Convert.ToHexStringLower(SHA256.HashData(image.Pixels.Span)),
                batch.InputSha256);
            Assert.AreEqual(width, batch.ImageWidth);
            Assert.AreEqual(height, batch.ImageHeight);
            Assert.AreEqual(width, batch.TensorWidth);
            Assert.AreEqual(height, batch.TensorHeight);
            OcrDbAcceptedContourGeometry contour = AssertExactlyOne(batch.AcceptedContours);
            Assert.AreEqual(actual[0].RegionId, contour.ReturnedRegionId);
            Assert.AreEqual(actual[0].Polygon, contour.ExpandedPolygon);
            Assert.AreEqual(actual[0].DetectionConfidence, contour.DetectionConfidence);
            Assert.AreEqual(actual[0].Evidence!.InkDensity, contour.InkDensity);
            Assert.AreEqual(new OcrRectangle(8, 8, 5, 5), contour.InitialPolygon.Bounds);
            Assert.AreEqual(new OcrRectangle(6, 6, 9, 9), contour.ExpandedPolygon.Bounds);
            Assert.IsTrue(((IList<OcrDbAcceptedContourGeometry>)batch.AcceptedContours).IsReadOnly);
            Assert.ThrowsExactly<NotSupportedException>(() =>
                ((IList<OcrDbAcceptedContourGeometry>)batch.AcceptedContours).Add(contour));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ObserverEmitsHonestEmptyBatchWhenNoContourIsAccepted()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "empty-db-geometry-observer.onnx");
        await File.WriteAllBytesAsync(modelPath, [2, 5, 7, 9]);
        try
        {
            var observations = new List<OcrDbGeometryObservation>();
            await using InferenceRuntime runtime = CreateRuntime(directory, new float[32 * 32]);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                Options(Identity(modelPath)) with { DbGeometryObserver = observations.Add });

            IReadOnlyList<OcrDetectedRegion> regions = await detector.DetectAsync(
                Image(32, 32),
                CancellationToken.None);

            Assert.IsEmpty(regions);
            Assert.IsEmpty(AssertExactlyOne(observations).AcceptedContours);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ObserverFailsClosedUnlessDbInferenceBypassesCache()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "invalid-db-geometry-observer.onnx");
        await File.WriteAllBytesAsync(modelPath, [1, 3, 3, 7]);
        try
        {
            await using InferenceRuntime runtime = CreateRuntime(directory, new float[32 * 32]);
            Action<OcrDbGeometryObservation> observer = static _ => { };
            LocalOnnxTextRegionDetectorOptions dbOptions = Options(Identity(modelPath));

            Assert.ThrowsExactly<ArgumentException>(() => new LocalOnnxTextRegionDetector(
                runtime,
                dbOptions with { BypassCache = false, DbGeometryObserver = observer }));
            Assert.ThrowsExactly<ArgumentException>(() => new LocalOnnxTextRegionDetector(
                runtime,
                dbOptions with
                {
                    PostprocessAlgorithm = OcrDetectionPostprocessAlgorithm.DenseProbabilityComponentsV1,
                    DbGeometryObserver = observer,
                }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static LocalOnnxTextRegionDetectorOptions Options(ModelIdentity model) => new(model)
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
        BypassCache = true,
    };

    private static OcrImage Image(int width, int height) => new(
        width,
        height,
        width,
        new byte[width * height],
        OcrSourceImage.Original,
        OcrFrameTransform.Identity,
        CanonicalOriginalWidth: width,
        CanonicalOriginalHeight: height);

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

    private static T AssertExactlyOne<T>(IReadOnlyList<T> values)
    {
        Assert.HasCount(1, values);
        return values[0];
    }

    private static ModelIdentity Identity(string path) => new(
        "fixture-db-geometry-observer",
        "1.0.0",
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
        path);

    private static string CreateDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "GraphReaderOcrDbGeometryObservationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static InferenceRuntime CreateRuntime(string directory, float[] output)
    {
        var registry = new OnnxSessionRegistry(
            new FakeExecutionProviderDiscovery("CPUExecutionProvider"),
            new WindowsExecutionProviderPolicy(),
            new ProbabilityMapSessionFactory(output),
            CpuThreadConfiguration.Create(1));
        return new InferenceRuntime(
            registry,
            new BoundedInferenceScheduler(capacity: 2, workerCount: 1),
            new ContentAddressedStageCache(Path.Combine(directory, "cache")));
    }

    private sealed class ProbabilityMapSessionFactory(float[] output) : IInferenceSessionFactory
    {
        private readonly float[] output = (float[])output.Clone();

        public ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IInferenceSession>(new Session(output, provider));
        }

        private sealed class Session(float[] output, InferenceProvider provider) : IInferenceSession
        {
            private readonly float[] output = (float[])output.Clone();

            public InferenceProvider Provider { get; } = provider;

            public ValueTask<InferenceExecution> RunAsync(
                InferenceInput input,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new InferenceExecution(
                    Array.AsReadOnly((float[])output.Clone()),
                    Provider,
                    new StageTiming(0, 1, 0, 1, 0, true, false),
                    new MemoryDiagnostics(0, 0, 0, 0, output.Length)));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
