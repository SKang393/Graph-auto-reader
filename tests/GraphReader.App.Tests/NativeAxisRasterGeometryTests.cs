// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App;
using GraphReader.App.Integration.NativeAxis;
using GraphReader.Axis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class NativeAxisRasterGeometryTests
{
    private static readonly int[] PhaseXTicks = [140, 340, 540];
    private static readonly int[] PhaseYTicks = [100, 230, 360];

    [TestMethod]
    [DataRow(-2, -2)]
    [DataRow(-1, -1)]
    [DataRow(0, 0)]
    [DataRow(1, 1)]
    [DataRow(2, 2)]
    public void AdjustsOnlyToObservedNearEndpoints(int rightOffset, int topOffset)
    {
        byte[] pixels = Raster(80 + rightOffset, 20 + topOffset);
        var geometry = new RustAxisGeometry(10, 90, 80, 20, 0.8);
        var adjusted = NativeAxisRasterGeometry.Adjust(geometry, pixels, 100, 100);
        Assert.AreEqual(80 + rightOffset, adjusted.Right);
        Assert.AreEqual(20 + topOffset, adjusted.Top);
        Assert.AreEqual(geometry.Left, adjusted.Left);
        Assert.AreEqual(geometry.Bottom, adjusted.Bottom);
        Assert.AreEqual(geometry.Confidence, adjusted.Confidence);
    }

    [TestMethod]
    public void BeyondRadiusAndDisconnectedOriginLeaveGeometryUnchanged()
    {
        var geometry = new RustAxisGeometry(10, 90, 80, 20, 0.8);
        foreach (var (right, top) in new[] { (77, 23), (83, 17) })
            Assert.AreEqual(geometry, NativeAxisRasterGeometry.Adjust(geometry, Raster(right, top), 100, 100));
        byte[] disconnected = Raster(79, 21);
        disconnected[90 * 100 + 10] = 255;
        Assert.AreEqual(geometry, NativeAxisRasterGeometry.Adjust(geometry, disconnected, 100, 100));
    }

    [TestMethod]
    public void DetachedInkCannotPullEndpointAcrossGap()
    {
        var geometry = new RustAxisGeometry(10, 90, 80, 20, 0.8);
        byte[] pixels = Raster(79, 21);
        pixels[90 * 100 + 81] = 0;
        pixels[19 * 100 + 10] = 0;
        var adjusted = NativeAxisRasterGeometry.Adjust(geometry, pixels, 100, 100);
        Assert.AreEqual(79, adjusted.Right);
        Assert.AreEqual(21, adjusted.Top);
    }

    [TestMethod]
    public void GapBetweenOriginAndFarSegmentPreventsAdjustment()
    {
        var geometry = new RustAxisGeometry(10, 90, 80, 20, 0.8);
        byte[] pixels = Raster(79, 21);
        pixels[90 * 100 + 45] = 255;
        pixels[55 * 100 + 10] = 255;
        Assert.AreEqual(geometry, NativeAxisRasterGeometry.Adjust(geometry, pixels, 100, 100));
    }

    [TestMethod]
    public void CancelledScanFailsWithoutReturningGeometry()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => NativeAxisRasterGeometry.Adjust(
            new RustAxisGeometry(10, 90, 80, 20, 0.8), Raster(79, 21), 100, 100, source.Token));
    }

    [TestMethod]
    public void AntialiasedEncodedGeometryMatchesFirstCandidateAfterRefinement() => StaTestHost.Run(() =>
    {
        const int width = 640, height = 480;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int x = 99; x <= 540; x++) { pixels[379 * width + x] = 63; pixels[380 * width + x] = 191; }
        for (int y = 80; y <= 380; y++)
        {
            int first = y * width + 99, second = y * width + 100;
            pixels[first] = Math.Min(pixels[first], (byte)63);
            pixels[second] = Math.Min(pixels[second], (byte)191);
        }
        foreach (int x in PhaseXTicks) for (int y = 380; y <= 385; y++)
        {
            int first = y * width + x, second = first + 1;
            pixels[first] = Math.Min(pixels[first], (byte)63);
            pixels[second] = Math.Min(pixels[second], (byte)191);
        }
        foreach (int y in PhaseYTicks) for (int x = 94; x <= 100; x++)
        {
            int first = y * width + x, second = first + width;
            pixels[first] = Math.Min(pixels[first], (byte)63);
            pixels[second] = Math.Min(pixels[second], (byte)191);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width)));
        using var stream = new MemoryStream(); encoder.Save(stream);
        byte[] image = stream.ToArray();
        var normal = Pump(NativeAxisWorker.ProcessAsync(image, CancellationToken.None));
        var candidates = Pump(NativeAxisCandidateReview.ProcessAsync(image, CancellationToken.None));
        Assert.IsNotNull(normal.Geometry);
        Assert.IsNull(normal.ErrorCode);
        Assert.IsNull(candidates.ErrorCode);
        Assert.IsGreaterThan(0, candidates.Candidates.Count);
        var firstCandidate = candidates.Candidates[0].Geometry;
        Assert.IsNotNull(firstCandidate);
        Assert.AreEqual((normal.Geometry.Left, normal.Geometry.Bottom, normal.Geometry.Right, normal.Geometry.Top),
            (firstCandidate.Left, firstCandidate.Bottom, firstCandidate.Right, firstCandidate.Top));
        Assert.AreEqual(540, normal.Geometry.Right);
        CollectionAssert.AreEqual(Enumerable.Range(0, candidates.Candidates.Count).ToArray(), candidates.Candidates.Select(c => c.Index).ToArray());
        Assert.AreEqual(candidates.Candidates.Count, candidates.Candidates.Select(c =>
        {
            Assert.IsNotNull(c.Geometry);
            return (c.Geometry.Left, c.Geometry.Bottom, c.Geometry.Right, c.Geometry.Top);
        }).Distinct().Count());
    });

    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private static byte[] Raster(int right, int top)
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 100 * 100).ToArray();
        for (int x = 10; x <= right; x++) pixels[90 * 100 + x] = 192;
        for (int y = top; y <= 90; y++) pixels[y * 100 + 10] = 192;
        return pixels;
    }
}
