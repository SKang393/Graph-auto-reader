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
        if (args.Length != 1 || args[0] != "--stdio") return 2;
        var decoder = new ProductionRasterFrameDecoder();
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            try
            {
                SourceInput input = JsonSerializer.Deserialize<SourceInput>(line, JsonOptions)!;
                byte[] png = Convert.FromBase64String(input.ImagePngBase64);
                Require(Hash(png) == input.ImagePngSha256, "PNG hash mismatch");
                var image = new WorkflowImageEvidence($"memory:{input.SourceId}.png", Hash(png),
                    input.Width, input.Height, WorkflowImageVariant.Original);
                var imported = new WorkflowImportedPanel(Guid.Parse(input.Panels[0].PanelId),
                    Guid.NewGuid(), input.SourceId, image);
                var request = new ProductionWorkflowDetectionRequest(
                    new WorkflowPreparedPanel(imported, image, null), image,
                    WorkflowImageVariant.Original, Guid.NewGuid(), Guid.NewGuid(), png);
                byte[] pixels = decoder.Decode(request, CancellationToken.None).CreateAxisFrame().Pixels.ToArray();
                var results = new List<PanelOutput>();
                foreach (PanelInput panel in input.Panels)
                {
                    int left = panel.Bounds[0], top = panel.Bounds[1];
                    int width = panel.Bounds[2] - left, height = panel.Bounds[3] - top;
                    var crop = new byte[checked(width * height)];
                    for (int y = 0; y < height; y++)
                        Buffer.BlockCopy(pixels, (top + y) * input.Width + left, crop, y * width, width);
                    var frame = new GrayscaleLineCandidateFrame(width, height, width, crop);
                    var provider = new OpenCvLineCandidateProvider();
                    IReadOnlyList<GeometryLineCandidate> candidates = await provider.DetectLinesAsync(frame, CancellationToken.None);
                    var observer = new PairObserver();
                    AxisGeometryResult geometry = await new AxisGeometryDetector().DetectAsync(
                        new AxisGeometryRequest(width, height, candidates) { CandidateObserver = observer });
                    int gateAccepted = candidates.Count(c => IsAcceptedObservation(c, width, height));
                    var dividerIds = geometry.PhaseDividers.SelectMany(item => item.SupportingCandidateIds)
                        .ToHashSet(StringComparer.Ordinal);
                    var selectedAxisIds = geometry.XAxis.SupportingCandidateIds
                        .Concat(geometry.YAxis.SupportingCandidateIds).ToHashSet(StringComparer.Ordinal);
                    var raw = new List<CandidateOutput>();
                    foreach (GeometryLineCandidate c in candidates)
                    {
                        var midpoint = c.Segment.Midpoint;
                        double nearest = double.PositiveInfinity;
                        string? nearestTruth = null;
                        foreach (TruthInput t in panel.VisibleTruths)
                        {
                            double d = PointDistance(new Pixel(midpoint.X, midpoint.Y),
                                new Pixel((t.Line[0] - left + t.Line[2] - left) / 2d,
                                          (t.Line[1] - top + t.Line[3] - top) / 2d));
                            if (d < nearest) { nearest = d; nearestTruth = t.CaseId; }
                        }
                        // Keep only candidates geometrically near a visible truth, to bound report size.
                        if (nearest <= 10d)
                            raw.Add(new CandidateOutput(c.CandidateId, c.Source.ToString(),
                                [c.Segment.Start.X, c.Segment.Start.Y, c.Segment.End.X, c.Segment.End.Y],
                                c.Segment.Length, nearestTruth, nearest, dividerIds.Contains(c.CandidateId),
                                selectedAxisIds.Contains(c.CandidateId)));
                    }
                    results.Add(new PanelOutput(panel.PanelId, panel.PanelIndex, [left, top, left + width, top + height],
                        geometry.Diagnostics.InputCandidateCount, geometry.Diagnostics.AcceptedCandidateCount,
                        geometry.Diagnostics.RejectedCandidateCount, gateAccepted,
                        [geometry.XAxis.Line.Start.X + left, geometry.XAxis.Line.Start.Y + top,
                         geometry.XAxis.Line.End.X + left, geometry.XAxis.Line.End.Y + top],
                        [geometry.PlotPolygon.BottomLeft.X + left, geometry.PlotPolygon.BottomLeft.Y + top,
                         geometry.PlotPolygon.BottomRight.X + left, geometry.PlotPolygon.BottomRight.Y + top],
                        geometry.XAxis.SupportingCandidateIds, geometry.PhaseDividers.SelectMany(d => d.SupportingCandidateIds).ToArray(),
                        observer.TopPairs, raw,
                        geometry.Ticks.Where(t => t.Axis == TickAxis.XAxis)
                            .Select(t => new TickOutput(t.TickId,
                                [t.Center.X + left, t.Center.Y + top],
                                [t.Line.Start.X + left, t.Line.Start.Y + top,
                                 t.Line.End.X + left, t.Line.End.Y + top],
                                t.SupportingCandidateIds)).ToArray()));
                }
                Console.WriteLine(JsonSerializer.Serialize(new SourceOutput(input.SourceId,
                    input.ExpectedSourcePixelSha256, Hash(png), results), JsonOptions));
            }
            catch (Exception e)
            {
                Console.WriteLine(JsonSerializer.Serialize(new Failure(e.GetType().Name + ": " + e.Message), JsonOptions));
            }
            await Console.Out.FlushAsync();
        }
        return 0;
    }

    private static double PointDistance(Pixel p, Pixel q)
    {
        double dx = q.X - p.X, dy = q.Y - p.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
    private static bool IsAcceptedObservation(GeometryLineCandidate candidate, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(candidate.CandidateId) || !candidate.Segment.Start.IsFinite ||
            !candidate.Segment.End.IsFinite || !double.IsFinite(candidate.Strength) ||
            candidate.Strength is <= 0 or > 1 || !double.IsFinite(candidate.StrokeWidthPixels) ||
            candidate.StrokeWidthPixels <= 0 || !IsInside(candidate.Segment.Start, width, height) ||
            !IsInside(candidate.Segment.End, width, height) || candidate.Segment.Length < 1d) return false;
        double dx = candidate.Segment.End.X - candidate.Segment.Start.X;
        double dy = candidate.Segment.End.Y - candidate.Segment.Start.Y;
        double angle = Math.Atan2(Math.Abs(dy), Math.Abs(dx)) * 180d / Math.PI;
        double verticalDeviation = 90d - angle;
        return (angle <= 15d && angle <= verticalDeviation) || verticalDeviation <= 15d;
    }
    private static bool IsInside(PixelPoint p, int width, int height) =>
        p.X >= 0d && p.X <= width - 1d && p.Y >= 0d && p.Y <= height - 1d;
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }

    private sealed class PairObserver : IAxisGeometryCandidateObserver
    {
        public AxisGeometryCandidateSnapshot[] TopPairs { get; private set; } = [];
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) =>
            TopPairs = candidates.Take(3).ToArray();
    }
    private sealed record Pixel(double X, double Y);
    private sealed record SourceInput(string SourceId, string ExpectedSourcePixelSha256, int Width, int Height,
        string ImagePngBase64, string ImagePngSha256, List<PanelInput> Panels);
    private sealed record PanelInput(string PanelId, int PanelIndex, int[] Bounds, List<TruthInput> VisibleTruths);
    private sealed record TruthInput(string CaseId, double[] Line);
    private sealed record SourceOutput(string SourceId, string ExpectedSourcePixelSha256, string PngSha256,
        List<PanelOutput> Panels);
    private sealed record PanelOutput(string PanelId, int PanelIndex, int[] Bounds, int InputCandidates,
        int AcceptedCandidates, int RejectedCandidates, int ReimplementedAcceptedCandidates,
        double[] SelectedXAxisLine, double[] PlotBaseline,
        IReadOnlyList<string> SelectedXAxisSupportIds, IReadOnlyList<string> DividerSupportIds,
        IReadOnlyList<AxisGeometryCandidateSnapshot> TopAxisPairs, IReadOnlyList<CandidateOutput> NearbyCandidates,
        IReadOnlyList<TickOutput> XAxisTicks);
    private sealed record CandidateOutput(string CandidateId, string Source, double[] Line, double Length,
        string? NearestTruthCaseId, double MidpointDistanceToTruthCenter, bool DividerMember, bool SelectedAxisMember);
    private sealed record TickOutput(string TickId, double[] Center, double[] Line,
        IReadOnlyList<string> SupportingCandidateIds);
    private sealed record Failure(string Error);
}
