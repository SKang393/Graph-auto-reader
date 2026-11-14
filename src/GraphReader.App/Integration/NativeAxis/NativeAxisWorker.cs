// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.CalibrationOcr;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using OpenCvSharp;

namespace GraphReader.App.Integration.NativeAxis;

internal static class NativeAxisWorker
{
    internal const int MaximumImageBytes = 32 * 1024 * 1024;
    internal const int MaximumResponseBytes = 4096;
    private sealed class Observer : IAxisGeometryCandidateObserver
    {
        internal int Count { get; private set; }
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) => Count = candidates.Count;
    }

    internal static async Task<int> RunAsync(WorkflowRuntimeEnvironment environment, Stream input, Stream output)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var watchdog = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(12), Timeout.InfiniteTimeSpan);
        RustAxisProposalResult result;
        try
        {
            if (environment != WorkflowRuntimeEnvironment.ManualPreview) return 2;
            using CpuBudgetJob job = CpuBudgetJob.Apply();
            Cv2.SetNumThreads(1);
            if (Cv2.GetNumThreads() != 1) throw new InvalidOperationException();
            byte[] header = new byte[4];
            await input.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > MaximumImageBytes) throw new InvalidDataException();
            byte[] encoded = new byte[length];
            await input.ReadExactlyAsync(encoded, deadline.Token).ConfigureAwait(false);
            result = await ProcessAsync(encoded, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return 3; }
        catch (Exception) { result = new(null, "OPENCV_AXIS_UNAVAILABLE") { Provider = "opencv-review" }; }
        byte[] response = JsonSerializer.SerializeToUtf8Bytes(result);
        if (response.Length > MaximumResponseBytes) return 2;
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, response.Length);
        await output.WriteAsync(prefix, deadline.Token).ConfigureAwait(false);
        await output.WriteAsync(response, deadline.Token).ConfigureAwait(false);
        await output.FlushAsync(deadline.Token).ConfigureAwait(false);
        return 0;
    }

    internal static async Task<RustAxisProposalResult> ProcessAsync(byte[] encoded, CancellationToken token)
    {
        using var stream = new MemoryStream(encoded, writable: false);
        BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;
        if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216)
            return Failure("INVALID_INPUT");
        var gray = new FormatConvertedBitmap(WhiteBackgroundRaster.Normalize(frame, token), PixelFormats.Gray8, null, 0);
        byte[] pixels = new byte[checked(width * height)];
        gray.CopyPixels(pixels, width, 0);
        var lines = await new OpenCvLineCandidateProvider().DetectLinesAsync(new(width, height, width, pixels), token).ConfigureAwait(false);
        var observer = new Observer();
        AxisGeometryResult geometry = await new AxisGeometryDetector().DetectAsync(
            new AxisGeometryRequest(width, height, lines) { CandidateObserver = observer, MaximumExaminedPairs = 128 }, token).ConfigureAwait(false);
        var p = geometry.PlotPolygon;
        PixelPoint[] corners = [p.BottomLeft, p.BottomRight, p.TopRight, p.TopLeft];
        if (observer.Count != 1 || corners.Any(c => !double.IsFinite(c.X) || !double.IsFinite(c.Y) || c.X < 0 || c.Y < 0 || c.X >= width || c.Y >= height) ||
            Math.Abs(p.BottomLeft.Y - p.BottomRight.Y) > 1 || Math.Abs(p.TopLeft.Y - p.TopRight.Y) > 1 ||
            Math.Abs(p.BottomLeft.X - p.TopLeft.X) > 1 || Math.Abs(p.BottomRight.X - p.TopRight.X) > 1)
            return Failure("OPENCV_AXIS_AMBIGUOUS");
        int left = (int)Math.Round(p.BottomLeft.X), right = (int)Math.Round(p.BottomRight.X);
        int bottom = (int)Math.Round(p.BottomLeft.Y), top = (int)Math.Round(p.TopLeft.Y);
        if (left < 0 || top < 0 || right >= width || bottom >= height || left >= right || top >= bottom || !double.IsFinite(geometry.Confidence))
            return Failure("OPENCV_AXIS_AMBIGUOUS");
        string[] reasons = geometry.Uncertainty.Reasons.ToArray();
        if (geometry.Uncertainty.NeedsReview && reasons.Length == 0) reasons = ["needs_review"];
        return new(new RustAxisGeometry(left, bottom, right, top, geometry.Confidence), null) { Provider = "opencv-review", ReviewReasons = reasons };
    }
    private static RustAxisProposalResult Failure(string reason) => new(null, reason) { Provider = "opencv-review" };
}
