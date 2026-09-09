// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using GraphReader.App.Integration.Workflow;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class OriginalDbInputDetectorTests
{
    [TestMethod]
    public async Task AlignedMaskedDerivativeCannotReplaceOriginalDetectorPixels()
    {
        var recording = new RecordingDetector();
        var detector = new ProductionOcrAdapter.OriginalDbInputDetector(recording);
        OcrImage original = Original();
        OcrImage derivative = original with { Pixels = new byte[] { 255, 255, 255, 255 } };
        _ = await detector.DetectAsync(original, derivative, CancellationToken.None);
        Assert.AreSame(original, recording.Image);
        StringAssert.Contains(detector.ConfigurationFingerprint, recording.ConfigurationFingerprint);
    }

    [TestMethod]
    public async Task MisalignedDerivativeFailsBeforeDetectorExecution()
    {
        var recording = new RecordingDetector();
        var detector = new ProductionOcrAdapter.OriginalDbInputDetector(recording);
        OcrImage original = Original();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            _ = await detector.DetectAsync(original, original with { Width = 3 }, CancellationToken.None));
        Assert.IsNull(recording.Image);
    }

    [TestMethod]
    public async Task CancellationFailsBeforeDetectorExecution()
    {
        var recording = new RecordingDetector();
        var detector = new ProductionOcrAdapter.OriginalDbInputDetector(recording);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            _ = await detector.DetectAsync(Original(), new CancellationToken(canceled: true)));
        Assert.IsNull(recording.Image);
    }

    private static OcrImage Original() => new(2, 2, 2, new byte[] { 0, 1, 2, 3 },
        OcrSourceImage.Original, OcrFrameTransform.Identity,
        CanonicalOriginalWidth: 2, CanonicalOriginalHeight: 2);

    private sealed class RecordingDetector : ITextRegionDetector
    {
        public OcrImage? Image { get; private set; }
        public string ConfigurationFingerprint => "fixed-recording-detector";
        public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(OcrImage image,
            CancellationToken cancellationToken)
        {
            Image = image;
            return ValueTask.FromResult<IReadOnlyList<OcrDetectedRegion>>([]);
        }
    }
}
