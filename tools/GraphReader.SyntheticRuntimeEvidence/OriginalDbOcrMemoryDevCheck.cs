// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class OriginalDbOcrMemoryDevCheck
{
    internal const string Command = "--check-original-db-memory-dev";
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static async Task<int> RunAsync(string[] args, string root)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (args.Length != 5) throw new InvalidDataException();
            string path = Path.GetFullPath(Path.Combine(root, args[1]));
            if (!path.StartsWith(Path.Combine(root, "artifacts") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            byte[] requestBytes = await File.ReadAllBytesAsync(path, cancellation.Token).ConfigureAwait(false);
            if (Convert.ToHexStringLower(SHA256.HashData(requestBytes)) != args[2]) throw new InvalidDataException();
            using JsonDocument request = JsonDocument.Parse(requestBytes);
            JsonElement document = request.RootElement;
            if (document.GetProperty("schema").GetString() != "graphreader.original-db-memory-dev-check.v1" ||
                document.GetProperty("scope").GetString() != "project-owned-synthetic-dev-no-truth" ||
                document.GetProperty("split").GetString() != "validation") throw new InvalidDataException();
            JsonElement[] sources = document.GetProperty("sources").EnumerateArray().ToArray();
            if (sources.Length == 0) throw new InvalidDataException();
            var timer = Stopwatch.StartNew();
            var result = await OriginalDbOcrMemoryRuntime.RunAsync(root, args[3], args[4],
                async (ocr, raw, axis, cancellation) =>
                {
                    int panels = 0, regions = 0, recognized = 0;
                    foreach (JsonElement source in sources)
                    {
                        string sourcePath = Path.GetFullPath(Path.Combine(root, source.GetProperty("path").GetString()!));
                        if (!sourcePath.StartsWith(Path.Combine(root, "artifacts") + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                        byte[] bytes = await File.ReadAllBytesAsync(sourcePath, cancellation).ConfigureAwait(false);
                        var output = await OriginalDbOcrInMemorySourceEvaluator.EvaluateAsync(bytes,
                            source.GetProperty("sha256").GetString()!, ocr, raw, axis, cancellation).ConfigureAwait(false);
                        panels = checked(panels + output.PanelCount);
                        regions = checked(regions + output.Predictions.Count);
                        recognized = checked(recognized + output.Predictions.Count(item => item.Text is not null));
                    }
                    return new { panels, regions, recognized };
                }, cancellation.Token).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "completed", sources = sources.Length,
                result.panels, result.regions, result.recognized, elapsed_ms = timer.Elapsed.TotalMilliseconds,
                candidate_sha256 = args[4], request_sha256 = args[2], model_inference = true,
                declared_input_scope = document.GetProperty("scope").GetString(),
                truth_input_supported = false, production_approval = false,
                scope = "dev_execution_only_not_accuracy", case_output = false }, Options));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("ORIGINAL_DB_MEMORY_DEV_CHECK_CANCELLED");
            return 130;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            string code = error.Message switch
            {
                "SEALED_OCR_SOURCE_EVALUATION_FAILED:import" => "import",
                "SEALED_OCR_SOURCE_EVALUATION_FAILED:axis" => "axis",
                "SEALED_OCR_SOURCE_EVALUATION_FAILED:raw_detection" => "raw_detection",
                "SEALED_OCR_SOURCE_EVALUATION_FAILED:recognition" => "recognition",
                "SEALED_OCR_SOURCE_EVALUATION_FAILED:coverage" => "coverage",
                "SEALED_OCR_SOURCE_EVALUATION_FAILED:mapping" => "mapping",
                _ => "setup",
            };
            Console.Error.WriteLine("ORIGINAL_DB_MEMORY_DEV_CHECK_FAILED:" + code);
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
