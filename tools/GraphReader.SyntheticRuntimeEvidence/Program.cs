// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration;
using GraphReader.App.Integration.Workflow;
using GraphReader.Imaging;
using GraphReader.Inference;
using GraphReader.Ocr;
using GraphReader.Pdf;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class Program
{
    private const string ComponentGeometryProtocolSha256 =
        "efe791352c6dd29b36dc8098ed742b370a0db186c307ed6af851c82e9b54e67e";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: <input-manifest.json> <candidate.json> <candidate-sha256> <new-output-directory>");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            return await RunAsync(args, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                Status = "failed", Error = exception.Message, ProductionApproved = false,
                TrainingInputReady = false,
            }, JsonOptions));
            return exception is OperationCanceledException ? 130 : 1;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        string inputPath = Path.GetFullPath(args[0]);
        string candidatePath = Path.GetFullPath(args[1]);
        string outputRoot = Path.GetFullPath(args[3]);
        RequireArtifactOutput(outputRoot);
        byte[] candidateBytes = File.ReadAllBytes(candidatePath);
        byte[] inputBytes = File.ReadAllBytes(inputPath);
        if (!string.Equals(Hash(candidateBytes), args[2], StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Candidate descriptor checksum mismatch.");
        }
        using JsonDocument input = JsonDocument.Parse(inputBytes);
        using JsonDocument candidate = JsonDocument.Parse(candidateBytes);
        JsonElement inputs = input.RootElement;
        JsonElement config = candidate.RootElement;
        RequireKeys(inputs, "schema", "source", "preset", "seed", "split", "contains_truth", "contains_precomputed_masks", "images");
        if (Text(inputs, "schema") != "graphreader.synthetic-runtime-raster-inputs.v1" ||
            Text(inputs, "source") != "project-owned-synthetic-five-axis-family-v1" ||
            Text(inputs, "split") is not ("train" or "validation") ||
            inputs.GetProperty("contains_truth").GetBoolean() ||
            inputs.GetProperty("contains_precomputed_masks").GetBoolean())
        {
            throw new InvalidDataException("Only annotation-free project-owned synthetic train/dev raster inputs are accepted.");
        }
        if (Text(config, "schema") != "graphreader.local-synthetic-ocr-candidate.v1" ||
            config.GetProperty("production_approved").GetBoolean())
        {
            throw new InvalidDataException("An explicitly unapproved local candidate descriptor is required.");
        }
        GraphStructureConsensusGeometry outputGeometry =
            config.TryGetProperty("ocr_output_geometry", out JsonElement geometryOption)
                ? geometryOption.GetString() switch
                {
                    "model_polygon" => GraphStructureConsensusGeometry.ModelPolygon,
                    "matched_component" => GraphStructureConsensusGeometry.MatchedComponent,
                    _ => throw new InvalidDataException("Unsupported synthetic OCR output geometry."),
                }
                : GraphStructureConsensusGeometry.ModelPolygon;
        string? geometryProtocolSha256 = null;
        if (outputGeometry == GraphStructureConsensusGeometry.MatchedComponent)
        {
            if (!config.TryGetProperty("geometry_protocol", out JsonElement protocol))
            {
                throw new InvalidDataException("Experimental OCR geometry requires its pinned protocol.");
            }
            RequireKeys(protocol, "path", "sha256");
            geometryProtocolSha256 = Text(protocol, "sha256");
            if (!string.Equals(geometryProtocolSha256, ComponentGeometryProtocolSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Experimental OCR geometry requires the reviewed protocol identity.");
            }
            byte[] protocolBytes = File.ReadAllBytes(Text(protocol, "path"));
            if (!string.Equals(Hash(protocolBytes), ComponentGeometryProtocolSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Experimental OCR geometry protocol checksum mismatch.");
            }
            using JsonDocument declaration = JsonDocument.Parse(protocolBytes);
            string splitName = Text(inputs, "split") == "train" ? "train" : "dev";
            JsonElement declaredSplit = declaration.RootElement.GetProperty("split_identities").GetProperty(splitName);
            if (Text(declaration.RootElement, "evidence_policy") != "ml/policy/evidence-policy.json" ||
                declaration.RootElement.GetProperty("budget").GetProperty("sealed_runs").GetInt32() != 0 ||
                !string.Equals(Text(declaredSplit, "sha256"), Hash(inputBytes), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Experimental OCR geometry protocol does not bind these train/dev inputs.");
            }
        }
        foreach (JsonElement notice in config.GetProperty("license_inputs").EnumerateArray())
        {
            VerifyFile(Text(notice, "path"), Text(notice, "sha256"));
        }
        var images = new List<(JsonElement Record, string Path)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement record in inputs.GetProperty("images").EnumerateArray())
        {
            RequireKeys(record, "image", "image_sha256", "width", "height", "split", "family", "seed");
            string name = Text(record, "image");
            if (name != Path.GetFileName(name) || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                !names.Add(name) || !hashes.Add(Text(record, "image_sha256")) ||
                Text(record, "split") != Text(inputs, "split"))
            {
                throw new InvalidDataException("Synthetic raster names must be unique local PNG basenames in the declared split.");
            }
            string path = Path.Combine(Path.GetDirectoryName(inputPath)!, name);
            VerifyFile(path, Text(record, "image_sha256"));
            images.Add((record, path));
        }
        if (images.Count == 0)
        {
            throw new InvalidDataException("The synthetic input exchange is empty.");
        }
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            throw new IOException("Use a new output directory; prior evidence is never replaced.");
        }

        string nativePath = Path.GetFullPath(Text(config, "native_path"));
        string nativeSha = Text(config, "native_sha256");
        string nativeScope = nativeSha.ToLowerInvariant() switch
        {
            "1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd" =>
                "existing-development-runtime-unapproved-for-release",
            "87c12460daba638b36e916ea2bb832d0759fbf094b8639919a7ce11b0cca5791" =>
                "reviewed-source-runtime-local-diagnostic",
            "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31" =>
                "reviewed-source-runtime-local-diagnostic",
            _ => throw new InvalidDataException("Native runtime has no recorded local diagnostic scope."),
        };
        if (Text(config, "native_scope") != nativeScope)
        {
            throw new InvalidDataException("Native scope does not match the pinned runtime bytes.");
        }
        // Keep the exact native file read-locked for this process. This is a local
        // diagnostic identity, not a production runtime approval.
        using var nativeLock = new FileStream(nativePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(nativeLock)), nativeSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Native runtime checksum mismatch.");
        }
        nint nativeHandle = NativeLibrary.Load(nativePath);
        NativeLibrary.SetDllImportResolver(typeof(OpenCvSharp.Mat).Assembly,
            (name, _, _) => name == "OpenCvSharpExtern" ? nativeHandle : nint.Zero);
        Directory.CreateDirectory(outputRoot);
        await using var runtime = new ProductionInferenceRuntimeHost(
            new OrtExecutionProviderDiscovery(),
            new WindowsExecutionProviderPolicy(),
            new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance),
            CpuThreadConfiguration.Create(),
            [InferenceProvider.Cpu],
            Path.Combine(outputRoot, "cache"),
            ProductionInferenceRuntimeHost.DefaultQueueCapacity,
            ProductionInferenceRuntimeHost.DefaultWorkerCount);
        var total = Stopwatch.StartNew();
        ProductionOcrAdapter ocr = await ProductionOcrAdapter.CreateForLocalSyntheticCandidateEvaluationAsync(
            Descriptor(config.GetProperty("detector")), Descriptor(config.GetProperty("recognizer")),
            runtime, nativeSha, cancellationToken, outputGeometry).ConfigureAwait(false);
        LocalSyntheticOcrModelDescriptor diagnosticDescriptor = Descriptor(config.GetProperty("detector"));
        var diagnosticModelDetector = new LocalOnnxTextRegionDetector(runtime.Runtime,
            ProductionOcrAdapter.ReadDetectionOptions(diagnosticDescriptor.Identity, diagnosticDescriptor.ManifestPath));
        var diagnosticComponentDetector = new ConnectedComponentTextRegionDetector();
        var axis = new ProductionAxisGeometryAdapter(nativeSha, isApproved: false);
        var artifactAdapter = new RasterResidualArtifactMaskAdapter();
        var maskComposer = new ProductionDetectionMaskComposer(artifactAdapter);
        if (ocr.IsApproved || axis.IsApproved)
        {
            throw new InvalidOperationException("Synthetic evaluation must never approve a production adapter.");
        }
        string inputManifestSha256 = Hash(inputBytes);
        string candidateSha256 = Hash(candidateBytes);
        Guid projectId = ProductionWorkflowPanelStore.CreateStableId(
            "synthetic-runtime-project-v2",
            inputManifestSha256,
            Text(inputs, "source"),
            Text(inputs, "preset"),
            inputs.GetProperty("seed").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            Text(inputs, "split"));
        var panelStore = new ProductionWorkflowPanelStore();
        var importStage = new ProductionWorkflowImportStage(panelStore, new ImageImportService());
        var results = new List<object>();
        int completedSources = 0;
        int panelCount = 0;
        int completedPanels = 0;
        int failedPanels = 0;
        foreach ((JsonElement record, string path) in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string imageSha = Text(record, "image_sha256");
            int sourceWidth = record.GetProperty("width").GetInt32();
            int sourceHeight = record.GetProperty("height").GetInt32();
            Guid sourceId = ProductionWorkflowPanelStore.CreateStableId(
                "synthetic-runtime-source-v2",
                projectId.ToString("D"),
                inputManifestSha256,
                Path.GetFileName(path),
                imageSha,
                Text(record, "family"),
                record.GetProperty("seed").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
            WorkflowImportSnapshot imported;
            try
            {
                imported = await importStage.ImportAsync(
                        new WorkflowImportRequest(
                            projectId,
                            [new WorkflowSourceRequest(sourceId, WorkflowSourceKind.Image, path)]),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
            {
                results.Add(new
                {
                    ImageSha256 = imageSha,
                    Width = sourceWidth,
                    Height = sourceHeight,
                    Status = "failed",
                    Panels = Array.Empty<object>(),
                    Stage = "import",
                    Error = exception.Message,
                });
                continue;
            }

            var panelResults = new List<object>(imported.Panels.Count);
            int sourceCompletedPanels = 0;
            int sourceFailedPanels = 0;
            foreach (WorkflowImportedPanel panel in imported.Panels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                panelCount++;
                var timer = Stopwatch.StartNew();
                string stage = "source-validation";
                int? regionCount = null;
                int? cropCount = null;
                object? emptyOcrDiagnostic = null;
                object? ocrProposalDiagnostic = null;
                object? preOcrDiagnostic = null;
                object? panelPng = null;
                IReadOnlyList<string> importWarnings = Array.Empty<string>();
                PdfRectD? crop = null;
                PdfRectD? requestedCrop = null;
                IReadOnlyList<double>? sourceToPanel = null;
                IReadOnlyList<double>? panelToSource = null;
                try
                {
                    ProductionPanelEvidence panelEvidence = panelStore.Get(panel.PanelId);
                    RasterPanelSourceProvenance? provenance = panelEvidence.RasterPanelSource;
                    importWarnings = panelEvidence.Warnings;
                    crop = provenance?.EncodedCropInSourcePixels;
                    requestedCrop = provenance?.RequestedCropInSourcePixels;
                    sourceToPanel = provenance?.SourceToPanelMatrix;
                    panelToSource = provenance?.PanelToSourceMatrix;
                    ValidateImportedPanelSource(
                        projectId,
                        sourceId,
                        imageSha,
                        sourceWidth,
                        sourceHeight,
                        panel,
                        panelEvidence,
                        provenance);
                    byte[] panelBytes = panelEvidence.CopyOriginalBytes();
                    string panelId = panel.PanelId.ToString("D");
                    string panelRoot = Path.Combine(outputRoot, imageSha, panelId);
                    Directory.CreateDirectory(panelRoot);
                    stage = "panel-write";
                    panelPng = await WriteBytesAsync(
                            panelRoot,
                            "panel.png",
                            panelBytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var prepared = new WorkflowPreparedPanel(panel, panel.Original, enhanced: null);
                    Guid detectionRunId = ProductionWorkflowPanelStore.CreateStableId(
                        "synthetic-runtime-panel-run-v2",
                        projectId.ToString("D"),
                        sourceId.ToString("D"),
                        panelId,
                        candidateSha256);
                    var request = new ProductionWorkflowDetectionRequest(
                        prepared,
                        panel.Original,
                        WorkflowImageVariant.Original,
                        detectionRunId,
                        projectId,
                        panelBytes);
                    stage = "decode";
                    ProductionDecodedRaster raster = new ProductionRasterFrameDecoder().Decode(request, cancellationToken);
                    stage = "axis";
                    ProductionAxisGeometryEvidence geometry = await axis
                        .DetectForLocalSyntheticCandidateEvaluationAsync(request, cancellationToken).ConfigureAwait(false);
                    double left = geometry.Geometry.PlotPolygon.Points.Min(static point => point.X);
                    double top = geometry.Geometry.PlotPolygon.Points.Min(static point => point.Y);
                    double right = geometry.Geometry.PlotPolygon.Points.Max(static point => point.X);
                    double bottom = geometry.Geometry.PlotPolygon.Points.Max(static point => point.Y);
                    OcrDetectorImage detectorImage = raster.CreateOcrDetectorImage(geometry.Geometry, cancellationToken);
                    OcrImage sourceImage = raster.CreateOcrImage();
                    byte[] geometryPixels = new byte[checked(raster.Width * raster.Height)];
                    for (int index = 0; index < geometryPixels.Length; index++)
                    {
                        if ((index & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
                        // Only pixels whitened by the existing pre-OCR axis path
                        // are excluded here. Original white pixels carry no ink.
                        geometryPixels[index] = sourceImage.Pixels.Span[index] != detectorImage.Image.Pixels.Span[index]
                            ? byte.MaxValue : (byte)0;
                    }
                    stage = "pre-ocr-structure-diagnostic";
                    PreOcrStructuralProbabilityResult structure = await new RasterPreOcrStructuralProbabilityProvider()
                        .AnalyzeAsync(new PreOcrStructuralFrame(raster.Width, raster.Height,
                            sourceImage.Stride, sourceImage.Pixels, raster.Width, geometryPixels), cancellationToken)
                        .ConfigureAwait(false);
                    object sourceGray = await WriteBytesAsync(panelRoot, "source-gray8.bin", sourceImage.Pixels.ToArray(), cancellationToken).ConfigureAwait(false);
                    preOcrDiagnostic = new
                    {
                        SourceGray = sourceGray,
                        GeometryExcludedInk = await WriteBytesAsync(panelRoot, "pre-ocr-geometry-ink.bin", geometryPixels, cancellationToken).ConfigureAwait(false),
                        MarkerLike = await WritePlaneAsync(panelRoot, "pre-ocr-marker-like.f32", structure.MarkerLikeProbabilities.ToArray(), cancellationToken).ConfigureAwait(false),
                        ThinConnector = await WritePlaneAsync(panelRoot, "pre-ocr-thin-connector.f32", structure.ThinConnectorProbabilities.ToArray(), cancellationToken).ConfigureAwait(false),
                        AppliedToOcr = false, ProductionApproved = false,
                    };
                    stage = "ocr-proposal-diagnostic";
                    // Persist independent stage output for every development panel.
                    // It is diagnostic only and never substitutes for consensus output.
                    IReadOnlyList<OcrDetectedRegion> modelRegions = await diagnosticModelDetector
                        .DetectAsync(detectorImage.Image, cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<OcrDetectedRegion> componentRegions = await diagnosticComponentDetector
                        .DetectAsync(detectorImage.Image, cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<OcrDetectedRegion> unmaskedModelRegions = await diagnosticModelDetector
                        .DetectAsync(sourceImage, cancellationToken).ConfigureAwait(false);
                    ocrProposalDiagnostic = new
                    {
                        ModelRegions = modelRegions,
                        ComponentRegions = componentRegions,
                        DetectorInputSha256 = detectorImage.PixelSha256,
                        UnmaskedModelRegions = unmaskedModelRegions,
                        UnmaskedInputSha256 = Hash(sourceImage.Pixels.ToArray()),
                        CoordinateSpace = "original_pixels",
                        UsedAsAcceptedEvidence = false,
                    };
                    stage = "ocr";
                    ProductionOcrEvidence text = await ocr.RecognizeForLocalSyntheticCandidateEvaluationAsync(
                        request, raster, new OcrRectangle(left, top, right - left, bottom - top),
                        detectorImage, cancellationToken).ConfigureAwait(false);
                    regionCount = text.Result.Regions.Count;
                    cropCount = text.Result.Cache.CropCount;
                    if (regionCount == 0)
                    {
                        emptyOcrDiagnostic = new { ModelRegions = modelRegions, ComponentRegions = componentRegions };
                    }
                    stage = "seed-composition";
                    ProductionDetectionMaskSeed seed = ProductionDetectionMaskComposer.BuildSeedForLocalSyntheticCandidateEvaluation(
                        request, raster, geometry, text, cancellationToken);
                    object ocrMask = await WritePlaneAsync(panelRoot, "ocr-seed.f32", seed.CopyOcrMask().Values.ToArray(), cancellationToken).ConfigureAwait(false);
                    object geometryMask = await WritePlaneAsync(panelRoot, "geometry-seed.f32", seed.CopyArtifactMask().Values.ToArray(), cancellationToken).ConfigureAwait(false);
                    stage = "residual-artifact-diagnostic";
                    ProductionDetectionMaskEvidence masks = await maskComposer.ComposeForLocalSyntheticCandidateEvaluationAsync(
                        request, raster, geometry, text, cancellationToken).ConfigureAwait(false);
                    object artifactMask = await WritePlaneAsync(panelRoot, "composed-artifact-candidate.f32", masks.CopyArtifactMask().Values.ToArray(), cancellationToken).ConfigureAwait(false);
                    panelResults.Add(new
                    {
                        PanelId = panelId,
                        ImageSha256 = panel.Original.Sha256,
                        Width = raster.Width,
                        Height = raster.Height,
                        SourceImageSha256 = imageSha,
                        SourceWidth = sourceWidth,
                        SourceHeight = sourceHeight,
                        Crop = Box(crop),
                        RequestedCrop = Box(requestedCrop),
                        SourceToPanelMatrix = sourceToPanel,
                        PanelToSourceMatrix = panelToSource,
                        PanelPng = panelPng,
                        ImportWarnings = importWarnings,
                        Status = "seed-completed",
                        ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                        OcrMask = ocrMask,
                        GeometryMask = geometryMask,
                        SourceGray = sourceGray,
                        PreOcrDiagnostic = preOcrDiagnostic,
                        ComposedArtifactCandidateMask = artifactMask,
                        ResidualArtifactCandidateEnvelope = masks.ArtifactEnvelope,
                        ComposedMaskSourceEnvelopes = masks.SourceEnvelopes,
                        ArtifactCandidateWarnings = masks.Warnings,
                        DetectorInputSha256 = detectorImage.PixelSha256,
                        DetectorBgrSha256 = detectorImage.BgrPixelSha256,
                        Axis = geometry,
                        Ocr = text.Result,
                        OcrModels = text.ModelEvidence,
                        OcrConfiguredModels = text.ConfiguredModels,
                        OcrProposalDiagnostic = ocrProposalDiagnostic,
                    });
                    sourceCompletedPanels++;
                    completedPanels++;
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
                {
                    panelResults.Add(new
                    {
                        PanelId = panel.PanelId.ToString("D"),
                        ImageSha256 = panel.Original.Sha256,
                        Width = panel.Original.Width,
                        Height = panel.Original.Height,
                        SourceImageSha256 = imageSha,
                        SourceWidth = sourceWidth,
                        SourceHeight = sourceHeight,
                        Crop = Box(crop),
                        RequestedCrop = Box(requestedCrop),
                        SourceToPanelMatrix = sourceToPanel,
                        PanelToSourceMatrix = panelToSource,
                        PanelPng = panelPng,
                        ImportWarnings = importWarnings,
                        Status = "failed",
                        Stage = stage,
                        DetectedRegionCount = regionCount,
                        RecognitionCropCount = cropCount,
                        Error = exception.Message,
                        EmptyOcrDiagnostic = emptyOcrDiagnostic,
                        OcrProposalDiagnostic = ocrProposalDiagnostic,
                        PreOcrDiagnostic = preOcrDiagnostic,
                        ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                    });
                    sourceFailedPanels++;
                    failedPanels++;
                }
            }

            bool sourceCompleted = panelResults.Count > 0 && sourceFailedPanels == 0 &&
                sourceCompletedPanels == panelResults.Count;
            if (sourceCompleted)
            {
                completedSources++;
                results.Add(new
                {
                    ImageSha256 = imageSha,
                    Width = sourceWidth,
                    Height = sourceHeight,
                    Status = "panels-completed",
                    Panels = panelResults,
                });
            }
            else
            {
                results.Add(new
                {
                    ImageSha256 = imageSha,
                    Width = sourceWidth,
                    Height = sourceHeight,
                    Status = "failed",
                    Panels = panelResults,
                    Stage = "panels",
                    Error = panelResults.Count == 0
                        ? "Image import produced no detector-ready or manual-review panel."
                        : "One or more imported panels did not complete seed composition.",
                });
            }
        }
        object report = new
        {
            Schema = "graphreader.synthetic-runtime-seed-evidence.v2",
            Scope = "local-synthetic-seed-diagnostic", ProductionApproved = false, TrainingInputReady = false,
            CompleteArtifactMask = false, MissingStage = "representative-artifact-validation-and-training-input-binding",
            ArtifactCandidateIdentity = artifactAdapter.Identity,
            ArtifactCandidateConfiguration = JsonSerializer.Deserialize<JsonElement>(artifactAdapter.ConfigurationJson),
            InputManifestSha256 = inputManifestSha256, CandidateSha256 = candidateSha256,
            OcrAdapterId = ocr.AdapterId,
            GeometryProtocolSha256 = geometryProtocolSha256,
            NativeSha256 = nativeSha, NativeScope = nativeScope,
            RuntimeAssemblies = new[]
            {
                typeof(Program).Assembly, typeof(ProductionOcrAdapter).Assembly,
                typeof(GraphReader.Axis.AxisGeometryDetector).Assembly, typeof(OcrPipeline).Assembly,
                typeof(InferenceRuntime).Assembly,
                typeof(GraphReader.Pdf.PanelizationEngine).Assembly,
            }.Select(static assembly => new
            {
                Name = assembly.GetName().Name,
                Sha256 = Hash(File.ReadAllBytes(assembly.Location)),
            }).ToArray(),
            Count = images.Count, Completed = completedSources, Failed = images.Count - completedSources,
            PanelCount = panelCount, CompletedPanels = completedPanels, FailedPanels = failedPanels,
            ElapsedMilliseconds = total.Elapsed.TotalMilliseconds, Cases = results,
        };
        _ = await WriteBytesAsync(outputRoot, "report.json", System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine), cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Completed = completedSources,
            Failed = images.Count - completedSources,
            PanelCount = panelCount,
            CompletedPanels = completedPanels,
            FailedPanels = failedPanels,
            ProductionApproved = false,
            TrainingInputReady = false,
            Output = outputRoot,
        }, JsonOptions));
        return completedSources == images.Count ? 0 : 1;
    }

    private static void ValidateImportedPanelSource(
        Guid projectId,
        Guid sourceId,
        string sourceImageSha256,
        int sourceWidth,
        int sourceHeight,
        WorkflowImportedPanel panel,
        ProductionPanelEvidence evidence,
        RasterPanelSourceProvenance? provenance)
    {
        if (projectId == Guid.Empty || panel.SourceId != sourceId || evidence.Panel.PanelId != panel.PanelId ||
            evidence.Panel.SourceId != sourceId || evidence.SourceKind != WorkflowSourceKind.Image ||
            evidence.PdfPanelSource is not null || provenance is null ||
            !string.Equals(provenance.Source.Image.Sha256, sourceImageSha256, StringComparison.OrdinalIgnoreCase) ||
            provenance.Source.Image.Width != sourceWidth || provenance.Source.Image.Height != sourceHeight ||
            !string.Equals(Hash(provenance.Source.CopyBytes()), sourceImageSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(provenance.PanelImageSha256, panel.Original.Sha256, StringComparison.OrdinalIgnoreCase) ||
            provenance.EncodedCropInSourcePixels.Width != panel.Original.Width ||
            provenance.EncodedCropInSourcePixels.Height != panel.Original.Height)
        {
            throw new InvalidDataException(
                "Imported panel evidence does not match the manifest-bound immutable raster source and crop.");
        }
    }

    private static LocalSyntheticOcrModelDescriptor Descriptor(JsonElement record) => new(
        new ModelIdentity(Text(record, "model_id"), Text(record, "model_version"),
            Text(record, "model_sha256"), Text(record, "model_path")),
        Text(record, "manifest_path"), Text(record, "manifest_sha256"));

    private static object? Box(PdfRectD? value) => value is { } box
        ? new { box.X, box.Y, box.Width, box.Height }
        : null;

    private static void RequireArtifactOutput(string outputRoot)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
               !File.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }
        if (directory is null || !outputRoot.StartsWith(
                Path.Combine(directory.FullName, "artifacts") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Synthetic evidence output must stay under this repository's ignored artifacts directory.");
        }
    }

    private static void RequireKeys(JsonElement record, params string[] keys)
    {
        string[] actual = record.EnumerateObject().Select(static property => property.Name).ToArray();
        if (actual.Length != keys.Length || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(keys))
        {
            throw new InvalidDataException("Unexpected or duplicate synthetic input fields; truth and masks must stay outside the runtime input exchange.");
        }
    }

    private static string Text(JsonElement record, string key) =>
        record.GetProperty(key).GetString() ?? throw new InvalidDataException($"Missing {key}.");

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void VerifyFile(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Input checksum mismatch: {Path.GetFileName(path)}.");
        }
    }

    private static Task<object> WritePlaneAsync(string directory, string name, float[] values, CancellationToken cancellationToken)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Float evidence uses little-endian float32.");
        }
        return WriteEvidenceAsync(directory, name, checked(values.Length * sizeof(float)),
            (offset, buffer, count) => Buffer.BlockCopy(values, offset, buffer, 0, count), cancellationToken);
    }

    private static Task<object> WriteBytesAsync(string directory, string name, byte[] bytes, CancellationToken cancellationToken) =>
        WriteEvidenceAsync(directory, name, bytes.Length,
            (offset, buffer, count) => bytes.AsMemory(offset, count).CopyTo(buffer), cancellationToken);

    private static async Task<object> WriteEvidenceAsync(
        string directory, string name, int byteCount, Action<int, byte[], int> copyChunk,
        CancellationToken cancellationToken)
    {
        string outputPath = Path.GetFullPath(Path.Combine(directory, name));
        RequireArtifactOutput(outputPath);
        string partialPath = outputPath + ".partial";
        bool partialCreated = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65_536, FileOptions.Asynchronous))
            {
                partialCreated = true;
                var buffer = new byte[65_536];
                for (int offset = 0; offset < byteCount;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(buffer.Length, byteCount - offset);
                    copyChunk(offset, buffer, count);
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    offset += count;
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, outputPath, overwrite: false);
            return new { File = name, Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()), ByteCount = byteCount };
        }
        catch
        {
            if (partialCreated && File.Exists(partialPath)) File.Delete(partialPath);
            throw;
        }
    }
}
