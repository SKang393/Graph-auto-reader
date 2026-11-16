// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Integration.Tests.Validation.Core.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Integration.Tests.Validation.Tests;

[TestClass]
public sealed class ValidationCsvCorrespondenceTests
{
    private static readonly CsvPoint[] Expected =
    [new("e1", 1, 10, "A", "one"), new("e2", 1, 90, "B", "two")];

    [TestMethod]
    public void ValidationCsvReorderedSeriesPreserveScores()
    {
        var actual = Expected.Reverse().Select(row => row with { RowId = "a" + row.RowId }).ToArray();
        var score = Calculate(Expected, actual).Value;
        Assert.AreEqual(1d, score.PointPrecision);
        Assert.AreEqual(1d, score.PointRecall);
        Assert.AreEqual(0d, score.YMeanAbsoluteError);
        Assert.AreEqual(1d, score.ExactPhaseCodeAccuracy);
    }

    [TestMethod]
    public void ValidationCsvSeriesSwapCannotHideWrongValues()
    {
        var actual = Expected.Select(row => row with { SeriesId = row.SeriesId == "one" ? "two" : "one" }).ToArray();
        var score = Calculate(Expected, actual).Value;
        Assert.AreEqual(0d, score.PointPrecision);
        Assert.AreEqual(80d, score.YMeanAbsoluteError);
        Assert.AreEqual(0d, score.ExactPhaseCodeAccuracy);
    }

    [TestMethod]
    public void ValidationCsvMissingSeriesMakesSharedXExplicitlyAmbiguous()
    {
        var rows = Expected.Select(row => row with { SeriesId = null }).ToArray();
        Assert.ThrowsExactly<ArgumentException>(() => Calculate(rows, rows.Reverse().ToArray()));
    }

    [TestMethod]
    public void ValidationCsvPartialOrBlankSeriesIdentityIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Calculate(Expected, [Expected[0] with { SeriesId = null }]));
        Assert.ThrowsExactly<ArgumentException>(() => Calculate(Expected, [Expected[0] with { SeriesId = " " }]));
    }

    [TestMethod]
    public void ValidationCsvAmbiguousToleranceIsRejectedInEitherDirection()
    {
        CsvPoint[] two = [new("e1", 1, 10, "A"), new("e2", 1.1, 20, "A")];
        CsvPoint[] one = [new("a1", 1.05, 10, "A")];
        Assert.ThrowsExactly<ArgumentException>(() => Calculate(two, one, 0.1));
        Assert.ThrowsExactly<ArgumentException>(() => Calculate(one, two, 0.1));
    }

    [TestMethod]
    public void ValidationCsvSingleSeriesLegacyInputStillWorks()
    {
        CsvPoint[] rows = [new("one", 1, 10, "A"), new("two", 2, 90, "B")];
        var score = Calculate(rows, rows.Reverse().ToArray()).Value;
        Assert.AreEqual(0d, score.YMeanAbsoluteError);
        Assert.AreEqual(1d, score.ExactPhaseCodeAccuracy);
    }

    private static MetricOutcome<CsvMetrics> Calculate(
        IReadOnlyList<CsvPoint> expected, IReadOnlyList<CsvPoint> actual, double tolerance = 1e-9) =>
        CsvMetricsCalculator.Calculate(new(new("csv", "correspondence"), expected, actual, 100, tolerance));
}
