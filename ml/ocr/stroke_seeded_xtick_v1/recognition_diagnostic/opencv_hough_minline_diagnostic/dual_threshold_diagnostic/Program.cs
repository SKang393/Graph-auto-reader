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
        if (args.Length != 1 || args[0] != "--stdio") return 2;
        var decoder = new ProductionRasterFrameDecoder();
        string? inputLine;
        while ((inputLine = await Console.In.ReadLineAsync()) is not null)
        {
            string sourceId = "unknown";
            try
            {
                SourceInput input = JsonSerializer.Deserialize<SourceInput>(inputLine, JsonOptions)
                    ?? throw new InvalidDataException("Empty input.");
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
                byte[] pixels = decoder.Decode(request, CancellationToken.None).CreateAxisFrame().Pixels.ToArray();
                var panels = new List<PanelOutput>();
                foreach (PanelInput panel in input.Panels)
                {
                    int left = panel.Bounds[0], top = panel.Bounds[1];
                    int right = panel.Bounds[2], bottom = panel.Bounds[3];
                    int width = right - left, height = bottom - top;
                    var crop = new byte[checked(width * height)];
                    for (int y = 0; y < height; y++)
                        Buffer.BlockCopy(pixels, (top + y) * input.Width + left, crop, y * width, width);
                    var frame = new GrayscaleLineCandidateFrame(width, height, width, crop);
                    panels.Add(new PanelOutput(panel.PanelId, panel.PanelIndex, panel.Bounds,
                        await Detect(frame, left, top, new OpenCvLineCandidateOptions()),
                        await Detect(frame, left, top, new OpenCvLineCandidateOptions
                        {
                            HoughThreshold = 5,
                            HoughMinimumLineLengthPixels = 5d,
                        })));
                }
                Console.WriteLine(JsonSerializer.Serialize(new SourceOutput(sourceId,
                    input.ExpectedSourcePixelSha256, Hash(png), panels), JsonOptions));
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

    private static async Task<VariantOutput> Detect(GrayscaleLineCandidateFrame frame, int xOffset, int yOffset,
        OpenCvLineCandidateOptions options)
    {
        try
        {
            AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(
                frame, new OpenCvLineCandidateProvider(options));
            var ticks = result.Ticks.Where(t => t.Axis == TickAxis.XAxis).Select(t => new TickOutput(
                t.TickId, [t.Center.X + xOffset, t.Center.Y + yOffset],
                [t.Line.Start.X + xOffset, t.Line.Start.Y + yOffset,
                 t.Line.End.X + xOffset, t.Line.End.Y + yOffset])).ToArray();
            return new VariantOutput("ok", null, result.Diagnostics.InputCandidateCount,
                result.Diagnostics.AcceptedCandidateCount, result.Diagnostics.RejectedCandidateCount,
                result.Confidence, result.Uncertainty.NeedsReview, result.Uncertainty.Reasons, ticks);
        }
        catch (Exception exception)
        {
            return new VariantOutput("failed", exception.GetType().Name + ": " + exception.Message,
                0, 0, 0, null, null, [], []);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }

    private sealed record SourceInput(string SourceId, string ExpectedSourcePixelSha256,
        int Width, int Height, string ImagePngBase64, string ImagePngSha256, List<PanelInput> Panels);
    private sealed record PanelInput(string PanelId, int PanelIndex, int[] Bounds);
    private sealed record SourceOutput(string SourceId, string ExpectedSourcePixelSha256,
        string PngSha256, List<PanelOutput> Panels);
    private sealed record PanelOutput(string PanelId, int PanelIndex, int[] Bounds,
        VariantOutput Default, VariantOutput DualThreshold5);
    private sealed record VariantOutput(string Status, string? Failure, int InputCandidates,
        int AcceptedCandidates, int RejectedCandidates, double? Confidence, bool? NeedsReview,
        IReadOnlyList<string> UncertaintyReasons, IReadOnlyList<TickOutput> XAxisTicks);
    private sealed record TickOutput(string TickId, double[] Center, double[] Line);
    private sealed record SourceFailure(string SourceId, string Error);
}
