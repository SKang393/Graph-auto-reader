// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.Imaging;
using GraphReader.Pdf;
using Imazen.WebP;

namespace GraphReader.App.Integration.Workflow;

public sealed class ProductionWorkflowImportStage : IWorkflowImportStage
{
    private const long MaximumRasterEncodedBytes = 256L * 1024L * 1024L;
    private const long MaximumRasterPixels = 40_000_000L;
    private static readonly HashSet<string> DetectorReadyMediaTypes = new(
        ["image/png", "image/jpeg", "image/tiff", "image/bmp", "image/webp"],
        StringComparer.OrdinalIgnoreCase);

    private readonly ProductionWorkflowPanelStore panelStore;
    private readonly IImageImportService imageImportService;
    private readonly IPdfImportService? pdfImportService;
    private readonly ProductionRasterPanelizer rasterPanelizer;

    public ProductionWorkflowImportStage(
        ProductionWorkflowPanelStore panelStore,
        IImageImportService imageImportService,
        IPdfImportService? pdfImportService = null)
        : this(panelStore, imageImportService, pdfImportService, new ProductionRasterPanelizer())
    {
    }

    internal ProductionWorkflowImportStage(
        ProductionWorkflowPanelStore panelStore,
        IImageImportService imageImportService,
        IPdfImportService? pdfImportService,
        ProductionRasterPanelizer rasterPanelizer)
    {
        this.panelStore = panelStore ?? throw new ArgumentNullException(nameof(panelStore));
        this.imageImportService = imageImportService ?? throw new ArgumentNullException(nameof(imageImportService));
        this.pdfImportService = pdfImportService;
        this.rasterPanelizer = rasterPanelizer ?? throw new ArgumentNullException(nameof(rasterPanelizer));
    }

    public async Task<WorkflowImportSnapshot> ImportAsync(
        WorkflowImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new List<ProductionPanelEvidence>();
        var warnings = new List<string>();

        foreach (WorkflowSourceRequest source in request.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ProductionPanelEvidence> imported;
            if (source.RetainedRaster is { } retained)
            {
                WorkflowImportSnapshot restored = await RestoreRasterPanelsAsync(request.ProjectId, source,
                    retained.SourceSha256, retained.Panels, cancellationToken).ConfigureAwait(false);
                imported = restored.Panels.Select(panel => panelStore.Get(panel.PanelId)).ToArray();
            }
            else
            {
                imported = source.Kind switch
            {
                WorkflowSourceKind.Image =>
                    await ImportImageAsync(request.ProjectId, source, cancellationToken).ConfigureAwait(false),
                WorkflowSourceKind.Pdf =>
                    await ImportPdfAsync(request.ProjectId, source, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(request), source.Kind, "Unsupported workflow source kind."),
            };
            }

            pending.AddRange(imported);
            warnings.AddRange(imported.SelectMany(static panel => panel.Warnings));
            foreach (ProductionPanelEvidence evidence in imported)
            {
                panelStore.Register(evidence);
            }
        }

        return new WorkflowImportSnapshot(
            request.ProjectId,
            pending.Select(static evidence => evidence.Panel),
            warnings.Distinct(StringComparer.Ordinal));
    }

    internal async Task<WorkflowImportSnapshot> RestoreRasterPanelsAsync(
        Guid projectId,
        WorkflowSourceRequest source,
        string expectedSourceSha256,
        IReadOnlyList<RetainedRasterPanelReference> retainedPanels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(retainedPanels);
        WorkflowContractGuards.RequireSha256(expectedSourceSha256, nameof(expectedSourceSha256));
        if (source.Kind != WorkflowSourceKind.Image || projectId == Guid.Empty || retainedPanels.Count == 0 ||
            retainedPanels.Any(static panel => panel.PanelId == Guid.Empty) ||
            retainedPanels.Select(static panel => panel.PanelId).Distinct().Count() != retainedPanels.Count)
        {
            throw new ArgumentException("Saved raster panels require unique identities and an image source.");
        }
        ImageImportResult result = await imageImportService.ImportAsync(source.Path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.IsSuccess || result.Image is not { } image)
        {
            throw Failure(ProductionWorkflowFailureCodes.ImageImportFailed, "Errors.ImageReadFailed",
                result.Error?.TechnicalMessage ?? "Saved raster source could not be read.", true, "Select the original source image.");
        }
        if (!string.Equals(image.Sha256, expectedSourceSha256, StringComparison.OrdinalIgnoreCase) ||
            retainedPanels.Any(panel => panel.SourceWidth != image.Metadata.Width || panel.SourceHeight != image.Metadata.Height))
        {
            throw Failure(ProductionWorkflowFailureCodes.ImageImportFailed, "Errors.ImageCorrupt",
                "Saved raster source checksum or dimensions no longer match.", true, "Select the unchanged original source image.");
        }
        try
        {
            IReadOnlyList<ProductionPanelEvidence> restored = await Task.Run(() =>
            {
                byte[] fullBytes = image.OriginalBytes.Copy();
                var sourceEvidence = new RasterSourceImageEvidence(new WorkflowImageEvidence(image.SourcePath,
                    image.Sha256, image.Metadata.Width, image.Metadata.Height, WorkflowImageVariant.Original),
                    new ImmutableByteBuffer(fullBytes));
                CanonicalRasterSource canonical = image.Metadata.Format == ImageFileFormat.Png
                    ? new CanonicalRasterSource(fullBytes, image.Sha256)
                    : CreateCanonicalPng(image, fullBytes, cancellationToken);
                byte[] canonicalBytes = canonical.CopyBytes();
                BitmapSource decoded = ProductionRasterPanelizer.DecodeValidatedSource(canonicalBytes, canonical.Sha256,
                    image.Metadata.Width, image.Metadata.Height, cancellationToken);
                var output = new List<ProductionPanelEvidence>(retainedPanels.Count);
                var requestedCrops = new List<PdfRectD>(retainedPanels.Count);
                var encodedCrops = new List<PdfRectD>(retainedPanels.Count);
                foreach (RetainedRasterPanelReference retained in retainedPanels)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var provenance = new RasterPanelSourceProvenance(sourceEvidence, retained.PanelImageSha256,
                        retained.RequestedCropInSourcePixels, retained.EncodedCropInSourcePixels);
                    PdfRectD crop = retained.EncodedCropInSourcePixels;
                    ProductionRasterPanelizer.EnsureNoOverlap(requestedCrops, retained.RequestedCropInSourcePixels, "requested", []);
                    ProductionRasterPanelizer.EnsureNoOverlap(encodedCrops, crop, "encoded", []);
                    requestedCrops.Add(retained.RequestedCropInSourcePixels);
                    encodedCrops.Add(crop);
                    bool fullCrop = crop == new PdfRectD(0, 0, image.Metadata.Width, image.Metadata.Height);
                    byte[] cropBytes = fullCrop && string.Equals(retained.PanelImageSha256, image.Sha256, StringComparison.OrdinalIgnoreCase)
                        ? fullBytes
                        : fullCrop ? canonicalBytes : EncodeIntegerCrop(decoded, crop, cancellationToken);
                    string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(cropBytes));
                    if (!string.Equals(actualSha256, retained.PanelImageSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Saved raster panel checksum was not reproduced from the retained source crop.");
                    }
                    var original = new WorkflowImageEvidence(CreateRasterCropReference(image.SourcePath, retained.PanelId, crop),
                        actualSha256, checked((int)crop.Width), checked((int)crop.Height), WorkflowImageVariant.Original);
                    var panel = new WorkflowImportedPanel(retained.PanelId, source.SourceId, retained.DisplayName, original);
                    output.Add(new ProductionPanelEvidence(panel, WorkflowSourceKind.Image, cropBytes, rasterPanelSource: provenance));
                }
                return (IReadOnlyList<ProductionPanelEvidence>)output;
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (ProductionPanelEvidence panel in restored)
            {
                cancellationToken.ThrowIfCancellationRequested();
                panelStore.Register(panel);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new WorkflowImportSnapshot(projectId, restored.Select(static panel => panel.Panel));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException or
            FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw Failure(ProductionWorkflowFailureCodes.ImageImportFailed, "Errors.ImageCorrupt",
                $"Saved raster panel restoration failed: {exception.Message}", true, "Select the unchanged original source image.");
        }
    }

    private static byte[] EncodeIntegerCrop(BitmapSource source, PdfRectD crop, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cropped = new CroppedBitmap(source, new Int32Rect(checked((int)crop.X), checked((int)crop.Y),
            checked((int)crop.Width), checked((int)crop.Height)));
        cancellationToken.ThrowIfCancellationRequested();
        cropped.Freeze();
        cancellationToken.ThrowIfCancellationRequested();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new MemoryStream();
        cancellationToken.ThrowIfCancellationRequested();
        encoder.Save(output);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = output.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    private async Task<IReadOnlyList<ProductionPanelEvidence>> ImportImageAsync(
        Guid projectId,
        WorkflowSourceRequest source,
        CancellationToken cancellationToken)
    {
        ImageImportResult result = await imageImportService
            .ImportAsync(source.Path, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.IsSuccess || result.Image is null)
        {
            ImageImportError? error = result.Error;
            throw Failure(
                ProductionWorkflowFailureCodes.ImageImportFailed,
                error?.UserMessageKey ?? "Errors.ImageReadFailed",
                error?.TechnicalMessage ?? $"Image import returned no image for '{source.Path}'.",
                error?.Recoverable ?? true,
                (error?.SuggestedAction ?? ImageSuggestedAction.Retry).ToString());
        }

        ImportedImage image = result.Image;
        byte[] fullSourceBytes = image.OriginalBytes.Copy();
        var fullSourceImage = new WorkflowImageEvidence(
            image.SourcePath,
            image.Sha256,
            image.Metadata.Width,
            image.Metadata.Height,
            WorkflowImageVariant.Original);
        var sourceEvidence = new RasterSourceImageEvidence(
            fullSourceImage,
            new ImmutableByteBuffer(fullSourceBytes));

        CanonicalRasterSource panelizationSource;
        try
        {
            panelizationSource = image.Metadata.Format == ImageFileFormat.Png
                ? new CanonicalRasterSource(fullSourceBytes, image.Sha256)
                : await Task.Run(
                        () => CreateCanonicalPng(image, fullSourceBytes, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            NotSupportedException or FormatException or ArgumentException or InvalidOperationException or
            OverflowException)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.ImageImportFailed,
                "Errors.ImageCorrupt",
                $"Image '{source.Path}' could not produce a lossless detector-ready PNG: {exception.Message}",
                recoverable: true,
                "Retry the import or select a readable image.");
        }

        ProductionRasterPanelizationResult panelization;
        try
        {
            panelization = await rasterPanelizer.PanelizeAsync(
                    new ImmutableByteBuffer(panelizationSource.CopyBytes()),
                    panelizationSource.Sha256,
                    image.Metadata.Width,
                    image.Metadata.Height,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProductionRasterPanelizationException exception) when (IsGenuineNoPanelResult(exception))
        {
            return
            [
                CreateFullImageReviewPanel(
                    projectId,
                    source,
                    image,
                    fullSourceBytes,
                    sourceEvidence,
                    exception.Warnings),
            ];
        }
        catch (ProductionRasterPanelizationException exception)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.ImageImportFailed,
                "Errors.ImageCorrupt",
                $"Image '{source.Path}' could not be panelized ({exception.Code}): {exception.Message}",
                recoverable: true,
                "Retry the import or select a readable image.");
        }

        string fileName = Path.GetFileName(image.SourcePath);
        var imported = new List<ProductionPanelEvidence>(panelization.Panels.Count);
        foreach (ProductionRasterPanel rasterPanel in panelization.Panels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] panelBytes = rasterPanel.CopyEncodedBytes();
            Guid panelId = ProductionWorkflowPanelStore.CreateStableId(
                "image-panel-v2",
                projectId.ToString("D"),
                source.SourceId.ToString("D"),
                rasterPanel.PanelId.ToString("D"));
            PdfRectD encodedCrop = rasterPanel.EncodedCropInSourcePixels;
            string reference = CreateRasterCropReference(image.SourcePath, panelId, encodedCrop);
            var original = new WorkflowImageEvidence(
                reference,
                rasterPanel.Sha256,
                rasterPanel.Width,
                rasterPanel.Height,
                WorkflowImageVariant.Original);
            var panel = new WorkflowImportedPanel(
                panelId,
                source.SourceId,
                panelization.Panels.Count == 1 ? fileName : $"{fileName} ({rasterPanel.Order})",
                original);
            imported.Add(new ProductionPanelEvidence(
                panel,
                WorkflowSourceKind.Image,
                panelBytes,
                warnings: panelization.Warnings,
                rasterPanelSource: new RasterPanelSourceProvenance(
                    sourceEvidence,
                    rasterPanel.Sha256,
                    rasterPanel.RequestedCropInSourcePixels,
                    encodedCrop)));
        }

        return imported;
    }

    private static ProductionPanelEvidence CreateFullImageReviewPanel(
        Guid projectId,
        WorkflowSourceRequest source,
        ImportedImage image,
        byte[] fullSourceBytes,
        RasterSourceImageEvidence sourceEvidence,
        IReadOnlyList<string> warnings)
    {
        var fullCrop = new PdfRectD(0d, 0d, image.Metadata.Width, image.Metadata.Height);
        Guid helperPanelId = ProductionWorkflowPanelStore.CreateStableId(
            "raster-full-image-review-v1",
            image.Sha256,
            image.Metadata.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            image.Metadata.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Guid panelId = ProductionWorkflowPanelStore.CreateStableId(
            "image-panel-v2",
            projectId.ToString("D"),
            source.SourceId.ToString("D"),
            helperPanelId.ToString("D"));
        var original = new WorkflowImageEvidence(
            image.SourcePath,
            image.Sha256,
            image.Metadata.Width,
            image.Metadata.Height,
            WorkflowImageVariant.Original);
        var panel = new WorkflowImportedPanel(
            panelId,
            source.SourceId,
            Path.GetFileName(image.SourcePath),
            original);
        return new ProductionPanelEvidence(
            panel,
            WorkflowSourceKind.Image,
            fullSourceBytes,
            warnings: warnings,
            rasterPanelSource: new RasterPanelSourceProvenance(
                sourceEvidence,
                image.Sha256,
                fullCrop,
                fullCrop));
    }

    private static bool IsGenuineNoPanelResult(ProductionRasterPanelizationException exception) =>
        exception.Code == ProductionRasterPanelizationFailureCodes.NoPanelDetected;

    private static string CreateRasterCropReference(
        string sourcePath,
        Guid panelId,
        PdfRectD crop) =>
        FormattableString.Invariant(
            $"image:{Path.GetFullPath(sourcePath)}#panel={panelId:D}&crop={crop.X:R},{crop.Y:R},{crop.Width:R},{crop.Height:R}");

    private static CanonicalRasterSource CreateCanonicalPng(
        ImportedImage image,
        byte[] sourceBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceBytes.LongLength > MaximumRasterEncodedBytes)
        {
            throw new InvalidDataException(
                $"Raster panelization is limited to {MaximumRasterEncodedBytes} encoded bytes.");
        }

        if ((long)image.Metadata.Width * image.Metadata.Height > MaximumRasterPixels)
        {
            throw new InvalidDataException(
                $"Raster panelization is limited to {MaximumRasterPixels} decoded pixels.");
        }

        byte[] bgra;
        double dpiX = image.Metadata.DpiX > 0 ? image.Metadata.DpiX : 96d;
        double dpiY = image.Metadata.DpiY > 0 ? image.Metadata.DpiY : 96d;
        if (image.Metadata.Format == ImageFileFormat.WebP)
        {
            // libwebp cannot be interrupted while native decoding is running. Pixel and
            // encoded-byte limits plus the adjacent checkpoints bound the accepted work.
            cancellationToken.ThrowIfCancellationRequested();
            bgra = WebPDecoder.Decode(sourceBytes, out int width, out int height, WebPPixelFormat.Bgra);
            cancellationToken.ThrowIfCancellationRequested();
            if (width != image.Metadata.Width || height != image.Metadata.Height)
            {
                throw new InvalidDataException("Decoded WebP dimensions do not match imported source metadata.");
            }
        }
        else
        {
            using var input = new MemoryStream(sourceBytes, writable: false);
            // WPF codec calls cannot be interrupted while native decoding is running.
            cancellationToken.ThrowIfCancellationRequested();
            BitmapDecoder decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            cancellationToken.ThrowIfCancellationRequested();
            BitmapSource frame = decoder.Frames[0];
            if (frame.PixelWidth != image.Metadata.Width || frame.PixelHeight != image.Metadata.Height)
            {
                throw new InvalidDataException("Decoded raster dimensions do not match imported source metadata.");
            }

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0d);
            converted.Freeze();
            int stride = checked(image.Metadata.Width * 4);
            bgra = new byte[checked(stride * image.Metadata.Height)];
            cancellationToken.ThrowIfCancellationRequested();
            converted.CopyPixels(bgra, stride, 0);
            cancellationToken.ThrowIfCancellationRequested();
        }

        CompositeOntoWhite(bgra, cancellationToken);
        int outputStride = checked(image.Metadata.Width * 4);
        BitmapSource canonical = BitmapSource.Create(
            image.Metadata.Width,
            image.Metadata.Height,
            dpiX,
            dpiY,
            PixelFormats.Bgra32,
            palette: null,
            bgra,
            outputStride);
        canonical.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(canonical));
        using var output = new MemoryStream();
        cancellationToken.ThrowIfCancellationRequested();
        encoder.Save(output);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encoded = output.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new CanonicalRasterSource(
            encoded,
            Convert.ToHexStringLower(SHA256.HashData(encoded)));
    }

    private static void CompositeOntoWhite(byte[] bgra, CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < bgra.Length; offset += 4)
        {
            if ((offset & 0x3ffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int alpha = bgra[offset + 3];
            if (alpha != byte.MaxValue)
            {
                bgra[offset] = CompositeChannel(bgra[offset], alpha);
                bgra[offset + 1] = CompositeChannel(bgra[offset + 1], alpha);
                bgra[offset + 2] = CompositeChannel(bgra[offset + 2], alpha);
                bgra[offset + 3] = byte.MaxValue;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static byte CompositeChannel(byte channel, int alpha) =>
        checked((byte)(((channel * alpha) + (255 * (255 - alpha)) + 127) / 255));

    private sealed class CanonicalRasterSource
    {
        private readonly byte[] bytes;

        public CanonicalRasterSource(byte[] bytes, string sha256)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
            this.bytes = (byte[])bytes.Clone();
            Sha256 = sha256;
        }

        public string Sha256 { get; }

        public byte[] CopyBytes() => (byte[])bytes.Clone();
    }

    private async Task<IReadOnlyList<ProductionPanelEvidence>> ImportPdfAsync(
        Guid projectId,
        WorkflowSourceRequest source,
        CancellationToken cancellationToken)
    {
        if (pdfImportService is null)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.PdfImportUnavailable,
                "Errors.PdfRendererUnavailable",
                "No production PDF import service is configured.",
                recoverable: true,
                "Configure the reviewed local PDF inspector and renderer or import an image.");
        }

        byte[] pdfBytes;
        try
        {
            pdfBytes = await File.ReadAllBytesAsync(source.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.PdfImportFailed,
                "Errors.PdfReadFailed",
                exception.Message,
                recoverable: true,
                "Retry the import or select a readable PDF.");
        }

        string documentSha256 = Convert.ToHexString(SHA256.HashData(pdfBytes)).ToLowerInvariant();
        Guid pdfRunId = ProductionWorkflowPanelStore.CreateStableId(
            "pdf-import-run-v1",
            projectId.ToString("D"),
            source.SourceId.ToString("D"),
            documentSha256);
        PdfImportResult result = await pdfImportService.ImportAsync(
                new PdfImportRequest(
                    pdfRunId,
                    projectId,
                    new ImmutableByteBuffer(pdfBytes),
                    Path.GetFileName(source.Path),
                    Password: null,
                    new PdfPanelizationOptions()),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        PdfFailure? error = result.Failures.FirstOrDefault(static failure =>
            failure.Severity == PdfFailureSeverity.Error);
        if (!result.Succeeded || error is not null)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.PdfImportFailed,
                error?.UserMessageKey ?? "Errors.PdfImportFailed",
                error?.TechnicalMessage ?? "The PDF importer did not return a usable document.",
                error?.Recoverable ?? true,
                error?.SuggestedAction ?? "Retry the import or import a detector-ready image.");
        }

        var figures = result.Figures.ToDictionary(static figure => figure.FigureId);
        var imported = new List<ProductionPanelEvidence>(result.Panels.Count);
        foreach (PdfPanelRecord pdfPanel in result.Panels
                     .OrderBy(static panel => panel.PageNumber)
                     .ThenBy(static panel => panel.Order)
                     .ThenBy(static panel => panel.PanelId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!figures.TryGetValue(pdfPanel.FigureId, out PdfFigureCandidate? figure))
            {
                throw Failure(
                    ProductionWorkflowFailureCodes.PdfPanelBytesUnavailable,
                    "Errors.PdfPanelBytesUnavailable",
                    $"PDF panel '{pdfPanel.PanelId}' does not reference a retained figure.",
                    recoverable: true,
                    "Render or extract the panel through a reviewed local PDF renderer, or import the panel as an image.");
            }

            EncodedRasterCrop detectorPanel;
            try
            {
                detectorPanel = CreateDetectorReadyCrop(pdfPanel, figure, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or
                NotSupportedException or FormatException or ArgumentException or OverflowException)
            {
                throw Failure(
                    ProductionWorkflowFailureCodes.PdfPanelBytesUnavailable,
                    "Errors.PdfPanelBytesUnavailable",
                    $"PDF panel '{pdfPanel.PanelId}' could not produce detector-ready bytes: {exception.Message}",
                    recoverable: true,
                    "Render or extract the panel through a reviewed local PDF renderer, or import the panel as an image.");
            }

            byte[] encoded = detectorPanel.CopyBytes();
            string imageSha256 = Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant();
            Guid panelId = ProductionWorkflowPanelStore.CreateStableId(
                "pdf-panel-v1",
                projectId.ToString("D"),
                source.SourceId.ToString("D"),
                documentSha256,
                pdfPanel.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                pdfPanel.Order.ToString(System.Globalization.CultureInfo.InvariantCulture),
                imageSha256);
            PdfRectD encodedCrop = detectorPanel.EncodedCropInSourcePixels;
            string reference = FormattableString.Invariant(
                $"pdf:{Path.GetFullPath(source.Path)}#page={pdfPanel.PageNumber}&panel={panelId:D}&crop={encodedCrop.X:R},{encodedCrop.Y:R},{encodedCrop.Width:R},{encodedCrop.Height:R}");
            var original = new WorkflowImageEvidence(
                reference,
                imageSha256,
                detectorPanel.Width,
                detectorPanel.Height,
                WorkflowImageVariant.Original);
            var panel = new WorkflowImportedPanel(
                panelId,
                source.SourceId,
                $"{Path.GetFileName(source.Path)} - page {pdfPanel.PageNumber}, panel {pdfPanel.Order + 1}",
                original,
                pdfPanel.PageNumber);
            imported.Add(new ProductionPanelEvidence(
                panel,
                WorkflowSourceKind.Pdf,
                encoded,
                documentSha256,
                warnings: result.Warnings,
                pdfPanelSource: new PdfPanelSourceProvenance(
                    documentSha256,
                    pdfPanel.PanelId,
                    figure.FigureId,
                    pdfPanel.PageNumber,
                    figure.SourceKind,
                    pdfPanel.CropInSourcePixels,
                    detectorPanel.EncodedCropInSourcePixels,
                    figure.SourcePixelsToPagePoints)));
        }

        if (imported.Count == 0)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.PdfPanelBytesUnavailable,
                "Errors.PdfPanelBytesUnavailable",
                "The PDF importer returned no detector-ready panel bytes.",
                recoverable: true,
                "Import a graph image or configure the reviewed scanned-PDF renderer.");
        }

        return imported;
    }

    internal static EncodedRasterCrop CreateDetectorReadyCrop(
        PdfPanelRecord panel,
        PdfFigureCandidate figure,
        CancellationToken cancellationToken,
        BitmapSource? decodedSource = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (figure.EncodedSource is null || figure.EncodedSource.Length == 0 ||
            string.IsNullOrWhiteSpace(figure.MediaType) ||
            !DetectorReadyMediaTypes.Contains(figure.MediaType) ||
            figure.SourcePixelWidth <= 0 || figure.SourcePixelHeight <= 0)
        {
            throw new InvalidDataException("The referenced figure has no supported encoded source image.");
        }

        const double tolerance = 0.01d;
        PdfRectD crop = panel.CropInSourcePixels;
        if (!crop.IsValid || crop.X < -tolerance || crop.Y < -tolerance ||
            crop.Right > figure.SourcePixelWidth + tolerance ||
            crop.Bottom > figure.SourcePixelHeight + tolerance)
        {
            throw new InvalidDataException("The requested panel crop is outside the encoded figure bounds.");
        }

        double normalizedLeft = Math.Clamp(crop.X, 0d, figure.SourcePixelWidth);
        double normalizedTop = Math.Clamp(crop.Y, 0d, figure.SourcePixelHeight);
        double normalizedRight = Math.Clamp(crop.Right, 0d, figure.SourcePixelWidth);
        double normalizedBottom = Math.Clamp(crop.Bottom, 0d, figure.SourcePixelHeight);
        var normalizedCrop = new PdfRectD(
            normalizedLeft,
            normalizedTop,
            normalizedRight - normalizedLeft,
            normalizedBottom - normalizedTop);
        bool fullPanel = Math.Abs(normalizedCrop.X) <= tolerance &&
            Math.Abs(normalizedCrop.Y) <= tolerance &&
            Math.Abs(normalizedCrop.Width - figure.SourcePixelWidth) <= tolerance &&
            Math.Abs(normalizedCrop.Height - figure.SourcePixelHeight) <= tolerance;
        if (fullPanel)
        {
            byte[] fullSource = figure.EncodedSource.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            var fullCrop = new EncodedRasterCrop(
                fullSource,
                figure.SourcePixelWidth,
                figure.SourcePixelHeight,
                new PdfRectD(0d, 0d, figure.SourcePixelWidth, figure.SourcePixelHeight));
            cancellationToken.ThrowIfCancellationRequested();
            return fullCrop;
        }

        if (!IsAxisAlignedCrop(panel.CropInSourcePixelsQuadrilateral, crop, tolerance))
        {
            throw new NotSupportedException(
                "The panel crop requires a non-axis-aligned transform that has not been applied.");
        }

        int left = checked((int)Math.Floor(normalizedCrop.X));
        int top = checked((int)Math.Floor(normalizedCrop.Y));
        int right = checked((int)Math.Ceiling(normalizedCrop.Right));
        int bottom = checked((int)Math.Ceiling(normalizedCrop.Bottom));
        int width = checked(right - left);
        int height = checked(bottom - top);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("The panel crop rounds to an empty detector image.");
        }

        BitmapSource source;
        if (decodedSource is null)
        {
            byte[] sourceBytes = figure.EncodedSource.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            using var input = new MemoryStream(sourceBytes, writable: false);
            // WPF codec calls cannot be interrupted while native code is running. The
            // checkpoints bound cancellation latency before and immediately after them.
            BitmapDecoder decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            cancellationToken.ThrowIfCancellationRequested();
            source = decoder.Frames[0];
        }
        else
        {
            source = decodedSource;
        }

        if (source.PixelWidth != figure.SourcePixelWidth || source.PixelHeight != figure.SourcePixelHeight)
        {
            throw new InvalidDataException(
                "The encoded figure dimensions do not match the retained PDF figure metadata.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var cropped = new CroppedBitmap(source, new Int32Rect(left, top, width, height));
        cancellationToken.ThrowIfCancellationRequested();
        cropped.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));
        using var output = new MemoryStream();
        cancellationToken.ThrowIfCancellationRequested();
        encoder.Save(output);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encodedBytes = output.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var encodedCrop = new EncodedRasterCrop(
            encodedBytes,
            width,
            height,
            new PdfRectD(left, top, width, height));
        cancellationToken.ThrowIfCancellationRequested();
        return encodedCrop;
    }

    private static bool IsAxisAlignedCrop(
        PdfQuadrilateralD quadrilateral,
        PdfRectD crop,
        double tolerance) =>
        Math.Abs(quadrilateral.TopLeft.X - crop.X) <= tolerance &&
        Math.Abs(quadrilateral.TopLeft.Y - crop.Y) <= tolerance &&
        Math.Abs(quadrilateral.TopRight.X - crop.Right) <= tolerance &&
        Math.Abs(quadrilateral.TopRight.Y - crop.Y) <= tolerance &&
        Math.Abs(quadrilateral.BottomRight.X - crop.Right) <= tolerance &&
        Math.Abs(quadrilateral.BottomRight.Y - crop.Bottom) <= tolerance &&
        Math.Abs(quadrilateral.BottomLeft.X - crop.X) <= tolerance &&
        Math.Abs(quadrilateral.BottomLeft.Y - crop.Bottom) <= tolerance;

    private static ProductionWorkflowStageException Failure(
        string code,
        string userMessageKey,
        string technicalMessage,
        bool recoverable,
        string suggestedAction) =>
        new(new ProductionWorkflowFailure(
            code,
            userMessageKey,
            technicalMessage,
            recoverable,
            suggestedAction));

}
