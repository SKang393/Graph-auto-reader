// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.NativeAxis;
using GraphReader.Axis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class NativeAxisWorkerDetectionFailureTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BlankOrSingleOrientationEvidenceIsReportedAsAmbiguous(bool horizontalOnly) => StaTestHost.Run(() =>
    {
        RustAxisProposalResult result = NativeAxisWorker.ProcessAsync(CreatePng(horizontalOnly), CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.IsNull(result.Geometry);
        Assert.AreEqual("OPENCV_AXIS_AMBIGUOUS", result.ErrorCode);
        Assert.AreEqual("opencv-review", result.Provider);
    });

    [TestMethod]
    public void PreCanceledDetectionStillPropagatesCancellation() => StaTestHost.Run(() =>
    {
        byte[] png = CreatePng(horizontalOnly: false);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            NativeAxisWorker.ProcessAsync(png, canceled.Token).GetAwaiter().GetResult());
    });

    private static byte[] CreatePng(bool horizontalOnly)
    {
        const int width = 200;
        const int height = 120;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        if (horizontalOnly)
        {
            for (int x = 10; x < width - 10; x++) pixels[(60 * width) + x] = 0;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Gray8, null, pixels, width)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
