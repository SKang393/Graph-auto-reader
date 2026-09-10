// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text;
using System.Text.Json;
using GraphReader.Domain;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>Bounded, model-free metric fixtures supplied through stdin, never an archive reader.</summary>
internal static class OriginalDbOcrMetricFixtures
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal const string Command = "--score-synthetic-ocr-metric-fixtures";

    internal static int Run()
    {
        try
        {
            var input = new StringBuilder();
            char[] buffer = new char[4096];
            int read;
            while ((read = Console.In.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (input.Length + read > 1_048_576)
                    throw new InvalidDataException("Metric fixture input exceeds its bound.");
                input.Append(buffer, 0, read);
            }
            using JsonDocument document = JsonDocument.Parse(input.ToString());
            JsonElement root = document.RootElement;
            if (root.GetProperty("schema").GetString() != "graphreader.synthetic-ocr-metric-fixtures.v1" ||
                root.GetProperty("scope").GetString() != "hand-authored-model-free-metric-fixtures")
                throw new InvalidDataException("Metric fixture scope changed.");
            JsonElement fixtures = root.GetProperty("fixtures");
            if (fixtures.ValueKind != JsonValueKind.Array || fixtures.GetArrayLength() > 256)
                throw new InvalidDataException("Metric fixture count exceeds its bound.");
            var results = new List<OriginalDbOcrAggregateResult>();
            foreach (JsonElement fixture in fixtures.EnumerateArray())
            {
                var scorer = new OriginalDbOcrAggregateScorer();
                JsonElement sources = fixture.GetProperty("sources");
                if (sources.GetArrayLength() > 16)
                    throw new InvalidDataException("Metric source count exceeds its bound.");
                foreach (JsonElement source in sources.EnumerateArray())
                {
                    JsonElement truthRows = source.GetProperty("truths");
                    JsonElement predictionRows = source.GetProperty("predictions");
                    if (truthRows.GetArrayLength() > 64 || predictionRows.GetArrayLength() > 64)
                        throw new InvalidDataException("Metric region count exceeds its bound.");
                    OriginalDbOcrAggregateTruth[] truths = truthRows.EnumerateArray().Select(row =>
                        new OriginalDbOcrAggregateTruth(Box(row.GetProperty("box")),
                            row.GetProperty("text").GetString() ?? throw new InvalidDataException(),
                            GeneratorRole(row.GetProperty("generator_role").GetString()))).ToArray();
                    OriginalDbOcrAggregatePrediction[] predictions = predictionRows.EnumerateArray().Select(row =>
                        new OriginalDbOcrAggregatePrediction(Box(row.GetProperty("box")),
                            row.GetProperty("text").GetString(),
                            row.GetProperty("role").ValueKind == JsonValueKind.Null ? null :
                                RuntimeRole(row.GetProperty("role").GetString()))).ToArray();
                    scorer.AddSource(truths, predictions);
                }
                results.Add(scorer.Score());
            }
            Console.WriteLine(JsonSerializer.Serialize(new { Results = results }, JsonOptions));
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Malformed fixtures must not echo their text or any region data.
            Console.Error.WriteLine("SYNTHETIC_OCR_METRIC_FIXTURE_INVALID");
            return 1;
        }
    }

    private static OriginalDbOcrAggregateBox Box(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 4)
            throw new InvalidDataException("Metric box shape changed.");
        return new(row[0].GetDouble(), row[1].GetDouble(), row[2].GetDouble(), row[3].GetDouble());
    }

    private static OriginalDbOcrGeneratorRole GeneratorRole(string? value) => value switch
    {
        "x_tick" => OriginalDbOcrGeneratorRole.XTick,
        "y_tick" => OriginalDbOcrGeneratorRole.YTick,
        "axis_title" => OriginalDbOcrGeneratorRole.AxisTitle,
        "phase_heading" => OriginalDbOcrGeneratorRole.PhaseHeading,
        "legend_text" => OriginalDbOcrGeneratorRole.LegendText,
        "participant" => OriginalDbOcrGeneratorRole.Participant,
        "annotation" => OriginalDbOcrGeneratorRole.Annotation,
        "condition_label" => OriginalDbOcrGeneratorRole.ConditionLabel,
        _ => throw new InvalidDataException("Unknown generator role."),
    };

    private static OcrRole RuntimeRole(string? value) => value switch
    {
        "xtick" => OcrRole.XTick,
        "ytick" => OcrRole.YTick,
        "axistitle" => OcrRole.AxisTitle,
        "phaseheading" => OcrRole.PhaseHeading,
        "legendtext" => OcrRole.LegendText,
        "participant" => OcrRole.Participant,
        "annotation" => OcrRole.Annotation,
        "other" => OcrRole.Other,
        _ => throw new InvalidDataException("Unknown runtime role."),
    };
}
