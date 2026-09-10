// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Runtime.InteropServices;
using System.IO;
using System.Security.Cryptography;
using GraphReader.App.Integration;
using GraphReader.App.Integration.Workflow;
using GraphReader.Export;
using GraphReader.Imaging;
using GraphReader.Inference;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using OpenCvSharp;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed class FrozenCandidateWorkflowRuntime : IAsyncDisposable
{
    private readonly ProductionInferenceRuntimeHost inferenceRuntime;
    private readonly List<FileStream> readLocks;

    internal FrozenCandidateWorkflowRuntime(
        WorkflowOrchestrator workflow,
        ProductionAutomaticDetectionAdapter adapter,
        ProductionWorkflowPanelStore panelStore,
        ProductionInferenceRuntimeHost inferenceRuntime,
        IEnumerable<FileStream> readLocks)
    {
        Workflow = workflow;
        Adapter = adapter;
        PanelStore = panelStore;
        this.inferenceRuntime = inferenceRuntime;
        this.readLocks = readLocks.ToList();
    }

    internal WorkflowOrchestrator Workflow { get; }
    internal ProductionAutomaticDetectionAdapter Adapter { get; }
    internal ProductionWorkflowPanelStore PanelStore { get; }

    public async ValueTask DisposeAsync()
    {
        await inferenceRuntime.DisposeAsync().ConfigureAwait(false);
        foreach (FileStream stream in readLocks)
        {
            stream.Dispose();
        }
    }
}

internal static class FrozenCandidateWorkflowFactory
{
    internal static async Task<FrozenCandidateWorkflowRuntime> CreateAsync(
        string repositoryRoot,
        string outputRoot,
        FrozenCandidateBinding binding,
        CancellationToken cancellationToken,
        bool aggregateOnly = false)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();
        string snapshotRoot = Path.Combine(outputRoot, "frozen-inputs", "candidate");
        Directory.CreateDirectory(snapshotRoot);
        var locks = new List<FileStream>();
        nint nativeHandle = nint.Zero;
        ProductionInferenceRuntimeHost? runtimeHost = null;
        try
        {
            string detectionRoot = CreateDirectory(snapshotRoot, "ocr-detection");
            string recognitionRoot = CreateDirectory(snapshotRoot, "ocr-recognition");
            string markerRoot = CreateDirectory(snapshotRoot, "marker-center");
            string detectionPath = Materialize(detectionRoot, Path.GetFileName(binding.OcrDetection.Payload.RelativePath), binding.OcrDetection.Payload, locks);
            string detectionManifestPath = Materialize(detectionRoot, Path.GetFileName(binding.OcrDetection.Manifest.RelativePath), binding.OcrDetection.Manifest, locks);
            string recognitionPath = Materialize(recognitionRoot, Path.GetFileName(binding.OcrRecognition.Payload.RelativePath), binding.OcrRecognition.Payload, locks);
            string recognitionManifestPath = Materialize(recognitionRoot, Path.GetFileName(binding.OcrRecognition.Manifest.RelativePath), binding.OcrRecognition.Manifest, locks);
            string markerPath = Materialize(markerRoot, Path.GetFileName(binding.MarkerCenter.Payload.RelativePath), binding.MarkerCenter.Payload, locks);
            string markerManifestPath = Materialize(markerRoot, Path.GetFileName(binding.MarkerCenter.Manifest.RelativePath), binding.MarkerCenter.Manifest, locks);
            _ = Materialize(snapshotRoot, "protocol.json", binding.Protocol, locks);
            foreach (FrozenCandidateReviewedLicense license in binding.OcrDetection.ReviewedLicenseInputs)
            {
                _ = Materialize(
                    detectionRoot,
                    $"reviewed-{license.Role}{Path.GetExtension(license.File.RelativePath)}",
                    license.File,
                    locks);
            }
            foreach (FrozenCandidateReviewedLicense license in binding.OcrRecognition.ReviewedLicenseInputs)
            {
                _ = Materialize(
                    recognitionRoot,
                    $"reviewed-{license.Role}{Path.GetExtension(license.File.RelativePath)}",
                    license.File,
                    locks);
            }
            foreach (FrozenCandidateReviewedLicense license in binding.MarkerCenter.ReviewedLicenseInputs)
            {
                _ = Materialize(
                    markerRoot,
                    $"reviewed-{license.Role}{Path.GetExtension(license.File.RelativePath)}",
                    license.File,
                    locks);
            }
            foreach ((FrozenCandidateFile file, int index) in binding.ManagedFiles.Select((file, index) => (file, index)))
            {
                _ = Materialize(snapshotRoot, $"managed-{index:D2}-{Path.GetFileName(file.RelativePath)}", file, locks);
            }
            foreach ((FrozenCandidateFile file, int index) in binding.NativeFiles.Select((file, index) => (file, index)))
            {
                _ = Materialize(snapshotRoot, $"native-{index:D2}-{Path.GetFileName(file.RelativePath)}", file, locks);
            }
            string openCvPath = Materialize(snapshotRoot, "OpenCvSharpExtern.dll", binding.OpenCvNative, locks);
            LockAndVerifyExecutableInputs(
                binding.ManagedFiles,
                FrozenCandidateBinding.ActualManagedExecutablePaths(),
                "managed executable",
                locks,
                cancellationToken);
            LockAndVerifyExecutableInputs(
                binding.NativeFiles,
                FrozenCandidateBinding.ActualNativeExecutablePaths(),
                "native executable",
                locks,
                cancellationToken);

            var artifact = new RasterResidualArtifactMaskAdapter();
            var legend = new ProductionLegendReasoningAdapter();
            var phases = new ProductionPhaseReasoningAdapter();
            ValidateStaticAlgorithms(binding, artifact, legend, phases);

            nativeHandle = NativeLibrary.Load(openCvPath);
            NativeLibrary.SetDllImportResolver(
                typeof(Mat).Assembly,
                (name, _, _) => string.Equals(name, "OpenCvSharpExtern", StringComparison.Ordinal)
                    ? nativeHandle
                    : nint.Zero);

            CpuThreadConfiguration cpu = CpuThreadConfiguration.Create(binding.Runtime.IntraOperationThreads);
            runtimeHost = new ProductionInferenceRuntimeHost(
                new OrtExecutionProviderDiscovery(),
                new WindowsExecutionProviderPolicy(),
                new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled),
                cpu,
                [InferenceProvider.Cpu],
                Path.Combine(outputRoot, "inference-cache"),
                binding.Runtime.QueueCapacity,
                binding.Runtime.WorkerCount,
                aggregateOnly ? new NoPersistenceStageCache() : null);

            var detectionIdentity = new ModelIdentity(
                binding.OcrDetection.ModelId,
                binding.OcrDetection.Version,
                binding.OcrDetection.Payload.Sha256,
                detectionPath);
            var recognitionIdentity = new ModelIdentity(
                binding.OcrRecognition.ModelId,
                binding.OcrRecognition.Version,
                binding.OcrRecognition.Payload.Sha256,
                recognitionPath);
            var detectorDescriptor = new FrozenCandidateOcrModelDescriptor(
                detectionIdentity, detectionManifestPath, binding.OcrDetection.Manifest.Sha256);
            var recognizerDescriptor = new FrozenCandidateOcrModelDescriptor(
                recognitionIdentity, recognitionManifestPath, binding.OcrRecognition.Manifest.Sha256);
            ProductionOcrAdapter ocr = await (ResolveOcrComposition(binding.Algorithms) switch
            {
                OcrCompositionKind.OriginalDb => ProductionOcrAdapter.CreateForFrozenDbHeadCandidateEvaluationAsync(
                    detectorDescriptor, recognizerDescriptor, runtimeHost,
                    binding.OpenCvNative.Sha256, cancellationToken),
                OcrCompositionKind.TiledProbability => ProductionOcrAdapter.CreateForFrozenTiledProbabilityCandidateEvaluationAsync(
                    detectorDescriptor, recognizerDescriptor, runtimeHost,
                    binding.OpenCvNative.Sha256, cancellationToken),
                OcrCompositionKind.StructureConsensus => ProductionOcrAdapter.CreateForFrozenCandidateEvaluationAsync(
                    detectorDescriptor, recognizerDescriptor, runtimeHost,
                    binding.OpenCvNative.Sha256, cancellationToken,
                    GraphStructureConsensusGeometry.ModelPolygon),
                _ => throw new InvalidDataException("Frozen candidate OCR composition is unsupported."),
            }).ConfigureAwait(false);
            if (ocr.IsApproved || !string.Equals(
                    ocr.ConfigurationScope,
                    "unapproved_frozen_candidate",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Frozen candidate OCR factory changed approval scope.");
            }

            var axis = new ProductionAxisGeometryAdapter(binding.OpenCvNative.Sha256, isApproved: false);
            var masks = new ProductionDetectionMaskComposer(artifact);
            var markerDescriptor = new FrozenCandidateMarkerCenterModelDescriptor(
                    new ModelIdentity(
                        binding.MarkerCenter.ModelId,
                        binding.MarkerCenter.Version,
                        binding.MarkerCenter.Payload.Sha256,
                        markerPath),
                    markerManifestPath,
                    binding.MarkerCenter.Manifest.Sha256);
            ProductionProposalMarkerCenterAdapter marker = binding.Algorithms.MarkerProposalDomain switch
            {
                "full_frame_v24" => ProductionProposalMarkerCenterAdapter.CreateForFrozenCandidateEvaluation(
                    markerDescriptor, runtimeHost.Runtime),
                "axis_polygon_or_16px_v25" => ProductionProposalMarkerCenterAdapter.CreateForFrozenCandidatePlotDomainEvaluation(
                    markerDescriptor, runtimeHost.Runtime),
                _ => throw new InvalidDataException("Frozen candidate marker proposal domain is unsupported."),
            };
            ResolvedProductionModel classifierModel = await ResolveClassifierAsync(
                    repositoryRoot,
                    binding.MarkerClassifier,
                    locks,
                    cancellationToken)
                .ConfigureAwait(false);
            ProductionMarkerClassificationAdapter classifier =
                ProductionMarkerClassificationAdapter.Create(classifierModel, runtimeHost);
            ValidateAlgorithms(binding, ocr, axis, artifact, marker, classifier, legend, phases);

            var panelStore = new ProductionWorkflowPanelStore();
            var automatic = new ProductionAutomaticDetectionAdapter(
                panelStore,
                new ProductionRasterFrameDecoder(),
                axis,
                ocr,
                masks,
                marker,
                classifier,
                legend,
                phases);
            if (automatic.IsApproved)
            {
                throw new InvalidDataException("Frozen candidate workflow must remain explicitly unapproved.");
            }
            WorkflowOrchestrator workflow = ProductionCandidateWorkflowComposition.Create(
                panelStore,
                new ImageImportService(),
                automatic,
                new ExportService());
            return new FrozenCandidateWorkflowRuntime(
                workflow, automatic, panelStore, runtimeHost, locks);
        }
        catch
        {
            if (runtimeHost is not null)
            {
                await runtimeHost.DisposeAsync().ConfigureAwait(false);
            }
            foreach (FileStream stream in locks)
            {
                stream.Dispose();
            }
            // A DllImport resolver cannot be unset. Once installed, its native
            // handle must remain valid until this short-lived process exits.
            throw;
        }
    }

    private static async Task<ResolvedProductionModel> ResolveClassifierAsync(
        string repositoryRoot,
        FrozenCandidateClassifier binding,
        List<FileStream> locks,
        CancellationToken cancellationToken)
    {
        string storeRoot = FrozenCandidateBinding.RequireUnderRoot(
            repositoryRoot, binding.StoreRoot, "marker classifier store");
        var store = new ProductionModelStore(storeRoot);
        ResolvedProductionModel resolved = await store.ResolveAsync(
            binding.ModelId,
            binding.Version,
            InferenceProvider.Cpu,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(resolved.Task, "marker_classifier", StringComparison.Ordinal) ||
            !string.Equals(resolved.Identity.Sha256, binding.ModelSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.ManifestSha256, binding.ManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.NoticeSha256, binding.NoticeSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.BenchmarkEvidenceSha256, binding.BenchmarkSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Approved marker classifier resolution differs from the frozen candidate binding.");
        }

        var expectedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string relativePath, string payloadPath) in resolved.PayloadPaths)
        {
            if (!resolved.PayloadSha256.TryGetValue(relativePath, out string? payloadSha256))
            {
                throw new InvalidDataException("Approved marker classifier payload lacks resolved checksum evidence.");
            }
            expectedPaths.Add(payloadPath, payloadSha256);
        }
        expectedPaths.Add(resolved.ManifestPath, binding.ManifestSha256);
        expectedPaths.Add(resolved.NoticePath, binding.NoticeSha256);
        expectedPaths.Add(resolved.BenchmarkEvidencePath, binding.BenchmarkSha256);
        expectedPaths.Add(Path.Combine(storeRoot, "production-model-index.json"), binding.PackageIndexSha256);
        foreach ((string path, string expectedSha256) in expectedPaths)
        {
            locks.Add(OpenVerifiedReadLock(
                path,
                expectedSha256,
                "approved marker classifier input",
                cancellationToken));
        }
        return resolved;
    }

    private static void ValidateAlgorithms(
        FrozenCandidateBinding binding,
        ProductionOcrAdapter ocr,
        ProductionAxisGeometryAdapter axis,
        RasterResidualArtifactMaskAdapter artifact,
        ProductionProposalMarkerCenterAdapter marker,
        ProductionMarkerClassificationAdapter classifier,
        ProductionLegendReasoningAdapter legend,
        ProductionPhaseReasoningAdapter phases)
    {
        FrozenCandidateAlgorithms expected = binding.Algorithms;
        string expectedMarkerAdapterId = $"graphreader-marker-center-proposal:{binding.MarkerCenter.Payload.Sha256[..12]}" +
            (expected.MarkerProposalDomain == "axis_polygon_or_16px_v25" ? ":plot-domain-v25" : string.Empty);
        if (!ocr.AdapterId.StartsWith($"graphreader-ocr:{expected.OcrCompositionVersion}:", StringComparison.Ordinal) ||
            !string.Equals(marker.AdapterId, expectedMarkerAdapterId, StringComparison.Ordinal) ||
            !string.Equals(marker.Model.Sha256, binding.MarkerCenter.Payload.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.MarkerClassifierAdapterId, classifier.AdapterId, StringComparison.Ordinal) ||
            !string.Equals(axis.AdapterId, $"graphreader-axis-opencv:{binding.OpenCvNative.Sha256[..12]}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Frozen candidate algorithm identities do not match the actual workflow composition.");
        }
        if (axis.IsApproved || ocr.IsApproved || marker.IsApproved || artifact.IsApproved ||
            !classifier.IsApproved || !legend.IsApproved || !phases.IsApproved)
        {
            throw new InvalidDataException("Frozen candidate component approval states do not match the required boundary.");
        }
    }

    private static void ValidateStaticAlgorithms(
        FrozenCandidateBinding binding,
        RasterResidualArtifactMaskAdapter artifact,
        ProductionLegendReasoningAdapter legend,
        ProductionPhaseReasoningAdapter phases)
    {
        FrozenCandidateAlgorithms expected = binding.Algorithms;
        _ = ResolveOcrComposition(expected);
        string expectedClassifierAdapterId =
            $"graphreader-marker-classifier:{binding.MarkerClassifier.ModelSha256[..12]}";
        if (!string.Equals(expected.AxisStageVersion, ProductionAxisGeometryAdapter.StageVersion, StringComparison.Ordinal) ||
            !string.Equals(expected.ArtifactAlgorithmId, artifact.Identity.AlgorithmId, StringComparison.Ordinal) ||
            !string.Equals(expected.ArtifactAlgorithmVersion, artifact.Identity.Version, StringComparison.Ordinal) ||
            !string.Equals(expected.ArtifactConfigurationSha256, artifact.Identity.ConfigurationSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.ArtifactAppAssemblySha256, artifact.Identity.AssemblySha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.ArtifactOcrAssemblySha256, artifact.OcrAssemblySha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.MarkerCenterRevision, binding.MarkerCenter.ModelId, StringComparison.Ordinal) ||
            !string.Equals(expected.MarkerCenterCandidateId, binding.MarkerCenter.Version, StringComparison.Ordinal) ||
            !string.Equals(expected.MarkerClassifierAdapterId, expectedClassifierAdapterId, StringComparison.Ordinal) ||
            !string.Equals(expected.LegendAdapterId, legend.AdapterId, StringComparison.Ordinal) ||
            !string.Equals(expected.PhaseAdapterId, phases.AdapterId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Frozen candidate algorithm identities do not match the current executable.");
        }
    }

    internal enum OcrCompositionKind
    {
        StructureConsensus,
        TiledProbability,
        OriginalDb,
    }

    internal static bool IsTiledProbabilityComposition(FrozenCandidateAlgorithms algorithms) =>
        ResolveOcrComposition(algorithms) == OcrCompositionKind.TiledProbability;

    internal static OcrCompositionKind ResolveOcrComposition(FrozenCandidateAlgorithms algorithms)
    {
        ArgumentNullException.ThrowIfNull(algorithms);
        if (algorithms.OcrCompositionVersion == ProductionOcrAdapter.OriginalDbCandidateCompositionVersion &&
            algorithms.OcrOutputGeometry == "model_polygon")
        {
            return OcrCompositionKind.OriginalDb;
        }
        if (algorithms.OcrCompositionVersion == ProductionOcrAdapter.TiledProbabilityCandidateCompositionVersion &&
            algorithms.OcrOutputGeometry == "tiled_probability_components")
        {
            return OcrCompositionKind.TiledProbability;
        }
        if (algorithms.OcrCompositionVersion == GraphStructureConsensusTextRegionDetector.GetCompositionVersion(
                GraphStructureConsensusGeometry.ModelPolygon) && algorithms.OcrOutputGeometry == "model_polygon")
        {
            return OcrCompositionKind.StructureConsensus;
        }
        throw new InvalidDataException("Frozen candidate OCR composition is unsupported or its geometry disagrees.");
    }

    private static string Materialize(
        string root,
        string fileName,
        FrozenCandidateFile source,
        List<FileStream> locks)
    {
        string path = Path.Combine(root, fileName);
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = source.CopyBytes();
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        locks.Add(OpenVerifiedReadLock(
            path,
            source.Sha256,
            "frozen candidate snapshot",
            CancellationToken.None));
        return path;
    }

    private static string CreateDirectory(string root, string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void LockAndVerifyExecutableInputs(
        IReadOnlyList<FrozenCandidateFile> declared,
        IReadOnlyList<string> actualPaths,
        string label,
        List<FileStream> locks,
        CancellationToken cancellationToken)
    {
        foreach (FrozenCandidateFile file in declared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = actualPaths.Single(actual => string.Equals(
                Path.GetFileName(actual),
                Path.GetFileName(file.RelativePath),
                StringComparison.OrdinalIgnoreCase));
            locks.Add(OpenVerifiedReadLock(path, file.Sha256, label, cancellationToken));
        }
    }

    private static FileStream OpenVerifiedReadLock(
        string path,
        string expectedSha256,
        string label,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Locked {label} '{Path.GetFileName(path)}' differs from the frozen binding.");
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
