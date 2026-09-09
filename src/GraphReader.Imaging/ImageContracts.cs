// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.IO;

namespace GraphReader.Imaging;

public enum ImageFileFormat
{
    Png,
    Jpeg,
    Tiff,
    Bmp,
    WebP
}

public enum ImageImportErrorCode
{
    FileNotFound,
    AccessDenied,
    UnsupportedFormat,
    CorruptImage,
    IoFailure
}

public enum ImageErrorSeverity
{
    Warning,
    Error
}

public enum ImageSuggestedAction
{
    Retry,
    SelectManualMode
}

public sealed record ImageImportError(
    ImageImportErrorCode Code,
    ImageErrorSeverity Severity,
    string UserMessageKey,
    string TechnicalMessage,
    bool Recoverable,
    ImageSuggestedAction SuggestedAction,
    string? SourcePath);

public sealed record ImageMetadata(
    int Width,
    int Height,
    ImageFileFormat Format,
    string MimeType,
    int FrameCount,
    int BitsPerChannel,
    double DpiX,
    double DpiY,
    long ByteLength);

public sealed class ImmutableImageBytes
{
    private readonly byte[] bytes;

    public ImmutableImageBytes(ReadOnlySpan<byte> source)
    {
        bytes = source.ToArray();
    }

    public int Length => bytes.Length;

    public Stream OpenRead() => new MemoryStream(bytes, writable: false);

    public byte[] Copy() => (byte[])bytes.Clone();

    internal ReadOnlySpan<byte> AsSpan() => bytes;
}

public sealed record ImportedImage
{
    public ImportedImage(
        string sourcePath,
        string sha256,
        ImageMetadata metadata,
        ImmutableImageBytes originalBytes,
        int inputIndex = 0,
        int? duplicateOfInputIndex = null)
    {
        SourcePath = sourcePath;
        Sha256 = sha256;
        Metadata = metadata;
        OriginalBytes = originalBytes;
        InputIndex = inputIndex;
        DuplicateOfInputIndex = duplicateOfInputIndex;
    }

    public string SourcePath { get; init; }

    public string Sha256 { get; init; }

    public ImageMetadata Metadata { get; init; }

    public ImmutableImageBytes OriginalBytes { get; init; }

    public int InputIndex { get; init; }

    public int? DuplicateOfInputIndex { get; init; }

    public bool IsDuplicate => DuplicateOfInputIndex.HasValue;
}

public sealed record ImageImportResult
{
    private ImageImportResult(string sourcePath, int inputIndex, ImportedImage? image, ImageImportError? error)
    {
        SourcePath = sourcePath;
        InputIndex = inputIndex;
        Image = image;
        Error = error;
    }

    public string SourcePath { get; }

    public int InputIndex { get; }

    public ImportedImage? Image { get; }

    public ImageImportError? Error { get; }

    public bool IsSuccess => Image is not null && Error is null;

    public static ImageImportResult Success(ImportedImage image) =>
        new(image.SourcePath, image.InputIndex, image, null);

    public static ImageImportResult Failure(string sourcePath, int inputIndex, ImageImportError error) =>
        new(sourcePath, inputIndex, null, error);
}

public sealed class BatchImportResult
{
    public BatchImportResult(IEnumerable<ImageImportResult> items)
    {
        Items = new ReadOnlyCollection<ImageImportResult>(items.OrderBy(static item => item.InputIndex).ToArray());
    }

    public IReadOnlyList<ImageImportResult> Items { get; }

    public int SuccessfulCount => Items.Count(static item => item.IsSuccess);

    public int DuplicateCount => Items.Count(static item => item.Image?.IsDuplicate == true);
}

public interface IImageImportService
{
    Task<ImageImportResult> ImportAsync(string path, CancellationToken cancellationToken);

    Task<BatchImportResult> ImportBatchAsync(IEnumerable<string> paths, CancellationToken cancellationToken);
}

/// <summary>
/// Imports immutable encoded image bytes through the same validation path as file-backed input.
/// The source reference identifies the bytes but is never opened as a file.
/// </summary>
public interface IEncodedImageByteImportService
{
    Task<ImageImportResult> ImportBytesAsync(
        string sourceReference,
        ImmutableImageBytes sourceBytes,
        CancellationToken cancellationToken);
}
