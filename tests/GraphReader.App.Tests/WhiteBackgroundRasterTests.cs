// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class WhiteBackgroundRasterTests
{
    [TestMethod]
    public void TransparentAndPartialPixelsCompositeWithoutChangingSource() => StaTestHost.Run(() =>
    {
        byte[] pixels = [0, 0, 0, 0, 0, 0, 0, 128, 0, 0, 0, 255];
        byte[] before = SHA256.HashData(pixels);
        BitmapSource source = BitmapSource.Create(3, 1, 123, 147, PixelFormats.Bgra32, null, pixels, 12);
        BitmapSource result = WhiteBackgroundRaster.Normalize(source, CancellationToken.None);
        byte[] actual = new byte[12]; result.CopyPixels(actual, 12, 0);
        CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 255, 127, 127, 127, 255, 0, 0, 0, 255 }, actual);
        CollectionAssert.AreEqual(before, SHA256.HashData(pixels));
        byte[] sourcePixels = new byte[12]; source.CopyPixels(sourcePixels, 12, 0);
        CollectionAssert.AreEqual(pixels, sourcePixels);
        Assert.AreEqual(source.DpiX, result.DpiX);
        Assert.AreEqual(source.DpiY, result.DpiY);
        Assert.AreEqual(source.PixelWidth, result.PixelWidth);
        Assert.AreEqual(source.PixelHeight, result.PixelHeight);
    });

    [TestMethod]
    public void OpaquePixelsRetainOriginalConversionAndPremultipliedAndIndexedAlphaWork() => StaTestHost.Run(() =>
    {
        BitmapSource opaque = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, new byte[] { 12, 34, 56 }, 3);
        Assert.AreSame(opaque, WhiteBackgroundRaster.Normalize(opaque, CancellationToken.None));
        BitmapSource premultiplied = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[] { 0, 0, 0, 128 }, 4);
        byte[] actual = new byte[4];
        WhiteBackgroundRaster.Normalize(premultiplied, CancellationToken.None).CopyPixels(actual, 4, 0);
        CollectionAssert.AreEqual(new byte[] { 127, 127, 127, 255 }, actual);
        BitmapSource colored = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[] { 32, 64, 96, 128 }, 4);
        WhiteBackgroundRaster.Normalize(colored, CancellationToken.None).CopyPixels(actual, 4, 0);
        CollectionAssert.AreEqual(new byte[] { 159, 191, 223, 255 }, actual);
        var palette = new BitmapPalette([Color.FromArgb(0, 0, 0, 0), Colors.Black]);
        BitmapSource indexed = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Indexed8, palette, new byte[] { 0, 1 }, 2);
        actual = new byte[8]; WhiteBackgroundRaster.Normalize(indexed, CancellationToken.None).CopyPixels(actual, 8, 0);
        CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 255, 0, 0, 0, 255 }, actual);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => WhiteBackgroundRaster.Normalize(indexed, cancelled.Token));
    });
}
