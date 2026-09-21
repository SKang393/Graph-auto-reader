// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;

namespace GraphReader.SyntheticRuntimeEvidence;

internal sealed record ComposedOcrCorpusAggregate(int SourceCount, int PanelCount, ComposedOcrAggregateResult Metrics);

internal static class ComposedOcrInMemoryCorpusEvaluator
{
    internal static async Task<ComposedOcrCorpusAggregate> EvaluateAsync(
        IReadOnlyList<OriginalDbOcrSealedSourcePayload> sources,
        Func<byte[], string, CancellationToken, Task<ComposedOcrSourcePredictions>> infer,
        CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(sources);
            ArgumentNullException.ThrowIfNull(infer);
            cancellationToken.ThrowIfCancellationRequested();
            if (sources.Count is <= 0 or > OriginalDbOcrInMemoryCorpusEvaluator.MaximumSources)
                throw new InvalidDataException();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var truths = new List<IReadOnlyList<OriginalDbOcrAggregateTruth>>();
            int truthCount = 0;
            foreach (OriginalDbOcrSealedSourcePayload source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!identities.Add(source.ImageSha256) ||
                    Convert.ToHexStringLower(SHA256.HashData(source.ImageBytes)) != source.ImageSha256 ||
                    Convert.ToHexStringLower(SHA256.HashData(source.AnnotationBytes)) != source.AnnotationSha256)
                    throw new InvalidDataException();
                IReadOnlyList<OriginalDbOcrAggregateTruth> rows = OriginalDbOcrAnnotationReader.Read(
                    source.AnnotationBytes, source.Width, source.Height);
                if (rows.Count == 0) throw new InvalidDataException();
                truthCount = CheckCount(rows.Count, truthCount);
                truths.Add(rows);
            }
            var scorer = new ComposedOcrAggregateScorer();
            int panels = 0, rawCount = 0, finalCount = 0;
            for (int index = 0; index < sources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OriginalDbOcrSealedSourcePayload source = sources[index];
                // Neither annotations nor truth-derived geometry cross this boundary.
                ComposedOcrSourcePredictions output = await infer(source.ImageBytes,
                    source.ImageSha256, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (output.PanelCount <= 0) throw new InvalidDataException();
                rawCount = CheckCount(output.RawDetectorRegions.Count, rawCount);
                finalCount = CheckCount(output.AssembledRegions.Count, finalCount);
                scorer.AddSource(truths[index], output.RawDetectorRegions,
                    output.AssembledRegions, output.RecognitionFailedRegionCount);
                panels = checked(panels + output.PanelCount);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(sources.Count, panels, scorer.Score());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Abort instead of reporting partial success or leaking case-level errors.
            throw new InvalidDataException("COMPOSED_OCR_CORPUS_EVALUATION_FAILED");
        }
    }

    private static int CheckCount(int count, int previous)
    {
        int total = checked(count + previous);
        if (count < 0 || count > OriginalDbOcrInMemoryCorpusEvaluator.MaximumRegionsPerSource ||
            total > OriginalDbOcrInMemoryCorpusEvaluator.MaximumRegionsPerCorpus)
            throw new InvalidDataException();
        return total;
    }
}
