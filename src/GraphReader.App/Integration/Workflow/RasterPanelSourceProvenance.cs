// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.Pdf;

namespace GraphReader.App.Integration.Workflow;

internal sealed record RetainedRasterPanelReference(
    Guid PanelId,
    string DisplayName,
    string PanelImageSha256,
    int SourceWidth,
    int SourceHeight,
    PdfRectD RequestedCropInSourcePixels,
    PdfRectD EncodedCropInSourcePixels);

internal sealed class RetainedRasterSourceRequest
{
    public RetainedRasterSourceRequest(string sourceSha256, IEnumerable<RetainedRasterPanelReference> panels)
    {
        WorkflowContractGuards.RequireSha256(sourceSha256, nameof(sourceSha256));
        ArgumentNullException.ThrowIfNull(panels);
        SourceSha256 = sourceSha256.ToLowerInvariant();
        Panels = Array.AsReadOnly(panels.ToArray());
    }
    public string SourceSha256 { get; }
    public IReadOnlyList<RetainedRasterPanelReference> Panels { get; }
}

/// <summary>One shared, immutable copy of the full imported raster file.</summary>
public sealed class RasterSourceImageEvidence
{
    private readonly ImmutableByteBuffer bytes;

    public RasterSourceImageEvidence(WorkflowImageEvidence image, ImmutableByteBuffer bytes)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(bytes);
        if (image.Variant != WorkflowImageVariant.Original ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())),
                image.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Full raster source bytes must match their immutable original identity.");
        }
        Image = image;
        this.bytes = bytes;
    }

    public WorkflowImageEvidence Image { get; }
    public byte[] CopyBytes() => bytes.ToArray();
}

/// <summary>
/// Binds a detector's immutable panel image to the full source raster. Vision
/// and canvas coordinates are panel-local; persisted original coordinates use
/// this explicit translation. No PDF document or page identity is introduced.
/// </summary>
public sealed class RasterPanelSourceProvenance : IEquatable<RasterPanelSourceProvenance>
{
    public RasterPanelSourceProvenance(
        RasterSourceImageEvidence source,
        string panelImageSha256,
        PdfRectD requestedCropInSourcePixels,
        PdfRectD encodedCropInSourcePixels)
    {
        ArgumentNullException.ThrowIfNull(source);
        WorkflowContractGuards.RequireSha256(panelImageSha256, nameof(panelImageSha256));
        PdfRectD crop = encodedCropInSourcePixels;
        PdfRectD requested = requestedCropInSourcePixels;
        if (!crop.IsValid || !requested.IsValid ||
            crop.X < 0 || crop.Y < 0 || crop.Right > source.Image.Width || crop.Bottom > source.Image.Height ||
            crop.X != Math.Floor(crop.X) || crop.Y != Math.Floor(crop.Y) ||
            crop.Width != Math.Floor(crop.Width) || crop.Height != Math.Floor(crop.Height) ||
            requested.X < crop.X || requested.Y < crop.Y ||
            requested.Right > crop.Right || requested.Bottom > crop.Bottom)
        {
            throw new ArgumentException("Raster crop provenance must use a bounded actual integer crop containing the requested crop.");
        }
        Source = source;
        PanelImageSha256 = panelImageSha256.ToLowerInvariant();
        RequestedCropInSourcePixels = requested;
        EncodedCropInSourcePixels = crop;
        SourceToPanelMatrix = Array.AsReadOnly(new[] { 1d, 0d, -crop.X, 0d, 1d, -crop.Y, 0d, 0d, 1d });
        PanelToSourceMatrix = Array.AsReadOnly(new[] { 1d, 0d, crop.X, 0d, 1d, crop.Y, 0d, 0d, 1d });
    }

    public RasterSourceImageEvidence Source { get; }
    public string PanelImageSha256 { get; }
    public PdfRectD RequestedCropInSourcePixels { get; }
    public PdfRectD EncodedCropInSourcePixels { get; }
    public IReadOnlyList<double> SourceToPanelMatrix { get; }
    public IReadOnlyList<double> PanelToSourceMatrix { get; }

    public PdfPointD MapPanelPixelToSource(PdfPointD point)
    {
        if (!point.IsFinite || point.X < 0 || point.Y < 0 ||
            point.X > EncodedCropInSourcePixels.Width || point.Y > EncodedCropInSourcePixels.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(point), "Panel coordinates must lie within the retained crop.");
        }
        return new PdfPointD(point.X + EncodedCropInSourcePixels.X, point.Y + EncodedCropInSourcePixels.Y);
    }

    public PdfPointD MapSourcePixelToPanel(PdfPointD point)
    {
        PdfRectD crop = EncodedCropInSourcePixels;
        if (!point.IsFinite || point.X < crop.X || point.Y < crop.Y || point.X > crop.Right || point.Y > crop.Bottom)
        {
            throw new ArgumentOutOfRangeException(nameof(point), "Source coordinates must lie within the retained panel crop.");
        }
        return new PdfPointD(point.X - crop.X, point.Y - crop.Y);
    }

    public bool Equals(RasterPanelSourceProvenance? other) =>
        other is not null &&
        Source.Image.Reference == other.Source.Image.Reference &&
        Source.Image.Sha256 == other.Source.Image.Sha256 &&
        Source.Image.Width == other.Source.Image.Width && Source.Image.Height == other.Source.Image.Height &&
        PanelImageSha256 == other.PanelImageSha256 &&
        RequestedCropInSourcePixels == other.RequestedCropInSourcePixels &&
        EncodedCropInSourcePixels == other.EncodedCropInSourcePixels;

    public override bool Equals(object? obj) => Equals(obj as RasterPanelSourceProvenance);
    public override int GetHashCode() => HashCode.Combine(Source.Image.Reference, Source.Image.Sha256, Source.Image.Width,
        Source.Image.Height, PanelImageSha256, RequestedCropInSourcePixels, EncodedCropInSourcePixels);
}
