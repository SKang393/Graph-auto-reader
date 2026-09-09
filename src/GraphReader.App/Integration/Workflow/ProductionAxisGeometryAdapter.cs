// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using GraphReader.Axis;

namespace GraphReader.App.Integration.Workflow;

public interface IProductionAxisGeometryAdapter
{
    string AdapterId { get; }

    bool IsApproved { get; }

    Task<ProductionAxisGeometryEvidence> DetectAsync(
        ProductionWorkflowDetectionRequest request,
        CancellationToken cancellationToken);
}

public sealed record ProductionAxisGeometryEvidence(
    WorkflowVisionEnvelope Envelope,
    AxisGeometryResult Geometry);

/// <summary>
/// Decodes immutable original image bytes to a grayscale frame and composes the
/// production OpenCV line provider with the deterministic axis geometry fitter.
/// </summary>
public sealed class ProductionAxisGeometryAdapter :
    IProductionAxisGeometryAdapter,
    IProductionCandidateAxisGeometryAdapter
{
    public const string StageVersion = "axis-opencv-v2";

    private readonly IAxisGeometryDetector detector;
    private readonly ILineCandidateProvider candidateProvider;
    private readonly IProductionRasterFrameDecoder frameDecoder;
    private readonly string runtimeSha256;

    public ProductionAxisGeometryAdapter(
        string runtimeSha256,
        bool isApproved,
        IAxisGeometryDetector? detector = null,
        ILineCandidateProvider? candidateProvider = null,
        IProductionRasterFrameDecoder? frameDecoder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeSha256);
        if (runtimeSha256.Length != 64 || !runtimeSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "The axis runtime SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(runtimeSha256));
        }

        this.runtimeSha256 = runtimeSha256.ToLowerInvariant();
        IsApproved = isApproved;
        this.detector = detector ?? new AxisGeometryDetector();
        this.candidateProvider = candidateProvider ?? new OpenCvLineCandidateProvider();
        this.frameDecoder = frameDecoder ?? new ProductionRasterFrameDecoder();
    }

    public string AdapterId => $"graphreader-axis-opencv:{runtimeSha256[..12]}";

    public bool IsApproved { get; }

    public async Task<ProductionAxisGeometryEvidence> DetectAsync(
        ProductionWorkflowDetectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsApproved)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
                "Errors.AxisGeometryNotFound",
                $"Axis adapter '{AdapterId}' does not have release-approved native runtime evidence.",
                "Continue with manual calibration or install the exact approved runtime.");
        }

        return await DetectCoreAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal Task<ProductionAxisGeometryEvidence> DetectForLocalSyntheticCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        CancellationToken cancellationToken)
        => DetectForCandidateEvaluationAsync(request, cancellationToken);

    Task<ProductionAxisGeometryEvidence> IProductionCandidateAxisGeometryAdapter.DetectForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        CancellationToken cancellationToken) =>
        DetectForCandidateEvaluationAsync(request, cancellationToken);

    internal Task<ProductionAxisGeometryEvidence> DetectForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsApproved)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Local candidate evaluation requires an explicitly unapproved axis adapter.",
                "Use normal production execution for an approved adapter.");
        }
        return DetectCoreAsync(request, cancellationToken);
    }

    private async Task<ProductionAxisGeometryEvidence> DetectCoreAsync(
        ProductionWorkflowDetectionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ImageVariant != WorkflowImageVariant.Original ||
            request.Image.Variant != WorkflowImageVariant.Original)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Production axis geometry must run on the immutable original image.",
                "Run axis geometry on the original image before derivative stages.");
        }

        var total = Stopwatch.StartNew();
        var preprocessing = Stopwatch.StartNew();
        ProductionDecodedRaster raster = frameDecoder.Decode(request, cancellationToken);
        GrayscaleLineCandidateFrame frame = raster.CreateAxisFrame();

        preprocessing.Stop();
        AxisGeometryResult geometry;
        try
        {
            geometry = await detector.DetectAsync(
                    frame,
                    candidateProvider,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AxisGeometryDetectionException exception)
        {
            throw Failure(
                exception.Code,
                exception.UserMessageKey,
                exception.Message,
                exception.SuggestedAction);
        }
        catch (Exception exception) when (exception is DllNotFoundException or
            EntryPointNotFoundException or BadImageFormatException)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.AxisGeometryNotFound",
                $"The approved OpenCV axis runtime could not be loaded: {exception.Message}",
                "Restore the exact reviewed native runtime or continue with manual calibration.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        total.Stop();

        var envelope = new WorkflowVisionEnvelope(
            contractVersion: 1,
            request.RunId,
            request.ProjectId,
            request.Panel.ImportedPanel.PanelId,
            stage: "axis",
            StageVersion,
            request.Image.Sha256,
            new WorkflowVisionModel(
                "OpenCvSharpExtern",
                StageVersion,
                runtimeSha256,
                "cpu"),
            new WorkflowVisionTiming(
                preprocessing.Elapsed.TotalMilliseconds,
                InferenceMilliseconds: null,
                geometry.Diagnostics.Elapsed.TotalMilliseconds,
                total.Elapsed.TotalMilliseconds),
            geometry.Confidence,
            geometry.Diagnostics.Warnings,
            request.Transforms);
        return new ProductionAxisGeometryEvidence(envelope, geometry);
    }

    private static ProductionWorkflowStageException Failure(
        string code,
        string userMessageKey,
        string technicalMessage,
        string suggestedAction) =>
        new(new ProductionWorkflowFailure(
            code,
            userMessageKey,
            technicalMessage,
            Recoverable: true,
            suggestedAction));
}
