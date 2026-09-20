// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphReader.Export;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record FrozenWorkflowReportReference(string Path, string Sha256);

internal sealed record FrozenWorkflowCsvEvaluationInput(
    string Schema,
    string Scope,
    bool PrivateCorpusAccess,
    bool SealedCorpusAccess,
    bool TruthConsumedByInference,
    FrozenWorkflowReportReference WorkflowReport,
    IReadOnlyList<WholeWorkflowTruthCase> TruthCases,
    IReadOnlyList<WholeWorkflowCaseOutput> Outputs,
    WholeWorkflowEvaluationOptions Options,
    JsonElement Provenance);

/// <summary>Offline scoring only. This command never constructs a model or a workflow.</summary>
internal static class FrozenWorkflowCsvScoring
{
    internal const string InputSchema = "graphreader.frozen-workflow-csv-evaluation-input.v1";
    internal const string ReportPath = "artifacts/frozen-workflow-candidate-inputs/workflow-run-v2/report.json";
    internal const string ReportSha256 = "3f5e2a840252d98c3f1af6bd8d87ca9e79b64f57829f3b9b7b9dd7abf45c83ef";
    private const string ArtifactDirectory = "artifacts";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter<ExportMode>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    internal static object Run(
        string repositoryRoot, string inputPath, string inputSha256, string outputPath,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        string artifactRoot = Path.Combine(repositoryRoot, ArtifactDirectory);
        string inputFile = FrozenCandidateBinding.RequireUnderRoot(artifactRoot, inputPath, "CSV evaluation input");
        string outputFile = FrozenCandidateBinding.RequireUnderRoot(artifactRoot, outputPath, "CSV evaluation output");
        if (File.Exists(outputFile)) throw new InvalidDataException("Refusing to replace a CSV evaluation result.");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] inputBytes = File.ReadAllBytes(inputFile);
        if (FrozenCandidateBinding.Hash(inputBytes) != FrozenCandidateBinding.RequireSha256(inputSha256, nameof(inputSha256)))
            throw new InvalidDataException("CSV evaluation input checksum mismatch.");
        using (JsonDocument inputDocument = JsonDocument.Parse(inputBytes))
            RejectDuplicateProperties(inputDocument.RootElement);
        FrozenWorkflowCsvEvaluationInput input = JsonSerializer.Deserialize<FrozenWorkflowCsvEvaluationInput>(inputBytes, JsonOptions)
            ?? throw new InvalidDataException("CSV evaluation input is empty.");
        string reportFile = FrozenCandidateBinding.RequireUnderRoot(artifactRoot,
            RelativeFileUnder(repositoryRoot, input.WorkflowReport.Path), "workflow report");
        byte[] reportBytes = File.ReadAllBytes(reportFile);
        if (FrozenCandidateBinding.Hash(reportBytes) !=
            FrozenCandidateBinding.RequireSha256(input.WorkflowReport.Sha256, "workflow report SHA-256"))
            throw new InvalidDataException("Frozen workflow report checksum mismatch.");
        using JsonDocument report = JsonDocument.Parse(reportBytes);
        RejectDuplicateProperties(report.RootElement);
        ValidateFrozenSources(reportFile, report.RootElement);
        IReadOnlyList<WholeWorkflowCaseOutput> outputs = ValidateInput(input, report.RootElement, repositoryRoot);
        cancellationToken.ThrowIfCancellationRequested();
        WholeWorkflowEvaluationResult metrics = WholeWorkflowCsvEvaluator.Evaluate(
            input.TruthCases, outputs, input.Options, cancellationToken);
        object result = new
        {
            schema = "graphreader.frozen-workflow-csv-evaluation-report.v1",
            scope = "project-owned-synthetic-only",
            status = "diagnostic-completed",
            evaluation_input_sha256 = FrozenCandidateBinding.Hash(inputBytes),
            workflow_report = input.WorkflowReport,
            source_count = input.TruthCases.Count,
            metrics,
            options = input.Options,
            acceptance_evaluated = false,
            production_approved = false,
            private_corpus_access = false,
            sealed_corpus_access = false,
            truth_consumed_by_inference = false,
            model_inference_runs = 0,
            elapsed_milliseconds = timer.Elapsed.TotalMilliseconds,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        FrozenCandidateSyntheticRunner.WriteNew(outputFile, JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions));
        return result;
    }

    internal static IReadOnlyList<WholeWorkflowCaseOutput> ValidateInput(
        FrozenWorkflowCsvEvaluationInput input, JsonElement report, string repositoryRoot)
    {
        if (input.Schema != InputSchema || input.Scope != "project-owned-synthetic-only" ||
            input.PrivateCorpusAccess || input.SealedCorpusAccess || input.TruthConsumedByInference ||
            input.Provenance.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("CSV evaluation scope or provenance is invalid.");
        if (report.GetProperty("schema").GetString() != FrozenCandidateSyntheticRunner.ReportSchema ||
            report.GetProperty("scope").GetString() != "local-synthetic-frozen-candidate-diagnostic" ||
            report.GetProperty("production_approved").GetBoolean() ||
            report.GetProperty("private_corpus_access").GetBoolean() ||
            report.GetProperty("sealed_corpus_access").GetBoolean() ||
            report.GetProperty("truth_consumed_by_inference").GetBoolean())
            throw new InvalidDataException("The saved workflow report is not a synthetic diagnostic.");
        JsonElement[] cases = report.GetProperty("cases").EnumerateArray().ToArray();
        if (cases.Length == 0 || report.GetProperty("source_count").GetInt32() != cases.Length ||
            input.TruthCases.Count != cases.Length || input.Outputs.Count != cases.Length)
            throw new InvalidDataException("CSV evaluation must retain every source in the frozen workflow report.");
        var result = new List<WholeWorkflowCaseOutput>(cases.Length);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        string reportRoot = Path.GetDirectoryName(RelativeFileUnder(repositoryRoot, input.WorkflowReport.Path))!;
        for (int index = 0; index < cases.Length; index++)
        {
            JsonElement saved = cases[index];
            WholeWorkflowTruthCase truth = input.TruthCases[index];
            WholeWorkflowCaseOutput output = input.Outputs[index];
            string sourceId = saved.GetProperty("source_id").GetString()!;
            string sourceSha256 = saved.GetProperty("image_sha256").GetString()!;
            string status = saved.GetProperty("status").GetString()!;
            bool succeeded = status == "completed";
            if (status is not ("completed" or "failed") || !identities.Add(sourceId) ||
                truth.CaseKey != sourceId || output.CaseKey != sourceId ||
                truth.SourceSha256 != sourceSha256 || output.SourceSha256 != sourceSha256 ||
                truth.SourceWidth != saved.GetProperty("width").GetInt32() ||
                truth.SourceHeight != saved.GetProperty("height").GetInt32() ||
                output.WorkflowSucceeded != succeeded ||
                output.FailureCode != (succeeded ? null : saved.GetProperty("failure_type").GetString()))
                throw new InvalidDataException("CSV evaluation changed a saved source identity or outcome.");
            JsonElement[] artifacts = saved.GetProperty("artifacts").EnumerateArray().ToArray();
            if (output.Artifacts.Count != artifacts.Length)
                throw new InvalidDataException("CSV evaluation changed the saved artifact set.");
            var boundArtifacts = new List<WholeWorkflowCsvArtifact>(artifacts.Length);
            for (int artifactIndex = 0; artifactIndex < artifacts.Length; artifactIndex++)
            {
                JsonElement savedArtifact = artifacts[artifactIndex];
                WholeWorkflowCsvArtifact artifact = output.Artifacts[artifactIndex];
                string savedPath = RelativeFileUnder(reportRoot, savedArtifact.GetProperty("path").GetString()!);
                string inputArtifactPath = RelativeFileUnder(repositoryRoot,
                    artifact.WrittenPath ?? throw new InvalidDataException("Saved CSV evaluation requires a file artifact."));
                if (!string.Equals(savedPath, inputArtifactPath, StringComparison.OrdinalIgnoreCase) ||
                    artifact.FileName != savedArtifact.GetProperty("file").GetString() ||
                    artifact.Sha256 != savedArtifact.GetProperty("sha256").GetString() ||
                    artifact.RowCount != savedArtifact.GetProperty("row_count").GetInt32())
                    throw new InvalidDataException("CSV evaluation changed a saved artifact identity.");
                boundArtifacts.Add(artifact with { WrittenPath = savedPath });
            }
            result.Add(output with { Artifacts = boundArtifacts });
        }
        if (result.Count(static output => output.WorkflowSucceeded) != report.GetProperty("completed_count").GetInt32() ||
            result.Count(static output => !output.WorkflowSucceeded) != report.GetProperty("failed_count").GetInt32())
            throw new InvalidDataException("Saved workflow case totals do not reconcile.");
        return result;
    }

    internal static void ValidateFrozenSources(string reportFile, JsonElement report)
    {
        string snapshotRoot = Path.Combine(Path.GetDirectoryName(reportFile)!, "frozen-inputs");
        using JsonDocument input = ReadSnapshot("input-manifest.json", "input_manifest_sha256");
        using JsonDocument candidate = ReadSnapshot("candidate-binding.json", "candidate_binding_sha256");
        JsonElement manifest = input.RootElement;
        string protocolSha256 = report.GetProperty("protocol_sha256").GetString()!;
        _ = FrozenCandidateBinding.RequireSha256(protocolSha256, "protocol SHA-256");
        if (manifest.GetProperty("schema").GetString() != FrozenCandidateSyntheticRunner.InputSchema ||
            manifest.GetProperty("split").GetString() != "synthetic" ||
            manifest.GetProperty("protocol_sha256").GetString() != protocolSha256 ||
            candidate.RootElement.GetProperty("protocol").GetProperty("sha256").GetString() != protocolSha256 ||
            candidate.RootElement.GetProperty("candidate_id").GetString() != report.GetProperty("candidate_id").GetString() ||
            candidate.RootElement.GetProperty("revision").GetString() != report.GetProperty("revision").GetString())
            throw new InvalidDataException("Saved workflow snapshots do not bind the reported synthetic composition.");
        JsonElement[] sources = manifest.GetProperty("sources").EnumerateArray().ToArray();
        JsonElement[] cases = report.GetProperty("cases").EnumerateArray().ToArray();
        if (sources.Length == 0 || cases.Length != sources.Length ||
            report.GetProperty("source_count").GetInt32() != sources.Length)
            throw new InvalidDataException("Saved workflow report omitted a frozen source.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < sources.Length; index++)
        {
            JsonElement source = sources[index];
            JsonElement saved = cases[index];
            string sha256 = FrozenCandidateBinding.RequireSha256(source.GetProperty("sha256").GetString()!, "source SHA-256");
            if (!identities.Add(sha256) || saved.GetProperty("image_sha256").GetString() != sha256 ||
                saved.GetProperty("width").GetInt32() != source.GetProperty("width").GetInt32() ||
                saved.GetProperty("height").GetInt32() != source.GetProperty("height").GetInt32())
                throw new InvalidDataException("Saved workflow source identity or order differs from the frozen input.");
        }

        JsonDocument ReadSnapshot(string name, string hashProperty)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(snapshotRoot, name));
            string expected = FrozenCandidateBinding.RequireSha256(report.GetProperty(hashProperty).GetString()!, hashProperty);
            if (FrozenCandidateBinding.Hash(bytes) != expected)
                throw new InvalidDataException("Saved workflow input snapshot checksum mismatch.");
            JsonDocument document = JsonDocument.Parse(bytes);
            try { RejectDuplicateProperties(document.RootElement); return document; }
            catch { document.Dispose(); throw; }
        }
    }

    private static string RelativeFileUnder(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Replace('\\', '/').Split('/').Any(static part => part is "" or "." or ".."))
            throw new InvalidDataException("CSV artifact paths must be relative and cannot traverse directories.");
        return FrozenCandidateBinding.RequireUnderRoot(root, relative, "CSV artifact");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate CSV evaluation JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
