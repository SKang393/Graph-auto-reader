// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text;
using System.Text.Json;

namespace GraphReader.RealAcceptance.Ocr;

/// <summary>
/// Short-lived aggregate-only worker. The parent owns durable first-read
/// accounting, process supervision, and aggregate result persistence.
/// </summary>
internal static class FrozenRealWorkflowWorker
{
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        TextWriter previousOutput = Console.Out;
        TextWriter previousError = Console.Error;
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false),
            bufferSize: 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        // Managed runtime diagnostics must never become an accidental case-level
        // output channel. The parent also supervises the native stderr pipe.
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        string? checkedOutputRoot = null;
        try
        {
            Dictionary<string, string> options = ParseOptions(args);
            if (FrozenRealWorkflowAdmission.IsContinuousIntegration())
                throw new InvalidOperationException("REAL_WORKFLOW_EXPLICIT_LOCAL_OPT_IN_REQUIRED");
            string repositoryRoot = Path.GetFullPath(Environment.CurrentDirectory);
            string outputRoot = RequireUnusedOutputRoot(repositoryRoot, options["--output-root"]);
            checkedOutputRoot = outputRoot;
            string split = options["--split"];
            FrozenCandidateBinding binding = FrozenCandidateBinding.Load(repositoryRoot,
                options["--candidate-binding"], options["--candidate-binding-sha256"], cancellation.Token);
            FrozenRealCorpusSelection inventory = FrozenRealCorpusInventory.Load(
                options["--corpus-root"], split, cancellation.Token);
            FrozenRealWorkflowAdmissionResult admission = FrozenRealWorkflowAdmission.Load(
                repositoryRoot, Path.Combine(repositoryRoot, binding.Protocol.RelativePath),
                binding.Protocol.Sha256, binding, inventory, split,
                explicitOptIn: true, cancellation.Token);

            // Admission and metadata authentication precede all model probes.
            // Only public candidate snapshots are written under this sibling.
            await using FrozenCandidateWorkflowRuntime candidate = await FrozenCandidateWorkflowFactory.CreateAsync(
                repositoryRoot, Path.Combine(outputRoot, "candidate-runtime"), binding,
                cancellation.Token, aggregateOnly: true).ConfigureAwait(false);
            var adapter = new FrozenCandidateGroupedWorkflowAdapter(candidate.Workflow,
                repositoryRoot, Path.Combine(outputRoot, "case-output"), binding.Sha256,
                aggregateOnly: true);
            FrozenRealFirstReadHandshake? handshake = admission.SealedFirstReadRequired
                ? new FrozenRealFirstReadHandshake(input, output, options["--attempt-id"], binding.Sha256,
                    TimeSpan.FromSeconds(30))
                : null;
            FrozenRealWorkflowRunnerResult result = await FrozenRealWorkflowRunner.RunAsync(
                admission, options["--corpus-root"], adapter.ExecuteAsync, handshake,
                cancellation.Token).ConfigureAwait(false);
            if (Directory.Exists(Path.Combine(outputRoot, "case-output")) ||
                Directory.Exists(Path.Combine(outputRoot, "candidate-runtime", "inference-cache")))
            {
                throw new InvalidDataException("REAL_WORKFLOW_PERSISTENCE_BOUNDARY_VIOLATED");
            }
            string report = JsonSerializer.Serialize(new
            {
                candidate_sha256 = binding.Sha256,
                protocol_sha256 = admission.ProtocolSha256,
                assignment_sha256 = admission.AssignmentSha256,
                selected_inventory_sha256 = admission.SelectedInventorySha256,
                corpus_content_sha256 = result.CorpusContentSha256,
                split,
                aggregate = result.Aggregate,
            }, WireJson);
            await output.WriteLineAsync("G22_RESULT/1 " + report).ConfigureAwait(false);
            return 0;
        }
        catch (Exception)
        {
            // Never serialize the exception, message, stack, or paths. Even a
            // failed .dig parse may contain publisher or participant details.
            try
            {
                if (checkedOutputRoot is not null &&
                    (Directory.Exists(Path.Combine(checkedOutputRoot, "case-output")) ||
                     Directory.Exists(Path.Combine(checkedOutputRoot, "candidate-runtime", "inference-cache"))))
                {
                    await output.WriteLineAsync("G22_DISCLOSURE/1 PERSISTED_CASE_DATA").ConfigureAwait(false);
                }
                await output.WriteLineAsync("G22_FAILURE/1 REAL_WORKFLOW_FAILED").ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A lost parent pipe is accounted for by process supervision.
            }
            return 2;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
    }

    internal static Dictionary<string, string> ParseOptions(IReadOnlyList<string> args)
    {
        string[] names = ["--candidate-binding", "--candidate-binding-sha256", "--corpus-root",
            "--split", "--output-root", "--attempt-id"];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        bool optIn = false;
        if (args.Count == 0 || args[0] != "--run-frozen-real-workflow-worker")
            throw new InvalidDataException("REAL_WORKFLOW_WORKER_ARGUMENTS_INVALID");
        for (int index = 1; index < args.Count; index++)
        {
            string name = args[index];
            if (name == "--explicit-opt-in" && !optIn)
            {
                optIn = true;
                continue;
            }
            if (!names.Contains(name, StringComparer.Ordinal) || ++index >= args.Count ||
                string.IsNullOrWhiteSpace(args[index]) || !options.TryAdd(name, args[index]))
                throw new InvalidDataException("REAL_WORKFLOW_WORKER_ARGUMENTS_INVALID");
        }
        if (!optIn || options.Count != names.Length ||
            options["--split"] is not (FrozenRealCorpusInventory.RealDev or FrozenRealCorpusInventory.RealSealed) ||
            !Guid.TryParseExact(options["--attempt-id"], "N", out _))
            throw new InvalidDataException("REAL_WORKFLOW_WORKER_ARGUMENTS_INVALID");
        return options;
    }

    private static string RequireUnusedOutputRoot(string repositoryRoot, string path)
    {
        string privateRoot = Path.Combine(repositoryRoot, "artifacts", "private-acceptance");
        string suppliedPath = Path.IsPathRooted(path) ? path : Path.Combine(repositoryRoot, path);
        string fullPath = FrozenCandidateBinding.RequireUnderRoot(privateRoot, suppliedPath, "private runtime output");
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new InvalidDataException("REAL_WORKFLOW_NEW_OUTPUT_REQUIRED");
        for (DirectoryInfo? parent = Directory.GetParent(fullPath); parent is not null; parent = parent.Parent)
        {
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("REAL_WORKFLOW_OUTPUT_LINK_REJECTED");
            if (string.Equals(parent.FullName, repositoryRoot, StringComparison.OrdinalIgnoreCase))
                break;
        }
        return fullPath;
    }
}
