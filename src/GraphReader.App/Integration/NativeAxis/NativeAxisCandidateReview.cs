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

internal sealed record NativeAxisCandidateReviewObservation(
    string Stage, bool EnumerationCompleted, bool Cancelled,
    int RawSnapshotCount, int CheckedCount, int EligibleCount, int RotatedCount, int RoundedExclusionCount, int DuplicateExclusionCount,
    string? FailureDetail, string? HardGeometryRejection, AxisGeometryWorkSnapshot? Work)
{
    public int IncompleteSnapshotCount { get; init; }
    public int RetainedAffineCount { get; init; }
}

internal static class NativeAxisCandidateReview
{
    internal const int MaximumResponseBytes = 64 * 1024;
    internal const int MaximumCandidates = 128;

    private sealed class Observer(Action<NativeAxisCandidateReviewObservation>? callback)
        : IAxisGeometryCandidateObserver, IAxisGeometryWorkObserver, IAxisGeometryIncompleteReviewObserver
    {
        internal System.Collections.ObjectModel.ReadOnlyCollection<AxisGeometryCandidateSnapshot>? Snapshots { get; private set; }
        internal AxisGeometryIncompleteReviewSnapshot? Interrupted { get; private set; }
        internal bool Enabled => callback is not null;
        internal string Stage { get; set; } = "enumeration";
        internal bool Cancelled { get; set; }
        internal int CheckedCount { get; set; }
        internal int EligibleCount { get; set; }
        internal int RotatedCount { get; set; }
        internal int RetainedAffineCount { get; set; }
        internal int RoundedExclusionCount { get; set; }
        internal int DuplicateExclusionCount { get; set; }
        internal string? FailureDetail { get; set; }
        internal string? HardGeometryRejection { get; set; }
        private AxisGeometryWorkSnapshot? _work;
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) =>
            Snapshots = Array.AsReadOnly(candidates.ToArray());
        public void OnWorkObserved(AxisGeometryWorkSnapshot snapshot) => _work = snapshot;
        public void OnSearchInterrupted(AxisGeometryIncompleteReviewSnapshot snapshot) => Interrupted = snapshot;
        internal void Notify()
        {
            if (callback is null) return;
            try
            {
                callback(new(Stage, _work?.EnumerationCompleted ?? (Snapshots is not null), Cancelled,
                    Snapshots?.Count ?? 0, CheckedCount, EligibleCount, RotatedCount, RoundedExclusionCount, DuplicateExclusionCount,
                    FailureDetail, HardGeometryRejection, _work)
                    { IncompleteSnapshotCount = Interrupted?.Candidates.Count ?? 0, RetainedAffineCount = RetainedAffineCount });
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                // Optional diagnostics preserve the result or original failure.
            }
        }
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

    internal static Task<AxisCandidateReviewResult> ProcessAsync(byte[] encoded, CancellationToken token) =>
        ProcessAsync(encoded, null, token);

    internal static async Task<AxisCandidateReviewResult> ProcessAsync(byte[] encoded,
        Action<NativeAxisCandidateReviewObservation>? observation, CancellationToken token)
    {
        var observer = new Observer(observation) { Stage = "decode" };
        try
        {
            using var stream = new MemoryStream(encoded, writable: false);
            BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216)
            {
                observer.FailureDetail = "INVALID_INPUT";
                return Failure("INVALID_INPUT");
            }
            var gray = new FormatConvertedBitmap(WhiteBackgroundRaster.Normalize(frame, token), PixelFormats.Gray8, null, 0);
            byte[] pixels = new byte[checked(width * height)];
            gray.CopyPixels(pixels, width, 0);
            observer.Stage = "line_detection";
            var lines = await new OpenCvLineCandidateProvider().DetectLinesAsync(new(width, height, width, pixels), token).ConfigureAwait(false);
            return await ProcessLinesCoreAsync(lines, width, height, observer, pixels, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { observer.Cancelled = true; observer.FailureDetail = "cancelled"; throw; }
        catch (Exception) { observer.FailureDetail ??= "native_" + observer.Stage + "_failed"; throw; }
        finally { observer.Notify(); }
    }

    internal static Task<AxisCandidateReviewResult> ProcessLinesAsync(
        IReadOnlyList<GeometryLineCandidate> lines, int width, int height, CancellationToken token) =>
        ProcessLinesAsync(lines, width, height, null, token);

    internal static async Task<AxisCandidateReviewResult> ProcessLinesAsync(
        IReadOnlyList<GeometryLineCandidate> lines, int width, int height,
        Action<NativeAxisCandidateReviewObservation>? observation, CancellationToken token)
    {
        var observer = new Observer(observation);
        try { return await ProcessLinesCoreAsync(lines, width, height, observer, null, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { observer.Cancelled = true; observer.FailureDetail = "cancelled"; throw; }
        catch (Exception) { observer.FailureDetail ??= "native_" + observer.Stage + "_failed"; throw; }
        finally { observer.Notify(); }
    }

    private static async Task<AxisCandidateReviewResult> ProcessLinesCoreAsync(
        IReadOnlyList<GeometryLineCandidate> lines, int width, int height, Observer observer, byte[]? pixels, CancellationToken token)
    {
        observer.Stage = "enumeration";
        bool enumerationCompleted = true;
        try
        {
            _ = await new AxisGeometryDetector().DetectAsync(new AxisGeometryRequest(width, height, lines)
                { CandidateObserver = observer, WorkObserver = observer.Enabled ? observer : null,
                    IncompleteReviewObserver = observer,
                    MaximumExaminedPairs = MaximumCandidates }, token).ConfigureAwait(false);
        }
        catch (AxisGeometryDetectionException error)
        {
            observer.FailureDetail = NativeAxisWorker.FailureDetail(error);
            token.ThrowIfCancellationRequested();
            if (error.Reason != AxisGeometryFailureReason.PairBudgetExceeded || observer.Interrupted is not { EnumerationCompleted: false } interrupted ||
                interrupted.FailureReason != error.Reason || interrupted.BudgetKind != error.BudgetKind)
                return Failure("OPENCV_AXIS_AMBIGUOUS");
            enumerationCompleted = false;
        }
        observer.Stage = "snapshot_validation";
        PlotPolygon[]? polygons = enumerationCompleted
            ? observer.Snapshots?.Select(snapshot => new PlotPolygon(snapshot.Intersection, snapshot.BottomRight, snapshot.TopRight, snapshot.TopLeft)).ToArray()
            : observer.Interrupted?.Candidates.Select(candidate => candidate.PlotPolygon).ToArray();
        if (polygons is not { Length: > 0 and <= MaximumCandidates })
        {
            observer.FailureDetail = polygons is { Length: > MaximumCandidates }
                ? "native_snapshot_limit_exceeded" : "native_no_ranked_candidates";
            return Failure("OPENCV_AXIS_AMBIGUOUS");
        }
        observer.Stage = "geometry_eligibility";
        var candidates = new List<AxisReviewCandidate>(polygons.Length);
        var distinct = new HashSet<(int Left, int Bottom, int Right, int Top)>();
        var distinctAffine = new HashSet<RustAffineAxisGeometry>();
        for (int i = 0; i < polygons.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            PlotPolygon polygon = polygons[i];
            PixelPoint[] corners = [polygon.BottomLeft, polygon.BottomRight, polygon.TopRight, polygon.TopLeft];
            if (observer.Enabled) observer.CheckedCount++;
            // Preserve valid tilted frames; malformed geometry invalidates the response.
            // Unsupported integer representation excludes only that rectangle.
            string? rejection = NativeAxisWorker.GeometryRejection(corners, 1, width, height);
            if (rejection == "native_rotated_geometry")
            {
                if (observer.Enabled) observer.RotatedCount++;
                var affine = new RustAffineAxisGeometry(new(corners[0].X, corners[0].Y),
                    new(corners[1].X, corners[1].Y), new(corners[3].X, corners[3].Y), 0);
                if (!OpenCvAxisProposalService.ValidAffineGeometry(affine, width, height) ||
                    corners[2].X != corners[1].X + corners[3].X - corners[0].X ||
                    corners[2].Y != corners[1].Y + corners[3].Y - corners[0].Y)
                {
                    observer.HardGeometryRejection = "native_invalid_affine_geometry";
                    observer.FailureDetail = observer.HardGeometryRejection;
                    return Failure("OPENCV_AXIS_AMBIGUOUS");
                }
                if (!distinctAffine.Add(affine))
                {
                    if (observer.Enabled) observer.DuplicateExclusionCount++;
                    continue;
                }
                candidates.Add(new AxisReviewCandidate(candidates.Count, null) { AffineGeometry = affine });
                if (observer.Enabled) { observer.EligibleCount++; observer.RetainedAffineCount++; }
                continue;
            }
            if (rejection is not null)
            {
                observer.HardGeometryRejection = corners.Any(c => !double.IsFinite(c.X) || !double.IsFinite(c.Y))
                    ? "native_nonfinite_geometry" : rejection;
                observer.FailureDetail = observer.HardGeometryRejection;
                return Failure("OPENCV_AXIS_AMBIGUOUS");
            }
            if (!TryGeometry(corners, width, height, out RustAxisGeometry? geometry))
            {
                if (observer.Enabled) observer.RoundedExclusionCount++;
                continue;
            }
            if (pixels is not null) geometry = NativeAxisRasterGeometry.Adjust(geometry!, pixels, width, height, token);
            if (!distinct.Add((geometry!.Left, geometry.Bottom, geometry.Right, geometry.Top)))
            {
                if (observer.Enabled) observer.DuplicateExclusionCount++;
                continue;
            }
            candidates.Add(new AxisReviewCandidate(candidates.Count, geometry!));
            if (observer.Enabled) observer.EligibleCount++;
        }
        if (candidates.Count == 0)
        {
            observer.FailureDetail = "native_no_eligible_candidates";
            return Failure("OPENCV_AXIS_AMBIGUOUS");
        }
        observer.Stage = enumerationCompleted ? "complete" : "incomplete_review";
        token.ThrowIfCancellationRequested();
        return new(candidates.AsReadOnly(), null) { EnumerationCompleted = enumerationCompleted };
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
