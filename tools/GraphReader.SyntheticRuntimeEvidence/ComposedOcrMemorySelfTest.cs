// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.Domain;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class ComposedOcrMemorySelfTest
{
    private static readonly int[] FixtureBox = [1, 2, 10, 5];
    internal static async Task<object> RunAsync()
    {
        int checks = 0;
        using (var stream = new MemoryStream())
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(3, 2, 96, 96,
                PixelFormats.Gray8, null, new byte[6], 3)));
            encoder.Save(stream);
            Require(ComposedOcrMemoryDevCheck.ReadImageDimensions(stream.ToArray()) == (3, 2),
                "expected truth dimensions come from image bytes, not their annotations");
        }
        checks++;
        Require(ComposedOcrMemoryDevCheck.NormalizeOwnedSplit("dev") == "validation" &&
            ComposedOcrMemoryDevCheck.NormalizeOwnedSplit("validation") == "validation" &&
            ComposedOcrMemoryDevCheck.NormalizeOwnedSplit("train") == "train", "owned split aliases");
        checks++;
        foreach (string split in new[] { "sealed", "real-dev", "real-sealed", "test" })
        {
            try
            {
                ComposedOcrMemoryDevCheck.NormalizeOwnedSplit(split);
                throw new InvalidOperationException("Non-development split accepted.");
            }
            catch (InvalidDataException) { }
            checks++;
        }
        OcrDetectionObservation Observation(bool supplied = false) => new("project", "panel", "hash", 100, 50, supplied, []);
        var buffer = new ComposedOcrObservationBuffer();
        buffer.Capture(Observation());
        Require(buffer.Take("project", "panel", "hash", 100, 50).Count == 0, "single-use capture");
        checks++;
        foreach (int kind in Enumerable.Range(0, 8))
        {
            buffer = new();
            try
            {
                if (kind != 0) buffer.Capture(Observation(kind == 1));
                if (kind == 2) buffer.Capture(Observation());
                buffer.Take(kind == 3 ? "wrong" : "project", kind == 4 ? "wrong" : "panel",
                    kind == 5 ? "wrong" : "hash", kind == 6 ? 101 : 100, kind == 7 ? 51 : 50);
                throw new InvalidOperationException("Capture accepted invalid provenance.");
            }
            catch (InvalidDataException) { }
            try
            {
                buffer.Capture(Observation());
                throw new InvalidOperationException("Invalid capture did not remain poisoned.");
            }
            catch (InvalidDataException) { }
            checks++;
        }
        OriginalDbOcrSealedSourcePayload[] sources = [Source(0), Source(1)];
        int calls = 0;
        Task<ComposedOcrSourcePredictions> Infer(byte[] image, string hash, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Require(image.Length == 1 && image[0] == calls && Hash(image) == hash, "inference receives image and hash only");
            var box = new OriginalDbOcrAggregateBox(1, 2, 11, 7);
            IReadOnlyList<OriginalDbOcrAggregatePrediction> raw = calls++ == 0 ? [new(box, null, null)] : [];
            var prediction = new OriginalDbOcrAggregatePrediction(box, "private-label", OcrRole.PhaseHeading);
            return Task.FromResult(new ComposedOcrSourcePredictions(2, 0, raw, [prediction])
            {
                RecognitionEvidence = [new(prediction, [new("private-alternative", 0.8, OcrSourceImage.Original)])],
                Warnings = ["private-warning"],
            });
        }
        ComposedOcrCorpusAggregate aggregate = await ComposedOcrInMemoryCorpusEvaluator.EvaluateAsync(
            sources, Infer, CancellationToken.None).ConfigureAwait(false);
        Require(calls == 2 && aggregate.SourceCount == 2 && aggregate.PanelCount == 4 &&
            aggregate.Metrics.RawDetectorGeometry.TruePositives == 1 &&
            aggregate.Metrics.AssembledGeometry.TruePositives == 2 &&
            aggregate.Metrics.FullOcrMetrics.RecognitionExactCount == 2 && aggregate.Metrics.RecognitionFailedRegionCount == 0,
            "all source truth retained after recovery");
        checks++;
        string serialized = JsonSerializer.Serialize(aggregate);
        Require(!serialized.Contains("private-label", StringComparison.Ordinal) &&
            !serialized.Contains(sources[0].ImageSha256, StringComparison.Ordinal), "aggregate has no case data");
        calls = 0;
        ComposedOcrSourcePredictions example = await Infer(sources[0].ImageBytes, sources[0].ImageSha256, CancellationToken.None);
        Require(!JsonSerializer.Serialize(example).Contains("private-label", StringComparison.Ordinal), "prediction fields excluded from serialization");
        Require(!JsonSerializer.Serialize(example).Contains("private-alternative", StringComparison.Ordinal) &&
            !JsonSerializer.Serialize(example).Contains("private-warning", StringComparison.Ordinal) &&
            !JsonSerializer.Serialize(example.RecognitionEvidence).Contains("private-", StringComparison.Ordinal),
            "recognition alternatives and warnings excluded from shared serialization");
        checks++;
        calls = 0;
        var openCases = new List<ComposedOcrMemoryDevCheck.OpenDiagnosticCase>();
        ComposedOcrCorpusAggregate observed = await ComposedOcrInMemoryCorpusEvaluator.EvaluateAsync(sources,
            async (image, hash, token) =>
            {
                ComposedOcrSourcePredictions prediction = await Infer(image, hash, token).ConfigureAwait(false);
                openCases.Add(ComposedOcrMemoryDevCheck.ProjectOpenCase(hash, prediction));
                return prediction;
            }, CancellationToken.None).ConfigureAwait(false);
        Require(calls == 2 && openCases.Count == 2 && JsonSerializer.Serialize(observed) == serialized,
            "open observation neither reruns inference nor changes aggregate scoring");
        Require(JsonSerializer.Serialize(openCases).Contains("private-label", StringComparison.Ordinal) &&
            openCases[0].AssembledRegions[0].Role == "phaseheading" &&
            openCases[0].SourceSha256 == sources[0].ImageSha256 && openCases[1].RawDetectorRegions.Count == 0,
            "explicit projection preserves predictions and their source identity");
        Require(openCases[0].RecognitionEvidence[0].FinalPrediction == openCases[0].AssembledRegions[0] &&
            openCases[0].RecognitionEvidence[0].Alternatives[0] is { Text: "private-alternative", Confidence: 0.8, SourceImage: "Original" } &&
            openCases[0].Warnings.Single() == "private-warning" &&
            JsonSerializer.Serialize(openCases).Contains("private-alternative", StringComparison.Ordinal),
            "only explicit open projection includes unchanged recognition alternatives and warnings");
        checks++;
        checks += OpenDiagnosticScopeChecks();
        foreach (OriginalDbOcrSealedSourcePayload[] invalid in new[]
        {
            Array.Empty<OriginalDbOcrSealedSourcePayload>(),
            Enumerable.Repeat(sources[0], 129).ToArray(),
            new[] { sources[0], sources[0] },
            new[] { sources[0], sources[1] with { ImageSha256 = new string('f', 64) } },
            new[] { sources[0], sources[1] with { AnnotationSha256 = new string('f', 64) } },
            new[] { sources[0], sources[1] with { Width = 99 } },
        })
        {
            calls = 0;
            await ExpectFailure(() => ComposedOcrInMemoryCorpusEvaluator.EvaluateAsync(invalid, Infer, CancellationToken.None));
            Require(calls == 0, "input validation precedes every inference call");
            checks++;
        }
        await ExpectFailure(() => ComposedOcrInMemoryCorpusEvaluator.EvaluateAsync(sources,
            (_, _, _) => throw new IOException("secret-path-and-label"), CancellationToken.None));
        checks++;
        foreach (string stage in new[] { "import", "axis", "recognition", "coverage", "mapping" })
        {
            var error = new InvalidDataException("COMPOSED_OCR_SOURCE_EVALUATION_FAILED:" + stage);
            string safeStage = ComposedOcrInMemoryCorpusEvaluator.SafeFailureStage(error, "source-inference");
            Require(safeStage == "source-" + stage &&
                ComposedOcrMemoryDevCheck.SafeFailureStage(new InvalidDataException(
                    "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:" + safeStage), "inference") == safeStage,
                "known failure stages survive without case details");
            Require(ComposedOcrInMemoryCorpusEvaluator.SafeFailureStage(new InvalidDataException(
                error.Message + ":secret-label"), "source-inference") == "source-inference" &&
                ComposedOcrMemoryDevCheck.SafeFailureStage(new InvalidDataException(
                    "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:" + safeStage + ":secret-label"), "inference") == "inference",
                "message suffixes cannot disclose private details");
            checks++;
        }
        using var cancellation = new CancellationTokenSource();
        calls = 0;
        try
        {
            await ComposedOcrInMemoryCorpusEvaluator.EvaluateAsync(sources, (_, _, _) =>
            {
                calls++;
                cancellation.Cancel();
                return Task.FromResult(new ComposedOcrSourcePredictions(1, 0, [], []));
            }, cancellation.Token);
            throw new InvalidOperationException("Cancellation ignored.");
        }
        catch (OperationCanceledException) { Require(calls == 1, "cancel before next source"); }
        checks++;
        return new { status = "pass", checks, model_inference_runs = 0, private_reads = 0, sealed_reads = 0 };
    }

    private static int OpenDiagnosticScopeChecks()
    {
        const string requestText = """
            {"schema":"graphreader.composed-ocr-open-diagnostic.v1","scope":"owned-synthetic-development",
             "open_development_fixture":true,"registered_reserve":false,"private_reads":0,"sealed_reads":0}
            """;
        const string manifestText = """
            {"schema":"graphreader.owned-open-ocr-coverage-fixture.v1","scope":"owned-synthetic-development",
             "production_approved":false,"private_reads":0,"sealed_reads":0,"sources":[{"split":"dev"}]}
            """;
        void Validate(JsonNode request, JsonNode manifest)
        {
            using JsonDocument requestDocument = JsonDocument.Parse(request.ToJsonString());
            using JsonDocument manifestDocument = JsonDocument.Parse(manifest.ToJsonString());
            ComposedOcrMemoryDevCheck.ValidateOpenDiagnosticScope(requestDocument.RootElement, manifestDocument.RootElement);
        }
        int checks = 0;
        foreach (string schema in new[] { "graphreader.owned-open-ocr-coverage-fixture.v1", "graphreader.owned-open-ocr-layout-fixture.v1" })
        {
            JsonNode manifest = JsonNode.Parse(manifestText)!;
            manifest["schema"] = schema;
            Validate(JsonNode.Parse(requestText)!, manifest);
            checks++;
        }
        Action<JsonNode, JsonNode>[] invalidScopes =
        [
            (r, _) => r["schema"] = "graphreader.composed-ocr-memory-dev-check.v1",
            (r, _) => r["scope"] = "private-acceptance",
            (r, _) => r.AsObject().Remove("open_development_fixture"),
            (r, _) => r["open_development_fixture"] = false,
            (r, _) => r.AsObject().Remove("registered_reserve"),
            (r, _) => r["registered_reserve"] = true,
            (r, _) => r["private_reads"] = 1,
            (r, _) => r["sealed_reads"] = 1,
            (_, m) => m["schema"] = "graphreader.sealed-reserve.v1",
            (_, m) => m["scope"] = "sealed",
            (_, m) => m["private_reads"] = 1,
            (_, m) => m["sealed_reads"] = 1,
            (_, m) => m["production_approved"] = true,
            (_, m) => m["registered_reserve"] = true,
            (_, m) => m["sources"]!.AsArray().Add(new JsonObject { ["split"] = "train" }),
            (_, m) => m["sources"]![0]!["split"] = "sealed",
            (_, m) => m["sources"]![0]!["split"] = "real-dev",
        ];
        foreach (Action<JsonNode, JsonNode> mutate in invalidScopes)
        {
            JsonNode request = JsonNode.Parse(requestText)!, manifest = JsonNode.Parse(manifestText)!;
            mutate(request, manifest);
            bool rejected = false;
            try { Validate(request, manifest); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException) { rejected = true; }
            Require(rejected, "open diagnostics reject non-open or implicit scope before source access");
            checks++;
        }
        return checks;
    }

    private static OriginalDbOcrSealedSourcePayload Source(int ordinal)
    {
        byte[] annotation = JsonSerializer.SerializeToUtf8Bytes(new
        {
            coordinate_space = "original_pixels", canvas = new { width = 100, height = 50 },
            texts = new[] { new { text_id = "text", region_id = "region", text = "private-label",
                role = "condition_label", rendered_pixel_box = FixtureBox } },
        });
        byte[] image = [(byte)ordinal];
        return new(ordinal, "synthetic-source", ordinal, Hash(image), Hash(annotation), image, annotation, 100, 50);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
    private static async Task ExpectFailure(Func<Task<ComposedOcrCorpusAggregate>> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (InvalidDataException error)
        {
            Require(error.Message is "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:input-validation" or
                    "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:annotation-validation" or
                    "COMPOSED_OCR_CORPUS_EVALUATION_FAILED:source-inference" && error.InnerException is null,
                "source failures expose no case-level information");
            return;
        }
        throw new InvalidOperationException("Invalid corpus accepted.");
    }
}
