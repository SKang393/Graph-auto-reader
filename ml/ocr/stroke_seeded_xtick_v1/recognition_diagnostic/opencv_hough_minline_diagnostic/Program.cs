// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
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
            Console.Error.WriteLine("usage: AxisHoughMinLineDiagnostic --stdio");
            return 2;
        }

        var decoder = new ProductionRasterFrameDecoder();
        string? inputLine;
        while ((inputLine = await Console.In.ReadLineAsync()) is not null)
        {
            string sourceId = "unknown";
            try
            {
                SourceInput input = JsonSerializer.Deserialize<SourceInput>(inputLine, JsonOptions)
                    ?? throw new InvalidDataException("Empty source input.");
                sourceId = input.SourceId;
                byte[] png = Convert.FromBase64String(input.ImagePngBase64);
                Require(Hash(png) == input.ImagePngSha256, "PNG transport hash mismatch.");
                var image = new WorkflowImageEvidence($"memory:{sourceId}.png", Hash(png),
                    input.Width, input.Height, WorkflowImageVariant.Original);
                var imported = new WorkflowImportedPanel(Guid.Parse(input.Panels[0].PanelId),
                    Guid.NewGuid(), sourceId, image);
                var request = new ProductionWorkflowDetectionRequest(
                    new WorkflowPreparedPanel(imported, image, null), image,
                    WorkflowImageVariant.Original, Guid.NewGuid(), Guid.NewGuid(), png);
                GrayscaleLineCandidateFrame full = decoder.Decode(request, CancellationToken.None).CreateAxisFrame();
                byte[] pixels = full.Pixels.ToArray();
                var panelResults = new List<PanelResult>(input.Panels.Count);
                foreach (PanelInput panel in input.Panels)
                {
                    int left = Math.Clamp(panel.Bounds[0], 0, input.Width - 1);
                    int top = Math.Clamp(panel.Bounds[1], 0, input.Height - 1);
                    int right = Math.Clamp(panel.Bounds[2], left + 1, input.Width);
                    int bottom = Math.Clamp(panel.Bounds[3], top + 1, input.Height);
                    int width = right - left, height = bottom - top;
                    var cropped = new byte[checked(width * height)];
                    for (int y = 0; y < height; y++)
                        Buffer.BlockCopy(pixels, (top + y) * input.Width + left, cropped, y * width, width);
                    var frame = new GrayscaleLineCandidateFrame(width, height, width, cropped);
                    panelResults.Add(new PanelResult(panel.PanelId, panel.PanelIndex, [left, top, right, bottom],
                        await Detect(frame, left, top, new OpenCvLineCandidateOptions()),
                        await Detect(frame, left, top, new OpenCvLineCandidateOptions { HoughMinimumLineLengthPixels = 5d })));
                }
                Console.WriteLine(JsonSerializer.Serialize(new SourceResult(sourceId,
                    input.ExpectedSourcePixelSha256, Hash(png), input.Width, input.Height, panelResults), JsonOptions));
            }
            catch (Exception exception)
            {
                Console.WriteLine(JsonSerializer.Serialize(new SourceFailure(sourceId,
                    exception.GetType().Name + ": " + exception.Message), JsonOptions));
            }
            await Console.Out.FlushAsync();
        }
        return 0;
    }

    private static async Task<RunResult> Detect(GrayscaleLineCandidateFrame frame, int xOffset, int yOffset,
        OpenCvLineCandidateOptions providerOptions)
    {
        try
        {
            AxisGeometryResult geometry = await new AxisGeometryDetector().DetectAsync(
                frame, new OpenCvLineCandidateProvider(providerOptions));
            TickResult[] ticks = geometry.Ticks.Where(static tick => tick.Axis == TickAxis.XAxis)
                .Select(tick => ToTick(tick, xOffset, yOffset)).ToArray();
            return new RunResult("ok", null, geometry.Diagnostics.InputCandidateCount,
                geometry.Diagnostics.AcceptedCandidateCount, geometry.Diagnostics.RejectedCandidateCount,
                geometry.Confidence, geometry.Uncertainty.NeedsReview, geometry.Uncertainty.Reasons, ticks);
        }
        catch (Exception exception)
        {
            return new RunResult("failed", exception.GetType().Name + ": " + exception.Message,
                0, 0, 0, null, null, [], []);
        }
    }

    private static TickResult ToTick(AxisTickGeometry tick, int xOffset, int yOffset)
    {
        bool usable = !string.IsNullOrWhiteSpace(tick.TickId) &&
            double.IsFinite(tick.Center.X) && double.IsFinite(tick.Center.Y) &&
            double.IsFinite(tick.Line.Start.X) && double.IsFinite(tick.Line.Start.Y) &&
            double.IsFinite(tick.Line.End.X) && double.IsFinite(tick.Line.End.Y) &&
            (tick.Line.Start.X != tick.Line.End.X || tick.Line.Start.Y != tick.Line.End.Y) &&
            double.IsFinite(tick.Confidence) && tick.Confidence is > 0d and <= 1d &&
            tick.SupportingCandidateIds is { Count: > 0 } &&
            tick.SupportingCandidateIds.All(static id => !string.IsNullOrWhiteSpace(id));
        return new TickResult(tick.TickId,
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

    private sealed record SourceInput(string SourceId, string ExpectedSourcePixelSha256,
        int Width, int Height, string ImagePngBase64, string ImagePngSha256, List<PanelInput> Panels);
    private sealed record PanelInput(string PanelId, int PanelIndex, int[] Bounds);
    private sealed record SourceResult(string SourceId, string ExpectedSourcePixelSha256,
        string PngSha256, int Width, int Height, List<PanelResult> Panels);
    private sealed record PanelResult(string PanelId, int PanelIndex, int[] Bounds,
        RunResult DefaultHough12, RunResult DiagnosticHough5);
    private sealed record RunResult(string Status, string? Failure, int InputCandidates,
        int AcceptedCandidates, int RejectedCandidates, double? Confidence, bool? NeedsReview,
        IReadOnlyList<string> UncertaintyReasons, IReadOnlyList<TickResult> XAxisTicks);
    private sealed record TickResult(string TickId, double[] Center, double[] Line,
        double Confidence, int SupportingCandidates, bool UsableForOcrTickSeed);
    private sealed record SourceFailure(string SourceId, string Error);
}
