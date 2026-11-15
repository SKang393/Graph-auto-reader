// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using GraphReader.App.Services;
using GraphReader.Ocr;

namespace GraphReader.CalibrationOcrProbe;

// Only text rows cross this boundary. Narrative is never an input to calibration.
internal static class MirageComparisonMapping
{
    internal sealed record Row(string Text, IReadOnlyList<double> Box, double? Score);
    internal sealed record Result(IReadOnlyList<CalibrationOcrProposal> Rows, int RejectedRows);

    internal static bool StrictUsable(int status, bool strictJsonValid, string? parseError,
        int repairCount, bool requiresReview, Result mapped) => status >= 0 && strictJsonValid &&
        parseError is null && repairCount == 0 && requiresReview && mapped.RejectedRows == 0;

    internal static CalibrationOcrPoint ToSource(CalibrationOcrPoint point, int cropX, int cropY,
        int panelWidth, int panelHeight, int sourceWidth, int sourceHeight)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || cropX < 0 || cropY < 0 ||
            panelWidth <= 0 || panelHeight <= 0 || (long)cropX + panelWidth > sourceWidth ||
            (long)cropY + panelHeight > sourceHeight || point.X < 0 || point.Y < 0 ||
            point.X > panelWidth || point.Y > panelHeight) throw new InvalidDataException();
        return new(point.X + cropX, point.Y + cropY);
    }

    internal static Result Map(IReadOnlyList<Row> rows, int width, int height, CalibrationOcrPlot plot)
    {
        if (width <= 0 || height <= 0 || rows.Count > 128) throw new InvalidDataException();
        var mapped = new List<CalibrationOcrProposal>();
        int rejected = 0;
        var bounds = new OcrRectangle(plot.Left, plot.Top, plot.Right - plot.Left, plot.Bottom - plot.Top);
        for (int index = 0; index < rows.Count; index++)
        {
            Row row = rows[index];
            if (row.Text is null || row.Box is not { Count: 4 } box ||
                box.Any(value => !double.IsFinite(value) || value is < 0 or > 1000) ||
                box[0] >= box[2] || box[1] >= box[3] ||
                row.Score is not { } score || !double.IsFinite(score) || score is < 0 or > 1)
            { rejected++; continue; }
            double x1 = box[0] * width / 1000d, y1 = box[1] * height / 1000d;
            double x2 = box[2] * width / 1000d, y2 = box[3] * height / 1000d;
            var polygon = OcrPolygon.FromRectangle(new OcrRectangle(x1, y1, x2 - x1, y2 - y1));
            var region = new OcrDetectedRegion($"mirage-{index}", polygon, 0, score);
            string axis = GraphTextRoleClassifier.Classify(region with { Context = new OcrRegionContext(NumericExpected: true) }, "", bounds).Role switch
            {
                OcrTextRole.XTick => "X", OcrTextRole.YTick => "Y", _ => "",
            };
            bool literal = CalibrationNumericLiteral.IsLiteral(row.Text);
            NumericParseResult? parsed = literal ? GraphNumericParser.Parse(row.Text) : null;
            mapped.Add(new CalibrationOcrProposal(region.RegionId,
                polygon.Points.Select(point => new CalibrationOcrPoint(point.X, point.Y)).ToArray(),
                row.Text, score, parsed?.Value, parsed?.NormalizedText, literal,
                GraphTextRoleClassifier.Classify(region, row.Text, bounds).Role.ToString(), axis, null));
        }
        return new(mapped.AsReadOnly(), rejected);
    }

    internal static object SelfTest()
    {
        var plot = new CalibrationOcrPlot(10, 10, 80, 70);
        Result result = Map([new("1", [200, 750, 250, 875], .9),
            new("2", [300, 750, 350, 875], null), new("3", [400, 750, 450, 875], double.NaN)], 100, 80, plot);
        if (result.Rows.Count != 1 || result.RejectedRows != 2) throw new InvalidDataException();
        if (StrictUsable(0, true, null, 0, true, result) || StrictUsable(-1, true, null, 0, true, new([], 0)) ||
            StrictUsable(0, false, null, 0, true, new([], 0)) || StrictUsable(0, true, "invalid", 0, true, new([], 0)) ||
            StrictUsable(0, true, null, 1, true, new([], 0)) || StrictUsable(0, true, null, 0, false, new([], 0)) ||
            !StrictUsable(0, true, null, 0, true, new([], 0))) throw new InvalidDataException();
        var p = result.Rows[0].Polygon;
        if (p[0] != new CalibrationOcrPoint(20, 60) || p[2] != new CalibrationOcrPoint(25, 70) ||
            ToSource(p[0], 40, 15, 100, 80, 200, 120) != new CalibrationOcrPoint(60, 75) ||
            !result.Rows[0].NumericLiteral ||
            result.Rows[0].NumericValue != 1) throw new InvalidDataException();
        return new { schema = "graphreader.mirage-mapping-owned-selftest.v1", status = "passed",
            normalized_boxes_mapped_once = true, unknown_nonfinite_scores_rejected = true,
            narrative_input = false, private_payload_reads = 0, review_only = true };
    }
}
