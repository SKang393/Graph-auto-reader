// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--stdio")
        {
            Console.Error.WriteLine("usage: AxisStrokeAlignment --stdio");
            return 2;
        }

        var decoder = new ProductionRasterFrameDecoder();
        var detector = new AxisGeometryDetector();
        var provider = new OpenCvLineCandidateProvider();
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            string sourceId = "unknown";
            try
            {
                var input = JsonSerializer.Deserialize<SourceInput>(line, JsonOptions)
                    ?? throw new InvalidDataException("Empty source input.");
                sourceId = input.SourceId;
                byte[] imageBytes = Convert.FromBase64String(input.ImagePngBase64);
                Require(Hash(imageBytes) == input.ImagePngSha256, "Source PNG transport hash mismatch.");
                var image = new WorkflowImageEvidence(
                    $"memory:{input.SourceId}.png", Hash(imageBytes), input.Width, input.Height,
                    WorkflowImageVariant.Original);
                var imported = new WorkflowImportedPanel(
                    Guid.Parse(input.Panels[0].PanelId), Guid.NewGuid(), input.SourceId, image);
                var prepared = new WorkflowPreparedPanel(imported, image, null);
                var request = new ProductionWorkflowDetectionRequest(
                    prepared, image, WorkflowImageVariant.Original, Guid.NewGuid(), Guid.NewGuid(), imageBytes);
                GrayscaleLineCandidateFrame sourceFrame = decoder.Decode(request, CancellationToken.None).CreateAxisFrame();
                byte[] fullPixels = sourceFrame.Pixels.ToArray();
                var results = new List<PanelOutput>(input.Panels.Count);

                foreach (PanelInput panel in input.Panels)
                {
                    int left = Math.Clamp(panel.Bounds[0], 0, input.Width - 1);
                    int top = Math.Clamp(panel.Bounds[1], 0, input.Height - 1);
                    int right = Math.Clamp(panel.Bounds[2], left + 1, input.Width);
                    int bottom = Math.Clamp(panel.Bounds[3], top + 1, input.Height);
                    int width = right - left;
                    int height = bottom - top;
                    var pixels = new byte[checked(width * height)];
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.BlockCopy(fullPixels, (top + y) * input.Width + left, pixels, y * width, width);
                    }

                    try
                    {
                        var frame = new GrayscaleLineCandidateFrame(width, height, width, pixels);
                        AxisGeometryResult geometry = await detector.DetectAsync(frame, provider);
                        var ticks = geometry.Ticks
                            .Where(static tick => tick.Axis == TickAxis.XAxis)
                            .Select(tick => ToTickOutput(tick, left, top))
                            .ToArray();
                        results.Add(new PanelOutput(panel.PanelId, panel.PanelIndex,
                            [left, top, right, bottom], "ok", null,
                            geometry.Diagnostics.InputCandidateCount,
                            geometry.Diagnostics.AcceptedCandidateCount,
                            geometry.Diagnostics.RejectedCandidateCount,
                            geometry.Confidence,
                            geometry.Uncertainty.NeedsReview,
                            geometry.Uncertainty.Reasons,
                            ticks));
                    }
                    catch (Exception exception)
                    {
                        results.Add(new PanelOutput(panel.PanelId, panel.PanelIndex,
                            [left, top, right, bottom], "failed", exception.GetType().Name + ": " + exception.Message,
                            0, 0, 0, null, null, [], []));
                    }
                }

                Console.WriteLine(JsonSerializer.Serialize(new SourceOutput(
                    input.SourceId, input.ExpectedSourcePixelSha256, Hash(imageBytes),
                    input.Width, input.Height, results), JsonOptions));
                await Console.Out.FlushAsync();
            }
            catch (Exception exception)
            {
                Console.WriteLine(JsonSerializer.Serialize(new SourceFailure(
                    sourceId, "source_failed", exception.GetType().Name + ": " + exception.Message), JsonOptions));
                await Console.Out.FlushAsync();
            }
        }

        return 0;
    }

    private static TickOutput ToTickOutput(AxisTickGeometry tick, int xOffset, int yOffset)
    {
        bool usable = !string.IsNullOrWhiteSpace(tick.TickId) &&
            double.IsFinite(tick.Center.X) && double.IsFinite(tick.Center.Y) &&
            double.IsFinite(tick.Line.Start.X) && double.IsFinite(tick.Line.Start.Y) &&
            double.IsFinite(tick.Line.End.X) && double.IsFinite(tick.Line.End.Y) &&
            (tick.Line.Start.X != tick.Line.End.X || tick.Line.Start.Y != tick.Line.End.Y) &&
            double.IsFinite(tick.Confidence) && tick.Confidence is > 0d and <= 1d &&
            tick.SupportingCandidateIds is { Count: > 0 } &&
            tick.SupportingCandidateIds.All(static id => !string.IsNullOrWhiteSpace(id));
        return new TickOutput(
            tick.TickId,
            [tick.Center.X + xOffset, tick.Center.Y + yOffset],
            [tick.Line.Start.X + xOffset, tick.Line.Start.Y + yOffset,
             tick.Line.End.X + xOffset, tick.Line.End.Y + yOffset],
            tick.Confidence, tick.SupportingCandidateIds.Count, usable);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record SourceInput(
        string SourceId,
        string ExpectedSourcePixelSha256,
        int Width,
        int Height,
        string ImagePngBase64,
        string ImagePngSha256,
        List<PanelInput> Panels);

    private sealed record PanelInput(string PanelId, int PanelIndex, int[] Bounds);
    private sealed record SourceOutput(string SourceId, string ExpectedSourcePixelSha256,
        string PngSha256, int Width, int Height, List<PanelOutput> Panels);
    private sealed record PanelOutput(string PanelId, int PanelIndex, int[] Bounds,
        string Status, string? Failure, int InputCandidates, int AcceptedCandidates,
        int RejectedCandidates, double? Confidence, bool? NeedsReview,
        IReadOnlyList<string> UncertaintyReasons, IReadOnlyList<TickOutput> XAxisTicks);
    private sealed record TickOutput(string TickId, double[] Center, double[] Line,
        double Confidence, int SupportingCandidates, bool UsableForOcrTickSeed);
    private sealed record SourceFailure(string SourceId, string Status, string Error);
}
