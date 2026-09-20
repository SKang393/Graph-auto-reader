// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Export.Tests;

[TestClass]
public sealed class ExportServiceAcceptanceTests
{
    private static readonly Guid RunId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ProjectId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid PanelId = Guid.Parse("30000000-0000-0000-0000-000000000001");

    private static readonly Guid PhaseA = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid PhaseB = Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly Guid PhaseA2 = Guid.Parse("40000000-0000-0000-0000-000000000003");
    private static readonly Guid PhaseB2 = Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static readonly Guid PhaseUnknown = Guid.Parse("40000000-0000-0000-0000-000000000005");
    private static readonly Guid PhaseMaintenance = Guid.Parse("40000000-0000-0000-0000-000000000006");
    private static readonly Guid PhaseGeneralization = Guid.Parse("40000000-0000-0000-0000-000000000007");

    private static readonly Guid BaselineSeriesId = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid InterventionOneId = Guid.Parse("50000000-0000-0000-0000-000000000002");
    private static readonly Guid InterventionTwoId = Guid.Parse("50000000-0000-0000-0000-000000000003");
    private static readonly Guid MaintenanceSeriesId = Guid.Parse("50000000-0000-0000-0000-000000000004");
    private static readonly Guid GeneralizationSeriesId = Guid.Parse("50000000-0000-0000-0000-000000000005");

    private static readonly Guid PointOne = Guid.Parse("60000000-0000-0000-0000-000000000001");
    private static readonly Guid PointTwo = Guid.Parse("60000000-0000-0000-0000-000000000002");
    private static readonly Guid PointThree = Guid.Parse("60000000-0000-0000-0000-000000000003");
    private static readonly Guid PointFour = Guid.Parse("60000000-0000-0000-0000-000000000004");
    private static readonly Guid PointFive = Guid.Parse("60000000-0000-0000-0000-000000000005");
    private static readonly Guid PointSix = Guid.Parse("60000000-0000-0000-0000-000000000006");
    private static readonly Guid PointSeven = Guid.Parse("60000000-0000-0000-0000-000000000007");
    private static readonly Guid PointEight = Guid.Parse("60000000-0000-0000-0000-000000000008");

    private static readonly string[] OneInterventionExpectedRows = ["1|11|a", "2|12|a", "3|21|b", "4|22|b"];
    private static readonly string[] ExpectedProbePhases = ["a", "b", "m1", "g1"];
    private static readonly Guid[] ExpectedProbeSeriesIds = [MaintenanceSeriesId, GeneralizationSeriesId];
    private static readonly string[] ExpectedAbabUnknownPhases = ["a1", "b1", "a2", "b2", "phase5"];
    private static readonly double[] ExpectedObservationOrder = [1d, 2d, 3d, 4d];
    private static readonly double[] ExpectedCalibratedGaps = [1d, 3d, 7d, 8d];
    private static readonly double[] ExpectedEqualSessionYOrder = [70d, 40d];
    private static readonly ExportXValueSource[] ExpectedMixedXSources =
        [ExportXValueSource.Printed, ExportXValueSource.Estimated, ExportXValueSource.Estimated, ExportXValueSource.Printed];

    [TestMethod]
    [DataRow(ExportOperation.Preview, false)]
    [DataRow(ExportOperation.WriteFiles, false)]
    [DataRow(ExportOperation.Preview, true)]
    [DataRow(ExportOperation.WriteFiles, true)]
    public async Task MissingOrEmptyInterventionCannotReportASuccessfulExport(ExportOperation operation, bool emptyIntervention)
    {
        Scenario original = OneInterventionScenario();
        Scenario scenario = emptyIntervention
            ? new(original.Phases,
                [new ExportSeries(InterventionOneId, "circle", "Series", ExportSeriesRole.Intervention, [], 0.95)],
                [], [new ExportSeriesRelation(InterventionOneId, null, [])])
            : new(original.Phases,
                original.Series.Where(static item => item.SemanticRole == ExportSeriesRole.Baseline).ToArray(),
                original.Points.Where(static item => item.SeriesId == BaselineSeriesId).ToArray(), []);
        string output = NewTemporaryDirectoryPath();
        try
        {
            ExportResult result = await ExportAsync(scenario, outputDirectory: output, operation: operation);
            Assert.IsFalse(result.Succeeded, "An empty export must require review, not report success.");
            Assert.IsEmpty(result.MinimalArtifacts);
            Assert.IsEmpty(result.AuditArtifacts);
            Assert.AreEqual(0, ExistingFileCount(output));
            Assert.IsTrue(result.Failures.Any(static failure => failure.Code is "NO_EXPORTABLE_SERIES" or "EMPTY_SELECTED_SERIES"));
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(output);
        }
    }

    [TestMethod]
    public async Task OneInterventionExportsSharedBaselineAndInterventionInPhaseOrder()
    {
        Scenario scenario = OneInterventionScenario();

        ExportResult result = await ExportAsync(scenario);

        Assert.IsTrue(result.Succeeded, FailureSummary(result));
        Assert.AreEqual(1, result.MinimalArtifacts.Count);
        MinimalCsvArtifact artifact = Minimal(result, InterventionOneId);
        CollectionAssert.AreEqual(
            OneInterventionExpectedRows,
            artifact.Rows.Select(RowKey).ToArray());
    }

    [TestMethod]
    public async Task TwoInterventionsDuplicateSharedBaselineOnceWithoutInterventionLeakage()
    {
        Scenario scenario = TwoInterventionScenario();

        ExportResult result = await ExportAsync(scenario, auditMode: ExportAuditMode.ExtendedCsv);

        Assert.IsTrue(result.Succeeded, FailureSummary(result));
        Assert.AreEqual(2, result.MinimalArtifacts.Count);
        AssertSeriesMembership(result, InterventionOneId, [PointOne, PointTwo], [PointThree, PointFour]);
        AssertSeriesMembership(result, InterventionTwoId, [PointOne, PointTwo], [PointFive, PointSix]);
        Assert.IsFalse(Audit(result, InterventionOneId).Rows.Any(row => row.SourceSeriesId == InterventionTwoId));
        Assert.IsFalse(Audit(result, InterventionTwoId).Rows.Any(row => row.SourceSeriesId == InterventionOneId));
    }

    [TestMethod]
    public async Task ApplicableMaintenanceAndGeneralizationAreIncludedWithProbeProvenance()
    {
        Scenario scenario = ProbeScenario();

        ExportResult result = await ExportAsync(scenario, auditMode: ExportAuditMode.ExtendedCsv);

        Assert.IsTrue(result.Succeeded, FailureSummary(result));
        ExtendedAuditArtifact audit = Audit(result, InterventionOneId);
        CollectionAssert.AreEqual(
            ExpectedProbePhases,
            audit.Rows.Select(static row => row.Phase).ToArray());
        CollectionAssert.AreEquivalent(
            ExpectedProbeSeriesIds,
            audit.Rows
                .Where(static row => row.Inclusion == ExportRowInclusion.ApplicableProbe)
                .Select(static row => row.SourceSeriesId)
                .ToArray());
    }

    [TestMethod]
    public async Task AbabAndUnknownPhasePreserveExplicitPhaseCodesAndOrder()
    {
        Scenario scenario = AbabUnknownScenario();

        ExportResult result = await ExportAsync(scenario);

        Assert.IsTrue(result.Succeeded, FailureSummary(result));
        CollectionAssert.AreEqual(
            ExpectedAbabUnknownPhases,
            Minimal(result, InterventionOneId).Rows.Select(static row => row.Phase).ToArray());
    }

    [TestMethod]
    public async Task InvalidSessionOriginBlocksPreviewAndWritesUntilFullyExplicitOverride()
    {
        string outputDirectory = NewTemporaryDirectoryPath();
        Scenario scenario = OneInterventionScenario();
        var invalidCalibration = new ExportCalibration(
            ExportCalibrationStatus.InvalidSessionOrigin,
            hasYCalibration: true,
            hasPrintedSessionCalibration: true,
            hasAbsoluteSessionOrigin: true,
            firstObservedSession: 2,
            confidence: 0.94,
            reasons: ["First observed session is 2."]);

        try
        {
            ExportResult preview = await ExportAsync(
                scenario,
                outputDirectory,
                operation: ExportOperation.Preview,
                calibration: invalidCalibration);
            Assert.IsTrue(preview.Succeeded, FailureSummary(preview));
            Assert.IsTrue(preview.Preview.FinalExportBlocked);
            Assert.AreEqual(1, preview.Preview.Files.Count);
            Assert.AreEqual(4, preview.Preview.Files[0].RowCount);
            Assert.IsFalse(Directory.Exists(outputDirectory));

            ExportResult blockedWrite = await ExportAsync(
                scenario,
                outputDirectory,
                operation: ExportOperation.WriteFiles,
                calibration: invalidCalibration);
            Assert.IsTrue(blockedWrite.Preview.FinalExportBlocked);
            AssertHasFailure(blockedWrite, "INVALID_SESSION_ORIGIN");
            Assert.IsFalse(blockedWrite.MinimalArtifacts.Any(static artifact => artifact.WrittenPath is not null));
            Assert.AreEqual(0, ExistingFileCount(outputDirectory));

            var incompleteOverride = new ExportSessionOriginPolicy(
                RequireFirstObservedSessionOne: true,
                InvalidOriginBehavior: InvalidSessionOriginBehavior.AllowWithExplicitOverride,
                OverrideReason: "Reviewed against the source graph.",
                OverrideConfirmedAtUtc: null);
            ExportResult incomplete = await ExportAsync(
                scenario,
                outputDirectory,
                operation: ExportOperation.WriteFiles,
                calibration: invalidCalibration,
                policy: incompleteOverride);
            AssertHasFailure(incomplete, "INVALID_SESSION_ORIGIN");
            Assert.AreEqual(0, ExistingFileCount(outputDirectory));

            var explicitOverride = incompleteOverride with
            {
                OverrideConfirmedAtUtc = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero),
            };
            ExportResult written = await ExportAsync(
                scenario,
                outputDirectory,
                auditMode: ExportAuditMode.ExtendedCsv,
                operation: ExportOperation.WriteFiles,
                calibration: invalidCalibration,
                policy: explicitOverride);
            Assert.IsTrue(written.Succeeded, FailureSummary(written));
            Assert.IsFalse(written.Preview.FinalExportBlocked);
            Assert.IsTrue(written.MinimalArtifacts.All(static artifact => artifact.WrittenPath is not null));
            Assert.IsTrue(
                written.AuditArtifacts.Single().Rows.All(static row =>
                    row.CalibrationStatus == ExportCalibrationStatus.InvalidSessionOrigin &&
                    row.SessionOriginOverrideApplied &&
                    row.SessionOriginOverrideReason == "Reviewed against the source graph." &&
                    row.SessionOriginOverrideConfirmedAtUtc.HasValue));
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(outputDirectory);
        }
    }

    [TestMethod]
    public async Task PrintedSessionPreservesEstimatedGapsAndExplicitAuditProvenance()
    {
        Scenario scenario = OneInterventionScenario();
        scenario = scenario with
        {
            Points = scenario.Points.Select(static point =>
            {
                double x = ExpectedCalibratedGaps[point.ObservationIndex - 1];
                bool inferred = point.ObservationIndex is 2 or 3;
                return point with
                {
                    GraphX = x,
                    OriginalPixel = new ExportPixelPoint(100 + (x * 10), point.OriginalPixel.Y),
                    PrintedXValue = inferred ? null : x,
                    EstimatedXValue = inferred ? x : null,
                    XSource = inferred ? ExportXValueSource.Estimated : ExportXValueSource.Printed,
                };
            }).ToArray(),
        };
        ExportResult result = await ExportAsync(scenario, auditMode: ExportAuditMode.ExtendedCsvAndJson);
        Assert.IsTrue(result.Succeeded, FailureSummary(result));
        CollectionAssert.AreEqual(ExpectedCalibratedGaps, Minimal(result, InterventionOneId).Rows.Select(static row => row.XValue).ToArray());
        CollectionAssert.AreEqual(ExpectedMixedXSources, AuditRows(result, InterventionOneId).Select(static row => row.XSource).ToArray());
        ExtendedAuditArtifact json = result.AuditArtifacts.Single(static artifact => artifact.Format == ExportAuditArtifactFormat.Json);
        using JsonDocument document = JsonDocument.Parse(json.Content);
        Assert.AreEqual(2, document.RootElement.GetProperty("rows").EnumerateArray().Count(static row =>
            row.GetProperty("x_source").GetString() == "estimated"));
        Assert.IsTrue(scenario.Points.Where(static point => point.XSource == ExportXValueSource.Estimated)
            .All(static point => point.PrintedXValue is null), "Export must not promote an estimate into printed evidence.");
    }

    [TestMethod]
    [DataRow(ExportXValueSource.Unknown)]
    [DataRow(ExportXValueSource.ObservationOrder)]
    [DataRow(ExportXValueSource.Printed)]
    public async Task PrintedSessionDoesNotUseAnEstimateWithoutEstimatedProvenance(ExportXValueSource source)
    {
        Scenario scenario = OneInterventionScenario();
        scenario = scenario with { Points = scenario.Points.Select(point => point with
        { PrintedXValue = null, EstimatedXValue = point.GraphX, XSource = source }).ToArray() };
        ExportResult result = await ExportAsync(scenario);
        AssertHasFailure(result, "INVALID_POINT");
        Assert.IsEmpty(result.MinimalArtifacts);
    }

    [TestMethod]
    public async Task PrintedSessionDoesNotInferMissingValuesFromGraphXOrObservationOrder()
    {
        Scenario scenario = OneInterventionScenario();
        scenario = scenario with { Points = scenario.Points.Select(static point => point with
        { PrintedXValue = null, EstimatedXValue = null, XSource = ExportXValueSource.Estimated }).ToArray() };
        ExportResult result = await ExportAsync(scenario);
        AssertHasFailure(result, "INVALID_POINT");
    }

    [TestMethod]
    public async Task EstimatedSessionsStillRequireCalibrationAndSessionOneOrigin()
    {
        Scenario scenario = OneInterventionScenario();
        scenario = scenario with { Points = scenario.Points.Select(static point => point with
        { PrintedXValue = null, XSource = ExportXValueSource.Estimated }).ToArray() };
        var missingCalibration = new ExportCalibration(ExportCalibrationStatus.Valid, true, false, true, 1, 0.99);
        ExportResult missing = await ExportAsync(scenario, calibration: missingCalibration);
        AssertHasFailure(missing, "PRINTED_SESSION_CALIBRATION_REQUIRED");

        string output = NewTemporaryDirectoryPath();
        try
        {
            var invalidOrigin = new ExportCalibration(ExportCalibrationStatus.InvalidSessionOrigin, true, true, true, 2, 0.99);
            ExportResult blocked = await ExportAsync(scenario, output, operation: ExportOperation.WriteFiles, calibration: invalidOrigin);
            AssertHasFailure(blocked, "INVALID_SESSION_ORIGIN");
            Assert.AreEqual(0, ExistingFileCount(output));
        }
        finally { DeleteOwnedTemporaryDirectory(output); }
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public async Task NonfiniteEstimatedSessionsRemainRejected(double value)
    {
        Scenario scenario = OneInterventionScenario();
        scenario = scenario with { Points = scenario.Points.Select(point => point with
        { PrintedXValue = null, EstimatedXValue = value, XSource = ExportXValueSource.Estimated }).ToArray() };
        ExportResult result = await ExportAsync(scenario);
        AssertHasFailure(result, "INVALID_POINT");
    }

    [TestMethod]
    public async Task ObservationOrderUsesObservationIndexInsteadOfPrintedOrEstimatedX()
    {
        Scenario scenario = OneInterventionScenario() with
        {
            Points = OneInterventionScenario().Points
                .Select(static point => point with
                {
                    ObservationIndex = (point.ObservationIndex * 10) + 5,
                    PrintedXValue = point.PrintedXValue + 100,
                    EstimatedXValue = point.EstimatedXValue + 200,
                })
                .ToArray(),
        };

        ExportResult result = await ExportAsync(
            scenario,
            mode: ExportMode.ObservationOrder,
            auditMode: ExportAuditMode.ExtendedCsv);

        Assert.IsTrue(result.Succeeded, FailureSummary(result));
        CollectionAssert.AreEqual(
            ExpectedObservationOrder,
            Minimal(result, InterventionOneId).Rows.Select(static row => row.XValue).ToArray());
        Assert.IsTrue(
            AuditRows(result, InterventionOneId).All(
                static row => row.XSource == ExportXValueSource.ObservationOrder));
    }

    [TestMethod]
    public async Task UnicodeSymbolsRemainMetadataWhileDuplicateSanitizedNamesStaySafeAndUnique()
    {
        Scenario scenario = DuplicateNameScenario();

        ExportResult result = await ExportAsync(scenario);

        Assert.IsTrue(result.Succeeded, FailureSummary(result));
        Assert.AreEqual("●", Minimal(result, InterventionOneId).SeriesSymbol);
        Assert.AreEqual("○", Minimal(result, InterventionTwoId).SeriesSymbol);

        string[] fileNames = result.MinimalArtifacts.Select(static artifact => artifact.FileName).ToArray();
        Assert.AreEqual(2, fileNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsTrue(fileNames.All(static fileName => fileName.EndsWith(".csv", StringComparison.Ordinal)));
        Assert.IsTrue(fileNames.All(static fileName => fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0));
        Assert.IsTrue(fileNames.All(static fileName => !fileName.Contains('●') && !fileName.Contains('○')));
        Assert.IsTrue(fileNames.Any(static fileName => fileName.EndsWith("-2.csv", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FilenameScopeIsSafeAndDoesNotChangeSerializedData(bool longPrefix)
    {
        Scenario scenario = DuplicateNameScenario();
        ExportRequest original = Request(scenario, auditMode: ExportAuditMode.ExtendedCsvAndJson);
        string prefix = longPrefix ? "panel-001_" + new string('p', 300) : "../CON/<panel>";
        ExportRequest scoped = Request(scenario, auditMode: ExportAuditMode.ExtendedCsvAndJson, fileNamePrefix: prefix);
        var service = new ExportService();
        ExportResult before = await service.ExportAsync(original, CancellationToken.None);
        ExportResult after = await service.ExportAsync(scoped, CancellationToken.None);

        Assert.IsTrue(before.Succeeded, FailureSummary(before));
        Assert.IsTrue(after.Succeeded, FailureSummary(after));
        Assert.AreEqual(original.Participant, scoped.Participant);
        string[] names = after.MinimalArtifacts.Select(static a => a.FileName)
            .Concat(after.AuditArtifacts.Select(static a => a.FileName)).ToArray();
        Assert.AreEqual(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsTrue(names.All(static n => n.Length <= 240 && Path.GetFileName(n) == n && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0));
        if (longPrefix)
        {
            Assert.IsTrue(names.All(static n => n.StartsWith("panel-001_", StringComparison.Ordinal)));
        }
        CollectionAssert.AreEqual(
            before.MinimalArtifacts.Select(static a => a.Content).ToArray(),
            after.MinimalArtifacts.Select(static a => a.Content).ToArray());
        CollectionAssert.AreEqual(
            before.AuditArtifacts.Select(static a => a.Content).ToArray(),
            after.AuditArtifacts.Select(static a => a.Content).ToArray());
    }

    [TestMethod]
    public async Task DecimalSerializationRoundTripsInvariantlyUnderNonEnglishCulture()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            Scenario scenario = DecimalScenario();

            ExportResult result = await ExportAsync(scenario);

            Assert.IsTrue(result.Succeeded, FailureSummary(result));
            MinimalCsvArtifact artifact = Minimal(result, InterventionOneId);
            Assert.AreEqual("x_value,y_value,phase\n1.25,33.125,b\n", artifact.Content);
            string[] fields = artifact.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].Split(',');
            Assert.AreEqual(1.25d, double.Parse(fields[0], CultureInfo.InvariantCulture));
            Assert.AreEqual(33.125d, double.Parse(fields[1], CultureInfo.InvariantCulture));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [TestMethod]
    public async Task CsvTextFieldsNeutralizeSpreadsheetFormulaPrefixesWithoutChangingNumbersOrJson()
    {
        const string phaseCode = "=SUM(1,2,\"quoted\")";
        const string seriesSymbol = "+marker";
        const string seriesName = " \t-HYPERLINK(\"https://example.invalid\",\"label\")";
        ExportPoint point = Point(PointOne, InterventionOneId, PhaseB, 1, -1e-12, 1) with
        {
            SourceStage = "@manual",
            ModelVersion = "-candidate",
        };
        var scenario = new Scenario(
            [Phase(PhaseB, 1, phaseCode, ExportPhaseType.Intervention)],
            [Series(InterventionOneId, seriesSymbol, seriesName, ExportSeriesRole.Intervention, PointOne)],
            [point],
            [new ExportSeriesRelation(InterventionOneId, null)]);

        ExportResult result = await ExportAsync(
            scenario,
            mode: ExportMode.ObservationOrder,
            auditMode: ExportAuditMode.ExtendedCsvAndJson);

        Assert.IsTrue(result.Succeeded, FailureSummary(result));
        MinimalCsvArtifact minimal = Minimal(result, InterventionOneId);
        Assert.AreEqual(
            "x_value,y_value,phase\n1,-1E-12,\"'=SUM(1,2,\"\"quoted\"\")\"\n",
            minimal.Content,
            "Numeric scientific values must bypass text-field neutralization.");
        Assert.AreEqual(ExportContract.MinimalCsvHeader, minimal.Content.Split('\n')[0]);

        ExtendedAuditArtifact auditCsv = Audit(result, InterventionOneId, ExportAuditArtifactFormat.Csv);
        StringAssert.Contains(auditCsv.Content, "\"'=SUM(1,2,\"\"quoted\"\")\"");
        StringAssert.Contains(auditCsv.Content, ",'+marker,");
        StringAssert.Contains(
            auditCsv.Content,
            "\"' \t-HYPERLINK(\"\"https://example.invalid\"\",\"\"label\"\")\"");
        StringAssert.Contains(auditCsv.Content, ",'@manual,'-candidate\n");
        StringAssert.Contains(auditCsv.Content, "1,-1E-12,");

        ExtendedAuditArtifact auditJson = Audit(result, InterventionOneId, ExportAuditArtifactFormat.Json);
        using JsonDocument parsed = JsonDocument.Parse(auditJson.Content);
        JsonElement jsonRow = parsed.RootElement.GetProperty("rows")[0];
        Assert.AreEqual(phaseCode, jsonRow.GetProperty("phase").GetString());
        Assert.AreEqual(seriesSymbol, jsonRow.GetProperty("series_symbol").GetString());
        Assert.AreEqual(seriesName, jsonRow.GetProperty("series_name").GetString());
        Assert.AreEqual("@manual", jsonRow.GetProperty("source_stage").GetString());
        Assert.AreEqual("-candidate", jsonRow.GetProperty("model_version").GetString());
    }

    [TestMethod]
    [DataRow(ExportMode.PrintedSession, false)]
    [DataRow(ExportMode.PrintedSession, true)]
    [DataRow(ExportMode.ObservationOrder, false)]
    [DataRow(ExportMode.ObservationOrder, true)]
    public async Task EqualSessionRowsKeepScientificOrderWhenIdentifiersChange(ExportMode mode, bool separateSources)
    {
        Scenario CreateScenario(bool swapIdentifiers)
        {
            Guid target = swapIdentifiers ? MaintenanceSeriesId : InterventionOneId;
            Guid probe = swapIdentifiers ? InterventionOneId : MaintenanceSeriesId;
            ExportPoint lower = Point(swapIdentifiers ? PointTwo : PointOne, target, PhaseB, 1, 40, 1);
            ExportPoint upper = Point(swapIdentifiers ? PointOne : PointTwo,
                separateSources ? probe : target, PhaseB, 1, 70, 1);
            ExportSeries[] series = separateSources
                ? [Series(target, "circle", "Outcome", ExportSeriesRole.Intervention, lower.PointId),
                   Series(probe, "circle", "Probe", ExportSeriesRole.Maintenance, upper.PointId)]
                : [Series(target, "circle", "Outcome", ExportSeriesRole.Intervention, lower.PointId, upper.PointId)];
            return new Scenario(
                [Phase(PhaseB, 1, "b", ExportPhaseType.Intervention)],
                swapIdentifiers ? series.Reverse().ToArray() : series,
                swapIdentifiers ? [upper, lower] : [lower, upper],
                [new ExportSeriesRelation(target, null, separateSources ? [probe] : [])]);
        }

        Scenario original = CreateScenario(false);
        Scenario reassigned = CreateScenario(true);
        ExportResult first = await ExportAsync(original, mode: mode, auditMode: ExportAuditMode.ExtendedCsv);
        ExportResult second = await ExportAsync(reassigned, mode: mode, auditMode: ExportAuditMode.ExtendedCsv);

        Assert.IsTrue(first.Succeeded, FailureSummary(first));
        Assert.IsTrue(second.Succeeded, FailureSummary(second));
        Assert.AreEqual(first.MinimalArtifacts.Single().Content, second.MinimalArtifacts.Single().Content);
        Assert.AreEqual(first.MinimalArtifacts.Single().Sha256, second.MinimalArtifacts.Single().Sha256);
        CollectionAssert.AreEqual(ExpectedEqualSessionYOrder, first.MinimalArtifacts.Single().Rows.Select(static row => row.YValue).ToArray());
        foreach ((Scenario scenario, ExportResult result) in new[] { (original, first), (reassigned, second) })
        {
            ExtendedAuditRow[] rows = result.AuditArtifacts.Single().Rows.ToArray();
            CollectionAssert.AreEquivalent(scenario.Points.Select(static point => point.PointId).ToArray(),
                rows.Select(static row => row.PointId).ToArray());
            foreach (ExportPoint point in scenario.Points)
            {
                ExtendedAuditRow row = rows.Single(row => row.PointId == point.PointId);
                Assert.AreEqual(point.SeriesId, row.SourceSeriesId);
                Assert.AreEqual(point.OriginalPixel, row.OriginalPixel);
                Assert.AreEqual(point.GraphY, row.YValue);
            }
        }
    }

    [TestMethod]
    public async Task OutputBytesAndHashesAreDeterministicAcrossInputEnumerationOrder()
    {
        Scenario ordered = TwoInterventionScenario();
        Scenario reversed = new(
            ordered.Phases.Reverse().ToArray(),
            ordered.Series.Reverse().ToArray(),
            ordered.Points.Reverse().ToArray(),
            ordered.Relations.Reverse().ToArray());

        ExportResult first = await ExportAsync(ordered, auditMode: ExportAuditMode.ExtendedCsvAndJson);
        ExportResult second = await ExportAsync(reversed, auditMode: ExportAuditMode.ExtendedCsvAndJson);

        Assert.IsTrue(first.Succeeded, FailureSummary(first));
        Assert.IsTrue(second.Succeeded, FailureSummary(second));
        CollectionAssert.AreEqual(ArtifactFingerprints(first), ArtifactFingerprints(second));
        Assert.AreEqual(first.Determinism.ArtifactSetSha256, second.Determinism.ArtifactSetSha256);
        foreach ((string content, string sha256) in AllArtifacts(first))
        {
            Assert.AreEqual(Sha256(content), sha256);
        }
    }

    [TestMethod]
    public async Task MinimalCsvHasExactHeaderAndExactlyThreeColumns()
    {
        ExportResult result = await ExportAsync(OneInterventionScenario());

        Assert.IsTrue(result.Succeeded, FailureSummary(result));
        string content = Minimal(result, InterventionOneId).Content;
        Assert.IsFalse(content.StartsWith('\uFEFF'));
        string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(ExportContract.MinimalCsvHeader, lines[0]);
        Assert.IsTrue(lines.Skip(1).All(static line => line.Split(',').Length == 3));
    }

    [TestMethod]
    public async Task AuditCsvAndJsonTraceEveryMinimalRowToPointPixelsAndProvenance()
    {
        ExportResult result = await ExportAsync(
            ProbeScenario(),
            auditMode: ExportAuditMode.ExtendedCsvAndJson);

        Assert.IsTrue(result.Succeeded, FailureSummary(result));
        MinimalCsvArtifact minimal = Minimal(result, InterventionOneId);
        ExtendedAuditArtifact csv = Audit(result, InterventionOneId, ExportAuditArtifactFormat.Csv);
        ExtendedAuditArtifact json = Audit(result, InterventionOneId, ExportAuditArtifactFormat.Json);
        Assert.AreEqual(minimal.Rows.Count, csv.Rows.Count);
        Assert.AreEqual(minimal.Rows.Count, json.Rows.Count);

        for (int index = 0; index < minimal.Rows.Count; index++)
        {
            MinimalExportRow minimalRow = minimal.Rows[index];
            ExtendedAuditRow auditRow = csv.Rows[index];
            Assert.AreEqual(minimalRow.XValue, auditRow.XValue);
            Assert.AreEqual(minimalRow.YValue, auditRow.YValue);
            Assert.AreEqual(minimalRow.Phase, auditRow.Phase);
            Assert.AreNotEqual(Guid.Empty, auditRow.PointId);
            Assert.AreNotEqual(Guid.Empty, auditRow.PhaseId);
            Assert.IsTrue(double.IsFinite(auditRow.OriginalPixel.X));
            Assert.IsTrue(double.IsFinite(auditRow.OriginalPixel.Y));
            Assert.IsFalse(string.IsNullOrWhiteSpace(auditRow.SourceStage));
        }

        string csvHeader = csv.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        StringAssert.Contains(csvHeader, "point_id");
        StringAssert.Contains(csvHeader, "original_pixel_x");
        StringAssert.Contains(csvHeader, "original_pixel_y");
        StringAssert.Contains(csvHeader, "source_stage");
        StringAssert.Contains(csvHeader, "export_mode");
        StringAssert.Contains(csvHeader, "calibration_status");
        Assert.IsTrue(csv.Rows.All(static row => row.ExportMode == ExportMode.PrintedSession));

        using JsonDocument document = JsonDocument.Parse(json.Content);
        JsonElement root = document.RootElement;
        Assert.AreEqual(ExportContract.CoordinateSpace, root.GetProperty("coordinate_space").GetString());
        Assert.AreEqual(minimal.Rows.Count, root.GetProperty("row_count").GetInt32());
        foreach (JsonElement row in root.GetProperty("rows").EnumerateArray())
        {
            Assert.IsTrue(row.TryGetProperty("point_id", out _));
            Assert.IsTrue(row.TryGetProperty("original_pixel_x", out _));
            Assert.IsTrue(row.TryGetProperty("original_pixel_y", out _));
            Assert.IsTrue(row.TryGetProperty("source_stage", out _));
        }
    }

    [TestMethod]
    public async Task PreCanceledRequestPropagatesCancellationWithoutWriting()
    {
        string outputDirectory = NewTemporaryDirectoryPath();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            try
            {
                _ = await new ExportService().ExportAsync(
                    Request(OneInterventionScenario(), outputDirectory, operation: ExportOperation.WriteFiles),
                    cancellation.Token);
                Assert.Fail("A pre-canceled export should propagate cancellation.");
            }
            catch (OperationCanceledException)
            {
            }

            Assert.AreEqual(0, ExistingFileCount(outputDirectory));
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(outputDirectory);
        }
    }

    [TestMethod]
    public async Task InvalidAndDanglingInputsReturnStructuredFailuresWithoutFiles()
    {
        Scenario valid = OneInterventionScenario();
        Scenario invalidId = valid with
        {
            Points = valid.Points
                .Select(point => point.PointId == PointOne ? point with { PointId = Guid.Empty } : point)
                .ToArray(),
        };
        ExportResult invalidResult = await ExportAsync(invalidId);
        AssertHasFailure(invalidResult, "INVALID_ID");
        Assert.IsFalse(invalidResult.Succeeded);

        Guid missingSeries = Guid.Parse("50000000-0000-0000-0000-000000000099");
        Scenario dangling = valid with
        {
            Relations = [new ExportSeriesRelation(InterventionOneId, missingSeries)],
        };
        ExportResult danglingResult = await ExportAsync(dangling);
        AssertHasFailure(danglingResult, "INVALID_REFERENCE");
        Assert.IsFalse(danglingResult.Succeeded);
        Assert.IsTrue(danglingResult.MinimalArtifacts.All(static artifact => artifact.WrittenPath is null));
    }

    [TestMethod]
    public async Task MissingRelationAndUnlistedOwnedPointBlockIncompleteExports()
    {
        Scenario valid = OneInterventionScenario();
        ExportResult missingRelation = await ExportAsync(valid with { Relations = [] });
        AssertHasFailure(missingRelation, "INVALID_REFERENCE");
        Assert.AreEqual(0, missingRelation.MinimalArtifacts.Count);

        ExportSeries intervention = valid.Series.Single(
            static item => item.SeriesId == InterventionOneId);
        Scenario missingMembership = valid with
        {
            Series = valid.Series
                .Select(item => item.SeriesId == InterventionOneId
                    ? Series(
                        item.SeriesId,
                        item.Symbol,
                        item.DisplayName,
                        item.SemanticRole,
                        intervention.PointIds[0])
                    : item)
                .ToArray(),
        };
        ExportResult unlistedPoint = await ExportAsync(missingMembership);
        AssertHasFailure(unlistedPoint, "INVALID_REFERENCE");
        Assert.AreEqual(0, unlistedPoint.MinimalArtifacts.Count);
    }

    [TestMethod]
    public async Task ExistingDestinationBlocksWholeArtifactSetWithoutOverwrite()
    {
        string outputDirectory = NewTemporaryDirectoryPath();
        try
        {
            Scenario scenario = OneInterventionScenario();
            ExportResult preview = await ExportAsync(
                scenario,
                outputDirectory,
                auditMode: ExportAuditMode.ExtendedCsv);
            string blockedAuditName = preview.Preview.Files.Single().AuditFileNames.Single();
            Directory.CreateDirectory(Path.Combine(outputDirectory, blockedAuditName));

            ExportResult result = await ExportAsync(
                scenario,
                outputDirectory,
                auditMode: ExportAuditMode.ExtendedCsv,
                operation: ExportOperation.WriteFiles);

            AssertHasFailure(result, "EXPORT_FILE_EXISTS");
            Assert.IsTrue(result.MinimalArtifacts.All(static artifact => artifact.WrittenPath is null));
            Assert.IsTrue(result.AuditArtifacts.All(static artifact => artifact.WrittenPath is null));
            Assert.AreEqual(0, ExistingFileCount(outputDirectory));
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(outputDirectory);
        }
    }

    [TestMethod]
    public async Task WriteFilesLeavesOnlyReturnedArtifactsWhoseContentsMatch()
    {
        string outputDirectory = NewTemporaryDirectoryPath();
        try
        {
            ExportResult result = await ExportAsync(
                TwoInterventionScenario(),
                outputDirectory,
                auditMode: ExportAuditMode.ExtendedCsvAndJson,
                operation: ExportOperation.WriteFiles);

            Assert.IsTrue(result.Succeeded, FailureSummary(result));
            var returnedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (MinimalCsvArtifact artifact in result.MinimalArtifacts)
            {
                Assert.IsNotNull(artifact.WrittenPath);
                returnedFiles.Add(Path.GetFullPath(artifact.WrittenPath), artifact.Content);
            }

            foreach (ExtendedAuditArtifact artifact in result.AuditArtifacts)
            {
                Assert.IsNotNull(artifact.WrittenPath);
                returnedFiles.Add(Path.GetFullPath(artifact.WrittenPath), artifact.Content);
            }

            string[] actualFiles = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            CollectionAssert.AreEqual(
                returnedFiles.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                actualFiles);
            foreach ((string path, string content) in returnedFiles)
            {
                Assert.AreEqual(content, await File.ReadAllTextAsync(path, Encoding.UTF8));
            }

            Assert.IsFalse(
                actualFiles.Any(static path =>
                    path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".pending", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(outputDirectory);
        }
    }

    private static async Task<ExportResult> ExportAsync(
        Scenario scenario,
        string? outputDirectory = null,
        ExportMode mode = ExportMode.PrintedSession,
        ExportAuditMode auditMode = ExportAuditMode.None,
        ExportOperation operation = ExportOperation.Preview,
        ExportCalibration? calibration = null,
        ExportSessionOriginPolicy? policy = null)
    {
        var service = new ExportService();
        return await service.ExportAsync(
            Request(scenario, outputDirectory, mode, auditMode, operation, calibration, policy),
            CancellationToken.None);
    }

    private static ExportRequest Request(
        Scenario scenario,
        string? outputDirectory = null,
        ExportMode mode = ExportMode.PrintedSession,
        ExportAuditMode auditMode = ExportAuditMode.None,
        ExportOperation operation = ExportOperation.Preview,
        ExportCalibration? calibration = null,
        ExportSessionOriginPolicy? policy = null,
        string? fileNamePrefix = null) =>
        new(
            RunId,
            ProjectId,
            PanelId,
            outputDirectory ?? Path.GetTempPath(),
            "Case 01",
            mode,
            auditMode,
            operation,
            calibration ?? ValidCalibration(),
            policy ?? ExportSessionOriginPolicy.Default,
            scenario.Phases,
            scenario.Series,
            scenario.Points,
            scenario.Relations)
        {
            FileNamePrefix = fileNamePrefix,
        };

    private static ExportCalibration ValidCalibration() => new(
        ExportCalibrationStatus.Valid,
        hasYCalibration: true,
        hasPrintedSessionCalibration: true,
        hasAbsoluteSessionOrigin: true,
        firstObservedSession: 1,
        confidence: 0.99);

    private static Scenario OneInterventionScenario()
    {
        ExportPoint[] points =
        [
            Point(PointFour, InterventionOneId, PhaseB, 4, 22, 4),
            Point(PointTwo, BaselineSeriesId, PhaseA, 2, 12, 2),
            Point(PointThree, InterventionOneId, PhaseB, 3, 21, 3),
            Point(PointOne, BaselineSeriesId, PhaseA, 1, 11, 1),
        ];
        ExportSeries[] series =
        [
            Series(InterventionOneId, "●", "● Intervention One", ExportSeriesRole.Intervention, PointThree, PointFour),
            Series(BaselineSeriesId, "■", "Shared Baseline", ExportSeriesRole.Baseline, PointOne, PointTwo),
        ];
        return new(
            [Phase(PhaseB, 2, "b", ExportPhaseType.Intervention), Phase(PhaseA, 1, "a", ExportPhaseType.Baseline)],
            series,
            points,
            [new ExportSeriesRelation(InterventionOneId, BaselineSeriesId)]);
    }

    private static Scenario TwoInterventionScenario()
    {
        Scenario one = OneInterventionScenario();
        ExportPoint[] points =
        [
            .. one.Points,
            Point(PointSix, InterventionTwoId, PhaseB, 4, 42, 6),
            Point(PointFive, InterventionTwoId, PhaseB, 3, 41, 5),
        ];
        ExportSeries[] series =
        [
            .. one.Series,
            Series(InterventionTwoId, "○", "○ Intervention Two", ExportSeriesRole.Intervention, PointFive, PointSix),
        ];
        return one with
        {
            Series = series,
            Points = points,
            Relations =
            [
                new ExportSeriesRelation(InterventionTwoId, BaselineSeriesId),
                new ExportSeriesRelation(InterventionOneId, BaselineSeriesId),
            ],
        };
    }

    private static Scenario ProbeScenario()
    {
        ExportPoint[] points =
        [
            Point(PointEight, GeneralizationSeriesId, PhaseGeneralization, 4, 31, 4),
            Point(PointThree, InterventionOneId, PhaseB, 2, 21, 2),
            Point(PointSeven, MaintenanceSeriesId, PhaseMaintenance, 3, 30, 3),
            Point(PointOne, BaselineSeriesId, PhaseA, 1, 11, 1),
        ];
        return new(
            [
                Phase(PhaseGeneralization, 4, "g1", ExportPhaseType.Generalization),
                Phase(PhaseA, 1, "a", ExportPhaseType.Baseline),
                Phase(PhaseMaintenance, 3, "m1", ExportPhaseType.Maintenance),
                Phase(PhaseB, 2, "b", ExportPhaseType.Intervention),
            ],
            [
                Series(GeneralizationSeriesId, "◇", "Generalization", ExportSeriesRole.Generalization, PointEight),
                Series(InterventionOneId, "●", "Intervention One", ExportSeriesRole.Intervention, PointThree),
                Series(BaselineSeriesId, "■", "Shared Baseline", ExportSeriesRole.Baseline, PointOne),
                Series(MaintenanceSeriesId, "△", "Maintenance", ExportSeriesRole.Maintenance, PointSeven),
            ],
            points,
            [new ExportSeriesRelation(
                InterventionOneId,
                BaselineSeriesId,
                [MaintenanceSeriesId, GeneralizationSeriesId])]);
    }

    private static Scenario AbabUnknownScenario()
    {
        ExportPoint[] points =
        [
            Point(PointFive, InterventionOneId, PhaseUnknown, 5, 25, 5),
            Point(PointFour, InterventionOneId, PhaseB2, 4, 24, 4),
            Point(PointTwo, BaselineSeriesId, PhaseA2, 3, 13, 3),
            Point(PointThree, InterventionOneId, PhaseB, 2, 23, 2),
            Point(PointOne, BaselineSeriesId, PhaseA, 1, 12, 1),
        ];
        return new(
            [
                Phase(PhaseUnknown, 5, "phase5", ExportPhaseType.Unknown),
                Phase(PhaseB2, 4, "b2", ExportPhaseType.Intervention),
                Phase(PhaseA2, 3, "a2", ExportPhaseType.Baseline),
                Phase(PhaseB, 2, "b1", ExportPhaseType.Intervention),
                Phase(PhaseA, 1, "a1", ExportPhaseType.Baseline),
            ],
            [
                Series(InterventionOneId, "●", "Intervention One", ExportSeriesRole.Intervention, PointThree, PointFour, PointFive),
                Series(BaselineSeriesId, "■", "Baseline", ExportSeriesRole.Baseline, PointOne, PointTwo),
            ],
            points,
            [new ExportSeriesRelation(InterventionOneId, BaselineSeriesId)]);
    }

    private static Scenario DuplicateNameScenario()
    {
        ExportPoint[] points =
        [
            Point(PointThree, InterventionOneId, PhaseB, 1, 21, 1),
            Point(PointFive, InterventionTwoId, PhaseB, 1, 41, 1),
        ];
        return new(
            [Phase(PhaseB, 1, "b", ExportPhaseType.Intervention)],
            [
                Series(InterventionTwoId, "○", "○ Treatment:A", ExportSeriesRole.Intervention, PointFive),
                Series(InterventionOneId, "●", "● Treatment/A", ExportSeriesRole.Intervention, PointThree),
            ],
            points,
            [
                new ExportSeriesRelation(InterventionTwoId, null),
                new ExportSeriesRelation(InterventionOneId, null),
            ]);
    }

    private static Scenario DecimalScenario()
    {
        ExportPoint point = Point(PointOne, InterventionOneId, PhaseB, 1.25, 33.125, 1);
        return new(
            [Phase(PhaseB, 1, "b", ExportPhaseType.Intervention)],
            [Series(InterventionOneId, "●", "Intervention", ExportSeriesRole.Intervention, PointOne)],
            [point],
            [new ExportSeriesRelation(InterventionOneId, null)]);
    }

    private static ExportPhase Phase(Guid id, int order, string code, ExportPhaseType type) =>
        new(id, order, code, type, code, order * 100, (order * 100) + 99, 0.95);

    private static ExportSeries Series(
        Guid id,
        string symbol,
        string name,
        ExportSeriesRole role,
        params Guid[] pointIds) =>
        new(id, symbol, name, role, pointIds, 0.93, name);

    private static ExportPoint Point(
        Guid id,
        Guid seriesId,
        Guid phaseId,
        double x,
        double y,
        int observationIndex) =>
        new(
            id,
            id,
            seriesId,
            phaseId,
            new ExportPixelPoint(100 + (x * 10), 500 - y),
            x,
            y,
            observationIndex,
            x,
            x + 0.125,
            ExportXValueSource.Printed,
            0.91,
            0.92,
            0.90,
            ExportReviewStatus.Accepted,
            "markers",
            "marker-center-1.2.3");

    private static MinimalCsvArtifact Minimal(ExportResult result, Guid interventionSeriesId) =>
        result.MinimalArtifacts.Single(artifact => artifact.InterventionSeriesId == interventionSeriesId);

    private static ExtendedAuditArtifact Audit(
        ExportResult result,
        Guid interventionSeriesId,
        ExportAuditArtifactFormat format = ExportAuditArtifactFormat.Csv) =>
        result.AuditArtifacts.Single(
            artifact => artifact.InterventionSeriesId == interventionSeriesId && artifact.Format == format);

    private static IReadOnlyList<ExtendedAuditRow> AuditRows(
        ExportResult result,
        Guid interventionSeriesId) =>
        result.AuditArtifacts.Count == 0
            ? throw new AssertFailedException("The result did not include audit rows.")
            : Audit(result, interventionSeriesId).Rows;

    private static void AssertSeriesMembership(
        ExportResult result,
        Guid targetInterventionId,
        Guid[] expectedBaselinePointIds,
        Guid[] expectedInterventionPointIds)
    {
        ExtendedAuditArtifact audit = Audit(result, targetInterventionId);
        CollectionAssert.AreEquivalent(
            expectedBaselinePointIds,
            audit.Rows
                .Where(static row => row.Inclusion == ExportRowInclusion.SharedBaseline)
                .Select(static row => row.PointId)
                .ToArray());
        CollectionAssert.AreEquivalent(
            expectedInterventionPointIds,
            audit.Rows
                .Where(static row => row.Inclusion == ExportRowInclusion.Intervention)
                .Select(static row => row.PointId)
                .ToArray());
        Assert.AreEqual(expectedBaselinePointIds.Length + expectedInterventionPointIds.Length, audit.Rows.Count);
    }

    private static void AssertHasFailure(ExportResult result, string code) =>
        Assert.IsTrue(
            result.Failures.Any(failure =>
                failure.Severity == ExportFailureSeverity.Error &&
                string.Equals(failure.Code, code, StringComparison.Ordinal)),
            $"Expected error '{code}'. Actual: {FailureSummary(result)}");

    private static string FailureSummary(ExportResult result) =>
        string.Join(" | ", result.Failures.Select(static failure => $"{failure.Code}: {failure.TechnicalMessage}"));

    private static string RowKey(MinimalExportRow row) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{row.XValue:R}|{row.YValue:R}|{row.Phase}");

    private static string[] ArtifactFingerprints(ExportResult result) =>
        result.MinimalArtifacts
            .Select(static artifact => $"M|{artifact.FileName}|{artifact.Sha256}|{artifact.Content}")
            .Concat(result.AuditArtifacts.Select(static artifact =>
                $"A|{artifact.Format}|{artifact.FileName}|{artifact.Sha256}|{artifact.Content}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<(string Content, string Sha256)> AllArtifacts(ExportResult result) =>
        result.MinimalArtifacts
            .Select(static artifact => (artifact.Content, artifact.Sha256))
            .Concat(result.AuditArtifacts.Select(static artifact => (artifact.Content, artifact.Sha256)));

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string NewTemporaryDirectoryPath() =>
        Path.Combine(Path.GetTempPath(), $"GraphReader.Export.Tests-{Guid.NewGuid():N}");

    private static int ExistingFileCount(string path) =>
        Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories).Length : 0;

    private static void DeleteOwnedTemporaryDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string expectedPrefix = Path.GetFullPath(Path.GetTempPath());
        if (Directory.Exists(fullPath) &&
            fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(fullPath).StartsWith("GraphReader.Export.Tests-", StringComparison.Ordinal))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private sealed record Scenario(
        IReadOnlyList<ExportPhase> Phases,
        IReadOnlyList<ExportSeries> Series,
        IReadOnlyList<ExportPoint> Points,
        IReadOnlyList<ExportSeriesRelation> Relations);
}
