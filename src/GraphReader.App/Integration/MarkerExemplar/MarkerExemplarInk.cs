// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Services;
using OpenCvSharp;

namespace GraphReader.App.Integration.MarkerExemplar;

internal static class MarkerExemplarInk
{
    // Use a color footprint only for a strongly chromatic, solid center with
    // visible surround. Otherwise let the worker use its ordinary grayscale path.
    internal static bool TryCreateMask(MarkerExemplarRequest request, byte[] bgra, byte[] mask, CancellationToken token)
    {
        var box = request.Exemplar;
        int x = (int)Math.Floor(request.SelectedPoint.X + .5), y = (int)Math.Floor(request.SelectedPoint.Y + .5);
        if (x <= box.X || x >= box.X + box.Width - 1 || y <= box.Y || y >= box.Y + box.Height - 1) return false;
        int center = 4 * (y * request.Width + x);
        int b = bgra[center], g = bgra[center + 1], r = bgra[center + 2];
        if (Math.Max(b, Math.Max(g, r)) - Math.Min(b, Math.Min(g, r)) < 64) return false;

        // Accept colors within half the sampled color’s Euclidean distance to
        // the neutral RGB axis. Even the closest neutral gray is outside this ball.
        int channelDifferences = (b - g) * (b - g) + (g - r) * (g - r) + (r - b) * (r - b);
        bool IsInk(int i)
        {
            int db = bgra[4 * i] - b, dg = bgra[4 * i + 1] - g, dr = bgra[4 * i + 2] - r;
            return 12 * (db * db + dg * dg + dr * dr) <= channelDifferences;
        }
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (!IsInk((y + dy) * request.Width + x + dx)) return false;

        int borderInk = 0, perimeter = 2 * (box.Width + box.Height) - 4;
        for (int dy = 0; dy < box.Height; dy++)
            for (int dx = 0; dx < box.Width; dx++)
                if ((dy == 0 || dy == box.Height - 1 || dx == 0 || dx == box.Width - 1) &&
                    IsInk((box.Y + dy) * request.Width + box.X + dx))
                    borderInk++;
        if (2 * borderInk >= perimeter) return false;

        for (int i = 0; i < mask.Length; i++)
        {
            if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
            mask[i] = IsInk(i) ? (byte)1 : (byte)0;
        }
        return true;
    }

    internal static float[] Score(Mat search, Mat template, CancellationToken token)
    {
        int pixels = template.Width * template.Height;
        int seedInk = Cv2.CountNonZero(template);
        using var counts = new Mat();
        Cv2.MatchTemplate(search, template, counts, TemplateMatchModes.CCorr);
        counts.GetArray(out float[] overlap);
        token.ThrowIfCancellationRequested();
        using var ones = new Mat(template.Size(), MatType.CV_8UC1, Scalar.All(1));
        Cv2.MatchTemplate(search, ones, counts, TemplateMatchModes.CCorr);
        counts.GetArray(out float[] areas);
        for (int i = 0; i < overlap.Length; i++)
        {
            if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
            // Binary products and sums are counts. Remove floating-point FFT roundoff.
            float common = MathF.Round(overlap[i]), area = MathF.Round(areas[i]);
            float background = pixels - (seedInk + area - common);
            // Each footprint and each surround must agree in both directions.
            // A small shape inside a larger one cannot score on containment alone.
            overlap[i] = Math.Min(common / Math.Max(seedInk, area), background / Math.Max(pixels - seedInk, pixels - area));
        }
        return overlap;
    }
}
