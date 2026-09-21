// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>
/// Replays separately authenticated synthetic observations to check the native
/// aggregate metric. This command grants no admission and opens no model/image.
/// </summary>
internal static class ComposedOcrDevelopmentReplay
{
    internal const string Command = "--score-composed-ocr-development";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };
    private sealed record Source(string SourceSha256, string Split,
        OriginalDbOcrAggregateTruth[] Truths,
        OriginalDbOcrAggregatePrediction[] RawDetectorRegions,
        OriginalDbOcrAggregatePrediction[] AssembledRegions,
        int RecognitionFailedRegionCount);
    private sealed record Request(string Schema, string Scope, int PrivateReads,
        int SealedReads, int SourceCount, int TruthCount, Source[] Sources);

    internal static object Run(string root, string inputPath, string expectedSha256)
    {
        string path = Path.GetFullPath(inputPath, root);
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || new FileInfo(path).Length is <= 0 or > 16 * 1024 * 1024)
            throw new InvalidDataException("COMPOSED_OCR_REPLAY_INPUT_INVALID");
        byte[] bytes = File.ReadAllBytes(path);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(hash, expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException("COMPOSED_OCR_REPLAY_CHECKSUM_MISMATCH");
        Request request = JsonSerializer.Deserialize<Request>(bytes, JsonOptions)
            ?? throw new InvalidDataException("COMPOSED_OCR_REPLAY_INPUT_INVALID");
        if (request.Schema != "graphreader.composed-ocr-development-input.v1"
            || request.Scope != "owned-synthetic-development" || request.PrivateReads != 0 || request.SealedReads != 0
            || request.Sources is null || request.SourceCount != request.Sources.Length || request.SourceCount is <= 0 or > 128
            || request.TruthCount <= 0 || request.TruthCount != request.Sources.Sum(static source => source.Truths.Length))
            throw new InvalidDataException("COMPOSED_OCR_REPLAY_SCOPE_INVALID");
        var scorers = new Dictionary<string, ComposedOcrAggregateScorer>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (Source source in request.Sources)
        {
            if (source.SourceSha256 is null || source.SourceSha256.Length != 64
                || source.SourceSha256.Any(static character => !char.IsAsciiHexDigitLower(character) && !char.IsAsciiDigit(character))
                || !identities.Add(source.SourceSha256) || source.Split is not ("train" or "validation"))
                throw new InvalidDataException("COMPOSED_OCR_REPLAY_SOURCE_INVALID");
            if (!scorers.TryGetValue(source.Split, out ComposedOcrAggregateScorer? scorer))
            {
                scorer = new();
                scorers.Add(source.Split, scorer);
            }
            scorer.AddSource(source.Truths, source.RawDetectorRegions, source.AssembledRegions, source.RecognitionFailedRegionCount);
        }
        return new
        {
            schema = "graphreader.composed-ocr-development-replay.v1",
            input_sha256 = hash,
            metrics = scorers.ToDictionary(static pair => pair.Key, static pair => pair.Value.Score(), StringComparer.Ordinal),
            aggregate_only = true,
            private_reads = 0,
            sealed_reads = 0,
            model_inference_runs = 0,
            stage_admission_granted = false,
            production_approved = false,
        };
    }
}
