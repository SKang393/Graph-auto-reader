// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Inference;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Markers.Tests.Classification;

[TestClass]
/// <summary>
/// Verifies classifier orchestration and postprocessing with scripted outputs.
/// Trained-model visual accuracy is measured only by the separately sealed benchmark.
/// </summary>
public sealed class MarkerClassificationFakeInferenceTests
{
    [TestMethod]
    public async Task ContentBoundsAreImmutableAndChangeCacheIdentityEvenForWhiteImages()
    {
        var marker = ClassificationTestSupport.Marker("symbol", 16, 16, 3);
        var bounds = new Dictionary<string, MarkerRectangle> { ["symbol"] = new(14, 14, 5, 5) };
        var first = ClassificationTestSupport.Request(markers: [marker], options: ClassificationTestSupport.Options() with
        { OriginalPixelContentBounds = bounds });
        bounds["symbol"] = new(13, 13, 7, 7);
        Assert.AreEqual(new MarkerRectangle(14, 14, 5, 5), first.Options.OriginalPixelContentBounds!["symbol"]);
        var second = ClassificationTestSupport.Request(markers: [marker], options: ClassificationTestSupport.Options() with
        { OriginalPixelContentBounds = bounds });
        var response = ClassificationInferenceResponses.Success([(MarkerShape.Circle, MarkerFill.Filled)]);
        var runner = new ClassificationInferenceRunnerStub(response, response);
        var service = new MarkerClassificationService(runner);
        Assert.IsTrue((await service.ClassifyAsync(first, CancellationToken.None)).Succeeded);
        Assert.IsTrue((await service.ClassifyAsync(second, CancellationToken.None)).Succeeded);
        Assert.AreNotEqual(InferenceCacheKeyDeriver.Derive(runner.Requests[0]).Value, InferenceCacheKeyDeriver.Derive(runner.Requests[1]).Value);
        CollectionAssert.AreEqual(runner.Requests[0].Input.Values.ToArray(), runner.Requests[1].Input.Values.ToArray());
    }

    [TestMethod]
    public async Task MixedRequiredShapesAndFillsDecodeIndependentlyAcrossBatches()
    {
        (MarkerShape Shape, MarkerFill Fill)[] identities =
        [
            (MarkerShape.Circle, MarkerFill.Filled),
            (MarkerShape.Circle, MarkerFill.Open),
            (MarkerShape.Square, MarkerFill.Filled),
            (MarkerShape.Square, MarkerFill.Open),
            (MarkerShape.TriangleUp, MarkerFill.Filled),
            (MarkerShape.TriangleDown, MarkerFill.Open),
            (MarkerShape.Diamond, MarkerFill.Filled),
            (MarkerShape.Star, MarkerFill.Open),
            (MarkerShape.Asterisk, MarkerFill.Filled),
            (MarkerShape.Cross, MarkerFill.Open),
            (MarkerShape.Other, MarkerFill.Unknown),
        ];
        MarkerClassificationOptions options = ClassificationTestSupport.Options() with { BatchSize = 4 };
        MarkerCenter[] markers = identities
            .Select((_, index) => ClassificationTestSupport.Marker($"m{index:D2}", 3 + (index * 2), 16))
            .ToArray();
        var expectedBatches = identities.Chunk(options.BatchSize).ToArray();
        var runner = new ClassificationInferenceRunnerStub(
            expectedBatches.Select(batch => ClassificationInferenceResponses.Success(batch)).ToArray());
        var service = new MarkerClassificationService(runner);

        MarkerClassificationResult result = await service.ClassifyAsync(
            ClassificationTestSupport.Request(markers: markers, options: options),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(identities.Length, result.Markers);
        Assert.HasCount(expectedBatches.Length, result.Batches);
        Assert.HasCount(expectedBatches.Length, runner.Requests);
        for (var index = 0; index < identities.Length; index++)
        {
            Assert.AreEqual(identities[index].Shape, result.Markers[index].Shape);
            Assert.AreEqual(identities[index].Fill, result.Markers[index].Fill);
            Assert.AreEqual(markers[index].MarkerId, result.Markers[index].Marker.MarkerId);
            Assert.AreEqual(
                MarkerSymbolMap.GetAccessibleName(identities[index].Shape, identities[index].Fill),
                result.Markers[index].AccessibleName);
            Assert.AreEqual(1, EmbeddingNorm(result.Markers[index]), 1e-6);
        }

        CollectionAssert.AreEqual(new long[] { 4, 1, 24, 24 }, runner.Requests[0].Input.Shape.ToArray());
        CollectionAssert.AreEqual(new long[] { 3, 1, 24, 24 }, runner.Requests[^1].Input.Shape.ToArray());
        Assert.IsTrue(runner.Requests.SelectMany(request => request.Input.Values.ToArray()).All(value => value == 0));
    }

    [TestMethod]
    public async Task LineContactDoesNotMergeFilledAndOpenCircleClassifications()
    {
        float[] pixels = Enumerable.Repeat(1f, ClassificationTestSupport.FrameSizeSquared).ToArray();
        DrawLine(pixels, 7, 16, 25, 16);
        MarkerCenter filled = ClassificationTestSupport.Marker("filled", 9, 16, 3);
        MarkerCenter open = ClassificationTestSupport.Marker("open", 23, 16, 3);
        var runner = new ClassificationInferenceRunnerStub(
            ClassificationInferenceResponses.Success(
            [
                (MarkerShape.Circle, MarkerFill.Filled),
                (MarkerShape.Circle, MarkerFill.Open),
            ]));
        var service = new MarkerClassificationService(runner);

        MarkerClassificationResult result = await service.ClassifyAsync(
            ClassificationTestSupport.Request(
                ClassificationTestSupport.Frame(pixels),
                [filled, open]),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(2, result.Markers);
        Assert.AreEqual(MarkerFill.Filled, result.Markers.Single(item => item.Marker.MarkerId == "filled").Fill);
        Assert.AreEqual(MarkerFill.Open, result.Markers.Single(item => item.Marker.MarkerId == "open").Fill);
        Assert.AreNotEqual(result.Markers[0].Symbol, result.Markers[1].Symbol);
    }

    [TestMethod]
    public async Task RepeatedClassificationIsStableApartFromRunTimingMetadata()
    {
        (MarkerShape Shape, MarkerFill Fill)[] expected =
        [
            (MarkerShape.Diamond, MarkerFill.Open),
            (MarkerShape.Star, MarkerFill.Filled),
        ];
        InferenceResponse response = ClassificationInferenceResponses.Success(expected);
        var runner = new ClassificationInferenceRunnerStub(response, response);
        var service = new MarkerClassificationService(runner);
        MarkerClassificationRequest request = ClassificationTestSupport.Request(
            markers:
            [
                ClassificationTestSupport.Marker("probe", 10, 10),
                ClassificationTestSupport.Marker("primary", 20, 20),
            ]);

        MarkerClassificationResult first = await service.ClassifyAsync(request, CancellationToken.None);
        MarkerClassificationResult second = await service.ClassifyAsync(request, CancellationToken.None);

        CollectionAssert.AreEqual(
            first.Markers.Select(IdentityMaterial).ToArray(),
            second.Markers.Select(IdentityMaterial).ToArray());
        Assert.AreNotEqual(first.RunId, second.RunId);
    }

    [TestMethod]
    public async Task CacheIdentityIncludesOrderedMarkerIdsCentersAndRadiiForUniformPatches()
    {
        MarkerCenter first = ClassificationTestSupport.Marker("first", 8, 8, 3);
        MarkerCenter second = ClassificationTestSupport.Marker("second", 20, 20, 4);
        InferenceResponse response = ClassificationInferenceResponses.Success(
            [
                (MarkerShape.Circle, MarkerFill.Filled),
                (MarkerShape.Square, MarkerFill.Open),
            ]);
        var runner = new ClassificationInferenceRunnerStub(response, response, response);
        var service = new MarkerClassificationService(runner);

        await service.ClassifyAsync(
            ClassificationTestSupport.Request(markers: [first, second]),
            CancellationToken.None);
        await service.ClassifyAsync(
            ClassificationTestSupport.Request(markers: [first with { Center = new MarkerPoint(9, 8) }, second]),
            CancellationToken.None);
        await service.ClassifyAsync(
            ClassificationTestSupport.Request(markers: [second, first]),
            CancellationToken.None);

        Assert.HasCount(3, runner.Requests);
        StageCacheKey[] keys = runner.Requests
            .Select(InferenceCacheKeyDeriver.Derive)
            .ToArray();
        Assert.AreEqual(3, keys.Select(key => key.Value).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(runner.Requests.All(request => request.CacheMaterial.Parameters.ContainsKey("markers")));
        Assert.IsTrue(
            runner.Requests[0].Input.Values.Span.SequenceEqual(runner.Requests[1].Input.Values.Span),
            "Uniform patches intentionally isolate marker metadata from tensor-content hashing.");
    }

    [TestMethod]
    public async Task EmptyMarkerSetSucceedsWithoutPatchInference()
    {
        var runner = new ClassificationInferenceRunnerStub();
        var service = new MarkerClassificationService(runner);

        MarkerClassificationResult result = await service.ClassifyAsync(
            ClassificationTestSupport.Request(markers: Array.Empty<MarkerCenter>()),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.IsEmpty(result.Markers);
        Assert.IsEmpty(result.Batches);
        Assert.IsEmpty(runner.Requests);
        Assert.AreEqual(0, result.Confidence);
    }

    [TestMethod]
    public async Task LaterBatchFailurePreservesEarlierEvidenceAndMarksItPartial()
    {
        MarkerClassificationOptions options = ClassificationTestSupport.Options() with { BatchSize = 1 };
        InferenceResponse first = ClassificationInferenceResponses.Success(
            [(MarkerShape.Circle, MarkerFill.Filled)]);
        InferenceResponse failure = InferenceResponse.Failure(
            new InferenceError(
                "INFERENCE_FAILED",
                "error",
                "Errors.InferenceFailed",
                "Scripted second-batch failure.",
                true,
                "retry"));
        var runner = new ClassificationInferenceRunnerStub(first, failure);
        var service = new MarkerClassificationService(runner);

        MarkerClassificationResult result = await service.ClassifyAsync(
            ClassificationTestSupport.Request(
                markers:
                [
                    ClassificationTestSupport.Marker("preserved", 8, 8),
                    ClassificationTestSupport.Marker("failed", 20, 20),
                ],
                options: options),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.HasCount(1, result.Markers);
        Assert.AreEqual("preserved", result.Markers[0].Marker.MarkerId);
        Assert.Contains("classification_partial_evidence", result.Warnings);
        Assert.HasCount(2, result.Batches);
        Assert.AreEqual("INFERENCE_FAILED", result.Failure?.Code);
    }

    [TestMethod]
    public async Task DuplicateMarkerIdsReturnStructuredFailureBeforeInference()
    {
        var runner = new ClassificationInferenceRunnerStub();
        var service = new MarkerClassificationService(runner);
        MarkerCenter marker = ClassificationTestSupport.Marker("duplicate", 8, 8);

        MarkerClassificationResult result = await service.ClassifyAsync(
            ClassificationTestSupport.Request(markers: [marker, marker with { Center = new MarkerPoint(16, 16) }]),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("MARKER_CLASSIFICATION_INVALID_REQUEST", result.Failure?.Code);
        Assert.IsEmpty(result.Markers);
        Assert.IsEmpty(runner.Requests);
    }

    [TestMethod]
    public async Task NonSingleChannelModelContractIsRejectedBeforeInference()
    {
        var runner = new ClassificationInferenceRunnerStub();
        var service = new MarkerClassificationService(runner);
        var invalidContract = new MarkerClassifierTensorContract(
            "patches",
            "predictions",
            24,
            24,
            3,
            8);

        MarkerClassificationResult result = await service.ClassifyAsync(
            ClassificationTestSupport.Request(
                options: new MarkerClassificationOptions(invalidContract)),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("MARKER_CLASSIFICATION_INVALID_OPTIONS", result.Failure?.Code);
        Assert.IsEmpty(runner.Requests);
    }

    [TestMethod]
    public async Task NonFiniteModelOutputReturnsStructuredFailureAndNoScientificGuess()
    {
        var invalid = ClassificationInferenceResponses.Success(
            [(MarkerShape.Circle, MarkerFill.Filled)],
            mutate: values => values[0] = float.NaN);
        var runner = new ClassificationInferenceRunnerStub(invalid);
        var service = new MarkerClassificationService(runner);

        MarkerClassificationResult result = await service.ClassifyAsync(
            ClassificationTestSupport.Request(),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("MARKER_CLASSIFICATION_INVALID_MODEL_OUTPUT", result.Failure?.Code);
        Assert.IsEmpty(result.Markers);
        Assert.HasCount(1, result.Batches);
    }

    [TestMethod]
    public async Task ProbabilityPackedOutputIsConsumedWithoutSecondSoftmax()
    {
        MarkerClassifierTensorContract contract = ClassificationTestSupport.TensorContract() with
        {
            OutputEncoding = MarkerClassifierOutputEncoding.Probabilities,
        };
        var runner = new ClassificationInferenceRunnerStub(
            ClassificationInferenceResponses.ProbabilitySuccess(
                shapeProbabilities: [0.51f, 0.49f, 0, 0, 0, 0, 0, 0, 0],
                fillProbabilities: [0.51f, 0.49f, 0],
                artifactProbability: 0.75f));
        var service = new MarkerClassificationService(runner);

        MarkerClassificationResult result = await service.ClassifyAsync(
            ClassificationTestSupport.Request(
                options: ClassificationTestSupport.Options() with { TensorContract = contract }),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.AreEqual(MarkerShape.Circle, result.Markers[0].Shape);
        Assert.AreEqual(MarkerFill.Filled, result.Markers[0].Fill);
        Assert.AreEqual(0.51, result.Markers[0].ShapeConfidence, 1e-6);
        Assert.AreEqual(0.51, result.Markers[0].FillConfidence, 1e-6);
        Assert.AreEqual(0.75, result.Markers[0].ArtifactProbability, 1e-6);
        Assert.AreEqual(
            nameof(MarkerClassifierOutputEncoding.Probabilities),
            runner.Requests.Single().CacheMaterial.Parameters["output_encoding"]);
    }

    [TestMethod]
    public async Task InvalidProbabilityPackedOutputFailsClosed()
    {
        MarkerClassifierTensorContract contract = ClassificationTestSupport.TensorContract() with
        {
            OutputEncoding = MarkerClassifierOutputEncoding.Probabilities,
        };
        var runner = new ClassificationInferenceRunnerStub(
            ClassificationInferenceResponses.ProbabilitySuccess(
                shapeProbabilities: [0.6f, 0.6f, 0, 0, 0, 0, 0, 0, 0],
                fillProbabilities: [1f, 0, 0],
                artifactProbability: 1.01f));
        var service = new MarkerClassificationService(runner);

        MarkerClassificationResult result = await service.ClassifyAsync(
            ClassificationTestSupport.Request(
                options: ClassificationTestSupport.Options() with { TensorContract = contract }),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("MARKER_CLASSIFICATION_INVALID_MODEL_OUTPUT", result.Failure?.Code);
        Assert.IsEmpty(result.Markers);
    }

    [TestMethod]
    public async Task CancellationFromInferencePropagatesWithoutFailureSubstitution()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new ClassificationInferenceRunnerStub((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new AssertFailedException("Cancellation should have interrupted fake inference.");
        });
        var service = new MarkerClassificationService(runner);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await service.ClassifyAsync(
                ClassificationTestSupport.Request(),
                cancellation.Token));
    }

    private static string IdentityMaterial(ClassifiedMarker marker) => string.Join(
        '|',
        marker.Marker.MarkerId,
        marker.Shape,
        marker.Fill,
        marker.Symbol,
        marker.AccessibleName,
        string.Join(',', marker.Embedding.Select(value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));

    private static double EmbeddingNorm(ClassifiedMarker marker) =>
        Math.Sqrt(marker.Embedding.Sum(value => value * value));

    private static void DrawLine(float[] pixels, int x1, int y1, int x2, int y2)
    {
        var steps = Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        for (var step = 0; step <= steps; step++)
        {
            var fraction = (double)step / steps;
            var x = (int)Math.Round(x1 + ((x2 - x1) * fraction));
            var y = (int)Math.Round(y1 + ((y2 - y1) * fraction));
            pixels[(y * ClassificationTestSupport.FrameSize) + x] = 0;
        }
    }
}

internal static class ClassificationInferenceResponses
{
    internal static InferenceResponse Success(
        IEnumerable<(MarkerShape Shape, MarkerFill Fill)> identities,
        Action<float[]>? mutate = null)
    {
        (MarkerShape Shape, MarkerFill Fill)[] values = identities.ToArray();
        MarkerClassifierTensorContract contract = ClassificationTestSupport.TensorContract();
        var output = new float[values.Length * contract.ValuesPerMarker];
        for (var markerIndex = 0; markerIndex < values.Length; markerIndex++)
        {
            var offset = markerIndex * contract.ValuesPerMarker;
            output[offset + MarkerClassifierTensorContract.ShapeOffset + (int)values[markerIndex].Shape] = 10;
            output[offset + MarkerClassifierTensorContract.FillOffset + (int)values[markerIndex].Fill] = 10;
            output[offset + MarkerClassifierTensorContract.ArtifactOffset] = -5;
            for (var embeddingIndex = 0; embeddingIndex < contract.EmbeddingLength; embeddingIndex++)
            {
                output[offset + MarkerClassifierTensorContract.EmbeddingOffset + embeddingIndex] =
                    (markerIndex + 1) * (embeddingIndex + 1);
            }
        }

        mutate?.Invoke(output);
        return new InferenceResponse(
            true,
            new InferenceExecution(
                output,
                InferenceProvider.Fake,
                new StageTiming(0.1, 0.2, 0.1, 0.4, 0, false, false),
                new MemoryDiagnostics(0, 0, 0, 0, 0)),
            null,
            [new ProviderAttempt(InferenceProvider.Fake, true, null)]);
    }

    internal static InferenceResponse ProbabilitySuccess(
        IReadOnlyList<float> shapeProbabilities,
        IReadOnlyList<float> fillProbabilities,
        float artifactProbability)
    {
        MarkerClassifierTensorContract contract = ClassificationTestSupport.TensorContract();
        var output = new float[contract.ValuesPerMarker];
        for (var index = 0; index < shapeProbabilities.Count; index++)
        {
            output[MarkerClassifierTensorContract.ShapeOffset + index] = shapeProbabilities[index];
        }

        for (var index = 0; index < fillProbabilities.Count; index++)
        {
            output[MarkerClassifierTensorContract.FillOffset + index] = fillProbabilities[index];
        }

        output[MarkerClassifierTensorContract.ArtifactOffset] = artifactProbability;
        output[MarkerClassifierTensorContract.EmbeddingOffset] = 1;
        return new InferenceResponse(
            true,
            new InferenceExecution(
                output,
                InferenceProvider.Fake,
                new StageTiming(0.1, 0.2, 0.1, 0.4, 0, false, false),
                new MemoryDiagnostics(0, 0, 0, 0, 0)),
            null,
            [new ProviderAttempt(InferenceProvider.Fake, true, null)]);
    }
}

internal sealed class ClassificationInferenceRunnerStub : IMarkerClassificationInferenceRunner
{
    private readonly Func<InferenceRequest, CancellationToken, ValueTask<InferenceResponse>> _run;
    private readonly List<InferenceRequest> _requests = [];

    internal ClassificationInferenceRunnerStub(params InferenceResponse[] responses)
    {
        var queue = new Queue<InferenceResponse>(responses);
        _run = (_, _) => queue.Count > 0
            ? ValueTask.FromResult(queue.Dequeue())
            : throw new InvalidOperationException("The fake classification response queue is empty.");
    }

    internal ClassificationInferenceRunnerStub(
        Func<InferenceRequest, CancellationToken, ValueTask<InferenceResponse>> run) =>
        _run = run;

    internal IReadOnlyList<InferenceRequest> Requests => _requests;

    public ValueTask<InferenceResponse> RunAsync(
        InferenceRequest request,
        CancellationToken cancellationToken)
    {
        _requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return _run(request, cancellationToken);
    }
}
