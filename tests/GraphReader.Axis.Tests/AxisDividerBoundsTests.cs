// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class AxisDividerBoundsTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task SlantedDividerStaysInsideNarrowPlot(bool leanRight, bool skewAxes)
    {
        // Every observed endpoint is inside the image and plot. Extending the
        // short slanted segment to the full plot height used to leave both.
        PixelPoint Map(double x, double y) => new(x, y + (skewAxes ? (x - 80d) * 0.05d : 0d));
        GeometryLineCandidate Line(string id, double x1, double y1, double x2, double y2) =>
            new(id, new(Map(x1, y1), Map(x2, y2)), LineCandidateSource.RecordedFixture,
                PatternHint: LinePatternHint.Solid);
        double lowerX = leanRight ? 480d : 140d;
        var request = new AxisGeometryRequest(600, 4484,
        [
            Line("x-axis", 80, 3600, 540, 3600),
            Line("y-axis", 80, 3600, 80, 900),
            Line("slanted-divider", lowerX, 2300, 310, 1000),
        ]);

        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);

        Assert.AreEqual(1, result.PhaseDividers.Count);
        GeometryLineSegment line = result.PhaseDividers[0].Line;
        Assert.AreEqual(leanRight ? 540d : 80d, line.Start.X, 1e-8);
        foreach (PixelPoint point in new[] { line.Start, line.End })
        {
            double unskewedY = point.Y - (skewAxes ? (point.X - 80d) * 0.05d : 0d);
            Assert.IsTrue(point.X >= 80d - 1e-8 && point.X <= 540d + 1e-8);
            Assert.IsTrue(unskewedY >= 900d - 1e-8 && unskewedY <= 3600d + 1e-8);
            // Clipping must retain the observed line, not clamp X independently
            // and thereby invent a different slope.
            double expectedX = lowerX + (unskewedY - 2300d) * (310d - lowerX) / (1000d - 2300d);
            Assert.AreEqual(expectedX, point.X, 1e-8);
        }
        Assert.IsTrue(result.XAxis.SupportingCandidateIds.Contains("x-axis"));
        Assert.IsTrue(result.YAxis.SupportingCandidateIds.Contains("y-axis"));
    }
}
