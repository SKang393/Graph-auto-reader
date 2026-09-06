// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.Domain;
using GraphReader.Export;
using GraphReader.Imaging;
using GraphReader.Inference;
using GraphReader.Pdf;
using GraphReader.SuperResolution;

namespace GraphReader.App.Integration;

public sealed record ApplicationCompositionResult(
    WorkflowRuntimeEnvironment Environment,
    IWorkspaceService WorkspaceService,
    DomainError? StartupError,
    ProductionInferenceRuntimeHost? InferenceRuntimeHost = null,
    IProductionAxisGeometryAdapter? AxisGeometryAdapter = null,
    IProductionMarkerCenterAdapter? MarkerCenterAdapter = null,
    IProductionMarkerClassificationAdapter? MarkerClassificationAdapter = null,
    IProductionLegendReasoningAdapter? LegendReasoningAdapter = null,
    IProductionPhaseReasoningAdapter? PhaseReasoningAdapter = null,
    IProductionRasterFrameDecoder? RasterFrameDecoder = null,
    IProductionDetectionMaskComposer? DetectionMaskComposer = null,
    IProductionOcrAdapter? OcrAdapter = null,
    IProductionWorkflowDetectionAdapter? AutomaticDetectionAdapter = null,
    IProductionArtifactMaskAdapter? ArtifactMaskAdapter = null);

public static class ApplicationComposition
{
    public const string RealEsrganManifestEnvironmentVariable = "GRAPHREADER_REALESRGAN_MANIFEST_PATH";
    public const string RealEsrganRuntimeRootEnvironmentVariable = "GRAPHREADER_REALESRGAN_RUNTIME_ROOT";
    public const string PdfiumApprovalEnvironmentVariable = "GRAPHREADER_PDFIUM_APPROVAL_PATH";

    public static ApplicationCompositionResult Create(
        WorkflowRuntimeEnvironment environment,
        IApplicationPaths? applicationPaths = null,
        string? applicationRoot = null)
    {
        Func<CancellationToken, Task<RealEsrganBackendResolution>>? enhancementResolver =
            CreateLocalEnhancementResolver(applicationPaths);
        return CreateCore(
            environment,
            applicationPaths,
            applicationRoot ?? AppContext.BaseDirectory,
            environment == WorkflowRuntimeEnvironment.Production
                ? CreateProductionUiThreadGuard()
                : null,
            modelAvailability: null,
            runtimeAvailability: null,
            enhancementResolver,
            enhancementResolution: null,
            precreatedInference: null,
            precreatedOcr: null);
    }

    public static async Task<ApplicationCompositionResult> CreateAsync(
        WorkflowRuntimeEnvironment environment,
        IApplicationPaths? applicationPaths = null,
        string? applicationRoot = null,
        CancellationToken cancellationToken = default)
    {
        IUiThreadGuard? uiThreadGuard = environment == WorkflowRuntimeEnvironment.Production
            ? CreateProductionUiThreadGuard()
            : null;
        Func<CancellationToken, Task<RealEsrganBackendResolution>>? enhancementResolver =
            CreateLocalEnhancementResolver(applicationPaths);
        RealEsrganBackendResolution? enhancementResolution = enhancementResolver is null
            ? null
            : await enhancementResolver(cancellationToken).ConfigureAwait(false);
        ProductionModelAvailabilitySnapshot? modelAvailability = environment == WorkflowRuntimeEnvironment.Production
            ? await ProductionModelAvailabilityProbe.InspectAsync(
                applicationPaths?.ModelRoot,
                cancellationToken)
                .ConfigureAwait(false)
            : null;
        ProductionRuntimeAvailabilitySnapshot? runtimeAvailability = environment == WorkflowRuntimeEnvironment.Production
            ? await ProductionRuntimeAvailabilityProbe.InspectAsync(
                applicationRoot ?? AppContext.BaseDirectory,
                cancellationToken).ConfigureAwait(false)
            : null;
        DomainResult<ProductionInferenceRuntimeHost>? inference =
            environment == WorkflowRuntimeEnvironment.Production && applicationPaths is not null
                ? ProductionInferenceRuntimeFactory.Create(
                    applicationPaths,
                    uiThreadGuard ?? NoUiThreadGuard.Instance)
                : null;
        return await CompleteWithOwnedInferenceAsync(
                inference?.Value,
                async () =>
                {
                    (IProductionOcrAdapter? Adapter, DomainError? Error) ocr =
                        await CreateApprovedOcrAdapterAsync(
                                modelAvailability,
                                inference?.Value,
                                runtimeAvailability,
                                cancellationToken)
                            .ConfigureAwait(false);
                    return CreateCore(
                        environment,
                        applicationPaths,
                        applicationRoot ?? AppContext.BaseDirectory,
                        uiThreadGuard,
                        modelAvailability,
                        runtimeAvailability,
                        enhancementResolver,
                        enhancementResolution,
                        inference,
                        ocr);
                })
            .ConfigureAwait(false);
    }

    internal static async Task<ApplicationCompositionResult> CompleteWithOwnedInferenceAsync(
        ProductionInferenceRuntimeHost? runtimeHost,
        Func<Task<ApplicationCompositionResult>> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        try
        {
            return await continuation().ConfigureAwait(false);
        }
        catch
        {
            if (runtimeHost is not null)
            {
                await runtimeHost.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static ApplicationCompositionResult CreateCore(
        WorkflowRuntimeEnvironment environment,
        IApplicationPaths? applicationPaths,
        string applicationRoot,
        IUiThreadGuard? uiThreadGuard,
        ProductionModelAvailabilitySnapshot? modelAvailability,
        ProductionRuntimeAvailabilitySnapshot? runtimeAvailability,
        Func<CancellationToken, Task<RealEsrganBackendResolution>>? enhancementResolver,
        RealEsrganBackendResolution? enhancementResolution,
        DomainResult<ProductionInferenceRuntimeHost>? precreatedInference,
        (IProductionOcrAdapter? Adapter, DomainError? Error)? precreatedOcr)
    {
        (IPdfImportService PdfImporter, bool ReviewedRendererConfigured, DomainError? Error) pdf =
            CreateReviewedPdfImporter(applicationRoot);
        DomainResult<ProductionInferenceRuntimeHost>? inference = precreatedInference ??
            (environment == WorkflowRuntimeEnvironment.Production && applicationPaths is not null
                ? ProductionInferenceRuntimeFactory.Create(
                    applicationPaths,
                    uiThreadGuard ?? NoUiThreadGuard.Instance)
                : null);
        var rasterFrameDecoder = new ProductionRasterFrameDecoder();
        ProductionAxisGeometryAdapter? axisAdapter = CreateApprovedAxisAdapter(
            runtimeAvailability,
            rasterFrameDecoder);
        (IProductionMarkerCenterAdapter? Adapter, DomainError? Error) markerCenter =
            CreateApprovedMarkerCenterAdapter(modelAvailability, inference?.Value);
        (ProductionMarkerArtifactMaskAdapter? Adapter, DomainError? Error) artifactMask =
            CreateApprovedArtifactMaskAdapter(modelAvailability, inference?.Value);
        var detectionMaskComposer = new ProductionDetectionMaskComposer(artifactMask.Adapter);
        (ProductionMarkerClassificationAdapter? Adapter, DomainError? Error) markerClassifier =
            CreateApprovedMarkerClassifierAdapter(modelAvailability, inference?.Value);
        (IProductionOcrAdapter? Adapter, DomainError? Error) ocr =
            precreatedOcr ?? (null, null);
        IProductionOcrAdapter? ocrAdapter = ocr.Adapter;
        var legendAdapter = new ProductionLegendReasoningAdapter();
        var phaseAdapter = new ProductionPhaseReasoningAdapter();
        bool completeDetectionAdapterAvailable =
            axisAdapter is not null &&
            ocrAdapter is not null &&
            markerCenter.Adapter is not null &&
            markerClassifier.Adapter is not null &&
            detectionMaskComposer.IsApproved;
        var approvedAdapterStages = new List<string>();
        var adapterEvidence = new List<string>();
        if (axisAdapter is not null)
        {
            approvedAdapterStages.Add("axis");
            adapterEvidence.Add($"Concrete production axis adapter '{axisAdapter.AdapterId}' is composed.");
        }

        if (markerClassifier.Adapter is not null)
        {
            adapterEvidence.Add(
                $"Concrete production marker-classifier component '{markerClassifier.Adapter.AdapterId}' is composed; marker-center remains independently required.");
        }

        if (markerCenter.Adapter is not null)
        {
            adapterEvidence.Add(
                $"Concrete production marker-center component '{markerCenter.Adapter.AdapterId}' is composed; the complete marker workflow adapter remains independently required.");
        }

        if (artifactMask.Adapter is not null)
        {
            adapterEvidence.Add(
                $"Concrete production artifact-mask component '{artifactMask.Adapter.AdapterId}' is composed from the independently gated marker-center artifact head.");
        }

        if (ocrAdapter is null)
        {
            adapterEvidence.Add(
                "No production OCR pipeline is composed because no checksum-bound detector and recognizer pair currently has an executable approved adapter factory.");
        }
        else
        {
            approvedAdapterStages.Add("ocr");
            adapterEvidence.Add(
                $"Concrete production OCR component '{ocrAdapter.AdapterId}' is composed from two independently approved CPU-bound payloads.");
        }

        if (completeDetectionAdapterAvailable)
        {
            approvedAdapterStages.Add("markers");
            adapterEvidence.Add(
                "The complete production detection, projection, and export-evidence adapter can be composed from approved components.");
        }

        approvedAdapterStages.Add("legends");
        approvedAdapterStages.Add("phases");
        adapterEvidence.Add(
            $"Deterministic production semantic adapters '{legendAdapter.AdapterId}' and '{phaseAdapter.AdapterId}' are composed and remain dependency-gated.");
        adapterEvidence.Add(
            detectionMaskComposer.IsApproved
                ? $"Production mask composer '{detectionMaskComposer.AdapterId}' is composed with approved artifact-mask evidence."
                : $"Production mask composer '{detectionMaskComposer.AdapterId}' is fail closed because no approved arrow, bracket, legend, and connecting-line-intersection mask provider is composed.");

        ProductionDetectionAdapterAvailabilitySnapshot? adapterAvailability = adapterEvidence.Count == 0
            ? null
            : new ProductionDetectionAdapterAvailabilitySnapshot(
                approvedAdapterStages,
                string.Join(' ', adapterEvidence));
        IReadOnlyList<AutomaticStageStatus> automaticStages =
            ProductionStageAvailabilityRegistry.Create(
                enhancementResolution?.IsAvailable == true,
                modelAvailability,
                pdf.ReviewedRendererConfigured,
                runtimeAvailability,
                inference?.Value is not null,
                adapterAvailability,
                DescribeEnhancementResolution(enhancementResolution));
        return environment switch
        {
            WorkflowRuntimeEnvironment.Production => CreateProduction(
                environment,
                applicationPaths,
                enhancementResolver,
                automaticStages,
                pdf,
                inference,
                axisAdapter,
                ocrAdapter,
                markerCenter.Adapter,
                markerClassifier.Adapter,
                legendAdapter,
                phaseAdapter,
                rasterFrameDecoder,
                detectionMaskComposer,
                ocr.Error ?? markerCenter.Error ?? markerClassifier.Error ?? artifactMask.Error,
                artifactMask.Adapter),
            WorkflowRuntimeEnvironment.ManualPreview => new ApplicationCompositionResult(
                environment,
                new ManualPreviewWorkspaceService(
                    applicationPaths,
                    automaticStages: automaticStages,
                    enhancementResolver: enhancementResolver,
                    pdfImportService: pdf.PdfImporter),
                null),
            WorkflowRuntimeEnvironment.RecordedFake => new ApplicationCompositionResult(
                environment,
                new FakeWorkspaceService(),
                null),
            _ => throw new ArgumentOutOfRangeException(nameof(environment)),
        };
    }

    private static IUiThreadGuard CreateProductionUiThreadGuard()
    {
        System.Windows.Threading.Dispatcher? dispatcher =
            System.Windows.Application.Current?.Dispatcher;
        return dispatcher is null
            ? NoUiThreadGuard.Instance
            : new DispatcherUiThreadGuard(dispatcher);
    }

    private static ApplicationCompositionResult CreateProduction(
        WorkflowRuntimeEnvironment environment,
        IApplicationPaths? applicationPaths,
        Func<CancellationToken, Task<RealEsrganBackendResolution>>? enhancementResolver,
        IReadOnlyList<AutomaticStageStatus> automaticStages,
        (IPdfImportService PdfImporter, bool ReviewedRendererConfigured, DomainError? Error) pdf,
        DomainResult<ProductionInferenceRuntimeHost>? inference,
        IProductionAxisGeometryAdapter? axisAdapter,
        IProductionOcrAdapter? ocrAdapter,
        IProductionMarkerCenterAdapter? markerCenterAdapter,
        IProductionMarkerClassificationAdapter? markerClassificationAdapter,
        IProductionLegendReasoningAdapter? legendReasoningAdapter,
        IProductionPhaseReasoningAdapter? phaseReasoningAdapter,
        IProductionRasterFrameDecoder rasterFrameDecoder,
        ProductionDetectionMaskComposer detectionMaskComposer,
        DomainError? markerAdapterError,
        IProductionArtifactMaskAdapter? artifactMaskAdapter)
    {
        var imageImporter = new ImageImportService();
        var exportService = new ExportService();
        var panelStore = new ProductionWorkflowPanelStore();
        IProductionWorkflowDetectionAdapter? automaticDetectionAdapter =
            axisAdapter is not null &&
            ocrAdapter is not null &&
            markerCenterAdapter is not null &&
            markerClassificationAdapter is not null &&
            legendReasoningAdapter is not null &&
            phaseReasoningAdapter is not null &&
            detectionMaskComposer.IsApproved
                ? new ProductionAutomaticDetectionAdapter(
                    panelStore,
                    rasterFrameDecoder,
                    axisAdapter,
                    ocrAdapter,
                    detectionMaskComposer,
                    markerCenterAdapter,
                    markerClassificationAdapter,
                    legendReasoningAdapter,
                    phaseReasoningAdapter)
                : null;
        var services = new WorkflowServiceSet(
            new ProductionWorkflowImportStage(panelStore, imageImporter, pdf.PdfImporter),
            new ProductionWorkflowPrepareStage(panelStore),
            new ProductionWorkflowDetectionStage(panelStore, automaticDetectionAdapter),
            new ProductionWorkflowExportStage(panelStore, exportService));
        var orchestrator = new WorkflowOrchestrator(services);
        DomainError? inferenceError = inference is { Errors.Count: > 0 }
            ? inference.Errors[0]
            : null;
        return new ApplicationCompositionResult(
            environment,
            new ProductionWorkspaceService(
                applicationPaths,
                imageImporter,
                exportService: exportService,
                automaticStages: automaticStages,
                enhancementResolver: enhancementResolver,
                workflowOrchestrator: orchestrator,
                panelStore: panelStore,
                pdfImportService: pdf.PdfImporter),
            pdf.Error ?? inferenceError ?? markerAdapterError,
            inference?.Value,
            axisAdapter,
            markerCenterAdapter,
            markerClassificationAdapter,
            legendReasoningAdapter,
            phaseReasoningAdapter,
            rasterFrameDecoder,
            detectionMaskComposer,
            ocrAdapter,
            automaticDetectionAdapter,
            artifactMaskAdapter);
    }

    private static ProductionAxisGeometryAdapter? CreateApprovedAxisAdapter(
        ProductionRuntimeAvailabilitySnapshot? runtimeAvailability,
        IProductionRasterFrameDecoder frameDecoder)
    {
        if (runtimeAvailability is not { AxisApproved: true } ||
            string.IsNullOrWhiteSpace(runtimeAvailability.RuntimeSha256))
        {
            return null;
        }

        return new ProductionAxisGeometryAdapter(
            runtimeAvailability.RuntimeSha256,
            isApproved: true,
            frameDecoder: frameDecoder);
    }

    private static (ProductionMarkerClassificationAdapter? Adapter, DomainError? Error)
        CreateApprovedMarkerClassifierAdapter(
            ProductionModelAvailabilitySnapshot? modelAvailability,
            ProductionInferenceRuntimeHost? runtimeHost)
    {
        if (runtimeHost is null ||
            modelAvailability is null ||
            !modelAvailability.ApprovedCpuModels.TryGetValue(
                "marker_classifier",
                out ResolvedProductionModel? model))
        {
            return (null, null);
        }

        try
        {
            return (ProductionMarkerClassificationAdapter.Create(model, runtimeHost), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (
                null,
                new DomainError(
                    "MARKER_CLASSIFIER_ADAPTER_UNAVAILABLE",
                    DomainErrorSeverity.Warning,
                    "Errors.ProductionWorkflowUnavailable",
                    $"The checksum-resolved marker classifier could not be composed: {exception.Message}",
                    Recoverable: true,
                    "continue_manual_or_repair_model_store"));
        }
    }

    private static async Task<(IProductionOcrAdapter? Adapter, DomainError? Error)>
        CreateApprovedOcrAdapterAsync(
            ProductionModelAvailabilitySnapshot? modelAvailability,
            ProductionInferenceRuntimeHost? runtimeHost,
            ProductionRuntimeAvailabilitySnapshot? runtimeAvailability,
            CancellationToken cancellationToken)
    {
        if (runtimeHost is null || modelAvailability is null ||
            !modelAvailability.ApprovedCpuModels.TryGetValue(
                "ocr_detection",
                out ResolvedProductionModel? detectionModel) ||
            !modelAvailability.ApprovedCpuModels.TryGetValue(
                "ocr_recognition",
                out ResolvedProductionModel? recognitionModel))
        {
            return (null, null);
        }

        if (runtimeAvailability is not { AxisApproved: true } ||
            string.IsNullOrWhiteSpace(runtimeAvailability.RuntimeSha256))
        {
            return (
                null,
                new DomainError(
                    "OCR_ADAPTER_UNAVAILABLE",
                    DomainErrorSeverity.Warning,
                    "Errors.ProductionWorkflowUnavailable",
                    "The checksum-resolved OCR pair requires the exact reviewed OpenCV runtime with provenance, notice, clean-machine, and release approval evidence.",
                    Recoverable: true,
                    "continue_manual_or_restore_reviewed_opencv_runtime"));
        }

        try
        {
            IProductionOcrAdapter adapter =
                ProductionComponentOcrAdapterFactory.UsesComponentEnsemble(recognitionModel)
                    ? await ProductionComponentOcrAdapterFactory.CreateAsync(
                            detectionModel,
                            recognitionModel,
                            runtimeHost,
                            runtimeAvailability.RuntimeSha256,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await ProductionOcrAdapter.CreateAsync(
                            detectionModel,
                            recognitionModel,
                            runtimeHost,
                            runtimeAvailability.RuntimeSha256,
                            cancellationToken)
                        .ConfigureAwait(false);
            return (adapter, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (
                null,
                new DomainError(
                    "OCR_ADAPTER_UNAVAILABLE",
                    DomainErrorSeverity.Warning,
                    "Errors.ProductionWorkflowUnavailable",
                    $"The checksum-resolved OCR detector and recognizer could not be composed: {exception.Message}",
                    Recoverable: true,
                    "continue_manual_or_repair_model_store"));
        }
    }

    private static (IProductionMarkerCenterAdapter? Adapter, DomainError? Error)
        CreateApprovedMarkerCenterAdapter(
            ProductionModelAvailabilitySnapshot? modelAvailability,
            ProductionInferenceRuntimeHost? runtimeHost)
    {
        if (runtimeHost is null ||
            modelAvailability is null ||
            !modelAvailability.ApprovedCpuModels.TryGetValue(
                "marker_center",
                out ResolvedProductionModel? model))
        {
            return (null, null);
        }

        try
        {
            IProductionMarkerCenterAdapter adapter = string.Equals(
                    model.Identity.ModelId,
                    ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateRevision,
                    StringComparison.Ordinal) &&
                string.Equals(
                    model.Identity.Version,
                    ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateId,
                    StringComparison.Ordinal)
                ? ProductionProposalMarkerCenterAdapter.Create(model, runtimeHost)
                : ProductionMarkerCenterAdapter.Create(model, runtimeHost);
            return (adapter, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (
                null,
                new DomainError(
                    "MARKER_CENTER_ADAPTER_UNAVAILABLE",
                    DomainErrorSeverity.Warning,
                    "Errors.ProductionWorkflowUnavailable",
                    $"The checksum-resolved marker-center model could not be composed: {exception.Message}",
                    Recoverable: true,
                    "continue_manual_or_repair_model_store"));
        }
    }

    private static (ProductionMarkerArtifactMaskAdapter? Adapter, DomainError? Error)
        CreateApprovedArtifactMaskAdapter(
            ProductionModelAvailabilitySnapshot? modelAvailability,
            ProductionInferenceRuntimeHost? runtimeHost)
    {
        if (runtimeHost is null ||
            modelAvailability is null ||
            !modelAvailability.ApprovedCpuModels.TryGetValue(
                "marker_center",
                out ResolvedProductionModel? model))
        {
            return (null, null);
        }

        try
        {
            return (ProductionMarkerArtifactMaskAdapter.Create(model, runtimeHost), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (
                null,
                new DomainError(
                    "ARTIFACT_MASK_ADAPTER_UNAVAILABLE",
                    DomainErrorSeverity.Warning,
                    "Errors.ProductionWorkflowUnavailable",
                    $"The checksum-resolved marker-center artifact head could not be composed as an artifact mask: {exception.Message}",
                    Recoverable: true,
                    "continue_manual_or_repair_model_store"));
        }
    }

    private static Func<CancellationToken, Task<RealEsrganBackendResolution>>? CreateLocalEnhancementResolver(
        IApplicationPaths? applicationPaths)
    {
        string? manifestPath = Environment.GetEnvironmentVariable(RealEsrganManifestEnvironmentVariable);
        string? runtimeRoot = Environment.GetEnvironmentVariable(RealEsrganRuntimeRootEnvironmentVariable);
        if (applicationPaths is null ||
            string.IsNullOrWhiteSpace(manifestPath) ||
            string.IsNullOrWhiteSpace(runtimeRoot))
        {
            return null;
        }

        string cacheRoot = Path.Combine(applicationPaths.CacheRoot, "RealESRGAN");
        return cancellationToken => ManifestDrivenRealEsrganBackend.ResolveFromRuntimeRootAsync(
            manifestPath,
            runtimeRoot,
            cacheRoot,
            RealEsrganBackendPurpose.LocalEvaluation,
            cancellationToken);
    }

    private static string? DescribeEnhancementResolution(
        RealEsrganBackendResolution? resolution)
    {
        if (resolution is null)
        {
            return null;
        }

        if (resolution.IsAvailable && resolution.Model is not null)
        {
            return $"Checksum-verified {resolution.Model.ModelId}@{resolution.Model.Version} resolved as {resolution.Availability}; release eligible: {resolution.ReleaseEligible}.";
        }

        return resolution.Diagnostic is null
            ? $"The configured enhancement resolved as {resolution.Availability}."
            : $"{resolution.Diagnostic.Code}: {resolution.Diagnostic.TechnicalMessage}";
    }

    internal static (IPdfImportService PdfImporter, bool ReviewedRendererConfigured, DomainError? Error)
        CreateReviewedPdfImporter(
            string applicationRoot)
    {
        var inspector = new PdfPigDocumentInspector();
        var panelization = new PanelizationEngine();
        string? approvalPath = Environment.GetEnvironmentVariable(PdfiumApprovalEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(approvalPath))
        {
            string packagedApprovalPath = Path.Combine(
                Path.GetFullPath(applicationRoot),
                "pdfium",
                "reviewed-approval.json");
            if (!File.Exists(packagedApprovalPath))
            {
                return (
                    new PdfImportService(inspector, panelization),
                    ReviewedRendererConfigured: false,
                    Error: null);
            }

            approvalPath = packagedApprovalPath;
        }

        try
        {
            ReviewedPdfiumPageRendererBackend backend = ReviewedPdfiumPageRendererBackend.Load(approvalPath);
            return (
                new PdfImportService(
                    inspector,
                    panelization,
                    backend.CreateRenderingService()),
                ReviewedRendererConfigured: true,
                Error: null);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            UnauthorizedAccessException or SecurityException or JsonException)
        {
            return (
                new PdfImportService(inspector, panelization),
                ReviewedRendererConfigured: false,
                Error: new DomainError(
                    "PDFIUM_APPROVAL_REJECTED",
                    DomainErrorSeverity.Warning,
                    "Errors.PdfRendererUnavailable",
                    exception.Message,
                    Recoverable: true,
                    "restore_reviewed_pdfium_approval"));
        }
    }
}
