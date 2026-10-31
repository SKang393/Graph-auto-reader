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

internal sealed record FullCaptureOutcome(int SourceRows, int CompletedSources, int FailedSources)
{
    internal bool SourceAccountingComplete => SourceRows == 64;
    internal bool AllSourcesSucceeded => SourceAccountingComplete && CompletedSources == 64 && FailedSources == 0;
    internal string Status => AllSourcesSucceeded ? "completed_all_sources" :
        SourceAccountingComplete ? "completed_with_source_failures" : "incomplete_source_accounting";
}

internal sealed class FullCaptureInferenceState
{
    internal bool InitializationProbeStarted { get; private set; }
    internal bool InitializationProbeCompleted { get; private set; }
    internal int CorpusPanelInferenceStartCount { get; private set; }

    internal void MarkInitializationProbeStarted()
    {
        if (InitializationProbeStarted) throw new InvalidOperationException("Initialization probe was already marked started.");
        InitializationProbeStarted = true;
    }

    internal void MarkInitializationProbeCompleted()
    {
        if (!InitializationProbeStarted || InitializationProbeCompleted)
            throw new InvalidOperationException("Initialization probe completion is out of order.");
        InitializationProbeCompleted = true;
    }

    internal void MarkCorpusPanelInferenceStarted()
    {
        if (!InitializationProbeCompleted)
            throw new InvalidOperationException("Corpus inference cannot start before model initialization completes.");
        CorpusPanelInferenceStartCount = checked(CorpusPanelInferenceStartCount + 1);
    }
}

internal static class FullRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly string[] Limitations =
    [
        "Exploratory 64-source synthetic train-raster capture only; the corpus and capture are not training approval.",
        "Production raster importer/decoder, OCR candidates and V27 marker-center model are used after external review.",
        "No marker-classifier model is loaded or executed; sampled classifier inputs are captured only.",
        "Only the 64 hash-bound v5 full train raster sources are resolved; no truth file, annotation, private input, sealed input or dev input is resolved.",
        "Panel provenance and sampler views are recorded; no classifier accuracy or crop-mask parity is claimed.",
        "Sources are processed serially, one production workflow store at a time; panel JSON output is capped during serialization, after marker proposals and sampler tensors have been allocated.",
        "Output byte caps and cooperative cancellation do not impose a hard process-memory bound or hard deadline for noncooperative native calls.",
        "Elapsed and peak working set are whole-process measurements including model load and inference, not isolated sampler benchmarks.",
    ];

    private const int MaxPanelEventBytes = 64 * 1024 * 1024;
    private const long MaxTotalEventBytes = 2L * 1024 * 1024 * 1024;

    internal static async Task<FullCaptureOutcome> RunAsync(FullBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.ReviewApprovalValidated)
            throw new InvalidOperationException("External review receipt is required before loading candidate inference sessions.");
        if (binding.Sources.Count != 64 || binding.Sources.Any(source => source.Bytes.Length == 0))
            throw new InvalidDataException("Exactly 64 authenticated v5 full train raster sources are required.");
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
        string sourceLedgerRoot = Path.Combine(output, "source-results"); Directory.CreateDirectory(sourceLedgerRoot);
        var timer = Stopwatch.StartNew();
        using Process process = Process.GetCurrentProcess();
        long workingSetBeforeCapture = process.WorkingSet64;
        long peakBeforeCapture = process.PeakWorkingSet64;
        var inferenceState = new FullCaptureInferenceState();
        byte[] startReceipt = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "graphreader.marker-context-bound-input-full-capture-start.v1",
            capture_binding_sha256 = binding.Digest,
            protocol_sha256 = binding.ProtocolSha,
            parent_binding_sha256 = binding.ParentBindingSha,
            runtime_binding_sha256 = binding.RuntimeBindingSha,
            source_count = 64,
            completed_source_count = 0,
            source_accounting_complete = false,
            all_sources_succeeded = false,
            training_approved = false,
            marker_classifier_model_loaded = false,
            model_inference_started = false,
            initialization_probe_started = false,
            initialization_probe_completed = false,
            corpus_panel_inference_start_count = 0,
        }, JsonOptions);
        using (FileStream stream = new(Path.Combine(output, "capture-start.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(startReceipt);
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
        ProductionOcrAdapter ocr;
        ProductionAxisGeometryAdapter axisModel;
        ProductionProposalMarkerCenterAdapter center;
        inferenceState.MarkInitializationProbeStarted();
        WritePhaseReceipt(output, "initialization-probe-started.json", new
        {
            schema = "graphreader.marker-context-bound-input-full-initialization-probe-start.v1",
            capture_binding_sha256 = binding.Digest,
            protocol_sha256 = binding.ProtocolSha,
            phase = "detector-recognizer-adapter-creation-may-run-synthetic-zero-image-probes",
            source_count = 64,
            corpus_source_started_count = 0,
            corpus_panel_inference_start_count = 0,
            model_inference_started = true,
            initialization_probe_started = true,
            initialization_probe_completed = false,
            marker_classifier_model_loaded = false,
            training_approved = false,
        });
        try
        {
            ocr = await ProductionOcrAdapter.CreateForFrozenCandidateEvaluationAsync(
                OcrDescriptor(binding.BaseBinding.Detector), OcrDescriptor(binding.BaseBinding.Recognizer), runtime,
                binding.BaseBinding.OpenCv.Sha256, cancellationToken, GraphStructureConsensusGeometry.ModelPolygon).ConfigureAwait(false);
            if (ocr.IsApproved || ocr.ConfigurationScope != "unapproved_frozen_candidate")
                throw new InvalidDataException("OCR candidate approval state differs.");
            axisModel = new ProductionAxisGeometryAdapter(binding.BaseBinding.OpenCv.Sha256, isApproved: false);
            var centerDescriptor = new FrozenCandidateMarkerCenterModelDescriptor(
                new ModelIdentity(binding.BaseBinding.Center.ModelId, binding.BaseBinding.Center.Version,
                    binding.BaseBinding.Center.Payload.Sha256, Path.Combine(root, binding.BaseBinding.Center.Payload.RelativePath)),
                Path.Combine(root, binding.BaseBinding.Center.Manifest.RelativePath), binding.BaseBinding.Center.Manifest.Sha256);
            center = ProductionProposalMarkerCenterAdapter.CreateForFrozenCandidateCascadeEvaluation(
                centerDescriptor, runtime.Runtime);
            if (center.IsApproved) throw new InvalidDataException("Center candidate approval state differs.");
            inferenceState.MarkInitializationProbeCompleted();
            WritePhaseReceipt(output, "initialization-probe-completed.json", new
            {
                schema = "graphreader.marker-context-bound-input-full-initialization-probe-complete.v1",
                capture_binding_sha256 = binding.Digest,
                protocol_sha256 = binding.ProtocolSha,
                source_count = 64,
                corpus_source_started_count = 0,
                corpus_panel_inference_start_count = 0,
                model_inference_started = true,
                initialization_probe_started = true,
                initialization_probe_completed = true,
                marker_classifier_model_loaded = false,
                training_approved = false,
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WritePhaseReceipt(output, "initialization-probe-failed.json", new
            {
                schema = "graphreader.marker-context-bound-input-full-initialization-probe-failure.v1",
                capture_binding_sha256 = binding.Digest,
                protocol_sha256 = binding.ProtocolSha,
                source_count = 64,
                corpus_source_started_count = 0,
                corpus_panel_inference_start_count = inferenceState.CorpusPanelInferenceStartCount,
                model_inference_started = inferenceState.InitializationProbeStarted,
                initialization_probe_started = inferenceState.InitializationProbeStarted,
                initialization_probe_completed = inferenceState.InitializationProbeCompleted,
                source_accounting_complete = false,
                marker_classifier_model_loaded = false,
                training_approved = false,
                error_type = exception.GetType().Name,
                error = exception.Message,
            });
            throw;
        }
        var sourceRows = new List<object>();
        var eventRows = new List<object>();
        int completedSources = 0;
        int failedSources = 0;
        int completedPanels = 0;
        int failedPanels = 0;
        int notProcessedPanels = 0;
        long totalEventBytes = 0;
        var smokeComparisons = new List<object>(2);
        Guid project = ProductionWorkflowPanelStore.CreateStableId("marker-context-full-project-v1", binding.Digest, binding.ProtocolSha);

        for (int sourceIndex = 0; sourceIndex < binding.Sources.Count; sourceIndex++)
        {
            FullSource source = binding.Sources[sourceIndex];
            cancellationToken.ThrowIfCancellationRequested();
            var sourceTimer = Stopwatch.StartNew();
            long sourceWorkingSetBefore = process.WorkingSet64;
            string sourcePath = Path.Combine(sourceRoot, source.FileName);
            using (FileStream stream = new(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(source.Bytes);
            var sourcePanelRows = new List<object>();
            var processedPanels = new HashSet<Guid>();
            string sourceStatus = "completed";
            string? sourceFailure = null;
            int sourceMarkerCount = 0;
            int sourceSamplerViewCount = 0;
            int sourceCompletedPanelCount = 0;
            var store = new ProductionWorkflowPanelStore();
            var observedOcr = new ObservedOcr(ocr);
            var axis = new ObservedAxis(axisModel);
            var automatic = new ProductionAutomaticDetectionAdapter(store, new ProductionRasterFrameDecoder(), axis,
                observedOcr, new ProductionDetectionMaskComposer(artifact), center, new NoClassification(),
                new ProductionLegendReasoningAdapter(), new ProductionPhaseReasoningAdapter());
            var importer = new ProductionWorkflowImportStage(store, new ImageImportService());
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
                        var requestId = ProductionWorkflowPanelStore.CreateStableId("marker-context-full-request-v1",
                            binding.Digest, source.SourceId.ToString("D"), panel.PanelId.ToString("D"));
                        var request = new ProductionWorkflowDetectionRequest(prepared, panel.Original,
                            WorkflowImageVariant.Original, requestId, project, evidence.CopyOriginalBytes());
                        int nextPanelInferenceStartCount = checked(inferenceState.CorpusPanelInferenceStartCount + 1);
                        WritePhaseReceipt(output, $"panel-inference-start-{sourceIndex:D3}-{panel.PanelId:D}.json", new
                        {
                            schema = "graphreader.marker-context-bound-input-full-panel-inference-start.v1",
                            capture_binding_sha256 = binding.Digest,
                            protocol_sha256 = binding.ProtocolSha,
                            source_index = sourceIndex,
                            source_id = source.SourceId,
                            panel_id = panel.PanelId,
                            corpus_panel_inference_start_count = nextPanelInferenceStartCount,
                            model_inference_started = true,
                            initialization_probe_started = inferenceState.InitializationProbeStarted,
                            initialization_probe_completed = inferenceState.InitializationProbeCompleted,
                            marker_classifier_model_loaded = false,
                            training_approved = false,
                        });
                        inferenceState.MarkCorpusPanelInferenceStarted();
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
                        using var eventBuffer = new CappedMemoryStream(MaxPanelEventBytes);
                        JsonSerializer.Serialize(eventBuffer, new
                        {
                            source_id = source.SourceId,
                            case_id = source.CaseId,
                            scene_id = source.SceneId,
                            scene_sha256 = source.SceneSha256,
                            seed = source.Seed,
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
                        if (!eventBuffer.TryGetBuffer(out ArraySegment<byte> eventSegment))
                            throw new InvalidDataException("Bounded event buffer did not expose its written bytes.");
                        if (totalEventBytes > MaxTotalEventBytes - eventSegment.Count)
                            throw new InvalidDataException("Total capture event output exceeded the fixed 2 GiB bound.");
                        string eventName = $"panel-{eventRows.Count:D4}.json";
                        using (FileStream stream = new(Path.Combine(eventRoot, eventName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            stream.Write(eventSegment.Array!, eventSegment.Offset, checked((int)eventSegment.Count));
                        totalEventBytes += eventSegment.Count;
                        eventRows.Add(new { source_id = source.SourceId, panel_id = panel.PanelId, file = eventName,
                            sha256 = HashSegment(eventSegment) });
                        sourceMarkerCount += centerResult.Markers.Count;
                        sourceSamplerViewCount += views.Count;
                        sourceCompletedPanelCount++;
                        completedPanels++;
                        sourcePanelRows.Add(new { panel_id = panel.PanelId, status = "completed",
                            marker_count = centerResult.Markers.Count, sampler_view_count = views.Count,
                            provenance, warnings = evidence.Warnings });
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                    {
                        sourceStatus = "failed";
                        failedPanels++;
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
                    notProcessedPanels++;
                }
            }
            if (sourceStatus == "completed") completedSources++; else failedSources++;
            FullSmokeReferenceSource? smokeReference = binding.SmokeReference.SingleOrDefault(row => row.ImageSha256 == source.ImageSha256);
            if (smokeReference is not null)
            {
                smokeComparisons.Add(new
                {
                    source_id = source.SourceId,
                    image_sha256 = source.ImageSha256,
                    full_status = sourceStatus,
                    full_panel_count = sourcePanelRows.Count,
                    full_completed_panel_count = sourceCompletedPanelCount,
                    full_marker_count = sourceMarkerCount,
                    full_sampler_view_count = sourceSamplerViewCount,
                    smoke_panel_count = smokeReference.PanelCount,
                    smoke_marker_count = smokeReference.MarkerCount,
                    smoke_sampler_view_count = smokeReference.SamplerViewCount,
                    marker_count_delta = sourceMarkerCount - smokeReference.MarkerCount,
                    sampler_view_count_delta = sourceSamplerViewCount - smokeReference.SamplerViewCount,
                    comparison_by_exact_image_sha256 = true,
                });
            }
            sourceTimer.Stop();
            long sourceWorkingSetAfter = process.WorkingSet64;
            var sourceRow = new
            {
                source_index = sourceIndex,
                source_id = source.SourceId,
                case_id = source.CaseId,
                scene_id = source.SceneId,
                scene_sha256 = source.SceneSha256,
                seed = source.Seed,
                image_sha256 = source.ImageSha256,
                status = sourceStatus,
                failure = sourceFailure,
                panel_count = sourcePanelRows.Count,
                panels = sourcePanelRows,
                resource = new
                {
                    elapsed_ms = sourceTimer.Elapsed.TotalMilliseconds,
                    process_working_set_before_bytes = sourceWorkingSetBefore,
                    process_working_set_after_bytes = sourceWorkingSetAfter,
                    cumulative_process_peak_working_set_bytes = process.PeakWorkingSet64,
                    measurement_scope = "whole process; peak is cumulative since process start, not a source-local peak",
                },
            };
            byte[] sourceResult = JsonSerializer.SerializeToUtf8Bytes(sourceRow, JsonOptions);
            string sourceResultPath = Path.Combine(sourceLedgerRoot, $"source-{sourceIndex:D3}-{source.SourceId:D}.json");
            using (FileStream stream = new(sourceResultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(sourceResult);
            sourceRows.Add(sourceRow);
        }

        timer.Stop();
        long peakAfterCapture = process.PeakWorkingSet64;
        var outcome = new FullCaptureOutcome(sourceRows.Count, completedSources, failedSources);
        string outputHash = string.Join(";", binding.Sources.Select(source => source.ImageSha256));
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "graphreader.marker-context-bound-input-full-capture-report.v1",
            capture_binding_sha256 = binding.Digest,
            protocol_sha256 = binding.ProtocolSha,
            parent_binding_sha256 = binding.ParentBindingSha,
            runtime_binding_sha256 = binding.RuntimeBindingSha,
            sources = sourceRows,
            events = eventRows,
            source_count = sourceRows.Count,
            status = outcome.Status,
            source_accounting_complete = outcome.SourceAccountingComplete,
            all_sources_succeeded = outcome.AllSourcesSucceeded,
            source_status_counts = new { completed = completedSources, failed = failedSources },
            panel_status_counts = new { completed = completedPanels, failed = failedPanels, not_processed = notProcessedPanels },
            event_count = eventRows.Count,
            event_output_bytes = totalEventBytes,
            input_image_sha256_join = outputHash,
            marker_center_model_id = binding.BaseBinding.Center.ModelId,
            ocr_detector_model_id = binding.BaseBinding.Detector.ModelId,
            ocr_recognizer_model_id = binding.BaseBinding.Recognizer.ModelId,
            marker_classifier_model_loaded = false,
            model_inference_started = inferenceState.InitializationProbeStarted || inferenceState.CorpusPanelInferenceStartCount > 0,
            initialization_probe_started = inferenceState.InitializationProbeStarted,
            initialization_probe_completed = inferenceState.InitializationProbeCompleted,
            corpus_panel_inference_start_count = inferenceState.CorpusPanelInferenceStartCount,
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
                peak_process_working_set_bytes = Math.Max(peakBeforeCapture, peakAfterCapture),
                process_working_set_before_capture_bytes = workingSetBeforeCapture,
                process_peak_working_set_before_capture_bytes = peakBeforeCapture,
                process_peak_working_set_after_capture_bytes = peakAfterCapture,
                measurement_scope = "whole diagnostic process peak; includes runtime and loaded models",
            },
            resource_limits = new
            {
                cooperative_cancellation_timeout_ms = 1_800_000,
                hard_process_deadline = false,
                max_sources = 64,
                max_panels_per_source = 8,
                max_source_png_bytes = 1_048_576,
                max_panel_event_bytes = MaxPanelEventBytes,
                max_total_event_bytes = MaxTotalEventBytes,
                event_size_caps_apply_during_serialization_after_capture_arrays_are_allocated = true,
                peak_memory_is_measured_not_hard_bounded = true,
                serial_source_processing = true,
            },
            smoke_comparison = new
            {
                report_sha256 = binding.SmokeReportSha,
                compared_by_exact_image_sha256 = true,
                expected_matching_source_count = 2,
                sources = smokeComparisons,
                comparison_scope = "matched input identity and count/status summaries only; no acceptance claim",
            },
            limitations = Limitations,
        }, JsonOptions);
        using (FileStream reportStream = new(Path.Combine(output, "report.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            reportStream.Write(report);
        return outcome;
    }

    private static string HashSegment(ArraySegment<byte> segment) =>
        Convert.ToHexStringLower(SHA256.HashData(segment.AsSpan()));

    private static void WritePhaseReceipt(string output, string fileName, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using FileStream stream = new(Path.Combine(output, fileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    private sealed class CappedMemoryStream(long maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacity(1);
            base.WriteByte(value);
        }

        private void EnsureCapacity(int additionalBytes)
        {
            if (additionalBytes < 0 || Length > maximumBytes - additionalBytes)
                throw new InvalidDataException($"Panel event exceeded the fixed {maximumBytes}-byte bound.");
        }
    }
}
