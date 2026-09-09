// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Axis;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

public sealed record ProductionOcrModelEvidence(
    string Task,
    WorkflowVisionEnvelope Envelope);

public sealed class ProductionDetectionMaskSeed
{
    private readonly float[] ocrMask;
    private readonly float[] artifactMask;

    internal ProductionDetectionMaskSeed(
        int width,
        int height,
        string rasterSha256,
        WorkflowImageVariant rasterVariant,
        float[] ocrMask,
        float[] artifactMask)
    {
        ArgumentNullException.ThrowIfNull(ocrMask);
        ArgumentNullException.ThrowIfNull(artifactMask);
        int expectedLength = checked(width * height);
        if (width <= 0 || height <= 0 ||
            ocrMask.Length != expectedLength || artifactMask.Length != expectedLength ||
            ocrMask.AsSpan().ContainsAnyExceptInRange(0f, 1f) ||
            artifactMask.AsSpan().ContainsAnyExceptInRange(0f, 1f))
        {
            throw new ArgumentException(
                "Detection seed masks must be normalized exclusive-ownership planes matching positive dimensions.");
        }

        Width = width;
        Height = height;
        RasterSha256 = rasterSha256;
        RasterVariant = rasterVariant;
        this.ocrMask = ocrMask;
        this.artifactMask = artifactMask;
    }

    public int Width { get; }

    public int Height { get; }

    public string RasterSha256 { get; }

    public WorkflowImageVariant RasterVariant { get; }

    public MarkerMask CopyOcrMask() =>
        new(Width, Height, (float[])ocrMask.Clone());

    public MarkerMask CopyArtifactMask() =>
        new(Width, Height, (float[])artifactMask.Clone());

    internal ReadOnlyMemory<float> OcrMaskValues => ocrMask;

    internal ReadOnlyMemory<float> ArtifactMaskValues => artifactMask;

    public MarkerImageFrame CreateMarkerFrame(ProductionDecodedRaster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        if (raster.Width != Width || raster.Height != Height ||
            raster.Variant != RasterVariant ||
            !string.Equals(raster.InputSha256, RasterSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Failure("Detection seed masks cannot be attached to a different raster identity.");
        }

        return raster.CreateMarkerFrameBorrowingMasks(OcrMaskValues, ArtifactMaskValues);
    }

    private static ProductionWorkflowStageException Failure(string technicalMessage) =>
        new(new ProductionWorkflowFailure(
            ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
            "Errors.DetectionEvidenceRejected",
            technicalMessage,
            Recoverable: true,
            "Rebuild the seed masks from the current OCR and axis evidence."));
}

public interface IProductionArtifactMaskAdapter
{
    string AdapterId { get; }

    bool IsApproved { get; }

    Task<ProductionArtifactMaskEvidence> DetectAsync(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster,
        ProductionDetectionMaskSeed seed,
        CancellationToken cancellationToken);
}

public sealed class ProductionArtifactMaskEvidence
{
    private readonly float[] mask;

    public ProductionArtifactMaskEvidence(
        int width,
        int height,
        string rasterSha256,
        WorkflowImageVariant rasterVariant,
        WorkflowVisionEnvelope envelope,
        float[] mask,
        IEnumerable<string>? warnings = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Positive mask dimensions are required.");
        }

        WorkflowContractGuards.RequireSha256(rasterSha256, nameof(rasterSha256));
        ArgumentNullException.ThrowIfNull(mask);
        if (mask.Length != checked(width * height) ||
            mask.AsSpan().ContainsAnyExceptInRange(0f, 1f))
        {
            throw new ArgumentException(
                "Artifact mask values must match the dimensions and remain within [0,1].",
                nameof(mask));
        }

        Width = width;
        Height = height;
        RasterSha256 = rasterSha256.ToLowerInvariant();
        RasterVariant = rasterVariant;
        Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        this.mask = (float[])mask.Clone();
        Warnings = Array.AsReadOnly((warnings ?? Array.Empty<string>()).ToArray());
    }

    public int Width { get; }

    public int Height { get; }

    public string RasterSha256 { get; }

    public WorkflowImageVariant RasterVariant { get; }

    public WorkflowVisionEnvelope Envelope { get; }

    public IReadOnlyList<string> Warnings { get; }

    public MarkerMask CopyMask() =>
        new(Width, Height, (float[])mask.Clone());

    internal ReadOnlyMemory<float> MaskValues => mask;
}

public interface IProductionDetectionMaskComposer
{
    string AdapterId { get; }

    bool IsApproved { get; }

    Task<ProductionDetectionMaskEvidence> ComposeAsync(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster,
        ProductionAxisGeometryEvidence axisEvidence,
        IReadOnlyList<ProductionOcrModelEvidence> ocrModelEvidence,
        OcrResult ocrResult,
        CancellationToken cancellationToken);
}

public sealed class ProductionDetectionMaskEvidence
{
    private readonly float[] ocrMask;
    private readonly float[] artifactMask;

    internal ProductionDetectionMaskEvidence(
        int width,
        int height,
        string rasterSha256,
        WorkflowImageVariant rasterVariant,
        IEnumerable<WorkflowVisionEnvelope> sourceEnvelopes,
        WorkflowVisionEnvelope artifactEnvelope,
        float[] ocrMask,
        float[] artifactMask,
        IEnumerable<string> warnings)
    {
        Width = width;
        Height = height;
        RasterSha256 = rasterSha256;
        RasterVariant = rasterVariant;
        SourceEnvelopes = Array.AsReadOnly(sourceEnvelopes.ToArray());
        ArtifactEnvelope = artifactEnvelope ?? throw new ArgumentNullException(nameof(artifactEnvelope));
        this.ocrMask = ocrMask;
        this.artifactMask = artifactMask;
        Warnings = Array.AsReadOnly(warnings.ToArray());
        OcrMaskedPixelCount = this.ocrMask.Count(static value => value >= 1);
        ArtifactMaskedPixelCount = this.artifactMask.Count(static value => value >= 1);
    }

    public int Width { get; }

    public int Height { get; }

    public string RasterSha256 { get; }

    public WorkflowImageVariant RasterVariant { get; }

    public IReadOnlyList<WorkflowVisionEnvelope> SourceEnvelopes { get; }

    public WorkflowVisionEnvelope ArtifactEnvelope { get; }

    public IReadOnlyList<string> Warnings { get; }

    public int OcrMaskedPixelCount { get; }

    public int ArtifactMaskedPixelCount { get; }

    public MarkerMask CopyOcrMask() =>
        new(Width, Height, (float[])ocrMask.Clone());

    public MarkerMask CopyArtifactMask() =>
        new(Width, Height, (float[])artifactMask.Clone());

    public MarkerImageFrame CreateMarkerFrame(ProductionDecodedRaster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        if (raster.Width != Width || raster.Height != Height ||
            raster.Variant != RasterVariant ||
            !string.Equals(raster.InputSha256, RasterSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                "Detection masks cannot be attached to a different raster identity.");
        }

        return raster.CreateMarkerFrameBorrowingMasks(ocrMask, artifactMask);
    }

    private static ProductionWorkflowStageException Failure(string technicalMessage) =>
        new(new ProductionWorkflowFailure(
            ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
            "Errors.DetectionEvidenceRejected",
            technicalMessage,
            Recoverable: true,
            "Recompose masks from the current axis and OCR evidence."));
}

/// <summary>
/// Converts validated original-coordinate OCR, axis, and separately approved
/// artifact evidence into dense marker exclusion masks.
/// </summary>
public sealed class ProductionDetectionMaskComposer : IProductionDetectionMaskComposer
{
    private const double TextPaddingOriginalPixels = 1;
    private const double StructureHalfWidthOriginalPixels = 2;
    private static readonly string[] RequiredOcrTasks =
        ["ocr_detection", "ocr_recognition"];
    private readonly IProductionArtifactMaskAdapter? artifactMaskAdapter;

    public ProductionDetectionMaskComposer(
        IProductionArtifactMaskAdapter? artifactMaskAdapter = null) =>
        this.artifactMaskAdapter = artifactMaskAdapter;

    public string AdapterId => string.Join(
        ':',
        "graphreader-detection-masks-v2",
        artifactMaskAdapter?.AdapterId ?? "artifact-provider-unavailable");

    public bool IsApproved => artifactMaskAdapter?.IsApproved == true;

    /// <summary>
    /// Builds the validated OCR and structural seed masks used by the production
    /// composer for local synthetic candidate evaluation. This does not compose
    /// residual artifact evidence or bypass the production approval gate.
    /// </summary>
    internal static ProductionDetectionMaskSeed BuildSeedForLocalSyntheticCandidateEvaluation(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster,
        ProductionAxisGeometryEvidence axisEvidence,
        IReadOnlyList<ProductionOcrModelEvidence> ocrModelEvidence,
        OcrResult ocrResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(axisEvidence);
        ArgumentNullException.ThrowIfNull(ocrModelEvidence);
        ArgumentNullException.ThrowIfNull(ocrResult);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRaster(request, raster);
        ValidateAxisEvidence(request, axisEvidence);
        _ = ValidateOcrEvidence(request, ocrModelEvidence, ocrResult);
        return BuildSeedMasks(raster, axisEvidence, ocrResult, cancellationToken);
    }

    public async Task<ProductionDetectionMaskEvidence> ComposeAsync(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster,
        ProductionAxisGeometryEvidence axisEvidence,
        IReadOnlyList<ProductionOcrModelEvidence> ocrModelEvidence,
        OcrResult ocrResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(axisEvidence);
        ArgumentNullException.ThrowIfNull(ocrModelEvidence);
        ArgumentNullException.ThrowIfNull(ocrResult);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRaster(request, raster);
        ValidateAxisEvidence(request, axisEvidence);
        WorkflowVisionEnvelope[] ocrEnvelopes = ValidateOcrEvidence(
            request,
            ocrModelEvidence,
            ocrResult);

        if (artifactMaskAdapter?.IsApproved != true)
        {
            throw new ProductionWorkflowStageException(new ProductionWorkflowFailure(
                ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
                "Errors.ModelNotFound",
                "No approved artifact-mask adapter is available for arrows, brackets, legends, and connecting-line intersections.",
                Recoverable: true,
                "Continue in manual mode until checksum-bound artifact-mask evidence passes its fixed public gate."));
        }

        ProductionDetectionMaskSeed seed = await Task.Run(
            () => BuildSeedMasks(raster, axisEvidence, ocrResult, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        ProductionArtifactMaskEvidence artifactEvidence = await artifactMaskAdapter
            .DetectAsync(
                request,
                raster,
                seed,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateArtifactEvidence(request, raster, artifactEvidence);

        return await Task.Run(
            () => ComposeCore(
                raster,
                axisEvidence,
                ocrEnvelopes,
                seed,
                artifactEvidence,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static ProductionDetectionMaskEvidence ComposeCore(
        ProductionDecodedRaster raster,
        ProductionAxisGeometryEvidence axisEvidence,
        WorkflowVisionEnvelope[] ocrEnvelopes,
        ProductionDetectionMaskSeed seed,
        ProductionArtifactMaskEvidence artifactEvidence,
        CancellationToken cancellationToken)
    {
        float[] ocrMask = seed.OcrMaskValues.ToArray();
        ReadOnlySpan<float> seededArtifactMask = seed.ArtifactMaskValues.Span;
        float[] artifactMask = artifactEvidence.MaskValues.ToArray();
        for (int index = 0; index < artifactMask.Length; index++)
        {
            if ((index & 0x3fff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            artifactMask[index] = Math.Max(artifactMask[index], seededArtifactMask[index]);
        }

        var sources = new[] { axisEvidence.Envelope }
            .Concat(ocrEnvelopes)
            .Append(artifactEvidence.Envelope);
        return new ProductionDetectionMaskEvidence(
            raster.Width,
            raster.Height,
            raster.InputSha256,
            raster.Variant,
            sources,
            artifactEvidence.Envelope,
            ocrMask,
            artifactMask,
            artifactEvidence.Warnings.Concat(
            [
                "artifact_mask_scope:approved_provider_plus_axis_ticks_dividers_ambiguous",
            ]));
    }

    private static ProductionDetectionMaskSeed BuildSeedMasks(
        ProductionDecodedRaster raster,
        ProductionAxisGeometryEvidence axisEvidence,
        OcrResult ocrResult,
        CancellationToken cancellationToken)
    {
        int pixelCount = checked(raster.Width * raster.Height);
        var ocrMask = new float[pixelCount];
        var artifactMask = new float[pixelCount];

        IEnumerable<OcrPolygon> textPolygons = ocrResult.Regions
            .Select(static region => region.Polygon)
            .Concat(ocrResult.Masks.Select(static mask => mask.Polygon));
        foreach (OcrPolygon polygon in textPolygons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOriginalPolygon(polygon, raster);
            RasterizeTextBounds(ocrMask, raster, polygon.Bounds);
        }

        IEnumerable<GeometryLineSegment> structureLines =
            new[] { axisEvidence.Geometry.XAxis.Line, axisEvidence.Geometry.YAxis.Line }
                .Concat(axisEvidence.Geometry.Ticks.Select(static tick => tick.Line))
                .Concat(axisEvidence.Geometry.PhaseDividers.Select(static divider => divider.Line))
                .Concat(axisEvidence.Geometry.AmbiguousGridOrDividers.Select(static item => item.Line));
        foreach (GeometryLineSegment line in structureLines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOriginalLine(line, raster);
            RasterizeStructureLine(artifactMask, raster, line, cancellationToken);
        }

        return new ProductionDetectionMaskSeed(
            raster.Width,
            raster.Height,
            raster.InputSha256,
            raster.Variant,
            ocrMask,
            artifactMask);
    }

    private static void ValidateArtifactEvidence(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster,
        ProductionArtifactMaskEvidence evidence)
    {
        WorkflowVisionEnvelope envelope = evidence.Envelope;
        ReadOnlySpan<float> mask = evidence.MaskValues.Span;
        WorkflowVisionModel? model = envelope.Model;
        int expectedPixelCount = checked(raster.Width * raster.Height);
        if (evidence.Width != raster.Width || evidence.Height != raster.Height ||
            evidence.RasterVariant != raster.Variant ||
            !string.Equals(evidence.RasterSha256, raster.InputSha256, StringComparison.OrdinalIgnoreCase) ||
            mask.Length != expectedPixelCount ||
            mask.ContainsAnyExceptInRange(0f, 1f) ||
            !envelope.Stage.Equals("markers", StringComparison.Ordinal) ||
            envelope.RunId != request.RunId || envelope.ProjectId != request.ProjectId ||
            envelope.PanelId != request.Panel.ImportedPanel.PanelId ||
            !string.Equals(envelope.InputSha256, request.Image.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(envelope.CoordinateSpace, "original_pixels", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(model?.ModelId) ||
            string.IsNullOrWhiteSpace(model.Version) ||
            string.IsNullOrWhiteSpace(model.Sha256) ||
            model.Provider is not ("cpu" or "directml"))
        {
            throw Failure(
                "Artifact-mask evidence must be normalized, checksum-bound, CPU-compatible original-pixel evidence for the current raster.");
        }
    }

    private static void ValidateRaster(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster)
    {
        if (raster.Width != request.Image.Width || raster.Height != request.Image.Height ||
            raster.Variant != request.ImageVariant ||
            !string.Equals(raster.InputSha256, request.Image.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Failure("The mask raster does not match the current detection request.");
        }
    }

    private static void ValidateAxisEvidence(
        ProductionWorkflowDetectionRequest request,
        ProductionAxisGeometryEvidence evidence)
    {
        WorkflowVisionEnvelope envelope = evidence.Envelope;
        if (!envelope.Stage.Equals("axis", StringComparison.Ordinal) ||
            envelope.RunId != request.RunId || envelope.ProjectId != request.ProjectId ||
            envelope.PanelId != request.Panel.ImportedPanel.PanelId ||
            !string.Equals(
                envelope.InputSha256,
                request.Panel.Original.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(envelope.CoordinateSpace, "original_pixels", StringComparison.Ordinal) ||
            !string.Equals(evidence.Geometry.CoordinateSpace, "original_pixels", StringComparison.Ordinal))
        {
            throw Failure("Axis mask evidence does not match the current original panel identity.");
        }
    }

    private static WorkflowVisionEnvelope[] ValidateOcrEvidence(
        ProductionWorkflowDetectionRequest request,
        IReadOnlyList<ProductionOcrModelEvidence> modelEvidence,
        OcrResult result)
    {
        if (!result.Succeeded || result.ContractVersion != OcrContract.Version ||
            !string.Equals(result.ProjectId, request.ProjectId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(
                result.PanelId,
                request.Panel.ImportedPanel.PanelId.ToString("D"),
                StringComparison.Ordinal) ||
            !string.Equals(result.Stage, OcrContract.Stage, StringComparison.Ordinal) ||
            !string.Equals(result.InputSha256, request.Panel.Original.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.CoordinateSpace, OcrContract.CoordinateSpace, StringComparison.Ordinal))
        {
            throw Failure("OCR mask result does not match the current original panel identity or is unsuccessful.");
        }

        ProductionOcrModelEvidence[] ordered = modelEvidence
            .OrderBy(static evidence => evidence.Task, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length != RequiredOcrTasks.Length ||
            !ordered.Select(static evidence => evidence.Task)
                .SequenceEqual(RequiredOcrTasks, StringComparer.Ordinal))
        {
            throw Failure("Both approved OCR detection and recognition model envelopes are required.");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (ProductionOcrModelEvidence modelEvidenceItem in ordered)
        {
            WorkflowVisionEnvelope envelope = modelEvidenceItem.Envelope;
            WorkflowVisionModel? model = envelope.Model;
            string identity = $"{model?.ModelId}|{model?.Version}|{model?.Sha256}";
            if (!envelope.Stage.Equals(OcrContract.Stage, StringComparison.Ordinal) ||
                !envelope.StageVersion.Equals(result.StageVersion, StringComparison.Ordinal) ||
                envelope.RunId != request.RunId || envelope.ProjectId != request.ProjectId ||
                envelope.PanelId != request.Panel.ImportedPanel.PanelId ||
                !string.Equals(
                    envelope.InputSha256,
                    request.Panel.Original.Sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(envelope.CoordinateSpace, OcrContract.CoordinateSpace, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(model?.ModelId) ||
                string.IsNullOrWhiteSpace(model.Version) ||
                string.IsNullOrWhiteSpace(model.Sha256) ||
                model.Provider is not ("cpu" or "directml") ||
                !identities.Add(identity))
            {
                throw Failure(
                    "OCR model evidence must contain two distinct checksum-bound CPU-compatible envelopes for the current run.");
            }
        }

        return ordered.Select(static evidence => evidence.Envelope).ToArray();
    }

    private static void ValidateOriginalPolygon(
        OcrPolygon polygon,
        ProductionDecodedRaster raster)
    {
        OcrRectangle bounds = polygon.Bounds;
        if (!bounds.IsValid || bounds.Left < 0 || bounds.Top < 0 ||
            bounds.Right > raster.CanonicalOriginalWidth ||
            bounds.Bottom > raster.CanonicalOriginalHeight)
        {
            throw Failure("OCR mask polygon leaves immutable original-image bounds.");
        }
    }

    private static void ValidateOriginalLine(
        GeometryLineSegment line,
        ProductionDecodedRaster raster)
    {
        if (!line.Start.IsFinite || !line.End.IsFinite ||
            !IsInOriginalBounds(line.Start, raster) ||
            !IsInOriginalBounds(line.End, raster))
        {
            throw Failure("Axis artifact line leaves immutable original-image bounds.");
        }
    }

    private static bool IsInOriginalBounds(
        PixelPoint point,
        ProductionDecodedRaster raster) =>
        point.X >= 0 && point.X <= raster.CanonicalOriginalWidth &&
        point.Y >= 0 && point.Y <= raster.CanonicalOriginalHeight;

    private static void RasterizeTextBounds(
        float[] mask,
        ProductionDecodedRaster raster,
        OcrRectangle originalBounds)
    {
        MarkerPoint topLeft = raster.OriginalToFrame.MapFromOriginal(
            new MarkerPoint(
                originalBounds.Left - TextPaddingOriginalPixels,
                originalBounds.Top - TextPaddingOriginalPixels));
        MarkerPoint bottomRight = raster.OriginalToFrame.MapFromOriginal(
            new MarkerPoint(
                originalBounds.Right + TextPaddingOriginalPixels,
                originalBounds.Bottom + TextPaddingOriginalPixels));
        int left = Math.Clamp((int)Math.Floor(Math.Min(topLeft.X, bottomRight.X)), 0, raster.Width - 1);
        int top = Math.Clamp((int)Math.Floor(Math.Min(topLeft.Y, bottomRight.Y)), 0, raster.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(Math.Max(topLeft.X, bottomRight.X)), 0, raster.Width - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(Math.Max(topLeft.Y, bottomRight.Y)), 0, raster.Height - 1);
        for (int y = top; y <= bottom; y++)
        {
            int row = y * raster.Width;
            for (int x = left; x <= right; x++)
            {
                mask[row + x] = 1;
            }
        }
    }

    private static void RasterizeStructureLine(
        float[] mask,
        ProductionDecodedRaster raster,
        GeometryLineSegment originalLine,
        CancellationToken cancellationToken)
    {
        MarkerPoint start = raster.OriginalToFrame.MapFromOriginal(
            new MarkerPoint(originalLine.Start.X, originalLine.Start.Y));
        MarkerPoint end = raster.OriginalToFrame.MapFromOriginal(
            new MarkerPoint(originalLine.End.X, originalLine.End.Y));
        double scale = Math.Sqrt(Math.Abs(raster.OriginalToFrame.Determinant));
        double radius = StructureHalfWidthOriginalPixels * scale;
        int left = Math.Clamp((int)Math.Floor(Math.Min(start.X, end.X) - radius), 0, raster.Width - 1);
        int top = Math.Clamp((int)Math.Floor(Math.Min(start.Y, end.Y) - radius), 0, raster.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(Math.Max(start.X, end.X) + radius), 0, raster.Width - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(Math.Max(start.Y, end.Y) + radius), 0, raster.Height - 1);
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        double lengthSquared = (deltaX * deltaX) + (deltaY * deltaY);
        for (int y = top; y <= bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = y * raster.Width;
            for (int x = left; x <= right; x++)
            {
                double sampleX = x + 0.5;
                double sampleY = y + 0.5;
                double position = lengthSquared <= double.Epsilon
                    ? 0
                    : Math.Clamp(
                        (((sampleX - start.X) * deltaX) + ((sampleY - start.Y) * deltaY)) /
                        lengthSquared,
                        0,
                        1);
                double closestX = start.X + (position * deltaX);
                double closestY = start.Y + (position * deltaY);
                double distanceX = sampleX - closestX;
                double distanceY = sampleY - closestY;
                if ((distanceX * distanceX) + (distanceY * distanceY) <= radius * radius)
                {
                    mask[row + x] = 1;
                }
            }
        }
    }

    private static ProductionWorkflowStageException Failure(string technicalMessage) =>
        new(new ProductionWorkflowFailure(
            ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
            "Errors.DetectionEvidenceRejected",
            technicalMessage,
            Recoverable: true,
            "Retain earlier evidence and continue manual review until exact production masks can be recomposed."));
}
