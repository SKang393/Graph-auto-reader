// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using GraphReader.Pdf;

namespace GraphReader.App.Integration.Workflow;

internal static class ProductionRasterPanelizationFailureCodes
{
    public const string InvalidSource = "RASTER_PANEL_SOURCE_INVALID";
    public const string ChecksumMismatch = "RASTER_PANEL_CHECKSUM_MISMATCH";
    public const string DimensionMismatch = "RASTER_PANEL_DIMENSION_MISMATCH";
    public const string PanelizationFailed = "RASTER_PANELIZATION_FAILED";
    public const string PanelUnavailable = "RASTER_PANEL_UNAVAILABLE";
    public const string NoPanelDetected = "RASTER_NO_PANEL_DETECTED";
}

internal sealed class ProductionRasterPanelizationException : InvalidOperationException
{
    public ProductionRasterPanelizationException(
        string code,
        string message,
        IEnumerable<string>? warnings = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        Warnings = Array.AsReadOnly((warnings ?? []).ToArray());
    }

    public string Code { get; }

    public IReadOnlyList<string> Warnings { get; }
}

internal sealed class EncodedRasterCrop
{
    private readonly byte[] bytes;

    public EncodedRasterCrop(
        byte[] bytes,
        int width,
        int height,
        PdfRectD encodedCropInSourcePixels)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (!encodedCropInSourcePixels.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(encodedCropInSourcePixels));
        }

        this.bytes = (byte[])bytes.Clone();
        Width = width;
        Height = height;
        EncodedCropInSourcePixels = encodedCropInSourcePixels;
    }

    public int Width { get; }

    public int Height { get; }

    public PdfRectD EncodedCropInSourcePixels { get; }

    public byte[] CopyBytes() => (byte[])bytes.Clone();
}

internal sealed class ProductionRasterPanel
{
    private readonly byte[] encodedBytes;

    public ProductionRasterPanel(
        Guid panelId,
        int order,
        byte[] encodedBytes,
        string sha256,
        int width,
        int height,
        PdfRectD requestedCropInSourcePixels,
        PdfRectD encodedCropInSourcePixels)
    {
        if (panelId == Guid.Empty)
        {
            throw new ArgumentException("A stable panel ID is required.", nameof(panelId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(order);
        ArgumentNullException.ThrowIfNull(encodedBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (!requestedCropInSourcePixels.IsValid || !encodedCropInSourcePixels.IsValid)
        {
            throw new ArgumentException("Raster panel crops must be finite and non-empty.");
        }

        PanelId = panelId;
        Order = order;
        this.encodedBytes = (byte[])encodedBytes.Clone();
        Sha256 = sha256;
        Width = width;
        Height = height;
        RequestedCropInSourcePixels = requestedCropInSourcePixels;
        EncodedCropInSourcePixels = encodedCropInSourcePixels;
        SourceToPanelMatrix = Matrix(-encodedCropInSourcePixels.X, -encodedCropInSourcePixels.Y);
        PanelToSourceMatrix = Matrix(encodedCropInSourcePixels.X, encodedCropInSourcePixels.Y);
    }

    public Guid PanelId { get; }

    public int Order { get; }

    public string Sha256 { get; }

    public int Width { get; }

    public int Height { get; }

    public PdfRectD RequestedCropInSourcePixels { get; }

    public PdfRectD EncodedCropInSourcePixels { get; }

    public IReadOnlyList<double> SourceToPanelMatrix { get; }

    public IReadOnlyList<double> PanelToSourceMatrix { get; }

    public byte[] CopyEncodedBytes() => (byte[])encodedBytes.Clone();

    public PdfPointD MapSourceToPanel(PdfPointD point)
    {
        if (!point.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(point));
        }

        return new PdfPointD(
            point.X - EncodedCropInSourcePixels.X,
            point.Y - EncodedCropInSourcePixels.Y);
    }

    public PdfPointD MapPanelToSource(PdfPointD point)
    {
        if (!point.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(point));
        }

        return new PdfPointD(
            point.X + EncodedCropInSourcePixels.X,
            point.Y + EncodedCropInSourcePixels.Y);
    }

    private static ReadOnlyCollection<double> Matrix(double translateX, double translateY) =>
        new ReadOnlyCollection<double>(
            [1d, 0d, translateX, 0d, 1d, translateY, 0d, 0d, 1d]);
}

internal sealed class ProductionRasterPanelizationResult
{
    public ProductionRasterPanelizationResult(
        IEnumerable<ProductionRasterPanel> panels,
        IEnumerable<string> warnings,
        double elapsedMilliseconds)
    {
        Panels = Array.AsReadOnly(panels.ToArray());
        Warnings = Array.AsReadOnly(warnings.ToArray());
        ElapsedMilliseconds = elapsedMilliseconds;
    }

    public IReadOnlyList<ProductionRasterPanel> Panels { get; }

    public IReadOnlyList<string> Warnings { get; }

    public double ElapsedMilliseconds { get; }
}

/// <summary>
/// Produces detector-ready raster panels from image bytes alone. The PDF geometry
/// types are transient implementation details and do not create PDF provenance.
/// </summary>
internal sealed class ProductionRasterPanelizer
{
    private const int AnalysisDpi = 144;
    private const double CropTolerance = 0.01d;
    private const long MaximumSourcePixels = 40_000_000;
    private const long MaximumEncodedBytes = 256L * 1024L * 1024L;
    private const int MaximumChunkBytes = 64 * 1024 * 1024;
    private const int MaximumChunkCount = 100_000;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] PngCrcTable = CreatePngCrcTable();

    private readonly IPdfPanelizationEngine panelizationEngine;

    public ProductionRasterPanelizer(IPdfPanelizationEngine? panelizationEngine = null) =>
        this.panelizationEngine = panelizationEngine ?? PanelizationEngine.CreateForStandaloneRasterSource();

    public Task<ProductionRasterPanelizationResult> PanelizeAsync(
        ImmutableByteBuffer encodedPngBytes,
        string sourceSha256,
        int sourceWidth,
        int sourceHeight,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encodedPngBytes);
        // Run PNG decoding and deterministic raster analysis away from a possible UI caller.
        return Task.Run(
            () => PanelizeCoreAsync(
                encodedPngBytes,
                sourceSha256,
                sourceWidth,
                sourceHeight,
                cancellationToken),
            cancellationToken);
    }

    private async Task<ProductionRasterPanelizationResult> PanelizeCoreAsync(
        ImmutableByteBuffer encodedPngBytes,
        string sourceSha256,
        int sourceWidth,
        int sourceHeight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] sourceBytes = encodedPngBytes.ToArray();
        BitmapSource decodedSource = DecodeValidatedSource(sourceBytes, sourceSha256, sourceWidth, sourceHeight, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var options = new PdfPanelizationOptions(RenderDpi: AnalysisDpi);
        var page = new PdfPageSnapshot(
            pageNumber: 1,
            widthPoints: sourceWidth * 72d / AnalysisDpi,
            heightPoints: sourceHeight * 72d / AnalysisDpi,
            textBlocks: [],
            embeddedImages: [],
            vectorLines: []);
        var rendered = new PdfRenderedPage(
            new ImmutableByteBuffer(sourceBytes),
            sourceWidth,
            sourceHeight);

        PdfPanelizationResult proposed;
        try
        {
            proposed = await panelizationEngine.ProposeAsync(
                    new PdfPanelizationInput(sourceSha256, page, rendered, options),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.PanelizationFailed,
                "Raster panelization failed before producing a validated result.",
                innerException: exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        string[] warnings = proposed.Warnings
            .Concat(proposed.Failures
                .Where(static failure => failure.Severity == PdfFailureSeverity.Warning)
                .Select(static failure => failure.TechnicalMessage))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        PdfFailure? error = proposed.Failures.FirstOrDefault(
            static failure => failure.Severity == PdfFailureSeverity.Error);
        if (!proposed.Succeeded || error is not null)
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.PanelizationFailed,
                error?.TechnicalMessage ?? "Raster panelization returned an unsuccessful result.",
                warnings);
        }

        if (proposed.Panels.Count == 0)
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.NoPanelDetected,
                "No graph-like raster panel met the deterministic panelization threshold.",
                warnings);
        }

        Dictionary<Guid, PdfFigureCandidate> figures;
        try
        {
            figures = proposed.Figures.ToDictionary(static figure => figure.FigureId);
        }
        catch (ArgumentException exception)
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.PanelUnavailable,
                "Raster panelization returned duplicate figure identities.",
                warnings,
                exception);
        }

        var retainedFigureIds = new HashSet<Guid>();
        var retainedPanelIds = new HashSet<Guid>();
        var ordered = proposed.Panels
            .OrderBy(static panel => panel.Order)
            .ThenBy(static panel => panel.BoundsPagePixels.Y)
            .ThenBy(static panel => panel.BoundsPagePixels.X)
            .ThenBy(static panel => panel.PanelId)
            .ToArray();
        var output = new List<ProductionRasterPanel>(ordered.Length);
        var requestedCrops = new List<PdfRectD>(ordered.Length);
        var encodedCrops = new List<PdfRectD>(ordered.Length);
        for (int index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPanelRecord panel = ordered[index];
            if (!retainedPanelIds.Add(panel.PanelId) ||
                !figures.TryGetValue(panel.FigureId, out PdfFigureCandidate? figure))
            {
                throw Failure(
                    ProductionRasterPanelizationFailureCodes.PanelUnavailable,
                    "Raster panelization returned a duplicate panel or a panel without its figure.",
                    warnings);
            }

            if (retainedFigureIds.Add(figure.FigureId))
            {
                ValidateFigure(figure, sourceSha256, sourceWidth, sourceHeight, warnings);
            }

            ValidateRequestedCrop(panel.CropInSourcePixels, sourceWidth, sourceHeight, warnings);
            EnsureNoOverlap(requestedCrops, panel.CropInSourcePixels, "requested", warnings);

            EncodedRasterCrop crop;
            try
            {
                crop = ProductionWorkflowImportStage.CreateDetectorReadyCrop(
                    panel,
                    figure,
                    cancellationToken,
                    decodedSource);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or
                NotSupportedException or FormatException or ArgumentException or OverflowException)
            {
                throw Failure(
                    ProductionRasterPanelizationFailureCodes.PanelUnavailable,
                    $"Raster panel '{panel.PanelId}' could not produce detector-ready bytes: {exception.Message}",
                    warnings,
                    exception);
            }

            ValidateEncodedCrop(crop, sourceWidth, sourceHeight, warnings);
            EnsureNoOverlap(encodedCrops, crop.EncodedCropInSourcePixels, "encoded", warnings);
            byte[] cropBytes = crop.CopyBytes();
            string cropSha256 = Convert.ToHexStringLower(SHA256.HashData(cropBytes));
            PdfRectD encoded = crop.EncodedCropInSourcePixels;
            Guid stableId = ProductionWorkflowPanelStore.CreateStableId(
                "raster-panel-v1",
                sourceSha256.ToLowerInvariant(),
                encoded.X.ToString("R", CultureInfo.InvariantCulture),
                encoded.Y.ToString("R", CultureInfo.InvariantCulture),
                encoded.Width.ToString("R", CultureInfo.InvariantCulture),
                encoded.Height.ToString("R", CultureInfo.InvariantCulture));
            output.Add(new ProductionRasterPanel(
                stableId,
                index + 1,
                cropBytes,
                cropSha256,
                crop.Width,
                crop.Height,
                panel.CropInSourcePixels,
                encoded));
            requestedCrops.Add(panel.CropInSourcePixels);
            encodedCrops.Add(encoded);
        }

        return new ProductionRasterPanelizationResult(output, warnings, proposed.ElapsedMilliseconds);
    }

    internal static BitmapFrame DecodeValidatedSource(
        byte[] sourceBytes, string sourceSha256, int sourceWidth, int sourceHeight,
        CancellationToken cancellationToken)
    {
        ValidateSource(sourceBytes, sourceSha256, sourceWidth, sourceHeight, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return DecodeSource(sourceBytes, sourceWidth, sourceHeight, cancellationToken);
    }

    private static void ValidateSource(
        byte[] sourceBytes,
        string sourceSha256,
        int sourceWidth,
        int sourceHeight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.DimensionMismatch,
                "Declared raster dimensions must be positive.");
        }

        long sourcePixels = (long)sourceWidth * sourceHeight;
        if (sourcePixels > MaximumSourcePixels)
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.InvalidSource,
                $"Raster panelization is limited to {MaximumSourcePixels} decoded pixels.");
        }

        if (sourceBytes.LongLength > MaximumEncodedBytes)
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.InvalidSource,
                $"Encoded raster input is limited to {MaximumEncodedBytes} bytes.");
        }

        if (sourceBytes.Length < 24 || !sourceBytes.AsSpan(0, 8).SequenceEqual(PngSignature) ||
            BinaryPrimitives.ReadUInt32BigEndian(sourceBytes.AsSpan(8, 4)) != 13 ||
            !sourceBytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.InvalidSource,
                "Raster panelization requires an encoded PNG with a valid leading header.");
        }

        string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(actualSha256, sourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.ChecksumMismatch,
                "Encoded raster bytes do not match the declared SHA-256 checksum.");
        }

        ValidatePngContainer(sourceBytes, sourceWidth, sourceHeight, cancellationToken);
    }

    private static void ValidatePngContainer(
        byte[] sourceBytes,
        int sourceWidth,
        int sourceHeight,
        CancellationToken cancellationToken)
    {
        int offset = PngSignature.Length;
        int chunkCount = 0;
        bool sawHeader = false;
        bool sawImageData = false;
        bool imageDataEnded = false;
        long imageDataBytes = 0;
        while (offset < sourceBytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chunkCount++;
            if (chunkCount > MaximumChunkCount || sourceBytes.Length - offset < 12)
            {
                throw Failure(
                    ProductionRasterPanelizationFailureCodes.InvalidSource,
                    "Encoded PNG has too many chunks or ends inside a chunk header.");
            }

            uint unsignedLength = BinaryPrimitives.ReadUInt32BigEndian(sourceBytes.AsSpan(offset, 4));
            if (unsignedLength > int.MaxValue || unsignedLength > MaximumChunkBytes)
            {
                throw Failure(
                    ProductionRasterPanelizationFailureCodes.InvalidSource,
                    $"Encoded PNG chunk exceeds the {MaximumChunkBytes}-byte safety limit.");
            }

            int chunkLength = (int)unsignedLength;
            long chunkEnd = (long)offset + 12L + chunkLength;
            if (chunkEnd > sourceBytes.Length)
            {
                throw Failure(
                    ProductionRasterPanelizationFailureCodes.InvalidSource,
                    "Encoded PNG chunk declares data beyond the immutable source buffer.");
            }

            int typeOffset = offset + 4;
            int dataOffset = typeOffset + 4;
            int crcOffset = dataOffset + chunkLength;
            ReadOnlySpan<byte> chunkType = sourceBytes.AsSpan(typeOffset, 4);
            uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(sourceBytes.AsSpan(crcOffset, 4));
            uint computedCrc = ComputePngCrc(
                sourceBytes.AsSpan(typeOffset, chunkLength + 4),
                cancellationToken);
            if (storedCrc != computedCrc)
            {
                throw Failure(
                    ProductionRasterPanelizationFailureCodes.InvalidSource,
                    "Encoded PNG contains a chunk with an invalid CRC.");
            }

            if (chunkType.SequenceEqual("IHDR"u8))
            {
                if (sawHeader || chunkCount != 1 || chunkLength != 13)
                {
                    throw Failure(
                        ProductionRasterPanelizationFailureCodes.InvalidSource,
                        "Encoded PNG must contain one complete IHDR chunk first.");
                }

                uint encodedWidth = BinaryPrimitives.ReadUInt32BigEndian(sourceBytes.AsSpan(dataOffset, 4));
                uint encodedHeight = BinaryPrimitives.ReadUInt32BigEndian(sourceBytes.AsSpan(dataOffset + 4, 4));
                if (encodedWidth != sourceWidth || encodedHeight != sourceHeight)
                {
                    throw Failure(
                        ProductionRasterPanelizationFailureCodes.DimensionMismatch,
                        "Encoded raster dimensions do not match the declared source dimensions.");
                }

                sawHeader = true;
            }
            else if (chunkType.SequenceEqual("IDAT"u8))
            {
                if (!sawHeader || imageDataEnded)
                {
                    throw Failure(
                        ProductionRasterPanelizationFailureCodes.InvalidSource,
                        "Encoded PNG IDAT chunks must be consecutive and follow IHDR.");
                }

                sawImageData = true;
                imageDataBytes += chunkLength;
            }
            else if (chunkType.SequenceEqual("IEND"u8))
            {
                if (!sawHeader || !sawImageData || imageDataBytes == 0 || chunkLength != 0 ||
                    chunkEnd != sourceBytes.Length)
                {
                    throw Failure(
                        ProductionRasterPanelizationFailureCodes.InvalidSource,
                        "Encoded PNG must end with one empty terminal IEND after image data.");
                }

                return;
            }
            else
            {
                if (!sawHeader)
                {
                    throw Failure(
                        ProductionRasterPanelizationFailureCodes.InvalidSource,
                        "Encoded PNG IHDR must be the first chunk.");
                }

                imageDataEnded = sawImageData;
            }

            offset = (int)chunkEnd;
        }

        throw Failure(
            ProductionRasterPanelizationFailureCodes.InvalidSource,
            "Encoded PNG is missing required IHDR, IDAT, or terminal IEND content.");
    }

    private static uint ComputePngCrc(
        ReadOnlySpan<byte> data,
        CancellationToken cancellationToken)
    {
        uint crc = uint.MaxValue;
        for (var index = 0; index < data.Length; index++)
        {
            if ((index & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            crc = PngCrcTable[(crc ^ data[index]) & 0xff] ^ (crc >> 8);
        }

        return crc ^ uint.MaxValue;
    }

    private static uint[] CreatePngCrcTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            uint entry = value;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0
                    ? 0xedb88320u ^ (entry >> 1)
                    : entry >> 1;
            }

            table[value] = entry;
        }

        return table;
    }

    private static BitmapFrame DecodeSource(
        byte[] sourceBytes,
        int sourceWidth,
        int sourceHeight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var input = new MemoryStream(sourceBytes, writable: false);
            // BitmapDecoder and frame materialization are native WPF codec calls. They
            // cannot be interrupted mid-call, so the decoded-pixel cap and checkpoints
            // bound their work and cancellation latency.
            BitmapDecoder decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            cancellationToken.ThrowIfCancellationRequested();
            if (decoder.Frames.Count != 1)
            {
                throw Failure(
                    ProductionRasterPanelizationFailureCodes.InvalidSource,
                    "Raster panelization requires a single-frame PNG.");
            }

            BitmapFrame frame = decoder.Frames[0];
            if (frame.PixelWidth != sourceWidth || frame.PixelHeight != sourceHeight)
            {
                throw Failure(
                    ProductionRasterPanelizationFailureCodes.DimensionMismatch,
                    "Decoded raster dimensions do not match the declared source dimensions.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            int validationStride = Math.Max(1, (frame.Format.BitsPerPixel + 7) / 8);
            byte[] decodedSample = new byte[validationStride];
            frame.CopyPixels(
                new Int32Rect(sourceWidth - 1, sourceHeight - 1, 1, 1),
                decodedSample,
                validationStride,
                0);
            cancellationToken.ThrowIfCancellationRequested();
            frame.Freeze();
            cancellationToken.ThrowIfCancellationRequested();
            return frame;
        }
        catch (ProductionRasterPanelizationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            NotSupportedException or FormatException or ArgumentException or OverflowException)
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.InvalidSource,
                "Encoded PNG bytes could not be completely decoded.",
                innerException: exception);
        }
    }

    private static void ValidateFigure(
        PdfFigureCandidate figure,
        string sourceSha256,
        int sourceWidth,
        int sourceHeight,
        IReadOnlyList<string> warnings)
    {
        if (figure.SourceKind != PdfFigureSourceKind.RenderedPage ||
            figure.EmbeddedImageId is not null ||
            figure.EncodedSource is null ||
            figure.SourcePixelWidth != sourceWidth ||
            figure.SourcePixelHeight != sourceHeight ||
            !string.Equals(figure.MediaType, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.PanelUnavailable,
                "Raster panelization returned a figure that is not bound to the complete input PNG.",
                warnings);
        }

        string figureSha256 = Convert.ToHexStringLower(SHA256.HashData(figure.EncodedSource.ToArray()));
        if (!string.Equals(figureSha256, sourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.PanelUnavailable,
                "Raster panelization returned figure bytes that do not match the immutable source.",
                warnings);
        }
    }

    private static void ValidateRequestedCrop(
        PdfRectD crop,
        int sourceWidth,
        int sourceHeight,
        IReadOnlyList<string> warnings)
    {
        if (!crop.IsValid || crop.X < -CropTolerance || crop.Y < -CropTolerance ||
            crop.Right > sourceWidth + CropTolerance || crop.Bottom > sourceHeight + CropTolerance)
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.PanelUnavailable,
                "Raster panelization returned a requested crop outside the immutable source.",
                warnings);
        }
    }

    private static void ValidateEncodedCrop(
        EncodedRasterCrop crop,
        int sourceWidth,
        int sourceHeight,
        IReadOnlyList<string> warnings)
    {
        PdfRectD bounds = crop.EncodedCropInSourcePixels;
        if (bounds.X < 0d || bounds.Y < 0d || bounds.Right > sourceWidth || bounds.Bottom > sourceHeight ||
            bounds.Width != crop.Width || bounds.Height != crop.Height)
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.PanelUnavailable,
                "Detector-ready crop geometry does not match its encoded dimensions or source bounds.",
                warnings);
        }
    }

    internal static void EnsureNoOverlap(
        IReadOnlyList<PdfRectD> existing,
        PdfRectD candidate,
        string kind,
        IReadOnlyList<string> warnings)
    {
        if (existing.Any(value =>
            Math.Min(value.Right, candidate.Right) - Math.Max(value.X, candidate.X) > CropTolerance &&
            Math.Min(value.Bottom, candidate.Bottom) - Math.Max(value.Y, candidate.Y) > CropTolerance))
        {
            throw Failure(
                ProductionRasterPanelizationFailureCodes.PanelUnavailable,
                $"Raster panelization returned overlapping {kind} source crops.",
                warnings);
        }
    }

    private static ProductionRasterPanelizationException Failure(
        string code,
        string message,
        IEnumerable<string>? warnings = null,
        Exception? innerException = null) =>
        new(code, message, warnings, innerException);
}
