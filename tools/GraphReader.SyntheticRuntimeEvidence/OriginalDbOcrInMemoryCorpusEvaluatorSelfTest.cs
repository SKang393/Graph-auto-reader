// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Domain;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class OriginalDbOcrInMemoryCorpusEvaluatorSelfTest
{
    internal static async Task<object> RunAsync()
    {
        string[] roles = ["x_tick", "y_tick", "axis_title", "phase_heading",
            "legend_text", "participant", "annotation", "condition_label"];
        OcrRole[] runtimeRoles = [OcrRole.XTick, OcrRole.YTick, OcrRole.AxisTitle,
            OcrRole.PhaseHeading, OcrRole.LegendText, OcrRole.Participant,
            OcrRole.Annotation, OcrRole.PhaseHeading];
        OriginalDbOcrSealedSourcePayload[] sources = [Source(0, roles), Source(1, roles)];
        int calls = 0;
        Task<OriginalDbOcrSourcePredictions> Infer(byte[] image, string hash, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int ordinal = image[0];
            Require(ordinal == calls && image.Length == 1 &&
                hash == Convert.ToHexStringLower(SHA256.HashData(image)), "image-only inference inputs");
            calls++;
            var predictions = new List<OriginalDbOcrAggregatePrediction>();
            for (int local = 0; local < 4; local++)
            {
                int index = ordinal * 4 + local;
                if (index == 3) continue;
                predictions.Add(new(Box(local), "secret😀", runtimeRoles[index]));
            }
            if (ordinal == 1)
                predictions.Add(new(new(80, 20, 90, 25), "noise", OcrRole.Other));
            return Task.FromResult(new OriginalDbOcrSourcePredictions(2, predictions));
        }

        OriginalDbOcrCorpusAggregate aggregate = await OriginalDbOcrInMemoryCorpusEvaluator
            .EvaluateCoreAsync(sources, Infer, CancellationToken.None).ConfigureAwait(false);
        Require(calls == 2 && aggregate.SourceCount == 2 && aggregate.PanelCount == 4,
            "all sources and panels accounted");
        Require(aggregate.Metrics.RawDetectorGeometry.TruePositives == 7 &&
            aggregate.Metrics.RawDetectorGeometry.FalsePositives == 1 &&
            aggregate.Metrics.RawDetectorGeometry.FalseNegatives == 1 &&
            aggregate.Metrics.FullOcrMetrics.TruthRegionCount == 8 &&
            aggregate.Metrics.FullOcrMetrics.TruthCharacterCount == 56 &&
            aggregate.Metrics.FullOcrMetrics.CharacterErrorCount == 12 &&
            aggregate.Metrics.FullOcrMetrics.RoleCorrectCount == 7 &&
            aggregate.Metrics.FullOcrMetrics.RecognitionExactCount == 7,
            "cross-source complete denominators");
        string serialized = JsonSerializer.Serialize(aggregate);
        Require(!serialized.Contains("secret", StringComparison.Ordinal) &&
            !serialized.Contains("source-id", StringComparison.Ordinal) &&
            !serialized.Contains(sources[0].ImageSha256, StringComparison.Ordinal),
            "aggregate omits case data");

        await ExpectFailureAsync(() => OriginalDbOcrInMemoryCorpusEvaluator.EvaluateCoreAsync(
            sources, (_, _, _) => throw new IOException("secret-case-path"), CancellationToken.None))
            .ConfigureAwait(false);
        calls = 0;
        await ExpectFailureAsync(() => OriginalDbOcrInMemoryCorpusEvaluator.EvaluateCoreAsync(
            [sources[0]], Infer, CancellationToken.None)).ConfigureAwait(false);

        using var cancellation = new CancellationTokenSource();
        int cancelledCalls = 0;
        try
        {
            await OriginalDbOcrInMemoryCorpusEvaluator.EvaluateCoreAsync(sources, (_, _, _) =>
            {
                cancelledCalls++;
                cancellation.Cancel();
                return Task.FromResult(new OriginalDbOcrSourcePredictions(1, []));
            }, cancellation.Token).ConfigureAwait(false);
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException)
        {
            Require(cancelledCalls == 1, "cancellation stops later sources");
        }
        await CheckLimitsAsync(roles, sources).ConfigureAwait(false);
        using var duringScoring = new CancellationTokenSource();
        try
        {
            await OriginalDbOcrInMemoryCorpusEvaluator.EvaluateCoreAsync(sources, (_, _, _) =>
                Task.FromResult(new OriginalDbOcrSourcePredictions(1,
                    new CancellingPredictions(duringScoring))), duringScoring.Token).ConfigureAwait(false);
            throw new InvalidOperationException("Expected scoring cancellation.");
        }
        catch (OperationCanceledException) { }

        return new { Status = "passed", CheckCount = 12, ModelInference = false,
            FileIo = false, PrivateData = false, SealedData = false, CaseOutput = false,
            ProductionApproved = false };
    }

    private static async Task CheckLimitsAsync(string[] roles, OriginalDbOcrSealedSourcePayload[] sources)
    {
        int calls = 0;
        Task<OriginalDbOcrSourcePredictions> Empty(byte[] image, string hash, CancellationToken token)
        {
            calls++;
            return Task.FromResult(new OriginalDbOcrSourcePredictions(1, []));
        }
        await ExpectFailureAsync(() => OriginalDbOcrInMemoryCorpusEvaluator.EvaluateCoreAsync(
            Enumerable.Repeat(sources[0], 129).ToArray(), Empty, CancellationToken.None)).ConfigureAwait(false);
        Require(calls == 0, "source limit precedes inference");
        await ExpectFailureAsync(() => OriginalDbOcrInMemoryCorpusEvaluator.EvaluateCoreAsync(
            [Source(0, roles, 1025)], Empty, CancellationToken.None)).ConfigureAwait(false);
        Require(calls == 0, "truth limit precedes inference");

        OriginalDbOcrAggregatePrediction prediction = new(new(80, 20, 90, 25), null, null);
        await ExpectFailureAsync(() => OriginalDbOcrInMemoryCorpusEvaluator.EvaluateCoreAsync(
            sources, (_, _, _) => Task.FromResult(new OriginalDbOcrSourcePredictions(1,
                Enumerable.Repeat(prediction, 1025).ToArray())), CancellationToken.None)).ConfigureAwait(false);
        await ExpectFailureAsync(() => OriginalDbOcrInMemoryCorpusEvaluator.EvaluateCoreAsync(
            Enumerable.Repeat(Source(0, roles, 1024), 17).ToArray(), Empty, CancellationToken.None)).ConfigureAwait(false);
        Require(calls == 16, "corpus truth budget aborts before excess source inference");
        await ExpectFailureAsync(() => OriginalDbOcrInMemoryCorpusEvaluator.EvaluateCoreAsync(
            Enumerable.Repeat(sources[0], 17).ToArray(), (_, _, _) =>
                Task.FromResult(new OriginalDbOcrSourcePredictions(1,
                    Enumerable.Repeat(prediction, 1024).ToArray())), CancellationToken.None)).ConfigureAwait(false);
    }

    private sealed class CancellingPredictions(CancellationTokenSource cancellation)
        : IReadOnlyList<OriginalDbOcrAggregatePrediction>
    {
        public int Count => 1;
        public OriginalDbOcrAggregatePrediction this[int index]
        {
            get
            {
                cancellation.Cancel();
                return new(Box(0), null, null);
            }
        }
        public IEnumerator<OriginalDbOcrAggregatePrediction> GetEnumerator() =>
            Enumerable.Range(0, Count).Select(index => this[index]).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static OriginalDbOcrSealedSourcePayload Source(int ordinal, string[] roles, int truthCount = 4)
    {
        byte[] annotation = JsonSerializer.SerializeToUtf8Bytes(new
        {
            coordinate_space = "original_pixels", canvas = new { width = 100, height = 50 },
            texts = Enumerable.Range(0, truthCount).Select(local => new
            {
                text_id = "text-" + local, region_id = "region-" + local,
                text = "secret😀", role = roles[(ordinal * 4 + local) % roles.Length],
                rendered_pixel_box = new[] { 1 + local % 4 * 20, 2, 10, 5 },
            }).ToArray(),
        });
        byte[] image = [(byte)ordinal];
        return new(ordinal, "source-id-" + ordinal, ordinal,
            Convert.ToHexStringLower(SHA256.HashData(image)),
            Convert.ToHexStringLower(SHA256.HashData(annotation)), image, annotation, 100, 50);
    }

    private static OriginalDbOcrAggregateBox Box(int local) => new(1 + local * 20, 2, 11 + local * 20, 7);

    private static async Task ExpectFailureAsync(Func<Task<OriginalDbOcrCorpusAggregate>> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            throw new InvalidOperationException("Expected corpus failure.");
        }
        catch (InvalidDataException error)
        {
            Require(error.Message == "OCR_CORPUS_EVALUATION_FAILED" && error.InnerException is null,
                "failure is sanitized and returns no partial metric");
        }
    }

    private static void Require(bool condition, string code)
    {
        if (!condition) throw new InvalidOperationException("OCR corpus self-test failed: " + code);
    }
}
