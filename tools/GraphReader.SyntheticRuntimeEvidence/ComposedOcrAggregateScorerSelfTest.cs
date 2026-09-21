// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Domain;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class ComposedOcrAggregateScorerSelfTest
{
    internal static object Run()
    {
        int checks = 0;
        var merged = new ComposedOcrAggregateScorer();
        merged.AddSource([Truth(0, 20, "label")], [Raw(0, 4), Raw(8, 12), Raw(16, 20)],
            [Text(0, 20, "label")], 0);
        ComposedOcrAggregateResult result = merged.Score();
        Require(result.RawDetectorGeometry.TruePositives == 0 && result.RawDetectorGeometry.FalsePositives == 3
            && result.AssembledGeometry.TruePositives == 1 && result.FullOcrMetrics.RecognitionExactCount == 1
            && result.RecognitionFailedRegionCount == 0, "merged fragments remain distinct from final OCR");
        checks++;

        var recovered = new ComposedOcrAggregateScorer();
        recovered.AddSource([Truth(0, 10, "a"), Truth(20, 30, "b")], [Raw(0, 10)],
            [Text(0, 10, "a"), Text(20, 30, "b")], 0);
        result = recovered.Score();
        Require(result.RawDetectorGeometry.Recall == .5 && result.AssembledGeometry.Recall == 1
            && result.RecognitionFailedRegionCount == 0, "recovered region cannot produce a negative failure count");
        checks++;

        var missing = new ComposedOcrAggregateScorer();
        missing.AddSource([Truth(0, 10, "tick")], [Raw(0, 10)], [], 1);
        result = missing.Score();
        Require(result.RawDetectorGeometry.Recall == 1 && result.AssembledGeometry.Recall == 0
            && result.FullOcrMetrics.CharacterErrorCount == 4 && result.RecognitionFailedRegionCount == 1,
            "failed recognition retains all truth and explicit failures");
        checks++;

        recovered.AddSource([Truth(0, 10, "other source")], [], [], 0);
        result = recovered.Score();
        Require(result.SourceCount == 2 && result.FullOcrMetrics.TruthRegionCount == 3
            && result.FullOcrMetrics.GeometryMatchedRegionCount == 2,
            "sources cannot match each other's predictions");
        checks++;

        foreach (int kind in Enumerable.Range(0, 4))
        {
            var invalid = new ComposedOcrAggregateScorer();
            invalid.AddSource([Truth(0, 10, "good")], [Raw(0, 10)], [Text(0, 10, "good")], 0);
            ExpectInvalid(() => invalid.AddSource([Truth(0, 10, "bad")],
                kind == 0 ? [Text(0, 10, "not raw")] : [Raw(0, 10)],
                kind == 1 ? [Raw(0, 10)] : kind == 2 ? [Text(10, 0, "reversed")] : [Text(0, 10, "bad")],
                kind == 3 ? -1 : 0));
            ExpectInvalid(() => invalid.Score());
            checks++;
        }

        string serialized = JsonSerializer.Serialize(result);
        Require(!serialized.Contains("other source", StringComparison.Ordinal)
            && !serialized.Contains("Box", StringComparison.Ordinal), "aggregate output contains no text or boxes");
        checks++;
        checks += CheckReplayBoundary();
        return new { status = "pass", checks, private_reads = 0, sealed_reads = 0, model_inference_runs = 0 };
    }

    private static int CheckReplayBoundary()
    {
        string directory = Path.Combine(Path.GetTempPath(), "graphreader-composed-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "input.json");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        object Source(string split = "validation") => new
        {
            source_sha256 = new string('a', 64), split,
            truths = new[] { Truth(0, 10, "fixture") },
            raw_detector_regions = new[] { Raw(0, 10) },
            assembled_regions = new[] { Text(0, 10, "fixture") },
            recognition_failed_region_count = 0,
        };
        string Write(int privateReads = 0, int sealedReads = 0, int truthCount = 1, bool duplicate = false, string split = "validation")
        {
            object[] sources = duplicate ? [Source(split), Source(split)] : [Source(split)];
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "graphreader.composed-ocr-development-input.v1", scope = "owned-synthetic-development",
                private_reads = privateReads, sealed_reads = sealedReads,
                source_count = sources.Length, truth_count = truthCount, sources,
            }, options);
            File.WriteAllBytes(path, bytes);
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        try
        {
            string hash = Write();
            string result = JsonSerializer.Serialize(ComposedOcrDevelopmentReplay.Run(directory, path, hash), options);
            Require(result.Contains("\"recognition_exact_accuracy\":1", StringComparison.Ordinal)
                && !result.Contains("fixture", StringComparison.Ordinal), "native replay returns only measured aggregates");
            ExpectInvalid(() => ComposedOcrDevelopmentReplay.Run(directory, path, new string('f', 64)));
            ExpectInvalid(() => ComposedOcrDevelopmentReplay.Run(directory, path, Write(privateReads: 1)));
            ExpectInvalid(() => ComposedOcrDevelopmentReplay.Run(directory, path, Write(sealedReads: 1)));
            ExpectInvalid(() => ComposedOcrDevelopmentReplay.Run(directory, path, Write(truthCount: 2)));
            ExpectInvalid(() => ComposedOcrDevelopmentReplay.Run(directory, path, Write(duplicate: true, truthCount: 2)));
            ExpectInvalid(() => ComposedOcrDevelopmentReplay.Run(directory, path, Write(split: "sealed")));
            return 7;
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static OriginalDbOcrAggregateTruth Truth(double left, double right, string text) =>
        new(new(left, 0, right, 10), text, OriginalDbOcrGeneratorRole.Annotation);

    private static OriginalDbOcrAggregatePrediction Raw(double left, double right) =>
        new(new(left, 0, right, 10), null, null);

    private static OriginalDbOcrAggregatePrediction Text(double left, double right, string text) =>
        new(new(left, 0, right, 10), text, OcrRole.Annotation);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("COMPOSED_OCR_SELF_TEST_FAILED: " + message);
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("COMPOSED_OCR_SELF_TEST_EXPECTED_REJECTION");
    }
}
