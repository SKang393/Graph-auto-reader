// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using OpenCvSharp;

namespace GraphReader.App.Integration.MarkerExemplar;

/// <summary>Single-scale image similarity with proposal centers confined to the plot.</summary>
internal static class MarkerExemplarMatcher
{
    internal static IReadOnlyList<MarkerExemplarCandidate> Find(MarkerExemplarRequest request,
        byte[] grayscale, CancellationToken token)
    {
        if (!MarkerExemplarValidation.ValidMetadata(request) || grayscale.Length != checked(request.Width * request.Height))
            throw new MarkerExemplarException(MarkerExemplarReasonCodes.InvalidInput);
        token.ThrowIfCancellationRequested();
        Cv2.SetNumThreads(1);
        using var original = Mat.FromPixelData(request.Height, request.Width, MatType.CV_8UC1, grayscale);
        MarkerExemplarRectangle exemplar = request.Exemplar, plot = request.Plot;
        using var template = new Mat(original, new Rect(exemplar.X, exemplar.Y, exemplar.Width, exemplar.Height));
        Cv2.MeanStdDev(template, out _, out Scalar deviation);
        if (!double.IsFinite(deviation.Val0) || deviation.Val0 <= 0)
            throw new MarkerExemplarException(MarkerExemplarReasonCodes.UninformativeExemplar);
        double offsetX = request.SelectedPoint.X - exemplar.X, offsetY = request.SelectedPoint.Y - exemplar.Y;
        int x0 = Math.Max(0, (int)Math.Ceiling(plot.X - offsetX));
        int y0 = Math.Max(0, (int)Math.Ceiling(plot.Y - offsetY));
        int x1 = Math.Min(request.Width - exemplar.Width, (int)Math.Floor((long)plot.X + plot.Width - 1 - offsetX));
        int y1 = Math.Min(request.Height - exemplar.Height, (int)Math.Floor((long)plot.Y + plot.Height - 1 - offsetY));
        if (x1 < x0 || y1 < y0) return Array.Empty<MarkerExemplarCandidate>();
        using var search = new Mat(original, new Rect(x0, y0, x1 - x0 + exemplar.Width, y1 - y0 + exemplar.Height));
        using var correlation = new Mat();
        // Native matching cannot cooperatively cancel. The isolated worker has a hard five-second watchdog.
        Cv2.MatchTemplate(search, template, correlation, TemplateMatchModes.CCoeffNormed);
        token.ThrowIfCancellationRequested();
        correlation.GetArray(out float[] scores);
        int width = correlation.Width, height = correlation.Height;
        double suppression = Math.Max(2, Math.Min(exemplar.Width, exemplar.Height) / 2d);
        var retained = new List<MarkerExemplarCandidate>();
        // Keep memory bounded even for repetitive/degenerate pages; overflow is visible, never truncated silently.
        var peaks = new List<(int X, int Y, float Score)>();
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                float score = scores[index];
                if (!float.IsFinite(score) || score < ProductionMarkerTemplateRecovery.MinimumSimilarity) continue;
                bool peak = true;
                for (int yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1) && peak; yy++)
                    for (int xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++)
                    {
                        int other = yy * width + xx;
                        if (scores[other] > score || scores[other] == score && other < index)
                        { peak = false; break; }
                    }
                if (!peak) continue;
                if (peaks.Count >= 4096) throw new MarkerExemplarException(MarkerExemplarReasonCodes.TooManyCandidates);
                peaks.Add((x, y, score));
            }
        }
        foreach (var peak in peaks.OrderByDescending(static value => value.Score).ThenBy(static value => value.Y).ThenBy(static value => value.X))
        {
            token.ThrowIfCancellationRequested();
            var center = new MarkerExemplarPoint(x0 + peak.X + offsetX, y0 + peak.Y + offsetY);
            if (!MarkerExemplarGeometry.ContainsCenter(plot, center) ||
                MarkerExemplarValidation.DistanceSquared(center, request.SelectedPoint) <= suppression * suppression ||
                retained.Any(item => MarkerExemplarValidation.DistanceSquared(center, item.Center) <= suppression * suppression)) continue;
            if (retained.Count == MarkerExemplarValidation.MaximumCandidates)
                throw new MarkerExemplarException(MarkerExemplarReasonCodes.TooManyCandidates);
            retained.Add(new(retained.Count.ToString(CultureInfo.InvariantCulture), center, Math.Clamp(peak.Score, 0, 1)));
        }
        return retained.AsReadOnly();
    }
}

internal sealed class MarkerExemplarException(string reason) : Exception(reason)
{
    internal string Reason { get; } = reason;
}
