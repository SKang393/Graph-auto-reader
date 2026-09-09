// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.Ocr;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class OcrDualInputTextRegionDetectorTests
{
    [TestMethod]
    public async Task OriginalModeRoutesUntouchedOriginalToModelAndMaskedDerivativeToStructure()
    {
        byte[] originalGray = Enumerable.Range(0, 240).Select(static value => (byte)value).ToArray();
        byte[] originalBgr = Enumerable.Range(0, 720).Select(static value => (byte)(value % 251)).ToArray();
        byte[] maskedGray = Enumerable.Repeat((byte)255, 240).ToArray();
        byte[] maskedBgr = Enumerable.Repeat((byte)255, 720).ToArray();
        byte[] expectedOriginalGray = originalGray.ToArray();
        byte[] expectedOriginalBgr = originalBgr.ToArray();
        byte[] expectedMaskedGray = maskedGray.ToArray();
        byte[] expectedMaskedBgr = maskedBgr.ToArray();
        OcrImage original = Image(originalGray, originalBgr);
        OcrImage masked = Image(maskedGray, maskedBgr);
        var model = new RecordingDetector([Region("model", evidence: null)], "model-v1");
        var structure = new RecordingDetector([
            Region("component", Evidence()),
        ], "structure-v1");
        var consensus = new GraphStructureConsensusTextRegionDetector(
            model,
            structure,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                ModelInput = GraphStructureModelInput.Original,
            });
        var recognizer = new StubTextRecognizer((crops, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                new OcrRecognition(
                    crop.RegionId,
                    crop.SourceImage,
                    [new OcrRecognitionAlternative("10", 0.95, crop.SourceImage)],
                    0.1)).ToArray());
        });
        var pipeline = new OcrPipeline(
            consensus,
            recognizer,
            new InMemoryOcrResultCache(),
            new OcrPipelineOptions
            {
                CropWidth = 8,
                CropHeight = 4,
                CropPaddingPixels = 0,
            });
        OcrRequest request = Request(original, masked);

        OcrResult result = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.AreSame(original, model.LastImage);
        Assert.AreSame(masked, structure.LastImage);
        CollectionAssert.AreEqual(expectedOriginalGray, originalGray);
        CollectionAssert.AreEqual(expectedOriginalBgr, originalBgr);
        CollectionAssert.AreEqual(expectedMaskedGray, maskedGray);
        CollectionAssert.AreEqual(expectedMaskedBgr, maskedBgr);
    }

    [TestMethod]
    public async Task DefaultModePreservesSingleMaskedInputBehaviorAndFingerprint()
    {
        OcrImage masked = Image(new byte[240], new byte[720]);
        var model = new RecordingDetector([Region("model", evidence: null)], "model-v1");
        var structure = new RecordingDetector([Region("component", Evidence())], "structure-v1");
        var consensus = new GraphStructureConsensusTextRegionDetector(model, structure);

        IReadOnlyList<OcrDetectedRegion> result = await consensus.DetectAsync(
            masked,
            CancellationToken.None);

        Assert.HasCount(1, result);
        Assert.AreSame(masked, model.LastImage);
        Assert.AreSame(masked, structure.LastImage);
        Assert.AreEqual(
            "graph-structure-consensus-v1:0.5:0.45:model=model-v1:candidate=structure-v1",
            consensus.ConfigurationFingerprint);
    }

    [TestMethod]
    public void OriginalModeHasDistinctCompositionFingerprintAndCacheBindsBothImages()
    {
        var model = new RecordingDetector([], "model-v1");
        var structure = new RecordingDetector([], "structure-v1");
        var baseline = new GraphStructureConsensusTextRegionDetector(model, structure);
        var originalMode = new GraphStructureConsensusTextRegionDetector(
            model,
            structure,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                ModelInput = GraphStructureModelInput.Original,
            });
        OcrImage originalA = Image(new byte[240], new byte[720]);
        OcrImage originalB = Image(Enumerable.Repeat((byte)1, 240).ToArray(), new byte[720]);
        OcrImage maskedA = Image(Enumerable.Repeat((byte)255, 240).ToArray(), new byte[720]);
        OcrImage maskedB = Image(Enumerable.Repeat((byte)254, 240).ToArray(), new byte[720]);
        var recognizer = new StubTextRecognizer(
            new Dictionary<(string RegionId, OcrSourceImage Source), IReadOnlyList<OcrRecognitionAlternative>>());

        string first = CacheKey(Request(originalA, maskedA), originalMode, recognizer);
        string changedOriginal = CacheKey(Request(originalB, maskedA), originalMode, recognizer);
        string changedMasked = CacheKey(Request(originalA, maskedB), originalMode, recognizer);
        string changedMode = CacheKey(Request(originalA, maskedA), baseline, recognizer);

        Assert.AreEqual(
            "graph-structure-consensus-original-model-input-v1:0.5:0.45:model=model-v1:candidate=structure-v1",
            originalMode.ConfigurationFingerprint);
        Assert.AreNotEqual(first, changedOriginal);
        Assert.AreNotEqual(first, changedMasked);
        Assert.AreNotEqual(first, changedMode);
    }

    [TestMethod]
    public async Task OriginalModeRejectsMissingOrMisalignedDetectorContextBeforeRunningDetectors()
    {
        OcrImage original = Image(new byte[240], new byte[720]);
        OcrImage misaligned = original with
        {
            OriginalToImage = new OcrFrameTransform(2, 1, 0, 0),
        };
        var model = new RecordingDetector([], "model-v1");
        var structure = new RecordingDetector([], "structure-v1");
        var consensus = new GraphStructureConsensusTextRegionDetector(
            model,
            structure,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                ModelInput = GraphStructureModelInput.Original,
            });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await consensus.DetectAsync(original, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await consensus.DetectAsync(original, misaligned, CancellationToken.None));

        Assert.AreEqual(0, model.CallCount);
        Assert.AreEqual(0, structure.CallCount);
    }

    [TestMethod]
    public async Task OriginalModeObservesCancellationBeforeEitherDetectorRuns()
    {
        OcrImage original = Image(new byte[240], new byte[720]);
        OcrImage masked = Image(Enumerable.Repeat((byte)255, 240).ToArray(), new byte[720]);
        var model = new RecordingDetector([], "model-v1");
        var structure = new RecordingDetector([], "structure-v1");
        var consensus = new GraphStructureConsensusTextRegionDetector(
            model,
            structure,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                ModelInput = GraphStructureModelInput.Original,
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await consensus.DetectAsync(original, masked, cancellation.Token));

        Assert.AreEqual(0, model.CallCount);
        Assert.AreEqual(0, structure.CallCount);
    }

    [TestMethod]
    public async Task PipelineFailsClosedWhenOriginalModeHasNoDetectorDerivative()
    {
        OcrImage original = Image(new byte[240], new byte[720]);
        var model = new RecordingDetector([], "model-v1");
        var structure = new RecordingDetector([], "structure-v1");
        var consensus = new GraphStructureConsensusTextRegionDetector(
            model,
            structure,
            new GraphStructureConsensusTextRegionDetectorOptions
            {
                ModelInput = GraphStructureModelInput.Original,
            });
        var pipeline = new OcrPipeline(
            consensus,
            new StubTextRecognizer((_, _) => throw new AssertFailedException("Recognizer must not run.")),
            new InMemoryOcrResultCache());
        OcrRequest request = Request(original, original) with { DetectorImage = null };

        OcrResult result = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.AreEqual("OCR_REGION_DETECTION_FAILED", result.Failure?.Code);
        Assert.AreEqual(0, model.CallCount);
        Assert.AreEqual(0, structure.CallCount);
    }

    [TestMethod]
    public void UnknownModelInputFailsClosed()
    {
        var model = new RecordingDetector([], "model-v1");
        var structure = new RecordingDetector([], "structure-v1");

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new GraphStructureConsensusTextRegionDetector(
                model,
                structure,
                new GraphStructureConsensusTextRegionDetectorOptions
                {
                    ModelInput = (GraphStructureModelInput)99,
                }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            GraphStructureConsensusTextRegionDetector.GetCompositionVersion(
                GraphStructureConsensusGeometry.ModelPolygon,
                (GraphStructureModelInput)99));
    }

    private static string CacheKey(
        OcrRequest request,
        ITextRegionDetector detector,
        ITextRecognizer recognizer) => OcrCacheKeyDeriver.CreateRequestAlias(
            request,
            recognizer,
            new OcrPipelineOptions(),
            detector.ConfigurationFingerprint);

    private static OcrRequest Request(OcrImage original, OcrImage masked) => new(
        "11111111-1111-1111-1111-111111111111",
        "22222222-2222-2222-2222-222222222222",
        new string('a', 64),
        original,
        new OcrRectangle(0, 0, original.Width, original.Height),
        DetectorImage: new OcrDetectorImage(
            masked,
            Convert.ToHexStringLower(SHA256.HashData(masked.Pixels.Span)),
            Convert.ToHexStringLower(SHA256.HashData(masked.BgrPixels!.Pixels.Span))));

    private static OcrImage Image(byte[] gray, byte[] bgr) => new(
        20,
        12,
        20,
        gray,
        OcrSourceImage.Original,
        OcrFrameTransform.Identity,
        CanonicalOriginalWidth: 20,
        CanonicalOriginalHeight: 12,
        BgrPixels: new OcrBgrBytePixels(60, bgr));

    private static OcrDetectedRegion Region(string id, OcrRegionEvidence? evidence) => new(
        id,
        OcrPolygon.FromRectangle(new OcrRectangle(4, 3, 8, 4)),
        0,
        0.9,
        Evidence: evidence);

    private static OcrRegionEvidence Evidence() => new(
        2,
        0.4,
        0.9,
        0.1,
        false,
        ["text_candidate"]);

    private sealed class RecordingDetector(
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
