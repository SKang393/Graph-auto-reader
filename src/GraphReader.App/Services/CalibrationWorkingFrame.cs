// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration;
using GraphReader.Axis;
using GraphReader.Imaging;

namespace GraphReader.App.Services;

/// <summary>Transient derived-pixel evidence. Its inverse returns panel pixels, never full-source pixels.</summary>
internal sealed class CalibrationWorkingFrame
{
    private CalibrationWorkingFrame(AffineRasterRectificationResult raster, int originalWidth, int originalHeight,
        string originalHash, string derivedHash, string binding)
    {
        Width = raster.Width; Height = raster.Height;
        OriginalWidth = originalWidth; OriginalHeight = originalHeight;
        OriginalImageSha256 = originalHash; DerivedImageSha256 = derivedHash;
        TransformChain = raster.TransformChain;
        Geometry = new(raster.Left, raster.Bottom, raster.Right, raster.Top, 0);
        Binding = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Version = "calibration-affine-gray8-v1", OriginalBinding = binding, originalHash, derivedHash,
            originalWidth, originalHeight, Width, Height, raster.Left, raster.Bottom, raster.Right, raster.Top,
            TransformChain.OriginalToDerived, TransformChain.DerivedToOriginal,
        }))));
    }

    internal int Width { get; }
    internal int Height { get; }
    internal int OriginalWidth { get; }
    internal int OriginalHeight { get; }
    internal string OriginalImageSha256 { get; }
    internal string DerivedImageSha256 { get; }
    internal string Binding { get; }
    internal TransformChain TransformChain { get; }
    internal RustAxisGeometry Geometry { get; }

    internal static (CalibrationWorkingFrame Frame, byte[] Image) Create(byte[] original, int width, int height,
        RustAffineAxisGeometry axes, string binding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216 ||
            original.Length is 0 or > 32 * 1024 * 1024)
            throw new ArgumentException("Unsupported calibration raster.", nameof(original));
        using var source = new MemoryStream(original, writable: false);
        BitmapFrame decoded = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (decoded.PixelWidth != width || decoded.PixelHeight != height)
            throw new ArgumentException("Calibration raster dimensions changed.", nameof(original));
        var gray = new FormatConvertedBitmap(WhiteBackgroundRaster.Normalize(decoded, token), PixelFormats.Gray8, null, 0);
        byte[] pixels = new byte[checked(width * height)];
        gray.CopyPixels(pixels, width, 0);
        AffineRasterRectificationResult raster = AffineRasterRectifier.Rectify(pixels, width, height, width,
            new(axes.Origin.X, axes.Origin.Y), new(axes.XEnd.X, axes.XEnd.Y), new(axes.YEnd.X, axes.YEnd.Y), token);
        token.ThrowIfCancellationRequested();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(raster.Width, raster.Height, 96, 96,
            PixelFormats.Gray8, null, raster.Pixels.ToArray(), raster.Stride)));
        using var output = new MemoryStream();
        encoder.Save(output);
        token.ThrowIfCancellationRequested();
        byte[] image = output.ToArray();
        if (image.Length > 32 * 1024 * 1024) throw new ArgumentException("Rectified image exceeds the worker limit.", nameof(original));
        return (new(raster, width, height, Convert.ToHexStringLower(SHA256.HashData(original)),
            Convert.ToHexStringLower(SHA256.HashData(image)), binding), image);
    }

    internal CalibrationOcrPoint ToOriginal(CalibrationOcrPoint point)
    {
        GraphReader.Imaging.PixelPoint mapped = TransformChain.ToOriginal(new(point.X, point.Y));
        return new(mapped.X, mapped.Y);
    }

    internal CalibrationOcrPoint ToDerived(CalibrationOcrPoint point)
    {
        GraphReader.Imaging.PixelPoint mapped = TransformChain.ToDerived(new(point.X, point.Y));
        return new(mapped.X, mapped.Y);
    }

    internal bool ContainsOriginal(CalibrationOcrPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        point.X >= 0 && point.Y >= 0 && point.X <= OriginalWidth - 1 && point.Y <= OriginalHeight - 1;

    // OCR polygons describe region edges; unlike anchor centers they may reach Width/Height.
    internal bool ContainsOriginalEdge(CalibrationOcrPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        point.X >= 0 && point.Y >= 0 && point.X <= OriginalWidth && point.Y <= OriginalHeight;

    internal CalibrationTickAnchors ToOriginal(CalibrationTickAnchors anchors) =>
        new(ToOriginal(anchors.FirstSessionY0), ToOriginal(anchors.FirstSessionYMaximum), ToOriginal(anchors.FinalSessionY0));
}
