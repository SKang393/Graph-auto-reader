// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using GraphReader.Domain;
using GraphReader.Export;
using GraphReader.Pdf;

namespace GraphReader.App.Integration.Workflow;

public static class ProductionWorkflowFailureCodes
{
    public const string ImageImportFailed = "WORKFLOW_IMAGE_IMPORT_FAILED";
    public const string PdfImportUnavailable = "WORKFLOW_PDF_IMPORT_UNAVAILABLE";
    public const string PdfImportFailed = "WORKFLOW_PDF_IMPORT_FAILED";
    public const string PdfPanelBytesUnavailable = "WORKFLOW_PDF_PANEL_BYTES_UNAVAILABLE";
    public const string DetectionModelsUnavailable = "WORKFLOW_DETECTION_MODELS_UNAVAILABLE";
    public const string DetectionEvidenceRejected = "WORKFLOW_DETECTION_EVIDENCE_REJECTED";
    public const string ReviewProjectionRejected = "WORKFLOW_REVIEW_PROJECTION_REJECTED";
    public const string RecalibrationRequired = "WORKFLOW_RECALIBRATION_REQUIRED";
}

public sealed record ProductionWorkflowFailure(
    string Code,
    string UserMessageKey,
    string TechnicalMessage,
    bool Recoverable,
    string SuggestedAction);

public sealed record ProductionReviewProjectionResult(
    bool Succeeded,
    int ProjectedPanelCount,
    int ProjectedPointCount,
    ProductionWorkflowFailure? Failure,
    WorkflowRunResult? ProjectedRun)
{
    public static ProductionReviewProjectionResult Success(
        int projectedPanelCount,
        int projectedPointCount,
        WorkflowRunResult projectedRun) =>
        new(true, projectedPanelCount, projectedPointCount, null,
            projectedRun ?? throw new ArgumentNullException(nameof(projectedRun)));

    public static ProductionReviewProjectionResult Rejected(ProductionWorkflowFailure failure) =>
        new(false, 0, 0, failure ?? throw new ArgumentNullException(nameof(failure)), null);
}

public sealed class ProductionWorkflowStageException : InvalidOperationException
{
    public ProductionWorkflowStageException(
        ProductionWorkflowFailure failure,
        IEnumerable<WorkflowVisionEnvelope>? completedEvidence = null)
        : base((failure ?? throw new ArgumentNullException(nameof(failure))).TechnicalMessage)
    {
        Failure = failure;
        CompletedEvidence = Array.AsReadOnly((completedEvidence ?? []).ToArray());
    }

    public ProductionWorkflowFailure Failure { get; }

    public IReadOnlyList<WorkflowVisionEnvelope> CompletedEvidence { get; }
}

public sealed class PdfPanelSourceProvenance : IEquatable<PdfPanelSourceProvenance>
{
    public PdfPanelSourceProvenance(
        string documentSha256,
        Guid pdfPanelId,
        Guid figureId,
        int pageNumber,
        PdfFigureSourceKind figureSourceKind,
        PdfRectD requestedCropInSourcePixels,
        PdfRectD encodedCropInSourcePixels,
        PdfAffineTransform sourcePixelsToPagePoints)
    {
        WorkflowContractGuards.RequireSha256(documentSha256, nameof(documentSha256));
        if (pdfPanelId == Guid.Empty || figureId == Guid.Empty || pageNumber < 1 ||
            !requestedCropInSourcePixels.IsValid || !encodedCropInSourcePixels.IsValid ||
            !ContainsWithinTolerance(encodedCropInSourcePixels, requestedCropInSourcePixels, 0.01d) ||
            !sourcePixelsToPagePoints.IsInvertible)
        {
            throw new ArgumentException("PDF panel source provenance must be finite and complete.");
        }

        DocumentSha256 = documentSha256.ToLowerInvariant();
        PdfPanelId = pdfPanelId;
        FigureId = figureId;
        PageNumber = pageNumber;
        FigureSourceKind = figureSourceKind;
        RequestedCropInSourcePixels = requestedCropInSourcePixels;
        EncodedCropInSourcePixels = encodedCropInSourcePixels;
        SourcePixelsToPagePoints = sourcePixelsToPagePoints;
    }

    public string DocumentSha256 { get; }

    public Guid PdfPanelId { get; }

    public Guid FigureId { get; }

    public int PageNumber { get; }

    public PdfFigureSourceKind FigureSourceKind { get; }

    public PdfRectD RequestedCropInSourcePixels { get; }

    public PdfRectD EncodedCropInSourcePixels { get; }

    public PdfAffineTransform SourcePixelsToPagePoints { get; }

    public PdfPointD MapPanelPixelToPagePoint(PdfPointD panelPixel)
    {
        if (!panelPixel.IsFinite || panelPixel.X < 0d || panelPixel.Y < 0d ||
            panelPixel.X > EncodedCropInSourcePixels.Width ||
            panelPixel.Y > EncodedCropInSourcePixels.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(panelPixel));
        }

        return SourcePixelsToPagePoints.Transform(new PdfPointD(
            panelPixel.X + EncodedCropInSourcePixels.X,
            panelPixel.Y + EncodedCropInSourcePixels.Y));
    }

    public bool Equals(PdfPanelSourceProvenance? other) =>
        other is not null &&
        string.Equals(DocumentSha256, other.DocumentSha256, StringComparison.OrdinalIgnoreCase) &&
        PdfPanelId == other.PdfPanelId &&
        FigureId == other.FigureId &&
        PageNumber == other.PageNumber &&
        FigureSourceKind == other.FigureSourceKind &&
        RequestedCropInSourcePixels == other.RequestedCropInSourcePixels &&
        EncodedCropInSourcePixels == other.EncodedCropInSourcePixels &&
        SourcePixelsToPagePoints == other.SourcePixelsToPagePoints;

    public override bool Equals(object? obj) => Equals(obj as PdfPanelSourceProvenance);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(DocumentSha256),
        PdfPanelId,
        FigureId,
        PageNumber,
        FigureSourceKind,
        RequestedCropInSourcePixels,
        EncodedCropInSourcePixels,
        SourcePixelsToPagePoints);

    private static bool ContainsWithinTolerance(PdfRectD container, PdfRectD value, double tolerance) =>
        value.X >= container.X - tolerance &&
        value.Y >= container.Y - tolerance &&
        value.Right <= container.Right + tolerance &&
        value.Bottom <= container.Bottom + tolerance;
}

public sealed class ProductionPanelEvidence
{
    private readonly byte[] originalBytes;
    private readonly byte[]? enhancedBytes;

    internal ProductionPanelEvidence(
        WorkflowImportedPanel panel,
        WorkflowSourceKind sourceKind,
        byte[] originalBytes,
        string? sourceDocumentSha256 = null,
        WorkflowImageEvidence? enhanced = null,
        byte[]? enhancedBytes = null,
        IEnumerable<WorkflowTransformProvenance>? enhancementTransforms = null,
        IEnumerable<string>? warnings = null,
        ProductionPanelExportEvidence? exportEvidence = null,
        PdfPanelSourceProvenance? pdfPanelSource = null,
        RasterPanelSourceProvenance? rasterPanelSource = null)
    {
        Panel = panel ?? throw new ArgumentNullException(nameof(panel));
        ArgumentNullException.ThrowIfNull(originalBytes);
        VerifyChecksum(originalBytes, panel.Original.Sha256, nameof(originalBytes));
        if (enhanced is null != (enhancedBytes is null))
        {
            throw new ArgumentException("Enhanced metadata and bytes must be supplied together.", nameof(enhanced));
        }

        if (enhanced is not null)
        {
            VerifyChecksum(enhancedBytes!, enhanced.Sha256, nameof(enhancedBytes));
        }

        SourceKind = sourceKind;
        this.originalBytes = (byte[])originalBytes.Clone();
        SourceDocumentSha256 = sourceDocumentSha256;
        Enhanced = enhanced;
        this.enhancedBytes = enhancedBytes is null ? null : (byte[])enhancedBytes.Clone();
        EnhancementTransforms = Freeze(enhancementTransforms ?? []);
        Warnings = Freeze(warnings ?? []);
        ExportEvidence = exportEvidence;
        if (pdfPanelSource is not null && sourceKind != WorkflowSourceKind.Pdf)
        {
            throw new ArgumentException("PDF source provenance is valid only for PDF panels.", nameof(pdfPanelSource));
        }

        if (pdfPanelSource is not null &&
            !string.Equals(pdfPanelSource.DocumentSha256, sourceDocumentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("PDF source provenance must match the retained document checksum.", nameof(pdfPanelSource));
        }

        PdfPanelSource = pdfPanelSource;
        if (rasterPanelSource is not null &&
            (sourceKind != WorkflowSourceKind.Image || panel.PageNumber is not null ||
             rasterPanelSource.PanelImageSha256 != panel.Original.Sha256 ||
             rasterPanelSource.EncodedCropInSourcePixels.Width != panel.Original.Width ||
             rasterPanelSource.EncodedCropInSourcePixels.Height != panel.Original.Height))
        {
            throw new ArgumentException("Raster source provenance must match the retained image panel.", nameof(rasterPanelSource));
        }
        RasterPanelSource = rasterPanelSource;
    }

    public WorkflowImportedPanel Panel { get; }

    public WorkflowSourceKind SourceKind { get; }

    public string? SourceDocumentSha256 { get; }

    public WorkflowImageEvidence? Enhanced { get; }

    public IReadOnlyList<WorkflowTransformProvenance> EnhancementTransforms { get; }

    public IReadOnlyList<string> Warnings { get; }

    public ProductionPanelExportEvidence? ExportEvidence { get; }

    public PdfPanelSourceProvenance? PdfPanelSource { get; }

    public RasterPanelSourceProvenance? RasterPanelSource { get; }

    public byte[] CopyOriginalBytes() => (byte[])originalBytes.Clone();

    public byte[]? CopyEnhancedBytes() => enhancedBytes is null ? null : (byte[])enhancedBytes.Clone();

    internal ProductionPanelEvidence WithPreparation(
        WorkflowImageEvidence? preparedEnhanced,
        byte[]? preparedEnhancedBytes,
        IEnumerable<WorkflowTransformProvenance> transforms,
        IEnumerable<string> warnings) =>
        new(
            Panel,
            SourceKind,
            originalBytes,
            SourceDocumentSha256,
            preparedEnhanced,
            preparedEnhancedBytes,
            transforms,
            warnings,
            ExportEvidence,
            PdfPanelSource,
            RasterPanelSource);

    internal ProductionPanelEvidence WithExportEvidence(ProductionPanelExportEvidence evidence) =>
        new(
            Panel,
            SourceKind,
            originalBytes,
            SourceDocumentSha256,
            Enhanced,
            enhancedBytes,
            EnhancementTransforms,
            Warnings,
            evidence,
            PdfPanelSource,
            RasterPanelSource);

    private static void VerifyChecksum(byte[] bytes, string expected, string parameterName)
    {
        string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Image bytes do not match the declared SHA-256 checksum.", parameterName);
        }
    }

    private static ReadOnlyCollection<T> Freeze<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());
}

public sealed record ProductionPointExportEvidence(
    Guid PointId,
    Guid? MarkerId,
    int ObservationIndex,
    double? PrintedXValue,
    double? EstimatedXValue,
    ExportXValueSource XSource,
    double XConfidence,
    double YConfidence);

public sealed class ProductionPanelProjectionEvidence
{
    public ProductionPanelProjectionEvidence(
        CalibrationRecord calibration,
        IEnumerable<PhaseRecord> phases,
        IEnumerable<SeriesRecord> series,
        IEnumerable<PointRecord> points,
        IEnumerable<TransformRecord>? transforms = null,
        IEnumerable<OcrEvidence>? ocrRegions = null,
        IEnumerable<MarkerRecord>? markers = null,
        string? participant = null)
    {
        Calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        Phases = Array.AsReadOnly((phases ?? throw new ArgumentNullException(nameof(phases))).ToArray());
        Series = Array.AsReadOnly((series ?? throw new ArgumentNullException(nameof(series))).ToArray());
        Points = Array.AsReadOnly((points ?? throw new ArgumentNullException(nameof(points))).ToArray());
        Transforms = Array.AsReadOnly((transforms ?? []).ToArray());
        OcrRegions = Array.AsReadOnly((ocrRegions ?? []).ToArray());
        Markers = Array.AsReadOnly((markers ?? []).ToArray());
        Participant = participant;
    }

    public CalibrationRecord Calibration { get; }

    public IReadOnlyList<PhaseRecord> Phases { get; }

    public IReadOnlyList<SeriesRecord> Series { get; }

    public IReadOnlyList<PointRecord> Points { get; }

    public IReadOnlyList<TransformRecord> Transforms { get; }

    public IReadOnlyList<OcrEvidence> OcrRegions { get; }

    public IReadOnlyList<MarkerRecord> Markers { get; }

    public string? Participant { get; }
}

public sealed class ProductionPanelExportEvidence
{
    public ProductionPanelExportEvidence(
        ExportCalibration calibration,
        IEnumerable<ExportPhase> phases,
        IEnumerable<ExportSeries> series,
        IEnumerable<ExportSeriesRelation> relations,
        IEnumerable<ProductionPointExportEvidence> points,
        IEnumerable<WorkflowVisionEnvelope> provenance,
        string? participant = null,
        ExportMode mode = ExportMode.PrintedSession,
        ExportAuditMode auditMode = ExportAuditMode.ExtendedCsv,
        ExportSessionOriginPolicy? sessionOriginPolicy = null,
        ProductionPanelProjectionEvidence? projectionEvidence = null)
    {
        Calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        Phases = Freeze(phases);
        Series = Freeze(series);
        Relations = Freeze(relations);
        Points = Freeze(points);
        Provenance = Freeze(provenance);
        Participant = participant;
        Mode = mode;
        AuditMode = auditMode;
        SessionOriginPolicy = sessionOriginPolicy ?? ExportSessionOriginPolicy.Default;
        ProjectionEvidence = projectionEvidence;
        if (Points.Select(static point => point.PointId).Distinct().Count() != Points.Count)
        {
            throw new ArgumentException("Point export evidence IDs must be unique.", nameof(points));
        }
    }

    public ExportCalibration Calibration { get; }

    public IReadOnlyList<ExportPhase> Phases { get; }

    public IReadOnlyList<ExportSeries> Series { get; }

    public IReadOnlyList<ExportSeriesRelation> Relations { get; }

    public IReadOnlyList<ProductionPointExportEvidence> Points { get; }

    public IReadOnlyList<WorkflowVisionEnvelope> Provenance { get; }

    public string? Participant { get; }

    public ExportMode Mode { get; }

    public ExportAuditMode AuditMode { get; }

    public ExportSessionOriginPolicy SessionOriginPolicy { get; }

    public ProductionPanelProjectionEvidence? ProjectionEvidence { get; }

    private static ReadOnlyCollection<T> Freeze<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ReadOnlyCollection<T>(values.ToArray());
    }
}

public sealed class ProductionWorkflowPanelStore
{
    private readonly ConcurrentDictionary<Guid, ProductionPanelEvidence> panels = new();

    public IReadOnlyList<Guid> PanelIds => panels.Keys.Order().ToArray();

    public ProductionPanelEvidence Get(Guid panelId) =>
        panels.TryGetValue(panelId, out ProductionPanelEvidence? evidence)
            ? evidence
            : throw new KeyNotFoundException($"No production panel evidence exists for '{panelId}'.");

    public bool TryGet(Guid panelId, out ProductionPanelEvidence? evidence) =>
        panels.TryGetValue(panelId, out evidence);

    public void Register(ProductionPanelEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        panels.AddOrUpdate(
            evidence.Panel.PanelId,
            evidence,
            (_, current) => HasSameImmutableIdentity(current, evidence)
                ? current
                : throw new InvalidOperationException(
                    $"Panel '{evidence.Panel.PanelId}' conflicts with retained immutable source evidence."));
    }

    public void SetPreparation(
        Guid panelId,
        WorkflowImageEvidence? enhanced,
        byte[]? enhancedBytes,
        IEnumerable<WorkflowTransformProvenance> transforms,
        IEnumerable<string> warnings) =>
        panels.AddOrUpdate(
            panelId,
            static id => throw new KeyNotFoundException($"No production panel evidence exists for '{id}'."),
            (_, current) => current.WithPreparation(enhanced, enhancedBytes, transforms, warnings));

    public void SetExportEvidence(Guid panelId, ProductionPanelExportEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        panels.AddOrUpdate(
            panelId,
            static id => throw new KeyNotFoundException($"No production panel evidence exists for '{id}'."),
            (_, current) => current.WithExportEvidence(evidence));
    }

    internal static Guid CreateStableId(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string canonical = string.Join("\u001f", values);
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static bool HasSameImmutableIdentity(
        ProductionPanelEvidence left,
        ProductionPanelEvidence right)
    {
        byte[] leftBytes = left.CopyOriginalBytes();
        byte[] rightBytes = right.CopyOriginalBytes();
        return left.SourceKind == right.SourceKind &&
            left.Panel.SourceId == right.Panel.SourceId &&
            left.Panel.PageNumber == right.Panel.PageNumber &&
            left.Panel.Original.Width == right.Panel.Original.Width &&
            left.Panel.Original.Height == right.Panel.Original.Height &&
            string.Equals(left.Panel.Original.Sha256, right.Panel.Original.Sha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.SourceDocumentSha256, right.SourceDocumentSha256, StringComparison.OrdinalIgnoreCase) &&
            Equals(left.PdfPanelSource, right.PdfPanelSource) &&
            Equals(left.RasterPanelSource, right.RasterPanelSource) &&
            leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
