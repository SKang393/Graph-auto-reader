// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Markers.Detection;

namespace GraphReader.Markers.Grouping;

/// <summary>
/// Finds plausible marker-to-marker strokes in an immutable source image.
/// Connections are evidence for grouping, not a declaration of series identity.
/// </summary>
public sealed class MarkerConnectionGraphBuilder : IMarkerConnectionGraphBuilder
{
    private const float InkThreshold = 0.50f;
    private const float MaskThreshold = 0.50f;
    private const double MaximumArtifactMarkerProbability = 0.50;

    public ValueTask<IReadOnlyList<MarkerConnection>> BuildAsync(
        MarkerConnectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);

        var orderedMarkers = request.Markers
            .OrderBy(static evidence => evidence.Marker.Marker.Center.X)
            .ThenBy(static evidence => evidence.Marker.Marker.Center.Y)
            .ThenBy(static evidence => evidence.Marker.Marker.MarkerId, StringComparer.Ordinal)
            .ToArray();
        var connections = new List<MarkerConnection>();

        for (var fromIndex = 0; fromIndex < orderedMarkers.Length; fromIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var from = orderedMarkers[fromIndex].Marker;
            if (from.ArtifactProbability > MaximumArtifactMarkerProbability)
            {
                continue;
            }

            for (var toIndex = fromIndex + 1; toIndex < orderedMarkers.Length; toIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var to = orderedMarkers[toIndex].Marker;
                var horizontalGap = to.Marker.Center.X - from.Marker.Center.X;
                if (horizontalGap > request.Options.MaximumHorizontalGapPixels)
                {
                    break;
                }

                if (horizontalGap <= 0 || to.ArtifactProbability > MaximumArtifactMarkerProbability)
                {
                    continue;
                }

                var sample = SampleCorridor(request.Image, from, to, request.Options, cancellationToken);
                if (sample is null ||
                    sample.InkFraction < request.Options.MinimumInkFraction ||
                    sample.MaskFraction > request.Options.MaximumMaskFraction)
                {
                    continue;
                }

                connections.Add(new MarkerConnection(
                    from.Marker.MarkerId,
                    to.Marker.MarkerId,
                    CalculateConfidence(sample, request.Options),
                    ClassifyStyle(sample)));
            }
        }

        return ValueTask.FromResult(GroupingCollections.Freeze(connections));
    }

    private static CorridorSample? SampleCorridor(
        MarkerImageFrame image,
        Classification.ClassifiedMarker from,
        Classification.ClassifiedMarker to,
        MarkerConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var fromCenter = image.OriginalToFrame.MapFromOriginal(from.Marker.Center);
        var toCenter = image.OriginalToFrame.MapFromOriginal(to.Marker.Center);
        var deltaX = toCenter.X - fromCenter.X;
        var deltaY = toCenter.Y - fromCenter.Y;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (!double.IsFinite(distance) || distance <= 0)
        {
            return null;
        }

        var scaleX = Math.Sqrt(
            (image.OriginalToFrame.M11 * image.OriginalToFrame.M11) +
            (image.OriginalToFrame.M21 * image.OriginalToFrame.M21));
        var scaleY = Math.Sqrt(
            (image.OriginalToFrame.M12 * image.OriginalToFrame.M12) +
            (image.OriginalToFrame.M22 * image.OriginalToFrame.M22));
        var radiusScale = Math.Sqrt(scaleX * scaleY) * options.MarkerExclusionRadiusScale;
        var fromExclusion = from.Marker.Radius * radiusScale;
        var toExclusion = to.Marker.Radius * radiusScale;
        var sampleLength = distance - fromExclusion - toExclusion;
        if (!double.IsFinite(sampleLength) || sampleLength <= 1)
        {
            return null;
        }

        var unitX = deltaX / distance;
        var unitY = deltaY / distance;
        var normalX = -unitY;
        var normalY = unitX;
        var start = new MarkerPoint(
            fromCenter.X + (unitX * fromExclusion),
            fromCenter.Y + (unitY * fromExclusion));
        var end = new MarkerPoint(
            toCenter.X - (unitX * toExclusion),
            toCenter.Y - (unitY * toExclusion));
        var longitudinalSamples = Math.Max(options.MinimumSamples, (int)Math.Ceiling(sampleLength) + 1);
        var corridorSamples = Math.Max(3, ((int)Math.Ceiling(options.CorridorHalfWidthPixels) * 2) + 1);
        var inkByPosition = new bool[longitudinalSamples];
        var maskedPositions = 0;
        var unmaskedPositions = 0;
        var inkPositions = 0;

        for (var sampleIndex = 0; sampleIndex < longitudinalSamples; sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fraction = longitudinalSamples == 1 ? 0.5 : sampleIndex / (double)(longitudinalSamples - 1);
            var centerX = start.X + ((end.X - start.X) * fraction);
            var centerY = start.Y + ((end.Y - start.Y) * fraction);
            var positionMasked = false;
            var maximumInk = 0f;

            for (var corridorIndex = 0; corridorIndex < corridorSamples; corridorIndex++)
            {
                var corridorFraction = corridorSamples == 1
                    ? 0
                    : (corridorIndex / (double)(corridorSamples - 1) * 2) - 1;
                var offset = corridorFraction * options.CorridorHalfWidthPixels;
                var x = centerX + (normalX * offset);
                var y = centerY + (normalY * offset);
                if (SampleMask(image.OcrMask, x, y) >= MaskThreshold ||
                    SampleMask(image.ArtifactMask, x, y) >= MaskThreshold)
                {
                    positionMasked = true;
                }

                maximumInk = Math.Max(maximumInk, SampleInk(image, x, y));
            }

            if (positionMasked)
            {
                maskedPositions++;
                continue;
            }

            unmaskedPositions++;
            if (maximumInk >= InkThreshold)
            {
                inkByPosition[sampleIndex] = true;
                inkPositions++;
            }
        }

        if (unmaskedPositions == 0)
        {
            return null;
        }

        return new CorridorSample(
            inkPositions / (double)unmaskedPositions,
            maskedPositions / (double)longitudinalSamples,
            CountInkRuns(inkByPosition),
            LongestInternalGap(inkByPosition));
    }

    private static double CalculateConfidence(CorridorSample sample, MarkerConnectionOptions options)
    {
        var availableRange = Math.Max(1e-12, 1 - options.MinimumInkFraction);
        var coverage = Math.Clamp(
            (sample.InkFraction - options.MinimumInkFraction) / availableRange,
            0,
            1);
        return Math.Clamp((0.50 + (0.50 * coverage)) * (1 - sample.MaskFraction), 0, 1);
    }

    private static MarkerConnectionStyle ClassifyStyle(CorridorSample sample)
    {
        if (sample.InkRuns >= 2 && sample.LongestInternalGap >= 2)
        {
            return MarkerConnectionStyle.Dashed;
        }

        if (sample.InkRuns == 1 || sample.InkFraction >= 0.90)
        {
            return MarkerConnectionStyle.Solid;
        }

        return MarkerConnectionStyle.Unknown;
    }

    private static int CountInkRuns(bool[] ink)
    {
        var runs = 0;
        var inRun = false;
        for (var index = 0; index < ink.Length; index++)
        {
            if (ink[index] && !inRun)
            {
                runs++;
                inRun = true;
            }
            else if (!ink[index])
            {
                inRun = false;
            }
        }

        return runs;
    }

    private static int LongestInternalGap(bool[] ink)
    {
        var firstInk = -1;
        var lastInk = -1;
        for (var index = 0; index < ink.Length; index++)
        {
            if (!ink[index])
            {
                continue;
            }

            firstInk = firstInk < 0 ? index : firstInk;
            lastInk = index;
        }

        if (firstInk < 0 || firstInk == lastInk)
        {
            return 0;
        }

        var longest = 0;
        var current = 0;
        for (var index = firstInk + 1; index < lastInk; index++)
        {
            if (!ink[index])
            {
                current++;
                longest = Math.Max(longest, current);
            }
            else
            {
                current = 0;
            }
        }

        return longest;
    }

    private static float SampleInk(MarkerImageFrame image, double x, double y)
    {
        // Pixels outside the image contain no evidence of a connecting stroke.
        // SamplePlane pads masks with zero, but zero image brightness is black.
        if (x < 0 || y < 0 || x > image.Width - 1d || y > image.Height - 1d)
        {
            return 0;
        }

        var pixelsPerChannel = checked(image.Width * image.Height);
        var brightness = 0d;
        for (var channel = 0; channel < image.ChannelCount; channel++)
        {
            brightness += SamplePlane(
                image.ChannelsFirstPixels.Span.Slice(channel * pixelsPerChannel, pixelsPerChannel),
                image.Width,
                image.Height,
                x,
                y);
        }

        return (float)Math.Clamp(1 - (brightness / image.ChannelCount), 0, 1);
    }

    private static float SampleMask(MarkerMask mask, double x, double y) =>
        SamplePlane(mask.Values.Span, mask.Width, mask.Height, x, y);

    private static float SamplePlane(
        ReadOnlySpan<float> values,
        int width,
        int height,
        double x,
        double y)
    {
        if (x < 0 || y < 0 || x > width - 1d || y > height - 1d)
        {
            return 0;
        }

        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(width - 1, x0 + 1);
        var y1 = Math.Min(height - 1, y0 + 1);
        var xWeight = x - x0;
        var yWeight = y - y0;
        var top = (values[(y0 * width) + x0] * (1 - xWeight)) +
                  (values[(y0 * width) + x1] * xWeight);
        var bottom = (values[(y1 * width) + x0] * (1 - xWeight)) +
                     (values[(y1 * width) + x1] * xWeight);
        return (float)((top * (1 - yWeight)) + (bottom * yWeight));
    }

    private static void Validate(MarkerConnectionRequest request)
    {
        ValidateImage(request.Image);
        ValidateOptions(request.Options);
        var markerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in request.Markers)
        {
            if (evidence is null ||
                evidence.Marker is null ||
                string.IsNullOrWhiteSpace(evidence.Marker.Marker.MarkerId) ||
                !markerIds.Add(evidence.Marker.Marker.MarkerId) ||
                !evidence.Marker.Marker.Center.IsFinite ||
                !double.IsFinite(evidence.Marker.Marker.Radius) || evidence.Marker.Marker.Radius <= 0 ||
                !double.IsFinite(evidence.Marker.ArtifactProbability) ||
                evidence.Marker.ArtifactProbability < 0 || evidence.Marker.ArtifactProbability > 1 ||
                !string.Equals(
                    evidence.Marker.Marker.CoordinateSpace,
                    MarkerGroupingContract.CoordinateSpace,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Connection markers require unique IDs, finite geometry and confidence, and original-pixel coordinates.",
                    nameof(request));
            }
        }
    }

    private static void ValidateImage(MarkerImageFrame image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.ChannelCount <= 0 ||
            !image.OriginalToFrame.IsInvertible)
        {
            throw new ArgumentException(
                "Connection images require positive dimensions and an invertible transform.",
                nameof(image));
        }

        int pixelCount;
        try
        {
            pixelCount = checked(image.Width * image.Height);
            if (image.ChannelsFirstPixels.Length != checked(pixelCount * image.ChannelCount) ||
                image.OcrMask.Width != image.Width || image.OcrMask.Height != image.Height ||
                image.ArtifactMask.Width != image.Width || image.ArtifactMask.Height != image.Height ||
                image.OcrMask.Values.Length != pixelCount || image.ArtifactMask.Values.Length != pixelCount)
            {
                throw new ArgumentException(
                    "Connection image planes and masks must match the declared dimensions.",
                    nameof(image));
            }
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException(
                "Connection image dimensions exceed supported memory limits.",
                nameof(image),
                exception);
        }

        if (image.ChannelsFirstPixels.Span.ContainsAnyExceptInRange(0f, 1f) ||
            image.OcrMask.Values.Span.ContainsAnyExceptInRange(0f, 1f) ||
            image.ArtifactMask.Values.Span.ContainsAnyExceptInRange(0f, 1f))
        {
            throw new ArgumentException(
                "Connection image and mask values must be finite values from zero through one.",
                nameof(image));
        }
    }

    private static void ValidateOptions(MarkerConnectionOptions options)
    {
        if (!double.IsFinite(options.MarkerExclusionRadiusScale) ||
            options.MarkerExclusionRadiusScale < 0 ||
            !double.IsFinite(options.CorridorHalfWidthPixels) || options.CorridorHalfWidthPixels < 0 ||
            !IsProbability(options.MinimumInkFraction) ||
            !IsProbability(options.MaximumMaskFraction) ||
            !double.IsFinite(options.MaximumHorizontalGapPixels) ||
            options.MaximumHorizontalGapPixels <= 0 ||
            options.MinimumSamples < 2 || options.MinimumSamples > 4096)
        {
            throw new ArgumentException("Connection sampling options are invalid.", nameof(options));
        }
    }

    private static bool IsProbability(double value) =>
        double.IsFinite(value) && value >= 0 && value <= 1;

    private sealed record CorridorSample(
        double InkFraction,
        double MaskFraction,
        int InkRuns,
        int LongestInternalGap);
}
