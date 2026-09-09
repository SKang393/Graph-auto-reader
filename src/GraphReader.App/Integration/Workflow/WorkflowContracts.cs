// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;

namespace GraphReader.App.Integration.Workflow;

public enum WorkflowRuntimeEnvironment
{
    Production,
    ManualPreview,
    RecordedFake,
}

public enum WorkflowSourceKind
{
    Image,
    Pdf,
}

public enum WorkflowImageVariant
{
    Original,
    Enhanced,
    Consensus,
}

public enum WorkflowReviewStatus
{
    Unreviewed,
    Accepted,
    Corrected,
    Rejected,
}

public enum WorkflowStep
{
    Import,
    Prepare,
    Detect,
    Review,
    Export,
}

public sealed record WorkflowSourceRequest(Guid SourceId, WorkflowSourceKind Kind, string Path)
{
    // An open workspace keeps its reviewed source crops when Auto Detect reruns.
    // This is an in-process request detail, not part of a frozen file contract.
    internal RetainedRasterSourceRequest? RetainedRaster { get; init; }
}

public sealed class WorkflowImportRequest
{
    public WorkflowImportRequest(
        Guid projectId,
        IEnumerable<WorkflowSourceRequest> sources,
        bool enhancementEnabled = true)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A project ID is required.", nameof(projectId));
        }

        ProjectId = projectId;
        Sources = WorkflowCollections.Freeze(sources);
        if (Sources.Count == 0)
        {
            throw new ArgumentException("At least one source is required.", nameof(sources));
        }

        if (Sources.Any(static source => source.SourceId == Guid.Empty || string.IsNullOrWhiteSpace(source.Path)))
        {
            throw new ArgumentException("Each source requires an ID and path.", nameof(sources));
        }

        if (Sources.Select(static source => source.SourceId).Distinct().Count() != Sources.Count)
        {
            throw new ArgumentException("Source IDs must be unique.", nameof(sources));
        }

        EnhancementEnabled = enhancementEnabled;
    }

    public Guid ProjectId { get; }

    public IReadOnlyList<WorkflowSourceRequest> Sources { get; }

    public bool EnhancementEnabled { get; }
}

public sealed record WorkflowRunRequest(
    Guid RunId,
    WorkflowImportRequest Import,
    WorkflowConsensusOptions? ConsensusOptions = null);

public sealed class WorkflowImageEvidence
{
    public WorkflowImageEvidence(
        string reference,
        string sha256,
        int width,
        int height,
        WorkflowImageVariant variant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        WorkflowContractGuards.RequireSha256(sha256, nameof(sha256));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (variant == WorkflowImageVariant.Consensus)
        {
            throw new ArgumentException("Consensus is detection evidence, not an image input.", nameof(variant));
        }

        Reference = reference;
        Sha256 = sha256.ToLowerInvariant();
        Width = width;
        Height = height;
        Variant = variant;
    }

    public string Reference { get; }

    public string Sha256 { get; }

    public int Width { get; }

    public int Height { get; }

    public WorkflowImageVariant Variant { get; }
}

public sealed class WorkflowImportedPanel
{
    public WorkflowImportedPanel(
        Guid panelId,
        Guid sourceId,
        string displayName,
        WorkflowImageEvidence original,
        int? pageNumber = null)
    {
        if (panelId == Guid.Empty)
        {
            throw new ArgumentException("A panel ID is required.", nameof(panelId));
        }

        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A source ID is required.", nameof(sourceId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(original);
        if (original.Variant != WorkflowImageVariant.Original)
        {
            throw new ArgumentException("Imported panel evidence must be the original image.", nameof(original));
        }

        if (pageNumber is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        PanelId = panelId;
        SourceId = sourceId;
        DisplayName = displayName;
        Original = original;
        PageNumber = pageNumber;
    }

    public Guid PanelId { get; }

    public Guid SourceId { get; }

    public string DisplayName { get; }

    public WorkflowImageEvidence Original { get; }

    public int? PageNumber { get; }
}

public sealed class WorkflowImportSnapshot
{
    public WorkflowImportSnapshot(
        Guid projectId,
        IEnumerable<WorkflowImportedPanel> panels,
        IEnumerable<string>? warnings = null)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A project ID is required.", nameof(projectId));
        }

        ProjectId = projectId;
        Panels = WorkflowCollections.Freeze(panels);
        Warnings = WorkflowCollections.Freeze(warnings ?? Array.Empty<string>());
        if (Panels.Select(static panel => panel.PanelId).Distinct().Count() != Panels.Count)
        {
            throw new ArgumentException("Panel IDs must be unique.", nameof(panels));
        }
    }

    public Guid ProjectId { get; }

    public IReadOnlyList<WorkflowImportedPanel> Panels { get; }

    public IReadOnlyList<string> Warnings { get; }
}

public sealed class WorkflowPreparedPanel
{
    public WorkflowPreparedPanel(
        WorkflowImportedPanel importedPanel,
        WorkflowImageEvidence original,
        WorkflowImageEvidence? enhanced,
        IEnumerable<string>? warnings = null)
    {
        ImportedPanel = importedPanel ?? throw new ArgumentNullException(nameof(importedPanel));
        Original = original ?? throw new ArgumentNullException(nameof(original));
        if (original.Variant != WorkflowImageVariant.Original ||
            !string.Equals(original.Sha256, importedPanel.Original.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Prepare must preserve the imported original image and checksum.", nameof(original));
        }

        if (enhanced?.Variant is not null and not WorkflowImageVariant.Enhanced)
        {
            throw new ArgumentException("The derived image must be marked enhanced.", nameof(enhanced));
        }

        Enhanced = enhanced;
        Warnings = WorkflowCollections.Freeze(warnings ?? Array.Empty<string>());
    }

    public WorkflowImportedPanel ImportedPanel { get; }

    public WorkflowImageEvidence Original { get; }

    public WorkflowImageEvidence? Enhanced { get; }

    public IReadOnlyList<string> Warnings { get; }
}

public sealed class WorkflowDetectionCandidate
{
    public WorkflowDetectionCandidate(
        string pointId,
        string detectionKey,
        double originalPixelX,
        double originalPixelY,
        double confidence,
        WorkflowImageVariant sourceImage,
        string symbol = "?",
        string shape = "other",
        string fill = "unknown",
        string? seriesId = null,
        string? phaseId = null,
        double? graphX = null,
        double? graphY = null,
        string sourceStage = "markers",
        string? modelVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(detectionKey);
        WorkflowContractGuards.RequireFinite(originalPixelX, nameof(originalPixelX));
        WorkflowContractGuards.RequireFinite(originalPixelY, nameof(originalPixelY));
        WorkflowContractGuards.RequireConfidence(confidence, nameof(confidence));
        WorkflowContractGuards.RequireOptionalFinite(graphX, nameof(graphX));
        WorkflowContractGuards.RequireOptionalFinite(graphY, nameof(graphY));
        if (sourceImage is not (WorkflowImageVariant.Original or WorkflowImageVariant.Enhanced))
        {
            throw new ArgumentException("Detection candidates must identify original or enhanced evidence.", nameof(sourceImage));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(shape);
        ArgumentException.ThrowIfNullOrWhiteSpace(fill);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStage);

        PointId = pointId;
        DetectionKey = detectionKey;
        OriginalPixelX = originalPixelX;
        OriginalPixelY = originalPixelY;
        Confidence = confidence;
        SourceImage = sourceImage;
        Symbol = symbol;
        Shape = shape;
        Fill = fill;
        SeriesId = seriesId;
        PhaseId = phaseId;
        GraphX = graphX;
        GraphY = graphY;
        SourceStage = sourceStage;
        ModelVersion = modelVersion;
    }

    public string PointId { get; }

    public string DetectionKey { get; }

    public double OriginalPixelX { get; }

    public double OriginalPixelY { get; }

    public double Confidence { get; }

    public WorkflowImageVariant SourceImage { get; }

    public string Symbol { get; }

    public string Shape { get; }

    public string Fill { get; }

    public string? SeriesId { get; }

    public string? PhaseId { get; }

    public double? GraphX { get; }

    public double? GraphY { get; }

    public string SourceStage { get; }

    public string? ModelVersion { get; }
}

public sealed record WorkflowVisionModel(
    string? ModelId,
    string? Version,
    string? Sha256,
    string? Provider);

public sealed record WorkflowVisionTiming(
    double? PreprocessMilliseconds,
    double? InferenceMilliseconds,
    double? PostprocessMilliseconds,
    double TotalMilliseconds);

public sealed class WorkflowTransformProvenance
{
    public WorkflowTransformProvenance(
        string transformId,
        string inputCoordinateSpace,
        string outputCoordinateSpace,
        IEnumerable<double> inputToOutputMatrix,
        IEnumerable<double>? outputToInputMatrix,
        bool lossy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transformId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputCoordinateSpace);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputCoordinateSpace);
        TransformId = transformId;
        InputCoordinateSpace = inputCoordinateSpace;
        OutputCoordinateSpace = outputCoordinateSpace;
        InputToOutputMatrix = FreezeMatrix(inputToOutputMatrix, nameof(inputToOutputMatrix));
        OutputToInputMatrix = outputToInputMatrix is null
            ? null
            : FreezeMatrix(outputToInputMatrix, nameof(outputToInputMatrix));
        Lossy = lossy;
        if (!lossy && OutputToInputMatrix is null)
        {
            throw new ArgumentException("A reversible transform requires an inverse matrix.", nameof(outputToInputMatrix));
        }
    }

    public string TransformId { get; }

    public string InputCoordinateSpace { get; }

    public string OutputCoordinateSpace { get; }

    public IReadOnlyList<double> InputToOutputMatrix { get; }

    public IReadOnlyList<double>? OutputToInputMatrix { get; }

    public bool Lossy { get; }

    private static IReadOnlyList<double> FreezeMatrix(IEnumerable<double> values, string parameterName)
    {
        IReadOnlyList<double> matrix = WorkflowCollections.Freeze(values);
        if (matrix.Count != 9 || matrix.Any(static value => !double.IsFinite(value)))
        {
            throw new ArgumentException("A finite 3x3 transform matrix is required.", parameterName);
        }

        return matrix;
    }
}

public sealed class WorkflowVisionEnvelope
{
    private static readonly HashSet<string> AllowedStages = new(
        ["import", "panelization", "enhancement", "axis", "ocr", "markers", "legends", "phases"],
        StringComparer.Ordinal);
    private static readonly HashSet<string?> AllowedProviders = new(
        ["cpu", "directml", "winml", "cuda", "openvino", "vulkan", null],
        StringComparer.Ordinal);

    public WorkflowVisionEnvelope(
        int contractVersion,
        Guid runId,
        Guid projectId,
        Guid panelId,
        string stage,
        string stageVersion,
        string inputSha256,
        WorkflowVisionModel? model,
        WorkflowVisionTiming timing,
        double confidence,
        IEnumerable<string>? warnings = null,
        IEnumerable<WorkflowTransformProvenance>? transforms = null,
        string coordinateSpace = "original_pixels")
    {
        if (contractVersion != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(contractVersion), "Vision contract version 1 is required.");
        }

        if (runId == Guid.Empty || projectId == Guid.Empty || panelId == Guid.Empty)
        {
            throw new ArgumentException("Run, project, and panel IDs are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        if (!AllowedStages.Contains(stage))
        {
            throw new ArgumentException("The stage is not defined by vision-result schema v1.", nameof(stage));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stageVersion);
        WorkflowContractGuards.RequireSha256(inputSha256, nameof(inputSha256));
        if (!string.Equals(coordinateSpace, "original_pixels", StringComparison.Ordinal))
        {
            throw new ArgumentException("Vision results must use original pixel coordinates.", nameof(coordinateSpace));
        }

        ValidateTiming(timing);
        WorkflowContractGuards.RequireConfidence(confidence, nameof(confidence));
        ValidateModel(model);

        ContractVersion = contractVersion;
        RunId = runId;
        ProjectId = projectId;
        PanelId = panelId;
        Stage = stage;
        StageVersion = stageVersion;
        InputSha256 = inputSha256.ToLowerInvariant();
        CoordinateSpace = coordinateSpace;
        Model = model;
        Timing = timing;
        Confidence = confidence;
        Warnings = WorkflowCollections.Freeze(warnings ?? Array.Empty<string>());
        Transforms = WorkflowCollections.Freeze(transforms ?? Array.Empty<WorkflowTransformProvenance>());
    }

    public int ContractVersion { get; }

    public Guid RunId { get; }

    public Guid ProjectId { get; }

    public Guid PanelId { get; }

    public string Stage { get; }

    public string StageVersion { get; }

    public string InputSha256 { get; }

    public string CoordinateSpace { get; }

    public WorkflowVisionModel? Model { get; }

    public WorkflowVisionTiming Timing { get; }

    public double Confidence { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<WorkflowTransformProvenance> Transforms { get; }

    private static void ValidateModel(WorkflowVisionModel? model)
    {
        if (model?.Sha256 is { } checksum)
        {
            WorkflowContractGuards.RequireSha256(checksum, nameof(model));
        }

        if (model is not null && !AllowedProviders.Contains(model.Provider))
        {
            throw new ArgumentException("The execution provider is not defined by vision-result schema v1.", nameof(model));
        }
    }

    private static void ValidateTiming(WorkflowVisionTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        double?[] values =
        [
            timing.PreprocessMilliseconds,
            timing.InferenceMilliseconds,
            timing.PostprocessMilliseconds,
            timing.TotalMilliseconds,
        ];
        if (values.Any(static value => value is < 0 || (value.HasValue && !double.IsFinite(value.Value))))
        {
            throw new ArgumentOutOfRangeException(nameof(timing), "Vision timing values must be finite and non-negative.");
        }
    }
}

public sealed class WorkflowDetectionBatch
{
    public WorkflowDetectionBatch(
        WorkflowVisionEnvelope envelope,
        WorkflowImageVariant sourceImage,
        IEnumerable<WorkflowDetectionCandidate> candidates)
    {
        Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        if (sourceImage is not (WorkflowImageVariant.Original or WorkflowImageVariant.Enhanced))
        {
            throw new ArgumentException("Detection batches must identify original or enhanced evidence.", nameof(sourceImage));
        }

        if (!string.Equals(envelope.Stage, "markers", StringComparison.Ordinal))
        {
            throw new ArgumentException("Detection batches require a markers vision envelope.", nameof(envelope));
        }

        SourceImage = sourceImage;
        Candidates = WorkflowCollections.Freeze(candidates);
        if (Candidates.Any(candidate => candidate.SourceImage != sourceImage))
        {
            throw new ArgumentException("Every candidate must match the batch image variant.", nameof(candidates));
        }

        if (Candidates.Select(static candidate => candidate.DetectionKey).Distinct(StringComparer.Ordinal).Count() != Candidates.Count)
        {
            throw new ArgumentException("Detection keys must be unique within a batch.", nameof(candidates));
        }

        if (Candidates.Select(static candidate => candidate.PointId).Distinct(StringComparer.Ordinal).Count() != Candidates.Count)
        {
            throw new ArgumentException("Point IDs must be unique within a detection batch.", nameof(candidates));
        }

        if (Candidates.Any(candidate =>
            !string.Equals(candidate.SourceStage, envelope.Stage, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Candidate source stages must match the validated vision envelope.", nameof(candidates));
        }

        if (Candidates.Any(candidate =>
            !string.Equals(candidate.ModelVersion, envelope.Model?.Version, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Candidate model versions must match the validated vision envelope.", nameof(candidates));
        }
    }

    public WorkflowVisionEnvelope Envelope { get; }

    public Guid PanelId => Envelope.PanelId;

    public WorkflowImageVariant SourceImage { get; }

    public string CoordinateSpace => Envelope.CoordinateSpace;

    public IReadOnlyList<WorkflowDetectionCandidate> Candidates { get; }

    public IReadOnlyList<string> Warnings => Envelope.Warnings;

    internal ProductionPanelExportEvidence? PendingExportEvidence { get; init; }
}

public sealed record WorkflowConsensusOptions(
    double AgreementDistancePixels = 3d,
    double MaximumMatchDistancePixels = 5d,
    double OriginalOnlyConfidenceFactor = 0.75d,
    double EnhancedOnlyConfidenceFactor = 0.5d);

public sealed record WorkflowPoint
{
    public WorkflowPoint(
        string pointId,
        string? detectionKey,
        double originalPixelX,
        double originalPixelY,
        double confidence,
        WorkflowImageVariant sourceImage,
        WorkflowReviewStatus reviewStatus,
        string symbol,
        string shape,
        string fill,
        string? seriesId,
        string? phaseId,
        double? graphX,
        double? graphY,
        string sourceStage,
        string? modelVersion,
        bool isManual,
        IEnumerable<string>? correctionIds = null,
        IEnumerable<string>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pointId);
        if (!isManual)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(detectionKey);
        }

        WorkflowContractGuards.RequireFinite(originalPixelX, nameof(originalPixelX));
        WorkflowContractGuards.RequireFinite(originalPixelY, nameof(originalPixelY));
        WorkflowContractGuards.RequireConfidence(confidence, nameof(confidence));
        WorkflowContractGuards.RequireOptionalFinite(graphX, nameof(graphX));
        WorkflowContractGuards.RequireOptionalFinite(graphY, nameof(graphY));
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(shape);
        ArgumentException.ThrowIfNullOrWhiteSpace(fill);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStage);

        PointId = pointId;
        DetectionKey = detectionKey;
        OriginalPixelX = originalPixelX;
        OriginalPixelY = originalPixelY;
        Confidence = confidence;
        SourceImage = sourceImage;
        ReviewStatus = reviewStatus;
        Symbol = symbol;
        Shape = shape;
        Fill = fill;
        SeriesId = seriesId;
        PhaseId = phaseId;
        GraphX = graphX;
        GraphY = graphY;
        SourceStage = sourceStage;
        ModelVersion = modelVersion;
        IsManual = isManual;
        CorrectionIds = WorkflowCollections.Freeze(correctionIds ?? Array.Empty<string>());
        Warnings = WorkflowCollections.Freeze(warnings ?? Array.Empty<string>());
    }

    public string PointId { get; init; }

    public string? DetectionKey { get; init; }

    public double OriginalPixelX { get; init; }

    public double OriginalPixelY { get; init; }

    public double Confidence { get; init; }

    public WorkflowImageVariant SourceImage { get; init; }

    public WorkflowReviewStatus ReviewStatus { get; init; }

    public string Symbol { get; init; }

    public string Shape { get; init; }

    public string Fill { get; init; }

    public string? SeriesId { get; init; }

    public string? PhaseId { get; init; }

    public double? GraphX { get; init; }

    public double? GraphY { get; init; }

    public string SourceStage { get; init; }

    public string? ModelVersion { get; init; }

    public bool IsManual { get; init; }

    public IReadOnlyList<string> CorrectionIds { get; init; }

    public IReadOnlyList<string> Warnings { get; init; }
}

public abstract record WorkflowCorrection(string CorrectionId, Guid PanelId);

public sealed record MoveWorkflowPointCorrection(
    string CorrectionId,
    Guid PanelId,
    string TargetPointId,
    string? TargetDetectionKey,
    double OriginalPixelX,
    double OriginalPixelY) : WorkflowCorrection(CorrectionId, PanelId);

public sealed record DeleteWorkflowPointCorrection(
    string CorrectionId,
    Guid PanelId,
    string TargetPointId,
    string? TargetDetectionKey) : WorkflowCorrection(CorrectionId, PanelId);

public sealed record AddWorkflowPointCorrection(
    string CorrectionId,
    Guid PanelId,
    WorkflowPoint Point) : WorkflowCorrection(CorrectionId, PanelId);

public sealed record ReassignWorkflowPointCorrection(
    string CorrectionId,
    Guid PanelId,
    string TargetPointId,
    string? TargetDetectionKey,
    string SeriesId) : WorkflowCorrection(CorrectionId, PanelId);

public sealed record AssignWorkflowPointPhaseCorrection(
    string CorrectionId,
    Guid PanelId,
    string TargetPointId,
    string? TargetDetectionKey,
    string PhaseId) : WorkflowCorrection(CorrectionId, PanelId);

public sealed class WorkflowReviewPanel
{
    public WorkflowReviewPanel(
        WorkflowPreparedPanel preparedPanel,
        IEnumerable<WorkflowPoint> points,
        IEnumerable<WorkflowVisionEnvelope>? detectionProvenance = null)
    {
        PreparedPanel = preparedPanel ?? throw new ArgumentNullException(nameof(preparedPanel));
        Points = WorkflowCollections.Freeze(points);
        DetectionProvenance = WorkflowCollections.Freeze(
            detectionProvenance ?? Array.Empty<WorkflowVisionEnvelope>());
        if (Points.Select(static point => point.PointId).Distinct(StringComparer.Ordinal).Count() != Points.Count)
        {
            throw new ArgumentException("Point IDs must be unique within a panel.", nameof(points));
        }

        if (DetectionProvenance.Any(envelope => envelope.PanelId != PanelId))
        {
            throw new ArgumentException("Detection provenance must describe the review panel.", nameof(detectionProvenance));
        }

        var allowedInputHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PreparedPanel.Original.Sha256,
        };
        if (PreparedPanel.Enhanced is not null)
        {
            allowedInputHashes.Add(PreparedPanel.Enhanced.Sha256);
        }

        if (DetectionProvenance.Any(envelope => !allowedInputHashes.Contains(envelope.InputSha256)))
        {
            throw new ArgumentException("Detection provenance must identify prepared panel evidence.", nameof(detectionProvenance));
        }
    }

    public Guid PanelId => PreparedPanel.ImportedPanel.PanelId;

    public WorkflowPreparedPanel PreparedPanel { get; }

    public IReadOnlyList<WorkflowPoint> Points { get; }

    public IReadOnlyList<WorkflowVisionEnvelope> DetectionProvenance { get; }
}

public sealed class WorkflowReviewState
{
    public WorkflowReviewState(
        Guid projectId,
        IEnumerable<WorkflowReviewPanel> panels,
        IEnumerable<WorkflowCorrection>? correctionJournal = null,
        IEnumerable<string>? warnings = null)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A project ID is required.", nameof(projectId));
        }

        ProjectId = projectId;
        Panels = WorkflowCollections.Freeze(panels);
        CorrectionJournal = WorkflowCollections.Freeze(correctionJournal ?? Array.Empty<WorkflowCorrection>());
        Warnings = WorkflowCollections.Freeze(warnings ?? Array.Empty<string>());
        if (Panels.Select(static panel => panel.PanelId).Distinct().Count() != Panels.Count)
        {
            throw new ArgumentException("Panel IDs must be unique.", nameof(panels));
        }

        if (Panels.SelectMany(static panel => panel.DetectionProvenance)
            .Any(envelope => envelope.ProjectId != projectId))
        {
            throw new ArgumentException("Detection provenance must describe the review project.", nameof(panels));
        }

        if (CorrectionJournal.Select(static correction => correction.CorrectionId)
            .Distinct(StringComparer.Ordinal).Count() != CorrectionJournal.Count)
        {
            throw new ArgumentException("Correction IDs must be unique.", nameof(correctionJournal));
        }
    }

    public Guid ProjectId { get; }

    public IReadOnlyList<WorkflowReviewPanel> Panels { get; }

    public IReadOnlyList<WorkflowCorrection> CorrectionJournal { get; }

    public IReadOnlyList<string> Warnings { get; }
}

public sealed record WorkflowExportRequest(Guid RunId, string OutputDirectory);

public sealed record WorkflowExportArtifact(string FileName, string Sha256, int RowCount, string? WrittenPath);

public sealed class WorkflowExportResult
{
    public WorkflowExportResult(
        bool succeeded,
        IEnumerable<WorkflowExportArtifact>? artifacts = null,
        IEnumerable<string>? warnings = null,
        string? failureCode = null)
    {
        if (!succeeded && string.IsNullOrWhiteSpace(failureCode))
        {
            throw new ArgumentException("A failed export requires a structured failure code.", nameof(failureCode));
        }

        Succeeded = succeeded;
        Artifacts = WorkflowCollections.Freeze(artifacts ?? Array.Empty<WorkflowExportArtifact>());
        Warnings = WorkflowCollections.Freeze(warnings ?? Array.Empty<string>());
        FailureCode = failureCode;
    }

    public bool Succeeded { get; }

    public IReadOnlyList<WorkflowExportArtifact> Artifacts { get; }

    public IReadOnlyList<string> Warnings { get; }

    public string? FailureCode { get; }
}

public sealed record WorkflowStepRecord(WorkflowStep Step, TimeSpan Elapsed, int ItemCount);

public sealed class WorkflowRunResult
{
    public WorkflowRunResult(
        Guid runId,
        WorkflowReviewState review,
        IEnumerable<WorkflowStepRecord> steps)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("A run ID is required.", nameof(runId));
        }

        RunId = runId;
        Review = review ?? throw new ArgumentNullException(nameof(review));
        Steps = WorkflowCollections.Freeze(steps);
    }

    public Guid RunId { get; }

    public WorkflowReviewState Review { get; }

    public IReadOnlyList<WorkflowStepRecord> Steps { get; }
}

public interface IWorkflowImportStage
{
    Task<WorkflowImportSnapshot> ImportAsync(
        WorkflowImportRequest request,
        CancellationToken cancellationToken);
}

public interface IWorkflowPrepareStage
{
    Task<WorkflowPreparedPanel> PrepareAsync(
        WorkflowImportedPanel panel,
        bool enhancementEnabled,
        CancellationToken cancellationToken);
}

public interface IWorkflowDetectionStage
{
    Task<WorkflowDetectionBatch> DetectAsync(
        WorkflowPreparedPanel panel,
        WorkflowImageVariant imageVariant,
        Guid runId,
        Guid projectId,
        CancellationToken cancellationToken);
}

public interface IWorkflowExportStage
{
    Task<WorkflowExportResult> ExportAsync(
        WorkflowReviewState review,
        WorkflowExportRequest request,
        CancellationToken cancellationToken);
}

public sealed class WorkflowServiceSet
{
    public WorkflowServiceSet(
        IWorkflowImportStage importer,
        IWorkflowPrepareStage preparer,
        IWorkflowDetectionStage detector,
        IWorkflowExportStage exporter)
    {
        Importer = importer ?? throw new ArgumentNullException(nameof(importer));
        Preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
        Detector = detector ?? throw new ArgumentNullException(nameof(detector));
        Exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
    }

    public IWorkflowImportStage Importer { get; }

    public IWorkflowPrepareStage Preparer { get; }

    public IWorkflowDetectionStage Detector { get; }

    public IWorkflowExportStage Exporter { get; }
}

internal static class WorkflowCollections
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ReadOnlyCollection<T>(values.ToArray());
    }
}

internal static class WorkflowContractGuards
{
    public static void RequireConfidence(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    public static void RequireFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    public static void RequireOptionalFinite(double? value, string parameterName)
    {
        if (value.HasValue)
        {
            RequireFinite(value.Value, parameterName);
        }
    }

    public static void RequireSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A 64-character SHA-256 is required.", parameterName);
        }
    }
}
