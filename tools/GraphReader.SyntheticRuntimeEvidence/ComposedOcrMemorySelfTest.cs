// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
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
            return Task.FromResult(new ComposedOcrSourcePredictions(2, 0, raw, [new(box, "private-label", OcrRole.PhaseHeading)]));
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
        checks++;
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
            Require(error.Message == "COMPOSED_OCR_CORPUS_EVALUATION_FAILED" && error.InnerException is null,
                "source failures expose no case-level information");
            return;
        }
        throw new InvalidOperationException("Invalid corpus accepted.");
    }
}
