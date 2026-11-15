// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration;
using GraphReader.Axis;

namespace GraphReader.App.Services;

public sealed record AxisTickMeasurementResult(string PanelImageSha256, int Width, int Height,
    RustAxisTickEvidence? Evidence, string? ErrorCode);

public interface IAxisTickMeasurementService
{
    Task<AxisTickMeasurementResult> MeasureAsync(byte[] originalImage, RustAxisGeometry geometry,
        CancellationToken cancellationToken);
}

/// <summary>Measures pixel strokes at an exact review box without selecting new geometry.</summary>
public sealed class RustAxisTickMeasurementService(string workerPath) : IAxisTickMeasurementService
{
    public Task<AxisTickMeasurementResult> MeasureAsync(byte[] originalImage, RustAxisGeometry geometry,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<AxisTickMeasurementResult>(cancellationToken);
        if (originalImage is null || originalImage.Length is 0 or > 32 * 1024 * 1024 || geometry is null)
            return Task.FromResult(new AxisTickMeasurementResult(string.Empty, 0, 0, null, "INVALID_INPUT"));
        byte[] snapshot = (byte[])originalImage.Clone();
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string hash = Convert.ToHexStringLower(SHA256.HashData(snapshot));
            try
            {
                using var stream = new MemoryStream(snapshot, writable: false);
                BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad).Frames[0];
                int width = frame.PixelWidth, height = frame.PixelHeight;
                if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216)
                    return new AxisTickMeasurementResult(hash, width, height, null, "INVALID_INPUT");
                var gray = new FormatConvertedBitmap(WhiteBackgroundRaster.Normalize(frame, cancellationToken),
                    PixelFormats.Gray8, null, 0);
                byte[] pixels = new byte[checked(width * height)];
                gray.CopyPixels(pixels, width, 0);
                RustAxisTickMeasurementResult result = await RustAxisTickMeasurement.RunAsync(workerPath,
                    width, height, pixels, geometry, cancellationToken: cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new AxisTickMeasurementResult(hash, width, height, result.Evidence, result.ErrorCode);
            }
            catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or FormatException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new AxisTickMeasurementResult(hash, 0, 0, null, "TICK_IMAGE_DECODE_FAILED");
            }
        }, cancellationToken);
    }
}
