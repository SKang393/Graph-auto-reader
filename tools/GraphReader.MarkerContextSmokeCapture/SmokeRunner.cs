// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration;
using GraphReader.App.Integration.Workflow;
using GraphReader.Imaging;
using GraphReader.Inference;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using OpenCvSharp;

namespace GraphReader.RealAcceptance.Ocr;

internal static class SmokeRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly string[] Limitations =
    [
        "Exploratory synthetic smoke capture only; not a training approval.",
        "Production raster importer/decoder, OCR candidates and V27 marker-center model are used after external review.",
        "No marker-classifier model is loaded or executed; sampled classifier inputs are captured only.",
        "No annotation, truth file, private input, sealed input or dev input is resolved.",
        "Panel provenance and sampler views are recorded; no classifier accuracy or crop-mask parity is claimed.",
    ];

    internal static async Task RunAsync(SmokeBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.ReviewApprovalValidated)
            throw new InvalidOperationException("External review receipt is required before loading candidate inference sessions.");
        if (binding.Sources.Count != 2 || binding.Sources.Any(source => source.Bytes.Length == 0))
            throw new InvalidDataException("Exactly two authenticated smoke raster sources are required.");
        cancellationToken.ThrowIfCancellationRequested();

        string root = binding.Root;
        string output = CenterBinding.Under(root, binding.OutputRelativePath);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Capture output must be fresh.");
        string outputParent = Path.GetDirectoryName(output) ?? throw new InvalidDataException("Output parent missing.");
        if (!Directory.Exists(outputParent)) throw new DirectoryNotFoundException("Output parent must exist.");
        CenterBinding.RejectReparse(root, outputParent);
        Directory.CreateDirectory(output);
        string sourceRoot = Path.Combine(output, "sources"); Directory.CreateDirectory(sourceRoot);
        string eventRoot = Path.Combine(output, "panels"); Directory.CreateDirectory(eventRoot);
        var timer = Stopwatch.StartNew();
        using Process process = Process.GetCurrentProcess();
        long peakBefore = process.PeakWorkingSet64;
        nint native = NativeLibrary.Load(Path.Combine(root, binding.BaseBinding.OpenCv.RelativePath));
        NativeLibrary.SetDllImportResolver(typeof(Mat).Assembly,
            (name, _, _) => string.Equals(name, "OpenCvSharpExtern", StringComparison.Ordinal) ? native : nint.Zero);
        var artifact = new RasterResidualArtifactMaskAdapter();
        if (artifact.Identity.ConfigurationSha256 != binding.BaseBinding.ArtifactConfigurationSha ||
            artifact.Identity.AssemblySha256 != binding.BaseBinding.ArtifactAppSha ||
            artifact.OcrAssemblySha256 != binding.BaseBinding.ArtifactOcrSha)
            throw new InvalidDataException("Actual artifact algorithm identity differs from reviewed closure.");

        CpuThreadConfiguration cpu = CpuThreadConfiguration.Create(2);
        await using var runtime = new ProductionInferenceRuntimeHost(new OrtExecutionProviderDiscovery(),
            new WindowsExecutionProviderPolicy(),
            new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled),
            cpu, [InferenceProvider.Cpu], Path.Combine(output, "inference-cache"), 1, 1, new NoPersistenceStageCache());

        FrozenCandidateOcrModelDescriptor OcrDescriptor(FrozenCandidateModel model) => new(
            new ModelIdentity(model.ModelId, model.Version, model.Payload.Sha256, Path.Combine(root, model.Payload.RelativePath)),
            Path.Combine(root, model.Manifest.RelativePath), model.Manifest.Sha256);
        ProductionOcrAdapter ocr = await ProductionOcrAdapter.CreateForFrozenCandidateEvaluationAsync(
            OcrDescriptor(binding.BaseBinding.Detector), OcrDescriptor(binding.BaseBinding.Recognizer), runtime,
            binding.BaseBinding.OpenCv.Sha256, cancellationToken, GraphStructureConsensusGeometry.ModelPolygon).ConfigureAwait(false);
        if (ocr.IsApproved || ocr.ConfigurationScope != "unapproved_frozen_candidate")
            throw new InvalidDataException("OCR candidate approval state differs.");
        var observedOcr = new ObservedOcr(ocr);
        var axis = new ObservedAxis(new ProductionAxisGeometryAdapter(binding.BaseBinding.OpenCv.Sha256, isApproved: false));
        var centerDescriptor = new FrozenCandidateMarkerCenterModelDescriptor(
            new ModelIdentity(binding.BaseBinding.Center.ModelId, binding.BaseBinding.Center.Version,
                binding.BaseBinding.Center.Payload.Sha256, Path.Combine(root, binding.BaseBinding.Center.Payload.RelativePath)),
            Path.Combine(root, binding.BaseBinding.Center.Manifest.RelativePath), binding.BaseBinding.Center.Manifest.Sha256);
        ProductionProposalMarkerCenterAdapter center = ProductionProposalMarkerCenterAdapter.CreateForFrozenCandidateCascadeEvaluation(
            centerDescriptor, runtime.Runtime);
        if (center.IsApproved) throw new InvalidDataException("Center candidate approval state differs.");

        var store = new ProductionWorkflowPanelStore();
        var automatic = new ProductionAutomaticDetectionAdapter(store, new ProductionRasterFrameDecoder(), axis,
            observedOcr, new ProductionDetectionMaskComposer(artifact), center, new NoClassification(),
            new ProductionLegendReasoningAdapter(), new ProductionPhaseReasoningAdapter());
        var importer = new ProductionWorkflowImportStage(store, new ImageImportService());
        var sourceRows = new List<object>();
        var eventRows = new List<object>();
        Guid project = ProductionWorkflowPanelStore.CreateStableId("marker-context-smoke-project-v1", binding.Digest, binding.ProtocolSha);

        foreach (SmokeSource source in binding.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourcePath = Path.Combine(sourceRoot, source.FileName);
            using (FileStream stream = new(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(source.Bytes);
            var sourcePanelRows = new List<object>();
            var processedPanels = new HashSet<Guid>();
            string sourceStatus = "completed";
            string? sourceFailure = null;
            try
            {
                WorkflowImportSnapshot snapshot = await importer.ImportAsync(new WorkflowImportRequest(project,
                    [new WorkflowSourceRequest(source.SourceId, WorkflowSourceKind.Image, sourcePath)], enhancementEnabled: false),
                    cancellationToken).ConfigureAwait(false);
                if (snapshot.Panels.Count <= 0 || snapshot.Panels.Count > 8)
                    throw new InvalidDataException("Production importer returned an unsupported panel count.");
                foreach (WorkflowImportedPanel panel in snapshot.Panels)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProductionPanelEvidence evidence = store.Get(panel.PanelId);
                    object? provenance = null;
                    try
                    {
                        if (panel.SourceId != source.SourceId || evidence.Panel.SourceId != source.SourceId ||
                            evidence.Panel.PanelId != panel.PanelId)
                            throw new InvalidDataException("Imported panel/source identity differs.");
                        provenance = PanelProvenance.Validate(evidence, source.ImageSha256, panel.Original.Sha256);
                        var prepared = new WorkflowPreparedPanel(panel, panel.Original, enhanced: null);
                        var requestId = ProductionWorkflowPanelStore.CreateStableId("marker-context-smoke-request-v1",
                            binding.Digest, source.SourceId.ToString("D"), panel.PanelId.ToString("D"));
                        var request = new ProductionWorkflowDetectionRequest(prepared, panel.Original,
                            WorkflowImageVariant.Original, requestId, project, evidence.CopyOriginalBytes());
                        ProductionCandidateMarkerFrameBundle bundle = await automatic.PrepareMarkerFrameBundleForCandidateEvaluationAsync(
                            request, cancellationToken).ConfigureAwait(false);
                        ProductionAxisGeometryEvidence geometry = axis.ForPanel(panel.PanelId);
                        var plot = new MarkerPolygon(geometry.Geometry.PlotPolygon.Points
                            .Select(point => new MarkerPoint(point.X, point.Y)).ToArray());
                        ProductionMarkerCenterEvidence centerResult = await ((IProductionCandidateMarkerCenterAdapter)center)
                            .DetectForCandidateEvaluationAsync(request, bundle.Frame, plot, cancellationToken).ConfigureAwait(false);
                        var ocrObservation = observedOcr.ForPanel(panel.PanelId);
                        if (!string.Equals(ocrObservation.Raster.InputSha256, panel.Original.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Observed OCR raster identity differs.");
                        LegendSymbolInputBatch inputs = ProductionLegendSymbolInputs.Prepare(request, ocrObservation.Raster,
                            ocrObservation.Evidence.Result, centerResult.Markers, cancellationToken);
                        IReadOnlyList<CapturedViews> views = SamplerCapture.Capture(bundle.Frame,
                            inputs.ClassifierInputs, inputs.OriginalPixelContentBounds, cancellationToken);
                        byte[] eventBytes = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            source_id = source.SourceId,
                            scene_id = source.SceneId,
                            scene_sha256 = source.SceneSha256,
                            panel_id = panel.PanelId,
                            panel_sha256 = panel.Original.Sha256,
                            provenance,
                            frame = bundle.Frame,
                            center_markers = centerResult.Markers,
                            ocr_regions = ocrObservation.Evidence.Result.Regions,
                            classifier_inputs = inputs.ClassifierInputs,
                            symbol_input_ids = inputs.SymbolInputIds,
                            symbol_crop_input_ids = inputs.SymbolCropInputIds,
                            original_pixel_content_bounds = inputs.OriginalPixelContentBounds,
                            sampled_local_and_experimental_wide_views = views,
                        }, JsonOptions);
                        string eventName = $"panel-{eventRows.Count:D4}.json";
                        using (FileStream stream = new(Path.Combine(eventRoot, eventName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            stream.Write(eventBytes);
                        eventRows.Add(new { source_id = source.SourceId, panel_id = panel.PanelId, file = eventName,
                            sha256 = CenterBinding.Hash(eventBytes) });
                        sourcePanelRows.Add(new { panel_id = panel.PanelId, status = "completed",
                            marker_count = centerResult.Markers.Count, sampler_view_count = views.Count,
                            provenance, warnings = evidence.Warnings });
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                    {
                        sourceStatus = "failed";
                        sourcePanelRows.Add(new { panel_id = panel.PanelId, status = "failed", provenance,
                            warnings = evidence.Warnings, error_type = exception.GetType().Name, error = exception.Message });
                    }
                    processedPanels.Add(panel.PanelId);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                sourceStatus = "failed";
                sourceFailure = exception.GetType().Name + ": " + exception.Message;
                foreach (ProductionPanelEvidence retained in store.PanelIds.Select(store.Get)
                    .Where(panel => panel.Panel.SourceId == source.SourceId))
                {
                    if (processedPanels.Contains(retained.Panel.PanelId)) continue;
                    object? provenance = null;
                    try { provenance = PanelProvenance.Validate(retained, source.ImageSha256, retained.Panel.Original.Sha256); }
                    catch (InvalidDataException) { }
                    sourcePanelRows.Add(new { panel_id = retained.Panel.PanelId, status = "not_processed", provenance,
                        warnings = retained.Warnings });
                }
            }
            sourceRows.Add(new { source_id = source.SourceId, scene_id = source.SceneId,
                scene_sha256 = source.SceneSha256, image_sha256 = source.ImageSha256, status = sourceStatus,
                failure = sourceFailure, panel_count = sourcePanelRows.Count, panels = sourcePanelRows });
        }

        timer.Stop();
        long peakAfter = process.PeakWorkingSet64;
        string outputHash = string.Join(";", binding.Sources.Select(source => source.ImageSha256));
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "graphreader.marker-context-bound-input-smoke-capture-report.v1",
            capture_binding_sha256 = binding.Digest,
            protocol_sha256 = binding.ProtocolSha,
            parent_binding_sha256 = binding.ParentBindingSha,
            runtime_binding_sha256 = binding.RuntimeBindingSha,
            sources = sourceRows,
            events = eventRows,
            source_count = sourceRows.Count,
            event_count = eventRows.Count,
            input_image_sha256_join = outputHash,
            marker_center_model_id = binding.BaseBinding.Center.ModelId,
            ocr_detector_model_id = binding.BaseBinding.Detector.ModelId,
            ocr_recognizer_model_id = binding.BaseBinding.Recognizer.ModelId,
            marker_classifier_model_loaded = false,
            classification_executed = false,
            truth_path_resolved = false,
            truth_content_read = false,
            private_input_supplied = false,
            sealed_input_supplied = false,
            dev_input_supplied = false,
            optimizer_steps = 0,
            training_approved = false,
            resource = new
            {
                elapsed_ms = timer.Elapsed.TotalMilliseconds,
                peak_process_working_set_bytes = Math.Max(peakBefore, peakAfter),
                process_working_set_before_capture_bytes = peakBefore,
                process_peak_working_set_after_capture_bytes = peakAfter,
                measurement_scope = "whole diagnostic process peak; includes runtime and loaded models",
            },
            limitations = Limitations,
        }, JsonOptions);
        using (FileStream reportStream = new(Path.Combine(output, "report.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            reportStream.Write(report);
    }
}
