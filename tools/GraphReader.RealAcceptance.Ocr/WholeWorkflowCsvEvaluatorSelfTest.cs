// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.Export;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record WholeWorkflowCsvEvaluatorSelfTestResult(
    string Status,
    bool SelfTest,
    int ScenarioCount,
    bool PrivateCorpusAccess,
    int ModelInferenceRuns,
    bool ValidAccepted,
    bool AnonymousSeriesPermutationAccepted,
    bool UnavailableEvidenceReported,
    bool CompleteDenominatorsPreserved,
    bool IntegrityFailuresRejected);

internal static class WholeWorkflowCsvEvaluatorSelfTest
{
    private const string SourceSha256 = "8e09d9e4c9a3c36b85f9b060728e643b3f1403431e381b51062318fb9977f752";
    private static readonly Guid BaselineRuntimeSeries = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid InterventionRuntimeSeries = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid SecondInterventionRuntimeSeries = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid PhaseA = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid PhaseB = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid RunId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid ProjectId = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid FirstPanelId = Guid.Parse("60000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondPanelId = Guid.Parse("60000000-0000-0000-0000-000000000002");
    private static readonly WholeWorkflowEvaluationOptions Options = new(1, 0.001, 0.001);
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    internal static WholeWorkflowCsvEvaluatorSelfTestResult Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "graphreader-whole-workflow-csv", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WholeWorkflowTruthCase truth = CreateTruth(authoritativePhases: true, authoritativeRelations: true);
            TestRow[] validRows = CreateRows();
            WholeWorkflowEvaluationResult valid = Evaluate(root, "valid", truth, validRows);
            Require(valid.ArtifactIntegrityValid && valid.CompletedCases == 1 && valid.FailedCases == 0 &&
                valid.ExpectedRows == 4 && valid.ActualRows == 4 && valid.CorrectRows == 4 &&
                valid.MatchedSeries == 2 && valid.MatchedPoints == 4 && valid.RelationalRowPrecision == 1 &&
                valid.RelationalRowCoverage == 1 && valid.MatchedRelationalGraphValueAccuracy == 1 &&
                valid.MatchedRelationalPhaseAccuracy == 1 && valid.UniquePointValueCoverage == 1,
                "valid output");

            TestRow[] permutedRows = validRows.Select(row => row with
            {
                SourceSeriesId = row.SourceSeriesId == BaselineRuntimeSeries
                    ? InterventionRuntimeSeries : BaselineRuntimeSeries,
                TargetInterventionSeriesId = row.TargetInterventionSeriesId == InterventionRuntimeSeries
                    ? BaselineRuntimeSeries : InterventionRuntimeSeries,
            }).ToArray();
            WholeWorkflowEvaluationResult permuted = Evaluate(root, "permuted", truth, permutedRows);
            Require(permuted.CorrectRows == 4 && permuted.MatchedSeries == 2, "anonymous whole-series permutation");

            WholeWorkflowEvaluationResult subset = Evaluate(root, "subset", truth, validRows[..^1]);
            Require(subset.MissingRows == 1 && subset.RelationalRowCoverage == .75, "subset output");

            WholeWorkflowEvaluationResult duplicate = Evaluate(root, "duplicate", truth, [.. validRows, validRows[0]]);
            Require(duplicate.DuplicateRows == 1 && duplicate.ExtraRows == 1 && duplicate.RelationalRowPrecision == .8,
                "duplicate output");

            TestRow extraRow = validRows[^1] with
            {
                PointId = Guid.Parse("30000000-0000-0000-0000-000000000099"),
                OriginalPixelX = 90,
                OriginalPixelY = 90,
            };
            WholeWorkflowEvaluationResult extra = Evaluate(root, "extra", truth, [.. validRows, extraRow]);
            Require(extra.ExtraRows == 1 && extra.PredictedPoints == 5 && extra.RelationalRowPrecision == .8, "extra output");

            WholeWorkflowCaseOutput primaryArtifact = WriteOutput(root, "extra-artifact", validRows);
            Guid extraSeries = Guid.Parse("10000000-0000-0000-0000-000000000099");
            WholeWorkflowCaseOutput secondaryArtifact = WriteOutput(
                root,
                "extra-artifact",
                [extraRow with { SourceSeriesId = extraSeries, TargetInterventionSeriesId = extraSeries }],
                stem: "case_extra");
            WholeWorkflowEvaluationResult extraArtifact = WholeWorkflowCsvEvaluator.Evaluate(
                [truth],
                [primaryArtifact with { Artifacts = [.. primaryArtifact.Artifacts, .. secondaryArtifact.Artifacts] }],
                Options,
                CancellationToken.None);
            Require(extraArtifact.ExtraRows == 1 && extraArtifact.ActualRows == 5 && extraArtifact.CorrectRows == 4,
                "extra artifact");

            WholeWorkflowTruthCase multiTargetTruth = CreateMultiTargetTruth();
            WholeWorkflowCaseOutput firstTarget = WriteOutput(root, "shared-copy", validRows);
            TestRow[] secondTargetRows =
            [
                validRows[0] with { TargetInterventionSeriesId = SecondInterventionRuntimeSeries },
                validRows[1] with { TargetInterventionSeriesId = SecondInterventionRuntimeSeries },
                Row("30000000-0000-0000-0000-000000000005", SecondInterventionRuntimeSeries,
                    PhaseB, 50, 50, 5, 40, "b", "intervention") with
                    { TargetInterventionSeriesId = SecondInterventionRuntimeSeries },
            ];
            WholeWorkflowCaseOutput secondTarget = WriteOutput(
                root, "shared-copy", secondTargetRows, stem: "case_second_intervention",
                panelId: SecondPanelId);
            WholeWorkflowCaseOutput sharedCopyOutput = firstTarget with
            {
                Artifacts = [.. firstTarget.Artifacts, .. secondTarget.Artifacts],
            };
            WholeWorkflowEvaluationResult sharedCopy = WholeWorkflowCsvEvaluator.Evaluate(
                [multiTargetTruth], [sharedCopyOutput], Options, CancellationToken.None);
            Require(sharedCopy.ArtifactIntegrityValid && sharedCopy.ActualRows == 7 &&
                sharedCopy.ExpectedRows == 7 && sharedCopy.CorrectRows == 7 &&
                sharedCopy.PredictedPoints == 5 && sharedCopy.MatchedPoints == 5 &&
                sharedCopy.UniquePointValueCorrect == 5,
                "consistent shared-baseline copies deduplicated by point ID");

            WholeWorkflowTruthCase mixedModeTruth = CreateMixedModeTruth();
            TestRow printedModeRow = Row(
                "30000000-0000-0000-0000-000000000011", InterventionRuntimeSeries,
                PhaseA, 10, 90, 1, 0, "a", "intervention");
            TestRow observationModeRow = Row(
                "30000000-0000-0000-0000-000000000012", SecondInterventionRuntimeSeries,
                PhaseB, 20, 80, 2, 10, "b", "intervention", "observation_order") with
                { TargetInterventionSeriesId = SecondInterventionRuntimeSeries };
            WholeWorkflowCaseOutput mixedPrinted = WriteOutput(
                root, "mixed-mode", [printedModeRow], stem: "case_printed");
            WholeWorkflowCaseOutput mixedObservation = WriteOutput(
                root, "mixed-mode", [observationModeRow], stem: "case_observation",
                panelId: SecondPanelId);
            WholeWorkflowEvaluationResult mixedMode = WholeWorkflowCsvEvaluator.Evaluate(
                [mixedModeTruth],
                [mixedPrinted with { Artifacts = [.. mixedPrinted.Artifacts, .. mixedObservation.Artifacts] }],
                Options,
                CancellationToken.None);
            Require(mixedMode.ArtifactIntegrityValid && mixedMode.CorrectRows == 2 &&
                mixedMode.UniquePointValueCorrect == 2 && mixedMode.WrongExportModeRows == 0,
                "mixed per-point export modes across panels");

            WholeWorkflowCaseOutput foreignRun = WriteOutput(
                root, "foreign-run", secondTargetRows, stem: "case_second_intervention",
                runId: Guid.Parse("40000000-0000-0000-0000-000000000099"), panelId: SecondPanelId);
            WholeWorkflowEvaluationResult foreignRunResult = WholeWorkflowCsvEvaluator.Evaluate(
                [multiTargetTruth],
                [firstTarget with { Artifacts = [.. firstTarget.Artifacts, .. foreignRun.Artifacts] }],
                Options,
                CancellationToken.None);
            Require(!foreignRunResult.ArtifactIntegrityValid && foreignRunResult.FailedCases == 1 &&
                foreignRunResult.IntegrityFailureCases == 1, "foreign run provenance");

            WholeWorkflowCaseOutput foreignProject = WriteOutput(
                root, "foreign-project", secondTargetRows, stem: "case_second_intervention",
                projectId: Guid.Parse("50000000-0000-0000-0000-000000000099"), panelId: SecondPanelId);
            WholeWorkflowEvaluationResult foreignProjectResult = WholeWorkflowCsvEvaluator.Evaluate(
                [multiTargetTruth],
                [firstTarget with { Artifacts = [.. firstTarget.Artifacts, .. foreignProject.Artifacts] }],
                Options,
                CancellationToken.None);
            Require(!foreignProjectResult.ArtifactIntegrityValid && foreignProjectResult.FailedCases == 1 &&
                foreignProjectResult.IntegrityFailureCases == 1, "foreign project provenance");

            WholeWorkflowCaseOutput inconsistentSecondTarget = WriteOutput(
                root,
                "inconsistent-copy",
                secondTargetRows.Select((row, index) => index == 0 ? row with { YValue = 999 } : row).ToArray(),
                stem: "case_second_intervention");
            WholeWorkflowCaseOutput inconsistentFirstTarget = WriteOutput(root, "inconsistent-copy", validRows);
            WholeWorkflowEvaluationResult inconsistentCopy = WholeWorkflowCsvEvaluator.Evaluate(
                [multiTargetTruth],
                [inconsistentFirstTarget with
                {
                    Artifacts = [.. inconsistentFirstTarget.Artifacts, .. inconsistentSecondTarget.Artifacts],
                }],
                Options,
                CancellationToken.None);
            Require(!inconsistentCopy.ArtifactIntegrityValid && inconsistentCopy.IntegrityFailureCases == 1,
                "inconsistent shared-baseline copies");

            TestRow[] wrongScaleRows = validRows.Select((row, index) => index == 2 ? row with { YValue = 200 } : row).ToArray();
            WholeWorkflowEvaluationResult wrongScale = Evaluate(root, "wrong-scale", truth, wrongScaleRows);
            Require(wrongScale.WrongScaleRows == 1 && wrongScale.MatchedRelationalGraphValueAccuracy == .75 &&
                wrongScale.CorrectRows == 3, "wrong scale");

            TestRow[] wrongPhaseRows = validRows.Select((row, index) => index == 2 ? row with { Phase = "a" } : row).ToArray();
            WholeWorkflowEvaluationResult wrongPhase = Evaluate(root, "wrong-phase", truth, wrongPhaseRows);
            Require(wrongPhase.WrongPhaseRows == 1 && wrongPhase.MatchedRelationalPhaseAccuracy == .75 &&
                wrongPhase.CorrectRows == 3, "phase mix-up");

            TestRow[] wrongRelationRows = validRows.Select((row, index) => index == 0 ? row with { Inclusion = "intervention" } : row).ToArray();
            WholeWorkflowEvaluationResult wrongRelation = Evaluate(root, "wrong-relation", truth, wrongRelationRows);
            Require(wrongRelation.WrongRelationRows == 1 && wrongRelation.CorrectRows == 3,
                "series relation mix-up");

            TestRow[] mixedRows = validRows.Select((row, index) => index == 3
                ? row with { SourceSeriesId = BaselineRuntimeSeries }
                : row).ToArray();
            WholeWorkflowEvaluationResult mixed = Evaluate(root, "mixed-series", truth, mixedRows);
            Require(mixed.MatchedSeries == 2 && mixed.MatchedPoints == 3 && mixed.MissingRows == 1 &&
                mixed.ExtraRows == 1 && mixed.CorrectRows == 3, "mixed runtime series");

            var failedOutput = new WholeWorkflowCaseOutput("case", SourceSha256, false, "WORKFLOW_FAILED", []);
            WholeWorkflowEvaluationResult failed = WholeWorkflowCsvEvaluator.Evaluate([truth], [failedOutput], Options,
                CancellationToken.None);
            Require(failed.FailedCases == 1 && failed.MissingRows == 4 && failed.ActualRows == 0 &&
                failed.UniquePointValuePrecision is null && failed.RelationalRowPrecision is null &&
                failed.UniquePointValueCoverage == 0 && failed.RelationalRowCoverage == 0,
                "failed-case denominator");

            WholeWorkflowCaseOutput failedWithArtifacts = WriteOutput(root, "failed-with-artifacts", validRows) with
            {
                WorkflowSucceeded = false,
                FailureCode = "WORKFLOW_FAILED_AFTER_PARTIAL_EXPORT",
            };
            WholeWorkflowEvaluationResult failedArtifactResult = WholeWorkflowCsvEvaluator.Evaluate(
                [truth], [failedWithArtifacts], Options, CancellationToken.None);
            Require(failedArtifactResult.FailedCases == 1 && failedArtifactResult.CorrectRows == 0 &&
                failedArtifactResult.MissingRows == 4 && failedArtifactResult.ExtraRows == 4 &&
                failedArtifactResult.UniquePointValueCorrect == 0 &&
                failedArtifactResult.UniquePointValueCoverage == 0 &&
                failedArtifactResult.MatchedUniquePointValueAccuracy is null &&
                failedArtifactResult.ResidualArtifactRowsFromFailedCases == 4,
                "failed case with valid residual artifacts");

            WholeWorkflowEvaluationResult absent = WholeWorkflowCsvEvaluator.Evaluate([truth], [], Options,
                CancellationToken.None);
            Require(absent.FailedCases == 1 && absent.MissingRows == 4 && absent.ActualRows == 0,
                "absent-case denominator");

            WholeWorkflowTruthCase unavailableTruth = CreateTruth(authoritativePhases: false, authoritativeRelations: false);
            WholeWorkflowEvaluationResult unavailable = Evaluate(root, "unavailable", unavailableTruth, validRows);
            Require(unavailable.UniquePointValueMetricsAvailable && !unavailable.RelationalRowMetricsAvailable &&
                !unavailable.RelationalPhaseMetricsAvailable && unavailable.ExpectedRows is null &&
                unavailable.RelationalRowPrecision is null && unavailable.MatchedRelationalPhaseAccuracy is null &&
                unavailable.MatchedPoints == 4 && unavailable.UniquePointValueCoverage == 1,
                "unavailable phase and relation evidence");

            WholeWorkflowEvaluationResult unavailableWrongScale = Evaluate(
                root,
                "unavailable-wrong-scale",
                unavailableTruth,
                validRows.Select((row, index) => index == 1 ? row with { YValue = 999 } : row).ToArray());
            Require(unavailableWrongScale.RelationalRowCoverage is null &&
                unavailableWrongScale.UniquePointValueCorrect == 3 &&
                unavailableWrongScale.UniquePointWrongScale == 1 &&
                unavailableWrongScale.UniquePointValueCoverage == .75 &&
                unavailableWrongScale.MatchedUniquePointValueAccuracy == .75,
                "unknown relations with wrong scale");

            WholeWorkflowEvaluationResult unavailableSubset = Evaluate(
                root, "unavailable-subset", unavailableTruth, validRows[..^1]);
            Require(unavailableSubset.RelationalRowCoverage is null && unavailableSubset.UniquePointMissing == 1 &&
                unavailableSubset.UniquePointValueCoverage == .75 &&
                unavailableSubset.UniquePointValuePrecision == 1,
                "unknown relations with subset output");

            WholeWorkflowCaseOutput checksumOutput = WriteOutput(root, "checksum", validRows);
            checksumOutput = checksumOutput with
            {
                Artifacts = checksumOutput.Artifacts.Select((artifact, index) => index == 0
                    ? artifact with { Sha256 = new string('0', 64) }
                    : artifact).ToArray(),
            };
            WholeWorkflowEvaluationResult checksum = WholeWorkflowCsvEvaluator.Evaluate([truth], [checksumOutput], Options,
                CancellationToken.None);
            Require(!checksum.ArtifactIntegrityValid && checksum.IntegrityFailureCases == 1 &&
                checksum.FailedCases == 1 && checksum.CompletedCases == 0 &&
                checksum.ExpectedRows is null, "checksum mismatch and failed-case conservation");

            WholeWorkflowCaseOutput projectionOutput = WriteOutput(root, "projection", validRows,
                minimalRows: validRows.Select((row, index) => index == 0 ? row with { YValue = 999 } : row).ToArray());
            WholeWorkflowEvaluationResult projection = WholeWorkflowCsvEvaluator.Evaluate([truth], [projectionOutput], Options,
                CancellationToken.None);
            Require(!projection.ArtifactIntegrityValid && projection.IntegrityFailureCases == 1,
                "minimal/audit projection mismatch");

            WholeWorkflowCaseOutput collisionOutput = WriteOutput(root, "collision", validRows);
            collisionOutput = collisionOutput with { Artifacts = [.. collisionOutput.Artifacts, collisionOutput.Artifacts[0]] };
            WholeWorkflowEvaluationResult collision = WholeWorkflowCsvEvaluator.Evaluate([truth], [collisionOutput], Options,
                CancellationToken.None);
            Require(!collision.ArtifactIntegrityValid && collision.IntegrityFailureCases == 1,
                "artifact path collision");

            WholeWorkflowCaseOutput rowCountOutput = WriteOutput(root, "row-count", validRows);
            rowCountOutput = rowCountOutput with
            {
                Artifacts = rowCountOutput.Artifacts.Select((artifact, index) => index == 0
                    ? artifact with { RowCount = artifact.RowCount + 1 }
                    : artifact).ToArray(),
            };
            WholeWorkflowEvaluationResult rowCount = WholeWorkflowCsvEvaluator.Evaluate([truth], [rowCountOutput], Options,
                CancellationToken.None);
            Require(!rowCount.ArtifactIntegrityValid && rowCount.IntegrityFailureCases == 1,
                "artifact row-count mismatch");

            const int scenarios = 25;
            return new WholeWorkflowCsvEvaluatorSelfTestResult(
                "pass", true, scenarios, false, 0, true, true, true, true, true);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static WholeWorkflowEvaluationResult Evaluate(
        string root,
        string scenario,
        WholeWorkflowTruthCase truth,
        IReadOnlyList<TestRow> rows)
    {
        WholeWorkflowCaseOutput output = WriteOutput(root, scenario, rows);
        return WholeWorkflowCsvEvaluator.Evaluate([truth], [output], Options, CancellationToken.None);
    }

    private static WholeWorkflowTruthCase CreateTruth(bool authoritativePhases, bool authoritativeRelations)
    {
        string? a = authoritativePhases ? "a" : null;
        string? b = authoritativePhases ? "b" : null;
        return new WholeWorkflowTruthCase(
            "case",
            SourceSha256,
            100,
            100,
            [new("baseline"), new("intervention")],
            [
                new("b1", "baseline", 10, 90, 101, 0, 1, ExportMode.PrintedSession, a),
                new("b2", "baseline", 20, 80, 102, 10, 2, ExportMode.PrintedSession, a),
                new("i1", "intervention", 30, 70, 103, 20, 3, ExportMode.PrintedSession, b),
                new("i2", "intervention", 40, 60, 104, 30, 4, ExportMode.PrintedSession, b),
            ],
            authoritativeRelations
                ? [new WholeWorkflowTruthRelation("intervention", "baseline", [])]
                : null);
    }

    private static WholeWorkflowTruthCase CreateMultiTargetTruth() => new(
        "case",
        SourceSha256,
        100,
        100,
        [new("baseline"), new("intervention"), new("second-intervention")],
        [
            new("b1", "baseline", 10, 90, 101, 0, 1, ExportMode.PrintedSession, "a"),
            new("b2", "baseline", 20, 80, 102, 10, 2, ExportMode.PrintedSession, "a"),
            new("i1", "intervention", 30, 70, 103, 20, 3, ExportMode.PrintedSession, "b"),
            new("i2", "intervention", 40, 60, 104, 30, 4, ExportMode.PrintedSession, "b"),
            new("j1", "second-intervention", 50, 50, 105, 40, 5, ExportMode.PrintedSession, "b"),
        ],
        [
            new WholeWorkflowTruthRelation("intervention", "baseline", []),
            new WholeWorkflowTruthRelation("second-intervention", "baseline", []),
        ]);

    private static WholeWorkflowTruthCase CreateMixedModeTruth() => new(
        "case",
        SourceSha256,
        100,
        100,
        [new("intervention"), new("second-intervention")],
        [
            new("i1", "intervention", 10, 90, 101, 0, 1, ExportMode.PrintedSession, "a"),
            new("j1", "second-intervention", 20, 80, 102, 10, 2, ExportMode.ObservationOrder, "b"),
        ],
        [
            new WholeWorkflowTruthRelation("intervention", null, []),
            new WholeWorkflowTruthRelation("second-intervention", null, []),
        ]);

    private static TestRow[] CreateRows() =>
    [
        Row("30000000-0000-0000-0000-000000000001", BaselineRuntimeSeries, PhaseA, 10, 90, 1, 0, "a", "shared_baseline"),
        Row("30000000-0000-0000-0000-000000000002", BaselineRuntimeSeries, PhaseA, 20, 80, 2, 10, "a", "shared_baseline"),
        Row("30000000-0000-0000-0000-000000000003", InterventionRuntimeSeries, PhaseB, 30, 70, 3, 20, "b", "intervention"),
        Row("30000000-0000-0000-0000-000000000004", InterventionRuntimeSeries, PhaseB, 40, 60, 4, 30, "b", "intervention"),
    ];

    private static TestRow Row(
        string pointId, Guid sourceSeries, Guid phaseId, double pixelX, double pixelY,
        double x, double y, string phase, string inclusion,
        string exportMode = "printed_session") =>
        new(x, y, phase, Guid.Parse(pointId), sourceSeries, InterventionRuntimeSeries, phaseId,
            pixelX, pixelY, inclusion, exportMode);

    private static WholeWorkflowCaseOutput WriteOutput(
        string root,
        string scenario,
        IReadOnlyList<TestRow> auditRows,
        IReadOnlyList<TestRow>? minimalRows = null,
        string stem = "case_intervention",
        Guid? runId = null,
        Guid? projectId = null,
        Guid? panelId = null)
    {
        string directory = Path.Combine(root, scenario);
        Directory.CreateDirectory(directory);
        byte[] minimal = Encoding.UTF8.GetBytes(MinimalCsv(minimalRows ?? auditRows));
        byte[] auditCsv = Encoding.UTF8.GetBytes(AuditCsv(auditRows));
        byte[] auditJson = AuditJson(
            auditRows, runId ?? RunId, projectId ?? ProjectId, panelId ?? FirstPanelId);
        var artifacts = new List<WholeWorkflowCsvArtifact>
        {
            Write(directory, stem + ".csv", minimal, auditRows.Count),
            Write(directory, stem + ".audit.csv", auditCsv, auditRows.Count),
            Write(directory, stem + ".audit.json", auditJson, auditRows.Count),
        };
        return new WholeWorkflowCaseOutput("case", SourceSha256, true, null, artifacts);
    }

    private static WholeWorkflowCsvArtifact Write(string directory, string name, byte[] bytes, int rows)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return new WholeWorkflowCsvArtifact(name, Hash(bytes), rows, path);
    }

    private static string MinimalCsv(IEnumerable<TestRow> rows)
    {
        var result = new StringBuilder("x_value,y_value,phase\n");
        foreach (TestRow row in rows)
            result.Append(Number(row.XValue)).Append(',').Append(Number(row.YValue)).Append(',')
                .Append(Csv(row.Phase)).Append('\n');
        return result.ToString();
    }

    private static string AuditCsv(IEnumerable<TestRow> rows)
    {
        var result = new StringBuilder(
            "x_value,y_value,phase,point_id,source_series_id,target_intervention_series_id," +
            "phase_id,original_pixel_x,original_pixel_y,x_source,x_confidence,y_confidence," +
            "point_confidence,review_status,inclusion,export_mode,calibration_status," +
            "session_origin_override_applied,session_origin_override_reason," +
            "session_origin_override_confirmed_at_utc,series_symbol,series_name,source_stage,model_version\n");
        foreach (TestRow row in rows)
        {
            result.Append(Number(row.XValue)).Append(',').Append(Number(row.YValue)).Append(',')
                .Append(Csv(row.Phase)).Append(',').Append(row.PointId.ToString("D")).Append(',')
                .Append(row.SourceSeriesId.ToString("D")).Append(',')
                .Append(row.TargetInterventionSeriesId.ToString("D")).Append(',')
                .Append(row.PhaseId.ToString("D")).Append(',')
                .Append(Number(row.OriginalPixelX)).Append(',').Append(Number(row.OriginalPixelY))
                .Append(row.ExportMode == "observation_order" ? ",observation_order" : ",printed")
                .Append(",0.99,0.99,0.99,unreviewed,").Append(row.Inclusion)
                .Append(',').Append(row.ExportMode)
                .Append(",valid,false,,,circle,Intervention,markers,candidate-v1\n");
        }
        return result.ToString();
    }

    private static byte[] AuditJson(
        IReadOnlyList<TestRow> rows,
        Guid runId,
        Guid projectId,
        Guid panelId)
    {
        Guid targetInterventionSeriesId = rows.Select(static row => row.TargetInterventionSeriesId)
            .Distinct().Single();
        string exportMode = rows.Select(static row => row.ExportMode).Distinct(StringComparer.Ordinal).Single();
        var payload = new
        {
            contract_version = 1,
            run_id = runId,
            project_id = projectId,
            panel_id = panelId,
            intervention_series_id = targetInterventionSeriesId,
            export_mode = exportMode,
            series_symbol = "circle",
            series_name = "Intervention",
            coordinate_space = "original_pixels",
            row_count = rows.Count,
            rows = rows.Select(row => new
            {
                x_value = row.XValue,
                y_value = row.YValue,
                phase = row.Phase,
                point_id = row.PointId,
                source_series_id = row.SourceSeriesId,
                target_intervention_series_id = row.TargetInterventionSeriesId,
                phase_id = row.PhaseId,
                original_pixel_x = row.OriginalPixelX,
                original_pixel_y = row.OriginalPixelY,
                x_source = row.ExportMode == "observation_order" ? "observation_order" : "printed",
                x_confidence = .99,
                y_confidence = .99,
                point_confidence = .99,
                review_status = "unreviewed",
                inclusion = row.Inclusion,
                export_mode = row.ExportMode,
                calibration_status = "valid",
                session_origin_override_applied = false,
                session_origin_override_reason = (string?)null,
                session_origin_override_confirmed_at_utc = (DateTimeOffset?)null,
                series_symbol = "circle",
                series_name = "Intervention",
                source_stage = "markers",
                model_version = "candidate-v1",
            }).ToArray(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, IndentedJson);
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Csv(string value)
    {
        string safe = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return safe.IndexOfAny([',', '"', '\n', '\r']) < 0 ? safe : '"' + safe.Replace("\"", "\"\"") + '"';
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Require(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException($"WHOLE_WORKFLOW_CSV_SELF_TEST_FAILED:{scenario}");
    }

    private sealed record TestRow(
        double XValue,
        double YValue,
        string Phase,
        Guid PointId,
        Guid SourceSeriesId,
        Guid TargetInterventionSeriesId,
        Guid PhaseId,
        double OriginalPixelX,
        double OriginalPixelY,
        string Inclusion,
        string ExportMode);
}
