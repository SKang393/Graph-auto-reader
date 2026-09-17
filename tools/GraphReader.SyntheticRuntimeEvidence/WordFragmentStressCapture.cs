// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>
/// Captures source-space raw OCR boxes for a bounded project-owned train-only
/// word-fragment diagnostic. Runtime input never contains truth, text, roles,
/// or truth-derived crops. Recognition output is intentionally not serialized.
/// </summary>
internal static class WordFragmentStressCapture
{
    internal const string Command = "--capture-word-fragment-stress";
    internal const string SelfTestCommand = "--self-test-word-fragment-stress-boundary";
    private const string RequestSchema = "graphreader.word-fragment-stress-v1.capture-request.v1";
    private const string Scope = "project-owned-synthetic-train-only-word-fragment-stress";
    private const int MaximumSourceCount = 36;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static async Task<int> RunAsync(string[] args, string repositoryRoot)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            if (args.Length != 6 || args[0] != Command)
            {
                throw new InvalidDataException(
                    "Usage: --capture-word-fragment-stress <request.json> <request-sha256> " +
                    "<diagnostic-candidate.json> <candidate-sha256> <new-output.json>");
            }

            string root = Path.GetFullPath(repositoryRoot);
            string requestPath = InsideArtifacts(root, args[1]);
            string requestSha = RequireSha(args[2], "request");
            string candidatePath = InsideArtifacts(root, args[3]);
            string candidateSha = RequireSha(args[4], "candidate");
            string outputPath = InsideArtifacts(root, args[5]);
            if (File.Exists(outputPath) || Directory.Exists(outputPath))
            {
                throw new IOException("Use a new output path for stress capture evidence.");
            }
            string evidenceRoot = Path.GetDirectoryName(requestPath)
                ?? throw new InvalidDataException("Stress request has no parent directory.");
            if (!string.Equals(Path.GetFileName(requestPath), "capture-request.json", StringComparison.Ordinal) ||
                !string.Equals(candidatePath, Path.Combine(evidenceRoot, "diagnostic-candidate.json"),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(outputPath), evidenceRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Stress request, candidate, and output are not one evidence inventory.");
            }

            byte[] requestBytes = await File.ReadAllBytesAsync(requestPath, cancellation.Token)
                .ConfigureAwait(false);
            if (!string.Equals(Hash(requestBytes), requestSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Stress capture request checksum mismatch.");
            }

            using JsonDocument requestDocument = JsonDocument.Parse(requestBytes);
            JsonElement request = requestDocument.RootElement;
            RequireExactProperties(request,
                "schema", "scope", "split", "synthetic_only", "private_data", "sealed_data",
                "model_inference", "production_approved", "truth_included", "candidate", "sources");
            if (Text(request, "schema") != RequestSchema || Text(request, "scope") != Scope ||
                Text(request, "split") != "train" ||
                !request.GetProperty("synthetic_only").GetBoolean() ||
                request.GetProperty("private_data").GetBoolean() ||
                request.GetProperty("sealed_data").GetBoolean() ||
                !request.GetProperty("model_inference").GetBoolean() ||
                request.GetProperty("production_approved").GetBoolean() ||
                request.GetProperty("truth_included").GetBoolean())
            {
                throw new InvalidDataException("Stress capture request scope is invalid.");
            }

            JsonElement candidate = request.GetProperty("candidate");
            RequireExactProperties(candidate, "path", "sha256");
            if (!string.Equals(InsideArtifacts(root, Text(candidate, "path")), candidatePath,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(RequireSha(Text(candidate, "sha256"), "request candidate"), candidateSha,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Stress request and candidate command disagree.");
            }

            JsonElement[] sources = request.GetProperty("sources").EnumerateArray().ToArray();
            if (sources.Length is < 1 or > MaximumSourceCount)
            {
                throw new InvalidDataException("Stress capture source count is outside its bounded scope.");
            }
            var sourceInputs = new List<(string Path, string Sha256)>(sources.Length);
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordinals = new HashSet<int>();
            foreach (JsonElement source in sources)
            {
                RequireExactProperties(source, "path", "sha256");
                string sourcePath = InsideArtifacts(root, Text(source, "path"));
                string sourceSha = RequireSha(Text(source, "sha256"), "source");
                int ordinal = ValidateGeneratedSourcePath(requestPath, sourcePath, sourceSha);
                if (!identities.Add(sourceSha) || !paths.Add(sourcePath) || !ordinals.Add(ordinal))
                {
                    throw new InvalidDataException("Stress capture repeats a source path, identity, or ordinal.");
                }
                sourceInputs.Add((sourcePath, sourceSha));
            }
            if (!ordinals.SetEquals(Enumerable.Range(0, sources.Length)))
            {
                throw new InvalidDataException("Stress capture source ordinals are not contiguous.");
            }

            var timer = Stopwatch.StartNew();
            object[] captured = await OriginalDbOcrMemoryRuntime.RunAsync(
                root,
                Path.GetRelativePath(root, candidatePath),
                candidateSha,
                async (ocr, raw, axis, runtimeCancellation) =>
                {
                    var output = new List<object>(sourceInputs.Count);
                    foreach ((string sourcePath, string sourceSha) in sourceInputs)
                    {
                        runtimeCancellation.ThrowIfCancellationRequested();
                        byte[] bytes = await File.ReadAllBytesAsync(sourcePath, runtimeCancellation)
                            .ConfigureAwait(false);
                        if (!string.Equals(Hash(bytes), sourceSha, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("Stress source checksum mismatch.");
                        }
                        OriginalDbOcrSourcePredictions evaluated =
                            await OriginalDbOcrInMemorySourceEvaluator.EvaluateAsync(
                                bytes, sourceSha, ocr, raw, axis, runtimeCancellation).ConfigureAwait(false);
                        output.Add(new
                        {
                            SourceSha256 = sourceSha,
                            evaluated.PanelCount,
                            RawBoxesLtrb = evaluated.Predictions.Select(static prediction => new[]
                            {
                                prediction.Box.Left,
                                prediction.Box.Top,
                                prediction.Box.Right,
                                prediction.Box.Bottom,
                            }).ToArray(),
                        });
                    }
                    return output.ToArray();
                },
                cancellation.Token).ConfigureAwait(false);
            if (captured.Length != sourceInputs.Count)
            {
                throw new InvalidDataException("Stress capture source inventory changed during evaluation.");
            }

            var report = new
            {
                Schema = "graphreader.word-fragment-stress-v1.raw-capture.v1",
                Status = "completed",
                Scope,
                Split = "train",
                ModelInference = true,
                ProductionApproved = false,
                InputScope = new
                {
                    Source = "authenticated_capture_request",
                    SyntheticOnlyDeclared = true,
                    PrivateDataDeclared = false,
                    SealedDataDeclared = false,
                    IndependentlyVerified = false,
                },
                TruthReceivedByRuntime = false,
                TruthBasedCrops = false,
                RecognitionTextSerialized = false,
                Request = new { Path = Path.GetRelativePath(root, requestPath).Replace('\\', '/'), Sha256 = requestSha },
                RequestSha256 = requestSha,
                Candidate = new { Path = Path.GetRelativePath(root, candidatePath).Replace('\\', '/'), Sha256 = candidateSha },
                CandidateSha256 = candidateSha,
                SourceCount = captured.Length,
                Sources = captured,
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                OptimizerSteps = 0,
                NewModelRevision = false,
            };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(report, Options);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
                ?? throw new InvalidDataException("Stress output has no parent directory."));
            await using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(payload, cancellation.Token).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellation.Token).ConfigureAwait(false);
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Status = "completed",
                Output = Path.GetRelativePath(root, outputPath).Replace('\\', '/'),
                Sha256 = Hash(await File.ReadAllBytesAsync(outputPath, cancellation.Token).ConfigureAwait(false)),
                SourceCount = captured.Length,
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            }, Options));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("WORD_FRAGMENT_STRESS_CAPTURE_CANCELLED");
            return 130;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                Status = "void",
                Error = error.Message,
                ProductionApproved = false,
            }, Options));
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    internal static object RunBoundarySelfTest(string repositoryRoot)
    {
        static bool Rejects(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (InvalidDataException)
            {
                return true;
            }
        }

        using JsonDocument requestExtra = JsonDocument.Parse(
            "{\"schema\":\"x\",\"scope\":\"x\",\"split\":\"train\",\"synthetic_only\":true," +
            "\"private_data\":false,\"sealed_data\":false,\"model_inference\":true," +
            "\"production_approved\":false,\"truth_included\":false,\"candidate\":{}," +
            "\"sources\":[],\"truth\":{}}");
        using JsonDocument candidateExtra = JsonDocument.Parse(
            "{\"path\":\"x\",\"sha256\":\"x\",\"truth\":{}}");
        using JsonDocument sourceExtra = JsonDocument.Parse(
            "{\"path\":\"x\",\"sha256\":\"x\",\"role\":\"participant\"}");
        bool requestRejected = Rejects(() => RequireExactProperties(requestExtra.RootElement,
            "schema", "scope", "split", "synthetic_only", "private_data", "sealed_data",
            "model_inference", "production_approved", "truth_included", "candidate", "sources"));
        bool candidateRejected = Rejects(() =>
            RequireExactProperties(candidateExtra.RootElement, "path", "sha256"));
        bool sourceRejected = Rejects(() =>
            RequireExactProperties(sourceExtra.RootElement, "path", "sha256"));
        string root = Path.GetFullPath(repositoryRoot);
        string requestPath = Path.Combine(root, "artifacts", "stress-fixture", "capture-request.json");
        string sourceSha = new('a', 64);
        string redirected = Path.Combine(root, "artifacts", "redirected", $"stress-00-{sourceSha[..12]}.png");
        bool redirectedRejected = Rejects(() =>
            ValidateGeneratedSourcePath(requestPath, redirected, sourceSha));
        if (!requestRejected || !candidateRejected || !sourceRejected || !redirectedRejected)
        {
            throw new InvalidDataException("WORD_FRAGMENT_STRESS_BOUNDARY_SELF_TEST_FAILED");
        }
        return new
        {
            Status = "passed",
            ExtraRequestPropertyRejected = requestRejected,
            ExtraCandidatePropertyRejected = candidateRejected,
            ExtraSourcePropertyRejected = sourceRejected,
            RedirectedSourceRejected = redirectedRejected,
            ModelInferenceRuns = 0,
        };
    }

    private static void RequireExactProperties(JsonElement row, params string[] names)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Stress capture record is not an object.");
        }
        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        if (expected.Count != names.Length)
        {
            throw new InvalidOperationException("Stress capture expected-property list contains duplicates.");
        }
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in row.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !observed.Add(property.Name))
            {
                throw new InvalidDataException($"Stress capture record has unexpected or duplicate '{property.Name}'.");
            }
        }
        if (!observed.SetEquals(expected))
        {
            throw new InvalidDataException("Stress capture record lacks a required property.");
        }
    }

    private static int ValidateGeneratedSourcePath(string requestPath, string sourcePath, string sourceSha)
    {
        string requestParent = Path.GetDirectoryName(requestPath)
            ?? throw new InvalidDataException("Stress request has no parent directory.");
        string expectedParent = Path.Combine(requestParent, "sources");
        if (!string.Equals(Path.GetDirectoryName(sourcePath), expectedParent,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Stress source is outside its generated inventory.");
        }
        string name = Path.GetFileName(sourcePath);
        if (name.Length != 26 || !name.StartsWith("stress-", StringComparison.Ordinal) ||
            name[9] != '-' ||
            !int.TryParse(name.AsSpan(7, 2), NumberStyles.None, CultureInfo.InvariantCulture,
                out int ordinal) ||
            !string.Equals(name, $"stress-{ordinal:00}-{sourceSha[..12]}.png", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Stress source name is not bound to its ordinal and checksum.");
        }
        return ordinal;
    }

    private static string Text(JsonElement row, string name) =>
        row.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"Stress capture field '{name}' is missing.");

    private static string InsideArtifacts(string root, string value)
    {
        string full = Path.GetFullPath(Path.Combine(root, value));
        string artifacts = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, "artifacts")))
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Stress capture path escaped repository artifacts.");
        }
        return full;
    }

    private static string RequireSha(string value, string label)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"Stress capture {label} SHA-256 is invalid.");
        }
        return value.ToLowerInvariant();
    }

    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
