// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class OriginalDbOcrSourceMappingSelfTest
{
    internal static object Run()
    {
        var polygon = new OcrPolygon([new(1, 2), new(11, 2), new(11, 7), new(1, 7)]);
        OriginalDbOcrAggregateBox mapped = OriginalDbOcrInMemorySourceEvaluator.MapBox(
            polygon, [1, 0, 100, 0, 1, 200, 0, 0, 1]);
        Require(mapped == new OriginalDbOcrAggregateBox(101, 202, 111, 207));
        mapped = OriginalDbOcrInMemorySourceEvaluator.MapBox(
            polygon, [2, 0, 0, 0, 3, 0, 0, 0, 1]);
        Require(mapped == new OriginalDbOcrAggregateBox(2, 6, 22, 21));
        OcrRectangle bounds = OriginalDbOcrInMemorySourceEvaluator.PlotBounds(
            [new(100, 200), new(400, 200), new(400, 500), new(100, 500)]);
        Require(bounds == new OcrRectangle(100, 200, 300, 300));
        Reject(() => OriginalDbOcrInMemorySourceEvaluator.MapBox(polygon, [1, 0, 0]));
        Reject(() => OriginalDbOcrInMemorySourceEvaluator.MapBox(polygon, [1, 0, 0, 0, 1, 0, 0, 0, 0]));
        Reject(() => OriginalDbOcrInMemorySourceEvaluator.MapBox(polygon, [1, 0, -20, 0, 1, 0, 0, 0, 1]));
        Reject(() => OriginalDbOcrInMemorySourceEvaluator.PlotBounds([new(1, 1), new(1, 1), new(1, 1)]));
        Reject(() => OriginalDbOcrInMemorySourceEvaluator.PlotBounds([new(double.NaN, 1), new(2, 3), new(4, 5)]));
        var output = new OriginalDbOcrSourcePredictions(1,
            [new(new(0, 0, 10, 10), "DO_NOT_SERIALIZE_TEXT", GraphReader.Domain.OcrRole.Annotation)]);
        using JsonDocument serialized = JsonDocument.Parse(JsonSerializer.Serialize(output));
        Require(serialized.RootElement.EnumerateObject().Count() == 1 &&
            serialized.RootElement.GetProperty("PanelCount").GetInt32() == 1);
        var sameBox = OcrPolygon.FromRectangle(new OcrRectangle(0, 0, 10, 10));
        OcrDetectedRegion[] raw = [new("z", sameBox, 0, 1), new("a", sameBox, 0, 1),
            new("failed", OcrPolygon.FromRectangle(new OcrRectangle(30, 0, 10, 10)), 0, 1)];
        OcrRegion[] recognized = [
            new("a", sameBox, "A", [], OcrTextRole.Annotation, 1, OcrSourceImage.Original, OcrReviewStatus.Unreviewed),
            new("z", sameBox, "B", [], OcrTextRole.Annotation, 1, OcrSourceImage.Original, OcrReviewStatus.Unreviewed)];
        var ordered = OriginalDbOcrInMemorySourceEvaluator.InRecognitionOrder(raw, recognized).ToArray();
        Require(ordered.Select(row => row.RegionId).SequenceEqual(["a", "z", "failed"]));
        OriginalDbOcrAggregateTruth[] truth = [
            new(new(0, 0, 10, 10), "B", OriginalDbOcrGeneratorRole.Annotation),
            new(new(0, 0, 10, 10), "A", OriginalDbOcrGeneratorRole.Annotation)];
        OriginalDbOcrAggregateResult Score(IEnumerable<OcrDetectedRegion> sequence)
        {
            var scorer = new OriginalDbOcrAggregateScorer();
            scorer.AddSource(truth, sequence.Select(row => {
                var text = recognized.SingleOrDefault(item => item.RegionId == row.RegionId);
                return new OriginalDbOcrAggregatePrediction(
                    OriginalDbOcrInMemorySourceEvaluator.MapBox(row.Polygon, [1,0,0,0,1,0,0,0,1]),
                    text?.Text, text is null ? null : GraphReader.Domain.OcrRole.Annotation);
            }).ToArray());
            return scorer.Score();
        }
        var correctOrder = Score(ordered);
        var wrongOrder = Score(raw);
        Require(correctOrder.RawDetectorGeometry == wrongOrder.RawDetectorGeometry &&
            correctOrder.FullOcrMetrics.RecognitionExactCount == 2 &&
            wrongOrder.FullOcrMetrics.RecognitionExactCount == 0 &&
            correctOrder.RecognitionFailures.RawRegionsWithoutSuccessfulRecognition == 1);
        return new { status = "pass", checks = 11, model_inference = false,
            private_reads = 0, sealed_reads = 0, scope = "coordinate_mapping_recognition_order_and_output_boundary" };
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("OCR_SOURCE_MAPPING_SELF_TEST_FAILED");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("OCR_SOURCE_MAPPING_INVALID_INPUT_ACCEPTED");
    }
}
