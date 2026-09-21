// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;

namespace GraphReader.SyntheticRuntimeEvidence;

internal sealed record ComposedOcrAggregateResult(
    int SourceCount,
    OriginalDbOcrGeometryMetrics RawDetectorGeometry,
    OriginalDbOcrGeometryMetrics AssembledGeometry,
    OriginalDbOcrFullMetrics FullOcrMetrics,
    long RecognitionFailedRegionCount);

/// <summary>
/// Scores the two actual boundaries of composed OCR independently. Assembly can
/// merge, split or recover regions, so raw and final counts cannot be subtracted
/// to infer recognition failures. No case-level data is returned by Score().
/// </summary>
internal sealed class ComposedOcrAggregateScorer
{
    private readonly OriginalDbOcrAggregateScorer _raw = new();
    private readonly OriginalDbOcrAggregateScorer _assembled = new();
    private int _sourceCount;
    private long _recognitionFailures;
    private bool _invalid;

    public void AddSource(
        IReadOnlyList<OriginalDbOcrAggregateTruth> truths,
        IReadOnlyList<OriginalDbOcrAggregatePrediction> rawDetectorRegions,
        IReadOnlyList<OriginalDbOcrAggregatePrediction> assembledRegions,
        int recognitionFailedRegionCount)
    {
        if (_invalid)
            throw new InvalidDataException("COMPOSED_OCR_AGGREGATE_INVALID");
        try
        {
            ArgumentNullException.ThrowIfNull(truths);
            ArgumentNullException.ThrowIfNull(rawDetectorRegions);
            ArgumentNullException.ThrowIfNull(assembledRegions);
            if (recognitionFailedRegionCount < 0 ||
                rawDetectorRegions.Any(static p => p is null || p.Text is not null || p.Role is not null) ||
                assembledRegions.Any(static p => p is null || p.Text is null || p.Role is null))
            {
                throw new InvalidDataException("COMPOSED_OCR_BOUNDARY_INVALID");
            }
            _raw.AddSource(truths, rawDetectorRegions);
            _assembled.AddSource(truths, assembledRegions);
            _sourceCount = checked(_sourceCount + 1);
            _recognitionFailures = checked(_recognitionFailures + recognitionFailedRegionCount);
        }
        catch
        {
            // A rejected source must not leave an apparently valid subset score.
            _invalid = true;
            throw;
        }
    }

    public ComposedOcrAggregateResult Score()
    {
        if (_invalid)
            throw new InvalidDataException("COMPOSED_OCR_AGGREGATE_INVALID");
        OriginalDbOcrAggregateResult raw = _raw.Score();
        OriginalDbOcrAggregateResult assembled = _assembled.Score();
        return new(_sourceCount, raw.RawDetectorGeometry,
            assembled.SuccessfullyRecognizedRegionGeometry,
            assembled.FullOcrMetrics, _recognitionFailures);
    }
}
