// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphReader.Export;

namespace GraphReader.RealAcceptance.Ocr;

internal static class FrozenWorkflowCsvScoringSelfTest
{
    internal static object Run()
    {
        const string sourceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        WholeWorkflowTruthCase[] truths = Enumerable.Range(0, 7).Select(index => new WholeWorkflowTruthCase(
            $"source-{index}", index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture), 100, 100, [new("series")],
            [new("point", "series", 10, 20, 1, 2, 1, ExportMode.PrintedSession, null)], null)).ToArray();
        WholeWorkflowCaseOutput[] outputs = truths.Select(truth => new WholeWorkflowCaseOutput(
            truth.CaseKey, truth.SourceSha256, false, "TestFailure", [])).ToArray();
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
                source_id = truth.CaseKey, image_sha256 = truth.SourceSha256, width = 100, height = 100,
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
        Reject(input with { WorkflowReport = input.WorkflowReport with { Path = "../report.json" } });

        // A different inventory size must work without permitting partial scoring.
        JsonObject smallReport = JsonNode.Parse(report.GetRawText())!.AsObject();
        smallReport["source_count"] = 2;
        smallReport["failed_count"] = 2;
        smallReport["cases"] = new JsonArray(smallReport["cases"]!.AsArray().Take(2)
            .Select(static item => item!.DeepClone()).ToArray());
        FrozenWorkflowCsvEvaluationInput smallInput = input with
        {
            TruthCases = truths.Take(2).ToArray(), Outputs = outputs.Take(2).ToArray(),
        };
        IReadOnlyList<WholeWorkflowCaseOutput> smallBound = FrozenWorkflowCsvScoring.ValidateInput(
            smallInput, JsonSerializer.SerializeToElement(smallReport), repositoryRoot);
        if (WholeWorkflowCsvEvaluator.Evaluate(smallInput.TruthCases, smallBound, input.Options,
            CancellationToken.None).FailedCases != 2)
            throw new InvalidDataException("The complete two-source inventory was not retained.");

        string snapshotRoot = Path.Combine(repositoryRoot, "artifacts", "workflow-csv-binding-selftest",
            Guid.NewGuid().ToString("N"), "frozen-inputs");
        Directory.CreateDirectory(snapshotRoot);
        string reportFile = Path.Combine(Path.GetDirectoryName(snapshotRoot)!, "report.json");
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = FrozenCandidateSyntheticRunner.InputSchema, split = "synthetic", protocol_sha256 = sourceHash,
            sources = smallInput.TruthCases.Select(truth => new
            {
                sha256 = truth.SourceSha256, width = truth.SourceWidth, height = truth.SourceHeight,
            }).ToArray(),
        });
        byte[] candidateBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            candidate_id = "fictitious", revision = "fixture", protocol = new { sha256 = sourceHash },
        });
        File.WriteAllBytes(Path.Combine(snapshotRoot, "input-manifest.json"), manifestBytes);
        File.WriteAllBytes(Path.Combine(snapshotRoot, "candidate-binding.json"), candidateBytes);
        smallReport["protocol_sha256"] = sourceHash;
        smallReport["candidate_id"] = "fictitious";
        smallReport["revision"] = "fixture";
        smallReport["input_manifest_sha256"] = FrozenCandidateBinding.Hash(manifestBytes);
        smallReport["candidate_binding_sha256"] = FrozenCandidateBinding.Hash(candidateBytes);
        FrozenWorkflowCsvScoring.ValidateFrozenSources(reportFile, JsonSerializer.SerializeToElement(smallReport));
        void RejectSnapshot(Action<JsonObject> change)
        {
            JsonObject altered = smallReport.DeepClone().AsObject();
            change(altered);
            try { FrozenWorkflowCsvScoring.ValidateFrozenSources(reportFile, JsonSerializer.SerializeToElement(altered)); }
            catch (InvalidDataException) { rejected++; return; }
            throw new InvalidDataException("Changed workflow snapshot binding was accepted.");
        }
        RejectSnapshot(node => node["input_manifest_sha256"] = sourceHash);
        RejectSnapshot(node => node["candidate_binding_sha256"] = sourceHash);
        RejectSnapshot(node => node["protocol_sha256"] = new string('b', 64));
        RejectSnapshot(node => node["candidate_id"] = "different");
        RejectSnapshot(node => node["cases"]!.AsArray().RemoveAt(1));
        RejectSnapshot(node => node["cases"]![0]!["width"] = 99);
        RejectSnapshot(node => node["cases"]![0]!["image_sha256"] = new string('c', 64));
        RejectSnapshot(node => node["source_count"] = 1);
        return new
        {
            status = "pass", model_free = true, scenarios_passed = 3 + rejected,
            variable_inventory_verified = true, saved_source_snapshots_verified = true,
            failed_cases_preserve_full_truth = true, private_corpus_access = false,
            sealed_corpus_access = false, model_inference_runs = 0,
        };
    }
}
