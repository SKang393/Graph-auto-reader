// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("GraphReader.Imaging.Tests")]

namespace GraphReader.Imaging;

/// <summary>A derived Gray8 raster with reversible original-pixel coordinates.</summary>
public sealed record AffineRasterRectificationResult(
    int Width,
    int Height,
    int Stride,
    ReadOnlyMemory<byte> Pixels,
    TransformChain TransformChain,
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    /// <summary>Measured cooperative loop-wait time, separate from pixel processing.</summary>
    public double PacingMilliseconds { get; init; }
}

/// <summary>Rectifies a three-point affine plot frame without modifying its source raster.</summary>
public static class AffineRasterRectifier
{
    private const int MaximumDimension = 4096;
    private const long MaximumPixels = 16_777_216;
    private const double SingularTolerance = 1e-12;
    private static readonly TimeSpan ActiveSliceTarget = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// Maps the supplied origin, X endpoint, and Y endpoint onto an upright raster frame.
    /// Input and output pixels are Gray8. Coordinates address pixel centers. The synchronous
    /// pixel loop yields cooperatively; callers are responsible for running it off the UI thread.
    /// </summary>
    public static AffineRasterRectificationResult Rectify(
        ReadOnlyMemory<byte> pixels,
        int width,
        int height,
        int stride,
        PixelPoint origin,
        PixelPoint xEnd,
        PixelPoint yEnd,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRaster(pixels.Length, width, height, stride);
        ValidatePoint(origin, width, height, nameof(origin));
        ValidatePoint(xEnd, width, height, nameof(xEnd));
        ValidatePoint(yEnd, width, height, nameof(yEnd));
        if (xEnd.X <= origin.X || yEnd.Y >= origin.Y)
            throw new ArgumentException("The supplied axis endpoints must point right and up from the origin.");

        PixelPoint fourth = new(xEnd.X + yEnd.X - origin.X, xEnd.Y + yEnd.Y - origin.Y);
        ValidatePoint(fourth, width, height, "fourthCorner");

        double xVectorX = xEnd.X - origin.X;
        double xVectorY = xEnd.Y - origin.Y;
        double yVectorX = yEnd.X - origin.X;
        double yVectorY = yEnd.Y - origin.Y;
        double determinant = (xVectorX * yVectorY) - (yVectorX * xVectorY);
        if (!double.IsFinite(determinant) || determinant >= -SingularTolerance)
            throw new ArgumentException("The supplied affine axes are singular, reflected, or too close to singular.");

        int xLength = RoundedLength(xVectorX, xVectorY, nameof(xEnd));
        int yLength = RoundedLength(yVectorX, yVectorY, nameof(yEnd));

        // Original pixels are expressed in the affine basis. The X basis maps right;
        // the Y basis maps upward, matching raster coordinates where Y grows downward.
        double m11 = xLength * yVectorY / determinant;
        double m12 = -xLength * yVectorX / determinant;
        double m21 = yLength * xVectorY / determinant;
        double m22 = -yLength * xVectorX / determinant;
        double m13 = -(m11 * origin.X) - (m12 * origin.Y);
        double m23 = -(m21 * origin.X) - (m22 * origin.Y);
        var baseMatrix = new Matrix3x3(m11, m12, m13, m21, m22, m23, 0, 0, 1);
        if (!Finite(baseMatrix))
            throw new ArgumentException("The supplied affine axes produce a non-finite transform.");

        PixelPoint[] sourceCorners =
        [
            new(0, 0), new(width - 1, 0), new(0, height - 1), new(width - 1, height - 1)
        ];
        PixelPoint[] mappedCorners = sourceCorners.Select(baseMatrix.Transform).ToArray();
        if (mappedCorners.Any(static point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            throw new ArgumentException("The supplied affine axes map the source bounds to non-finite coordinates.");

        double minX = Math.Floor(mappedCorners.Min(static point => point.X));
        double maxX = Math.Ceiling(mappedCorners.Max(static point => point.X));
        double minY = Math.Floor(mappedCorners.Min(static point => point.Y));
        double maxY = Math.Ceiling(mappedCorners.Max(static point => point.Y));
        if (minX < int.MinValue || maxX > int.MaxValue || minY < int.MinValue || maxY > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(pixels), "The transformed source bounds exceed supported coordinates.");

        long outputWidthLong = checked((long)maxX - (long)minX + 1);
        long outputHeightLong = checked((long)maxY - (long)minY + 1);
        long outputPixels = checked(outputWidthLong * outputHeightLong);
        if (outputWidthLong is <= 0 or > MaximumDimension || outputHeightLong is <= 0 or > MaximumDimension ||
            outputPixels > MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(pixels), "Affine rectification exceeds the supported raster bounds.");

        int outputWidth = checked((int)outputWidthLong);
        int outputHeight = checked((int)outputHeightLong);
        int left = checked((int)-minX);
        int bottom = checked((int)-minY);
        int top = checked(bottom - yLength);
        int right = checked(left + xLength);
        var transformMatrix = baseMatrix with
        {
            M13 = baseMatrix.M13 - minX,
            M23 = baseMatrix.M23 - minY,
        };
        if (!Finite(transformMatrix))
            throw new ArgumentException("The output affine transform is non-finite.");

        ImageTransform transform = ImageTransform.Create(
            ImageTransformKind.Affine,
            ImageCoordinateSpace.OriginalPixels,
            ImageCoordinateSpace.DeskewedPixels,
            transformMatrix);
        var chain = new TransformChain([transform]);
        if (chain.DerivedToOriginal is null)
            throw new ArgumentException("The output affine transform is not invertible.");

        int outputStride = outputWidth;
        byte[] output = new byte[checked(outputStride * outputHeight)];
        ReadOnlySpan<byte> source = pixels.Span;
        var activeSlice = Stopwatch.StartNew();
        var pacing = new AffinePacingBalance();
        double pacingMilliseconds = 0;
        for (int y = 0; y < outputHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = y * outputStride;
            for (int x = 0; x < outputWidth; x++)
            {
                PixelPoint sourcePoint = chain.ToOriginal(new PixelPoint(x, y));
                output[row + x] = SampleWhiteBorder(source, width, height, stride, sourcePoint.X, sourcePoint.Y);
            }

            TimeSpan active = activeSlice.Elapsed;
            if (active >= ActiveSliceTarget)
            {
                activeSlice.Stop();
                TimeSpan requested = pacing.RecordActive(active);
                if (requested > TimeSpan.Zero)
                {
                    var wait = Stopwatch.StartNew();
                    try
                    {
                        Task.Delay(requested, cancellationToken).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        wait.Stop();
                        pacingMilliseconds += wait.Elapsed.TotalMilliseconds;
                        pacing.RecordWait(wait.Elapsed);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                activeSlice.Restart();
            }
        }

        return new(outputWidth, outputHeight, outputStride, output, chain, left, top, right, bottom)
        {
            PacingMilliseconds = pacingMilliseconds,
        };
    }

    private static void ValidateRaster(int pixelLength, int width, int height, int stride)
    {
        if (width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(width), "Raster dimensions must be within the supported bounds.");
        if ((long)width * height > MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "Raster exceeds the supported pixel count.");
        if (stride < width || (long)stride * height != pixelLength)
            throw new ArgumentException("Gray8 pixel data must contain exactly stride times height bytes.", nameof(pixelLength));
    }

    private static void ValidatePoint(PixelPoint point, int width, int height, string parameterName)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 ||
            point.X > width - 1 || point.Y > height - 1)
            throw new ArgumentOutOfRangeException(parameterName, "Axis points and their fourth corner must lie inside the source raster.");
    }

    private static int RoundedLength(double x, double y, string parameterName)
    {
        double length = Math.Sqrt((x * x) + (y * y));
        if (!double.IsFinite(length) || length < 0.5 || length > int.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "Axis length is outside supported bounds.");
        int rounded = checked((int)Math.Round(length, MidpointRounding.AwayFromZero));
        if (rounded <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Axis length must be positive.");
        return rounded;
    }

    private static bool Finite(Matrix3x3 matrix) =>
        matrix.ToValues().All(double.IsFinite);

    private static byte SampleWhiteBorder(ReadOnlySpan<byte> source, int width, int height, int stride, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x <= -1 || y <= -1 || x >= width || y >= height)
            return byte.MaxValue;

        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        int x1 = x0 + 1, y1 = y0 + 1;
        double fx = x - x0, fy = y - y0;
        double top = SamplePixel(source, width, height, stride, x0, y0) * (1 - fx) +
                     SamplePixel(source, width, height, stride, x1, y0) * fx;
        double bottom = SamplePixel(source, width, height, stride, x0, y1) * (1 - fx) +
                        SamplePixel(source, width, height, stride, x1, y1) * fx;
        return (byte)Math.Clamp((int)Math.Round(top * (1 - fy) + bottom * fy, MidpointRounding.AwayFromZero), 0, 255);
    }

    private static byte SamplePixel(ReadOnlySpan<byte> source, int width, int height, int stride, int x, int y) =>
        x < 0 || y < 0 || x >= width || y >= height ? byte.MaxValue : source[(y * stride) + x];
}

// Wall-time duty accounting only. Any enclosing CPU job remains unchanged.
// Oversleep credit is limited to four existing 2 ms active slices.
internal sealed class AffinePacingBalance
{
    private static readonly long MaximumCreditTicks = TimeSpan.FromMilliseconds(8).Ticks;
    private long debtTicks;

    internal TimeSpan RecordActive(TimeSpan active)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(active, TimeSpan.Zero);
        debtTicks = debtTicks > long.MaxValue - active.Ticks ? long.MaxValue : debtTicks + active.Ticks;
        return TimeSpan.FromTicks(Math.Max(0, debtTicks));
    }

    internal void RecordWait(TimeSpan actual)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(actual, TimeSpan.Zero);
        if (debtTicks <= 0) throw new InvalidOperationException("A wait requires positive pacing debt.");
        debtTicks = Math.Max(-MaximumCreditTicks, debtTicks - actual.Ticks);
    }
}
