// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Separates image-backed legend samples from plotted observations.</summary>
internal static class ProductionLegendSymbolInputs
{
    internal const string Version = "original-pixel-legend-symbol-inputs-v1";

    internal static LegendSymbolInputBatch Prepare(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster,
        OcrResult ocr,
        IReadOnlyList<MarkerCenter> plotCandidates,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        IReadOnlyList<FramedLegendRoleEvidence> found = FramedLegendRoleResolver.LocateSymbols(
            raster.CreateOcrImage(), ocr.Regions, cancellationToken);
        var inputs = plotCandidates.ToList();
        var symbolIds = new HashSet<string>(StringComparer.Ordinal);
        var symbolCropIds = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (FramedLegendRoleEvidence evidence in found
            .OrderBy(static item => item.RegionId, StringComparer.Ordinal)
            .DistinctBy(static item => item.GlyphBounds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle glyph = evidence.GlyphBounds;
            foreach (MarkerCenter candidate in plotCandidates)
            {
                if (candidate.Center.X >= glyph.Left && candidate.Center.X < glyph.Right &&
                    candidate.Center.Y >= glyph.Top && candidate.Center.Y < glyph.Bottom)
                    symbolIds.Add(candidate.MarkerId);
            }
            string geometry = string.Create(CultureInfo.InvariantCulture,
                $"{glyph.Left:R},{glyph.Top:R},{glyph.Width:R},{glyph.Height:R}");
            double radius = Math.Max(glyph.Width, glyph.Height) / 2;
            MarkerCenter? existing = plotCandidates.FirstOrDefault(candidate =>
                candidate.Center.X == glyph.Center.X && candidate.Center.Y == glyph.Center.Y && candidate.Radius == radius);
            string id = existing?.MarkerId ?? ProductionWorkflowPanelStore.CreateStableId(
                request.Panel.ImportedPanel.PanelId.ToString("D"), request.Image.Sha256,
                Version, geometry).ToString("D");
            if (existing is null && inputs.Any(marker => string.Equals(marker.MarkerId, id, StringComparison.Ordinal)))
                throw new InvalidOperationException("Legend symbol identity collides with a detector input.");
            OcrRegion text = ocr.Regions.Single(region => region.RegionId == evidence.RegionId);
            if (existing is null)
                inputs.Add(new MarkerCenter(id, new MarkerPoint(glyph.Center.X, glyph.Center.Y),
                    radius, 0, Math.Min(text.Confidence, 0.70), MarkerSourceImage.Original));
            symbolIds.Add(id);
            symbolCropIds.Add(id);
            warnings.Add($"legend_symbol_original_pixels:{id}:{evidence.RegionId}:{geometry}");
        }
        timer.Stop();
        WorkflowVisionEnvelope? envelope = warnings.Count == 0 ? null : new WorkflowVisionEnvelope(
            1, request.RunId, request.ProjectId, request.Panel.ImportedPanel.PanelId,
            "markers", Version, request.Image.Sha256, null,
            new WorkflowVisionTiming(timer.Elapsed.TotalMilliseconds, 0, 0, timer.Elapsed.TotalMilliseconds),
            0.70, warnings, request.Transforms);
        return new(Array.AsReadOnly(inputs.ToArray()), symbolIds.ToFrozenSet(StringComparer.Ordinal),
            symbolCropIds.ToFrozenSet(StringComparer.Ordinal), envelope);
    }
}

internal sealed record LegendSymbolInputBatch(
    IReadOnlyList<MarkerCenter> ClassifierInputs,
    IReadOnlySet<string> SymbolInputIds,
    IReadOnlySet<string> SymbolCropInputIds,
    WorkflowVisionEnvelope? Envelope);
