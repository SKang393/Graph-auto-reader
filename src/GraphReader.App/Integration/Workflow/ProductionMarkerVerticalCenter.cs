// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Measures the vertical midpoint of a bounded filled body on connected traces.</summary>
internal static class ProductionMarkerVerticalCenter
{
    internal const string Version = "filled-marker-vertical-chords-v1";
    // The classifier's 32-pixel patch limits the size of the local glyph it can support.
    private const int MaximumRadius = 16;
    private readonly record struct Chord(int X, int FirstRow, int LastRow)
    {
        // Original-pixel samples have integer centers, including the last ink row.
        public double Middle => (FirstRow + (double)LastRow) / 2;
        public int Height => LastRow - FirstRow + 1;
        public bool InsideEdges(Chord wider) => FirstRow > wider.FirstRow && LastRow < wider.LastRow;
    }

    internal static double? Measure(OcrImage image, ClassifiedMarker classified)
    {
        MarkerCenter marker = classified.Marker;
        if (classified.Fill != MarkerFill.Filled || marker.SourceImage != MarkerSourceImage.Original ||
            marker.CoordinateSpace != MarkerContract.CoordinateSpace || !marker.Center.IsFinite ||
            !double.IsFinite(marker.Radius) || marker.Radius is < 1 or > MaximumRadius ||
            image.SourceImage != OcrSourceImage.Original || image.OriginalToImage != OcrFrameTransform.Identity ||
            image.CoordinateSpace != OcrContract.CoordinateSpace || image.Width <= 0 || image.Height <= 0 ||
            image.Stride < image.Width || (long)image.Stride * image.Height != image.Pixels.Length)
            return null;

        double x = marker.Center.X, y = marker.Center.Y;
        int radius = (int)Math.Ceiling(marker.Radius);
        int verticalReach = 2 * radius + 1;
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return null;
        int seedX = (int)Math.Round(x), seedY = (int)Math.Round(y);
        if (seedX < radius || seedX >= image.Width - radius || seedY < verticalReach || seedY >= image.Height - verticalReach)
            return null;
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        if (pixels[seedY * image.Stride + seedX] >= 128) return null;

        var chords = new List<Chord>(2 * radius + 1);
        for (int column = seedX - radius; column <= seedX + radius; column++)
        {
            Chord? chord = SingleBoundedRun(pixels, image.Stride, column, seedY - verticalReach, seedY + verticalReach, y, marker.Radius);
            if (chord.HasValue) chords.Add(chord.Value);
        }
        if (chords.Count < 3) return null;

        // Both measured boundaries are quantized to whole pixels. Connecting strokes
        // may contaminate a flank, so require a majority agreeing within one pixel.
        double middle = MedianMidpoint(chords);
        Chord[] supported = chords.Where(chord => Math.Abs(chord.Middle - middle) <= 1).ToArray();
        if (supported.Length * 2 <= chords.Count) return null;
        double measured = MedianMidpoint(supported);
        if (!supported.Any(chord => chord.X == seedX && chord.FirstRow <= seedY && seedY <= chord.LastRow &&
            chord.FirstRow <= measured && measured <= chord.LastRow)) return null;

        int maximum = supported.Max(chord => chord.Height);
        Chord[] widest = supported.Where(chord => chord.Height == maximum).ToArray();
        Chord left = widest[0], right = widest[^1];
        // One continuous core, with both edges receding on both sides. A flat
        // stroke, open symbol, triangular wedge or pair of competing cores cannot suffice.
        if (right.X - left.X + 1 != widest.Length ||
            !supported.Any(chord => chord.X < left.X && chord.InsideEdges(left)) ||
            !supported.Any(chord => chord.X > right.X && chord.InsideEdges(right)))
            return null;
        return measured;
    }

    private static Chord? SingleBoundedRun(ReadOnlySpan<byte> pixels, int stride, int x, int top, int bottom, double seedY, double radius)
    {
        Chord? found = null;
        int row = top;
        while (row <= bottom)
        {
            if (pixels[row * stride + x] >= 128) { row++; continue; }
            int start = row;
            while (row <= bottom && pixels[row * stride + x] < 128) row++;
            var chord = new Chord(x, start, row - 1);
            if (start == top || row > bottom || chord.Height < 2 || Math.Abs(chord.Middle - seedY) > radius) continue;
            if (found.HasValue) return null;
            found = chord;
        }
        return found;
    }

    private static double MedianMidpoint(IEnumerable<Chord> chords)
    {
        double[] centers = chords.Select(chord => chord.Middle).Order().ToArray();
        return (centers[(centers.Length - 1) / 2] + centers[centers.Length / 2]) / 2;
    }
}
