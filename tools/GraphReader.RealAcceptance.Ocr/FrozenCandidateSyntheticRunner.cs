// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Imaging;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record FrozenSyntheticSource(
    string RelativePath,
    string Sha256,
    int Width,
    int Height,
    byte[] Bytes);

internal sealed record FrozenSyntheticInput(
    string Sha256,
    string ProtocolSha256,
    IReadOnlyList<FrozenSyntheticSource> Sources,
    byte[] DocumentBytes);

internal sealed record FrozenCandidateSyntheticExecution(object Report, int FailedCount);

internal static class FrozenCandidateSyntheticRunner
{
    internal const string InputSchema = "graphreader.real-acceptance-frozen-synthetic-input.v1";
    internal const string ReportSchema = "graphreader.real-acceptance-frozen-synthetic-report.v1";
    private const string ProtocolSchema = "graphreader.frozen-workflow-synthetic-diagnostic-protocol.v1";
    private const string SourceManifestSchema = "graphreader.synthetic-runtime-raster-inputs.v1";
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static async Task<FrozenCandidateSyntheticExecution> RunAsync(
        string repositoryRoot,
        string bindingPath,
        string bindingSha256,
        string inputManifestPath,
        string inputManifestSha256,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        outputRoot = RequireNewArtifactOutput(repositoryRoot, outputRoot);
        FrozenCandidateBinding binding = FrozenCandidateBinding.Load(
            repositoryRoot, bindingPath, bindingSha256, cancellationToken);
        FrozenSyntheticInput input = LoadInput(
            repositoryRoot, inputManifestPath, inputManifestSha256, cancellationToken);
        if (!string.Equals(input.ProtocolSha256, binding.Protocol.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Synthetic input manifest does not bind the frozen candidate protocol.");
        }
        ValidateProtocolDefinedSources(
            repositoryRoot,
            binding.Protocol,
            input,
            cancellationToken);

        Directory.CreateDirectory(outputRoot);
        string snapshots = Path.Combine(outputRoot, "frozen-inputs");
        Directory.CreateDirectory(snapshots);
        WriteNew(Path.Combine(snapshots, "candidate-binding.json"), binding.CopyDocumentBytes());
        WriteNew(Path.Combine(snapshots, "input-manifest.json"), input.DocumentBytes);
        string sourceRoot = Path.Combine(snapshots, "sources");
        Directory.CreateDirectory(sourceRoot);
        var sourceSnapshots = new List<(FrozenSyntheticSource Source, string Path)>();
        foreach (FrozenSyntheticSource source in input.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(sourceRoot, $"{source.Sha256}.png");
            WriteNew(path, source.Bytes);
            sourceSnapshots.Add((source, path));
        }

        await ValidateDecodedSourcesAsync(sourceSnapshots, cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var caseReports = new List<object>(sourceSnapshots.Count);
        int completed = 0;
        int failed = 0;
        await using FrozenCandidateWorkflowRuntime candidate =
            await FrozenCandidateWorkflowFactory.CreateAsync(
                    repositoryRoot, outputRoot, binding, cancellationToken)
                .ConfigureAwait(false);
        Guid projectId = ProductionWorkflowPanelStore.CreateStableId(
            "frozen-candidate-synthetic-project-v1", binding.Sha256, input.Sha256);
        foreach ((FrozenSyntheticSource source, string snapshotPath) in sourceSnapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid sourceId = ProductionWorkflowPanelStore.CreateStableId(
                "frozen-candidate-synthetic-source-v1",
                projectId.ToString("D"),
                source.Sha256,
                source.RelativePath);
            Guid runId = ProductionWorkflowPanelStore.CreateStableId(
                "frozen-candidate-synthetic-run-v1",
                binding.Sha256,
                input.Sha256,
                sourceId.ToString("D"));
            string caseOutput = Path.Combine(outputRoot, "cases", source.Sha256);
            try
            {
                var request = new WorkflowRunRequest(
                    runId,
                    new WorkflowImportRequest(
                        projectId,
                        [new WorkflowSourceRequest(sourceId, WorkflowSourceKind.Image, snapshotPath)],
                        enhancementEnabled: false));
                WorkflowRunResult workflow = await candidate.Workflow.RunThroughReviewAsync(
                        request,
                        previousReview: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (workflow.Review.CorrectionJournal.Count != 0)
                {
                    throw new InvalidDataException("Synthetic candidate workflow review was not untouched.");
                }
                WorkflowExportResult export = await candidate.Workflow.ExportAsync(
                        workflow.Review,
                        new WorkflowExportRequest(
                            ProductionWorkflowPanelStore.CreateStableId(
                                "frozen-candidate-synthetic-export-v1", runId.ToString("D")),
                            caseOutput),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!export.Succeeded)
                {
                    throw new InvalidDataException(
                        $"Candidate export failed closed: {export.FailureCode ?? "UNKNOWN"}.");
                }
                string[] csvKinds = export.Artifacts
                    .Where(static artifact => artifact.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    .Select(static artifact => artifact.FileName.Contains("audit", StringComparison.OrdinalIgnoreCase)
                        ? "audit" : "minimal")
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToArray();
                if (!csvKinds.SequenceEqual(new[] { "audit", "minimal" }, StringComparer.Ordinal))
                {
                    throw new InvalidDataException("Candidate export did not produce both minimal and audit CSV artifacts.");
                }
                completed++;
                caseReports.Add(new
                {
                    source_id = sourceId.ToString("D"),
                    image_sha256 = source.Sha256,
                    width = source.Width,
                    height = source.Height,
                    status = "completed",
                    panel_count = workflow.Review.Panels.Count,
                    correction_count = workflow.Review.CorrectionJournal.Count,
                    artifacts = export.Artifacts.Select(artifact => new
                    {
                        file = artifact.FileName,
                        sha256 = artifact.Sha256,
                        row_count = artifact.RowCount,
                        path = artifact.WrittenPath is null
                            ? null
                            : Path.GetRelativePath(outputRoot, artifact.WrittenPath).Replace('\\', '/'),
                    }).ToArray(),
                    warnings = workflow.Review.Warnings.Concat(export.Warnings)
                        .Distinct(StringComparer.Ordinal).ToArray(),
                });
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
            {
                failed++;
                int retainedPanelCount = candidate.PanelStore.PanelIds
                    .Select(candidate.PanelStore.Get)
                    .Count(evidence => evidence.Panel.SourceId == sourceId);
                caseReports.Add(new
                {
                    source_id = sourceId.ToString("D"),
                    image_sha256 = source.Sha256,
                    width = source.Width,
                    height = source.Height,
                    status = "failed",
                    panel_count = retainedPanelCount,
                    correction_count = 0,
                    artifacts = Array.Empty<object>(),
                    warnings = Array.Empty<string>(),
                    error = exception.Message,
                    failure_type = exception.GetType().Name,
                });
            }
        }
        stopwatch.Stop();

        object report = new
        {
            schema = ReportSchema,
            scope = "local-synthetic-frozen-candidate-diagnostic",
            candidate_id = binding.CandidateId,
            revision = binding.Revision,
            candidate_binding_sha256 = binding.Sha256,
            input_manifest_sha256 = input.Sha256,
            protocol_sha256 = input.ProtocolSha256,
            execution_provider = binding.Runtime.ExecutionProvider,
            graph_optimization = binding.Runtime.GraphOptimization,
            ocr_configuration_scope = "unapproved_frozen_candidate",
            source_count = input.Sources.Count,
            completed_count = completed,
            failed_count = failed,
            elapsed_milliseconds = stopwatch.Elapsed.TotalMilliseconds,
            production_approved = false,
            private_corpus_access = false,
            sealed_corpus_access = false,
            model_selection_performed = false,
            truth_consumed_by_inference = false,
            cases = caseReports,
        };
        byte[] reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, ReportJsonOptions);
        WriteNew(Path.Combine(outputRoot, "report.json"), reportBytes);
        return new FrozenCandidateSyntheticExecution(report, failed);
    }

    internal static FrozenSyntheticInput LoadInput(
        string repositoryRoot,
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        path = FrozenCandidateBinding.RequireUnderRoot(repositoryRoot, path, "synthetic input manifest");
        expectedSha256 = FrozenCandidateBinding.RequireSha256(expectedSha256, nameof(expectedSha256));
        byte[] bytes = File.ReadAllBytes(path);
        cancellationToken.ThrowIfCancellationRequested();
        string actualSha256 = FrozenCandidateBinding.Hash(bytes);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Synthetic input manifest checksum mismatch.");
        }
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        RejectDuplicates(document.RootElement);
        RequireExact(document.RootElement, ["schema", "protocol_sha256", "split", "sources"]);
        RequireText(document.RootElement, "schema", InputSchema);
        RequireText(document.RootElement, "split", "synthetic");
        string protocolSha256 = FrozenCandidateBinding.RequireSha256(
            Text(document.RootElement, "protocol_sha256"), "protocol_sha256");
        JsonElement sources = document.RootElement.GetProperty("sources");
        if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Synthetic input manifest requires a non-empty sources array.");
        }
        var result = new List<FrozenSyntheticSource>(sources.GetArrayLength());
        foreach (JsonElement source in sources.EnumerateArray())
        {
            RequireExact(source, ["relative_path", "sha256", "width", "height"]);
            string relativePath = Text(source, "relative_path").Replace('\\', '/');
            if (Path.IsPathRooted(relativePath) ||
                relativePath.Split('/').Any(static part => part is "" or "." or "..") ||
                !string.Equals(Path.GetExtension(relativePath), ".png", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Synthetic source must be a canonical repository-relative PNG path.");
            }
            string sha = FrozenCandidateBinding.RequireSha256(Text(source, "sha256"), "source sha256");
            int width = PositiveInt(source, "width");
            int height = PositiveInt(source, "height");
            string sourcePath = FrozenCandidateBinding.RequireUnderRoot(repositoryRoot, relativePath, "synthetic source");
            byte[] sourceBytes = File.ReadAllBytes(sourcePath);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(FrozenCandidateBinding.Hash(sourceBytes), sha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Synthetic source checksum mismatch.");
            }
            result.Add(new FrozenSyntheticSource(relativePath, sha, width, height, sourceBytes));
        }
        if (result.Select(static source => source.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count ||
            result.Select(static source => source.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
        {
            throw new InvalidDataException("Synthetic input sources must have distinct paths and byte identities.");
        }
        return new FrozenSyntheticInput(actualSha256, protocolSha256, result.AsReadOnly(), (byte[])bytes.Clone());
    }

    internal static void ValidateProtocolDefinedSources(
        string repositoryRoot,
        FrozenCandidateFile protocol,
        FrozenSyntheticInput input,
        CancellationToken cancellationToken)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] protocolBytes = protocol.CopyBytes();
        if (!string.Equals(
                FrozenCandidateBinding.Hash(protocolBytes),
                protocol.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Frozen candidate protocol bytes changed before source validation.");
        }

        using JsonDocument document = ParseDocument(protocolBytes, "frozen candidate protocol");
        RejectDuplicates(document.RootElement);
        RequireText(document.RootElement, "schema", ProtocolSchema);
        if (!document.RootElement.TryGetProperty("input_identity", out JsonElement identity) ||
            identity.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Frozen candidate protocol requires input_identity.");
        }
        RequireExact(identity,
        [
            "train_manifest", "train_manifest_sha256", "dev_manifest", "dev_manifest_sha256",
            "source_count", "expected_prepared_panels_from_prior_upstream_run", "source_selection",
        ]);
        string trainManifest = Text(identity, "train_manifest");
        string devManifest = Text(identity, "dev_manifest");
        if (string.Equals(trainManifest, devManifest, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Frozen candidate protocol train and dev manifests must be distinct.");
        }
        int sourceCount = PositiveInt(identity, "source_count");
        FrozenSyntheticSource[] expected = LoadDeclaredSourceManifest(
                repositoryRoot,
                trainManifest,
                FrozenCandidateBinding.RequireSha256(
                    Text(identity, "train_manifest_sha256"),
                    "train_manifest_sha256"),
                "train",
                cancellationToken)
            .Concat(LoadDeclaredSourceManifest(
                repositoryRoot,
                devManifest,
                FrozenCandidateBinding.RequireSha256(
                    Text(identity, "dev_manifest_sha256"),
                    "dev_manifest_sha256"),
                "validation",
                cancellationToken))
            .ToArray();
        if (expected.Length != sourceCount)
        {
            throw new InvalidDataException("Protocol source count differs from its declared train and dev manifests.");
        }
        if (expected.Select(static source => source.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected.Length ||
            expected.Select(static source => source.Sha256)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected.Length)
        {
            throw new InvalidDataException("Protocol-defined synthetic sources must have distinct paths and byte identities.");
        }
        if (input.Sources.Count != expected.Length)
        {
            throw new InvalidDataException("Synthetic input does not contain the complete protocol-defined source set.");
        }
        for (int index = 0; index < expected.Length; index++)
        {
            FrozenSyntheticSource actual = input.Sources[index];
            FrozenSyntheticSource required = expected[index];
            if (!string.Equals(actual.RelativePath, required.RelativePath, StringComparison.Ordinal) ||
                !string.Equals(actual.Sha256, required.Sha256, StringComparison.OrdinalIgnoreCase) ||
                actual.Width != required.Width ||
                actual.Height != required.Height)
            {
                throw new InvalidDataException(
                    $"Synthetic input source {index} differs from the protocol-defined ordered source sequence.");
            }
        }
    }

    private static FrozenSyntheticSource[] LoadDeclaredSourceManifest(
        string repositoryRoot,
        string relativeManifestPath,
        string expectedSha256,
        string expectedSplit,
        CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(relativeManifestPath) ||
            relativeManifestPath.Replace('\\', '/').Split('/').Any(static part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("Protocol source manifest path must be canonical and repository-relative.");
        }
        string manifestPath = FrozenCandidateBinding.RequireUnderRoot(
            repositoryRoot, relativeManifestPath, "protocol source manifest");
        byte[] bytes = File.ReadAllBytes(manifestPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                FrozenCandidateBinding.Hash(bytes),
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Protocol source manifest checksum mismatch.");
        }

        using JsonDocument document = ParseDocument(bytes, "protocol source manifest");
        RejectDuplicates(document.RootElement);
        RequireExact(document.RootElement,
        [
            "contains_precomputed_masks", "contains_truth", "images", "preset",
            "schema", "seed", "source", "split",
        ]);
        RequireText(document.RootElement, "schema", SourceManifestSchema);
        RequireText(document.RootElement, "split", expectedSplit);
        RequireFalse(document.RootElement, "contains_truth");
        RequireFalse(document.RootElement, "contains_precomputed_masks");
        JsonElement images = document.RootElement.GetProperty("images");
        if (images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Protocol source manifest requires a non-empty images array.");
        }

        string manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("Protocol source manifest has no directory.");
        var sources = new List<FrozenSyntheticSource>(images.GetArrayLength());
        foreach (JsonElement image in images.EnumerateArray())
        {
            RequireExact(image, ["family", "height", "image", "image_sha256", "seed", "split", "width"]);
            RequireText(image, "split", expectedSplit);
            string imageName = Text(image, "image").Replace('\\', '/');
            if (Path.IsPathRooted(imageName) ||
                imageName.Split('/').Any(static part => part is "" or "." or "..") ||
                !string.Equals(Path.GetExtension(imageName), ".png", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Protocol source image path must be a canonical relative PNG path.");
            }
            string sourcePath = FrozenCandidateBinding.RequireUnderRoot(
                repositoryRoot,
                Path.Combine(manifestDirectory, imageName),
                "protocol synthetic source");
            string relativeSource = Path.GetRelativePath(repositoryRoot, sourcePath).Replace('\\', '/');
            sources.Add(new FrozenSyntheticSource(
                relativeSource,
                FrozenCandidateBinding.RequireSha256(Text(image, "image_sha256"), "image_sha256"),
                PositiveInt(image, "width"),
                PositiveInt(image, "height"),
                []));
        }
        return sources.ToArray();
    }

    private static JsonDocument ParseDocument(byte[] bytes, string label)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{label} is not valid JSON.", exception);
        }
    }

    private static async Task ValidateDecodedSourcesAsync(
        IEnumerable<(FrozenSyntheticSource Source, string Path)> sources,
        CancellationToken cancellationToken)
    {
        var importer = new ImageImportService();
        foreach ((FrozenSyntheticSource source, string path) in sources)
        {
            ImageImportResult decoded = await importer.ImportAsync(path, cancellationToken).ConfigureAwait(false);
            ImportedImage? image = decoded.Image;
            if (!decoded.IsSuccess || image is null ||
                image.Metadata.Width != source.Width || image.Metadata.Height != source.Height ||
                !string.Equals(image.Sha256, source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Synthetic source decoded identity does not match its frozen manifest.");
            }
        }
    }

    private static string RequireNewArtifactOutput(string repositoryRoot, string outputRoot)
    {
        string artifactsRoot = Path.Combine(repositoryRoot, "artifacts");
        string resolved = FrozenCandidateBinding.RequireUnderRoot(artifactsRoot, outputRoot, "synthetic output");
        if (Directory.Exists(resolved) || File.Exists(resolved))
        {
            throw new IOException("Frozen candidate output must be a new path.");
        }
        return resolved;
    }

    internal static void WriteNew(string path, byte[] bytes)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        string expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new IOException("Frozen candidate snapshot write did not preserve exact bytes.");
        }
    }

    private static int PositiveInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || !value.TryGetInt32(out int result) || result <= 0)
        {
            throw new InvalidDataException($"Synthetic input {name} must be a positive integer.");
        }
        return result;
    }

    private static string Text(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Synthetic input {name} must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static void RequireText(JsonElement parent, string name, string expected)
    {
        if (!string.Equals(Text(parent, name), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Synthetic input {name} must equal '{expected}'.");
        }
    }

    private static void RequireFalse(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.False)
        {
            throw new InvalidDataException($"Synthetic input {name} must be false.");
        }
    }

    private static void RequireExact(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Synthetic input record must be an object.");
        }
        string[] actual = value.EnumerateObject().Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        string[] orderedExpected = expected.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(orderedExpected, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Synthetic input fields do not match the frozen schema.");
        }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException($"Synthetic input contains duplicate field '{property.Name}'.");
                }
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                RejectDuplicates(item);
            }
        }
    }
}
