// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using GraphReader.Imaging;
using GraphReader.Pdf;

namespace GraphReader.App.Integration.Workflow;

public sealed class ProductionWorkflowImportStage : IWorkflowImportStage
{
    private static readonly HashSet<string> DetectorReadyMediaTypes = new(
        ["image/png", "image/jpeg", "image/tiff", "image/bmp", "image/webp"],
        StringComparer.OrdinalIgnoreCase);

    private readonly ProductionWorkflowPanelStore panelStore;
    private readonly IImageImportService imageImportService;
    private readonly IPdfImportService? pdfImportService;

    public ProductionWorkflowImportStage(
        ProductionWorkflowPanelStore panelStore,
        IImageImportService imageImportService,
        IPdfImportService? pdfImportService = null)
    {
        this.panelStore = panelStore ?? throw new ArgumentNullException(nameof(panelStore));
        this.imageImportService = imageImportService ?? throw new ArgumentNullException(nameof(imageImportService));
        this.pdfImportService = pdfImportService;
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
            IReadOnlyList<ProductionPanelEvidence> imported = source.Kind switch
            {
                WorkflowSourceKind.Image =>
                    [await ImportImageAsync(request.ProjectId, source, cancellationToken).ConfigureAwait(false)],
                WorkflowSourceKind.Pdf =>
                    await ImportPdfAsync(request.ProjectId, source, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(request), source.Kind, "Unsupported workflow source kind."),
            };

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

    private async Task<ProductionPanelEvidence> ImportImageAsync(
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
        Guid panelId = ProductionWorkflowPanelStore.CreateStableId(
            "image-panel-v1",
            projectId.ToString("D"),
            source.SourceId.ToString("D"),
            image.Sha256);
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
            image.OriginalBytes.Copy());
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
