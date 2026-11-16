// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Integration.Tests.Validation.Core.Metrics;

public sealed record CsvPoint(string RowId, double XValue, double YValue, string PhaseCode, string? SeriesId = null);

public sealed record CsvMetricInput(
    MetricCaseIdentity Identity,
    IReadOnlyList<CsvPoint> Expected,
    IReadOnlyList<CsvPoint> Actual,
    double AxisYRange,
    double XTolerance = 1e-9,
    double YTolerance = 1e-9);

public sealed record CsvRowComparison(
    string ExpectedRowId,
    string ActualRowId,
    double XError,
    double YError,
    bool PhaseCodeMatches);

public sealed record CsvMetrics(
    int ExpectedPointCount,
    int ActualPointCount,
    int TruePositivePoints,
    int FalsePositivePoints,
    int FalseNegativePoints,
    double PointPrecision,
    double PointRecall,
    double YMeanAbsoluteError,
    double YMeanAbsoluteErrorPercentAxisRange,
    double ExactPhaseCodeAccuracy,
    IReadOnlyList<CsvRowComparison> RowComparisons);

public static class CsvMetricsCalculator
{
    public static MetricOutcome<CsvMetrics> Calculate(CsvMetricInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        MetricGuard.Identity(input.Identity);
        ArgumentNullException.ThrowIfNull(input.Expected);
        ArgumentNullException.ThrowIfNull(input.Actual);
        MetricGuard.NonNegativeFinite(input.XTolerance, nameof(input.XTolerance));
        MetricGuard.NonNegativeFinite(input.YTolerance, nameof(input.YTolerance));
        MetricGuard.NonNegativeFinite(input.AxisYRange, nameof(input.AxisYRange));

        if (input.AxisYRange == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "The axis y-range must be greater than zero.");
        }

        ValidateRows(input.Expected, nameof(input.Expected));
        ValidateRows(input.Actual, nameof(input.Actual));

        ValidateCorrespondence(input);

        var xAligned = OneToOneMatcher.MatchByCost(
            input.Expected.Count,
            input.Actual.Count,
            (expectedIndex, actualIndex) =>
            {
                var difference = Math.Abs(
                    input.Expected[expectedIndex].XValue - input.Actual[actualIndex].XValue);
                return SameSeries(input.Expected[expectedIndex], input.Actual[actualIndex]) &&
                    difference <= input.XTolerance ? difference : null;
            });
        var pointMatches = OneToOneMatcher.MatchByCost(
            input.Expected.Count,
            input.Actual.Count,
            (expectedIndex, actualIndex) =>
            {
                var xDifference = Math.Abs(
                    input.Expected[expectedIndex].XValue - input.Actual[actualIndex].XValue);
                var yDifference = Math.Abs(
                    input.Expected[expectedIndex].YValue - input.Actual[actualIndex].YValue);
                if (!SameSeries(input.Expected[expectedIndex], input.Actual[actualIndex]) ||
                    xDifference > input.XTolerance || yDifference > input.YTolerance)
                {
                    return null;
                }

                var normalizedX = input.XTolerance == 0 ? 0 : xDifference / input.XTolerance;
                var normalizedY = input.YTolerance == 0 ? 0 : yDifference / input.YTolerance;
                return Math.Sqrt((normalizedX * normalizedX) + (normalizedY * normalizedY));
            });

        var comparisons = xAligned.Matches.Select(match =>
        {
            var expected = input.Expected[match.ExpectedIndex];
            var actual = input.Actual[match.ActualIndex];
            return new CsvRowComparison(
                expected.RowId,
                actual.RowId,
                Math.Abs(actual.XValue - expected.XValue),
                Math.Abs(actual.YValue - expected.YValue),
                string.Equals(expected.PhaseCode, actual.PhaseCode, StringComparison.Ordinal));
        }).ToArray();

        var truePositives = pointMatches.Matches.Count;
        var falsePositives = input.Actual.Count - truePositives;
        var falseNegatives = input.Expected.Count - truePositives;
        var precision = MetricMath.Ratio(truePositives, input.Actual.Count, whenEmpty: input.Expected.Count == 0 ? 1 : 0);
        var recall = MetricMath.Ratio(truePositives, input.Expected.Count, whenEmpty: input.Actual.Count == 0 ? 1 : 0);
        var yMae = comparisons.Length == 0 ? 0 : comparisons.Average(comparison => comparison.YError);
        var exactPhaseMatches = comparisons.Count(comparison => comparison.PhaseCodeMatches);
        var phaseAccuracy = MetricMath.Ratio(exactPhaseMatches, input.Expected.Count, whenEmpty: 1);
        var failures = CreateFailures(input, pointMatches, xAligned, comparisons);
        var score = new CsvMetrics(
            input.Expected.Count,
            input.Actual.Count,
            truePositives,
            falsePositives,
            falseNegatives,
            precision,
            recall,
            yMae,
            yMae * 100 / input.AxisYRange,
            phaseAccuracy,
            comparisons);

        return new MetricOutcome<CsvMetrics>(score, failures);
    }

    private static List<MetricFailure> CreateFailures(
        CsvMetricInput input,
        OneToOneMatchingResult pointMatches,
        OneToOneMatchingResult xAligned,
        IReadOnlyList<CsvRowComparison> comparisons)
    {
        var failures = new List<MetricFailure>();
        foreach (var expectedIndex in pointMatches.UnmatchedExpectedIndices)
        {
            failures.Add(new MetricFailure(
                input.Identity.Module,
                input.Identity.CaseId,
                "csv_point_recall",
                input.Expected[expectedIndex].RowId,
                "missing or outside tolerance",
                "No exported point matched the expected x and y values within tolerance."));
        }

        foreach (var actualIndex in pointMatches.UnmatchedActualIndices)
        {
            failures.Add(new MetricFailure(
                input.Identity.Module,
                input.Identity.CaseId,
                "csv_point_precision",
                "no extra point",
                input.Actual[actualIndex].RowId,
                "The exported point did not match an expected x and y value within tolerance."));
        }

        foreach (var expectedIndex in xAligned.UnmatchedExpectedIndices)
        {
            failures.Add(new MetricFailure(
                input.Identity.Module,
                input.Identity.CaseId,
                "csv_phase_code_accuracy",
                input.Expected[expectedIndex].PhaseCode,
                "missing",
                $"Expected CSV row '{input.Expected[expectedIndex].RowId}' has no x-aligned exported row."));
        }

        foreach (var comparison in comparisons.Where(item => !item.PhaseCodeMatches))
        {
            var expected = input.Expected.First(row => row.RowId == comparison.ExpectedRowId);
            var actual = input.Actual.First(row => row.RowId == comparison.ActualRowId);
            failures.Add(new MetricFailure(
                input.Identity.Module,
                input.Identity.CaseId,
                "csv_phase_code_accuracy",
                expected.PhaseCode,
                actual.PhaseCode,
                $"X-aligned CSV row '{actual.RowId}' has the wrong phase code."));
        }

        return failures;
    }

    private static bool SameSeries(CsvPoint expected, CsvPoint actual) =>
        string.Equals(expected.SeriesId, actual.SeriesId, StringComparison.Ordinal);

    private static void ValidateCorrespondence(CsvMetricInput input)
    {
        // Y values and phase labels are outcomes being measured, not alignment keys.
        // Reject ambiguous X matches instead of letting input order choose the score.
        var rows = input.Expected.Concat(input.Actual);
        if (rows.Any(row => row.SeriesId is not null) && rows.Any(row => row.SeriesId is null))
        {
            throw new ArgumentException("CSV series identity must be supplied for every row or omitted for every row.", nameof(input));
        }

        var actualDegrees = new int[input.Actual.Count];
        foreach (var expected in input.Expected)
        {
            var expectedDegree = 0;
            for (var i = 0; i < input.Actual.Count; i++)
            {
                if (SameSeries(expected, input.Actual[i]) &&
                    Math.Abs(expected.XValue - input.Actual[i].XValue) <= input.XTolerance)
                {
                    expectedDegree++;
                    actualDegrees[i]++;
                }
            }

            if (expectedDegree > 1)
            {
                throw new ArgumentException("Ambiguous CSV X correspondence. Supply stable series IDs or a narrower X tolerance; Y and phase scores cannot resolve alignment.", nameof(input));
            }
        }

        if (actualDegrees.Any(degree => degree > 1))
        {
            throw new ArgumentException("Ambiguous CSV X correspondence. Supply stable series IDs or a narrower X tolerance; Y and phase scores cannot resolve alignment.", nameof(input));
        }
    }

    private static void ValidateRows(IReadOnlyList<CsvPoint> rows, string parameterName)
    {
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (string.IsNullOrWhiteSpace(row.RowId) || string.IsNullOrWhiteSpace(row.PhaseCode))
            {
                throw new ArgumentException("CSV row IDs and phase codes must be non-empty.", parameterName);
            }

            if (row.SeriesId is not null && string.IsNullOrWhiteSpace(row.SeriesId))
            {
                throw new ArgumentException("CSV series IDs must be non-empty when supplied.", parameterName);
            }

            if (!identifiers.Add(row.RowId))
            {
                throw new ArgumentException($"CSV row ID '{row.RowId}' is duplicated.", parameterName);
            }

            MetricGuard.Finite(row.XValue, parameterName);
            MetricGuard.Finite(row.YValue, parameterName);
        }
    }
}
