// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.Export;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record WholeWorkflowTruthSeries(string SeriesKey);

internal sealed record WholeWorkflowTruthPoint(
    string PointKey,
    string SeriesKey,
    double SourcePixelX,
    double SourcePixelY,
    double GraphX,
    double GraphY,
    double ExpectedExportX,
    ExportMode ExpectedExportMode,
    string? AuthoritativePhaseCode);

internal sealed record WholeWorkflowTruthRelation(
    string TargetInterventionSeriesKey,
    string? SharedBaselineSeriesKey,
    IReadOnlyList<string> ApplicableProbeSeriesKeys);

/// <summary>
/// Truth is evaluator-only. A null relation collection means that artifact grouping truth is
/// unavailable. All-null phase codes mean that phase truth is unavailable. Partial phase truth
/// is rejected because it would silently narrow the denominator.
/// </summary>
internal sealed record WholeWorkflowTruthCase(
    string CaseKey,
    string SourceSha256,
    int SourceWidth,
    int SourceHeight,
    IReadOnlyList<WholeWorkflowTruthSeries> Series,
    IReadOnlyList<WholeWorkflowTruthPoint> Points,
    IReadOnlyList<WholeWorkflowTruthRelation>? Relations);

internal sealed record WholeWorkflowCsvArtifact(
    string FileName,
    string Sha256,
    int RowCount,
    string? WrittenPath,
    WholeWorkflowArtifactContent? Content = null);

internal sealed class WholeWorkflowArtifactContent
{
    private readonly byte[] bytes;

    internal WholeWorkflowArtifactContent(ReadOnlySpan<byte> content) => bytes = content.ToArray();

    internal byte[] CopyBytes() => (byte[])bytes.Clone();
}

internal sealed record WholeWorkflowCaseOutput(
    string CaseKey,
    string SourceSha256,
    bool WorkflowSucceeded,
    string? FailureCode,
    IReadOnlyList<WholeWorkflowCsvArtifact> Artifacts);

/// <summary>
/// Numeric tolerances are supplied by the caller's reviewed protocol. This evaluator does not
/// define or tighten an acceptance bar.
/// </summary>
internal sealed record WholeWorkflowEvaluationOptions(
    double SourcePixelMatchTolerance,
    double GraphXAbsoluteTolerance,
    double GraphYAbsoluteTolerance,
    bool RequireInMemoryArtifacts = false);

internal sealed record WholeWorkflowEvaluationResult(
    int TruthCases,
    int OutputCases,
    int CompletedCases,
    int FailedCases,
    int UnexpectedCases,
    int IntegrityFailureCases,
    int TruthSeries,
    int PredictedSeries,
    int MatchedSeries,
    int TruthPoints,
    int PredictedPoints,
    int MatchedPoints,
    bool UniquePointValueMetricsAvailable,
    bool RelationalRowMetricsAvailable,
    bool RelationalPhaseMetricsAvailable,
    int ActualRows,
    int ResidualArtifactRowsFromFailedCases,
    int? ExpectedRows,
    int? StructurallyMatchedRows,
    int? CorrectRows,
    int? MissingRows,
    int? ExtraRows,
    int? DuplicateRows,
    int? WrongScaleRows,
    int? WrongExportModeRows,
    int? WrongPhaseRows,
    int? WrongRelationRows,
    int? UniquePointValueCorrect,
    int? UniquePointValueIncorrect,
    int? UniquePointMissing,
    int? UniquePointExtra,
    int? UniquePointWrongScale,
    int? UniquePointWrongExportMode,
    double UniquePointStructuralPrecision,
    double UniquePointStructuralCoverage,
    double? UniquePointValuePrecision,
    double? UniquePointValueCoverage,
    double? MatchedUniquePointValueAccuracy,
    double? RelationalRowPrecision,
    double? RelationalRowCoverage,
    double? MatchedRelationalGraphValueAccuracy,
    double? MatchedRelationalPhaseAccuracy,
    bool ArtifactIntegrityValid,
    IReadOnlyDictionary<string, int> FailureKinds);

internal static class WholeWorkflowCsvEvaluator
{
    private const string AuditCsvHeader =
        "x_value,y_value,phase,point_id,source_series_id,target_intervention_series_id," +
        "phase_id,original_pixel_x,original_pixel_y,x_source,x_confidence,y_confidence," +
        "point_confidence,review_status,inclusion,export_mode,calibration_status," +
        "session_origin_override_applied,session_origin_override_reason," +
        "session_origin_override_confirmed_at_utc,series_symbol,series_name,source_stage,model_version";

    private static readonly string[] AuditRootProperties =
    [
        "contract_version", "coordinate_space", "export_mode", "intervention_series_id",
        "panel_id", "project_id", "row_count", "rows", "run_id", "series_name", "series_symbol",
    ];

    private static readonly string[] AuditRowProperties =
    [
        "calibration_status", "export_mode", "inclusion", "model_version", "original_pixel_x",
        "original_pixel_y", "phase", "phase_id", "point_confidence", "point_id", "review_status",
        "series_name", "series_symbol", "session_origin_override_applied",
        "session_origin_override_confirmed_at_utc", "session_origin_override_reason", "source_series_id",
        "source_stage", "target_intervention_series_id", "x_confidence", "x_source", "x_value",
        "y_confidence", "y_value",
    ];

    internal static WholeWorkflowEvaluationResult Evaluate(
        IReadOnlyList<WholeWorkflowTruthCase> truthCases,
        IReadOnlyList<WholeWorkflowCaseOutput> outputs,
        WholeWorkflowEvaluationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(truthCases);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        Dictionary<string, ValidatedTruthCase> truthByKey = ValidateTruth(truthCases);
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        var outputGroups = outputs.GroupBy(static item => item.CaseKey, StringComparer.Ordinal).ToArray();
        var outputByKey = new Dictionary<string, WholeWorkflowCaseOutput>(StringComparer.Ordinal);
        int unexpectedCases = 0;
        bool globalIntegrity = true;
        foreach (IGrouping<string, WholeWorkflowCaseOutput> group in outputGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Count() != 1)
            {
                unexpectedCases += group.Count() - 1;
                globalIntegrity = false;
                Increment(failures, "duplicate_case_output");
            }

            WholeWorkflowCaseOutput output = group.First();
            if (!truthByKey.ContainsKey(group.Key))
            {
                unexpectedCases++;
                globalIntegrity = false;
                Increment(failures, "unexpected_case");
                continue;
            }

            outputByKey[group.Key] = output;
        }

        int completedCases = 0;
        int failedCases = 0;
        int integrityFailureCases = 0;
        int predictedSeries = 0;
        int matchedSeries = 0;
        int predictedPoints = 0;
        int matchedPoints = 0;
        int actualRows = 0;
        int residualArtifactRows = 0;
        int expectedRows = 0;
        int structurallyMatchedRows = 0;
        int correctRows = 0;
        int missingRows = 0;
        int extraRows = 0;
        int duplicateRows = 0;
        int wrongScaleRows = 0;
        int wrongExportModeRows = 0;
        int wrongPhaseRows = 0;
        int wrongRelationRows = 0;
        int phaseComparedRows = 0;
        int phaseCorrectRows = 0;
        int graphComparedRows = 0;
        int graphCorrectRows = 0;
        int uniqueValueCorrect = 0;
        int uniqueWrongScale = 0;
        int uniqueWrongExportMode = 0;
        bool relationMetricsAvailable = truthByKey.Values.All(static item => item.Relations is not null);
        bool phaseMetricsAvailable = truthByKey.Values.All(static item => item.PhaseTruthAvailable);

        foreach (ValidatedTruthCase truth in truthByKey.Values.OrderBy(static item => item.Case.CaseKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int caseExpectedRows = truth.ExpectedRows?.Count ?? 0;
            expectedRows += caseExpectedRows;
            if (!outputByKey.TryGetValue(truth.Case.CaseKey, out WholeWorkflowCaseOutput? output))
            {
                failedCases++;
                if (relationMetricsAvailable)
                {
                    missingRows += caseExpectedRows;
                }
                Increment(failures, "missing_case_output");
                continue;
            }

            if (!IsSha256(output.SourceSha256) ||
                !string.Equals(output.SourceSha256, truth.Case.SourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                failedCases++;
                integrityFailureCases++;
                globalIntegrity = false;
                if (relationMetricsAvailable)
                {
                    missingRows += caseExpectedRows;
                }
                Increment(failures, "source_identity_mismatch");
                continue;
            }

            if (!output.WorkflowSucceeded)
            {
                failedCases++;
                Increment(failures, string.IsNullOrWhiteSpace(output.FailureCode)
                    ? "workflow_failed_without_code"
                    : "workflow_failed");
            }
            ParsedCase parsed;
            try
            {
                parsed = ParseArtifacts(output.Artifacts, options.RequireInMemoryArtifacts, cancellationToken);
                if (output.WorkflowSucceeded && parsed.Targets.Count == 0)
                {
                    throw new InvalidDataException("A successful workflow output has no CSV artifact set.");
                }
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
            {
                if (output.WorkflowSucceeded)
                {
                    failedCases++;
                }
                integrityFailureCases++;
                globalIntegrity = false;
                if (relationMetricsAvailable)
                {
                    missingRows += caseExpectedRows;
                }
                Increment(failures, $"artifact_integrity:{exception.GetType().Name}");
                continue;
            }

            if (output.WorkflowSucceeded)
            {
                completedCases++;
            }

            actualRows += parsed.Targets.Sum(static item => item.Rows.Count);
            if (!output.WorkflowSucceeded)
            {
                int residualRows = parsed.Targets.Sum(static item => item.Rows.Count);
                residualArtifactRows += residualRows;
                predictedSeries += parsed.Points.Values.Select(static item => item.SourceSeriesId).Distinct().Count();
                predictedPoints += parsed.Points.Count;
                if (relationMetricsAvailable)
                {
                    missingRows += caseExpectedRows;
                    extraRows += residualRows;
                    duplicateRows += CountDuplicateRows(parsed.Targets);
                }
                continue;
            }

            CaseMatch match = MatchCase(truth, parsed, options, cancellationToken);
            predictedSeries += match.PredictedSeries;
            matchedSeries += match.MatchedSeries;
            predictedPoints += match.PredictedPoints;
            matchedPoints += match.MatchedPoints;
            uniqueValueCorrect += match.UniqueValueCorrectPoints;
            uniqueWrongScale += match.UniqueWrongScalePoints;
            uniqueWrongExportMode += match.UniqueWrongExportModePoints;
            if (relationMetricsAvailable)
            {
                structurallyMatchedRows += match.StructurallyMatchedRows;
                correctRows += match.CorrectRows;
                missingRows += match.MissingRows;
                extraRows += match.ExtraRows;
                duplicateRows += match.DuplicateRows;
                wrongScaleRows += match.WrongScaleRows;
                wrongExportModeRows += match.WrongExportModeRows;
                wrongPhaseRows += match.WrongPhaseRows;
                wrongRelationRows += match.WrongRelationRows;
                graphComparedRows += match.GraphComparedRows;
                graphCorrectRows += match.GraphCorrectRows;
                phaseComparedRows += match.PhaseComparedRows;
                phaseCorrectRows += match.PhaseCorrectRows;
            }
        }

        int truthSeries = truthByKey.Values.Sum(static item => item.Series.Count);
        int truthPoints = truthByKey.Values.Sum(static item => item.Points.Count);
        bool uniquePointMetricsAvailable = globalIntegrity;
        bool rowMetricsAvailable = relationMetricsAvailable && globalIntegrity;
        return new WholeWorkflowEvaluationResult(
            truthByKey.Count,
            outputs.Count,
            completedCases,
            failedCases,
            unexpectedCases,
            integrityFailureCases,
            truthSeries,
            predictedSeries,
            matchedSeries,
            truthPoints,
            predictedPoints,
            matchedPoints,
            uniquePointMetricsAvailable,
            rowMetricsAvailable,
            rowMetricsAvailable && phaseMetricsAvailable,
            actualRows,
            residualArtifactRows,
            rowMetricsAvailable ? expectedRows : null,
            rowMetricsAvailable ? structurallyMatchedRows : null,
            rowMetricsAvailable ? correctRows : null,
            rowMetricsAvailable ? missingRows : null,
            rowMetricsAvailable ? extraRows : null,
            rowMetricsAvailable ? duplicateRows : null,
            rowMetricsAvailable ? wrongScaleRows : null,
            rowMetricsAvailable ? wrongExportModeRows : null,
            rowMetricsAvailable && phaseMetricsAvailable ? wrongPhaseRows : null,
            rowMetricsAvailable ? wrongRelationRows : null,
            uniquePointMetricsAvailable ? uniqueValueCorrect : null,
            uniquePointMetricsAvailable ? matchedPoints - uniqueValueCorrect : null,
            uniquePointMetricsAvailable ? truthPoints - matchedPoints : null,
            uniquePointMetricsAvailable ? predictedPoints - matchedPoints : null,
            uniquePointMetricsAvailable ? uniqueWrongScale : null,
            uniquePointMetricsAvailable ? uniqueWrongExportMode : null,
            predictedPoints == 0 ? (truthPoints == 0 ? 1 : 0) : matchedPoints / (double)predictedPoints,
            truthPoints == 0 ? 1 : matchedPoints / (double)truthPoints,
            uniquePointMetricsAvailable ? NullableRatio(uniqueValueCorrect, predictedPoints) : null,
            uniquePointMetricsAvailable ? Ratio(uniqueValueCorrect, truthPoints) : null,
            uniquePointMetricsAvailable ? NullableRatio(uniqueValueCorrect, matchedPoints) : null,
            rowMetricsAvailable ? NullableRatio(correctRows, actualRows) : null,
            rowMetricsAvailable ? Ratio(correctRows, expectedRows) : null,
            rowMetricsAvailable ? NullableRatio(graphCorrectRows, graphComparedRows) : null,
            rowMetricsAvailable && phaseMetricsAvailable ? NullableRatio(phaseCorrectRows, phaseComparedRows) : null,
            globalIntegrity,
            new ReadOnlyDictionary<string, int>(failures));
    }

    private static CaseMatch MatchCase(
        ValidatedTruthCase truth,
        ParsedCase parsed,
        WholeWorkflowEvaluationOptions options,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, PredictedPoint> predictedPoints = parsed.Points;
        Guid[] predictedSeries = predictedPoints.Values.Select(static item => item.SourceSeriesId)
            .Distinct().Order().ToArray();
        string[] truthSeries = truth.Series.Keys.Order(StringComparer.Ordinal).ToArray();
        var pairMatches = new Dictionary<(Guid Predicted, string Truth), PointPairMatch>();
        var weights = new int[predictedSeries.Length, truthSeries.Length];
        for (int predictedIndex = 0; predictedIndex < predictedSeries.Length; predictedIndex++)
        {
            PredictedPoint[] sourcePoints = predictedPoints.Values
                .Where(item => item.SourceSeriesId == predictedSeries[predictedIndex])
                .OrderBy(static item => item.PointId).ToArray();
            for (int truthIndex = 0; truthIndex < truthSeries.Length; truthIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WholeWorkflowTruthPoint[] targetPoints = truth.Series[truthSeries[truthIndex]];
                PointPairMatch pair = MatchPointSets(sourcePoints, targetPoints, options.SourcePixelMatchTolerance);
                pairMatches[(predictedSeries[predictedIndex], truthSeries[truthIndex])] = pair;
                weights[predictedIndex, truthIndex] = pair.RuntimeToTruth.Count;
            }
        }

        Dictionary<int, int> seriesIndexes = MaximumWeightAssignment(weights);
        var predictedToTruthSeries = new Dictionary<Guid, string>();
        var predictedToTruthPoint = new Dictionary<Guid, WholeWorkflowTruthPoint>();
        foreach ((int predictedIndex, int truthIndex) in seriesIndexes)
        {
            if (predictedIndex >= predictedSeries.Length || truthIndex >= truthSeries.Length ||
                weights[predictedIndex, truthIndex] == 0)
            {
                continue;
            }

            Guid sourceSeries = predictedSeries[predictedIndex];
            string targetSeries = truthSeries[truthIndex];
            predictedToTruthSeries.Add(sourceSeries, targetSeries);
            PointPairMatch pair = pairMatches[(sourceSeries, targetSeries)];
            foreach ((Guid pointId, WholeWorkflowTruthPoint point) in pair.RuntimeToTruth)
            {
                predictedToTruthPoint.Add(pointId, point);
            }
        }

        int uniqueValueCorrect = 0;
        int uniqueWrongScale = 0;
        int uniqueWrongExportMode = 0;
        foreach ((Guid pointId, WholeWorkflowTruthPoint truthPoint) in predictedToTruthPoint)
        {
            PredictedPoint predictedPoint = predictedPoints[pointId];
            bool modeCorrect = string.Equals(
                predictedPoint.ExportMode,
                ExportModeName(truthPoint.ExpectedExportMode),
                StringComparison.Ordinal);
            bool scaleCorrect = Math.Abs(predictedPoint.XValue - truthPoint.ExpectedExportX) <= options.GraphXAbsoluteTolerance &&
                Math.Abs(predictedPoint.YValue - truthPoint.GraphY) <= options.GraphYAbsoluteTolerance;
            if (modeCorrect && scaleCorrect) uniqueValueCorrect++;
            if (!scaleCorrect) uniqueWrongScale++;
            if (!modeCorrect) uniqueWrongExportMode++;
        }

        if (truth.ExpectedRows is null)
        {
            return new CaseMatch(predictedSeries.Length, predictedToTruthSeries.Count, predictedPoints.Count,
                predictedToTruthPoint.Count, uniqueValueCorrect, uniqueWrongScale, uniqueWrongExportMode,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var usedExpected = new HashSet<(string TargetSeries, string PointKey)>();
        int structurallyMatched = 0;
        int correct = 0;
        int duplicates = 0;
        int wrongScale = 0;
        int wrongExportMode = 0;
        int wrongPhase = 0;
        int wrongRelation = 0;
        int graphCompared = 0;
        int graphCorrect = 0;
        int phaseCompared = 0;
        int phaseCorrect = 0;
        foreach (ParsedTarget target in parsed.Targets.OrderBy(static item => item.TargetInterventionSeriesId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seenPointIds = new HashSet<Guid>();
            foreach (ParsedAuditRow row in target.Rows)
            {
                if (!seenPointIds.Add(row.PointId))
                {
                    duplicates++;
                    continue;
                }

                if (!predictedToTruthSeries.TryGetValue(target.TargetInterventionSeriesId, out string? targetSeries) ||
                    !predictedToTruthPoint.TryGetValue(row.PointId, out WholeWorkflowTruthPoint? truthPoint))
                {
                    continue;
                }

                var key = (targetSeries, truthPoint.PointKey);
                if (!truth.ExpectedRows.TryGetValue(key, out ExpectedRow? expected) || !usedExpected.Add(key))
                {
                    continue;
                }

                structurallyMatched++;
                bool relationCorrect = string.Equals(expected.SourceSeriesKey, truthPoint.SeriesKey, StringComparison.Ordinal) &&
                    string.Equals(expected.Inclusion, row.Inclusion, StringComparison.Ordinal);
                bool modeCorrect = string.Equals(
                    row.ExportMode,
                    ExportModeName(truthPoint.ExpectedExportMode),
                    StringComparison.Ordinal);
                bool scaleCorrect = Math.Abs(row.XValue - truthPoint.ExpectedExportX) <= options.GraphXAbsoluteTolerance &&
                    Math.Abs(row.YValue - truthPoint.GraphY) <= options.GraphYAbsoluteTolerance;
                bool rowPhaseCorrect = !truth.PhaseTruthAvailable ||
                    string.Equals(row.Phase, truthPoint.AuthoritativePhaseCode, StringComparison.Ordinal);
                graphCompared++;
                if (modeCorrect && scaleCorrect) graphCorrect++;
                if (!scaleCorrect) wrongScale++;
                if (!modeCorrect) wrongExportMode++;
                if (truth.PhaseTruthAvailable)
                {
                    phaseCompared++;
                    if (rowPhaseCorrect) phaseCorrect++; else wrongPhase++;
                }
                if (!relationCorrect) wrongRelation++;
                if (relationCorrect && modeCorrect && scaleCorrect && rowPhaseCorrect) correct++;
            }
        }

        int actual = parsed.Targets.Sum(static item => item.Rows.Count);
        return new CaseMatch(
            predictedSeries.Length,
            predictedToTruthSeries.Count,
            predictedPoints.Count,
            predictedToTruthPoint.Count,
            uniqueValueCorrect,
            uniqueWrongScale,
            uniqueWrongExportMode,
            structurallyMatched,
            correct,
            truth.ExpectedRows.Count - structurallyMatched,
            actual - structurallyMatched,
            duplicates,
            wrongScale,
            wrongExportMode,
            wrongPhase,
            wrongRelation,
            graphCompared,
            graphCorrect,
            phaseCompared,
            phaseCorrect);
    }

    private static Dictionary<Guid, PredictedPoint> BuildPredictedPoints(IReadOnlyList<ParsedTarget> targets)
    {
        var points = new Dictionary<Guid, PredictedPoint>();
        foreach (ParsedAuditRow row in targets.SelectMany(static item => item.Rows))
        {
            var point = new PredictedPoint(
                row.PointId,
                row.SourceSeriesId,
                row.OriginalPixelX,
                row.OriginalPixelY,
                row.XValue,
                row.YValue,
                row.ExportMode);
            if (points.TryGetValue(row.PointId, out PredictedPoint? previous) && previous != point)
            {
                throw new InvalidDataException(
                    "An audit point changes source series, source-pixel, exported-value, or export-mode identity across artifacts.");
            }
            points[row.PointId] = point;
        }
        return points;
    }

    private static int CountDuplicateRows(IReadOnlyList<ParsedTarget> targets) => targets.Sum(target =>
        target.Rows.GroupBy(static row => row.PointId).Sum(static group => Math.Max(0, group.Count() - 1)));

    private static PointPairMatch MatchPointSets(
        IReadOnlyList<PredictedPoint> predicted,
        WholeWorkflowTruthPoint[] truth,
        double tolerance)
    {
        int[] truthToPredicted = Enumerable.Repeat(-1, truth.Length).ToArray();
        var adjacency = new int[predicted.Count][];
        for (int predictedIndex = 0; predictedIndex < predicted.Count; predictedIndex++)
        {
            PredictedPoint point = predicted[predictedIndex];
            adjacency[predictedIndex] = truth.Select((candidate, index) => new
                {
                    Index = index,
                    Distance = Distance(point.OriginalPixelX, point.OriginalPixelY,
                        candidate.SourcePixelX, candidate.SourcePixelY),
                    candidate.PointKey,
                })
                .Where(item => item.Distance <= tolerance)
                .OrderBy(static item => item.Distance)
                .ThenBy(static item => item.PointKey, StringComparer.Ordinal)
                .Select(static item => item.Index)
                .ToArray();
        }

        for (int predictedIndex = 0; predictedIndex < predicted.Count; predictedIndex++)
        {
            var visited = new bool[truth.Length];
            TryAssignPoint(predictedIndex, adjacency, truthToPredicted, visited);
        }

        var result = new Dictionary<Guid, WholeWorkflowTruthPoint>();
        for (int truthIndex = 0; truthIndex < truthToPredicted.Length; truthIndex++)
        {
            int predictedIndex = truthToPredicted[truthIndex];
            if (predictedIndex >= 0)
            {
                result.Add(predicted[predictedIndex].PointId, truth[truthIndex]);
            }
        }
        return new PointPairMatch(result);
    }

    private static bool TryAssignPoint(int predictedIndex, int[][] adjacency, int[] truthToPredicted, bool[] visited)
    {
        foreach (int truthIndex in adjacency[predictedIndex])
        {
            if (visited[truthIndex]) continue;
            visited[truthIndex] = true;
            if (truthToPredicted[truthIndex] < 0 ||
                TryAssignPoint(truthToPredicted[truthIndex], adjacency, truthToPredicted, visited))
            {
                truthToPredicted[truthIndex] = predictedIndex;
                return true;
            }
        }
        return false;
    }

    private static Dictionary<int, int> MaximumWeightAssignment(int[,] weights)
    {
        int rows = weights.GetLength(0);
        int columns = weights.GetLength(1);
        int size = Math.Max(rows, columns);
        if (size == 0) return [];
        int maximum = 0;
        foreach (int weight in weights) maximum = Math.Max(maximum, weight);
        var u = new int[size + 1];
        var v = new int[size + 1];
        var p = new int[size + 1];
        var way = new int[size + 1];
        for (int row = 1; row <= size; row++)
        {
            p[0] = row;
            int column0 = 0;
            var minimum = Enumerable.Repeat(int.MaxValue, size + 1).ToArray();
            var used = new bool[size + 1];
            do
            {
                used[column0] = true;
                int row0 = p[column0];
                int delta = int.MaxValue;
                int column1 = 0;
                for (int column = 1; column <= size; column++)
                {
                    if (used[column]) continue;
                    int weight = row0 <= rows && column <= columns ? weights[row0 - 1, column - 1] : 0;
                    int current = maximum - weight - u[row0] - v[column];
                    if (current < minimum[column])
                    {
                        minimum[column] = current;
                        way[column] = column0;
                    }
                    if (minimum[column] < delta)
                    {
                        delta = minimum[column];
                        column1 = column;
                    }
                }
                for (int column = 0; column <= size; column++)
                {
                    if (used[column])
                    {
                        u[p[column]] += delta;
                        v[column] -= delta;
                    }
                    else
                    {
                        minimum[column] -= delta;
                    }
                }
                column0 = column1;
            }
            while (p[column0] != 0);

            do
            {
                int column1 = way[column0];
                p[column0] = p[column1];
                column0 = column1;
            }
            while (column0 != 0);
        }

        var assignment = new Dictionary<int, int>();
        for (int column = 1; column <= size; column++)
        {
            if (p[column] > 0) assignment[p[column] - 1] = column - 1;
        }
        return assignment;
    }

    private static ParsedCase ParseArtifacts(
        IReadOnlyList<WholeWorkflowCsvArtifact> artifacts,
        bool requireInMemory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, ArtifactTriple>(StringComparer.OrdinalIgnoreCase);
        foreach (WholeWorkflowCsvArtifact artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateArtifactDescriptor(artifact);
            if (!names.Add(artifact.FileName))
            {
                throw new InvalidDataException("Workflow export artifacts reuse a final path or file name.");
            }
            byte[] bytes;
            if (artifact.Content is not null)
            {
                bytes = artifact.Content.CopyBytes();
            }
            else
            {
                if (requireInMemory)
                {
                    throw new InvalidDataException("Aggregate-only evaluation requires in-memory export artifacts.");
                }
                string fullPath = Path.GetFullPath(artifact.WrittenPath!);
                if (!paths.Add(fullPath))
                    throw new InvalidDataException("Workflow export artifacts reuse a final path.");
                if (!File.Exists(fullPath)) throw new InvalidDataException("A reported workflow export artifact is missing.");
                bytes = File.ReadAllBytes(fullPath);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Hash(bytes), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A workflow export artifact checksum does not match its final bytes.");
            }

            (string stem, ArtifactKind kind) = Classify(artifact.FileName);
            if (!groups.TryGetValue(stem, out ArtifactTriple? triple))
            {
                triple = new ArtifactTriple();
                groups.Add(stem, triple);
            }
            triple.Set(kind, artifact, bytes);
        }

        var targets = new List<ParsedTarget>(groups.Count);
        var targetIds = new HashSet<Guid>();
        foreach (ArtifactTriple triple in groups.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(static item => item.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            targets.Add(ParseTriple(triple, targetIds));
        }
        if (targets.Select(static target => target.RunId).Distinct().Count() > 1)
            throw new InvalidDataException("Workflow export artifacts combine different run identities.");
        if (targets.Select(static target => target.ProjectId).Distinct().Count() > 1)
            throw new InvalidDataException("Workflow export artifacts combine different project identities.");
        Dictionary<Guid, PredictedPoint> points = BuildPredictedPoints(targets);
        return new ParsedCase(targets, points);
    }

    private static ParsedTarget ParseTriple(ArtifactTriple triple, HashSet<Guid> targetIds)
    {
        ArtifactBytes minimal = triple.Minimal ?? throw new InvalidDataException("An export stem is missing its minimal CSV.");
        ArtifactBytes auditCsv = triple.AuditCsv ?? throw new InvalidDataException("An export stem is missing its audit CSV.");
        ArtifactBytes auditJson = triple.AuditJson ?? throw new InvalidDataException("An export stem is missing its audit JSON.");
        List<List<string>> minimalTable = ParseCsv(DecodeUtf8(minimal.Bytes));
        List<List<string>> auditTable = ParseCsv(DecodeUtf8(auditCsv.Bytes));
        if (minimalTable.Count == 0 || minimalTable[0].Count != 3 ||
            !string.Equals(string.Join(',', minimalTable[0]), ExportContract.MinimalCsvHeader, StringComparison.Ordinal))
            throw new InvalidDataException("Minimal CSV header is invalid.");
        if (auditTable.Count == 0 ||
            !string.Equals(string.Join(',', auditTable[0]), AuditCsvHeader, StringComparison.Ordinal))
            throw new InvalidDataException("Audit CSV header is invalid.");

        List<ParsedMinimalRow> minimalRows = minimalTable.Skip(1).Select(ParseMinimalRow).ToList();
        List<List<string>> auditCsvRows = auditTable.Skip(1).ToList();
        (Guid runId, Guid projectId, Guid panelId, Guid targetId, List<ParsedAuditRow> rows) =
            ParseAuditJson(auditJson.Bytes);
        if (!targetIds.Add(targetId)) throw new InvalidDataException("More than one artifact stem describes the same target intervention series.");
        if (rows.Count == 0) throw new InvalidDataException("A workflow target artifact cannot be empty.");
        if (minimal.Descriptor.RowCount != minimalRows.Count || auditCsv.Descriptor.RowCount != auditCsvRows.Count ||
            auditJson.Descriptor.RowCount != rows.Count || minimalRows.Count != rows.Count || auditCsvRows.Count != rows.Count)
            throw new InvalidDataException("Artifact row counts disagree.");
        for (int index = 0; index < rows.Count; index++)
        {
            ParsedAuditRow row = rows[index];
            if (row.TargetInterventionSeriesId != targetId) throw new InvalidDataException("Audit row target differs from its artifact target.");
            ValidateAuditCsvRow(auditCsvRows[index], row);
            ParsedMinimalRow minimalRow = minimalRows[index];
            if (!minimalRow.XValue.Equals(row.XValue) || !minimalRow.YValue.Equals(row.YValue) ||
                !string.Equals(minimalRow.Phase, CsvSafe(row.Phase), StringComparison.Ordinal))
                throw new InvalidDataException("Minimal CSV is not the exact ordered projection of its audit row.");
        }
        return new ParsedTarget(runId, projectId, panelId, targetId, rows);
    }

    private static (Guid RunId, Guid ProjectId, Guid PanelId, Guid TargetId, List<ParsedAuditRow> Rows)
        ParseAuditJson(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        RejectDuplicateProperties(document.RootElement);
        RequireExactProperties(document.RootElement, AuditRootProperties);
        RequireInt(document.RootElement, "contract_version", ExportContract.Version);
        RequireText(document.RootElement, "coordinate_space", ExportContract.CoordinateSpace);
        string exportMode = RequireOneOf(document.RootElement, "export_mode", "printed_session", "observation_order");
        Guid runId = RequireGuid(document.RootElement, "run_id");
        Guid projectId = RequireGuid(document.RootElement, "project_id");
        Guid panelId = RequireGuid(document.RootElement, "panel_id");
        Guid targetId = RequireGuid(document.RootElement, "intervention_series_id");
        string seriesSymbol = RequireString(document.RootElement, "series_symbol");
        string seriesName = RequireString(document.RootElement, "series_name");
        JsonElement rowsElement = document.RootElement.GetProperty("rows");
        if (rowsElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Audit JSON rows must be an array.");
        int declaredCount = RequireNonNegativeInt(document.RootElement, "row_count");
        var rows = new List<ParsedAuditRow>(rowsElement.GetArrayLength());
        foreach (JsonElement element in rowsElement.EnumerateArray()) rows.Add(ParseAuditJsonRow(element));
        if (declaredCount != rows.Count) throw new InvalidDataException("Audit JSON row_count differs from its rows.");
        if (rows.Any(row => !string.Equals(row.ExportMode, exportMode, StringComparison.Ordinal) ||
                !string.Equals(row.SeriesSymbol, seriesSymbol, StringComparison.Ordinal) ||
                !string.Equals(row.SeriesName, seriesName, StringComparison.Ordinal)))
            throw new InvalidDataException("Audit JSON root metadata differs from its rows.");
        return (runId, projectId, panelId, targetId, rows);
    }

    private static ParsedAuditRow ParseAuditJsonRow(JsonElement value)
    {
        RequireExactProperties(value, AuditRowProperties);
        return new ParsedAuditRow(
            FiniteDouble(value, "x_value"),
            FiniteDouble(value, "y_value"),
            RequireString(value, "phase"),
            RequireGuid(value, "point_id"),
            RequireGuid(value, "source_series_id"),
            RequireGuid(value, "target_intervention_series_id"),
            RequireGuid(value, "phase_id"),
            FiniteDouble(value, "original_pixel_x"),
            FiniteDouble(value, "original_pixel_y"),
            RequireOneOf(value, "x_source", "printed", "estimated", "observation_order", "unknown"),
            Confidence(value, "x_confidence"),
            Confidence(value, "y_confidence"),
            Confidence(value, "point_confidence"),
            RequireOneOf(value, "review_status", "unreviewed", "accepted", "corrected", "rejected"),
            RequireOneOf(value, "inclusion", "intervention", "shared_baseline", "applicable_probe"),
            RequireOneOf(value, "export_mode", "printed_session", "observation_order"),
            RequireOneOf(value, "calibration_status", "missing", "needs_review", "valid", "invalid_session_origin"),
            RequireBoolean(value, "session_origin_override_applied"),
            NullableString(value, "session_origin_override_reason"),
            NullableDateTimeOffset(value, "session_origin_override_confirmed_at_utc"),
            RequireString(value, "series_symbol"),
            RequireString(value, "series_name"),
            RequireString(value, "source_stage"),
            NullableString(value, "model_version"));
    }

    private static void ValidateAuditCsvRow(List<string> fields, ParsedAuditRow row)
    {
        if (fields.Count != 24 ||
            !ParseFinite(fields[0]).Equals(row.XValue) ||
            !ParseFinite(fields[1]).Equals(row.YValue) ||
            !string.Equals(fields[2], CsvSafe(row.Phase), StringComparison.Ordinal) ||
            !ParseGuid(fields[3]).Equals(row.PointId) ||
            !ParseGuid(fields[4]).Equals(row.SourceSeriesId) ||
            !ParseGuid(fields[5]).Equals(row.TargetInterventionSeriesId) ||
            !ParseGuid(fields[6]).Equals(row.PhaseId) ||
            !ParseFinite(fields[7]).Equals(row.OriginalPixelX) ||
            !ParseFinite(fields[8]).Equals(row.OriginalPixelY) ||
            !string.Equals(fields[9], row.XSource, StringComparison.Ordinal) ||
            !ParseFinite(fields[10]).Equals(row.XConfidence) ||
            !ParseFinite(fields[11]).Equals(row.YConfidence) ||
            !ParseFinite(fields[12]).Equals(row.PointConfidence) ||
            !string.Equals(fields[13], row.ReviewStatus, StringComparison.Ordinal) ||
            !string.Equals(fields[14], row.Inclusion, StringComparison.Ordinal) ||
            !string.Equals(fields[15], row.ExportMode, StringComparison.Ordinal) ||
            !string.Equals(fields[16], row.CalibrationStatus, StringComparison.Ordinal) ||
            !bool.TryParse(fields[17], out bool overrideApplied) || overrideApplied != row.SessionOriginOverrideApplied ||
            !string.Equals(fields[18], CsvSafe(row.SessionOriginOverrideReason), StringComparison.Ordinal) ||
            !NullableInstantEquals(fields[19], row.SessionOriginOverrideConfirmedAtUtc) ||
            !string.Equals(fields[20], CsvSafe(row.SeriesSymbol), StringComparison.Ordinal) ||
            !string.Equals(fields[21], CsvSafe(row.SeriesName), StringComparison.Ordinal) ||
            !string.Equals(fields[22], CsvSafe(row.SourceStage), StringComparison.Ordinal) ||
            !string.Equals(fields[23], CsvSafe(row.ModelVersion), StringComparison.Ordinal))
            throw new InvalidDataException("Audit CSV row differs from its audit JSON row.");
    }

    private static ParsedMinimalRow ParseMinimalRow(List<string> fields)
    {
        if (fields.Count != 3) throw new InvalidDataException("Minimal CSV row must contain exactly three fields.");
        return new ParsedMinimalRow(ParseFinite(fields[0]), ParseFinite(fields[1]), fields[2]);
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"') quoted = false;
                else field.Append(character);
                continue;
            }
            if (character == '"')
            {
                if (field.Length != 0) throw new InvalidDataException("CSV quote begins inside an unquoted field.");
                quoted = true;
            }
            else if (character == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else field.Append(character);
        }
        if (quoted) throw new InvalidDataException("CSV ends inside a quoted field.");
        if (field.Length != 0 || row.Count != 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    private static Dictionary<string, ValidatedTruthCase> ValidateTruth(IReadOnlyList<WholeWorkflowTruthCase> cases)
    {
        var result = new Dictionary<string, ValidatedTruthCase>(StringComparer.Ordinal);
        foreach (WholeWorkflowTruthCase item in cases)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (string.IsNullOrWhiteSpace(item.CaseKey) || !IsSha256(item.SourceSha256) ||
                item.SourceWidth <= 0 || item.SourceHeight <= 0)
                throw new ArgumentException("Truth case identity, checksum, and dimensions must be complete.", nameof(cases));
            if (!result.TryAdd(item.CaseKey, ValidateTruthCase(item)))
                throw new ArgumentException("Truth case keys must be unique.", nameof(cases));
        }
        return result;
    }

    private static ValidatedTruthCase ValidateTruthCase(WholeWorkflowTruthCase item)
    {
        ArgumentNullException.ThrowIfNull(item.Series);
        ArgumentNullException.ThrowIfNull(item.Points);
        if (item.Series.Count == 0 || item.Points.Count == 0)
            throw new ArgumentException("Truth cases require non-empty series and point collections.");
        string[] seriesKeys = item.Series.Select(static value => value.SeriesKey).ToArray();
        if (seriesKeys.Any(string.IsNullOrWhiteSpace) || seriesKeys.Distinct(StringComparer.Ordinal).Count() != seriesKeys.Length)
            throw new ArgumentException("Truth series keys must be non-empty and unique.");
        var seriesSet = seriesKeys.ToHashSet(StringComparer.Ordinal);
        if (item.Points.Select(static point => point.PointKey).Any(string.IsNullOrWhiteSpace) ||
            item.Points.Select(static point => point.PointKey).Distinct(StringComparer.Ordinal).Count() != item.Points.Count)
            throw new ArgumentException("Truth point keys must be non-empty and unique.");
        foreach (WholeWorkflowTruthPoint point in item.Points)
        {
            if (!seriesSet.Contains(point.SeriesKey) || !double.IsFinite(point.SourcePixelX) ||
                !double.IsFinite(point.SourcePixelY) || point.SourcePixelX < 0 || point.SourcePixelY < 0 ||
                point.SourcePixelX > item.SourceWidth || point.SourcePixelY > item.SourceHeight ||
                !double.IsFinite(point.GraphX) || !double.IsFinite(point.GraphY) ||
                !double.IsFinite(point.ExpectedExportX) || !Enum.IsDefined(point.ExpectedExportMode))
                throw new ArgumentException("Truth points require valid series, source-pixel, and graph-coordinate evidence.");
        }
        int phased = item.Points.Count(static point => point.AuthoritativePhaseCode is not null);
        if (phased != 0 && phased != item.Points.Count ||
            item.Points.Any(static point => point.AuthoritativePhaseCode is not null && string.IsNullOrWhiteSpace(point.AuthoritativePhaseCode)))
            throw new ArgumentException("Phase truth must cover every point or remain entirely unavailable.");
        var pointsBySeries = seriesKeys.ToDictionary(
            static key => key,
            key => item.Points.Where(point => string.Equals(point.SeriesKey, key, StringComparison.Ordinal))
                .OrderBy(static point => point.PointKey, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
        if (pointsBySeries.Values.Any(static points => points.Length == 0))
            throw new ArgumentException("Every truth series must contain at least one point.");

        Dictionary<(string TargetSeries, string PointKey), ExpectedRow>? expected = null;
        if (item.Relations is not null)
        {
            string[] targets = item.Relations.Select(static relation => relation.TargetInterventionSeriesKey).ToArray();
            if (targets.Any(string.IsNullOrWhiteSpace) || targets.Distinct(StringComparer.Ordinal).Count() != targets.Length)
                throw new ArgumentException("Truth relation targets must be non-empty and unique.");
            expected = [];
            foreach (WholeWorkflowTruthRelation relation in item.Relations)
            {
                ArgumentNullException.ThrowIfNull(relation.ApplicableProbeSeriesKeys);
                var included = new List<(string Series, string Inclusion)>
                {
                    (relation.TargetInterventionSeriesKey, "intervention"),
                };
                if (relation.SharedBaselineSeriesKey is not null)
                    included.Add((relation.SharedBaselineSeriesKey, "shared_baseline"));
                included.AddRange(relation.ApplicableProbeSeriesKeys.Select(static key => (key, "applicable_probe")));
                if (included.Any(pair => !seriesSet.Contains(pair.Series)) ||
                    included.Select(static pair => pair.Series).Distinct(StringComparer.Ordinal).Count() != included.Count)
                    throw new ArgumentException("Truth relations must reference distinct known series.");
                foreach ((string sourceSeries, string inclusion) in included)
                foreach (WholeWorkflowTruthPoint point in pointsBySeries[sourceSeries])
                {
                    if (!expected.TryAdd((relation.TargetInterventionSeriesKey, point.PointKey),
                            new ExpectedRow(sourceSeries, inclusion)))
                        throw new ArgumentException("Truth relations produce a duplicate expected artifact row.");
                }
            }
            if (item.Points.Any(point => !expected.Keys.Any(key => string.Equals(key.PointKey, point.PointKey, StringComparison.Ordinal))))
                throw new ArgumentException("Authoritative relations must include every truth point in the expected-row denominator.");
        }
        return new ValidatedTruthCase(item, pointsBySeries, item.Points.ToDictionary(static point => point.PointKey, StringComparer.Ordinal),
            phased == item.Points.Count, item.Relations, expected);
    }

    private static void ValidateOptions(WholeWorkflowEvaluationOptions options)
    {
        if (!double.IsFinite(options.SourcePixelMatchTolerance) || options.SourcePixelMatchTolerance < 0 ||
            !double.IsFinite(options.GraphXAbsoluteTolerance) || options.GraphXAbsoluteTolerance < 0 ||
            !double.IsFinite(options.GraphYAbsoluteTolerance) || options.GraphYAbsoluteTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Evaluation tolerances must be finite and non-negative.");
    }

    private static void ValidateArtifactDescriptor(WholeWorkflowCsvArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (string.IsNullOrWhiteSpace(artifact.FileName) || Path.GetFileName(artifact.FileName) != artifact.FileName ||
            !IsSha256(artifact.Sha256) || artifact.RowCount < 0 ||
            (artifact.Content is not null
                ? artifact.WrittenPath is not null
                : string.IsNullOrWhiteSpace(artifact.WrittenPath) ||
                  !string.Equals(Path.GetFileName(artifact.WrittenPath), artifact.FileName, StringComparison.Ordinal)))
            throw new InvalidDataException("Workflow export artifact descriptor is invalid.");
    }

    private static (string Stem, ArtifactKind Kind) Classify(string name)
    {
        if (name.EndsWith(".audit.csv", StringComparison.OrdinalIgnoreCase))
            return (name[..^".audit.csv".Length], ArtifactKind.AuditCsv);
        if (name.EndsWith(".audit.json", StringComparison.OrdinalIgnoreCase))
            return (name[..^".audit.json".Length], ArtifactKind.AuditJson);
        if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return (name[..^".csv".Length], ArtifactKind.Minimal);
        throw new InvalidDataException("Workflow export contains an unexpected artifact type.");
    }

    private static string DecodeUtf8(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static double Distance(double x1, double y1, double x2, double y2) => Math.Sqrt(Math.Pow(x1 - x2, 2) + Math.Pow(y1 - y2, 2));
    private static double Ratio(int numerator, int denominator) => denominator == 0 ? (numerator == 0 ? 1 : 0) : numerator / (double)denominator;
    private static double? NullableRatio(int numerator, int denominator) => denominator == 0 ? null : numerator / (double)denominator;
    private static void Increment(Dictionary<string, int> values, string key) => values[key] = values.GetValueOrDefault(key) + 1;
    private static string ExportModeName(ExportMode mode) => mode switch
    {
        ExportMode.PrintedSession => "printed_session",
        ExportMode.ObservationOrder => "observation_order",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static double ParseFinite(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) || !double.IsFinite(result))
            throw new InvalidDataException("CSV numeric value is invalid.");
        return result;
    }

    private static Guid ParseGuid(string value) => Guid.TryParseExact(value, "D", out Guid result) && result != Guid.Empty
        ? result : throw new InvalidDataException("CSV GUID value is invalid.");

    private static string CsvSafe(string? value)
    {
        if (value is null) return string.Empty;
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        int index = 0;
        while (index < normalized.Length && char.IsWhiteSpace(normalized[index])) index++;
        return index < normalized.Length && normalized[index] is '=' or '+' or '-' or '@' ? "'" + normalized : normalized;
    }

    private static bool NullableInstantEquals(string csv, DateTimeOffset? json)
    {
        if (json is null) return csv.Length == 0;
        return DateTimeOffset.TryParse(csv, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value) &&
            value.ToUniversalTime() == json.Value.ToUniversalTime();
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Audit JSON contains a duplicate property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static void RequireExactProperties(JsonElement value, IReadOnlyList<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.EnumerateObject().Select(static item => item.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Audit JSON properties differ from the export contract.");
    }

    private static string RequireString(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"Audit JSON {name} must be a string.");
        return value.GetString()!;
    }

    private static string? NullableString(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"Audit JSON {name} must be a string or null.");
        return value.GetString();
    }

    private static string RequireText(JsonElement parent, string name, string expected)
    {
        string actual = RequireString(parent, name);
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new InvalidDataException($"Audit JSON {name} is invalid.");
        return actual;
    }

    private static string RequireOneOf(JsonElement parent, string name, params string[] expected)
    {
        string actual = RequireString(parent, name);
        if (!expected.Contains(actual, StringComparer.Ordinal)) throw new InvalidDataException($"Audit JSON {name} is invalid.");
        return actual;
    }

    private static Guid RequireGuid(JsonElement parent, string name) => Guid.TryParseExact(RequireString(parent, name), "D", out Guid value) && value != Guid.Empty
        ? value : throw new InvalidDataException($"Audit JSON {name} is invalid.");

    private static double FiniteDouble(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) || !double.IsFinite(result))
            throw new InvalidDataException($"Audit JSON {name} must be finite.");
        return result;
    }

    private static double Confidence(JsonElement parent, string name)
    {
        double value = FiniteDouble(parent, name);
        if (value is < 0 or > 1) throw new InvalidDataException($"Audit JSON {name} must remain within [0,1].");
        return value;
    }

    private static int RequireNonNegativeInt(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);
        if (!value.TryGetInt32(out int result) || result < 0) throw new InvalidDataException($"Audit JSON {name} is invalid.");
        return result;
    }

    private static void RequireInt(JsonElement parent, string name, int expected)
    {
        if (RequireNonNegativeInt(parent, name) != expected) throw new InvalidDataException($"Audit JSON {name} is invalid.");
    }

    private static bool RequireBoolean(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Audit JSON {name} must be Boolean.");
        return value.GetBoolean();
    }

    private static DateTimeOffset? NullableDateTimeOffset(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out DateTimeOffset result))
            throw new InvalidDataException($"Audit JSON {name} must be a timestamp or null.");
        return result;
    }

    private enum ArtifactKind { Minimal, AuditCsv, AuditJson }
    private sealed record ArtifactBytes(WholeWorkflowCsvArtifact Descriptor, byte[] Bytes);
    private sealed class ArtifactTriple
    {
        public ArtifactBytes? Minimal { get; private set; }
        public ArtifactBytes? AuditCsv { get; private set; }
        public ArtifactBytes? AuditJson { get; private set; }
        public void Set(ArtifactKind kind, WholeWorkflowCsvArtifact descriptor, byte[] bytes)
        {
            var value = new ArtifactBytes(descriptor, bytes);
            switch (kind)
            {
                case ArtifactKind.Minimal when Minimal is null: Minimal = value; break;
                case ArtifactKind.AuditCsv when AuditCsv is null: AuditCsv = value; break;
                case ArtifactKind.AuditJson when AuditJson is null: AuditJson = value; break;
                default: throw new InvalidDataException("An export stem repeats an artifact type.");
            }
        }
    }

    private sealed record ParsedMinimalRow(double XValue, double YValue, string Phase);
    private sealed record ParsedAuditRow(
        double XValue, double YValue, string Phase, Guid PointId, Guid SourceSeriesId,
        Guid TargetInterventionSeriesId, Guid PhaseId, double OriginalPixelX, double OriginalPixelY,
        string XSource, double XConfidence, double YConfidence, double PointConfidence,
        string ReviewStatus, string Inclusion, string ExportMode, string CalibrationStatus,
        bool SessionOriginOverrideApplied, string? SessionOriginOverrideReason,
        DateTimeOffset? SessionOriginOverrideConfirmedAtUtc, string SeriesSymbol, string SeriesName,
        string SourceStage, string? ModelVersion);
    private sealed record ParsedTarget(
        Guid RunId,
        Guid ProjectId,
        Guid PanelId,
        Guid TargetInterventionSeriesId,
        IReadOnlyList<ParsedAuditRow> Rows);
    private sealed record ParsedCase(
        IReadOnlyList<ParsedTarget> Targets,
        IReadOnlyDictionary<Guid, PredictedPoint> Points);
    private sealed record PredictedPoint(
        Guid PointId,
        Guid SourceSeriesId,
        double OriginalPixelX,
        double OriginalPixelY,
        double XValue,
        double YValue,
        string ExportMode);
    private sealed record PointPairMatch(IReadOnlyDictionary<Guid, WholeWorkflowTruthPoint> RuntimeToTruth);
    private sealed record ExpectedRow(string SourceSeriesKey, string Inclusion);
    private sealed record ValidatedTruthCase(
        WholeWorkflowTruthCase Case,
        IReadOnlyDictionary<string, WholeWorkflowTruthPoint[]> Series,
        IReadOnlyDictionary<string, WholeWorkflowTruthPoint> Points,
        bool PhaseTruthAvailable,
        IReadOnlyList<WholeWorkflowTruthRelation>? Relations,
        IReadOnlyDictionary<(string TargetSeries, string PointKey), ExpectedRow>? ExpectedRows);
    private sealed record CaseMatch(
        int PredictedSeries, int MatchedSeries, int PredictedPoints, int MatchedPoints,
        int UniqueValueCorrectPoints, int UniqueWrongScalePoints, int UniqueWrongExportModePoints,
        int StructurallyMatchedRows, int CorrectRows, int MissingRows, int ExtraRows, int DuplicateRows,
        int WrongScaleRows, int WrongExportModeRows, int WrongPhaseRows, int WrongRelationRows, int GraphComparedRows,
        int GraphCorrectRows, int PhaseComparedRows, int PhaseCorrectRows);
}
