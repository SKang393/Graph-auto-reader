// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class OriginalDbOcrAnnotationReaderSelfTest
{
    public static object Run()
    {
        int checks = 0;

        IReadOnlyList<OriginalDbOcrAggregateTruth> truths = Read("""
            {
              "coordinate_space": "original_pixels",
              "canvas": { "width": 100, "height": 50 },
              "texts": [
                { "text_id": " top ", "region_id": "r-top", "text": "10", "role": "x_tick", "rendered_pixel_box": [1, 2, 10, 5] },
                { "text_id": "hidden", "region_id": "hidden", "text": 7, "role": [], "rendered_pixel_box": "bad", "visible": false },
                { "text_id": "blank", "region_id": "blank", "text": "  ", "role": "bad", "rendered_pixel_box": [] },
                { "text_id": "python-blank", "region_id": "python-blank", "text": "\u001c", "role": "bad", "rendered_pixel_box": [] },
                { "text_id": "absent", "region_id": "absent", "text": "ignored", "role": "bad" },
                { "text_id": "null", "region_id": "null", "text": "ignored", "role": "bad", "rendered_pixel_box": null }
              ],
              "panels": [
                { "texts": [
                  { "text_id": "panel", "region_id": "r-panel", "text": "A😀", "role": "condition_label", "rendered_pixel_box": [20.5, 3, 11.5, 6] }
                ] }
              ]
            }
            """);
        Require(truths.Count == 2 &&
            truths[0].Text == "10" && truths[0].GeneratorRole == OriginalDbOcrGeneratorRole.XTick &&
            truths[1].Text == "A😀" && truths[1].GeneratorRole == OriginalDbOcrGeneratorRole.ConditionLabel,
            "selection and source order");
        checks++;
        Require(truths[0].Box == new OriginalDbOcrAggregateBox(1, 2, 11, 7) &&
            truths[1].Box == new OriginalDbOcrAggregateBox(20.5, 3, 32, 9),
            "XYWH converts to source LTRB");
        checks++;

        string roles = string.Join(",", Read("""
            {
              "coordinate_space":"original_pixels","canvas":{"width":100,"height":50},"texts":[
                {"text_id":"1","region_id":"1","text":"x","role":"x_tick","rendered_pixel_box":[0,0,1,1]},
                {"text_id":"2","region_id":"2","text":"x","role":"y_tick","rendered_pixel_box":[1,0,1,1]},
                {"text_id":"3","region_id":"3","text":"x","role":"axis_title","rendered_pixel_box":[2,0,1,1]},
                {"text_id":"4","region_id":"4","text":"x","role":"phase_heading","rendered_pixel_box":[3,0,1,1]},
                {"text_id":"5","region_id":"5","text":"x","role":"legend_text","rendered_pixel_box":[4,0,1,1]},
                {"text_id":"6","region_id":"6","text":"x","role":"participant","rendered_pixel_box":[5,0,1,1]},
                {"text_id":"7","region_id":"7","text":"x","role":"annotation","rendered_pixel_box":[6,0,1,1]},
                {"text_id":"8","region_id":"8","text":"x","role":"condition_label","rendered_pixel_box":[7,0,1,1]}
              ]
            }
            """).Select(static item => item.GeneratorRole));
        Require(roles == "XTick,YTick,AxisTitle,PhaseHeading,LegendText,Participant,Annotation,ConditionLabel",
            "all frozen roles map exactly");
        checks++;

        ExpectFailure("""{"coordinate_space":"original_pixels","coordinate_space":"original_pixels","canvas":{"width":100,"height":50}}""");
        checks++;
        ExpectFailure("""{"coordinate_space":"panel_pixels","canvas":{"width":100,"height":50}}""");
        checks++;
        ExpectFailure("""{"coordinate_space":"original_pixels","canvas":{"width":100.5,"height":50}}""");
        checks++;
        ExpectFailure("""{"coordinate_space":"original_pixels","canvas":{"width":99,"height":50}}""");
        checks++;
        ExpectFailure("""{"coordinate_space":"original_pixels","canvas":{"width":100,"height":50},"texts":{}}""");
        checks++;
        ExpectFailure("""{"coordinate_space":"original_pixels","canvas":{"width":100,"height":50},"panels":[null]}""");
        checks++;
        ExpectFailure(OneText("1", "1", "7", "annotation", "[1,2,3,4]", rawText: true));
        checks++;
        ExpectFailure(OneText("1", "1", "\"x\"", "other", "[1,2,3,4]", rawText: true));
        checks++;
        ExpectFailure(OneText("1", "1", "\"x\"", "annotation", "[95,2,10,5]", rawText: true));
        checks++;
        ExpectFailure("""{"coordinate_space":"original_pixels","canvas":{"width":100,"height":50},"texts":[{"text_id":"a","region_id":"1","text":"x","role":"annotation","rendered_pixel_box":[1,2,3,4]},{"text_id":" a ","region_id":"2","text":"y","role":"annotation","rendered_pixel_box":[1,2,3,4]}]}""");
        checks++;
        ExpectFailure("""{"coordinate_space":"original_pixels","canvas":{"width":100,"height":50},"texts":[{"text_id":"a","region_id":"r","text":"x","role":"annotation","rendered_pixel_box":[1,2,3,4]}],"panels":[{"texts":[{"text_id":"b","region_id":" r ","text":"y","role":"annotation","rendered_pixel_box":[1,2,3,4]}]}]}""");
        checks++;
        ExpectFailure(OneText("1", "1", "\"bad\\ud800\"", "annotation", "[1,2,3,4]", rawText: true));
        checks++;
        ExpectFailure("""{"coordinate_space":"original_pixels","canvas":{"width":100,"height":50},"\ud800":true}""");
        checks++;
        ExpectFailure("not-json");
        checks++;

        return new
        {
            Status = "passed",
            CheckCount = checks,
            ModelInference = false,
            FileIo = false,
            CaseDataReturned = false,
            PrivateData = false,
            SealedData = false,
            ProductionApproved = false,
        };
    }

    private static IReadOnlyList<OriginalDbOcrAggregateTruth> Read(string json) =>
        OriginalDbOcrAnnotationReader.Read(Encoding.UTF8.GetBytes(json), 100, 50);

    private static string OneText(
        string textId,
        string regionId,
        string text,
        string role,
        string box,
        bool rawText = false)
    {
        string encodedText = rawText ? text : $"\"{text}\"";
        return $$"""
            {"coordinate_space":"original_pixels","canvas":{"width":100,"height":50},"texts":[{"text_id":"{{textId}}","region_id":"{{regionId}}","text":{{encodedText}},"role":"{{role}}","rendered_pixel_box":{{box}}}]}
            """;
    }

    private static void ExpectFailure(string json)
    {
        try
        {
            _ = Read(json);
            throw new InvalidOperationException("Expected annotation validation failure was not raised.");
        }
        catch (InvalidDataException error)
        {
            Require(error.Message.StartsWith("OCR_ANNOTATION_INVALID:", StringComparison.Ordinal),
                "failure is sanitized");
            Require(!error.Message.Contains("bad", StringComparison.Ordinal) &&
                !error.Message.Contains("not-json", StringComparison.Ordinal),
                "failure omits case content");
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
