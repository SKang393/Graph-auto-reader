// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class NativeDividerCandidateDiagnosticTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BottomTouchingDividerProducesDistinctReviewHypotheses(bool withDivider)
    {
        const int width = 800, height = 400;
        byte[] pixels = Enumerable.Repeat(byte.MaxValue, width * height).ToArray();
        for (int x = 100; x <= 700; x++) pixels[300 * width + x] = 0;
        for (int y = 50; y <= 300; y++)
        {
            pixels[y * width + 100] = 0;
            if (withDivider) pixels[y * width + 400] = 0;
        }
        byte[] original = pixels.ToArray();
        Cv2.SetNumThreads(1);
        Assert.AreEqual(1, Cv2.GetNumThreads());
        IReadOnlyList<GeometryLineCandidate> lines = await new OpenCvLineCandidateProvider()
            .DetectLinesAsync(new(width, height, width, pixels), CancellationToken.None);
        var observer = new Observer();
        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(
            new AxisGeometryRequest(width, height, lines)
            {
                CandidateObserver = observer,
                MaximumExaminedPairs = 128,
            }, CancellationToken.None);

        // These are observations of the worker's predicates, not worker execution.
        PixelPoint[] corners = [result.PlotPolygon.BottomLeft, result.PlotPolygon.BottomRight,
            result.PlotPolygon.TopRight, result.PlotPolygon.TopLeft];
        bool invalidCorners = corners.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height);
        bool rotated = Math.Abs(corners[0].Y - corners[1].Y) > 1 || Math.Abs(corners[3].Y - corners[2].Y) > 1 ||
            Math.Abs(corners[0].X - corners[3].X) > 1 || Math.Abs(corners[1].X - corners[2].X) > 1;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            withDivider,
            line_count = lines.Count,
            ranked_count = observer.Candidates.Count,
            candidates = observer.Candidates.Select(candidate => new
            {
                candidate.Rank, candidate.Score, candidate.Intersection,
                candidate.BottomRight, candidate.TopLeft, candidate.TopRight,
            }),
            phase_dividers = result.PhaseDividers.Count,
            review_reasons = result.Uncertainty.Reasons,
            worker_predicate_observation_only = new
            {
                candidate_count_rejects = observer.Candidates.Count != 1,
                invalidCorners, rotated,
                nonfinite_confidence = !double.IsFinite(result.Confidence),
            },
        }));

        Assert.AreEqual(100d, result.PlotPolygon.BottomLeft.X, 2d);
        Assert.AreEqual(300d, result.PlotPolygon.BottomLeft.Y, 2d);
        Assert.AreEqual(700d, result.PlotPolygon.BottomRight.X, 2d);
        Assert.AreEqual(50d, result.PlotPolygon.TopLeft.Y, 2d);
        Assert.IsFalse(invalidCorners);
        Assert.IsFalse(rotated);
        Assert.IsTrue(double.IsFinite(result.Confidence));
        CollectionAssert.AreEqual(original, pixels);
        if (withDivider)
        {
            Assert.IsGreaterThan(1, observer.Candidates.Count);
            Assert.IsTrue(observer.Candidates.Any(candidate => Math.Abs(candidate.Intersection.X - 400) <= 2));
            Assert.HasCount(1, result.PhaseDividers);
        }
        else
        {
            Assert.HasCount(1, observer.Candidates);
            Assert.HasCount(0, result.PhaseDividers);
        }
    }

    private sealed class Observer : IAxisGeometryCandidateObserver
    {
        internal IReadOnlyList<AxisGeometryCandidateSnapshot> Candidates { get; private set; } = [];
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) => Candidates = candidates;
    }
}
