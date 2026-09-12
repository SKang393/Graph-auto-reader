// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using GraphReader.App.Integration.Workflow;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

internal sealed record OriginalDbOcrCorpusAggregate(
    int SourceCount,
    int PanelCount,
    OriginalDbOcrAggregateResult Metrics);

/// <summary>
/// Combines authenticated in-memory sources without exposing truth to inference.
/// Admission, archive authentication and runtime ownership remain with the caller.
/// Any failed source aborts the run without returning partial accuracy metrics.
/// </summary>
internal static class OriginalDbOcrInMemoryCorpusEvaluator
{
    // Resource bounds abort the complete run; they never trim metric denominators.
    internal const int MaximumSources = 128;
    internal const int MaximumRegionsPerSource = 1024;
    internal const int MaximumRegionsPerCorpus = 16384;

    internal static Task<OriginalDbOcrCorpusAggregate> EvaluateAsync(
        IReadOnlyList<OriginalDbOcrSealedSourcePayload> sources,
        ProductionOcrAdapter ocr,
        LocalOnnxTextRegionDetector detector,
        ProductionAxisGeometryAdapter axis,
        CancellationToken cancellationToken) =>
        EvaluateCoreAsync(sources,
            (image, hash, token) => OriginalDbOcrInMemorySourceEvaluator.EvaluateAsync(
                image, hash, ocr, detector, axis, token), cancellationToken);

    internal static async Task<OriginalDbOcrCorpusAggregate> EvaluateCoreAsync(
        IReadOnlyList<OriginalDbOcrSealedSourcePayload> sources,
        Func<byte[], string, CancellationToken, Task<OriginalDbOcrSourcePredictions>> infer,
        CancellationToken cancellationToken)
    {
        try
        {
            return await EvaluateSourcesAsync(sources, infer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new InvalidDataException("OCR_CORPUS_EVALUATION_FAILED");
        }
    }

    private static async Task<OriginalDbOcrCorpusAggregate> EvaluateSourcesAsync(
        IReadOnlyList<OriginalDbOcrSealedSourcePayload> sources,
        Func<byte[], string, CancellationToken, Task<OriginalDbOcrSourcePredictions>> infer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(infer);
        cancellationToken.ThrowIfCancellationRequested();
        if (sources.Count == 0 || sources.Count > MaximumSources)
            throw new InvalidDataException("OCR_CORPUS_EMPTY");

        var scorer = new OriginalDbOcrAggregateScorer();
        var roles = new HashSet<OriginalDbOcrGeneratorRole>();
        int panelCount = 0;
        int truthCount = 0;
        int predictionCount = 0;
        foreach (OriginalDbOcrSealedSourcePayload source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<OriginalDbOcrAggregateTruth> truths = OriginalDbOcrAnnotationReader.Read(
                source.AnnotationBytes, source.Width, source.Height);
            cancellationToken.ThrowIfCancellationRequested();
            if (truths.Count == 0)
                throw new InvalidDataException("OCR_CORPUS_SOURCE_TRUTH_EMPTY");
            truthCount = CheckRegionBudget(truths.Count, truthCount);
            foreach (OriginalDbOcrAggregateTruth truth in truths)
                roles.Add(truth.GeneratorRole);

            // The inference delegate receives image bytes and their identity only.
            OriginalDbOcrSourcePredictions predictions = await infer(
                source.ImageBytes, source.ImageSha256, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (predictions.PanelCount <= 0)
                throw new InvalidDataException("OCR_CORPUS_SOURCE_PANELS_EMPTY");
            predictionCount = CheckRegionBudget(predictions.Predictions.Count, predictionCount);
            scorer.AddSource(truths, predictions.Predictions);
            cancellationToken.ThrowIfCancellationRequested();
            panelCount = checked(panelCount + predictions.PanelCount);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!roles.SetEquals(Enum.GetValues<OriginalDbOcrGeneratorRole>()))
            throw new InvalidDataException("OCR_CORPUS_ROLE_COVERAGE_INCOMPLETE");
        OriginalDbOcrAggregateResult metrics = scorer.Score();
        cancellationToken.ThrowIfCancellationRequested();
        return new OriginalDbOcrCorpusAggregate(sources.Count, panelCount, metrics);
    }

    private static int CheckRegionBudget(int sourceCount, int previousCount)
    {
        int total = checked(previousCount + sourceCount);
        if (sourceCount < 0 || sourceCount > MaximumRegionsPerSource || total > MaximumRegionsPerCorpus)
            throw new InvalidDataException("OCR_CORPUS_REGION_LIMIT");
        return total;
    }
}
