// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;
using GraphReader.Export;

namespace GraphReader.RealAcceptance.Ocr;

internal static class FrozenWorkflowCsvScoringSelfTest
{
    internal static object Run()
    {
        const string sourceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        WholeWorkflowTruthCase[] truths = Enumerable.Range(0, 7).Select(index => new WholeWorkflowTruthCase(
            $"source-{index}", sourceHash, 100, 100, [new("series")],
            [new("point", "series", 10, 20, 1, 2, 1, ExportMode.PrintedSession, null)], null)).ToArray();
        WholeWorkflowCaseOutput[] outputs = truths.Select(truth => new WholeWorkflowCaseOutput(
            truth.CaseKey, sourceHash, false, "TestFailure", [])).ToArray();
        var input = new FrozenWorkflowCsvEvaluationInput(
            FrozenWorkflowCsvScoring.InputSchema, "project-owned-synthetic-only", false, false, false,
            new(FrozenWorkflowCsvScoring.ReportPath, FrozenWorkflowCsvScoring.ReportSha256), truths, outputs,
            new(5, .5, 5), JsonSerializer.SerializeToElement(new { fixture = "project-owned" }));
        JsonElement report = JsonSerializer.SerializeToElement(new
        {
            schema = FrozenCandidateSyntheticRunner.ReportSchema,
            scope = "local-synthetic-frozen-candidate-diagnostic",
            production_approved = false,
            private_corpus_access = false,
            sealed_corpus_access = false,
            truth_consumed_by_inference = false,
            source_count = 7,
            completed_count = 0,
            failed_count = 7,
            cases = truths.Select(truth => new
            {
                source_id = truth.CaseKey, image_sha256 = sourceHash, width = 100, height = 100,
                status = "failed", failure_type = "TestFailure", artifacts = Array.Empty<object>(),
            }).ToArray(),
        });
        string repositoryRoot = Environment.CurrentDirectory;
        IReadOnlyList<WholeWorkflowCaseOutput> bound = FrozenWorkflowCsvScoring.ValidateInput(input, report, repositoryRoot);
        WholeWorkflowEvaluationResult metrics = WholeWorkflowCsvEvaluator.Evaluate(truths, bound, input.Options, CancellationToken.None);
        if (metrics.TruthCases != 7 || metrics.FailedCases != 7 || metrics.TruthPoints != 7 ||
            metrics.MatchedPoints != 0 || metrics.UniquePointValueCoverage != 0 ||
            metrics.UniquePointValuePrecision is not null)
            throw new InvalidDataException("Failed saved sources did not retain their full truth denominator.");
        int rejected = 0;
        void Reject(FrozenWorkflowCsvEvaluationInput changed)
        {
            try { FrozenWorkflowCsvScoring.ValidateInput(changed, report, repositoryRoot); }
            catch (InvalidDataException) { rejected++; return; }
            throw new InvalidDataException("A changed workflow input was accepted.");
        }
        Reject(input with { TruthCases = truths.Take(6).ToArray() });
        Reject(input with { Outputs = outputs.Reverse().ToArray() });
        Reject(input with { TruthCases = [truths[0] with { SourceWidth = 99 }, .. truths.Skip(1)] });
        Reject(input with { Outputs = [outputs[0] with { WorkflowSucceeded = true }, .. outputs.Skip(1)] });
        Reject(input with { Outputs = [outputs[0] with { FailureCode = "DifferentFailure" }, .. outputs.Skip(1)] });
        Reject(input with { Outputs = [outputs[0] with { Artifacts = [new("fake.csv", sourceHash, 1, "fake.csv")] }, .. outputs.Skip(1)] });
        Reject(input with { PrivateCorpusAccess = true });
        Reject(input with { SealedCorpusAccess = true });
        Reject(input with { TruthConsumedByInference = true });
        return new
        {
            status = "pass", model_free = true, scenarios_passed = 1 + rejected,
            failed_cases_preserve_full_truth = true, private_corpus_access = false,
            sealed_corpus_access = false, model_inference_runs = 0,
        };
    }
}
