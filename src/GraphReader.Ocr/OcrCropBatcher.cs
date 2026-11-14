// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GraphReader.Ocr;

public enum OcrCropResizeMode
{
    PreserveAspectRatioPad,
    Stretch,
}

public enum OcrCropWidthMode
{
    Fixed,
    PaddleBatchMaximumAspectRatio,
}

public sealed record OcrCropBatcherOptions
{
    public int TargetWidth { get; init; } = 128;

    public int TargetHeight { get; init; } = 32;

    /// <summary>
    /// Controls whether every crop uses <see cref="TargetWidth"/> or each
    /// aspect-ratio-sorted batch expands to PaddleOCR's maximum width-to-height
    /// ratio. TargetWidth is the minimum width in the dynamic mode.
    /// </summary>
    public OcrCropWidthMode WidthMode { get; init; } = OcrCropWidthMode.Fixed;

    /// <summary>
    /// Hard memory bound for dynamic recognition tensors. A source crop that
    /// requires a wider tensor is rejected instead of being truncated.
    /// </summary>
    public int MaximumTargetWidth { get; init; } = 4096;

    public int BatchSize { get; init; } = 16;

    /// <summary>
    /// Optional dynamic-batch allocation bound: padded batch width times its
    /// count cannot exceed this multiple of the sum of each crop's original
    /// content width or TargetWidth, whichever is larger. Splits stay within
    /// original batches and preserve their sampling widths, including clamps.
    /// Tensor allocation never exceeds the original batch allocation. This
    /// limits padding overhead, not recognition accuracy. Null preserves the
    /// existing grouping.
    /// </summary>
    public double? MaximumPaddingOverhead { get; init; }

    public double PaddingPixels { get; init; } = 1;

    public double? HorizontalPaddingPixels { get; init; }

    public double? VerticalPaddingPixels { get; init; }

    /// <summary>
    /// Adds padding on each side of the crop's oriented vertical content axis.
    /// A value of 0.25 maps a tight glyph to at most two thirds of the output
    /// height before interpolation, which is required by component recognizers
    /// that reserve taller components for graph-structure rejection.
    /// </summary>
    public double VerticalContentPaddingRatio { get; init; }

    /// <summary>
    /// Controls how a detected text region is mapped into the recognition tensor.
    /// PP-OCR recognition preserves the source aspect ratio and pads the remaining
    /// width instead of stretching every crop to the model's maximum width.
    /// </summary>
    public OcrCropResizeMode ResizeMode { get; init; } =
        OcrCropResizeMode.PreserveAspectRatioPad;

    /// <summary>
    /// Source-space value used for right padding. The PP-OCR normalization
    /// (value - 0.5) * 2 maps the default 0.5 value to tensor-space zero.
    /// </summary>
    public float PaddingValue { get; init; } = 0.5f;
}

public static class OcrCropBatcher
{
    public static IReadOnlyList<IReadOnlyList<OcrCrop>> CreateBatches(
        OcrImage image,
        IReadOnlyList<OcrDetectedRegion> regions,
        OcrCropBatcherOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(regions);
        options ??= new OcrCropBatcherOptions();
        Validate(image, options);

        var batches = new List<IReadOnlyList<OcrCrop>>();
        IEnumerable<PreparedRegion> ordered = regions
            .Select(region => PrepareRegion(region, options));
        ordered = options.WidthMode == OcrCropWidthMode.PaddleBatchMaximumAspectRatio
            ? ordered.OrderBy(static item => item.AspectRatio)
                .ThenBy(static item => item.Region.RegionId, StringComparer.Ordinal)
            : ordered.OrderBy(static item => item.Region.RegionId, StringComparer.Ordinal);
        PreparedRegion[] prepared = ordered.ToArray();
        for (var index = 0; index < prepared.Length; index += options.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreparedRegion[] batch = prepared.Skip(index).Take(options.BatchSize).ToArray();
            int targetWidth = TargetWidth(batch, options);
            if (options.MaximumPaddingOverhead is not { } limit)
            {
                batches.Add(Array.AsReadOnly(batch
                    .Select(item => CreateCrop(image, item, options, targetWidth, cancellationToken))
                    .ToArray()));
                continue;
            }

            // Derive sampling widths before splitting. A fractional aspect ratio
            // may round up for content but down for the legacy batch width;
            // recalculating after a split would change interpolation coordinates.
            int[] contentWidths = batch.Select(item => ContentWidth(
                item.PaddedBounds, item.Orientation, options, targetWidth)).ToArray();
            for (int start = 0; start < batch.Length;)
            {
                long individualWidthSum = 0;
                int count = 0;
                int groupWidth = options.TargetWidth;
                while (start + count < batch.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int width = Math.Max(options.TargetWidth, contentWidths[start + count]);
                    int proposedWidth = Math.Max(groupWidth, width);
                    long proposedSum = individualWidthSum + width;
                    if (count > 0 && (count + 1L) * proposedWidth > limit * proposedSum)
                    {
                        break;
                    }

                    individualWidthSum = proposedSum;
                    groupWidth = proposedWidth;
                    count++;
                }

                var crops = new OcrCrop[count];
                for (int offset = 0; offset < count; offset++)
                {
                    int itemIndex = start + offset;
                    crops[offset] = CreateCrop(image, batch[itemIndex], options, groupWidth,
                        cancellationToken, contentWidths[itemIndex]);
                }

                batches.Add(Array.AsReadOnly(crops));
                start += count;
            }
        }

        return OcrCollections.Freeze(batches);
    }

    private static OcrCrop CreateCrop(
        OcrImage image,
        PreparedRegion prepared,
        OcrCropBatcherOptions options,
        int targetWidth,
        CancellationToken cancellationToken,
        int? preservedContentWidth = null)
    {
        OcrDetectedRegion region = prepared.Region;
        OcrOrientation orientation = prepared.Orientation;
        OcrRectangle padded = prepared.PaddedBounds;
        int contentWidth = preservedContentWidth ?? ContentWidth(padded, orientation, options, targetWidth);
        var output = Enumerable.Repeat(
                options.PaddingValue,
                checked(targetWidth * options.TargetHeight))
            .ToArray();
        float[]? bgrOutput = image.BgrPixels is null
            ? null
            : Enumerable.Repeat(
                    options.PaddingValue,
                    checked(targetWidth * options.TargetHeight * 3))
                .ToArray();
        for (var targetY = 0; targetY < options.TargetHeight; targetY++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var v = (targetY + 0.5) / options.TargetHeight;
            for (var targetX = 0; targetX < contentWidth; targetX++)
            {
                var u = (targetX + 0.5) / contentWidth;
                var original = MapSample(padded, u, v, orientation);
                var source = image.OriginalToImage.MapFromOriginal(original);
                output[(targetY * targetWidth) + targetX] = Sample(image, source.X, source.Y);
                if (bgrOutput is not null)
                {
                    int targetOffset = checked(((targetY * targetWidth) + targetX) * 3);
                    for (int channel = 0; channel < 3; channel++)
                    {
                        bgrOutput[targetOffset + channel] = SampleBgr(image, source.X, source.Y, channel);
                    }
                }
            }
        }

        var bgrPixels = bgrOutput is null
            ? null
            : new OcrBgrFloatPixels(checked(targetWidth * 3), bgrOutput);
        OcrV8SourceCrop? sourceCrop = image.SourceImage == OcrSourceImage.Original &&
            image.OriginalToImage == OcrFrameTransform.Identity
                ? OcrV8SourcePostprocessor.ExtractSourceCrop(image, region)
                : null;

        return new OcrCrop(
            region.RegionId,
            image.SourceImage,
            targetWidth,
            options.TargetHeight,
            output,
            HashCrop(output, bgrOutput, image.SourceImage, targetWidth, options.TargetHeight),
            region.Polygon,
            bgrPixels,
            sourceCrop);
    }

    private static int ContentWidth(
        OcrRectangle bounds,
        OcrOrientation orientation,
        OcrCropBatcherOptions options,
        int targetWidth)
    {
        if (options.ResizeMode == OcrCropResizeMode.Stretch)
        {
            return targetWidth;
        }

        double orientedWidth = orientation is
            OcrOrientation.RotatedClockwise or OcrOrientation.RotatedCounterClockwise
                ? bounds.Height
                : bounds.Width;
        double orientedHeight = orientation is
            OcrOrientation.RotatedClockwise or OcrOrientation.RotatedCounterClockwise
                ? bounds.Width
                : bounds.Height;
        double ratio = orientedWidth / orientedHeight;
        return Math.Clamp(
            checked((int)Math.Ceiling(options.TargetHeight * ratio)),
            1,
            targetWidth);
    }

    private static PreparedRegion PrepareRegion(
        OcrDetectedRegion region,
        OcrCropBatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.CoordinateSpace != OcrContract.CoordinateSpace)
        {
            throw new ArgumentException("OCR regions must use original_pixels.", nameof(region));
        }

        OcrOrientation orientation = GraphTextRoleClassifier.GetOrientation(region.OrientationDegrees);
        OcrRectangle bounds = region.Polygon.Bounds;
        double horizontalPadding = options.HorizontalPaddingPixels ?? options.PaddingPixels;
        double verticalPadding = options.VerticalPaddingPixels ?? options.PaddingPixels;
        if (orientation is OcrOrientation.RotatedClockwise or OcrOrientation.RotatedCounterClockwise)
        {
            horizontalPadding += bounds.Width * options.VerticalContentPaddingRatio;
        }
        else
        {
            verticalPadding += bounds.Height * options.VerticalContentPaddingRatio;
        }

        var padded = new OcrRectangle(
            bounds.X - horizontalPadding,
            bounds.Y - verticalPadding,
            bounds.Width + (2 * horizontalPadding),
            bounds.Height + (2 * verticalPadding));
        double orientedWidth = orientation is
            OcrOrientation.RotatedClockwise or OcrOrientation.RotatedCounterClockwise
                ? padded.Height
                : padded.Width;
        double orientedHeight = orientation is
            OcrOrientation.RotatedClockwise or OcrOrientation.RotatedCounterClockwise
                ? padded.Width
                : padded.Height;
        return new PreparedRegion(region, orientation, padded, orientedWidth / orientedHeight);
    }

    private static int TargetWidth(
        IReadOnlyList<PreparedRegion> batch,
        OcrCropBatcherOptions options)
    {
        if (options.WidthMode == OcrCropWidthMode.Fixed || batch.Count == 0)
        {
            return options.TargetWidth;
        }

        double minimumRatio = options.TargetWidth / (double)options.TargetHeight;
        double maximumRatio = Math.Max(minimumRatio, batch.Max(static item => item.AspectRatio));
        double requiredWidth = options.TargetHeight * maximumRatio;
        if (!double.IsFinite(requiredWidth) || requiredWidth > options.MaximumTargetWidth)
        {
            throw new InvalidDataException(
                $"OCR crop batch requires width {requiredWidth:R}, above the reviewed bound {options.MaximumTargetWidth}.");
        }

        return Math.Max(options.TargetWidth, checked((int)requiredWidth));
    }

    private static OcrPoint MapSample(
        OcrRectangle bounds,
        double u,
        double v,
        OcrOrientation orientation) =>
        orientation switch
        {
            OcrOrientation.RotatedClockwise =>
                new OcrPoint(
                    bounds.Left + (v * bounds.Width) - 0.5,
                    bounds.Top + ((1 - u) * bounds.Height) - 0.5),
            OcrOrientation.RotatedCounterClockwise =>
                new OcrPoint(
                    bounds.Left + ((1 - v) * bounds.Width) - 0.5,
                    bounds.Top + (u * bounds.Height) - 0.5),
            _ => new OcrPoint(
                bounds.Left + (u * bounds.Width) - 0.5,
                bounds.Top + (v * bounds.Height) - 0.5),
        };

    private static float Sample(OcrImage image, double x, double y)
    {
        double boundedX = Math.Clamp(x, 0, image.Width - 1d);
        double boundedY = Math.Clamp(y, 0, image.Height - 1d);
        int x0 = (int)Math.Floor(boundedX);
        int y0 = (int)Math.Floor(boundedY);
        int x1 = Math.Min(x0 + 1, image.Width - 1);
        int y1 = Math.Min(y0 + 1, image.Height - 1);
        double xWeight = boundedX - x0;
        double yWeight = boundedY - y0;
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        double top = (pixels[(y0 * image.Stride) + x0] * (1 - xWeight)) +
            (pixels[(y0 * image.Stride) + x1] * xWeight);
        double bottom = (pixels[(y1 * image.Stride) + x0] * (1 - xWeight)) +
            (pixels[(y1 * image.Stride) + x1] * xWeight);
        return (float)(((top * (1 - yWeight)) + (bottom * yWeight)) / 255d);
    }

    private static float SampleBgr(OcrImage image, double x, double y, int channel)
    {
        OcrBgrBytePixels bgr = image.BgrPixels ??
            throw new InvalidOperationException("BGR sampling requires a BGR24 image plane.");
        double boundedX = Math.Clamp(x, 0, image.Width - 1d);
        double boundedY = Math.Clamp(y, 0, image.Height - 1d);
        int x0 = (int)Math.Floor(boundedX);
        int y0 = (int)Math.Floor(boundedY);
        int x1 = Math.Min(x0 + 1, image.Width - 1);
        int y1 = Math.Min(y0 + 1, image.Height - 1);
        double xWeight = boundedX - x0;
        double yWeight = boundedY - y0;
        ReadOnlySpan<byte> pixels = bgr.Pixels.Span;
        double top = (pixels[(y0 * bgr.Stride) + (x0 * 3) + channel] * (1 - xWeight)) +
            (pixels[(y0 * bgr.Stride) + (x1 * 3) + channel] * xWeight);
        double bottom = (pixels[(y1 * bgr.Stride) + (x0 * 3) + channel] * (1 - xWeight)) +
            (pixels[(y1 * bgr.Stride) + (x1 * 3) + channel] * xWeight);
        return (float)(((top * (1 - yWeight)) + (bottom * yWeight)) / 255d);
    }

    private static string HashCrop(
        ReadOnlySpan<float> values,
        float[]? bgrValues,
        OcrSourceImage source,
        int width,
        int height)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> integer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(integer, width);
        hash.AppendData(integer);
        BinaryPrimitives.WriteInt32LittleEndian(integer, height);
        hash.AppendData(integer);
        BinaryPrimitives.WriteInt32LittleEndian(integer, (int)source);
        hash.AppendData(integer);
        foreach (var value in values)
        {
            BinaryPrimitives.WriteSingleLittleEndian(integer, value);
            hash.AppendData(integer);
        }

        BinaryPrimitives.WriteInt32LittleEndian(integer, bgrValues is null ? 0 : 3);
        hash.AppendData(integer);
        if (bgrValues is not null)
        {
            foreach (float value in bgrValues)
            {
                BinaryPrimitives.WriteSingleLittleEndian(integer, value);
                hash.AppendData(integer);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Validate(OcrImage image, OcrCropBatcherOptions options)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Stride < image.Width ||
            image.Pixels.Length < checked(image.Stride * image.Height))
        {
            throw new ArgumentException("OCR image dimensions, stride, or pixel buffer are invalid.", nameof(image));
        }

        if (!image.OriginalToImage.IsInvertible)
        {
            throw new ArgumentException("OCR image transform must be finite and invertible.", nameof(image));
        }

        if (options.TargetWidth <= 0 || options.TargetHeight <= 0 || options.BatchSize <= 0 ||
            options.MaximumTargetWidth < options.TargetWidth ||
            !Enum.IsDefined(options.WidthMode) ||
            (options.MaximumPaddingOverhead is { } overhead &&
             (!double.IsFinite(overhead) || overhead < 1 || options.WidthMode != OcrCropWidthMode.PaddleBatchMaximumAspectRatio)) ||
            !double.IsFinite(options.PaddingPixels) || options.PaddingPixels < 0 ||
            (options.HorizontalPaddingPixels.HasValue &&
             (!double.IsFinite(options.HorizontalPaddingPixels.Value) ||
              options.HorizontalPaddingPixels.Value < 0)) ||
            (options.VerticalPaddingPixels.HasValue &&
             (!double.IsFinite(options.VerticalPaddingPixels.Value) ||
              options.VerticalPaddingPixels.Value < 0)) ||
            !double.IsFinite(options.VerticalContentPaddingRatio) ||
            options.VerticalContentPaddingRatio < 0 ||
            !Enum.IsDefined(options.ResizeMode) ||
            !float.IsFinite(options.PaddingValue) || options.PaddingValue is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (image.BgrPixels is { } bgr &&
            (bgr.Stride < checked(image.Width * 3) ||
             bgr.Pixels.Length != checked(bgr.Stride * image.Height)))
        {
            throw new ArgumentException("OCR BGR24 dimensions, stride, or pixel buffer are invalid.", nameof(image));
        }
    }

    private sealed record PreparedRegion(
        OcrDetectedRegion Region,
        OcrOrientation Orientation,
        OcrRectangle PaddedBounds,
        double AspectRatio);
}
