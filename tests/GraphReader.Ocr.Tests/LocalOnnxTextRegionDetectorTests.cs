// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.Inference;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class LocalOnnxTextRegionDetectorTests
{
    private static readonly float[] ExpectedBgrTensor = [10f / 255f, 20f / 255f, 30f / 255f];
    private static readonly byte[] BgrDetectorGrayscalePixel = [99];
    private static readonly byte[] MissingBgrGrayscalePixel = [0];

    [TestMethod]
    public async Task DetectorRunsOnBoundCpuPolicyAndMapsProbabilityRegionToOriginalPixels()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "detector.onnx");
        await File.WriteAllBytesAsync(modelPath, [2, 7, 1, 8]);
        try
        {
            var output = new float[8 * 4];
            foreach (int y in new[] { 1, 2 })
            {
                foreach (int x in new[] { 2, 3, 4 })
                {
                    output[(y * 8) + x] = 0.9f;
                }
            }

            var factory = new ProbabilityMapSessionFactory(output);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                Options(Identity(modelPath)) with
                {
                    ChannelMeans = [0f, 0.5f, 1f],
                    ChannelScales = [1f, 2f, 3f],
                    AllowedProviders = [InferenceProvider.Cpu],
                });
            var image = new OcrImage(
                8,
                4,
                8,
                Enumerable.Repeat((byte)255, 32).ToArray(),
                OcrSourceImage.Enhanced,
                new OcrFrameTransform(2, 2, 0, 0),
                CanonicalOriginalWidth: 4,
                CanonicalOriginalHeight: 2);

            IReadOnlyList<OcrDetectedRegion> first = await detector.DetectAsync(
                image,
                CancellationToken.None);
            IReadOnlyList<OcrDetectedRegion> cached = await detector.DetectAsync(
                image,
                CancellationToken.None);

            Assert.HasCount(1, first);
            Assert.AreEqual(new OcrRectangle(1, 0.5, 1.5, 1), first[0].Polygon.Bounds);
            Assert.AreEqual(OcrContract.CoordinateSpace, first[0].CoordinateSpace);
            Assert.AreEqual(0.9, first[0].DetectionConfidence, 0.0001);
            Assert.AreEqual(first[0].RegionId, cached[0].RegionId);
            Assert.IsTrue(Guid.TryParse(first[0].RegionId, out _));
            CollectionAssert.AreEqual(
                new[] { InferenceProvider.Cpu },
                factory.CreatedProviders.ToArray());
            Assert.IsNotNull(factory.LastInput);
            CollectionAssert.AreEqual(new long[] { 1, 3, 4, 8 }, factory.LastInput.Shape.ToArray());
            float[] input = factory.LastInput.Values.ToArray();
            Assert.IsTrue(input.Take(32).All(static value => value == 1f));
            Assert.IsTrue(input.Skip(32).Take(32).All(static value => value == 1f));
            Assert.IsTrue(input.Skip(64).Take(32).All(static value => value == 0f));
            Assert.AreEqual(1, factory.RunCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DetectorRejectsOutputShapeAndProbabilityContractViolations()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "invalid-output.onnx");
        await File.WriteAllBytesAsync(modelPath, [4, 2, 9]);
        try
        {
            var image = Image();
            await using (InferenceRuntime wrongShapeRuntime = CreateRuntime(
                Path.Combine(directory, "wrong-shape"),
                new ProbabilityMapSessionFactory(new float[31])))
            {
                var detector = new LocalOnnxTextRegionDetector(
                    wrongShapeRuntime,
                    Options(Identity(modelPath)));
                InvalidDataException wrongShape = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                    () => detector.DetectAsync(image, CancellationToken.None).AsTask());
                StringAssert.Contains(wrongShape.Message, "31 values; 32 were required");
            }

            float[] invalidProbability = new float[32];
            invalidProbability[10] = 1.1f;
            await using (InferenceRuntime probabilityRuntime = CreateRuntime(
                Path.Combine(directory, "invalid-probability"),
                new ProbabilityMapSessionFactory(invalidProbability)))
            {
                var detector = new LocalOnnxTextRegionDetector(
                    probabilityRuntime,
                    Options(Identity(modelPath)));
                InvalidDataException invalid = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                    () => detector.DetectAsync(image, CancellationToken.None).AsTask());
                StringAssert.Contains(invalid.Message, "within [0,1]");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProbabilityParityToleranceClampsOnlyBoundedFiniteDrift()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "probability-parity-tolerance.onnx");
        await File.WriteAllBytesAsync(modelPath, [9, 1, 7]);
        try
        {
            float[] boundedDrift = new float[32];
            boundedDrift[10] = 1.000005f;
            boundedDrift[11] = -0.000005f;
            await using (InferenceRuntime boundedRuntime = CreateRuntime(
                Path.Combine(directory, "bounded"),
                new ProbabilityMapSessionFactory(boundedDrift)))
            {
                var detector = new LocalOnnxTextRegionDetector(
                    boundedRuntime,
                    Options(Identity(modelPath)) with
                    {
                        OutputActivation = OcrDetectionOutputActivation.ProbabilityWithParityTolerance,
                    });
                var strictDetector = new LocalOnnxTextRegionDetector(
                    boundedRuntime,
                    Options(Identity(modelPath)));

                Assert.AreNotEqual(
                    strictDetector.ConfigurationFingerprint,
                    detector.ConfigurationFingerprint);
                _ = await detector.DetectAsync(Image(), CancellationToken.None);
            }

            float[] materialDrift = new float[32];
            materialDrift[10] = 1.00002f;
            await using (InferenceRuntime materialRuntime = CreateRuntime(
                Path.Combine(directory, "material"),
                new ProbabilityMapSessionFactory(materialDrift)))
            {
                var detector = new LocalOnnxTextRegionDetector(
                    materialRuntime,
                    Options(Identity(modelPath)) with
                    {
                        OutputActivation = OcrDetectionOutputActivation.ProbabilityWithParityTolerance,
                    });
                InvalidDataException invalid = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                    () => detector.DetectAsync(Image(), CancellationToken.None).AsTask());
                StringAssert.Contains(invalid.Message, "fixed 1e-5 parity tolerance");
            }

            float[] nonFinite = new float[32];
            nonFinite[10] = float.NaN;
            await using (InferenceRuntime nonFiniteRuntime = CreateRuntime(
                Path.Combine(directory, "non-finite"),
                new ProbabilityMapSessionFactory(nonFinite)))
            {
                var detector = new LocalOnnxTextRegionDetector(
                    nonFiniteRuntime,
                    Options(Identity(modelPath)) with
                    {
                        OutputActivation = OcrDetectionOutputActivation.ProbabilityWithParityTolerance,
                    });
                InvalidDataException invalid = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                    () => detector.DetectAsync(Image(), CancellationToken.None).AsTask());
                StringAssert.Contains(invalid.Message, "non-finite");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DetectorPreservesDeclaredBgrChannelOrder()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "bgr-detector.onnx");
        await File.WriteAllBytesAsync(modelPath, [9, 3, 7]);
        try
        {
            var factory = new ProbabilityMapSessionFactory([0f]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                Options(Identity(modelPath)) with
                {
                    InputColorMode = OcrTensorColorMode.Bgr,
                    ChannelMeans = [0f, 0f, 0f],
                    ChannelScales = [1f, 1f, 1f],
                });
            var image = new OcrImage(
                1,
                1,
                1,
                BgrDetectorGrayscalePixel,
                OcrSourceImage.Original,
                OcrFrameTransform.Identity,
                CanonicalOriginalWidth: 1,
                CanonicalOriginalHeight: 1,
                BgrPixels: new OcrBgrBytePixels(3, new byte[] { 10, 20, 30 }));

            _ = await detector.DetectAsync(image, CancellationToken.None);

            Assert.IsNotNull(factory.LastInput);
            CollectionAssert.AreEqual(new long[] { 1, 3, 1, 1 }, factory.LastInput.Shape.ToArray());
            CollectionAssert.AreEqual(ExpectedBgrTensor, factory.LastInput.Values.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task BgrDetectorRejectsMissingColorPlaneBeforeProviderExecution()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "missing-bgr.onnx");
        await File.WriteAllBytesAsync(modelPath, [7, 1, 3]);
        try
        {
            var factory = new ProbabilityMapSessionFactory([0f]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                Options(Identity(modelPath)) with { InputColorMode = OcrTensorColorMode.Bgr });

            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                detector.DetectAsync(
                    new OcrImage(
                        1,
                        1,
                        1,
                        MissingBgrGrayscalePixel,
                        OcrSourceImage.Original,
                        OcrFrameTransform.Identity,
                        CanonicalOriginalWidth: 1,
                        CanonicalOriginalHeight: 1),
                    CancellationToken.None).AsTask());

            Assert.AreEqual(0, factory.RunCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(OcrDetectionPostprocessAlgorithm.DenseProbabilityComponentsV1)]
    [DataRow(OcrDetectionPostprocessAlgorithm.DbPostprocessV1)]
    public async Task DetectorHonorsCancellationBeforeProviderExecution(
        OcrDetectionPostprocessAlgorithm postprocessAlgorithm)
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "cancelled.onnx");
        await File.WriteAllBytesAsync(modelPath, [8, 6, 7, 5]);
        try
        {
            var factory = new ProbabilityMapSessionFactory(new float[32]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                Options(Identity(modelPath)) with { PostprocessAlgorithm = postprocessAlgorithm });

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                detector.DetectAsync(Image(), new CancellationToken(canceled: true)).AsTask());

            Assert.AreEqual(0, factory.RunCount);
            Assert.IsEmpty(factory.CreatedProviders);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DetectorNeverRoundsPastNonDivisibleMaximumSide()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "bounded.onnx");
        await File.WriteAllBytesAsync(modelPath, [3, 1, 4, 1]);
        try
        {
            const int alignedWidth = 96;
            const int alignedHeight = 64;
            var factory = new ProbabilityMapSessionFactory(new float[alignedWidth * alignedHeight]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                Options(Identity(modelPath)) with
                {
                    MaximumSideLength = 100,
                    DimensionMultiple = 32,
                });
            var image = new OcrImage(
                100,
                80,
                100,
                new byte[8_000],
                OcrSourceImage.Original,
                OcrFrameTransform.Identity,
                CanonicalOriginalWidth: 100,
                CanonicalOriginalHeight: 80);

            _ = await detector.DetectAsync(image, CancellationToken.None);

            Assert.IsNotNull(factory.LastInput);
            CollectionAssert.AreEqual(
                new long[] { 1, 3, alignedHeight, alignedWidth },
                factory.LastInput.Shape.ToArray());
            Assert.IsTrue(factory.LastInput.Shape.Skip(2).All(dimension => dimension <= 100));
            Assert.IsTrue(factory.LastInput.Shape.Skip(2).All(dimension => dimension % 32 == 0));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbResizeLongTruncatesBeforePinnedStrideAlignment()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "db-resize-long.onnx");
        await File.WriteAllBytesAsync(modelPath, [9, 6, 0]);
        try
        {
            const int expectedWidth = 1024;
            const int expectedHeight = 128;
            var factory = new ProbabilityMapSessionFactory(new float[expectedWidth * expectedHeight]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with
                {
                    MaximumSideLength = 960,
                    DimensionMultiple = 128,
                });

            _ = await detector.DetectAsync(Image(1000, 134), CancellationToken.None);

            Assert.IsNotNull(factory.LastInput);
            CollectionAssert.AreEqual(
                new long[] { 1, 3, expectedHeight, expectedWidth },
                factory.LastInput.Shape.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbResizeUpscaleMatchesOfficialCv2TensorAtBorderAndInterior()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "db-cv2-upscale.onnx");
        await File.WriteAllBytesAsync(modelPath, [2, 6, 0, 9]);
        try
        {
            const int sourceWidth = 32;
            const int sourceHeight = 32;
            const int targetWidth = 64;
            const int targetHeight = 64;
            byte[] bgr = PatternedBgr(sourceWidth, sourceHeight);
            byte[] immutableOriginal = (byte[])bgr.Clone();
            var factory = new ProbabilityMapSessionFactory(new float[targetWidth * targetHeight]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with
                {
                    MaximumSideLength = 64,
                    DimensionMultiple = 32,
                    InputColorMode = OcrTensorColorMode.Bgr,
                    ChannelMeans = [0.485f, 0.456f, 0.406f],
                    ChannelScales = [4.366812227074235f, 4.464285714285714f, 4.444444444444445f],
                });
            var image = new OcrImage(
                sourceWidth,
                sourceHeight,
                sourceWidth,
                new byte[sourceWidth * sourceHeight],
                OcrSourceImage.Original,
                OcrFrameTransform.Identity,
                CanonicalOriginalWidth: sourceWidth,
                CanonicalOriginalHeight: sourceHeight,
                BgrPixels: new OcrBgrBytePixels(sourceWidth * 3, bgr));

            _ = await detector.DetectAsync(image, CancellationToken.None);

            Assert.IsNotNull(factory.LastInput);
            CollectionAssert.AreEqual(
                new long[] { 1, 3, targetHeight, targetWidth },
                factory.LastInput.Shape.ToArray());
            float[] tensor = factory.LastInput.Values.ToArray();
            AssertTensorPixel(tensor, targetWidth, targetHeight, 0, 0,
                -2.0665298f, -1.7030813f, -0.044095784f);
            AssertTensorPixel(tensor, targetWidth, targetHeight, 32, 32,
                1.5639181f, -1.2303921f, 2.1519828f);
            Assert.AreEqual(
                "d63623eff5d476ec25b35726f786608563fa847d7cb66d6bc9760a6999d62d39",
                TensorSha256(tensor),
                "Expected hash was generated independently with official cv2.resize INTER_LINEAR, uint8 output, BGR normalization, and NCHW transpose.");
            CollectionAssert.AreEqual(immutableOriginal, bgr);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbResizeDownscaleMatchesOfficialCv2TensorAtBorderAndInterior()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "db-cv2-downscale.onnx");
        await File.WriteAllBytesAsync(modelPath, [7, 1, 0, 5]);
        try
        {
            const int sourceWidth = 64;
            const int sourceHeight = 32;
            const int targetWidth = 32;
            const int targetHeight = 16;
            byte[] bgr = PatternedBgr(sourceWidth, sourceHeight);
            byte[] immutableOriginal = (byte[])bgr.Clone();
            var factory = new ProbabilityMapSessionFactory(new float[targetWidth * targetHeight]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with
                {
                    MaximumSideLength = 32,
                    DimensionMultiple = 16,
                    InputColorMode = OcrTensorColorMode.Bgr,
                    ChannelMeans = [0.485f, 0.456f, 0.406f],
                    ChannelScales = [4.366812227074235f, 4.464285714285714f, 4.444444444444445f],
                });
            var image = new OcrImage(
                sourceWidth,
                sourceHeight,
                sourceWidth,
                new byte[sourceWidth * sourceHeight],
                OcrSourceImage.Original,
                OcrFrameTransform.Identity,
                CanonicalOriginalWidth: sourceWidth,
                CanonicalOriginalHeight: sourceHeight,
                BgrPixels: new OcrBgrBytePixels(sourceWidth * 3, bgr));

            _ = await detector.DetectAsync(image, CancellationToken.None);

            Assert.IsNotNull(factory.LastInput);
            CollectionAssert.AreEqual(
                new long[] { 1, 3, targetHeight, targetWidth },
                factory.LastInput.Shape.ToArray());
            float[] tensor = factory.LastInput.Values.ToArray();
            AssertTensorPixel(tensor, targetWidth, targetHeight, 0, 0,
                -1.6726605f, -1.5455183f, 0.025620991f);
            AssertTensorPixel(tensor, targetWidth, targetHeight, 16, 8,
                -0.8506721f, 0.97549033f, -1.3687147f);
            Assert.AreEqual(
                "239be410a6cea14415ff6710b500f24ed181aaf36ef8b6c7492b3b1c26a86c53",
                TensorSha256(tensor),
                "Expected hash was generated independently with official cv2.resize INTER_LINEAR, uint8 output, BGR normalization, and NCHW transpose.");
            CollectionAssert.AreEqual(immutableOriginal, bgr);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbResizePadsSmallSourceWithZeroPixelsBeforeResize()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "db-small-padding.onnx");
        await File.WriteAllBytesAsync(modelPath, [6, 4, 0]);
        try
        {
            const int sourceWidth = 20;
            const int sourceHeight = 10;
            const int targetWidth = 32;
            const int targetHeight = 32;
            var factory = new ProbabilityMapSessionFactory(new float[targetWidth * targetHeight]);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with
                {
                    MaximumSideLength = 32,
                    DimensionMultiple = 32,
                    InputColorMode = OcrTensorColorMode.Bgr,
                    ChannelMeans = [0, 0, 0],
                    ChannelScales = [1, 1, 1],
                });
            var image = new OcrImage(
                sourceWidth,
                sourceHeight,
                sourceWidth,
                Enumerable.Repeat(byte.MaxValue, sourceWidth * sourceHeight).ToArray(),
                OcrSourceImage.Original,
                OcrFrameTransform.Identity,
                CanonicalOriginalWidth: sourceWidth,
                CanonicalOriginalHeight: sourceHeight,
                BgrPixels: new OcrBgrBytePixels(
                    sourceWidth * 3,
                    Enumerable.Repeat(byte.MaxValue, sourceWidth * sourceHeight * 3).ToArray()));

            _ = await detector.DetectAsync(image, CancellationToken.None);

            Assert.IsNotNull(factory.LastInput);
            CollectionAssert.AreEqual(
                new long[] { 1, 3, targetHeight, targetWidth },
                factory.LastInput.Shape.ToArray());
            float[] tensor = factory.LastInput.Values.ToArray();
            Assert.AreEqual(1f, tensor[(9 * targetWidth) + 19]);
            Assert.AreEqual(0f, tensor[(9 * targetWidth) + 20]);
            Assert.AreEqual(0f, tensor[(10 * targetWidth)]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DetectorRejectsProviderPoliciesWithoutMandatoryCpuFallback()
    {
        string directory = CreateDirectory();
        try
        {
            var model = new ModelIdentity(
                "detector",
                "1",
                new string('a', 64),
                Path.Combine(directory, "unused.onnx"));
            await using InferenceRuntime runtime = CreateRuntime(
                directory,
                new ProbabilityMapSessionFactory(new float[32]));

            Assert.Throws<ArgumentException>(() => new LocalOnnxTextRegionDetector(
                runtime,
                Options(model) with { AllowedProviders = [InferenceProvider.DirectMl] }));
            Assert.Throws<ArgumentException>(() => new LocalOnnxTextRegionDetector(
                runtime,
                Options(model) with { AllowedProviders = [InferenceProvider.Cpu, InferenceProvider.Fake] }));
            Assert.Throws<ArgumentException>(() => new LocalOnnxTextRegionDetector(
                runtime,
                Options(model) with { AllowedProviders = [InferenceProvider.Cpu, (InferenceProvider)99] }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbPostprocessPreservesRotatedPolygonAndOrientation()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "rotated-db.onnx");
        await File.WriteAllBytesAsync(modelPath, [4, 8, 1, 5]);
        try
        {
            const int width = 32;
            const int height = 32;
            float[] output = RotatedRectangleMap(
                width,
                height,
                centerX: 16,
                centerY: 16,
                halfWidth: 9,
                halfHeight: 3,
                angleDegrees: 30,
                probability: 0.9f);
            var factory = new ProbabilityMapSessionFactory(output);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with
                {
                    UnclipRatio = 0,
                    BoxConfidenceThreshold = 0.5f,
                });
            var image = new OcrImage(
                width,
                height,
                width,
                new byte[width * height],
                OcrSourceImage.Original,
                OcrFrameTransform.Identity,
                CanonicalOriginalWidth: width,
                CanonicalOriginalHeight: height);

            IReadOnlyList<OcrDetectedRegion> regions = await detector.DetectAsync(
                image,
                CancellationToken.None);

            Assert.HasCount(1, regions);
            Assert.HasCount(4, regions[0].Polygon.Points);
            Assert.IsTrue(Math.Abs(regions[0].OrientationDegrees) is > 15 and < 45);
            Assert.IsTrue(regions[0].Polygon.Points.Select(static point => point.X).Distinct().Count() > 2);
            Assert.IsTrue(regions[0].Polygon.Points.Select(static point => point.Y).Distinct().Count() > 2);
            Assert.IsTrue(regions[0].DetectionConfidence > 0.5);
            CollectionAssert.Contains(
                regions[0].Evidence!.Reasons.ToArray(),
                "onnx_db_text_probability");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbPostprocessRejectsPolygonBelowBoxConfidenceThreshold()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "low-score-db.onnx");
        await File.WriteAllBytesAsync(modelPath, [3, 5, 8, 9]);
        try
        {
            float[] output = RectangleMap(32, 32, 6, 6, 24, 18, 0.55f);
            var factory = new ProbabilityMapSessionFactory(output);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with
                {
                    ProbabilityThreshold = 0.5f,
                    BoxConfidenceThreshold = 0.6f,
                });

            IReadOnlyList<OcrDetectedRegion> regions = await detector.DetectAsync(
                Image(32, 32),
                CancellationToken.None);

            Assert.IsEmpty(regions);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbPostprocessAcceptsPolygonAtExactBoxConfidenceThreshold()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "boundary-score-db.onnx");
        await File.WriteAllBytesAsync(modelPath, [2, 7, 1, 8]);
        try
        {
            float[] output = RectangleMap(32, 32, 6, 6, 24, 18, 0.60f);
            var factory = new ProbabilityMapSessionFactory(output);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with
                {
                    ProbabilityThreshold = 0.5f,
                    BoxConfidenceThreshold = 0.60f,
                    UnclipRatio = 0,
                });

            OcrDetectedRegion region = AssertExactlyOne(
                await detector.DetectAsync(Image(32, 32), CancellationToken.None));

            Assert.AreEqual(0.60, region.DetectionConfidence, 0.0001);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbPostprocessUsesPinnedStrictProbabilityThreshold()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "strict-threshold-db.onnx");
        await File.WriteAllBytesAsync(modelPath, [3, 3, 3, 0]);
        try
        {
            float[] output = RectangleMap(32, 32, 6, 6, 24, 18, 0.30f);
            var factory = new ProbabilityMapSessionFactory(output);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with
                {
                    ProbabilityThreshold = 0.30f,
                    BoxConfidenceThreshold = 0.1f,
                });

            IReadOnlyList<OcrDetectedRegion> regions = await detector.DetectAsync(
                Image(32, 32),
                CancellationToken.None);

            Assert.IsEmpty(regions, "Pinned Paddle DB uses pred > thresh, so equality must remain background.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbPostprocessUsesPinnedFastMiniBoxScore()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "contour-mask-score-db.onnx");
        await File.WriteAllBytesAsync(modelPath, [5, 2, 0, 6]);
        try
        {
            const int width = 32;
            const int height = 32;
            var output = new float[width * height];
            FillRectangle(output, width, 5, 5, 12, 27, 0.9f);
            FillRectangle(output, width, 5, 19, 27, 27, 0.9f);
            var factory = new ProbabilityMapSessionFactory(output);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with
                {
                    ProbabilityThreshold = 0.30f,
                    BoxConfidenceThreshold = 0.80f,
                    UnclipRatio = 1.5,
                });

            IReadOnlyList<OcrDetectedRegion> regions = await detector.DetectAsync(
                Image(width, height),
                CancellationToken.None);

            Assert.IsEmpty(
                regions,
                "Pinned score_mode=fast scores the rotated mini-box, including the concavity's empty area.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbPostprocessAppliesPinnedPostUnclipMinimumSidePlusTwo()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "post-unclip-min-size-db.onnx");
        await File.WriteAllBytesAsync(modelPath, [4, 2, 4, 2]);
        try
        {
            float[] output = RectangleMap(32, 32, 6, 8, 14, 11, 0.9f);
            var factory = new ProbabilityMapSessionFactory(output);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with
                {
                    MinimumSideLength = 2,
                    UnclipRatio = 0,
                });

            IReadOnlyList<OcrDetectedRegion> regions = await detector.DetectAsync(
                Image(32, 32),
                CancellationToken.None);

            Assert.IsEmpty(
                regions,
                "The two-pixel initial short side passes min_size but must fail the pinned post-unclip min_size + 2 gate.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbPostprocessHonorsCancellationAfterProviderExecution()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "post-provider-cancel-db.onnx");
        await File.WriteAllBytesAsync(modelPath, [8, 5, 7, 1]);
        using var cancellation = new CancellationTokenSource();
        try
        {
            float[] output = RectangleMap(32, 32, 6, 6, 24, 18, 0.9f);
            var factory = new ProbabilityMapSessionFactory(output, cancellation.Cancel);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with { BypassCache = true });

            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
                detector.DetectAsync(Image(16, 12), cancellation.Token).AsTask());

            Assert.AreEqual(1, factory.RunCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbPostprocessRoundUnclipMatchesPinnedExpectedBox()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "unclip-db.onnx");
        await File.WriteAllBytesAsync(modelPath, [2, 6, 4, 3]);
        try
        {
            const int width = 32;
            const int height = 32;
            float[] output = RectangleMap(width, height, 8, 8, 14, 14, 0.9f);
            var factory = new ProbabilityMapSessionFactory(output);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var image = new OcrImage(
                width,
                height,
                width,
                new byte[width * height],
                OcrSourceImage.Enhanced,
                OcrFrameTransform.Identity,
                CanonicalOriginalWidth: width,
                CanonicalOriginalHeight: height);
            var compact = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with { UnclipRatio = 0 });
            var expanded = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with { UnclipRatio = 1.5 });

            OcrDetectedRegion compactRegion = AssertExactlyOne(
                await compact.DetectAsync(image, CancellationToken.None));
            OcrDetectedRegion expandedRegion = AssertExactlyOne(
                await expanded.DetectAsync(image, CancellationToken.None));

            Assert.AreEqual(new OcrRectangle(8, 8, 5, 5), compactRegion.Polygon.Bounds);
            Assert.AreEqual(new OcrRectangle(6, 6, 9, 9), expandedRegion.Polygon.Bounds);
            Assert.IsTrue(expandedRegion.Polygon.Points.All(static point =>
                point.X is >= 0 and <= width && point.Y is >= 0 and <= height));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbPostprocessCapsCandidatesDeterministicallyAndFingerprintBindsAlgorithm()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "candidate-cap-db.onnx");
        await File.WriteAllBytesAsync(modelPath, [1, 6, 1, 8]);
        try
        {
            float[] output = new float[32 * 32];
            FillRectangle(output, 32, 2, 3, 8, 9, 0.9f);
            FillRectangle(output, 32, 11, 3, 17, 9, 0.9f);
            FillRectangle(output, 32, 20, 3, 26, 9, 0.9f);
            var factory = new ProbabilityMapSessionFactory(output);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            LocalOnnxTextRegionDetectorOptions dbOptions = DbOptions(Identity(modelPath)) with
            {
                MaximumRegions = 2,
                UnclipRatio = 0,
            };
            var detector = new LocalOnnxTextRegionDetector(runtime, dbOptions);
            var denseDetector = new LocalOnnxTextRegionDetector(
                runtime,
                dbOptions with
                {
                    PostprocessAlgorithm = OcrDetectionPostprocessAlgorithm.DenseProbabilityComponentsV1,
                });

            IReadOnlyList<OcrDetectedRegion> first = await detector.DetectAsync(
                Image(32, 32),
                CancellationToken.None);
            IReadOnlyList<OcrDetectedRegion> second = await detector.DetectAsync(
                Image(32, 32),
                CancellationToken.None);

            Assert.HasCount(2, first);
            CollectionAssert.AreEqual(
                first.Select(static region => region.RegionId).ToArray(),
                second.Select(static region => region.RegionId).ToArray());
            Assert.AreEqual(20, first[0].Polygon.Bounds.Left);
            Assert.AreEqual(11, first[1].Polygon.Bounds.Left);
            Assert.AreNotEqual(detector.ConfigurationFingerprint, denseDetector.ConfigurationFingerprint);
            Assert.AreEqual(1, factory.RunCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbPostprocessUsesPinnedRoundAndClipCoordinateMapping()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "coordinate-rounding-db.onnx");
        await File.WriteAllBytesAsync(modelPath, [2, 7, 1, 8]);
        try
        {
            const int imageWidth = 40;
            const int imageHeight = 40;
            float[] output = RectangleMap(32, 32, 5, 6, 13, 18, 0.9f);
            var factory = new ProbabilityMapSessionFactory(output);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with
                {
                    MaximumSideLength = 32,
                    UnclipRatio = 0,
                });

            OcrDetectedRegion region = AssertExactlyOne(
                await detector.DetectAsync(Image(imageWidth, imageHeight), CancellationToken.None));

            Assert.AreEqual(new OcrRectangle(6, 8, 9, 13), region.Polygon.Bounds);
            Assert.IsTrue(region.Polygon.Points.All(static point =>
                point.X == Math.Truncate(point.X) && point.Y == Math.Truncate(point.Y)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DbPostprocessPreservesFractionalOriginalCoordinatesAfterTransformInversion()
    {
        string directory = CreateDirectory();
        string modelPath = Path.Combine(directory, "coordinate-transform-db.onnx");
        await File.WriteAllBytesAsync(modelPath, [7, 2, 5, 0]);
        try
        {
            const int imageWidth = 40;
            const int imageHeight = 40;
            float[] output = RectangleMap(32, 32, 5, 6, 13, 18, 0.9f);
            var factory = new ProbabilityMapSessionFactory(output);
            await using InferenceRuntime runtime = CreateRuntime(directory, factory);
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                DbOptions(Identity(modelPath)) with
                {
                    MaximumSideLength = 32,
                    UnclipRatio = 0,
                });
            var image = new OcrImage(
                imageWidth,
                imageHeight,
                imageWidth,
                new byte[imageWidth * imageHeight],
                OcrSourceImage.Enhanced,
                new OcrFrameTransform(2, 2, 0, 0),
                CanonicalOriginalWidth: imageWidth / 2,
                CanonicalOriginalHeight: imageHeight / 2);

            OcrDetectedRegion region = AssertExactlyOne(
                await detector.DetectAsync(image, CancellationToken.None));

            Assert.IsTrue(region.Polygon.Points.Any(static point =>
                point.X != Math.Truncate(point.X) || point.Y != Math.Truncate(point.Y)));
            Assert.IsTrue(region.Polygon.Points.All(static point =>
                point.X is >= 0 and <= imageWidth / 2 &&
                point.Y is >= 0 and <= imageHeight / 2));
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
        ProbabilityThreshold = 0.5f,
        BoxConfidenceThreshold = 0.8f,
        UnclipRatio = 0,
        MinimumComponentArea = 2,
        MinimumSideLength = 1,
        MaximumRegions = 20,
        AllowedProviders = [InferenceProvider.Cpu],
    };

    private static LocalOnnxTextRegionDetectorOptions DbOptions(ModelIdentity model) =>
        Options(model) with
        {
            PostprocessAlgorithm = OcrDetectionPostprocessAlgorithm.DbPostprocessV1,
            MinimumComponentArea = 3,
            MinimumSideLength = 2,
        };

    private static OcrImage Image() => new(
        8,
        4,
        8,
        Enumerable.Repeat((byte)255, 32).ToArray(),
        OcrSourceImage.Original,
        OcrFrameTransform.Identity);

    private static OcrImage Image(int width, int height) => new(
        width,
        height,
        width,
        new byte[width * height],
        OcrSourceImage.Original,
        OcrFrameTransform.Identity,
        CanonicalOriginalWidth: width,
        CanonicalOriginalHeight: height);

    private static OcrDetectedRegion AssertExactlyOne(IReadOnlyList<OcrDetectedRegion> regions)
    {
        Assert.HasCount(1, regions);
        return regions[0];
    }

    private static float[] RectangleMap(
        int width,
        int height,
        int left,
        int top,
        int right,
        int bottom,
        float probability)
    {
        var values = new float[width * height];
        FillRectangle(values, width, left, top, right, bottom, probability);
        return values;
    }

    private static byte[] PatternedBgr(int width, int height)
    {
        var pixels = new byte[checked(width * height * 3)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int index = checked(((y * width) + x) * 3);
                pixels[index] = (byte)((x * 17 + y * 29 + 3) % 256);
                pixels[index + 1] = (byte)((x * 7 + y * 11 + 19) % 256);
                pixels[index + 2] = (byte)((x * 3 + y * 5 + 101) % 256);
            }
        }

        return pixels;
    }

    private static string TensorSha256(float[] values)
    {
        var bytes = new byte[checked(values.Length * sizeof(float))];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static void AssertTensorPixel(
        float[] tensor,
        int width,
        int height,
        int x,
        int y,
        float expectedB,
        float expectedG,
        float expectedR)
    {
        int pixelsPerChannel = checked(width * height);
        int pixel = checked((y * width) + x);
        Assert.AreEqual(expectedB, tensor[pixel], 0.0000001f);
        Assert.AreEqual(expectedG, tensor[pixelsPerChannel + pixel], 0.0000001f);
        Assert.AreEqual(expectedR, tensor[(2 * pixelsPerChannel) + pixel], 0.0000001f);
    }

    private static void FillRectangle(
        float[] values,
        int width,
        int left,
        int top,
        int right,
        int bottom,
        float probability)
    {
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                values[(y * width) + x] = probability;
            }
        }
    }

    private static float[] RotatedRectangleMap(
        int width,
        int height,
        double centerX,
        double centerY,
        double halfWidth,
        double halfHeight,
        double angleDegrees,
        float probability)
    {
        var values = new float[width * height];
        double radians = angleDegrees * Math.PI / 180d;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double deltaX = (x + 0.5) - centerX;
                double deltaY = (y + 0.5) - centerY;
                double localX = (deltaX * cosine) + (deltaY * sine);
                double localY = (-deltaX * sine) + (deltaY * cosine);
                if (Math.Abs(localX) <= halfWidth && Math.Abs(localY) <= halfHeight)
                {
                    values[(y * width) + x] = probability;
                }
            }
        }

        return values;
    }

    private static ModelIdentity Identity(string path) => new(
        "fixture-ocr-detector",
        "1.0.0",
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
        path);

    private static InferenceRuntime CreateRuntime(
        string directory,
        ProbabilityMapSessionFactory factory)
    {
        var registry = new OnnxSessionRegistry(
            new FakeExecutionProviderDiscovery("DmlExecutionProvider", "CPUExecutionProvider"),
            new WindowsExecutionProviderPolicy(),
            factory,
            CpuThreadConfiguration.Create(1));
        return new InferenceRuntime(
            registry,
            new BoundedInferenceScheduler(capacity: 2, workerCount: 1),
            new ContentAddressedStageCache(Path.Combine(directory, "cache")));
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "GraphReaderOcrDetectorTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ProbabilityMapSessionFactory(
        float[] output,
        Action? afterProviderExecution = null) : IInferenceSessionFactory
    {
        private readonly float[] output = (float[])output.Clone();
        private readonly Action? afterProviderExecution = afterProviderExecution;

        public List<InferenceProvider> CreatedProviders { get; } = [];

        public InferenceInput? LastInput { get; private set; }

        public int RunCount { get; private set; }

        public ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreatedProviders.Add(provider);
            return ValueTask.FromResult<IInferenceSession>(new Session(this, provider));
        }

        private sealed class Session(
            ProbabilityMapSessionFactory owner,
            InferenceProvider provider) : IInferenceSession
        {
            public InferenceProvider Provider { get; } = provider;

            public ValueTask<InferenceExecution> RunAsync(
                InferenceInput input,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.LastInput = new InferenceInput(
                    input.Values.ToArray(),
                    input.Shape.ToArray(),
                    input.InputName,
                    input.OutputName);
                owner.RunCount++;
                owner.afterProviderExecution?.Invoke();
                return ValueTask.FromResult(new InferenceExecution(
                    Array.AsReadOnly((float[])owner.output.Clone()),
                    Provider,
                    new StageTiming(0, 1, 0, 1, 0, owner.RunCount == 1, false),
                    new MemoryDiagnostics(0, 0, 0, 0, owner.output.Length)));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
