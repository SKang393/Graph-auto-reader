// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace GraphReader.Axis;

public sealed record OpenCvLineCandidateOptions
{
    public bool UseLineSegmentDetector { get; init; } = true;

    public bool UseProbabilisticHough { get; init; } = true;

    public double CannyLowThreshold { get; init; } = 40d;

    public double CannyHighThreshold { get; init; } = 120d;

    public int HoughThreshold { get; init; } = 24;

    public double HoughMinimumLineLengthPixels { get; init; } = 12d;

    public double HoughMaximumLineGapPixels { get; init; } = 4d;

    public LineSegmentDetectorModes LsdRefinement { get; init; } = LineSegmentDetectorModes.RefineStd;
}

/// <summary>
/// Extracts detector-neutral line candidates from an original-pixel grayscale
/// frame using OpenCV LSD and probabilistic Hough. It does not classify or emit
/// plotted markers.
/// </summary>
public sealed class OpenCvLineCandidateProvider : ILineCandidateProvider
{
    private readonly OpenCvLineCandidateOptions _options;

    public OpenCvLineCandidateProvider(OpenCvLineCandidateOptions? options = null)
    {
        _options = options ?? new OpenCvLineCandidateOptions();
        ValidateOptions(_options);
    }

    public async ValueTask<IReadOnlyList<GeometryLineCandidate>> DetectLinesAsync(
        GrayscaleLineCandidateFrame frame,
        CancellationToken cancellationToken)
    {
        ValidateFrame(frame);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<GeometryLineCandidate> candidates = await Task.Run(
            () => Detect(frame, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return candidates;
    }

    private ReadOnlyCollection<GeometryLineCandidate> Detect(
        GrayscaleLineCandidateFrame frame,
        CancellationToken cancellationToken)
    {
        byte[] pixels = frame.Pixels.ToArray();
        using var gray = new Mat(frame.Height, frame.Width, MatType.CV_8UC1);
        long destinationStride = checked((long)gray.Step());
        for (int row = 0; row < frame.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.Copy(
                pixels,
                checked(row * frame.Stride),
                IntPtr.Add(gray.Data, checked((int)(row * destinationStride))),
                frame.Width);
        }

        var candidates = new List<GeometryLineCandidate>();

        if (_options.UseLineSegmentDetector)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using LineSegmentDetector detector = LineSegmentDetector.Create(_options.LsdRefinement);
            detector.Detect(
                gray,
                out Vec4f[] lines,
                out double[] widths,
                out _,
                out _);
            cancellationToken.ThrowIfCancellationRequested();
            for (int index = 0; index < lines.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Vec4f line = lines[index];
                double width = index < widths.Length && double.IsFinite(widths[index]) && widths[index] > 0
                    ? widths[index]
                    : 1d;
                candidates.Add(new GeometryLineCandidate(
                    $"opencv-lsd-{index:D6}",
                    new GeometryLineSegment(
                        new PixelPoint(line.Item0, line.Item1),
                        new PixelPoint(line.Item2, line.Item3)),
                    LineCandidateSource.OpenCvLsd,
                    Strength: 1d,
                    StrokeWidthPixels: width));
            }
        }

        if (_options.UseProbabilisticHough)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var edges = new Mat();
            Cv2.Canny(
                gray,
                edges,
                _options.CannyLowThreshold,
                _options.CannyHighThreshold,
                apertureSize: 3,
                L2gradient: true);
            LineSegmentPoint[] lines = Cv2.HoughLinesP(
                edges,
                rho: 1d,
                theta: Math.PI / 180d,
                threshold: _options.HoughThreshold,
                minLineLength: _options.HoughMinimumLineLengthPixels,
                maxLineGap: _options.HoughMaximumLineGapPixels);
            cancellationToken.ThrowIfCancellationRequested();
            for (int index = 0; index < lines.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LineSegmentPoint line = lines[index];
                candidates.Add(new GeometryLineCandidate(
                    $"opencv-hough-{index:D6}",
                    new GeometryLineSegment(
                        new PixelPoint(line.P1.X, line.P1.Y),
                        new PixelPoint(line.P2.X, line.P2.Y)),
                    LineCandidateSource.OpenCvHough));
            }
        }

        AddConnectedInkBridges(candidates, frame, pixels, cancellationToken);
        return candidates.AsReadOnly();
    }

    private void AddConnectedInkBridges(
        List<GeometryLineCandidate> candidates,
        GrayscaleLineCandidateFrame frame,
        byte[] pixels,
        CancellationToken cancellationToken)
    {
        // Native detectors follow stroke edges. An open symbol can interrupt
        // both edges even though its outline still connects the actual ink.
        // Supply that local connection only between existing collinear lines.
        var geometryOptions = new AxisGeometryOptions();
        double minimumGap = geometryOptions.MergeDistancePixels * 2d;
        // Hough's empty-gap allowance is not the size of a connected symbol.
        // Search at most two minimum-length segments, then require a real ink
        // path. Short LSD terminal stubs still carry valid endpoint evidence.
        double maximumGap = _options.HoughMinimumLineLengthPixels * 2d;
        if (maximumGap <= minimumGap)
        {
            return;
        }

        double axisCosine = Math.Cos(geometryOptions.MaximumAxisDeviationDegrees * Math.PI / 180d);
        double alignmentCosine = Math.Cos(geometryOptions.MergeAngleToleranceDegrees * Math.PI / 180d);
        int cellSize = (int)Math.Ceiling(maximumGap);
        var buckets = new Dictionary<(int X, int Y), List<InkEndpoint>>();
        var bridges = new List<GeometryLineCandidate>();
        var usedPairs = new HashSet<(string, string)>();
        foreach (GeometryLineCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double length = candidate.Segment.Length;
            if (length < minimumGap)
            {
                continue;
            }

            var delta = new PixelPoint(
                (candidate.Segment.End.X - candidate.Segment.Start.X) / length,
                (candidate.Segment.End.Y - candidate.Segment.Start.Y) / length);
            if (Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)) < axisCosine)
            {
                continue;
            }

            AddEndpoint(new InkEndpoint(candidate.Segment.Start, new PixelPoint(-delta.X, -delta.Y), candidate));
            AddEndpoint(new InkEndpoint(candidate.Segment.End, delta, candidate));
        }

        candidates.AddRange(bridges);

        void AddEndpoint(InkEndpoint endpoint)
        {
            int cellX = (int)Math.Floor(endpoint.Point.X / cellSize);
            int cellY = (int)Math.Floor(endpoint.Point.Y / cellSize);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (!buckets.TryGetValue((cellX + dx, cellY + dy), out List<InkEndpoint>? neighbors))
                    {
                        continue;
                    }

                    foreach (InkEndpoint neighbor in neighbors)
                    {
                        if (neighbor.Candidate.CandidateId == endpoint.Candidate.CandidateId)
                        {
                            continue;
                        }

                        double gapX = endpoint.Point.X - neighbor.Point.X;
                        double gapY = endpoint.Point.Y - neighbor.Point.Y;
                        double distance = Math.Sqrt((gapX * gapX) + (gapY * gapY));
                        if (distance <= minimumGap || distance > maximumGap ||
                            ((gapX * neighbor.Outward.X) + (gapY * neighbor.Outward.Y)) / distance < alignmentCosine ||
                            -((gapX * endpoint.Outward.X) + (gapY * endpoint.Outward.Y)) / distance < alignmentCosine ||
                            !HasLocalInkPath(neighbor.Point, endpoint.Point, frame, pixels, cellSize, cancellationToken) ||
                            !usedPairs.Add((neighbor.Candidate.CandidateId, endpoint.Candidate.CandidateId)))
                        {
                            continue;
                        }

                        bridges.Add(new GeometryLineCandidate(
                            $"raster-ink-bridge-{bridges.Count:D6}",
                            new GeometryLineSegment(neighbor.Point, endpoint.Point),
                            LineCandidateSource.Other,
                            Strength: Math.Min(neighbor.Candidate.Strength, endpoint.Candidate.Strength),
                            StrokeWidthPixels: Math.Min(neighbor.Candidate.StrokeWidthPixels, endpoint.Candidate.StrokeWidthPixels)));
                    }
                }
            }

            if (!buckets.TryGetValue((cellX, cellY), out List<InkEndpoint>? bucket))
            {
                bucket = [];
                buckets.Add((cellX, cellY), bucket);
            }

            bucket.Add(endpoint);
        }
    }

    private static bool HasLocalInkPath(
        PixelPoint from,
        PixelPoint to,
        GrayscaleLineCandidateFrame frame,
        byte[] pixels,
        int detourRadius,
        CancellationToken cancellationToken)
    {
        const int edgeRadius = 2;
        const byte maximumInkGray = 200;
        int fromX = (int)Math.Round(from.X), fromY = (int)Math.Round(from.Y);
        int toX = (int)Math.Round(to.X), toY = (int)Math.Round(to.Y);
        int left = Math.Max(0, Math.Min(fromX, toX) - detourRadius);
        int right = Math.Min(frame.Width - 1, Math.Max(fromX, toX) + detourRadius);
        int top = Math.Max(0, Math.Min(fromY, toY) - detourRadius);
        int bottom = Math.Min(frame.Height - 1, Math.Max(fromY, toY) + detourRadius);
        var pending = new Queue<(int X, int Y)>();
        var visited = new HashSet<(int X, int Y)>();
        for (int y = Math.Max(top, fromY - edgeRadius); y <= Math.Min(bottom, fromY + edgeRadius); y++)
        {
            for (int x = Math.Max(left, fromX - edgeRadius); x <= Math.Min(right, fromX + edgeRadius); x++)
            {
                if (pixels[(y * frame.Stride) + x] < maximumInkGray && visited.Add((x, y)))
                {
                    pending.Enqueue((x, y));
                }
            }
        }

        while (pending.TryDequeue(out var point))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Math.Abs(point.X - toX) <= edgeRadius && Math.Abs(point.Y - toY) <= edgeRadius)
            {
                return true;
            }

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = point.X + dx, y = point.Y + dy;
                    if (x >= left && x <= right && y >= top && y <= bottom &&
                        pixels[(y * frame.Stride) + x] < maximumInkGray && visited.Add((x, y)))
                    {
                        pending.Enqueue((x, y));
                    }
                }
            }
        }

        return false;
    }

    private sealed record InkEndpoint(PixelPoint Point, PixelPoint Outward, GeometryLineCandidate Candidate);

    private static void ValidateFrame(GrayscaleLineCandidateFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!string.Equals(
                frame.CoordinateSpace,
                AxisGeometryCoordinateSpaces.OriginalPixels,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "OpenCV line detection accepts only original-pixel frames.",
                nameof(frame));
        }

        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride < frame.Width)
        {
            throw new ArgumentException("The grayscale frame dimensions and stride are invalid.", nameof(frame));
        }

        long requiredBytes = checked((long)frame.Stride * frame.Height);
        if (requiredBytes > frame.Pixels.Length)
        {
            throw new ArgumentException("The grayscale frame buffer is shorter than its declared stride and height.", nameof(frame));
        }
    }

    private static void ValidateOptions(OpenCvLineCandidateOptions options)
    {
        if (!options.UseLineSegmentDetector && !options.UseProbabilisticHough)
        {
            throw new ArgumentException("At least one OpenCV line detector must be enabled.", nameof(options));
        }

        if (!double.IsFinite(options.CannyLowThreshold) || options.CannyLowThreshold < 0 ||
            !double.IsFinite(options.CannyHighThreshold) ||
            options.CannyHighThreshold <= options.CannyLowThreshold ||
            options.HoughThreshold <= 0 ||
            !double.IsFinite(options.HoughMinimumLineLengthPixels) ||
            options.HoughMinimumLineLengthPixels <= 0 ||
            !double.IsFinite(options.HoughMaximumLineGapPixels) ||
            options.HoughMaximumLineGapPixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "OpenCV line detector parameters are invalid.");
        }
    }
}
