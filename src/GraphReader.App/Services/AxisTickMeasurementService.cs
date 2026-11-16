// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration;
using GraphReader.Axis;

namespace GraphReader.App.Services;

public sealed record AxisTickMeasurementResult(string PanelImageSha256, int Width, int Height,
    RustAxisTickEvidence? Evidence, string? ErrorCode)
{
    public string? OriginalSupportBinding { get; init; }
}

public interface IAxisTickMeasurementService
{
    Task<AxisTickMeasurementResult> MeasureAsync(byte[] originalImage, RustAxisGeometry geometry,
        CancellationToken cancellationToken);
    // Existing adapters retain strict behavior unless they explicitly implement the manual path.
    Task<AxisTickMeasurementResult> MeasureManualPartialAsync(byte[] originalImage, RustAxisGeometry geometry,
        CancellationToken cancellationToken) => MeasureAsync(originalImage, geometry, cancellationToken);
    // Legacy adapters continue their existing derived-only review behavior.
    Task<AxisTickMeasurementResult> MeasureAffineManualPartialAsync(byte[] derivedImage, RustAxisGeometry geometry,
        RustAffineTickSource originalSupport, CancellationToken cancellationToken) =>
        MeasureManualPartialAsync(derivedImage, geometry, cancellationToken);
}

/// <summary>Measures pixel strokes at an exact review box without selecting new geometry.</summary>
public sealed class RustAxisTickMeasurementService(string workerPath) : IAxisTickMeasurementService
{
    public Task<AxisTickMeasurementResult> MeasureAsync(byte[] originalImage, RustAxisGeometry geometry,
        CancellationToken cancellationToken) => MeasureCoreAsync(originalImage, geometry, false, cancellationToken);

    public Task<AxisTickMeasurementResult> MeasureManualPartialAsync(byte[] originalImage, RustAxisGeometry geometry,
        CancellationToken cancellationToken) => MeasureCoreAsync(originalImage, geometry, true, cancellationToken);

    public Task<AxisTickMeasurementResult> MeasureAffineManualPartialAsync(byte[] derivedImage, RustAxisGeometry geometry,
        RustAffineTickSource originalSupport, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<AxisTickMeasurementResult>(cancellationToken);
        return originalSupport is null
            ? Task.FromResult(new AxisTickMeasurementResult(string.Empty, 0, 0, null, "INVALID_INPUT"))
            : MeasureCoreAsync(derivedImage, geometry, true, cancellationToken, originalSupport);
    }

    private Task<AxisTickMeasurementResult> MeasureCoreAsync(byte[] originalImage, RustAxisGeometry geometry,
        bool manualPartial, CancellationToken cancellationToken, RustAffineTickSource? originalSupport = null)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<AxisTickMeasurementResult>(cancellationToken);
        if (originalImage is null || originalImage.Length is 0 or > 32 * 1024 * 1024 || geometry is null)
            return Task.FromResult(new AxisTickMeasurementResult(string.Empty, 0, 0, null, "INVALID_INPUT"));
        if (originalSupport is not null && (originalSupport.Width is < 32 or > 4096 ||
            originalSupport.Height is < 32 or > 4096 ||
            (long)originalSupport.Width * originalSupport.Height > 16_777_216 ||
            (long)originalSupport.Width * originalSupport.Height != originalSupport.Pixels.Length))
            return Task.FromResult(new AxisTickMeasurementResult(string.Empty, 0, 0, null, "INVALID_INPUT"));
        byte[] snapshot = (byte[])originalImage.Clone();
        RustAffineTickSource? supportSnapshot = originalSupport is null ? null :
            originalSupport with { Pixels = originalSupport.Pixels.ToArray() };
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
                RustAxisTickMeasurementResult result = supportSnapshot is not null
                    ? await RustAxisTickMeasurement.RunAffineAsync(workerPath, width, height, pixels, geometry,
                        supportSnapshot, manualPartial, cancellationToken).ConfigureAwait(false)
                    : manualPartial
                    ? await RustAxisTickMeasurement.RunManualPartialAsync(workerPath, width, height, pixels, geometry, cancellationToken: cancellationToken).ConfigureAwait(false)
                    : await RustAxisTickMeasurement.RunAsync(workerPath, width, height, pixels, geometry, cancellationToken: cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new AxisTickMeasurementResult(hash, width, height, result.Evidence, result.ErrorCode)
                {
                    OriginalSupportBinding = supportSnapshot is not null && result.Evidence is not null
                        ? BindOriginalSupport(supportSnapshot) : null,
                };
            }
            catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or FormatException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new AxisTickMeasurementResult(hash, 0, 0, null, "TICK_IMAGE_DECODE_FAILED");
            }
        }, cancellationToken);
    }

    internal static string BindOriginalSupport(RustAffineTickSource source) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = "affine-original-support-v1", source.Width, source.Height,
            PixelsSha256 = Convert.ToHexStringLower(SHA256.HashData(source.Pixels.Span)),
            source.M11, source.M12, source.M13, source.M21, source.M22, source.M23,
        })));
}
