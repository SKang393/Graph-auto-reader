// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;

internal static class Program
{
    private const byte MaximumInkGray = 200;
    private const int AxisCoreExclusionPixels = 2;
    private const int ScanRadiusPixels = 12;
    private const int EdgeGuardPixels = 4;
    private const int MaximumClusterWidthPixels = 8;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private static readonly JsonSerializerOptions SelfTestJsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
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
                byte[] fullPixels = decoder.Decode(request, CancellationToken.None).CreateAxisFrame().Pixels.ToArray();
                var panelResults = new List<PanelOutput>();
                foreach (PanelInput panel in input.Panels)
                {
                    int left = panel.Bounds[0], top = panel.Bounds[1];
                    int width = panel.Bounds[2] - left, height = panel.Bounds[3] - top;
                    var pixels = new byte[checked(width * height)];
                    for (int y = 0; y < height; y++)
                        Buffer.BlockCopy(fullPixels, (top + y) * input.Width + left, pixels, y * width, width);
                    var frame = new GrayscaleLineCandidateFrame(width, height, width, pixels);
                    AxisGeometryResult geometry = await new AxisGeometryDetector().DetectAsync(
                        frame, new OpenCvLineCandidateProvider());
                    IReadOnlyList<TickProposal> proposals = Scan(frame, geometry.XAxis.Line);
                    var defaultTicks = geometry.Ticks.Where(t => t.Axis == TickAxis.XAxis)
                        .Select(t => ToTick(t, left, top)).ToArray();
                    panelResults.Add(new PanelOutput(panel.PanelId, panel.PanelIndex, panel.Bounds,
                        geometry.Diagnostics.InputCandidateCount, geometry.Diagnostics.AcceptedCandidateCount,
                        geometry.Diagnostics.RejectedCandidateCount, geometry.Confidence,
                        geometry.Uncertainty.NeedsReview, geometry.Uncertainty.Reasons,
                        [geometry.XAxis.Line.Start.X + left, geometry.XAxis.Line.Start.Y + top,
                         geometry.XAxis.Line.End.X + left, geometry.XAxis.Line.End.Y + top],
                        defaultTicks, proposals.Select(p => ToProposal(p, left, top)).ToArray()));
                }
                Console.WriteLine(JsonSerializer.Serialize(new SourceOutput(sourceId,
                    input.ExpectedSourcePixelSha256, Hash(png), panelResults), JsonOptions));
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

    private static ReadOnlyCollection<TickProposal> Scan(GrayscaleLineCandidateFrame frame, GeometryLineSegment axis)
    {
        double dx = axis.End.X - axis.Start.X, dy = axis.End.Y - axis.Start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length <= 2 * EdgeGuardPixels)
            return Array.AsReadOnly(Array.Empty<TickProposal>());
        double ux = dx / length, uy = dy / length;
        if (ux < 0) { ux = -ux; uy = -uy; }
        double nx = -uy, ny = ux;
        if (ny < 0) { nx = -nx; ny = -ny; }
        var hits = new List<ScanHit>();
        for (int along = EdgeGuardPixels; along <= length - EdgeGuardPixels; along++)
        {
            int darkCount = 0, longestRun = 0, run = 0;
            bool previousDark = false;
            for (int offset = -ScanRadiusPixels; offset <= ScanRadiusPixels; offset++)
            {
                if (Math.Abs(offset) <= AxisCoreExclusionPixels)
                {
                    run = 0; previousDark = false;
                    continue;
                }
                int x = (int)Math.Round(axis.Start.X + ux * along + nx * offset);
                int y = (int)Math.Round(axis.Start.Y + uy * along + ny * offset);
                bool dark = x >= 0 && x < frame.Width && y >= 0 && y < frame.Height &&
                    frame.Pixels.Span[(y * frame.Stride) + x] < MaximumInkGray;
                if (dark)
                {
                    darkCount++;
                    run = previousDark ? run + 1 : 1;
                    longestRun = Math.Max(longestRun, run);
                }
                else run = 0;
                previousDark = dark;
            }
            if (darkCount >= 3 && longestRun >= 2)
                hits.Add(new ScanHit(along, darkCount, longestRun));
        }

        var clusters = new List<List<ScanHit>>();
        foreach (ScanHit hit in hits)
        {
            if (clusters.Count == 0 || hit.Along - clusters[^1][^1].Along > 1)
                clusters.Add([]);
            clusters[^1].Add(hit);
        }
        var proposals = new List<TickProposal>();
        foreach (List<ScanHit> cluster in clusters)
        {
            int clusterWidth = cluster[^1].Along - cluster[0].Along + 1;
            if (clusterWidth > MaximumClusterWidthPixels) continue;
            double along = cluster[cluster.Count / 2].Along;
            var darkOffsets = new List<int>();
            for (int offset = -ScanRadiusPixels; offset <= ScanRadiusPixels; offset++)
            {
                if (Math.Abs(offset) <= AxisCoreExclusionPixels) continue;
                int x = (int)Math.Round(axis.Start.X + ux * along + nx * offset);
                int y = (int)Math.Round(axis.Start.Y + uy * along + ny * offset);
                if (x >= 0 && x < frame.Width && y >= 0 && y < frame.Height &&
                    frame.Pixels.Span[(y * frame.Stride) + x] < MaximumInkGray)
                    darkOffsets.Add(offset);
            }
            if (darkOffsets.Count < 3) continue;
            int startOffset = Math.Min(0, darkOffsets.Min());
            int endOffset = Math.Max(0, darkOffsets.Max());
            if (endOffset - startOffset < 4) continue;
            var start = new PixelPoint(axis.Start.X + ux * along + nx * startOffset,
                axis.Start.Y + uy * along + ny * startOffset);
            var end = new PixelPoint(axis.Start.X + ux * along + nx * endOffset,
                axis.Start.Y + uy * along + ny * endOffset);
            proposals.Add(new TickProposal(
                new PixelPoint((start.X + end.X) / 2, (start.Y + end.Y) / 2),
                new GeometryLineSegment(start, end), clusterWidth,
                cluster.Sum(hit => hit.DarkCount), cluster.Max(hit => hit.LongestRun)));
        }
        return proposals.AsReadOnly();
    }

    private static int SelfTest()
    {
        var results = new Dictionary<string, int>();
        var axis = new GeometryLineSegment(new PixelPoint(10, 40), new PixelPoint(90, 40));
        results["blank_axis"] = Scan(Fixture(100, 60, axis, []), axis).Count;
        results["horizontal_marker"] = Scan(Fixture(100, 60, axis, [new Stroke(15, 30, 85, 30)]), axis).Count;
        results["thin_horizontal_grid"] = Scan(Fixture(100, 60, axis, [new Stroke(10, 29, 90, 29)]), axis).Count;
        Stroke[] thickBorder = Enumerable.Range(0, 6).Select(x => new Stroke(10 + x, 34, 10 + x, 40)).ToArray();
        results["thick_axis_border_confuser"] = Scan(Fixture(100, 60, axis, thickBorder), axis).Count;
        results["crossing_gridline_confuser"] = Scan(Fixture(100, 60, axis, [new Stroke(50, 28, 50, 52)]), axis).Count;
        results["nearby_text_stroke_confuser"] = Scan(Fixture(100, 60, axis,
            [new Stroke(68, 43, 68, 51), new Stroke(66, 43, 68, 43)]), axis).Count;
        results["short_tick"] = Scan(Fixture(100, 60, axis, [new Stroke(40, 36, 40, 44)]), axis).Count;
        Require(results["blank_axis"] == 0 && results["horizontal_marker"] == 0 &&
            results["thin_horizontal_grid"] == 0,
            "A non-crossing hard negative produced a proposal.");
        Require(results["thick_axis_border_confuser"] > 0 && results["crossing_gridline_confuser"] > 0 &&
            results["nearby_text_stroke_confuser"] > 0,
            "Expected pixel-based grid/text confusers were not surfaced.");
        Require(results["short_tick"] == 1, "One isolated short tick should produce one proposal.");
        Console.WriteLine(JsonSerializer.Serialize(results, SelfTestJsonOptions));
        return 0;
    }

    private static GrayscaleLineCandidateFrame Fixture(int width, int height,
        GeometryLineSegment axis, IReadOnlyList<Stroke> strokes)
    {
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        Draw(pixels, width, axis.Start, axis.End);
        foreach (Stroke stroke in strokes)
            Draw(pixels, width, new PixelPoint(stroke.X0, stroke.Y0), new PixelPoint(stroke.X1, stroke.Y1));
        return new GrayscaleLineCandidateFrame(width, height, width, pixels);
    }

    private static void Draw(byte[] pixels, int width, PixelPoint start, PixelPoint end)
    {
        int x0 = (int)start.X, y0 = (int)start.Y, x1 = (int)end.X, y1 = (int)end.Y;
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1, error = dx + dy;
        while (true)
        {
            pixels[y0 * width + x0] = 0;
            if (x0 == x1 && y0 == y1) break;
            int twice = 2 * error;
            if (twice >= dy) { error += dy; x0 += sx; }
            if (twice <= dx) { error += dx; y0 += sy; }
        }
    }

    private static TickOutput ToTick(AxisTickGeometry tick, int xOffset, int yOffset) => new(
        tick.TickId, [tick.Center.X + xOffset, tick.Center.Y + yOffset],
        [tick.Line.Start.X + xOffset, tick.Line.Start.Y + yOffset,
         tick.Line.End.X + xOffset, tick.Line.End.Y + yOffset], IsUsable(tick));

    private static ProposalOutput ToProposal(TickProposal proposal, int xOffset, int yOffset) => new(
        [proposal.Center.X + xOffset, proposal.Center.Y + yOffset],
        [proposal.Line.Start.X + xOffset, proposal.Line.Start.Y + yOffset,
         proposal.Line.End.X + xOffset, proposal.Line.End.Y + yOffset],
        proposal.ClusterWidthPixels, proposal.DarkPixelCount, proposal.LongestDarkRun);

    private static bool IsUsable(AxisTickGeometry tick) =>
        !string.IsNullOrWhiteSpace(tick.TickId) && tick.Center.IsFinite && tick.Line.Start.IsFinite &&
        tick.Line.End.IsFinite && tick.Line.Start != tick.Line.End && double.IsFinite(tick.Confidence) &&
        tick.Confidence is > 0 and <= 1 && tick.SupportingCandidateIds.Count > 0 &&
        tick.SupportingCandidateIds.All(id => !string.IsNullOrWhiteSpace(id));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed record ScanHit(int Along, int DarkCount, int LongestRun);
    private sealed record TickProposal(PixelPoint Center, GeometryLineSegment Line,
        int ClusterWidthPixels, int DarkPixelCount, int LongestDarkRun);
    private sealed record Stroke(int X0, int Y0, int X1, int Y1);
    private sealed record SourceInput(string SourceId, string ExpectedSourcePixelSha256,
        int Width, int Height, string ImagePngBase64, string ImagePngSha256, List<PanelInput> Panels);
    private sealed record PanelInput(string PanelId, int PanelIndex, int[] Bounds);
    private sealed record SourceOutput(string SourceId, string ExpectedSourcePixelSha256,
        string PngSha256, List<PanelOutput> Panels);
    private sealed record PanelOutput(string PanelId, int PanelIndex, int[] Bounds,
        int InputCandidates, int AcceptedCandidates, int RejectedCandidates, double Confidence,
        bool NeedsReview, IReadOnlyList<string> UncertaintyReasons, double[] SelectedXAxisLine,
        IReadOnlyList<TickOutput> DefaultXAxisTicks, IReadOnlyList<ProposalOutput> InkProposals);
    private sealed record TickOutput(string TickId, double[] Center, double[] Line, bool UsableForOcrTickSeed);
    private sealed record ProposalOutput(double[] Center, double[] Line, int ClusterWidthPixels,
        int DarkPixelCount, int LongestDarkRun);
    private sealed record SourceFailure(string SourceId, string Error);
}
