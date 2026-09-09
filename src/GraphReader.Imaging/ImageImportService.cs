// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Imazen.WebP;

namespace GraphReader.Imaging;

public sealed class ImageImportService : IImageImportService, IEncodedImageByteImportService
{
    private readonly IImageImportStageObserver? stageObserver;

    public ImageImportService(IImageImportStageObserver? stageObserver = null)
    {
        this.stageObserver = stageObserver;
    }

    public Task<ImageImportResult> ImportAsync(string path, CancellationToken cancellationToken) =>
        ImportCoreAsync(path, inputIndex: 0, cancellationToken);

    public Task<ImageImportResult> ImportBytesAsync(
        string sourceReference,
        ImmutableImageBytes sourceBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceReference);
        ArgumentNullException.ThrowIfNull(sourceBytes);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = sourceBytes.Copy();
        cancellationToken.ThrowIfCancellationRequested();
        return ImportValidatedBytesAsync(
            sourceReference,
            bytes,
            inputIndex: 0,
            preserveSourceReference: true,
            cancellationToken);
    }

    public async Task<BatchImportResult> ImportBatchAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] orderedPaths = paths.ToArray();
        var firstIndexByHash = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ImageImportResult>(orderedPaths.Length);

        for (int index = 0; index < orderedPaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageImportResult result = await ImportCoreAsync(orderedPaths[index], index, cancellationToken).ConfigureAwait(false);
            if (result.Image is { } image)
            {
                if (firstIndexByHash.TryGetValue(image.Sha256, out int originalIndex))
                {
                    image = image with { DuplicateOfInputIndex = originalIndex };
                    result = ImageImportResult.Success(image);
                }
                else
                {
                    firstIndexByHash.Add(image.Sha256, index);
                }
            }

            results.Add(result);
        }

        return new BatchImportResult(results);
    }

    private async Task<ImageImportResult> ImportCoreAsync(
        string path,
        int inputIndex,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception)
        {
            return Failure(ImageImportErrorCode.FileNotFound, "Errors.ImageNotFound", path, inputIndex, exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            return Failure(ImageImportErrorCode.FileNotFound, "Errors.ImageNotFound", path, inputIndex, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(ImageImportErrorCode.AccessDenied, "Errors.ImageAccessDenied", path, inputIndex, exception);
        }
        catch (IOException exception)
        {
            return Failure(ImageImportErrorCode.IoFailure, "Errors.ImageReadFailed", path, inputIndex, exception);
        }

        return await ImportValidatedBytesAsync(
                path,
                bytes,
                inputIndex,
                preserveSourceReference: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<ImageImportResult> ImportValidatedBytesAsync(
        string sourceReference,
        byte[] bytes,
        int inputIndex,
        bool preserveSourceReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Observe(ImageImportStage.BeforeHash, sourceReference, cancellationToken);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        cancellationToken.ThrowIfCancellationRequested();
        Observe(ImageImportStage.BeforeMetadata, sourceReference, cancellationToken);
        bool metadataRead = TryReadMetadata(
            bytes,
            sourceReference,
            cancellationToken,
            out ImageMetadata? metadata,
            out ImageImportErrorCode errorCode,
            out string technicalMessage);
        cancellationToken.ThrowIfCancellationRequested();
        if (!metadataRead)
        {
            return Task.FromResult(ImageImportResult.Failure(
                sourceReference,
                inputIndex,
                new ImageImportError(
                    errorCode,
                    ImageErrorSeverity.Error,
                    errorCode == ImageImportErrorCode.UnsupportedFormat
                        ? "Errors.ImageFormatUnsupported"
                        : "Errors.ImageCorrupt",
                    technicalMessage,
                    Recoverable: true,
                    errorCode == ImageImportErrorCode.UnsupportedFormat
                        ? ImageSuggestedAction.SelectManualMode
                        : ImageSuggestedAction.Retry,
                    sourceReference)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Observe(ImageImportStage.AfterMetadata, sourceReference, cancellationToken);
        var imported = new ImportedImage(
            preserveSourceReference ? sourceReference : Path.GetFullPath(sourceReference),
            hash,
            metadata!,
            new ImmutableImageBytes(bytes),
            inputIndex);
        return Task.FromResult(ImageImportResult.Success(imported));
    }

    private void Observe(ImageImportStage stage, string path, CancellationToken cancellationToken)
    {
        stageObserver?.Observe(stage, path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private bool TryReadMetadata(
        byte[] bytes,
        string path,
        CancellationToken cancellationToken,
        out ImageMetadata? metadata,
        out ImageImportErrorCode errorCode,
        out string technicalMessage)
    {
        try
        {
            if (TryReadWebP(bytes, out metadata))
            {
                errorCode = default;
                technicalMessage = string.Empty;
                return true;
            }

            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                BitmapDecoder decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                if (!TryMapWicDecoder(decoder, out ImageFileFormat format, out string mimeType))
                {
                    metadata = null;
                    errorCode = ImageImportErrorCode.UnsupportedFormat;
                    technicalMessage = $"Decoded image codec '{decoder.CodecInfo?.FriendlyName ?? decoder.GetType().Name}' is not supported.";
                    return false;
                }

                BitmapFrame first = decoder.Frames[0];
                metadata = new ImageMetadata(
                    first.PixelWidth,
                    first.PixelHeight,
                    format,
                    mimeType,
                    decoder.Frames.Count,
                    GetBitsPerChannel(first.Format),
                    first.DpiX > 0 ? first.DpiX : 96,
                    first.DpiY > 0 ? first.DpiY : 96,
                    bytes.LongLength);
                errorCode = default;
                technicalMessage = string.Empty;
                return true;
            }
            catch (Exception exception) when (exception is NotSupportedException or FileFormatException or ArgumentException)
            {
                metadata = null;
                errorCode = HasRecognizedUnsupportedSignature(bytes)
                    ? ImageImportErrorCode.UnsupportedFormat
                    : ImageImportErrorCode.CorruptImage;
                technicalMessage = exception.Message;
                return false;
            }
        }
        finally
        {
            stageObserver?.Observe(ImageImportStage.AfterMetadataAttempt, path, cancellationToken);
        }
    }

    private static int GetBitsPerChannel(PixelFormat format)
    {
        if (format == PixelFormats.Bgra32 ||
            format == PixelFormats.Pbgra32 ||
            format == PixelFormats.Bgr32 ||
            format == PixelFormats.Bgr24 ||
            format == PixelFormats.Rgb24 ||
            format == PixelFormats.Gray8 ||
            format == PixelFormats.Indexed8 ||
            format == PixelFormats.Cmyk32)
        {
            return 8;
        }

        if (format == PixelFormats.Rgba64 ||
            format == PixelFormats.Prgba64 ||
            format == PixelFormats.Rgb48 ||
            format == PixelFormats.Gray16)
        {
            return 16;
        }

        int channelCount = format.Masks.Count;
        if (channelCount > 0 && format.BitsPerPixel % channelCount == 0)
        {
            return format.BitsPerPixel / channelCount;
        }

        return format.BitsPerPixel <= 8 ? 8 : format.BitsPerPixel;
    }

    private static bool TryReadWebP(byte[] bytes, out ImageMetadata? metadata)
    {
        if (!IsWebP(bytes))
        {
            metadata = null;
            return false;
        }

        try
        {
            WebPImageInfo dimensions = WebPInfo.GetImageInfo(bytes);
            int frameCount = 1;
            if (dimensions.HasAnimation)
            {
                using var animation = new AnimDecoder(bytes);
                frameCount = animation.Info.FrameCount;
            }

            metadata = new ImageMetadata(
                dimensions.Width,
                dimensions.Height,
                ImageFileFormat.WebP,
                "image/webp",
                frameCount,
                BitsPerChannel: 8,
                DpiX: 96,
                DpiY: 96,
                bytes.LongLength);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            metadata = null;
            return false;
        }
    }

    private static bool IsWebP(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 &&
        bytes[..4].SequenceEqual("RIFF"u8) &&
        bytes.Slice(8, 4).SequenceEqual("WEBP"u8);

    private static bool HasRecognizedUnsupportedSignature(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 6 &&
        (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8));

    private static bool TryMapWicDecoder(BitmapDecoder decoder, out ImageFileFormat format, out string mimeType)
    {
        switch (decoder)
        {
            case PngBitmapDecoder:
                format = ImageFileFormat.Png;
                mimeType = "image/png";
                return true;
            case JpegBitmapDecoder:
                format = ImageFileFormat.Jpeg;
                mimeType = "image/jpeg";
                return true;
            case TiffBitmapDecoder:
                format = ImageFileFormat.Tiff;
                mimeType = "image/tiff";
                return true;
            case BmpBitmapDecoder:
                format = ImageFileFormat.Bmp;
                mimeType = "image/bmp";
                return true;
            default:
                format = default;
                mimeType = string.Empty;
                return false;
        }
    }

    private static ImageImportResult Failure(
        ImageImportErrorCode code,
        string messageKey,
        string path,
        int index,
        Exception exception) =>
        ImageImportResult.Failure(
            path,
            index,
            new ImageImportError(
                code,
                ImageErrorSeverity.Error,
                messageKey,
                exception.Message,
                Recoverable: true,
                ImageSuggestedAction.Retry,
                path));
}

public enum ImageImportStage
{
    BeforeHash,
    BeforeMetadata,
    AfterMetadataAttempt,
    AfterMetadata
}

public interface IImageImportStageObserver
{
    void Observe(ImageImportStage stage, string path, CancellationToken cancellationToken);
}
