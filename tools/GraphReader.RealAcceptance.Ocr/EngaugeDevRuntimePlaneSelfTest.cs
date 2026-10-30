// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.Markers.Detection;

namespace GraphReader.RealAcceptance.Ocr;

internal static partial class EngaugeDevRuntimePlaneBridge
{
    internal static async Task<object> RunSyntheticSelfTestAsync()
    {
        const int width = 4, height = 3;
        byte[] png = SyntheticPng(width, height);
        string hash = Convert.ToHexStringLower(SHA256.HashData(png));
        EngaugeDevImageGroup Group(EngaugeDevStudyPartition partition, string checksum, int w, int h,
            params EngaugePositiveCenter[] centers) => new(partition, checksum, w, h, png, centers, []);
        var valid = Group(EngaugeDevStudyPartition.Train, hash, width, height, new(1.5, 1), new(3, 2));
        int calls = 0;
        var gray = Enumerable.Repeat(0.5f, width * height).ToArray();
        var ocr = new float[width * height];
        var artifact = new float[width * height];
        ocr[1] = 1;
        artifact[2] = 0.75f;
        Task<MarkerImageFrame> Fake(ProductionWorkflowDetectionRequest request, CancellationToken token)
        {
            calls++;
            token.ThrowIfCancellationRequested();
            Require(request.ImageVariant == WorkflowImageVariant.Original && request.Image.Sha256 == hash &&
                request.CopyImageBytes().AsSpan().SequenceEqual(png), "request_binding");
            return Task.FromResult(new MarkerImageFrame(width, height, 1, gray, MarkerSourceImage.Original,
                MarkerAffineTransform.Identity, new MarkerMask(width, height, ocr), new MarkerMask(width, height, artifact)));
        }
        EngaugeDevRuntimePlanes result = await PrepareCoreAsync(valid, new ProductionWorkflowPanelStore(), Fake, CancellationToken.None);
        Require(calls == 1 && result.ImageSha256 == hash && result.Gray.Span[0] == 0.5f && result.Ocr.Span[1] == 1 &&
            result.Artifact.Span[2] == 0.75f && result.PositiveCenters.SequenceEqual(valid.PositiveCenters) &&
            !result.LabelsQualityApproved && !result.SealedSourceOverlapVerified && !result.CandidateAuthorized &&
            !result.NegativeLabelsAvailable, "plane_and_label_preservation");
        gray[0] = 0; ocr[1] = 0; artifact[2] = 0;
        Require(result.Gray.Span[0] == 0.5f && result.Ocr.Span[1] == 1 && result.Artifact.Span[2] == 0.75f,
            "copied_planes");
        float[] seedArtifact = new float[width * height];
        float[] residualArtifact = new float[width * height];
        seedArtifact[2] = 0.5f;
        residualArtifact[2] = 0.75f;
        Task<ProductionCandidateMarkerFrameBundle> BundleFake(ProductionWorkflowDetectionRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new ProductionCandidateMarkerFrameBundle(
                new MarkerImageFrame(width, height, 1, Enumerable.Repeat(0.5f, width * height).ToArray(),
                    MarkerSourceImage.Original, MarkerAffineTransform.Identity,
                    new MarkerMask(width, height, new float[width * height]),
                    new MarkerMask(width, height, residualArtifact)),
                new MarkerMask(width, height, seedArtifact), new MarkerMask(width, height, residualArtifact)));
        }
        EngaugeDevRuntimePlanes provenance = await PrepareBundleCoreAsync(
            valid, new ProductionWorkflowPanelStore(), BundleFake, CancellationToken.None);
        Require(provenance.HasArtifactProvenance && provenance.ArtifactSeed.Span[2] == 0.5f &&
            provenance.ResidualArtifact.Span[2] == 0.75f && provenance.Artifact.Span[2] == 0.75f,
            "artifact_provenance_preserved");
        await ExpectFailure(Group(EngaugeDevStudyPartition.Development, hash, width, height, new EngaugePositiveCenter(1, 1)), Fake,
            "ENGAUGE_RUNTIME_TRAIN_PARTITION_REQUIRED");
        await ExpectFailure(Group(EngaugeDevStudyPartition.Train, new string('0', 64), width, height, new EngaugePositiveCenter(1, 1)), Fake,
            "ENGAUGE_RUNTIME_IMAGE_HASH_MISMATCH");
        await ExpectFailure(Group(EngaugeDevStudyPartition.Train, hash, width + 1, height, new EngaugePositiveCenter(1, 1)), Fake,
            "ENGAUGE_RUNTIME_IMAGE_DIMENSIONS_MISMATCH");
        await ExpectFailure(Group(EngaugeDevStudyPartition.Train, hash, width, height, new EngaugePositiveCenter(double.NaN, 1)), Fake,
            "ENGAUGE_RUNTIME_SOURCE_INVALID");
        await ExpectFailure(Group(EngaugeDevStudyPartition.Train, hash, width, height, new EngaugePositiveCenter(width, 1)), Fake,
            "ENGAUGE_RUNTIME_SOURCE_INVALID");
        Task<MarkerImageFrame> InvalidPlane(ProductionWorkflowDetectionRequest _, CancellationToken __) =>
            Task.FromResult(new MarkerImageFrame(width, height, 1,
                Enumerable.Repeat(float.NaN, width * height).ToArray(), MarkerSourceImage.Original,
                MarkerAffineTransform.Identity, new MarkerMask(width, height, ocr), new MarkerMask(width, height, artifact)));
        await ExpectFailure(valid, InvalidPlane, "ENGAUGE_RUNTIME_PLANE_INVALID");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = await PrepareCoreAsync(valid, new ProductionWorkflowPanelStore(), Fake, cancellation.Token);
            throw new InvalidOperationException("ENGAUGE_RUNTIME_CANCELLATION_NOT_OBSERVED");
        }
        catch (OperationCanceledException) { }
        Require(calls == 1, "invalid_sources_never_invoke_producer");
        return new { status = "pass", scope = "synthetic-in-memory-only", private_payload_reads = 0,
            sealed_payload_reads = 0, model_runs = 0, optimizer_steps = 0, training_authorized = false };
    }

    private static byte[] SyntheticPng(int width, int height)
    {
        byte[] pixels = Enumerable.Repeat((byte)128, width * height).ToArray();
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static async Task ExpectFailure(EngaugeDevImageGroup group,
        Func<ProductionWorkflowDetectionRequest, CancellationToken, Task<MarkerImageFrame>> producer, string code)
    {
        try { _ = await PrepareCoreAsync(group, new ProductionWorkflowPanelStore(), producer, CancellationToken.None); }
        catch (InvalidDataException error) when (error.Message == code) { return; }
        throw new InvalidOperationException(code + "_NOT_REJECTED");
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException("ENGAUGE_RUNTIME_TEST_" + check);
    }
}

internal static class EngaugeDevRuntimePlaneSelfTest
{
    internal static Task<object> RunAsync() => EngaugeDevRuntimePlaneBridge.RunSyntheticSelfTestAsync();
}
