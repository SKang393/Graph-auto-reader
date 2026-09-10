// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.IO;
using GraphReader.Domain;

namespace GraphReader.SyntheticRuntimeEvidence;

internal readonly record struct OriginalDbOcrAggregateBox(
    double Left,
    double Top,
    double Right,
    double Bottom);

internal enum OriginalDbOcrGeneratorRole
{
    XTick,
    YTick,
    AxisTitle,
    PhaseHeading,
    LegendText,
    Participant,
    Annotation,
    ConditionLabel,
}

internal sealed record OriginalDbOcrAggregateTruth(
    OriginalDbOcrAggregateBox Box,
    string Text,
    OriginalDbOcrGeneratorRole GeneratorRole);

internal sealed record OriginalDbOcrAggregatePrediction(
    OriginalDbOcrAggregateBox Box,
    string? Text,
    OcrRole? Role);

internal sealed record OriginalDbOcrGeometryMetrics(
    long TruthRegionCount,
    long PredictedRegionCount,
    long TruePositives,
    long FalsePositives,
    long FalseNegatives,
    double Precision,
    double Recall,
    double IntersectionOverUnionMinimum);

internal sealed record OriginalDbOcrRoleMetrics(
    long TruthCount,
    long CorrectCount,
    double Accuracy);

internal sealed record OriginalDbOcrFullMetrics(
    long TruthRegionCount,
    long PredictedRegionCount,
    long GeometryMatchedRegionCount,
    long GeometryFalsePositiveCount,
    long GeometryFalseNegativeCount,
    long RecognitionExactCount,
    double RecognitionExactAccuracy,
    long TruthCharacterCount,
    long MatchedPairEditCount,
    long UnmatchedTruthDeletionEditCount,
    long UnmatchedPredictionInsertionEditCount,
    long CharacterErrorCount,
    double CharacterErrorRate,
    long RoleCorrectCount,
    double RoleAccuracy,
    IReadOnlyDictionary<string, OriginalDbOcrRoleMetrics> ByExpectedRuntimeRole,
    double IntersectionOverUnionMinimum);

internal sealed record OriginalDbOcrRecognitionFailures(
    long RawRegionsWithoutSuccessfulRecognition);

internal sealed record OriginalDbOcrAggregateResult(
    OriginalDbOcrGeometryMetrics RawDetectorGeometry,
    OriginalDbOcrGeometryMetrics SuccessfullyRecognizedRegionGeometry,
    OriginalDbOcrRecognitionFailures RecognitionFailures,
    OriginalDbOcrFullMetrics FullOcrMetrics);

internal sealed class OriginalDbOcrAggregateScorer
{
    internal const double MatchIntersectionOverUnionMinimum = 0.5;

    private long _truthCount;
    private long _rawPredictionCount;
    private long _rawMatchCount;
    private long _recognizedPredictionCount;
    private long _recognizedMatchCount;
    private long _recognitionExactCount;
    private long _truthCharacterCount;
    private long _matchedPairEditCount;
    private long _unmatchedTruthDeletionEditCount;
    private long _unmatchedPredictionInsertionEditCount;
    private long _roleCorrectCount;
    private readonly Dictionary<OcrRole, MutableRoleCounts> _roleCounts = [];

    public void AddSource(
        IReadOnlyList<OriginalDbOcrAggregateTruth> truths,
        IReadOnlyList<OriginalDbOcrAggregatePrediction> predictions)
    {
        ArgumentNullException.ThrowIfNull(truths);
        ArgumentNullException.ThrowIfNull(predictions);

        OriginalDbOcrAggregateTruth[] sourceTruths = new OriginalDbOcrAggregateTruth[truths.Count];
        for (int index = 0; index < truths.Count; index++)
        {
            OriginalDbOcrAggregateTruth truth = truths[index]
                ?? throw new InvalidDataException("OCR truth cannot be null.");
            ValidateBox(truth.Box, "truth");
            _ = ToRunes(truth.Text, "truth text");
            _ = MapTruthRole(truth.GeneratorRole);
            sourceTruths[index] = truth;
        }

        OriginalDbOcrAggregatePrediction[] rawPredictions =
            new OriginalDbOcrAggregatePrediction[predictions.Count];
        List<OriginalDbOcrAggregatePrediction> recognizedPredictions = [];
        for (int index = 0; index < predictions.Count; index++)
        {
            OriginalDbOcrAggregatePrediction prediction = predictions[index]
                ?? throw new InvalidDataException("OCR prediction cannot be null.");
            ValidateBox(prediction.Box, "prediction");
            if ((prediction.Text is null) != (prediction.Role is null))
            {
                throw new InvalidDataException(
                    "OCR prediction text and role must both be present or both be absent.");
            }
            if (prediction.Role is OcrRole role && !Enum.IsDefined(role))
            {
                throw new InvalidDataException("OCR prediction carries an unknown runtime role.");
            }
            if (prediction.Text is not null)
            {
                _ = ToRunes(prediction.Text, "prediction text");
                recognizedPredictions.Add(prediction);
            }
            rawPredictions[index] = prediction;
        }

        List<(int PredictionIndex, int TruthIndex)> rawPairs =
            MaximumCardinalityPairs(rawPredictions, sourceTruths);
        List<(int PredictionIndex, int TruthIndex)> recognizedPairs =
            MaximumCardinalityPairs(recognizedPredictions, sourceTruths);

        checked
        {
            _truthCount += sourceTruths.Length;
            _rawPredictionCount += rawPredictions.Length;
            _rawMatchCount += rawPairs.Count;
            _recognizedPredictionCount += recognizedPredictions.Count;
            _recognizedMatchCount += recognizedPairs.Count;
        }

        HashSet<int> matchedPredictions = [];
        HashSet<int> matchedTruths = [];
        Dictionary<int, int> predictionByTruth = [];
        foreach ((int predictionIndex, int truthIndex) in recognizedPairs)
        {
            matchedPredictions.Add(predictionIndex);
            matchedTruths.Add(truthIndex);
            predictionByTruth.Add(truthIndex, predictionIndex);
            OriginalDbOcrAggregatePrediction prediction = recognizedPredictions[predictionIndex];
            OriginalDbOcrAggregateTruth truth = sourceTruths[truthIndex];
            OcrRole expectedRole = MapTruthRole(truth.GeneratorRole);
            checked
            {
                _recognitionExactCount += prediction.Text == truth.Text ? 1 : 0;
                _matchedPairEditCount += Levenshtein(truth.Text, prediction.Text!);
                _roleCorrectCount += prediction.Role == expectedRole ? 1 : 0;
            }
        }

        for (int index = 0; index < sourceTruths.Length; index++)
        {
            OriginalDbOcrAggregateTruth truth = sourceTruths[index];
            OcrRole expectedRole = MapTruthRole(truth.GeneratorRole);
            int characterCount = ToRunes(truth.Text, "truth text").Length;
            checked
            {
                _truthCharacterCount += characterCount;
                if (!matchedTruths.Contains(index))
                {
                    _unmatchedTruthDeletionEditCount += characterCount;
                }
            }

            if (!_roleCounts.TryGetValue(expectedRole, out MutableRoleCounts? roleCounts))
            {
                roleCounts = new MutableRoleCounts();
                _roleCounts.Add(expectedRole, roleCounts);
            }
            checked
            {
                roleCounts.TruthCount++;
            }
            if (predictionByTruth.TryGetValue(index, out int predictionIndex)
                && recognizedPredictions[predictionIndex].Role == expectedRole)
            {
                checked
                {
                    roleCounts.CorrectCount++;
                }
            }
        }

        for (int index = 0; index < recognizedPredictions.Count; index++)
        {
            if (!matchedPredictions.Contains(index))
            {
                checked
                {
                    _unmatchedPredictionInsertionEditCount +=
                        ToRunes(recognizedPredictions[index].Text!, "prediction text").Length;
                }
            }
        }
    }

    public OriginalDbOcrAggregateResult Score()
    {
        OriginalDbOcrGeometryMetrics raw = Geometry(
            _truthCount, _rawPredictionCount, _rawMatchCount);
        OriginalDbOcrGeometryMetrics recognized = Geometry(
            _truthCount, _recognizedPredictionCount, _recognizedMatchCount);
        long totalEdits;
        checked
        {
            totalEdits = _matchedPairEditCount
                + _unmatchedTruthDeletionEditCount
                + _unmatchedPredictionInsertionEditCount;
        }
        Dictionary<string, OriginalDbOcrRoleMetrics> roleMetrics = [];
        foreach ((OcrRole role, MutableRoleCounts counts) in _roleCounts
                     .OrderBy(item => RuntimeRoleName(item.Key), StringComparer.Ordinal))
        {
            roleMetrics.Add(RuntimeRoleName(role), new OriginalDbOcrRoleMetrics(
                counts.TruthCount,
                counts.CorrectCount,
                Ratio(counts.CorrectCount, counts.TruthCount)));
        }

        OriginalDbOcrFullMetrics full = new(
            _truthCount,
            _recognizedPredictionCount,
            _recognizedMatchCount,
            checked(_recognizedPredictionCount - _recognizedMatchCount),
            checked(_truthCount - _recognizedMatchCount),
            _recognitionExactCount,
            Ratio(_recognitionExactCount, _truthCount),
            _truthCharacterCount,
            _matchedPairEditCount,
            _unmatchedTruthDeletionEditCount,
            _unmatchedPredictionInsertionEditCount,
            totalEdits,
            Ratio(totalEdits, _truthCharacterCount),
            _roleCorrectCount,
            Ratio(_roleCorrectCount, _truthCount),
            new ReadOnlyDictionary<string, OriginalDbOcrRoleMetrics>(roleMetrics),
            MatchIntersectionOverUnionMinimum);
        return new OriginalDbOcrAggregateResult(
            raw,
            recognized,
            new OriginalDbOcrRecognitionFailures(
                checked(_rawPredictionCount - _recognizedPredictionCount)),
            full);
    }

    private static OriginalDbOcrGeometryMetrics Geometry(long truths, long predictions, long matches) =>
        new(
            truths,
            predictions,
            matches,
            checked(predictions - matches),
            checked(truths - matches),
            Ratio(matches, predictions),
            Ratio(matches, truths),
            MatchIntersectionOverUnionMinimum);

    private static double Ratio(long numerator, long denominator) =>
        (double)numerator / Math.Max(1, denominator);

    private static List<(int PredictionIndex, int TruthIndex)> MaximumCardinalityPairs(
        IReadOnlyList<OriginalDbOcrAggregatePrediction> predictions,
        OriginalDbOcrAggregateTruth[] truths)
    {
        List<int>[] edges = new List<int>[predictions.Count];
        for (int predictionIndex = 0; predictionIndex < predictions.Count; predictionIndex++)
        {
            edges[predictionIndex] = [];
            for (int truthIndex = 0; truthIndex < truths.Length; truthIndex++)
            {
                if (IntersectionOverUnion(predictions[predictionIndex].Box, truths[truthIndex].Box)
                    >= MatchIntersectionOverUnionMinimum)
                {
                    edges[predictionIndex].Add(truthIndex);
                }
            }
        }

        int[] owners = Enumerable.Repeat(-1, truths.Length).ToArray();
        bool Visit(int predictionIndex, HashSet<int> seen)
        {
            foreach (int truthIndex in edges[predictionIndex])
            {
                if (!seen.Add(truthIndex))
                {
                    continue;
                }
                if (owners[truthIndex] == -1 || Visit(owners[truthIndex], seen))
                {
                    owners[truthIndex] = predictionIndex;
                    return true;
                }
            }
            return false;
        }

        for (int predictionIndex = 0; predictionIndex < predictions.Count; predictionIndex++)
        {
            _ = Visit(predictionIndex, []);
        }
        List<(int PredictionIndex, int TruthIndex)> pairs = [];
        for (int truthIndex = 0; truthIndex < owners.Length; truthIndex++)
        {
            if (owners[truthIndex] != -1)
            {
                pairs.Add((owners[truthIndex], truthIndex));
            }
        }
        return pairs;
    }

    private static OcrRole MapTruthRole(OriginalDbOcrGeneratorRole role) => role switch
    {
        OriginalDbOcrGeneratorRole.XTick => OcrRole.XTick,
        OriginalDbOcrGeneratorRole.YTick => OcrRole.YTick,
        OriginalDbOcrGeneratorRole.AxisTitle => OcrRole.AxisTitle,
        OriginalDbOcrGeneratorRole.PhaseHeading => OcrRole.PhaseHeading,
        OriginalDbOcrGeneratorRole.LegendText => OcrRole.LegendText,
        OriginalDbOcrGeneratorRole.Participant => OcrRole.Participant,
        OriginalDbOcrGeneratorRole.Annotation => OcrRole.Annotation,
        OriginalDbOcrGeneratorRole.ConditionLabel => OcrRole.PhaseHeading,
        _ => throw new InvalidDataException("OCR truth carries an unknown generator role."),
    };

    private static string RuntimeRoleName(OcrRole role) => role switch
    {
        OcrRole.XTick => "xtick",
        OcrRole.YTick => "ytick",
        OcrRole.AxisTitle => "axistitle",
        OcrRole.PhaseHeading => "phaseheading",
        OcrRole.LegendText => "legendtext",
        OcrRole.Participant => "participant",
        OcrRole.Annotation => "annotation",
        OcrRole.Other => "other",
        _ => throw new InvalidDataException("OCR prediction carries an unknown runtime role."),
    };

    private static void ValidateBox(OriginalDbOcrAggregateBox box, string label)
    {
        if (!double.IsFinite(box.Left) || !double.IsFinite(box.Top)
            || !double.IsFinite(box.Right) || !double.IsFinite(box.Bottom)
            || box.Right <= box.Left || box.Bottom <= box.Top
            || !double.IsFinite(box.Right - box.Left)
            || !double.IsFinite(box.Bottom - box.Top)
            || !double.IsFinite((box.Right - box.Left) * (box.Bottom - box.Top)))
        {
            throw new InvalidDataException($"OCR {label} box must have finite positive area.");
        }
    }

    private static double IntersectionOverUnion(
        OriginalDbOcrAggregateBox left,
        OriginalDbOcrAggregateBox right)
    {
        double intersectionWidth = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));
        double intersectionHeight = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top));
        double intersection = intersectionWidth * intersectionHeight;
        double leftArea = (left.Right - left.Left) * (left.Bottom - left.Top);
        double rightArea = (right.Right - right.Left) * (right.Bottom - right.Top);
        double union = leftArea + rightArea - intersection;
        if (!double.IsFinite(union) || union <= 0)
        {
            throw new InvalidDataException("OCR box union area is not finite and positive.");
        }
        double value = intersection / union;
        if (!double.IsFinite(value))
        {
            throw new InvalidDataException("OCR box intersection-over-union is not finite.");
        }
        return value;
    }

    private static int Levenshtein(string left, string right)
    {
        Rune[] leftRunes = ToRunes(left, "truth text");
        Rune[] rightRunes = ToRunes(right, "prediction text");
        int rowLength = checked(rightRunes.Length + 1);
        int[] previous = new int[rowLength];
        int[] current = new int[rowLength];
        for (int column = 0; column < previous.Length; column++)
        {
            previous[column] = column;
        }
        for (int row = 1; row <= leftRunes.Length; row++)
        {
            current[0] = row;
            for (int column = 1; column <= rightRunes.Length; column++)
            {
                current[column] = checked(Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + (leftRunes[row - 1] == rightRunes[column - 1] ? 0 : 1)));
            }
            (previous, current) = (current, previous);
        }
        return previous[rightRunes.Length];
    }

    private static Rune[] ToRunes(string? value, string label)
    {
        if (value is null)
        {
            throw new InvalidDataException($"OCR {label} cannot be null.");
        }
        List<Rune> runes = [];
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(remaining, out Rune rune, out int consumed);
            if (status != OperationStatus.Done)
            {
                throw new InvalidDataException($"OCR {label} contains invalid Unicode.");
            }
            runes.Add(rune);
            remaining = remaining[consumed..];
        }
        return [.. runes];
    }

    private sealed class MutableRoleCounts
    {
        public long TruthCount { get; set; }

        public long CorrectCount { get; set; }
    }
}
