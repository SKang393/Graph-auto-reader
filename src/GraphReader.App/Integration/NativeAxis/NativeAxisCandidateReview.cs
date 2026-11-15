// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.CalibrationOcr;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.Axis;
using OpenCvSharp;

namespace GraphReader.App.Integration.NativeAxis;

internal static class NativeAxisCandidateReview
{
    internal const int MaximumResponseBytes = 64 * 1024;
    internal const int MaximumCandidates = 128;

    private sealed class Observer : IAxisGeometryCandidateObserver
    {
        internal IReadOnlyList<AxisGeometryCandidateSnapshot>? Snapshots { get; private set; }
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) =>
            Snapshots = Array.AsReadOnly(candidates.ToArray());
    }

    internal static async Task<int> RunAsync(WorkflowRuntimeEnvironment environment, Stream input, Stream output)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var watchdog = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(12), Timeout.InfiniteTimeSpan);
        AxisCandidateReviewResult result;
        try
        {
            if (environment != WorkflowRuntimeEnvironment.ManualPreview) return 2;
            using CpuBudgetJob job = CpuBudgetJob.Apply();
            Cv2.SetNumThreads(1);
            if (Cv2.GetNumThreads() != 1) throw new InvalidOperationException();
            byte[] prefix = new byte[4];
            await input.ReadExactlyAsync(prefix, deadline.Token).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (length <= 0 || length > NativeAxisWorker.MaximumImageBytes) throw new InvalidDataException();
            byte[] encoded = new byte[length];
            await input.ReadExactlyAsync(encoded, deadline.Token).ConfigureAwait(false);
            result = await ProcessAsync(encoded, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return 3; }
        catch (Exception) { result = Failure("OPENCV_AXIS_UNAVAILABLE"); }
        byte[] response = JsonSerializer.SerializeToUtf8Bytes(result);
        if (response.Length > MaximumResponseBytes) return 2;
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, response.Length);
        await output.WriteAsync(header, deadline.Token).ConfigureAwait(false);
        await output.WriteAsync(response, deadline.Token).ConfigureAwait(false);
        await output.FlushAsync(deadline.Token).ConfigureAwait(false);
        return 0;
    }

    internal static async Task<AxisCandidateReviewResult> ProcessAsync(byte[] encoded, CancellationToken token)
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
        return await ProcessLinesAsync(lines, width, height, token).ConfigureAwait(false);
    }

    internal static async Task<AxisCandidateReviewResult> ProcessLinesAsync(
        IReadOnlyList<GeometryLineCandidate> lines, int width, int height, CancellationToken token)
    {
        var observer = new Observer();
        try
        {
            _ = await new AxisGeometryDetector().DetectAsync(new AxisGeometryRequest(width, height, lines)
                { CandidateObserver = observer, MaximumExaminedPairs = MaximumCandidates }, token).ConfigureAwait(false);
        }
        catch (AxisGeometryDetectionException) { return Failure("OPENCV_AXIS_AMBIGUOUS"); }
        if (observer.Snapshots is not { Count: > 0 and <= MaximumCandidates } snapshots) return Failure("OPENCV_AXIS_AMBIGUOUS");
        var candidates = new List<AxisReviewCandidate>(snapshots.Count);
        for (int i = 0; i < snapshots.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            AxisGeometryCandidateSnapshot snapshot = snapshots[i];
            PixelPoint[] corners = [snapshot.Intersection, snapshot.BottomRight, snapshot.TopRight, snapshot.TopLeft];
            // Enumeration is complete. Unsupported rotation excludes only this review
            // option; malformed geometry still invalidates the complete response.
            string? rejection = NativeAxisWorker.GeometryRejection(corners, 1, width, height);
            if (rejection == "native_rotated_geometry") continue;
            if (rejection is not null) return Failure("OPENCV_AXIS_AMBIGUOUS");
            if (!TryGeometry(corners, width, height, out RustAxisGeometry? geometry)) return Failure("OPENCV_AXIS_AMBIGUOUS");
            candidates.Add(new AxisReviewCandidate(candidates.Count, geometry!));
        }
        return candidates.Count == 0 ? Failure("OPENCV_AXIS_AMBIGUOUS") : new(candidates.AsReadOnly(), null);
    }

    internal static bool TryGeometry(PixelPoint[] corners, int width, int height, out RustAxisGeometry? geometry)
    {
        geometry = null;
        if (corners.Length != 4 || NativeAxisWorker.GeometryRejection(corners, 1, width, height) is not null) return false;
        int left = (int)Math.Round(corners[0].X), bottom = (int)Math.Round(corners[0].Y);
        int right = (int)Math.Round(corners[1].X), top = (int)Math.Round(corners[3].Y);
        if (left < 0 || top < 0 || right >= width || bottom >= height || left >= right || top >= bottom) return false;
        geometry = new RustAxisGeometry(left, bottom, right, top, 0);
        return true;
    }

    private static AxisCandidateReviewResult Failure(string code) => new(Array.Empty<AxisReviewCandidate>(), code);
}
