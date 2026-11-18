// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Checks a classified center for solid round ink on the original grayscale plane.</summary>
internal static class OriginalPixelDiskRadius
{
    private static readonly (double X, double Y)[] Directions = Enumerable.Range(0, 16)
        .Select(i => (Math.Cos(i * Math.Tau / 16), Math.Sin(i * Math.Tau / 16))).ToArray();

    internal static int Measure(OcrImage image, MarkerCenter marker, CancellationToken cancellationToken)
    {
        // This check is applied to normalized detector proposals, before template recovery.
        if (!double.IsFinite(marker.Radius) || marker.Radius <= 0 ||
            marker.Radius > NormalizedMarkerProposalPostprocessContract.MaximumRadius) return 0;
        int x = (int)Math.Round(marker.Center.X), y = (int)Math.Round(marker.Center.Y);
        int shift = (int)Math.Ceiling(marker.Radius / 2);
        int reach = (int)Math.Ceiling(2 * marker.Radius) + 2;
        if (x - shift - reach < 0 || y - shift - reach < 0 ||
            x + shift + reach >= image.Width || y + shift + reach >= image.Height) return 0;

        int bestRadius = 0, bestAgreement = 0;
        double bestDistance = double.PositiveInfinity;
        Span<int> lengths = stackalloc int[16];
        for (int cy = y - shift; cy <= y + shift; cy++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int cx = x - shift; cx <= x + shift; cx++)
            {
                if (!Ink(cx, cy)) continue;
                for (int i = 0; i < Directions.Length; i++)
                {
                    var direction = Directions[i];
                    int step = 1;
                    while (step <= reach && Ink(cx + (int)Math.Round(step * direction.X),
                        cy + (int)Math.Round(step * direction.Y))) step++;
                    lengths[i] = step - 1;
                }
                lengths.Sort();
                int radius = lengths[lengths.Length / 2];
                if (radius < 3 || radius >= reach - 1 || lengths[0] < radius - 1) continue;

                // A thin connecting line can extend a few rays. At least three quarters
                // must end on the measured circumference within one raster sample.
                int agreement = 0;
                foreach (int length in lengths)
                    if (Math.Abs(radius - length) <= 1) agreement++;
                if (agreement < 12 || agreement < bestAgreement) continue;
                double distance = Math.Pow(cx - marker.Center.X, 2) + Math.Pow(cy - marker.Center.Y, 2);
                if (agreement == bestAgreement && distance >= bestDistance) continue;
                if (!HasSolidCore(cx, cy, radius - 1)) continue;
                bestRadius = radius;
                bestAgreement = agreement;
                bestDistance = distance;
            }
        }
        return bestRadius;

        bool Ink(int px, int py) => image.Pixels.Span[py * image.Stride + px] < 128;

        bool HasSolidCore(int cx, int cy, int r)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dy * dy <= r * r && !Ink(cx + dx, cy + dy)) return false;
            return true;
        }
    }
}
