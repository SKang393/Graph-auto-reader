// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class AxisTickMeasurementServiceManualPartialTests
{
    private static readonly int[] XTicks = [40, 70, 100];
    private static readonly int[] YTicks = [20, 50, 80];

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ActualWorkerRetainsOnlyObservedAxisThroughPngService(bool horizontalObserved)
    {
        string? worker = Environment.GetEnvironmentVariable("GRAPHREADER_RUST_WORKER");
        if (string.IsNullOrWhiteSpace(worker)) { Assert.Inconclusive("Set GRAPHREADER_RUST_WORKER to the built Rust worker."); return; }
        string workerPath = worker;
        StaTestHost.Run(() =>
        {
            const int side = 128;
            byte[] pixels = Enumerable.Repeat((byte)255, side * side).ToArray();
            for (int x = 20; x <= 115; x++) pixels[100 * side + x] = 0;
            for (int y = 10; y <= 100; y++) pixels[y * side + 20] = 0;
            foreach (int x in XTicks) for (int y = 101; y < 105; y++) pixels[y * side + x] = 0;
            foreach (int y in YTicks) for (int x = 16; x < 20; x++) pixels[y * side + x] = 0;
            if (horizontalObserved) for (int y = 40; y <= 80; y++) pixels[y * side + 20] = 255;
            else for (int x = 40; x <= 80; x++) pixels[100 * side + x] = 255;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(side, side, 96, 96, PixelFormats.Gray8, null, pixels, side)));
            using var stream = new MemoryStream(); encoder.Save(stream); byte[] image = stream.ToArray(); byte[] before = image.ToArray();
            var service = new RustAxisTickMeasurementService(workerPath);
            var geometry = new RustAxisGeometry(20, 100, 115, 10, 0);
            AxisTickMeasurementResult strict = service.MeasureAsync(image, geometry, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("AXIS_BOX_NOT_OBSERVED", strict.ErrorCode);
            Assert.IsNull(strict.Evidence);
            AxisTickMeasurementResult partial = service.MeasureManualPartialAsync(image, geometry, CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsNull(partial.ErrorCode);
            Assert.IsNotNull(partial.Evidence);
            CollectionAssert.AreEqual(horizontalObserved ? XTicks : Array.Empty<int>(), partial.Evidence.XTickPositions.ToArray());
            CollectionAssert.AreEqual(horizontalObserved ? Array.Empty<int>() : YTicks, partial.Evidence.YTickPositions.ToArray());
            Assert.AreEqual((20, 100, 115, 10), (partial.Evidence.Left, partial.Evidence.Bottom, partial.Evidence.Right, partial.Evidence.Top));
            CollectionAssert.AreEqual(before, image);
        });
    }
}
