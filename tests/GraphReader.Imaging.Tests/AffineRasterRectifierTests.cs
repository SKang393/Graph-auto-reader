// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Imaging.Tests;

[TestClass]
public sealed class AffineRasterRectifierTests
{
    [TestMethod]
    public void IdentityFramePreservesPixelsAndAxisCoordinates()
    {
        const int width = 7, height = 5, stride = 9;
        byte[] source = Enumerable.Repeat((byte)255, stride * height).ToArray();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                source[(y * stride) + x] = (byte)((y * width + x) * 5);
        byte[] original = source.ToArray();

        AffineRasterRectificationResult result = AffineRasterRectifier.Rectify(
            source, width, height, stride, new(1, 3), new(5, 3), new(1, 0));

        Assert.AreEqual(width, result.Width);
        Assert.AreEqual(height, result.Height);
        Assert.AreEqual(width, result.Stride);
        Assert.AreEqual(1, result.Left);
        Assert.AreEqual(0, result.Top);
        Assert.AreEqual(5, result.Right);
        Assert.AreEqual(3, result.Bottom);
        CollectionAssert.AreEqual(source.Take(width).ToArray(), result.Pixels.Span[..width].ToArray());
        for (int y = 0; y < height; y++)
            CollectionAssert.AreEqual(source.AsSpan(y * stride, width).ToArray(),
                result.Pixels.Span.Slice(y * result.Stride, width).ToArray());
        CollectionAssert.AreEqual(original, source);
    }

    [TestMethod]
    [DataRow(14d, 38d, 42d, 40d, 12d, 10d)]
    [DataRow(14d, 38d, 42d, 34d, 18d, 10d)]
    public void TiltedAndShearedFramesMapAnchorsAndRoundTrip(
        double ox, double oy, double xx, double xy, double yx, double yy)
    {
        PixelPoint origin = new(ox, oy), xEnd = new(xx, xy), yEnd = new(yx, yy);
        byte[] source = Enumerable.Repeat((byte)255, 64 * 48).ToArray();

        AffineRasterRectificationResult result = AffineRasterRectifier.Rectify(
            source, 64, 48, 64, origin, xEnd, yEnd);

        PixelPoint mappedOrigin = result.TransformChain.ToDerived(origin);
        PixelPoint mappedX = result.TransformChain.ToDerived(xEnd);
        PixelPoint mappedY = result.TransformChain.ToDerived(yEnd);
        Assert.AreEqual(result.Left, mappedOrigin.X, 1e-9);
        Assert.AreEqual(result.Bottom, mappedOrigin.Y, 1e-9);
        Assert.AreEqual(result.Right, mappedX.X, 1e-9);
        Assert.AreEqual(result.Bottom, mappedX.Y, 1e-9);
        Assert.AreEqual(result.Left, mappedY.X, 1e-9);
        Assert.AreEqual(result.Top, mappedY.Y, 1e-9);

        PixelPoint probe = new(28.25, 21.75);
        PixelPoint restored = result.TransformChain.ToOriginal(result.TransformChain.ToDerived(probe));
        Assert.AreEqual(probe.X, restored.X, 1e-8);
        Assert.AreEqual(probe.Y, restored.Y, 1e-8);
        Assert.IsTrue(result.TransformChain.DerivedToOriginal.HasValue);
    }

    [TestMethod]
    public void FullSourceMarginsRemainInsideRectifiedRasterAndSourceIsUntouched()
    {
        const int width = 64, height = 48;
        byte[] source = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int row = 2; row < 9; row++)
            for (int column = 2; column < 9; column++)
                source[(row * width) + column] = 0;
        byte[] original = source.ToArray();

        AffineRasterRectificationResult result = AffineRasterRectifier.Rectify(
            source, width, height, width, new(14, 38), new(42, 40), new(12, 10));

        PixelPoint transformedMargin = result.TransformChain.ToDerived(new PixelPoint(5, 5));
        int x = (int)Math.Round(transformedMargin.X, MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(transformedMargin.Y, MidpointRounding.AwayFromZero);
        Assert.IsTrue(x >= 0 && x < result.Width && y >= 0 && y < result.Height);
        Assert.IsTrue(result.Pixels.Span[(y * result.Stride) + x] < 255,
            "A planted mark outside the axis quadrilateral should survive in the full-source rectification.");
        CollectionAssert.AreEqual(original, source);
    }

    [TestMethod]
    public void RejectsInvalidRasterAnchorsAndFourthCorner()
    {
        byte[] pixels = new byte[64 * 64];
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AffineRasterRectifier.Rectify(
            pixels, 64, 64, 64, new(double.NaN, 20), new(40, 20), new(20, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AffineRasterRectifier.Rectify(
            pixels, 64, 64, 64, new(1, 63), new(62, 63), new(1, 64)));
        Assert.ThrowsExactly<ArgumentException>(() => AffineRasterRectifier.Rectify(
            pixels, 64, 64, 63, new(10, 40), new(40, 40), new(10, 10)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AffineRasterRectifier.Rectify(
            pixels, 64, 64, 64, new(30, 40), new(60, 40), new(60, 10)));
        Assert.ThrowsExactly<ArgumentException>(() => AffineRasterRectifier.Rectify(
            pixels, 64, 64, 64, new(20, 40), new(40, 20), new(30, 30)));
        Assert.ThrowsExactly<ArgumentException>(() => AffineRasterRectifier.Rectify(
            pixels, 64, 64, 64, new(10, 40), new(30, 20), new(40, 30)));
    }

    [TestMethod]
    public void RejectsOversizedNearSingularOutputAndCancellation()
    {
        byte[] pixels = new byte[64 * 64];
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AffineRasterRectifier.Rectify(
            pixels, 64, 64, 64, new(1, 63), new(32, 32), new(30, 33)));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => AffineRasterRectifier.Rectify(
            pixels, 64, 64, 64, new(10, 40), new(40, 40), new(10, 10), cancellation.Token));
    }

    [TestMethod]
    public async Task CancellationDuringLargeRasterWorkStopsRectification()
    {
        const int side = 4096;
        byte[] pixels = new byte[side * side];
        using var started = new ManualResetEventSlim();
        using var memory = new SignalingMemoryManager(pixels, started);
        Memory<byte> sourceMemory = memory.Memory;
        using var cancellation = new CancellationTokenSource();
        Task<AffineRasterRectificationResult> work = Task.Run(() => AffineRasterRectifier.Rectify(
            sourceMemory, side, side, side, new(0, side - 1), new(side - 1, side - 1), new(0, 0), cancellation.Token));

        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)), "Rectification did not reach source-pixel sampling.");
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await work);
    }

    private sealed class SignalingMemoryManager(byte[] pixels, ManualResetEventSlim started) : MemoryManager<byte>
    {
        private int spanAccesses;

        public override Span<byte> GetSpan()
        {
            if (Interlocked.Increment(ref spanAccesses) >= 2)
                started.Set();
            return pixels;
        }

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }
}
