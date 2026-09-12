// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>Aggregate-only child process. Canonical admission and budget accounting belong to its parent.</summary>
internal static class OriginalDbOcrSealedWorker
{
    internal const string Command = "--evaluate-original-db-sealed";
    internal const string RequestSchema = "graphreader.original-db-ocr-sealed-worker-request.v1";
    internal const string ResultSchema = "graphreader.original-db-ocr-sealed-worker-result.v1";
    internal const string MetricReferenceSha256 = "8b879664c33e83f2aaec0e637f4f0f4df0f9701fe04ac43867672dde2e662656";
    private const int MaximumRequestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly HashSet<string> RequestFields =
    [
        "schema", "acceptance_scope", "split", "attempt_id", "admission_binding_sha256", "set_id",
        "candidate_path", "candidate_sha256", "archive_path", "archive_sha256", "archive_manifest_sha256",
        "source_count", "coverage_protocol_sha256",
    ];

    internal sealed record Request(string AttemptId, string AdmissionBindingSha256, string SetId,
        string CandidatePath, string CandidateSha256, string ArchivePath, string ArchiveSha256,
        string ArchiveManifestSha256, int SourceCount, string CoverageProtocolSha256);

    internal static async Task<int> RunAsync(string[] args, string root)
    {
        TextWriter output = Console.Out;
        TextWriter errors = Console.Error;
        // Model diagnostics cannot become result records. The parent separately bounds native stderr.
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (args.Length != 3 || !IsHash(args[2])) throw new InvalidDataException();
            string requestPath = InsideArtifacts(root, args[1]);
            using var requestFile = new FileStream(requestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (requestFile.Length is <= 0 or > MaximumRequestBytes) throw new InvalidDataException();
            byte[] requestBytes = new byte[checked((int)requestFile.Length)];
            await requestFile.ReadExactlyAsync(requestBytes, cancellation.Token).ConfigureAwait(false);
            if (Convert.ToHexStringLower(SHA256.HashData(requestBytes)) != args[2]) throw new InvalidDataException();
            Request request = ParseRequest(requestBytes);
            string archivePath = InsideArtifacts(root, request.ArchivePath, "synthetic-sealed-reserves");
            _ = InsideArtifacts(root, request.CandidatePath);
            var timer = Stopwatch.StartNew();

            OriginalDbOcrCorpusAggregate aggregate = await OriginalDbOcrMemoryRuntime.RunAsync(
                root, request.CandidatePath, request.CandidateSha256,
                async (ocr, raw, axis, token) =>
                {
                    // Model files and the executing runtime are authenticated before sealed admission.
                    using var archiveFile = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    string nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                    var handshake = new FrozenRealFirstReadHandshake(Console.In, output,
                        request.AttemptId, request.CandidateSha256, TimeSpan.FromSeconds(30), nonce);
                    using var stream = new OriginalDbOcrReadReceiptStream(archiveFile, () =>
                    {
                        output.WriteLine($"G22_POSITIVE_READ/1 {request.AttemptId} {request.CandidateSha256} {nonce}");
                        output.Flush();
                    });
                    IReadOnlyList<OriginalDbOcrSealedSourcePayload> sources = OriginalDbOcrSealedArchive.Read(
                        stream, request.ArchiveSha256, request.ArchiveManifestSha256, request.SourceCount,
                        request.CoverageProtocolSha256, handshake.BeforeFirstPayloadRead, token);
                    return await OriginalDbOcrInMemoryCorpusEvaluator.EvaluateAsync(sources, ocr, raw, axis, token)
                        .ConfigureAwait(false);
                }, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            output.WriteLine(SerializeResult(request, args[2], timer.Elapsed.TotalMilliseconds, aggregate));
            output.Flush();
            return 0;
        }
        catch (OperationCanceledException)
        {
            errors.WriteLine("ORIGINAL_DB_SEALED_WORKER_CANCELLED");
            return 130;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            errors.WriteLine("ORIGINAL_DB_SEALED_WORKER_FAILED");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            Console.SetOut(output);
            Console.SetError(errors);
        }
    }

    internal static string SerializeResult(Request request, string requestSha256, double elapsedMilliseconds,
        OriginalDbOcrCorpusAggregate aggregate) => JsonSerializer.Serialize(new
        {
            schema = ResultSchema, status = "completed", split = "sealed",
            acceptance_scope = OriginalDbOcrSealedArchive.AcceptanceScope,
            request.AttemptId, request.AdmissionBindingSha256, request.SetId, request.CandidateSha256,
            request.ArchiveSha256, request.ArchiveManifestSha256, request.CoverageProtocolSha256,
            request_sha256 = requestSha256, metric_reference_sha256 = MetricReferenceSha256,
            execution_provider = "CPUExecutionProvider", cpu_threads = 1,
            graph_optimization = "ORT_DISABLE_ALL", model_inference = true,
            case_output = false, production_approved = false, elapsed_ms = elapsedMilliseconds,
            aggregate,
        }, Options);

    internal static Request ParseRequest(byte[] payload)
    {
        try
        {
            if (payload.Length is <= 0 or > MaximumRequestBytes) throw new InvalidDataException();
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
                if (!names.Add(property.Name)) throw new InvalidDataException();
            if (!names.SetEquals(RequestFields) || Text("schema") != RequestSchema ||
                Text("acceptance_scope") != OriginalDbOcrSealedArchive.AcceptanceScope || Text("split") != "sealed" ||
                Hash("coverage_protocol_sha256") != OriginalDbOcrSealedArchive.CoverageProtocolSha256)
                throw new InvalidDataException();
            string attempt = Text("attempt_id");
            if (attempt.Length is < 1 or > 128 || attempt.Any(character => character is < '!' or > '~'))
                throw new InvalidDataException();
            int count = value.GetProperty("source_count").GetInt32();
            if (count is < 1 or > OriginalDbOcrInMemoryCorpusEvaluator.MaximumSources) throw new InvalidDataException();
            return new(attempt, Hash("admission_binding_sha256"), Hash("set_id"),
                RelativePath(Text("candidate_path")), Hash("candidate_sha256"),
                RelativePath(Text("archive_path")), Hash("archive_sha256"), Hash("archive_manifest_sha256"),
                count, Hash("coverage_protocol_sha256"));

            string Text(string name) => value.GetProperty(name).GetString() ?? throw new InvalidDataException();
            string Hash(string name) => IsHash(Text(name)) ? Text(name) : throw new InvalidDataException();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new InvalidDataException("OCR_SEALED_REQUEST_INVALID");
        }
    }

    private static bool IsHash(string value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string RelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') ||
            path.Split('/').Any(part => part is "" or "." or "..")) throw new InvalidDataException();
        return path;
    }

    private static string InsideArtifacts(string root, string path, string? requiredDirectory = null)
    {
        _ = RelativePath(path);
        string fullRoot = Path.GetFullPath(root);
        string parent = Path.Combine(fullRoot, "artifacts");
        if (requiredDirectory is not null) parent = Path.Combine(parent, requiredDirectory);
        string full = Path.GetFullPath(Path.Combine(fullRoot, path));
        if (!full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        string current = fullRoot;
        foreach (string part in Path.GetRelativePath(fullRoot, full).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        }
        return full;
    }
}
