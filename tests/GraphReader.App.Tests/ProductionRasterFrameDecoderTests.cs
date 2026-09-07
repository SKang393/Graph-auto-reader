// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionRasterFrameDecoderTests
{
    [TestMethod]
    public void DecodeCompositesAlphaOntoWhiteAndMatchesPillowLuminance()
    {
        byte[] encoded = EncodeBgraPng(
        [
            0, 0, 0, 0,
            0, 0, 0, 128,
            0, 0, 255, 255,
            0, 255, 0, 255,
            255, 0, 0, 255,
        ], width: 5, height: 1);

        ProductionDecodedRaster decoded = new ProductionRasterFrameDecoder().Decode(
            CreateRequest(encoded, 5, 1), CancellationToken.None);

        OcrImage ocr = decoded.CreateOcrImage();
        CollectionAssert.AreEqual(
            new byte[] { 255, 127, 76, 150, 29 },
            ocr.Pixels.Span.ToArray());
        CollectionAssert.AreEqual(
            new byte[]
            {
                255, 255, 255,
                127, 127, 127,
                0, 0, 255,
                0, 255, 0,
                255, 0, 0,
            },
            ocr.BgrPixels!.Pixels.Span.ToArray());

        MarkerImageFrame marker = decoded.CreateMarkerFrame(
            MarkerMask.Empty(5, 1), MarkerMask.Empty(5, 1));
        CollectionAssert.AreEqual(
            new[] { 1f, 127f / 255f, 76f / 255f, 150f / 255f, 29f / 255f },
            marker.ChannelsFirstPixels.ToArray());
    }

    [TestMethod]
    public void DecodeRejectsDimensionMismatchBeforeReturningPixels()
    {
        byte[] encoded = EncodeBgraPng([255, 255, 255, 255], width: 1, height: 1);

        ProductionWorkflowStageException exception = Assert.ThrowsExactly<ProductionWorkflowStageException>(
            () => new ProductionRasterFrameDecoder().Decode(
                CreateRequest(encoded, 2, 1), CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionEvidenceRejected, exception.Failure.Code);
    }

    private static ProductionWorkflowDetectionRequest CreateRequest(
        byte[] encoded,
        int width,
        int height)
    {
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded));
        var image = new WorkflowImageEvidence(
            "decoder-test.png", sha256, width, height, WorkflowImageVariant.Original);
        var panel = new WorkflowImportedPanel(
            Guid.NewGuid(), Guid.NewGuid(), "decoder-test.png", image);
        return new ProductionWorkflowDetectionRequest(
            new WorkflowPreparedPanel(panel, image, null),
            image,
            WorkflowImageVariant.Original,
            Guid.NewGuid(),
            Guid.NewGuid(),
            encoded);
    }

    private static byte[] EncodeBgraPng(byte[] pixels, int width, int height)
    {
        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            checked(width * 4));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
