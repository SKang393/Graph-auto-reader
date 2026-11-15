// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class RegularGridFinalizationTests
{
    private static readonly double[] Regular = [200, 300, 400, 500, 600];
    private static readonly double[] Irregular = [200, 250, 300, 470, 600];
    private static readonly double[] Boundary = [200, 288, 400]; // Gaps88/112 give CV0.12.
    private static readonly MethodInfo Spacing = typeof(AxisGeometryDetector).GetMethod("IsRegularGridSpacing",
        BindingFlags.NonPublic | BindingFlags.Static)!;

    [TestMethod]
    [DataRow("regular", false)]
    [DataRow("regular", true)]
    [DataRow("irregular", false)]
    [DataRow("irregular", true)]
    [DataRow("boundary_below", false)]
    [DataRow("boundary_below", true)]
    [DataRow("boundary_exact", false)]
    [DataRow("boundary_exact", true)]
    [DataRow("boundary_above", false)]
    [DataRow("boundary_above", true)]
    public async Task MatchesOldWindowUnionAndDownstreamGeometry(string name, bool reverse)
    {
        double[] positions = name switch { "regular" => Regular, "irregular" => Irregular, _ => Boundary };
        double coefficient = name switch {
            "boundary_below" => Math.BitDecrement(.12), "boundary_above" => Math.BitIncrement(.12), _ => .12,
        };
        var options = new AxisGeometryOptions { GridSpacingCoefficientOfVariation = coefficient };
        var fixture = new AxisFixtureBuilder().CleanAxes();
        foreach (double position in reverse ? positions.Reverse() : positions) fixture.VerticalGrid(position);
        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(fixture.Build(options));
        HashSet<double> expected = OldWindowUnion(positions, options);

        AssertPositions(expected.Order(), result.AmbiguousGridOrDividers.Select(item => item.Line.Start.X));
        AssertPositions(positions.Where(position => !expected.Contains(position)).Order(),
            result.PhaseDividers.Select(item => item.Line.Start.X));
        Assert.AreEqual(expected.Count, result.Diagnostics.GridOrFrameExclusionCount);
        Assert.AreEqual(expected.Count > 0, result.Uncertainty.Reasons.Contains("grid_or_phase_divider_ambiguous", StringComparer.Ordinal));
        Assert.AreEqual(new PixelPoint(100, 300), result.PlotPolygon.BottomLeft);
        Assert.AreEqual(new PixelPoint(700, 300), result.PlotPolygon.BottomRight);
        Assert.AreEqual(new PixelPoint(100, 50), result.PlotPolygon.TopLeft);
        Assert.AreEqual(0, result.Ticks.Count);
        foreach (var item in result.AmbiguousGridOrDividers)
        {
            Assert.AreEqual(item.Line.Start.X, item.Line.End.X);
            Assert.AreEqual(50d, Math.Min(item.Line.Start.Y, item.Line.End.Y));
            Assert.AreEqual(300d, Math.Max(item.Line.Start.Y, item.Line.End.Y));
        }
    }

    private static void AssertPositions(IEnumerable<double> expected, IEnumerable<double> actual)
    {
        double[] expectedArray = expected.ToArray(), actualArray = actual.ToArray();
        Assert.AreEqual(expectedArray.Length, actualArray.Length);
        for (int index = 0; index < expectedArray.Length; index++)
            Assert.AreEqual(expectedArray[index], actualArray[index], 1e-9,
                "Fitted line coordinates can differ from owned integer positions by floating-point roundoff.");
    }

    [TestMethod]
    public void FullRunSpacingHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        double[] gaps = Enumerable.Repeat(100d, 1024).ToArray();
        TargetInvocationException error = Assert.ThrowsExactly<TargetInvocationException>(() =>
            Spacing.Invoke(null, [gaps, .12, cancellation.Token]));
        Assert.IsInstanceOfType<OperationCanceledException>(error.InnerException);
    }

    // Literal pre-change window algorithm on these owned axis-aligned positions.
    // This deliberately retains its arithmetic and exhaustive subset union.
    private static HashSet<double> OldWindowUnion(double[] input, AxisGeometryOptions options)
    {
        double[] sorted = input.Order().ToArray();
        var result = new HashSet<double>();
        for (int start = 0; start <= sorted.Length - options.GridMinimumAlignedLines; start++)
        {
            for (int end = start + options.GridMinimumAlignedLines; end <= sorted.Length; end++)
            {
                double[] run = sorted[start..end];
                double[] gaps = run.Zip(run.Skip(1), (left, right) => right - left).ToArray();
                double mean = gaps.Average();
                if (mean <= 0d) continue;
                double variance = gaps.Sum(gap => (gap - mean) * (gap - mean)) / gaps.Length;
                if (Math.Sqrt(variance) / mean <= options.GridSpacingCoefficientOfVariation)
                    foreach (double position in run) result.Add(position);
            }
        }
        return result;
    }
}
