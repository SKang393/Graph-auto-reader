// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using GraphReader.Domain;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class OriginalDbOcrAggregateScorerSelfTest
{
    public static object Run()
    {
        int checks = 0;

        OriginalDbOcrAggregateResult exact = Score(
            [Truth(Box(0, 0, 10, 10), "A", OriginalDbOcrGeneratorRole.ConditionLabel)],
            [Prediction(Box(0, 0, 10, 10), "A", OcrRole.PhaseHeading)]);
        Require(exact.RawDetectorGeometry.TruePositives == 1
            && exact.FullOcrMetrics.RecognitionExactCount == 1
            && exact.FullOcrMetrics.RoleCorrectCount == 1
            && exact.FullOcrMetrics.ByExpectedRuntimeRole["phaseheading"].CorrectCount == 1,
            "condition label maps to phase heading");
        checks++;

        OriginalDbOcrAggregateResult other = Score(
            [Truth(Box(0, 0, 10, 10), "note", OriginalDbOcrGeneratorRole.Annotation)],
            [Prediction(Box(0, 0, 10, 10), "note", OcrRole.Other)]);
        Require(other.FullOcrMetrics.RecognitionExactCount == 1
            && other.FullOcrMetrics.RoleCorrectCount == 0,
            "runtime Other remains a valid prediction role");
        checks++;

        OriginalDbOcrAggregateResult ambiguous = Score(
            [
                Truth(Box(0, 0, 10, 10), "left", OriginalDbOcrGeneratorRole.XTick),
                Truth(Box(5, 0, 15, 10), "right", OriginalDbOcrGeneratorRole.YTick),
            ],
            [
                Prediction(Box(2.5, 0, 12.5, 10), "left", OcrRole.XTick),
                Prediction(Box(0, 0, 10, 10), "right", OcrRole.YTick),
            ]);
        Require(ambiguous.FullOcrMetrics.GeometryMatchedRegionCount == 2
            && ambiguous.FullOcrMetrics.RecognitionExactCount == 0
            && ambiguous.FullOcrMetrics.RoleCorrectCount == 0,
            "maximum-cardinality geometry fixes ambiguous pairing independently of text and role");
        checks++;

        OriginalDbOcrAggregateResult recognitionMissing = Score(
            [Truth(Box(0, 0, 10, 10), "A😀", OriginalDbOcrGeneratorRole.Annotation)],
            [new OriginalDbOcrAggregatePrediction(Box(0, 0, 10, 10), null, null)]);
        Require(recognitionMissing.RawDetectorGeometry.TruePositives == 1
            && recognitionMissing.SuccessfullyRecognizedRegionGeometry.TruePositives == 0
            && recognitionMissing.RecognitionFailures.RawRegionsWithoutSuccessfulRecognition == 1
            && recognitionMissing.FullOcrMetrics.TruthCharacterCount == 2
            && recognitionMissing.FullOcrMetrics.UnmatchedTruthDeletionEditCount == 2,
            "raw detection survives recognition failure with Unicode scalar deletion count");
        checks++;

        OriginalDbOcrAggregateResult unicode = Score(
            [Truth(Box(0, 0, 10, 10), "A😀", OriginalDbOcrGeneratorRole.Participant)],
            [Prediction(Box(0, 0, 10, 10), "A😁", OcrRole.Participant)]);
        Require(unicode.FullOcrMetrics.TruthCharacterCount == 2
            && unicode.FullOcrMetrics.MatchedPairEditCount == 1
            && unicode.FullOcrMetrics.CharacterErrorCount == 1
            && unicode.FullOcrMetrics.CharacterErrorRate == 0.5,
            "supplementary Unicode is scored as one scalar");
        checks++;

        OriginalDbOcrAggregateResult insertionAndDeletion = Score(
            [
                Truth(Box(0, 0, 10, 10), "ok", OriginalDbOcrGeneratorRole.XTick),
                Truth(Box(20, 0, 30, 10), "gone", OriginalDbOcrGeneratorRole.YTick),
            ],
            [
                Prediction(Box(0, 0, 10, 10), "ok", OcrRole.XTick),
                Prediction(Box(40, 0, 50, 10), "extra", OcrRole.Other),
            ]);
        Require(insertionAndDeletion.FullOcrMetrics.RecognitionExactCount == 1
            && insertionAndDeletion.FullOcrMetrics.UnmatchedTruthDeletionEditCount == 4
            && insertionAndDeletion.FullOcrMetrics.UnmatchedPredictionInsertionEditCount == 5
            && insertionAndDeletion.FullOcrMetrics.CharacterErrorCount == 9,
            "unmatched truth and prediction edits retain the full denominator");
        checks++;

        var aggregate = new OriginalDbOcrAggregateScorer();
        aggregate.AddSource(
            [Truth(Box(0, 0, 10, 10), "a", OriginalDbOcrGeneratorRole.Annotation)],
            [Prediction(Box(0, 0, 10, 10), "a", OcrRole.Annotation)]);
        aggregate.AddSource(
            [Truth(Box(0, 0, 10, 10), "b", OriginalDbOcrGeneratorRole.LegendText)],
            []);
        OriginalDbOcrAggregateResult twoSources = aggregate.Score();
        Require(twoSources.FullOcrMetrics.TruthRegionCount == 2
            && twoSources.FullOcrMetrics.GeometryMatchedRegionCount == 1
            && twoSources.FullOcrMetrics.RecognitionExactAccuracy == 0.5,
            "per-source matching aggregates without cross-source matches");
        checks++;

        OriginalDbOcrAggregateResult empty = Score([], []);
        Require(empty.RawDetectorGeometry.Precision == 0
            && empty.RawDetectorGeometry.Recall == 0
            && empty.FullOcrMetrics.CharacterErrorRate == 0
            && empty.FullOcrMetrics.RoleAccuracy == 0,
            "empty aggregate ratios remain finite zero");
        checks++;

        OriginalDbOcrAggregateResult emptyTruthTextWithInsertion = Score(
            [Truth(Box(0, 0, 10, 10), "", OriginalDbOcrGeneratorRole.Annotation)],
            [Prediction(Box(20, 0, 30, 10), "x", OcrRole.Other)]);
        Require(emptyTruthTextWithInsertion.FullOcrMetrics.TruthCharacterCount == 0
            && emptyTruthTextWithInsertion.FullOcrMetrics.CharacterErrorCount == 1
            && emptyTruthTextWithInsertion.FullOcrMetrics.CharacterErrorRate == 1,
            "zero truth characters use the frozen max-one CER denominator");
        checks++;

        ExpectFailure(() => Score(
            [Truth(Box(double.NaN, 0, 10, 10), "x", OriginalDbOcrGeneratorRole.Annotation)], []),
            "finite positive area");
        checks++;
        ExpectFailure(() => Score(
            [Truth(Box(10, 0, 0, 10), "x", OriginalDbOcrGeneratorRole.Annotation)], []),
            "finite positive area");
        checks++;
        ExpectFailure(() => Score(
            [Truth(Box(0, 0, 10, 10), "x", (OriginalDbOcrGeneratorRole)999)], []),
            "unknown generator role");
        checks++;
        ExpectFailure(() => Score(
            [], [Prediction(Box(0, 0, 10, 10), "x", (OcrRole)999)]),
            "unknown runtime role");
        checks++;
        ExpectFailure(() => Score(
            [], [new OriginalDbOcrAggregatePrediction(Box(0, 0, 10, 10), "x", null)]),
            "both be present");
        checks++;
        ExpectFailure(() => Score(
            [Truth(Box(0, 0, 10, 10), "\ud800", OriginalDbOcrGeneratorRole.Annotation)], []),
            "invalid Unicode");
        checks++;

        return new
        {
            Status = "passed",
            CheckCount = checks,
            ModelInference = false,
            PrivateData = false,
            SealedData = false,
            ProductionApproved = false,
            CaseDataReturned = false,
        };
    }

    private static OriginalDbOcrAggregateResult Score(
        IReadOnlyList<OriginalDbOcrAggregateTruth> truths,
        IReadOnlyList<OriginalDbOcrAggregatePrediction> predictions)
    {
        var scorer = new OriginalDbOcrAggregateScorer();
        scorer.AddSource(truths, predictions);
        return scorer.Score();
    }

    private static OriginalDbOcrAggregateTruth Truth(
        OriginalDbOcrAggregateBox box,
        string text,
        OriginalDbOcrGeneratorRole role) => new(box, text, role);

    private static OriginalDbOcrAggregatePrediction Prediction(
        OriginalDbOcrAggregateBox box,
        string text,
        OcrRole role) => new(box, text, role);

    private static OriginalDbOcrAggregateBox Box(double left, double top, double right, double bottom) =>
        new(left, top, right, bottom);

    private static void ExpectFailure(Action action, string expected)
    {
        try
        {
            action();
            throw new InvalidOperationException("Expected validation failure was not raised.");
        }
        catch (InvalidDataException error)
        {
            if (!error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Expected failure containing '{expected}', got '{error.Message}'.", error);
            }
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Self-test failed: " + label);
        }
    }
}
