// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using GraphReader.Export;

namespace GraphReader.RealAcceptance.Ocr;

internal static class FrozenRealWorkflowRunnerSelfTest
{
    internal static async Task<object> RunAsync()
    {
        var checks = new List<string>();
        FrozenRealCorpusSelection devSelection = Selection(FrozenRealCorpusInventory.RealDev, "2");
        FrozenRealWorkflowAdmissionResult devAdmission = Admission(
            FrozenRealCorpusInventory.RealDev, devSelection, sealedRead: false);
        var devEvents = new List<string>();
        FrozenRealWorkflowRunnerResult dev = await FrozenRealWorkflowRunner.RunForTestAsync(
            devAdmission,
            "fixture-corpus",
            (_, _) => throw new InvalidOperationException("fake executor is replaced by dependency"),
            sealedFirstReadHandshake: null,
            Dependencies(devSelection, devEvents),
            CancellationToken.None).ConfigureAwait(false);
        Require(dev.Aggregate.ProjectFiles == 2 && dev.CorpusContentSha256.Length == 64 &&
                devEvents.SequenceEqual(
            ["inventory", "read-case-000", "read-case-001", "inventory", "group", "execute"]),
            "metadata_is_checked_before_payload_and_after_complete_parse");
        checks.Add("metadata_is_checked_before_payload_and_after_complete_parse");

        FrozenRealCorpusSelection sealedSelection = Selection(FrozenRealCorpusInventory.RealSealed, "3");
        FrozenRealWorkflowAdmissionResult sealedAdmission = Admission(
            FrozenRealCorpusInventory.RealSealed, sealedSelection, sealedRead: true);
        string nonce = new('e', 64);
        using var reader = new StringReader(
            $"G22_READ_ACK/1 {nonce}\nG22_CORPUS_ACK/1 {nonce}\n");
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var handshake = new FrozenRealFirstReadHandshake(
            reader, writer, "attempt-1", sealedAdmission.Candidate.CandidateSha256,
            TimeSpan.FromSeconds(2), nonce);
        var sealedEvents = new List<string>();
        FrozenRealWorkflowRunnerResult sealedResult = await FrozenRealWorkflowRunner.RunForTestAsync(
            sealedAdmission,
            "fixture-corpus",
            (_, _) => throw new InvalidOperationException("fake executor is replaced by dependency"),
            handshake,
            Dependencies(sealedSelection, sealedEvents, invokeFirstRead: true),
            CancellationToken.None).ConfigureAwait(false);
        Require(writer.ToString() == string.Concat(
                $"G22_FIRST_READ/1 attempt-1 {sealedAdmission.Candidate.CandidateSha256} {nonce}{Environment.NewLine}",
                $"G22_CORPUS/1 attempt-1 {sealedAdmission.Candidate.CandidateSha256} ",
                sealedResult.CorpusContentSha256, $" {nonce}{Environment.NewLine}"),
            "sealed_projects_share_one_first_read_and_content_handshake");
        checks.Add("sealed_projects_share_one_first_read_and_content_handshake");

        var failureEvents = new List<string>();
        FrozenRealWorkflowRunnerDependencies parseFailure = Dependencies(
            devSelection, failureEvents, failSecondRead: true);
        await ExpectFailureAsync<InvalidDataException>(() => FrozenRealWorkflowRunner.RunForTestAsync(
            devAdmission,
            "fixture-corpus",
            (_, _) => throw new InvalidOperationException(),
            null,
            parseFailure,
            CancellationToken.None)).ConfigureAwait(false);
        Require(!failureEvents.Contains("group", StringComparer.Ordinal) &&
                !failureEvents.Contains("execute", StringComparer.Ordinal),
            "one_project_parse_failure_prevents_partial_truth_scoring");
        checks.Add("one_project_parse_failure_prevents_partial_truth_scoring");

        int inventoryCall = 0;
        FrozenRealCorpusSelection drifted = Selection(FrozenRealCorpusInventory.RealDev, "4");
        var driftEvents = new List<string>();
        FrozenRealWorkflowRunnerDependencies driftDependencies = Dependencies(devSelection, driftEvents) with
        {
            LoadInventory = (_, _, _) => ++inventoryCall == 1 ? devSelection : drifted,
        };
        await ExpectFailureAsync<InvalidDataException>(() => FrozenRealWorkflowRunner.RunForTestAsync(
            devAdmission,
            "fixture-corpus",
            (_, _) => throw new InvalidOperationException(),
            null,
            driftDependencies,
            CancellationToken.None)).ConfigureAwait(false);
        Require(!driftEvents.Contains("execute", StringComparer.Ordinal),
            "metadata_drift_before_inference_rejected");
        checks.Add("metadata_drift_before_inference_rejected");

        await ExpectFailureAsync<InvalidDataException>(() => FrozenRealWorkflowRunner.RunForTestAsync(
            sealedAdmission,
            "fixture-corpus",
            (_, _) => throw new InvalidOperationException(),
            null,
            Dependencies(sealedSelection, []),
            CancellationToken.None)).ConfigureAwait(false);
        checks.Add("sealed_run_without_first_read_handshake_rejected");

        return new
        {
            status = "pass",
            checks,
            private_corpus_access = false,
            sealed_corpus_access = false,
            model_inference = false,
        };
    }

    private static FrozenRealWorkflowRunnerDependencies Dependencies(
        FrozenRealCorpusSelection selection,
        List<string> events,
        bool invokeFirstRead = false,
        bool failSecondRead = false)
    {
        int readCount = 0;
        return new FrozenRealWorkflowRunnerDependencies(
            (_, _, _) =>
            {
                events.Add("inventory");
                return selection;
            },
            (_, caseKey, beforeRead, _) =>
            {
                events.Add("read-" + caseKey);
                if (invokeFirstRead)
                {
                    beforeRead?.Invoke();
                }
                if (failSecondRead && ++readCount == 2)
                {
                    throw new InvalidDataException("fixture parse failure");
                }
                return Truth(caseKey);
            },
            (projects, _) =>
            {
                events.Add("group");
                return projects.Select((project, index) => new EngaugeWorkflowImageGroup(
                    [checked((byte)index)], project.TruthCase,
                    [new EngaugeWorkflowProjectIdentity(
                        project.TruthCase.CaseKey, project.ProjectSha256, 1, 1, project.Anchors)],
                    0)).ToArray();
            },
            (groups, options, _, _) =>
            {
                events.Add("execute");
                Require(options.RequireInMemoryArtifacts, "runner_forces_in_memory_evaluation");
                return Task.FromResult(Report(groups.Count, groups.Count));
            });
    }

    private static EngaugeDigWholeWorkflowTruth Truth(string caseKey)
    {
        string sourceSha = new(caseKey.EndsWith("000", StringComparison.Ordinal) ? 'a' : 'b', 64);
        var truth = new WholeWorkflowTruthCase(
            caseKey,
            sourceSha,
            1,
            1,
            [new WholeWorkflowTruthSeries("series")],
            [new WholeWorkflowTruthPoint("point", "series", 0, 0, 1, 2, 1, ExportMode.PrintedSession, null)],
            null);
        return new EngaugeDigWholeWorkflowTruth(
            new string(caseKey.EndsWith("000", StringComparison.Ordinal) ? 'c' : 'd', 64),
            sourceSha,
            1,
            1,
            [0],
            [
                new EngaugeDigAxisAnchor(0, 0, 0, 0),
                new EngaugeDigAxisAnchor(0, 1, 0, 1),
                new EngaugeDigAxisAnchor(1, 0, 1, 0),
            ],
            [new EngaugeDigCurve("series", [new EngaugeDigCurvePoint("point", 0, 0, 1, 2)])],
            truth);
    }

    private static FrozenRealWorkflowAdmissionResult Admission(
        string split,
        FrozenRealCorpusSelection selection,
        bool sealedRead)
    {
        var candidate = new FrozenRealWorkflowCandidateIdentity(
            "revision", "candidate", new string('a', 64), new string('4', 64),
            new string('5', 64), "protocol.json", new string('b', 64),
            new string('c', 64), new string('d', 64), new string('e', 64),
            new string('f', 64));
        return new FrozenRealWorkflowAdmissionResult(
            "protocol.json",
            candidate.ProtocolSha256,
            new string('1', 64),
            new string('2', 64),
            candidate,
            candidate.ExecutionDescriptorSha256,
            candidate.OperatingPointIdentity,
            split,
            selection.SelectedProjects.Count,
            selection.AssignmentSha256,
            selection.SelectedInventorySha256,
            new WholeWorkflowEvaluationOptions(5, 0.5, 5, true),
            0.95,
            0.95,
            sealedRead ? [new string('3', 64)] : [],
            AggregateOnly: true,
            SealedFirstReadRequired: sealedRead);
    }

    private static FrozenRealCorpusSelection Selection(string split, string inventoryDigit)
    {
        FrozenRealCorpusProject[] projects = Enumerable.Range(0, 2)
            .Select(index => new FrozenRealCorpusProject(
                $"fixture-{index}.dig", $"case-{index:D3}", $"study-{index:D3}", split))
            .ToArray();
        return new FrozenRealCorpusSelection(
            split, 171, 120, 51, new string('1', 64), new string(inventoryDigit[0], 64), projects);
    }

    private static EngaugeGroupedWorkflowAggregateReport Report(int imageGroups, int projects)
    {
        WholeWorkflowEvaluationResult evaluation = new(
            TruthCases: imageGroups,
            OutputCases: imageGroups,
            CompletedCases: imageGroups,
            FailedCases: 0,
            UnexpectedCases: 0,
            IntegrityFailureCases: 0,
            TruthSeries: imageGroups,
            PredictedSeries: imageGroups,
            MatchedSeries: imageGroups,
            TruthPoints: imageGroups,
            PredictedPoints: imageGroups,
            MatchedPoints: imageGroups,
            UniquePointValueMetricsAvailable: true,
            RelationalRowMetricsAvailable: false,
            RelationalPhaseMetricsAvailable: false,
            ActualRows: imageGroups,
            ResidualArtifactRowsFromFailedCases: 0,
            ExpectedRows: imageGroups,
            StructurallyMatchedRows: imageGroups,
            CorrectRows: imageGroups,
            MissingRows: 0,
            ExtraRows: 0,
            DuplicateRows: 0,
            WrongScaleRows: 0,
            WrongExportModeRows: 0,
            WrongPhaseRows: null,
            WrongRelationRows: null,
            UniquePointValueCorrect: imageGroups,
            UniquePointValueIncorrect: 0,
            UniquePointMissing: 0,
            UniquePointExtra: 0,
            UniquePointWrongScale: 0,
            UniquePointWrongExportMode: 0,
            UniquePointStructuralPrecision: 1,
            UniquePointStructuralCoverage: 1,
            UniquePointValuePrecision: 1,
            UniquePointValueCoverage: 1,
            MatchedUniquePointValueAccuracy: 1,
            RelationalRowPrecision: null,
            RelationalRowCoverage: null,
            MatchedRelationalGraphValueAccuracy: null,
            MatchedRelationalPhaseAccuracy: null,
            ArtifactIntegrityValid: true,
            FailureKinds: new Dictionary<string, int>());
        return new EngaugeGroupedWorkflowAggregateReport(
            EngaugeGroupedWorkflowExecutor.ReportSchema,
            imageGroups,
            imageGroups,
            projects,
            imageGroups,
            imageGroups,
            0,
            projects,
            0,
            imageGroups,
            imageGroups,
            0,
            new Dictionary<string, int>(),
            evaluation,
            AggregateOnly: true,
            CaseLevelOutput: false,
            TruthRowsOutput: false,
            PredictionOutput: false,
            PathsOutput: false,
            NamesOutput: false);
    }

    private static void Require(bool condition, string code)
    {
        if (!condition)
        {
            throw new InvalidOperationException(code);
        }
    }

    private static async Task ExpectFailureAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException("EXPECTED_REAL_RUNNER_FAILURE_MISSING");
    }
}
