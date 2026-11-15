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
    public async Task CompletedMultipleCandidatesReturnTopRankedButBudgetExhaustionReturnsNone()
    {
        var lines = new List<GeometryLineCandidate>
        {
            new("baseline", new(new(100, 700), new(3000, 700)), LineCandidateSource.RecordedFixture),
        };
        for (int index = 0; index < NativeAxisWorker.MaximumExaminedPairs + 1; index++)
            lines.Add(new($"vertical-{index}", new(new(100 + 10 * index, 700), new(100 + 10 * index, 100)), LineCandidateSource.RecordedFixture));
        var within = lines.Take(NativeAxisWorker.MaximumExaminedPairs + 1).ToArray();
        AxisGeometryResult best = await new AxisGeometryDetector().DetectAsync(
            new AxisGeometryRequest(3200, 800, within) { MaximumExaminedPairs = NativeAxisWorker.MaximumExaminedPairs }, CancellationToken.None);
        RustAxisProposalResult result = await NativeAxisWorker.ProcessLinesAsync(within, 3200, 800, null, CancellationToken.None);
        Assert.IsNull(result.ErrorCode);
        Assert.IsNotNull(result.Geometry);
        Assert.AreEqual((int)Math.Round(best.PlotPolygon.BottomLeft.X), result.Geometry.Left);
        Assert.AreEqual((int)Math.Round(best.PlotPolygon.BottomLeft.Y), result.Geometry.Bottom);
        Assert.AreEqual((int)Math.Round(best.PlotPolygon.BottomRight.X), result.Geometry.Right);
        Assert.AreEqual((int)Math.Round(best.PlotPolygon.TopLeft.Y), result.Geometry.Top);
        CollectionAssert.Contains(result.ReviewReasons.ToArray(), "native_multiple_ranked_candidates");
        Assert.IsEmpty(result.Geometry.XTickPositions);
        Assert.IsEmpty(result.Geometry.YTickPositions);
        RustAxisProposalResult exceeded = await NativeAxisWorker.ProcessLinesAsync(lines, 3200, 800, null, CancellationToken.None);
        Assert.IsNull(exceeded.Geometry);
        CollectionAssert.Contains(exceeded.ReviewReasons.ToArray(), "native_pair_budget_exceeded");
    }

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
        CollectionAssert.AreEqual(new[] { horizontalOnly ? "native_missing_orientation" : "native_no_line_evidence" },
            result.ReviewReasons.ToArray());
    });

    [TestMethod]
    public void CandidateCountBoundsAndRotationHaveDistinctReasonsInRejectionOrder()
    {
        PixelPoint[] corners = [new(10, 90), new(90, 90), new(90, 10), new(10, 10)];
        Assert.IsNull(NativeAxisWorker.GeometryRejection(corners, 1, 100, 100));
        Assert.IsNull(NativeAxisWorker.GeometryRejection(corners, 2, 100, 100));
        Assert.AreEqual("native_no_ranked_candidates", NativeAxisWorker.GeometryRejection(corners, 0, 100, 100));
        PixelPoint[] outside = [.. corners]; outside[2] = new(100, 10);
        Assert.AreEqual("native_invalid_corner_bounds", NativeAxisWorker.GeometryRejection(outside, 1, 100, 100));
        Assert.AreEqual("native_invalid_corner_bounds", NativeAxisWorker.GeometryRejection(outside, 2, 100, 100));
        PixelPoint[] rotated = [.. corners]; rotated[1] = new(90, 88);
        Assert.AreEqual("native_rotated_geometry", NativeAxisWorker.GeometryRejection(rotated, 1, 100, 100));
        Assert.AreEqual("native_rotated_geometry", NativeAxisWorker.GeometryRejection(rotated, 2, 100, 100));
    }

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
