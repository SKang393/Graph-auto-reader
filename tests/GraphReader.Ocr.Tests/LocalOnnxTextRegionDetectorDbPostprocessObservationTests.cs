// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.Inference;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class LocalOnnxTextRegionDetectorDbPostprocessObservationTests
{
    [TestMethod]
    public async Task IdealV39ShrinkTargetsDoNotGuaranteeRecoveryOfElongatedTextBoxes()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "ideal-shrink-target-fixture.onnx");
        await File.WriteAllBytesAsync(modelPath, [4, 3, 9, 1]);
        try
        {
            // A model-free round trip at the shipped DB operating point. These
            // are deliberately ideal V39 targets, not actual model predictions.
            const int canvasWidth = 1024;
            const int canvasHeight = 256;
            var cases = new[] { (Width: 96, Height: 12), (Width: 120, Height: 24),
                (Width: 24, Height: 24), (Width: 16, Height: 8) };
            var outputs = new Queue<float[]>();
            foreach (var item in cases)
            {
                double distance = item.Width * item.Height * (1 - (0.4 * 0.4)) /
                    (2 * (item.Width + item.Height));
                outputs.Enqueue(RectangleMap(canvasWidth, canvasHeight,
                    (int)Math.Ceiling(100 + distance), (int)Math.Ceiling(100 + distance),
                    (int)Math.Floor(100 + item.Width - distance),
                    (int)Math.Floor(100 + item.Height - distance), 1f));
            }
            await using InferenceRuntime runtime = CreateRuntime(directory, outputs);
            var observations = new List<OcrDbPostprocessObservation>();
            var detector = new LocalOnnxTextRegionDetector(runtime, Options(Identity(modelPath)) with
            {
                MaximumSideLength = 960, DimensionMultiple = 128,
                MinimumSideLength = 3, MaximumRegions = 1000,
                DbPostprocessObserver = observations.Add,
            });
            var overlaps = new List<double>();
            foreach (var item in cases)
            {
                IReadOnlyList<OcrDetectedRegion> regions = await detector.DetectAsync(
                    Image(canvasWidth, canvasHeight), CancellationToken.None);
                double overlap = 0;
                foreach (OcrDetectedRegion region in regions)
                {
                    OcrRectangle box = region.Polygon.Bounds;
                    double intersection = Math.Max(0, Math.Min(100 + item.Width, box.Right) - Math.Max(100, box.Left)) *
                        Math.Max(0, Math.Min(100 + item.Height, box.Bottom) - Math.Max(100, box.Top));
                    overlap = Math.Max(overlap, intersection /
                        ((item.Width * item.Height) + (box.Width * box.Height) - intersection));
                }
                overlaps.Add(overlap);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    width = item.Width, height = item.Height, returned_regions = regions.Count,
                    maximum_iou = overlap,
                    dispositions = observations[^1].Contours.Select(value => value.Disposition.ToString()).ToArray(),
                }));
                Assert.AreEqual(canvasWidth, observations[^1].TensorWidth);
                Assert.AreEqual(canvasHeight, observations[^1].TensorHeight);
            }
            Assert.IsLessThan(0.5, overlaps[0]);
            Assert.IsLessThan(0.5, overlaps[1]);
            Assert.IsGreaterThanOrEqualTo(0.5, overlaps[2]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ObserverRecordsThresholdSupportAndEveryReachedDispositionWithoutChangingOutput()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "db-postprocess-observer.onnx");
        await File.WriteAllBytesAsync(modelPath, [9, 4, 2, 7]);
        try
        {
            const int width = 64;
            const int height = 64;
            var output = new float[width * height];
            FillRectangle(output, width, 4, 4, 12, 12, 0.90f);
            FillRectangle(output, width, 20, 4, 28, 12, 0.55f);
            FillRectangle(output, width, 4, 20, 12, 22, 0.90f);
            FillRectangle(output, width, 20, 20, 28, 23, 0.90f);
            output[(4 * width) + 40] = 0.90f;
            await using InferenceRuntime runtime = CreateRuntime(
                directory,
                new Queue<float[]>([(float[])output.Clone(), (float[])output.Clone()]));
            LocalOnnxTextRegionDetectorOptions options = Options(Identity(modelPath)) with
            {
                MaximumSideLength = width,
                MinimumSideLength = 2,
                UnclipRatio = 0,
            };
            var observations = new List<OcrDbPostprocessObservation>();
            var baseline = new LocalOnnxTextRegionDetector(runtime, options);
            var observed = new LocalOnnxTextRegionDetector(
                runtime,
                options with { DbPostprocessObserver = observations.Add });
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
            AssertRegionsEqual(expected[0], actual[0]);

            OcrDbPostprocessObservation observation = AssertExactlyOne(observations);
            Assert.AreEqual(
                Convert.ToHexStringLower(SHA256.HashData(image.Pixels.Span)),
                observation.InputSha256);
            Assert.AreEqual(width, observation.ImageWidth);
            Assert.AreEqual(height, observation.ImageHeight);
            Assert.AreEqual(width, observation.TensorWidth);
            Assert.AreEqual(height, observation.TensorHeight);
            Assert.AreEqual(0.30f, observation.ProbabilityThreshold);
            Assert.AreEqual(0.60f, observation.BoxConfidenceThreshold);
            Assert.AreEqual(2, observation.MinimumSideLength);
            Assert.AreEqual(20, observation.MaximumRegions);
            Assert.AreEqual(169, observation.AboveThresholdPixelCount);
            Assert.AreEqual(5, observation.TotalContourCount);
            Assert.AreEqual(5, observation.EvaluatedContourCount);
            Assert.AreEqual(0, observation.TruncatedContourCount);
            Assert.HasCount(5, observation.Contours);

            OcrDbContourEvaluation accepted = AssertExactlyOne(
                observation.Contours.Where(static item =>
                    item.Disposition == OcrDbContourDisposition.Accepted).ToArray());
            Assert.AreEqual(actual[0].RegionId, accepted.ReturnedRegionId);
            Assert.AreEqual(actual[0].Polygon, accepted.ExpandedPolygon);
            Assert.AreEqual(actual[0].DetectionConfidence, accepted.BoxConfidence);
            Assert.IsNotNull(accepted.InitialPolygon);

            OcrDbContourEvaluation confidenceRejected = AssertExactlyOne(
                observation.Contours.Where(static item =>
                    item.Disposition == OcrDbContourDisposition.RejectedBoxConfidence).ToArray());
            Assert.AreEqual(0.55, confidenceRejected.BoxConfidence!.Value, 0.0001);
            Assert.IsNotNull(confidenceRejected.InitialPolygon);
            Assert.IsNull(confidenceRejected.ExpandedPolygon);

            OcrDbContourEvaluation initialSideRejected = AssertExactlyOne(
                observation.Contours.Where(static item =>
                    item.Disposition == OcrDbContourDisposition.RejectedInitialSideLength).ToArray());
            Assert.AreEqual(1, initialSideRejected.InitialShortSide!.Value, 0.0001);
            Assert.IsNull(initialSideRejected.BoxConfidence);

            OcrDbContourEvaluation expandedSideRejected = AssertExactlyOne(
                observation.Contours.Where(static item =>
                    item.Disposition == OcrDbContourDisposition.RejectedExpandedSideLength).ToArray());
            Assert.AreEqual(2, expandedSideRejected.InitialShortSide!.Value, 0.0001);
            Assert.AreEqual(2, expandedSideRejected.ExpandedShortSide!.Value, 0.0001);
            Assert.IsNotNull(expandedSideRejected.BoxConfidence);

            OcrDbContourEvaluation insufficientPoints = AssertExactlyOne(
                observation.Contours.Where(static item =>
                    item.Disposition == OcrDbContourDisposition.RejectedInsufficientPoints).ToArray());
            Assert.AreEqual(1, insufficientPoints.PointCount);
            Assert.IsNull(insufficientPoints.InitialPolygon);

            byte[] firstMask = observation.CopyThresholdMask();
            Assert.HasCount(width * height, firstMask);
            Assert.AreEqual(byte.MaxValue, firstMask[(4 * width) + 40]);
            firstMask[(4 * width) + 40] = 0;
            Assert.AreEqual(byte.MaxValue, observation.CopyThresholdMask()[(4 * width) + 40]);
            Assert.ThrowsExactly<NotSupportedException>(() =>
                ((IList<OcrDbContourEvaluation>)observation.Contours).Add(accepted));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ObserverDistinguishesAbsentThresholdSupportFromRejectedSupport()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "db-support-observer.onnx");
        await File.WriteAllBytesAsync(modelPath, [3, 8, 6, 1]);
        try
        {
            const int width = 32;
            const int height = 32;
            var outputs = new Queue<float[]>([
                new float[width * height],
                RectangleMap(width, height, 6, 6, 24, 18, 0.55f),
            ]);
            await using InferenceRuntime runtime = CreateRuntime(directory, outputs);
            var observations = new List<OcrDbPostprocessObservation>();
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                Options(Identity(modelPath)) with { DbPostprocessObserver = observations.Add });

            Assert.IsEmpty(await detector.DetectAsync(Image(width, height), CancellationToken.None));
            Assert.IsEmpty(await detector.DetectAsync(Image(width, height), CancellationToken.None));

            Assert.HasCount(2, observations);
            Assert.AreEqual(0, observations[0].AboveThresholdPixelCount);
            Assert.AreEqual(0, observations[0].TotalContourCount);
            Assert.IsEmpty(observations[0].Contours);
            Assert.AreEqual(216, observations[1].AboveThresholdPixelCount);
            Assert.AreEqual(1, observations[1].TotalContourCount);
            Assert.AreEqual(
                OcrDbContourDisposition.RejectedBoxConfidence,
                AssertExactlyOne(observations[1].Contours.ToArray()).Disposition);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ObserverAccountsForContoursNotEvaluatedByMaximumRegionLimit()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "db-region-limit-observer.onnx");
        await File.WriteAllBytesAsync(modelPath, [6, 2, 8, 4]);
        try
        {
            var output = new float[32 * 32];
            FillRectangle(output, 32, 2, 3, 8, 9, 0.9f);
            FillRectangle(output, 32, 11, 3, 17, 9, 0.9f);
            FillRectangle(output, 32, 20, 3, 26, 9, 0.9f);
            await using InferenceRuntime runtime = CreateRuntime(directory, output);
            var observations = new List<OcrDbPostprocessObservation>();
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                Options(Identity(modelPath)) with
                {
                    MaximumRegions = 1,
                    UnclipRatio = 0,
                    DbPostprocessObserver = observations.Add,
                });

            IReadOnlyList<OcrDetectedRegion> regions = await detector.DetectAsync(
                Image(32, 32),
                CancellationToken.None);

            Assert.HasCount(1, regions);
            OcrDbPostprocessObservation observation = AssertExactlyOne(observations);
            Assert.AreEqual(3, observation.TotalContourCount);
            Assert.AreEqual(1, observation.EvaluatedContourCount);
            Assert.AreEqual(2, observation.TruncatedContourCount);
            Assert.HasCount(2, observation.Contours.Where(static item =>
                item.Disposition == OcrDbContourDisposition.NotEvaluatedMaximumRegions).ToArray());
            Assert.HasCount(1, observation.Contours.Where(static item =>
                item.Disposition == OcrDbContourDisposition.Accepted).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task CancellationAfterInferenceEmitsNoPartialObservation()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "db-cancel-observer.onnx");
        await File.WriteAllBytesAsync(modelPath, [5, 5, 1, 3]);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var observations = new List<OcrDbPostprocessObservation>();
            await using InferenceRuntime runtime = CreateRuntime(
                directory,
                RectangleMap(32, 32, 6, 6, 24, 18, 0.9f),
                cancellation.Cancel);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                Options(Identity(modelPath)) with { DbPostprocessObserver = observations.Add });

            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
                detector.DetectAsync(Image(32, 32), cancellation.Token).AsTask());

            Assert.IsEmpty(observations);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ObserverRequiresDbPostprocessAndCurrentUncachedInference()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "db-invalid-observer.onnx");
        await File.WriteAllBytesAsync(modelPath, [8, 1, 4, 9]);
        try
        {
            await using InferenceRuntime runtime = CreateRuntime(directory, new float[32 * 32]);
            Action<OcrDbPostprocessObservation> observer = static _ => { };
            LocalOnnxTextRegionDetectorOptions options = Options(Identity(modelPath));

            Assert.ThrowsExactly<ArgumentException>(() => new LocalOnnxTextRegionDetector(
                runtime,
                options with { BypassCache = false, DbPostprocessObserver = observer }));
            Assert.ThrowsExactly<ArgumentException>(() => new LocalOnnxTextRegionDetector(
                runtime,
                options with
                {
                    PostprocessAlgorithm = OcrDetectionPostprocessAlgorithm.DenseProbabilityComponentsV1,
                    DbPostprocessObserver = observer,
                }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertRegionsEqual(OcrDetectedRegion expected, OcrDetectedRegion actual)
    {
        Assert.AreEqual(expected.RegionId, actual.RegionId);
        CollectionAssert.AreEqual(expected.Polygon.Points.ToArray(), actual.Polygon.Points.ToArray());
        Assert.AreEqual(expected.OrientationDegrees, actual.OrientationDegrees);
        Assert.AreEqual(expected.DetectionConfidence, actual.DetectionConfidence);
        Assert.AreEqual(expected.CoordinateSpace, actual.CoordinateSpace);
        OcrRegionEvidence expectedEvidence = expected.Evidence ??
            throw new AssertFailedException("Expected DB evidence is missing.");
        OcrRegionEvidence actualEvidence = actual.Evidence ??
            throw new AssertFailedException("Actual DB evidence is missing.");
        Assert.AreEqual(expectedEvidence.ComponentCount, actualEvidence.ComponentCount);
        Assert.AreEqual(expectedEvidence.InkDensity, actualEvidence.InkDensity);
        Assert.AreEqual(expectedEvidence.TextLikelihood, actualEvidence.TextLikelihood);
        Assert.AreEqual(expectedEvidence.StructureLikelihood, actualEvidence.StructureLikelihood);
        Assert.AreEqual(expectedEvidence.LikelyGraphStructure, actualEvidence.LikelyGraphStructure);
        CollectionAssert.AreEqual(expectedEvidence.Reasons.ToArray(), actualEvidence.Reasons.ToArray());
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

    private static float[] RectangleMap(
        int width,
        int height,
        int left,
        int top,
        int right,
        int bottom,
        float value)
    {
        var output = new float[width * height];
        FillRectangle(output, width, left, top, right, bottom, value);
        return output;
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

    private static T AssertExactlyOne<T>(List<T> values)
    {
        Assert.HasCount(1, values);
        return values[0];
    }

    private static T AssertExactlyOne<T>(T[] values)
    {
        Assert.HasCount(1, values);
        return values[0];
    }

    private static ModelIdentity Identity(string path) => new(
        "fixture-db-postprocess-observer",
        "1.0.0",
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
        path);

    private static string CreateDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "GraphReaderOcrDbPostprocessObservationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static InferenceRuntime CreateRuntime(
        string directory,
        float[] output,
        Action? afterRun = null) =>
        CreateRuntime(directory, new Queue<float[]>([output]), afterRun);

    private static InferenceRuntime CreateRuntime(
        string directory,
        Queue<float[]> outputs,
        Action? afterRun = null)
    {
        var registry = new OnnxSessionRegistry(
            new FakeExecutionProviderDiscovery("CPUExecutionProvider"),
            new WindowsExecutionProviderPolicy(),
            new ProbabilityMapSessionFactory(outputs, afterRun),
            CpuThreadConfiguration.Create(1));
        return new InferenceRuntime(
            registry,
            new BoundedInferenceScheduler(capacity: 2, workerCount: 1),
            new ContentAddressedStageCache(Path.Combine(directory, "cache")));
    }

    private sealed class ProbabilityMapSessionFactory(
        Queue<float[]> outputs,
        Action? afterRun) : IInferenceSessionFactory
    {
        private readonly Queue<float[]> outputs = new(outputs.Select(static item => (float[])item.Clone()));

        public ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IInferenceSession>(new Session(outputs, afterRun, provider));
        }

        private sealed class Session(
            Queue<float[]> outputs,
            Action? afterRun,
            InferenceProvider provider) : IInferenceSession
        {
            public InferenceProvider Provider { get; } = provider;

            public ValueTask<InferenceExecution> RunAsync(
                InferenceInput input,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float[] output = outputs.Dequeue();
                afterRun?.Invoke();
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
