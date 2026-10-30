// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;

namespace GraphReader.RealAcceptance.Ocr;

internal static class EngaugeDevLabelAuditSelfTest
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static object Run()
    {
        // Paths are synthetic and nonexistent. The reader below is the only
        // source of labels and does not access the filesystem.
        string root = Path.Combine(Environment.CurrentDirectory, "synthetic-dev-label-audit");
        var dev = Enumerable.Range(0, 4).Select(index => new FrozenRealCorpusProject(
            Path.GetFullPath(Path.Combine(root, $"study-{index}", "case.dig")),
            new string((char)('a' + index), 64), new string((char)('e' + index), 64),
            FrozenRealCorpusInventory.RealDev)).ToArray();
        var sealedProject = new FrozenRealCorpusProject(
            Path.GetFullPath(Path.Combine(root, "sealed-study", "sealed-case.dig")),
            new string('9', 64), new string('8', 64), FrozenRealCorpusInventory.RealSealed);
        FrozenRealCorpusSelection selection = Selection(dev);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.CreateForTest(_ => selection, CancellationToken.None);
        int reads = 0;
        EngaugeDevLabelCounts Reader(string path, string caseId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Require(!string.Equals(path, sealedProject.GetProjectPath(), StringComparison.Ordinal), "sealed_path_not_read");
            reads++;
            if (caseId == dev[0].AnonymizedCaseId) { return new EngaugeDevLabelCounts(3, 2, 11); }
            if (caseId == dev[1].AnonymizedCaseId) { throw new EngaugeDigTruthException("DIG_XML_INVALID:secret detail"); }
            if (caseId == dev[2].AnonymizedCaseId) { throw new IOException("private path detail"); }
            throw new EngaugeDigTruthException("UNTRUSTED_PRIVATE_NAME");
        }

        EngaugeDevLabelAuditResult result = EngaugeDevLabelAudit.RunForTest(selector, Reader, CancellationToken.None);
        Require(reads == 4 && result.SelectedProjects == 4 && result.ParsedProjects == 1 && result.FailedProjects == 3 &&
            result.Anchors == 3 && result.Curves == 2 && result.Points == 11 &&
            result.FailureCodes.Count == 3 && result.FailureCodes["DIG_XML_INVALID"] == 1 &&
            result.FailureCodes["DIG_UNEXPECTED_READ_FAILURE"] == 1 &&
            result.FailureCodes["DIG_UNCLASSIFIED_FORMAT_ERROR"] == 1 &&
            !result.LabelsQualityApproved && !result.SealedSourceOverlapVerified && !result.CandidateAuthorized &&
            !result.ModelInference && result.OptimizerSteps == 0,
            "aggregate_counts_and_flags");
        string json = JsonSerializer.Serialize(result, JsonOptions);
        Require(!json.Contains(root, StringComparison.OrdinalIgnoreCase) &&
            !json.Contains(dev[0].AnonymizedCaseId, StringComparison.Ordinal) &&
            !json.Contains(dev[0].AnonymizedStudyId, StringComparison.Ordinal) &&
            !json.Contains("secret", StringComparison.OrdinalIgnoreCase) &&
            !json.Contains("private", StringComparison.OrdinalIgnoreCase) &&
            !json.Contains("case.dig", StringComparison.OrdinalIgnoreCase), "aggregate_contains_no_case_data");
        ExpectFailure(() => selector.RequireProject(sealedProject.GetProjectPath(), sealedProject.AnonymizedCaseId,
            sealedProject.AnonymizedStudyId, CancellationToken.None), "FROZEN_DEV_TRAINING_SOURCE_NOT_SELECTED");
        ExpectFailure(() => FrozenDevTrainingSelector.CreateForTest(_ => Selection([sealedProject]), CancellationToken.None),
            "FROZEN_DEV_TRAINING_INVALID_SELECTION");

        int metadataLoads = 0;
        int driftReads = 0;
        FrozenDevTrainingSelector drift = FrozenDevTrainingSelector.CreateForTest(_ =>
        {
            metadataLoads++;
            return metadataLoads < 3 ? selection : Selection(dev, assignment: new string('2', 64));
        }, CancellationToken.None);
        ExpectFailure(() => EngaugeDevLabelAudit.RunForTest(drift, (_, _, _) =>
        {
            driftReads++;
            return new EngaugeDevLabelCounts(3, 1, 1);
        }, CancellationToken.None), "FROZEN_DEV_TRAINING_INVENTORY_CHANGED");
        Require(driftReads == 0, "drift_rejected_before_payload_read");

        int finalMetadataLoads = 0;
        int finalDriftReads = 0;
        FrozenDevTrainingSelector finalDrift = FrozenDevTrainingSelector.CreateForTest(_ =>
        {
            finalMetadataLoads++;
            // One creation load, one enumeration, four per-project checks, then
            // the final recheck after the last selected project was read.
            return finalMetadataLoads <= 6 ? selection : Selection(dev, assignment: new string('2', 64));
        }, CancellationToken.None);
        ExpectFailure(() => EngaugeDevLabelAudit.RunForTest(finalDrift, (_, _, _) =>
        {
            finalDriftReads++;
            return new EngaugeDevLabelCounts(3, 1, 1);
        }, CancellationToken.None), "FROZEN_DEV_TRAINING_INVENTORY_CHANGED");
        Require(finalDriftReads == 4 && finalMetadataLoads == 7, "final_drift_rejects_aggregate");

        using var midRunCancellation = new CancellationTokenSource();
        int cancelledReads = 0;
        try
        {
            EngaugeDevLabelAudit.RunForTest(selector, (_, _, _) =>
            {
                cancelledReads++;
                midRunCancellation.Cancel();
                return new EngaugeDevLabelCounts(3, 1, 1);
            }, midRunCancellation.Token);
            throw new InvalidOperationException("ENGAUGE_DEV_LABEL_AUDIT_MIDRUN_CANCELLATION_NOT_OBSERVED");
        }
        catch (OperationCanceledException) { }
        Require(cancelledReads == 1, "cancellation_stops_further_reads_and_rejects_aggregate");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            EngaugeDevLabelAudit.RunForTest(selector, Reader, cancellation.Token);
            throw new InvalidOperationException("ENGAUGE_DEV_LABEL_AUDIT_CANCELLATION_NOT_OBSERVED");
        }
        catch (OperationCanceledException) { }
        return new
        {
            status = "pass", scope = "synthetic-reader-only", private_payload_reads = 0,
            sealed_payload_reads = 0, model_runs = 0, optimizer_steps = 0,
        };
    }

    private static FrozenRealCorpusSelection Selection(
        FrozenRealCorpusProject[] projects, string? assignment = null) =>
        new(FrozenRealCorpusInventory.RealDev, projects.Length + 1, projects.Length, 1,
            assignment ?? new string('1', 64), new string('3', 64), projects);

    private static void ExpectFailure(Action action, string code)
    {
        try { action(); }
        catch (InvalidDataException exception) when (exception.Message == code) { return; }
        throw new InvalidOperationException($"{code}_NOT_REJECTED");
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) { throw new InvalidOperationException($"ENGAUGE_DEV_LABEL_AUDIT_SELF_TEST_FAILED:{check}"); }
    }
}
