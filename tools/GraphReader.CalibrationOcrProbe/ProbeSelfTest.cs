// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Text.Json;
using GraphReader.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static class ProbeSelfTest
{
    internal static async Task RunAsync(CancellationToken token)
    {
        const int width = 96, height = 96;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        Ink(20, 75);
        Ink(2, 30);
        Ink(35, 30);
        byte[] bgr = pixels.SelectMany(value => new[] { value, value, value }).ToArray();
        var image = new OcrImage(width, height, width, pixels, OcrSourceImage.Original,
            OcrFrameTransform.Identity, BgrPixels: new(width * 3, bgr));
        var plot = new OcrRectangle(10, 10, 70, 60);
        IReadOnlyList<OcrDetectedRegion> components = await new ConnectedComponentTextRegionDetector().DetectAsync(image, token);
        OcrDetectedRegion[] selected = Program.SelectCandidates(components, plot);
        Require(components.Count == 3 && selected.Length == 2, "Only original-pixel axis-margin components are selected.");
        IReadOnlyList<IReadOnlyList<OcrCrop>> batches = OcrCropBatcher.CreateBatches(image, selected,
            new OcrCropBatcherOptions
            {
                TargetWidth = 320, TargetHeight = 48, WidthMode = OcrCropWidthMode.PaddleBatchMaximumAspectRatio,
            }, token);
        OcrCrop[] crops = batches.SelectMany(batch => batch).ToArray();
        Require(crops.Length == 2 && crops.All(crop => crop.Height == 48 && crop.Width >= 320 && crop.BgrPixels is not null),
            "Existing cropper preserves English-reader dimensions and BGR samples.");
        foreach (OcrCrop crop in crops)
            Require(crop.OriginalPolygon.Points.SequenceEqual(selected.Single(row => row.RegionId == crop.RegionId).Polygon.Points),
                "Crop geometry stays in original panel pixels.");

        OcrDetectedRegion candidate = selected[0];
        var literal = new OcrRecognition(candidate.RegionId, OcrSourceImage.Original,
            [new("20", .9, OcrSourceImage.Original)], 1);
        JsonElement proposal = JsonSerializer.SerializeToElement(Program.ToProposal(candidate, literal, plot, 100, 200));
        Require(proposal.GetProperty("numeric_value").GetDouble() == 20 && proposal.GetProperty("requires_review").GetBoolean(),
            "Actual numeric text produces an unconfirmed suggestion.");
        Require(proposal.GetProperty("polygon")[0][0].GetDouble() == candidate.Polygon.Points[0].X + 100 &&
            proposal.GetProperty("polygon")[0][1].GetDouble() == candidate.Polygon.Points[0].Y + 200,
            "Panel crops map back to full source pixels.");
        var nonnumeric = literal with { Alternatives = [new("O", .9, OcrSourceImage.Original)] };
        proposal = JsonSerializer.SerializeToElement(Program.ToProposal(candidate, nonnumeric, plot, 0, 0));
        Require(proposal.GetProperty("numeric_value").ValueKind == JsonValueKind.Null && !proposal.GetProperty("numeric_literal").GetBoolean(),
            "Letters are not silently converted into scientific numbers.");
        var failed = literal with { Failure = new("TEST_FAILURE", "error", "test", "test", true, "review") };
        proposal = JsonSerializer.SerializeToElement(Program.ToProposal(candidate, failed, plot, 0, 0));
        Require(proposal.GetProperty("numeric_value").ValueKind == JsonValueKind.Null && proposal.GetProperty("failure").GetString() == "TEST_FAILURE",
            "Failed recognition remains visible without a numeric value.");

        void Ink(int x, int y)
        {
            for (int row = y; row < y + 7; row++)
                for (int column = x; column < x + 4; column++) pixels[row * width + column] = 0;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
