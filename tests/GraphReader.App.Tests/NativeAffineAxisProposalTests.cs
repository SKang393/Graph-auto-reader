// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Text.Json;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.NativeAxis;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class NativeAffineAxisProposalTests
{
    [TestMethod]
    public async Task CompletedRotatedGeometryRetainsFittedObservedAnchors()
    {
        GeometryLineCandidate[] lines = [
            new("x", new(new(30, 160), new(180, 150)), LineCandidateSource.RecordedFixture),
            new("y", new(new(30, 160), new(40, 30)), LineCandidateSource.RecordedFixture)];
        AxisGeometryResult expected = await new AxisGeometryDetector().DetectAsync(new(240, 200, lines)
            { MaximumExaminedPairs = NativeAxisWorker.MaximumExaminedPairs }, CancellationToken.None);
        RustAxisProposalResult actual = await NativeAxisWorker.ProcessLinesAsync(lines, 240, 200, null, CancellationToken.None);
        Assert.IsNull(actual.ErrorCode);
        Assert.IsNull(actual.Geometry);
        Assert.IsNotNull(actual.AffineGeometry);
        Assert.AreEqual("opencv-review", actual.Provider);
        CollectionAssert.Contains(actual.ReviewReasons.ToArray(), "native_affine_geometry");
        Assert.AreEqual(expected.PlotPolygon.BottomLeft.X, actual.AffineGeometry.Origin.X);
        Assert.AreEqual(expected.PlotPolygon.BottomLeft.Y, actual.AffineGeometry.Origin.Y);
        Assert.AreEqual(expected.PlotPolygon.BottomRight.X, actual.AffineGeometry.XEnd.X);
        Assert.AreEqual(expected.PlotPolygon.BottomRight.Y, actual.AffineGeometry.XEnd.Y);
        Assert.AreEqual(expected.PlotPolygon.TopLeft.X, actual.AffineGeometry.YEnd.X);
        Assert.AreEqual(expected.PlotPolygon.TopLeft.Y, actual.AffineGeometry.YEnd.Y);
        Assert.AreEqual(expected.Confidence, actual.AffineGeometry.Confidence);
        Assert.IsTrue(OpenCvAxisProposalService.Valid(actual, 240, 200));
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    public void TiltedRasterReturnsFittedOriginalPixelAxes(int direction) => StaTestHost.Run(() =>
    {
        const int width = 320, height = 240, left = 40, bottom = 190, right = 270, top = 40;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        int AxisY(int x) => bottom + (int)Math.Round(direction * (x - left) * .035);
        int AxisX(int y) => left + (int)Math.Round(direction * (bottom - y) * .035);
        for (int x = left; x <= right; x++)
            for (int thickness = -1; thickness <= 1; thickness++) pixels[(AxisY(x) + thickness) * width + x] = 0;
        for (int y = top; y <= bottom; y++)
            for (int thickness = -1; thickness <= 1; thickness++) pixels[y * width + AxisX(y) + thickness] = 0;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width)));
        using var stream = new MemoryStream(); encoder.Save(stream);
        byte[] image = stream.ToArray(), original = (byte[])image.Clone();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RustAxisProposalResult result = NativeAxisWorker.ProcessAsync(image, deadline.Token).GetAwaiter().GetResult();
        Assert.IsNull(result.ErrorCode);
        Assert.IsNull(result.Geometry);
        Assert.IsNotNull(result.AffineGeometry);
        Assert.IsTrue(OpenCvAxisProposalService.Valid(result, width, height));
        CollectionAssert.AreEqual(original, image);
        var affine = result.AffineGeometry;
        // The source is a three-pixel stroke; the existing family merge tolerance
        // is three pixels, so allow four pixels for raster edge fitting.
        const double rasterTolerance = 4;
        Assert.IsTrue(Math.Abs(affine.Origin.X - left) <= rasterTolerance && Math.Abs(affine.Origin.Y - bottom) <= rasterTolerance);
        Assert.IsTrue(Math.Abs(affine.XEnd.X - right) <= rasterTolerance && Math.Abs(affine.XEnd.Y - AxisY(right)) <= rasterTolerance);
        Assert.IsTrue(Math.Abs(affine.YEnd.X - AxisX(top)) <= rasterTolerance && Math.Abs(affine.YEnd.Y - top) <= rasterTolerance);
        Assert.IsTrue(affine.XEnd.X > affine.Origin.X && affine.YEnd.Y < affine.Origin.Y);
    });

    [TestMethod]
    public void TransportRejectsContradictionsAndInvalidAffineGeometry()
    {
        var frame = new RustAffineAxisGeometry(new(10, 90), new(80, 85), new(15, 10), .7);
        var success = new RustAxisProposalResult(null, null) { Provider = "opencv-review", AffineGeometry = frame, ReviewReasons = ["native_affine_geometry"] };
        Assert.IsTrue(OpenCvAxisProposalService.Valid(success, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.Valid(success with { ErrorCode = "OPENCV_AXIS_AMBIGUOUS" }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.Valid(success with { Geometry = new(10, 90, 80, 10, .7) }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.Valid(success with { Provider = "rust" }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.Valid(success with { ReviewReasons = [] }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.ValidAffineGeometry(frame with { Origin = new(double.NaN, 90) }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.ValidAffineGeometry(frame with { XEnd = new(100, 85) }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.ValidAffineGeometry(frame with { YEnd = new(30, 10) }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.ValidAffineGeometry(frame with { XEnd = new(5, 85) }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.ValidAffineGeometry(frame with { YEnd = new(80, 85) }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.ValidAffineGeometry(frame with { Confidence = double.NaN }, 100, 100));
    }

    [TestMethod]
    public void AlignedJsonOmitsNewFieldAndAffineJsonRoundTrips()
    {
        var aligned = new RustAxisProposalResult(new(10, 90, 80, 10, .8), null) { Provider = "opencv-review" };
        string legacy = JsonSerializer.Serialize(aligned);
        Assert.IsFalse(legacy.Contains("AffineGeometry", StringComparison.Ordinal));
        var affine = new RustAxisProposalResult(null, null) { Provider = "opencv-review", ReviewReasons = ["native_affine_geometry"],
            AffineGeometry = new(new(10, 90), new(80, 85), new(15, 10), .7) };
        string json = JsonSerializer.Serialize(affine);
        Assert.IsFalse(json.Contains("IsFinite", StringComparison.Ordinal));
        var restored = JsonSerializer.Deserialize<RustAxisProposalResult>(json);
        Assert.IsNotNull(restored);
        Assert.AreEqual(affine.AffineGeometry, restored.AffineGeometry);
        Assert.IsTrue(OpenCvAxisProposalService.Valid(restored, 100, 100));
    }

    [TestMethod]
    public async Task RustAmbiguityRetainsNativeAffineAsReviewSuccess()
    {
        var frame = new RustAffineAxisGeometry(new(10, 90), new(80, 85), new(15, 10), .7);
        var primary = new Stub(new(null, "AMBIGUOUS_AXIS"));
        var fallback = new Stub(new(null, null) { Provider = "opencv-review", AffineGeometry = frame, ReviewReasons = ["native_affine_geometry"] });
        var result = await new RustFirstAxisProposalService(primary, fallback).SuggestAsync([], CancellationToken.None);
        Assert.AreEqual(frame, result.AffineGeometry);
        Assert.IsNull(result.Geometry);
        Assert.IsNull(result.ErrorCode);
        CollectionAssert.Contains(result.ReviewReasons.ToArray(), "rust_axis_ambiguous");
        Assert.AreEqual(1, fallback.Calls);
    }
    private sealed class Stub(RustAxisProposalResult result) : IAxisProposalService
    {
        internal int Calls { get; private set; }
        public Task<RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token) { Calls++; return Task.FromResult(result); }
    }
}
