// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Markers.Detection;

namespace GraphReader.Markers.Classification;

public sealed class MarkerPatchExtractor : IMarkerPatchExtractor
{
    public IReadOnlyList<MarkerPatch> Extract(
        MarkerImageFrame image,
        IReadOnlyList<MarkerCenter> markers,
        MarkerPatchExtractionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateImage(image);
        ValidateOptions(options);
        ValidateContentBounds(image, markers, options.OriginalPixelContentBounds);

        var patches = new MarkerPatch[markers.Count];
        for (var markerIndex = 0; markerIndex < markers.Count; markerIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var marker = markers[markerIndex]
                ?? throw new ArgumentException("Marker collections cannot contain null entries.", nameof(markers));
            ValidateMarker(marker);
            patches[markerIndex] = ExtractPatch(image, marker, options);
        }

        return ClassificationCollections.Freeze(patches);
    }

    private static MarkerPatch ExtractPatch(
        MarkerImageFrame image,
        MarkerCenter marker,
        MarkerPatchExtractionOptions options)
    {
        var frameCenter = image.OriginalToFrame.MapFromOriginal(marker.Center);
        var scaleX = Math.Sqrt(
            (image.OriginalToFrame.M11 * image.OriginalToFrame.M11) +
            (image.OriginalToFrame.M21 * image.OriginalToFrame.M21));
        var scaleY = Math.Sqrt(
            (image.OriginalToFrame.M12 * image.OriginalToFrame.M12) +
            (image.OriginalToFrame.M22 * image.OriginalToFrame.M22));
        var frameRadius = marker.Radius * Math.Sqrt(scaleX * scaleY);
        var halfExtent = Math.Max(
            options.MinimumHalfExtentFramePixels,
            frameRadius * options.RadiusScale);

        if (!frameCenter.IsFinite || !double.IsFinite(halfExtent))
        {
            throw new ArgumentException(
                $"Marker '{marker.MarkerId}' cannot be mapped to a finite classifier patch.",
                nameof(marker));
        }

        var area = checked(options.Width * options.Height);
        var patch = new float[checked(area * options.ChannelCount)];
        MarkerRectangle? contentBounds = options.OriginalPixelContentBounds is { } bounds &&
            bounds.TryGetValue(marker.MarkerId, out MarkerRectangle selected) ? selected : null;
        for (var patchY = 0; patchY < options.Height; patchY++)
        {
            var sourceY = frameCenter.Y +
                ((((patchY + 0.5) / options.Height) * 2) - 1) * halfExtent;
            for (var patchX = 0; patchX < options.Width; patchX++)
            {
                var sourceX = frameCenter.X +
                    ((((patchX + 0.5) / options.Width) * 2) - 1) * halfExtent;
                patch[(patchY * options.Width) + patchX] = SampleInkProbability(
                    image,
                    sourceX,
                    sourceY,
                    options.PaddingValue,
                    contentBounds);
            }
        }

        return new MarkerPatch(
            marker,
            options.Width,
            options.Height,
            options.ChannelCount,
            patch);
    }

    private static float SampleInkProbability(
        MarkerImageFrame image,
        double x,
        double y,
        float paddingValue,
        MarkerRectangle? contentBounds)
    {
        if (x < 0 || y < 0 || x > image.Width - 1 || y > image.Height - 1)
        {
            return paddingValue;
        }

        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(x0 + 1, image.Width - 1);
        var y1 = Math.Min(y0 + 1, image.Height - 1);
        var xFraction = (float)(x - x0);
        var yFraction = (float)(y - y0);
        var pixels = image.ChannelsFirstPixels.Span;
        var brightness = 0f;
        for (var channel = 0; channel < image.ChannelCount; channel++)
        {
            var channelOffset = checked(channel * image.Width * image.Height);
            var topLeft = Brightness(pixels, channelOffset, image.Width, x0, y0, contentBounds);
            var topRight = Brightness(pixels, channelOffset, image.Width, x1, y0, contentBounds);
            var bottomLeft = Brightness(pixels, channelOffset, image.Width, x0, y1, contentBounds);
            var bottomRight = Brightness(pixels, channelOffset, image.Width, x1, y1, contentBounds);
            var top = topLeft + ((topRight - topLeft) * xFraction);
            var bottom = bottomLeft + ((bottomRight - bottomLeft) * xFraction);
            brightness += top + ((bottom - top) * yFraction);
        }

        return 1 - (brightness / image.ChannelCount);
    }

    private static float Brightness(ReadOnlySpan<float> pixels, int channelOffset, int width,
        int x, int y, MarkerRectangle? bounds) => bounds is { } box &&
        (x < box.Left || x >= box.Right || y < box.Top || y >= box.Bottom)
            ? 1f : pixels[channelOffset + (y * width) + x];

    private static void ValidateContentBounds(MarkerImageFrame image, IReadOnlyList<MarkerCenter> markers,
        IReadOnlyDictionary<string, MarkerRectangle>? bounds)
    {
        if (bounds is null || bounds.Count == 0) return;
        if (image.SourceImage != MarkerSourceImage.Original || image.OriginalToFrame != MarkerAffineTransform.Identity)
            throw new ArgumentException("Symbol content isolation requires aligned original pixels.", nameof(bounds));
        foreach ((string id, MarkerRectangle box) in bounds)
        {
            MarkerCenter? marker = markers.FirstOrDefault(item => item is not null && item.MarkerId == id);
            if (marker is null || !box.IsValid || box.Left < 0 || box.Top < 0 ||
                box.Right > image.Width || box.Bottom > image.Height ||
                marker.Center.X < box.Left || marker.Center.X >= box.Right ||
                marker.Center.Y < box.Top || marker.Center.Y >= box.Bottom)
                throw new ArgumentException("Symbol bounds must enclose their named marker in original pixels.", nameof(bounds));
        }
    }

    private static void ValidateImage(MarkerImageFrame image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.ChannelCount <= 0)
        {
            throw new ArgumentException("Classifier image dimensions and channel count must be positive.", nameof(image));
        }

        int expectedLength;
        try
        {
            expectedLength = checked(image.Width * image.Height * image.ChannelCount);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("Classifier image dimensions exceed supported memory limits.", nameof(image), exception);
        }

        if (image.ChannelsFirstPixels.Length != expectedLength)
        {
            throw new ArgumentException(
                $"Classifier image requires {expectedLength} channel-first values but received " +
                $"{image.ChannelsFirstPixels.Length}.",
                nameof(image));
        }

        if (!image.OriginalToFrame.IsInvertible)
        {
            throw new ArgumentException("Classifier image transform must be finite and invertible.", nameof(image));
        }

        foreach (var value in image.ChannelsFirstPixels.Span)
        {
            if (!float.IsFinite(value) || value < 0 || value > 1)
            {
                throw new ArgumentException(
                    "Classifier source pixels must be finite normalized brightness values in [0,1].",
                    nameof(image));
            }
        }
    }

    private static void ValidateOptions(MarkerPatchExtractionOptions options)
    {
        if (options.Width <= 0 || options.Height <= 0 || options.ChannelCount != 1)
        {
            throw new ArgumentException(
                "Classifier patches require positive dimensions and exactly one ink-probability channel.",
                nameof(options));
        }

        if (!double.IsFinite(options.RadiusScale) || options.RadiusScale <= 0 ||
            !double.IsFinite(options.MinimumHalfExtentFramePixels) ||
            options.MinimumHalfExtentFramePixels <= 0 ||
            !float.IsFinite(options.PaddingValue) ||
            options.PaddingValue < 0 || options.PaddingValue > 1)
        {
            throw new ArgumentException(
                "Classifier patch sampling options must be finite and use ink padding in [0,1].",
                nameof(options));
        }

        try
        {
            _ = checked(options.Width * options.Height * options.ChannelCount);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("Classifier patch dimensions exceed supported memory limits.", nameof(options), exception);
        }
    }

    private static void ValidateMarker(MarkerCenter marker)
    {
        if (string.IsNullOrWhiteSpace(marker.MarkerId) ||
            !marker.Center.IsFinite ||
            !double.IsFinite(marker.Radius) || marker.Radius <= 0 ||
            !string.Equals(
                marker.CoordinateSpace,
                MarkerClassificationContract.CoordinateSpace,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Classifier markers require an ID, finite center, positive radius, and original-pixel coordinates.",
                nameof(marker));
        }
    }
}
