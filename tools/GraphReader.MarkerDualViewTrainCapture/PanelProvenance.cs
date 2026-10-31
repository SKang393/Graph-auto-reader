// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Security.Cryptography;
using GraphReader.App.Integration.Workflow;

namespace GraphReader.RealAcceptance.Ocr;

internal static class PanelProvenance
{
    internal static object Validate(ProductionPanelEvidence panel, string sourceSha, string panelSha)
    {
        RasterPanelSourceProvenance p = panel.RasterPanelSource ??
            throw new InvalidDataException("Raster panel source provenance is missing.");
        string sourceActual = Convert.ToHexStringLower(SHA256.HashData(p.Source.CopyBytes()));
        string panelActual = Convert.ToHexStringLower(SHA256.HashData(panel.CopyOriginalBytes()));
        var crop = p.EncodedCropInSourcePixels;
        double[] forward = [1, 0, -crop.X, 0, 1, -crop.Y, 0, 0, 1];
        double[] inverse = [1, 0, crop.X, 0, 1, crop.Y, 0, 0, 1];
        if (!string.Equals(sourceActual, sourceSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(p.Source.Image.Sha256, sourceSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(panelActual, panelSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(p.PanelImageSha256, panelSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(panel.Panel.Original.Sha256, panelSha, StringComparison.OrdinalIgnoreCase) ||
            crop.Width != panel.Panel.Original.Width || crop.Height != panel.Panel.Original.Height ||
            !p.SourceToPanelMatrix.SequenceEqual(forward) || !p.PanelToSourceMatrix.SequenceEqual(inverse))
            throw new InvalidDataException("Source, panel, crop or transform identity differs.");
        return new
        {
            source_sha256 = sourceActual, panel_image_sha256 = panelActual,
            source_width = p.Source.Image.Width, source_height = p.Source.Image.Height,
            requested_crop_source_pixels = p.RequestedCropInSourcePixels,
            encoded_crop_source_pixels = crop,
            source_to_panel_matrix = p.SourceToPanelMatrix,
            panel_to_source_matrix = p.PanelToSourceMatrix,
            identity = crop.X == 0 && crop.Y == 0 && crop.Width == p.Source.Image.Width && crop.Height == p.Source.Image.Height,
        };
    }
}
