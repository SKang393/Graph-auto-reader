// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GraphReader.Inference;

namespace GraphReader.Ocr;

/// <summary>
/// Final bounded candidate for the selected-confidence defect class. It keeps
/// the immutable V10 P1 recognition composition, then uses the independently
/// public-gated V11 proposal-role model as cross-model evidence. This candidate
/// is unavailable to ordinary production composition.
/// </summary>
public sealed class OcrV9P3CrossModelConsensusPipeline
{
    public const string CandidateCompositionId =
        "graphreader-v10-v11-cross-model-consensus-composition-v9-p3";
    public const string StageVersion = "0.0.21-v9-p3";
    public const double DirectSelectedTextMinimumConfidence = 0.75;
    public const double ConsensusSelectedTextMinimumConfidence = 0.55;
    public const double RoleMatchMinimumIntersectionOverUnion = 0.95;

    private const string P1DirectRoute = "recognizer_confirmed_detector";
    private readonly OcrV8ProductionCompositionPipeline inner;
    private readonly ITextRegionDetector roleDetector;

    public OcrV9P3CrossModelConsensusPipeline(
        OcrV8ProductionCompositionPipeline inner,
        ITextRegionDetector roleDetector)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.roleDetector = roleDetector ?? throw new ArgumentNullException(nameof(roleDetector));
        if (!string.Equals(
                inner.CompositionId,
                OcrV9CandidateCompositionFactory.CandidateCompositionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("P3 must wrap the exact immutable P1 candidate.", nameof(inner));
        }
    }

    public string ConfigurationFingerprint => HashStrings(
    [
        CandidateCompositionId,
        StageVersion,
        DirectSelectedTextMinimumConfidence.ToString("R", CultureInfo.InvariantCulture),
        ConsensusSelectedTextMinimumConfidence.ToString("R", CultureInfo.InvariantCulture),
        RoleMatchMinimumIntersectionOverUnion.ToString("R", CultureInfo.InvariantCulture),
        inner.ConfigurationFingerprint,
        roleDetector.ConfigurationFingerprint,
    ]);

    public async ValueTask<OcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        OcrRequestImageSnapshot snapshot;
        try
        {
            snapshot = OcrRequestImageSnapshot.Create(request, cancellationToken);
            request = snapshot.Request;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            string message = error is OutOfMemoryException
                ? "Insufficient memory to snapshot OCR image planes."
                : "Could not snapshot OCR input: " + error.Message;
            return new OcrResult(
                request.ContractVersion, Guid.NewGuid().ToString(), request.ProjectId, request.PanelId,
                OcrContract.Stage, StageVersion, request.InputSha256, OcrContract.CoordinateSpace,
                Array.Empty<OcrRegion>(), Array.Empty<OcrMask>(), new OcrTiming(0, 0, 0, 0), 0,
                Array.Empty<string>(), new OcrCacheDiagnostics(false, string.Empty, 0, 0),
                new OcrFailure("OCR_INPUT_INVALID", "error", "Errors.OCR_INPUT_INVALID", message, true, "retry"));
        }
        OcrResult result = await inner.RecognizeSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = result.Warnings.ToList();
        warnings.Add("ocr_v9_p3_cross_model_consensus_candidate");
        if (!result.Succeeded)
        {
            return result with
            {
                StageVersion = StageVersion,
                Warnings = OcrCollections.Freeze(warnings.Distinct(StringComparer.Ordinal)),
            };
        }

        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<OcrDetectedRegion> roleRegions;
        try
        {
            OcrImage detectorImage = request.DetectorImage?.Image ?? request.OriginalImage;
            roleRegions = await roleDetector.DetectAsync(detectorImage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            stopwatch.Stop();
            warnings.Add("ocr_v9_p3_role_detector_failed");
            return result with
            {
                RunId = Guid.NewGuid().ToString(),
                StageVersion = StageVersion,
                Regions = Array.Empty<OcrRegion>(),
                Masks = Array.Empty<OcrMask>(),
                Confidence = 0,
                Warnings = OcrCollections.Freeze(warnings.Distinct(StringComparer.Ordinal)),
                Timing = AddElapsed(result.Timing, stopwatch.Elapsed.TotalMilliseconds),
                Failure = new OcrFailure(
                    "ocr_v9_p3_role_detector_failed",
                    "error",
                    "Errors.OcrRuntimeFailed",
                    error.Message,
                    true,
                    "Review the candidate runtime evidence before retrying a new preregistered candidate."),
            };
        }
        stopwatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();

        var selected = new List<OcrRegion>(result.Regions.Count);
        foreach (OcrRegion region in result.Regions)
        {
            OcrDetectedRegion? match = BestRoleMatch(region, roleRegions);
            bool directRoute = result.Warnings.Contains(
                $"ocr_v9_candidate_acceptance_route:{region.RegionId}:{P1DirectRoute}",
                StringComparer.Ordinal);
            double selectedConfidence = SelectedTextConfidence(region);
            bool keep = !directRoute ||
                selectedConfidence >= DirectSelectedTextMinimumConfidence ||
                (match is not null &&
                 selectedConfidence >= ConsensusSelectedTextMinimumConfidence);
            if (!keep)
            {
                warnings.Add($"ocr_v9_p3_cross_model_rejected:{region.RegionId}");
                continue;
            }

            OcrTextRole role = match?.Context?.ExplicitRoleHint ?? region.Role;
            if (match?.Context?.ExplicitRoleHint is not null)
            {
                warnings.Add($"ocr_v9_p3_v11_role_applied:{region.RegionId}:{role}");
            }
            else if (!directRoute)
            {
                warnings.Add($"ocr_v9_p3_non_direct_route_retained:{region.RegionId}");
            }
            else
            {
                warnings.Add($"ocr_v9_p3_direct_confidence_retained:{region.RegionId}");
            }
            selected.Add(region with { Role = role });
        }

        HashSet<string> selectedIds = selected
            .Select(static region => region.RegionId)
            .ToHashSet(StringComparer.Ordinal);
        OcrMask[] masks = result.Masks
            .Where(mask => selectedIds.Contains(mask.RegionId))
            .ToArray();
        return result with
        {
            RunId = Guid.NewGuid().ToString(),
            StageVersion = StageVersion,
            Regions = OcrCollections.Freeze(selected),
            Masks = OcrCollections.Freeze(masks),
            Confidence = selected.Count == 0 ? 0 : selected.Average(static region => region.Confidence),
            Warnings = OcrCollections.Freeze(warnings.Distinct(StringComparer.Ordinal)),
            Timing = AddElapsed(result.Timing, stopwatch.Elapsed.TotalMilliseconds),
        };
    }

    private static OcrDetectedRegion? BestRoleMatch(
        OcrRegion region,
        IReadOnlyList<OcrDetectedRegion> roleRegions) =>
        roleRegions
            .Select(candidate => new
            {
                Candidate = candidate,
                IntersectionOverUnion = IntersectionOverUnion(region.Polygon.Bounds, candidate.Polygon.Bounds),
            })
            .Where(static item =>
                item.Candidate.Context?.ExplicitRoleHint is not null &&
                item.IntersectionOverUnion >= RoleMatchMinimumIntersectionOverUnion)
            .OrderByDescending(static item => item.IntersectionOverUnion)
            .ThenBy(static item => item.Candidate.RegionId, StringComparer.Ordinal)
            .Select(static item => item.Candidate)
            .FirstOrDefault();

    private static double SelectedTextConfidence(OcrRegion region) =>
        region.Alternatives
            .Where(alternative => string.Equals(alternative.Text, region.Text, StringComparison.Ordinal))
            .Select(static alternative => alternative.Confidence)
            .DefaultIfEmpty(0)
            .Max();

    private static double IntersectionOverUnion(OcrRectangle first, OcrRectangle second)
    {
        double width = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        double height = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        double intersection = width * height;
        double union = (first.Width * first.Height) + (second.Width * second.Height) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static OcrTiming AddElapsed(OcrTiming timing, double elapsedMilliseconds) =>
        timing with
        {
            InferenceMilliseconds = timing.InferenceMilliseconds + elapsedMilliseconds,
            TotalMilliseconds = timing.TotalMilliseconds + elapsedMilliseconds,
        };

    private static string HashStrings(IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string value in values)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

public sealed record OcrV9P3PayloadSet(
    OcrV8ProductionPayloadSet Primary,
    ModelIdentity RoleDetector);

public static class OcrV9P3CrossModelConsensusFactory
{
    public static OcrV9P3CrossModelConsensusPipeline Create(
        InferenceRuntime runtime,
        OcrV9P3PayloadSet payloads,
        IReadOnlyList<InferenceProvider> allowedProviders,
        bool bypassCache = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(payloads);
        var providers = OcrV8ProductionCompositionFactory.ValidateProviderPolicy(allowedProviders);
        OcrV8ProductionCompositionFactory.ValidatePayloads(payloads.Primary);
        OcrV8ProductionCompositionFactory.ValidatePayload(
            payloads.RoleDetector,
            OcrV11CandidateCompositionFactory.DetectorModelId,
            OcrV11CandidateCompositionFactory.DetectorSha256,
            nameof(payloads.RoleDetector));

        var roleDetector = new LocalOnnxProposalTextRegionDetector(
            runtime,
            new LocalOnnxProposalTextRegionDetectorOptions(payloads.RoleDetector)
            {
                Contract = OcrProposalClassifierContract.CompositeProposalRoleV11,
                GeometryFeatureCount = 16,
                OutputName = "proposal_role_logits",
                StageVersion = "0.0.21-v11-p2-p3-consensus",
                AllowedProviders = providers,
                BypassCache = bypassCache,
            });
        return new OcrV9P3CrossModelConsensusPipeline(
            OcrV9CandidateCompositionFactory.Create(
                runtime,
                payloads.Primary,
                providers,
                bypassCache),
            roleDetector);
    }
}
