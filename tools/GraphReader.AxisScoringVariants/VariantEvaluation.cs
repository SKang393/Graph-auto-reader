// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Axis;

namespace GraphReader.AxisScoringVariants;

public sealed record SyntheticVariant(string Name, AxisGeometryRequest Request, PlotPolygon Expected);
public sealed record CandidateMeasurement(int Rank, double Score, double MaximumCornerErrorPixels, double MeanCornerErrorPixels);
public sealed record VariantResult(string Name, string Status, string? ErrorCode, bool NeedsReview,
    IReadOnlyList<string> ReviewReasons, double? MaximumCornerErrorPixels, double? MeanCornerErrorPixels,
    bool ObserverMatchesSelection, IReadOnlyList<CandidateMeasurement> Candidates);

public static class VariantEvaluation
{
    public static IReadOnlyList<SyntheticVariant> Generate()
    {
        var variants = new List<SyntheticVariant>();
        foreach (int offset in new[] { 0, 10, 20 })
        {
            foreach (string kind in new[] { "clean", "short", "frame", "divider", "scatter", "disconnected", "fragmented", "competing-short", "competing-long" })
                variants.Add(Create(kind, offset));
        }
        return variants.AsReadOnly();
    }

    public static async Task<VariantResult[]> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<VariantResult>();
        foreach (SyntheticVariant variant in Generate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observer = new Observer();
            try
            {
                AxisGeometryResult output = await new AxisGeometryDetector().DetectAsync(
                    variant.Request with { CandidateObserver = observer }, cancellationToken).ConfigureAwait(false);
                (double maximum, double mean) = Errors(output.PlotPolygon, variant.Expected);
                CandidateMeasurement[] measurements = observer.Candidates.Select(candidate =>
                {
                    (double max, double average) = Errors(new PlotPolygon(candidate.Intersection,
                        candidate.BottomRight, candidate.TopRight, candidate.TopLeft), variant.Expected);
                    return new CandidateMeasurement(candidate.Rank, candidate.Score, max, average);
                }).ToArray();
                bool parity = observer.Candidates.Count > 0 &&
                    Errors(output.PlotPolygon, new PlotPolygon(observer.Candidates[0].Intersection,
                        observer.Candidates[0].BottomRight, observer.Candidates[0].TopRight,
                        observer.Candidates[0].TopLeft)).Maximum < 1e-8;
                results.Add(new VariantResult(variant.Name, "completed", null, output.Uncertainty.NeedsReview,
                    output.Uncertainty.Reasons.ToArray(), maximum, mean, parity, measurements));
            }
            catch (AxisGeometryDetectionException exception)
            {
                results.Add(new VariantResult(variant.Name, "rejected", exception.Code, true, [], null, null, false, []));
            }
        }
        return results.ToArray();
    }

    private static SyntheticVariant Create(string kind, int offset)
    {
        double left = 100 + offset, bottom = 320, top = kind is "short" or "competing-long" ? 150 : 60;
        double right = kind is "short" or "competing-long" ? 520 + offset : 700 + offset;
        var lines = new List<GeometryLineCandidate>();
        void Line(string id, double x1, double y1, double x2, double y2, double strength = 1, LinePatternHint pattern = LinePatternHint.Solid) =>
            lines.Add(new GeometryLineCandidate(id, new GeometryLineSegment(new PixelPoint(x1, y1), new PixelPoint(x2, y2)),
                LineCandidateSource.RecordedFixture, strength, 1, pattern));
        if (kind == "fragmented")
        {
            double middle = (left + right) / 2;
            Line("x-left", left, bottom, middle, bottom);
            Line("x-right", middle + 1, bottom, right, bottom);
            Line("y-lower", left, bottom, left, 190);
            Line("y-upper", left, 189, left, top);
        }
        else
        {
            Line("x-axis", left, bottom, right, bottom, kind.StartsWith("competing-", StringComparison.Ordinal) ? 0.65 : 1);
            Line("y-axis", left, kind.StartsWith("competing-", StringComparison.Ordinal) ? bottom + 6 : bottom, left, top);
        }
        if (kind == "competing-short")
            Line("parallel-shorter-competitor", left, bottom + 6, right - 55, bottom + 6, 1);
        if (kind == "competing-long")
            Line("parallel-longer-competitor", left, bottom + 6, 700 + offset, bottom + 6, 1);
        for (int index = 1; index <= 5; index++)
        {
            double x = left + ((right - left) * index / 6);
            Line($"x-tick-{index}", x, bottom - 4, x, bottom + 4);
            double y = top + ((bottom - top) * index / 6);
            Line($"y-tick-{index}", left - 4, y, left + 4, y);
        }
        if (kind == "frame")
        {
            Line("frame-top", left, top, right, top, 0.8);
            Line("frame-right", right, top, right, bottom, 0.8);
        }
        if (kind == "divider")
        {
            for (int n = 0; n < 10; n++)
                Line($"divider-{n}", left + 300, top + n * 23, left + 300, top + n * 23 + 9, 0.8, LinePatternHint.Dotted);
        }
        if (kind == "scatter")
        {
            for (int n = 0; n < 12; n++)
            {
                double x = left + 40 + 41 * n, y = top + 30 + (n * 37 % 180);
                Line($"scatter-h-{n}", x - 3, y, x + 3, y, 0.7);
                Line($"scatter-v-{n}", x, y - 3, x, y + 3, 0.7);
            }
        }
        if (kind == "disconnected")
        {
            Line("long-detached-horizontal", 15, 370, 780, 370, 0.95);
            Line("long-detached-vertical", 40, 350, 40, 20, 0.95);
        }
        var expected = new PlotPolygon(new PixelPoint(left, bottom), new PixelPoint(right, bottom),
            new PixelPoint(right, top), new PixelPoint(left, top));
        return new SyntheticVariant($"{kind}-offset-{offset}", new AxisGeometryRequest(800, 400, lines.AsReadOnly()), expected);
    }

    private static (double Maximum, double Mean) Errors(PlotPolygon actual, PlotPolygon expected)
    {
        double[] distances = actual.Points.Zip(expected.Points,
            static (a, b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2))).ToArray();
        return (distances.Max(), distances.Average());
    }

    private sealed class Observer : IAxisGeometryCandidateObserver
    {
        public IReadOnlyList<AxisGeometryCandidateSnapshot> Candidates { get; private set; } = [];
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) => Candidates = candidates;
    }
}
