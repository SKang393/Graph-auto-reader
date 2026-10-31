// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.Markers.Detection;

namespace GraphReader.RealAcceptance.Ocr;

// Private, in-memory exploratory inputs only. These do not authorize training,
// establish label quality or establish separation from sealed sources.
internal sealed class EngaugeDevRuntimePlanes
{
    internal EngaugeDevRuntimePlanes(string imageSha256, int width, int height, float[] gray, float[] ocr, float[] artifact,
        IReadOnlyList<EngaugePositiveCenter> positiveCenters)
        : this(imageSha256, width, height, gray, ocr, artifact, positiveCenters, null, null) { }

    internal EngaugeDevRuntimePlanes(string imageSha256, int width, int height, float[] gray, float[] ocr, float[] artifact,
        IReadOnlyList<EngaugePositiveCenter> positiveCenters, float[]? artifactSeed, float[]? residualArtifact)
        : this(imageSha256, width, height, gray, ocr, artifact, positiveCenters, artifactSeed, residualArtifact, null) { }

    internal EngaugeDevRuntimePlanes(string imageSha256, int width, int height, float[] gray, float[] ocr, float[] artifact,
        IReadOnlyList<EngaugePositiveCenter> positiveCenters, float[]? artifactSeed, float[]? residualArtifact,
        byte[]? residualCategoryBits)
    {
        ImageSha256 = imageSha256;
        Width = width;
        Height = height;
        Gray = gray;
        Ocr = ocr;
        Artifact = artifact;
        ArtifactSeed = artifactSeed is null ? ReadOnlyMemory<float>.Empty : artifactSeed;
        ResidualArtifact = residualArtifact is null ? ReadOnlyMemory<float>.Empty : residualArtifact;
        HasArtifactProvenance = artifactSeed is not null && residualArtifact is not null;
        ResidualCategoryBits = residualCategoryBits is null ? ReadOnlyMemory<byte>.Empty : residualCategoryBits;
        HasResidualCategoryBits = residualCategoryBits is not null;
        PositiveCenters = Array.AsReadOnly(positiveCenters.ToArray());
        LabelsQualityApproved = false;
        SealedSourceOverlapVerified = false;
        CandidateAuthorized = false;
        NegativeLabelsAvailable = false;
    }
    internal string ImageSha256 { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal ReadOnlyMemory<float> Gray { get; }
    internal ReadOnlyMemory<float> Ocr { get; }
    internal ReadOnlyMemory<float> Artifact { get; }
    internal ReadOnlyMemory<float> ArtifactSeed { get; }
    internal ReadOnlyMemory<float> ResidualArtifact { get; }
    internal bool HasArtifactProvenance { get; }
    internal ReadOnlyMemory<byte> ResidualCategoryBits { get; }
    internal bool HasResidualCategoryBits { get; }
    internal IReadOnlyList<EngaugePositiveCenter> PositiveCenters { get; }
    internal bool LabelsQualityApproved { get; }
    internal bool SealedSourceOverlapVerified { get; }
    internal bool CandidateAuthorized { get; }
    internal bool NegativeLabelsAvailable { get; }
}

internal static partial class EngaugeDevRuntimePlaneBridge
{
    internal static Task<EngaugeDevRuntimePlanes> PrepareAsync(
        EngaugeDevImageGroup group, FrozenCandidateWorkflowRuntime runtime, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return PrepareBundleCoreAsync(group, runtime.PanelStore,
            runtime.Adapter.PrepareMarkerFrameBundleForCandidateEvaluationAsync, token);
    }

    private static async Task<EngaugeDevRuntimePlanes> PrepareCoreAsync(
        EngaugeDevImageGroup group, ProductionWorkflowPanelStore store,
        Func<ProductionWorkflowDetectionRequest, CancellationToken, Task<MarkerImageFrame>> prepare,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(prepare);
        token.ThrowIfCancellationRequested();
        if (group.Partition != EngaugeDevStudyPartition.Train)
            throw new InvalidDataException("ENGAUGE_RUNTIME_TRAIN_PARTITION_REQUIRED");
        byte[] bytes = group.CopyImageBytes();
        ValidateSource(group, bytes);
        var image = new WorkflowImageEvidence("memory:engauge.png", group.ImageSha256,
            group.Width, group.Height, WorkflowImageVariant.Original);
        var panel = new WorkflowImportedPanel(Guid.NewGuid(), Guid.NewGuid(), "engauge.png", image);
        store.Register(new ProductionPanelEvidence(panel, WorkflowSourceKind.Image, bytes));
        var request = new ProductionWorkflowDetectionRequest(new WorkflowPreparedPanel(panel, image, null), image,
            WorkflowImageVariant.Original, Guid.NewGuid(), Guid.NewGuid(), bytes);
        MarkerImageFrame frame = await prepare(request, token).ConfigureAwait(false);
        return Complete(group, store, request, bytes, frame, null, null, null, token);
    }

    private static async Task<EngaugeDevRuntimePlanes> PrepareBundleCoreAsync(
        EngaugeDevImageGroup group, ProductionWorkflowPanelStore store,
        Func<ProductionWorkflowDetectionRequest, CancellationToken, Task<ProductionCandidateMarkerFrameBundle>> prepare,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(prepare);
        token.ThrowIfCancellationRequested();
        if (group.Partition != EngaugeDevStudyPartition.Train)
            throw new InvalidDataException("ENGAUGE_RUNTIME_TRAIN_PARTITION_REQUIRED");
        byte[] bytes = group.CopyImageBytes();
        ValidateSource(group, bytes);
        var image = new WorkflowImageEvidence("memory:engauge.png", group.ImageSha256,
            group.Width, group.Height, WorkflowImageVariant.Original);
        var panel = new WorkflowImportedPanel(Guid.NewGuid(), Guid.NewGuid(), "engauge.png", image);
        store.Register(new ProductionPanelEvidence(panel, WorkflowSourceKind.Image, bytes));
        var request = new ProductionWorkflowDetectionRequest(new WorkflowPreparedPanel(panel, image, null), image,
            WorkflowImageVariant.Original, Guid.NewGuid(), Guid.NewGuid(), bytes);
        ProductionCandidateMarkerFrameBundle bundle = await prepare(request, token).ConfigureAwait(false);
        if (bundle is null)
            throw new InvalidDataException("ENGAUGE_RUNTIME_FRAME_MISMATCH");
        return Complete(group, store, request, bytes, bundle.Frame, bundle.ArtifactSeedMask.Values.ToArray(),
            bundle.ResidualArtifactMask.Values.ToArray(), bundle.ResidualCategoryBits, token);
    }

    private static EngaugeDevRuntimePlanes Complete(
        EngaugeDevImageGroup group, ProductionWorkflowPanelStore store, ProductionWorkflowDetectionRequest request,
        byte[] bytes, MarkerImageFrame frame, float[]? artifactSeed, float[]? residualArtifact,
        byte[]? residualCategoryBits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateSource(group, group.CopyImageBytes());
        if (!request.CopyImageBytes().AsSpan().SequenceEqual(bytes) ||
            !store.Get(request.Panel.ImportedPanel.PanelId).CopyOriginalBytes().AsSpan().SequenceEqual(bytes) ||
            request.ImageVariant != WorkflowImageVariant.Original ||
            request.Panel.Original != request.Image ||
            !string.Equals(request.Image.Sha256, group.ImageSha256, StringComparison.Ordinal))
            throw new InvalidDataException("ENGAUGE_RUNTIME_REQUEST_CHANGED");
        if (frame is null || frame.Width != group.Width || frame.Height != group.Height ||
            frame.ChannelCount != 1 || frame.SourceImage != MarkerSourceImage.Original ||
            frame.OriginalToFrame != MarkerAffineTransform.Identity ||
            frame.OcrMask is null || frame.ArtifactMask is null ||
            frame.OcrMask.Width != group.Width || frame.OcrMask.Height != group.Height ||
            frame.ArtifactMask.Width != group.Width || frame.ArtifactMask.Height != group.Height)
            throw new InvalidDataException("ENGAUGE_RUNTIME_FRAME_MISMATCH");
        int length = checked(group.Width * group.Height);
        float[] gray = ValidatePlane(frame.ChannelsFirstPixels, length);
        float[] ocr = ValidatePlane(frame.OcrMask.Values, length);
        float[] artifact = ValidatePlane(frame.ArtifactMask.Values, length);
        if ((artifactSeed is null) != (residualArtifact is null) ||
            (residualArtifact is null) != (residualCategoryBits is null))
            throw new InvalidDataException("ENGAUGE_RUNTIME_ARTIFACT_PROVENANCE_MISMATCH");
        if (artifactSeed is not null && residualArtifact is not null)
        {
            artifactSeed = ValidatePlane(artifactSeed, length);
            residualArtifact = ValidatePlane(residualArtifact, length);
            if (residualCategoryBits!.Length != length)
                throw new InvalidDataException("ENGAUGE_RUNTIME_ARTIFACT_PROVENANCE_MISMATCH");
            for (int index = 0; index < length; index++)
            {
                if (artifact[index] != Math.Max(artifactSeed[index], residualArtifact[index]))
                    throw new InvalidDataException("ENGAUGE_RUNTIME_ARTIFACT_PROVENANCE_MISMATCH");
            }
        }
        return new EngaugeDevRuntimePlanes(group.ImageSha256, group.Width, group.Height, gray, ocr, artifact,
            group.PositiveCenters, artifactSeed, residualArtifact, residualCategoryBits);
    }

    private static void ValidateSource(EngaugeDevImageGroup group, byte[] bytes)
    {
        if (group.Width <= 0 || group.Height <= 0 || group.PositiveCenters.Count == 0 ||
            group.PositiveCenters.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X < 0 || point.Y < 0 || point.X >= group.Width || point.Y >= group.Height))
            throw new InvalidDataException("ENGAUGE_RUNTIME_SOURCE_INVALID");
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), group.ImageSha256, StringComparison.Ordinal))
            throw new InvalidDataException("ENGAUGE_RUNTIME_IMAGE_HASH_MISMATCH");
        using var stream = new MemoryStream(bytes, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder is not PngBitmapDecoder || decoder.Frames.Count != 1 ||
            decoder.Frames[0].PixelWidth != group.Width || decoder.Frames[0].PixelHeight != group.Height)
            throw new InvalidDataException("ENGAUGE_RUNTIME_IMAGE_DIMENSIONS_MISMATCH");
    }

    private static float[] ValidatePlane(ReadOnlyMemory<float> input, int length)
    {
        float[] copy = input.ToArray();
        if (copy.Length != length || copy.Any(value => !float.IsFinite(value) || value < 0 || value > 1))
            throw new InvalidDataException("ENGAUGE_RUNTIME_PLANE_INVALID");
        return copy;
    }
}
