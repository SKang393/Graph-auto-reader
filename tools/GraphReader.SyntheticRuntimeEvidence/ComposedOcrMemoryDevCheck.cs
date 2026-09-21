// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>Local owned synthetic development only. No sealed worker calls this command.</summary>
internal static class ComposedOcrMemoryDevCheck
{
    internal const string Command = "--check-composed-ocr-memory-dev";
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static async Task<int> RunAsync(string[] args, string root)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        string stage = "setup";
        try
        {
            if (args.Length != 5) throw new InvalidDataException();
            byte[] requestBytes = Read(root, args[1], args[2], 4 * 1024 * 1024);
            using JsonDocument request = JsonDocument.Parse(requestBytes);
            JsonElement value = request.RootElement;
            if (value.GetProperty("schema").GetString() != "graphreader.composed-ocr-memory-dev-check.v1" ||
                value.GetProperty("scope").GetString() != "owned-synthetic-development" ||
                value.GetProperty("private_reads").GetInt32() != 0 || value.GetProperty("sealed_reads").GetInt32() != 0)
                throw new InvalidDataException();
            JsonElement generator = value.GetProperty("generator");
            using JsonDocument inventory = JsonDocument.Parse(Read(root,
                generator.GetProperty("path").GetString()!, generator.GetProperty("sha256").GetString()!, 4 * 1024 * 1024));
            JsonElement manifest = inventory.RootElement;
            if (manifest.GetProperty("private_reads").GetInt32() != 0 || manifest.GetProperty("sealed_reads").GetInt32() != 0 ||
                manifest.GetProperty("production_approved").GetBoolean()) throw new InvalidDataException();
            JsonElement[] entries = manifest.GetProperty("sources").EnumerateArray().ToArray();
            if (entries.Length is <= 0 or > OriginalDbOcrInMemoryCorpusEvaluator.MaximumSources ||
                entries.Length != value.GetProperty("source_count").GetInt32() ||
                entries.Length != manifest.GetProperty("source_count").GetInt32()) throw new InvalidDataException();
            var splits = new Dictionary<string, List<OriginalDbOcrSealedSourcePayload>>(StringComparer.Ordinal);
            long encodedBytes = 0;
            for (int index = 0; index < entries.Length; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                JsonElement entry = entries[index];
                string split = NormalizeOwnedSplit(entry.GetProperty("split").GetString()!);
                JsonElement image = entry.GetProperty("image"), annotation = entry.GetProperty("annotation");
                string imageHash = image.GetProperty("sha256").GetString()!;
                string annotationHash = annotation.GetProperty("sha256").GetString()!;
                byte[] pixels = Read(root, image.GetProperty("path").GetString()!, imageHash, 64 * 1024 * 1024);
                byte[] labels = Read(root, annotation.GetProperty("path").GetString()!, annotationHash, 4 * 1024 * 1024);
                encodedBytes = checked(encodedBytes + pixels.Length + labels.Length);
                if (encodedBytes > 256 * 1024 * 1024) throw new InvalidDataException();
                (int width, int height) = ReadImageDimensions(pixels);
                if (!splits.TryGetValue(split, out List<OriginalDbOcrSealedSourcePayload>? sources))
                {
                    sources = [];
                    splits.Add(split, sources);
                }
                sources.Add(new(index, string.Empty, 0, imageHash, annotationHash, pixels, labels,
                    width, height));
            }
            if (splits.Values.SelectMany(static rows => rows).Select(static row => row.ImageSha256)
                    .Distinct(StringComparer.Ordinal).Count() != entries.Length) throw new InvalidDataException();
            stage = "runtime";
            var timer = Stopwatch.StartNew();
            Dictionary<string, ComposedOcrCorpusAggregate> metrics = await OriginalDbOcrMemoryRuntime.RunComposedAsync(
                root, args[3], args[4], async (ocr, observations, axis, token) =>
                {
                    stage = "inference";
                    var result = new Dictionary<string, ComposedOcrCorpusAggregate>(StringComparer.Ordinal);
                    foreach ((string split, List<OriginalDbOcrSealedSourcePayload> sources) in splits)
                    {
                        result.Add(split, await ComposedOcrInMemoryCorpusEvaluator.EvaluateAsync(sources,
                            (image, hash, ct) => ComposedOcrInMemorySourceEvaluator.EvaluateAsync(
                                image, hash, ocr, observations, axis, ct), token).ConfigureAwait(false));
                    }
                    return result;
                }, cancellation.Token).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "graphreader.composed-ocr-memory-dev-result.v1", status = "completed",
                sources = entries.Length, metrics, elapsed_milliseconds = timer.Elapsed.TotalMilliseconds,
                request_sha256 = args[2], candidate_sha256 = args[4], model_inference = true,
                truth_consumed_by_inference = false, case_output = false, private_reads = 0,
                sealed_reads = 0, stage_admission_granted = false, production_approved = false,
            }, Options));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("COMPOSED_OCR_MEMORY_DEV_CANCELLED");
            return 130;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine("COMPOSED_OCR_MEMORY_DEV_FAILED:" + SafeFailureStage(error, stage));
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static string SafeFailureStage(Exception error, string fallback) => error.Message switch
    {
        "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:input-validation" => "input-validation",
        "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:annotation-validation" => "annotation-validation",
        "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:source-inference" => "source-inference",
        "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:aggregate-scoring" => "aggregate-scoring",
        "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:source-import" => "source-import",
        "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:source-axis" => "source-axis",
        "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:source-recognition" => "source-recognition",
        "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:source-coverage" => "source-coverage",
        "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:source-mapping" => "source-mapping",
        _ => fallback,
    };

    internal static string NormalizeOwnedSplit(string split) => split switch
    {
        "train" => "train",
        "dev" or "validation" => "validation",
        _ => throw new InvalidDataException("COMPOSED_OCR_DEVELOPMENT_SPLIT_INVALID"),
    };

    internal static (int Width, int Height) ReadImageDimensions(byte[] encoded)
    {
        using var stream = new MemoryStream(encoded, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("COMPOSED_OCR_SOURCE_FRAME_COUNT_INVALID");
        BitmapFrame frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static byte[] Read(string root, string path, string expected, int maximumBytes)
    {
        string full = Path.GetFullPath(path, root);
        string allowed = Path.Combine(Path.GetFullPath(root), "artifacts", "goal22-runs") + Path.DirectorySeparatorChar;
        if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || new FileInfo(full).Length is <= 0 ||
            new FileInfo(full).Length > maximumBytes) throw new InvalidDataException();
        byte[] bytes = File.ReadAllBytes(full);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected) throw new InvalidDataException();
        return bytes;
    }
}
