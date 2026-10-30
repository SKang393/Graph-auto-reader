// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

public sealed record OcrV8ProductionCompositionOptions
{
    public const string ReviewedCompositionId =
        "graphreader-v10-bounded-zero-consensus-ambiguity-alias-composition-v8";
    public const string CandidateV11CompositionId =
        "graphreader-v11-composite-proposal-role-candidate-composition-v1";
    public const string CandidateV9CompositionId =
        "graphreader-v10-recognizer-confirmed-acceptance-composition-v9";

    public string CompositionId { get; init; } = ReviewedCompositionId;

    public string StageVersion { get; init; } = "0.0.21-v8";

    public double DetectorThreshold { get; init; } = 0.95;

    public double OfficialRescueThreshold { get; init; } = 0.90;

    public double ConsensusRescueThreshold { get; init; } = 0.85;

    public double ZeroConsensusRescueThreshold { get; init; } = 0.82;

    public double NumericMinimumConfidence { get; init; } = 0.65;

    public double OfficialMinimumConfidence { get; init; }

    public double MaskPaddingPixels { get; init; } = 1;
}

/// <summary>
/// Executes the fixed V8 detector, official PP-OCR plus source rules, numeric
/// specialist, and ambiguity specialist composition. Low-confidence proposals
/// can survive only the reviewed numeric tick rescue bands. This class does not
/// grant model approval; its payloads must first resolve from the production
/// model store and pass the separate direct-fixture promotion gate.
/// </summary>
public sealed class OcrV8ProductionCompositionPipeline
{
    private readonly ITextRegionProposalDetector detector;
    private readonly OcrPipeline officialPipeline;
    private readonly OcrPipeline numericPipeline;
    private readonly ITextRecognizer officialRecognizer;
    private readonly ITextRecognizer numericRecognizer;
    private readonly OcrV8ProductionCompositionOptions options;

    public OcrV8ProductionCompositionPipeline(
        ITextRegionProposalDetector detector,
        ITextRecognizer officialRecognizer,
        IOcrResultCache officialCache,
        OcrPipelineOptions officialPipelineOptions,
        ITextRecognizer numericRecognizer,
        IOcrResultCache numericCache,
        OcrPipelineOptions numericPipelineOptions,
        OcrV8ProductionCompositionOptions? options = null)
    {
        this.detector = detector ?? throw new ArgumentNullException(nameof(detector));
        this.officialRecognizer = officialRecognizer ??
            throw new ArgumentNullException(nameof(officialRecognizer));
        this.numericRecognizer = numericRecognizer ??
            throw new ArgumentNullException(nameof(numericRecognizer));
        ArgumentNullException.ThrowIfNull(officialCache);
        ArgumentNullException.ThrowIfNull(officialPipelineOptions);
        ArgumentNullException.ThrowIfNull(numericCache);
        ArgumentNullException.ThrowIfNull(numericPipelineOptions);
        this.options = options ?? new OcrV8ProductionCompositionOptions();
        ValidateOptions(this.options);
        officialPipeline = new OcrPipeline(
            detector,
            officialRecognizer,
            officialCache,
            officialPipelineOptions);
        numericPipeline = new OcrPipeline(
            detector,
            numericRecognizer,
            numericCache,
            numericPipelineOptions);
    }

    public string CompositionId => options.CompositionId;

    public string ConfigurationFingerprint => HashStrings(FingerprintComponents());

    public ValueTask<OcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default) =>
        RecognizeCoreAsync(request, null, cancellationToken);

    internal ValueTask<OcrResult> RecognizeSnapshotAsync(
        OcrRequestImageSnapshot snapshot,
        CancellationToken cancellationToken) =>
        RecognizeCoreAsync(snapshot.Request, snapshot, cancellationToken);

    private async ValueTask<OcrResult> RecognizeCoreAsync(
        OcrRequest request,
        OcrRequestImageSnapshot? imageSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var total = Stopwatch.StartNew();
        try
        {
            imageSnapshot ??= OcrRequestImageSnapshot.Create(request, cancellationToken);
            request = imageSnapshot.Request;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            return FailureResult(request, Error("OCR_INPUT_INVALID", "Insufficient memory to snapshot OCR image planes.", "retry"), total.Elapsed.TotalMilliseconds);
        }
        catch (Exception error)
        {
            return FailureResult(request, Error("OCR_INPUT_INVALID", "Could not snapshot OCR input: " + error.Message, "retry"), total.Elapsed.TotalMilliseconds);
        }
        OcrFailure? inputFailure = OcrPipeline.ValidateRequest(request);
        if (inputFailure is not null)
        {
            return FailureResult(request, inputFailure, total.Elapsed.TotalMilliseconds);
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<OcrDetectedRegion> proposals;
        OcrRequestImageSnapshot recognitionSnapshot;
        var detection = Stopwatch.StartNew();
        try
        {
            proposals = request.DetectedRegions ??
                await detector.DetectProposalsAsync(
                        request.DetectorImage?.Image ?? request.OriginalImage,
                        cancellationToken)
                    .ConfigureAwait(false);
            OcrPipeline.ValidateDetectedRegions(proposals);
            if (proposals.Any(proposal =>
                    proposal.DetectionConfidence < options.ZeroConsensusRescueThreshold))
            {
                throw new InvalidDataException(
                    "The V8 proposal detector returned a region below the reviewed 0.82 proposal floor.");
            }
            recognitionSnapshot = imageSnapshot.WithDetectedRegions(proposals, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return FailureResult(
                request,
                Error("OCR_REGION_DETECTION_FAILED", exception.Message, "retry"),
                total.Elapsed.TotalMilliseconds);
        }
        finally
        {
            detection.Stop();
        }

        OcrResult official = await officialPipeline
            .RecognizeSnapshotAsync(recognitionSnapshot, cancellationToken)
            .ConfigureAwait(false);
        OcrResult numeric = await numericPipeline
            .RecognizeSnapshotAsync(recognitionSnapshot, cancellationToken)
            .ConfigureAwait(false);
        string warningPrefix = WarningPrefix(options.CompositionId);
        var warnings = new List<string> { warningPrefix + "_composition" };
        warnings.AddRange(official.Warnings);
        warnings.AddRange(numeric.Warnings);
        if (official.Failure is not null)
        {
            warnings.Add($"ocr_official_pipeline_failed:{official.Failure.Code}");
        }

        if (numeric.Failure is not null)
        {
            warnings.Add($"ocr_numeric_pipeline_failed:{numeric.Failure.Code}");
        }

        var officialById = official.Regions.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        var numericById = numeric.Regions.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        var selected = new List<OcrRegion>();
        var acceptedProposals = new List<OcrDetectedRegion>();
        foreach (OcrDetectedRegion proposal in proposals
                     .OrderBy(static region => region.RegionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            officialById.TryGetValue(proposal.RegionId, out OcrRegion? officialRegion);
            numericById.TryGetValue(proposal.RegionId, out OcrRegion? numericRegion);
            string? acceptanceRoute = AcceptanceRoute(
                proposal,
                officialRegion,
                numericRegion,
                options);
            if (acceptanceRoute is null)
            {
                warnings.Add($"{warningPrefix}_proposal_rejected:{proposal.RegionId}");
                continue;
            }

            OcrRegion? chosen = ShouldSelectNumeric(numericRegion, options)
                ? numericRegion
                : officialRegion;
            if (chosen is null)
            {
                warnings.Add($"{warningPrefix}_accepted_proposal_without_recognition:{proposal.RegionId}");
                continue;
            }

            OcrRegion? other = ReferenceEquals(chosen, numericRegion) ? officialRegion : numericRegion;
            selected.Add(MergeAlternatives(chosen, other));
            acceptedProposals.Add(proposal);
            warnings.Add($"{warningPrefix}_acceptance_route:{proposal.RegionId}:{acceptanceRoute}");
            if (ReferenceEquals(chosen, numericRegion))
            {
                warnings.Add($"ocr_numeric_specialist_selected:{proposal.RegionId}");
            }
        }

        OcrRegionFailure[] regionFailures = official.RegionFailures
            .OrEmpty()
            .Concat(numeric.RegionFailures.OrEmpty())
            .Distinct()
            .OrderBy(static failure => failure.RegionId, StringComparer.Ordinal)
            .ThenBy(static failure => failure.SourceImage)
            .ThenBy(static failure => failure.Failure.Code, StringComparer.Ordinal)
            .ToArray();
        if (proposals.Count > 0 && selected.Count == 0 &&
            official.Failure is not null && numeric.Failure is not null)
        {
            return FailureResult(
                request,
                Error(
                    "OCR_RECOGNITION_ENSEMBLE_FAILED",
                    $"Official recognizer: {official.Failure.TechnicalMessage} " +
                    $"Numeric recognizer: {numeric.Failure.TechnicalMessage}",
                    warningPrefix == "ocr_v11_candidate"
                        ? "repair_ocr_v11_candidate_payload_set"
                        : "repair_ocr_v8_payload_set"),
                total.Elapsed.TotalMilliseconds,
                warnings,
                regionFailures,
                CombinedCache(official.Cache, numeric.Cache));
        }

        (int width, int height) = OriginalBounds(request.OriginalImage);
        IReadOnlyList<OcrMask> masks = OcrMaskBuilder.Build(
            acceptedProposals,
            width,
            height,
            options.MaskPaddingPixels);
        total.Stop();
        double preprocess = detection.Elapsed.TotalMilliseconds +
            official.Timing.PreprocessMilliseconds + numeric.Timing.PreprocessMilliseconds;
        double inference = official.Timing.InferenceMilliseconds + numeric.Timing.InferenceMilliseconds;
        double postprocess = official.Timing.PostprocessMilliseconds + numeric.Timing.PostprocessMilliseconds;
        double componentTotal = preprocess + inference + postprocess;
        return new OcrResult(
            request.ContractVersion,
            Guid.NewGuid().ToString(),
            request.ProjectId,
            request.PanelId,
            OcrContract.Stage,
            options.StageVersion,
            request.InputSha256,
            OcrContract.CoordinateSpace,
            OcrCollections.Freeze(selected),
            masks,
            new OcrTiming(
                preprocess,
                inference,
                postprocess,
                Math.Max(total.Elapsed.TotalMilliseconds, componentTotal)),
            selected.Count == 0 ? 0 : selected.Average(static region => region.Confidence),
            OcrCollections.Freeze(warnings.Distinct(StringComparer.Ordinal)),
            CombinedCache(official.Cache, numeric.Cache),
            null,
            OcrCollections.Freeze(regionFailures));
    }

    private static string? AcceptanceRoute(
        OcrDetectedRegion proposal,
        OcrRegion? official,
        OcrRegion? numeric,
        OcrV8ProductionCompositionOptions options)
    {
        if (proposal.DetectionConfidence >= options.DetectorThreshold)
        {
            if (options.OfficialMinimumConfidence > 0 &&
                (official is null ||
                 RecognitionConfidence(official) < options.OfficialMinimumConfidence))
            {
                return null;
            }

            return options.OfficialMinimumConfidence > 0
                ? "recognizer_confirmed_detector"
                : "detector";
        }

        if (proposal.DetectionConfidence < options.ZeroConsensusRescueThreshold ||
            official is null ||
            !OcrV8SourcePostprocessor.GraphNumber().IsMatch(official.Text.Trim()) ||
            official.Role is not (OcrTextRole.XTick or OcrTextRole.YTick))
        {
            return null;
        }

        if (proposal.DetectionConfidence >= options.OfficialRescueThreshold)
        {
            return "official_tick_rescue";
        }

        bool exactConsensus = numeric is not null &&
            string.Equals(numeric.Text, official.Text, StringComparison.Ordinal) &&
            RecognitionConfidence(numeric) >= options.NumericMinimumConfidence &&
            numeric.Role == official.Role &&
            numeric.Role is OcrTextRole.XTick or OcrTextRole.YTick;
        if (proposal.DetectionConfidence >= options.ConsensusRescueThreshold && exactConsensus)
        {
            return "official_numeric_consensus_rescue";
        }

        return exactConsensus && string.Equals(numeric!.Text, "0", StringComparison.Ordinal)
            ? "zero_numeric_consensus_rescue"
            : null;
    }

    private static bool ShouldSelectNumeric(
        OcrRegion? numeric,
        OcrV8ProductionCompositionOptions options) =>
        numeric is not null &&
        OcrV8SourcePostprocessor.GraphNumber().IsMatch(numeric.Text.Trim()) &&
        RecognitionConfidence(numeric) >= options.NumericMinimumConfidence &&
        numeric.Role is OcrTextRole.XTick or OcrTextRole.YTick;

    private static double RecognitionConfidence(OcrRegion region) =>
        region.Alternatives
            .Where(alternative => string.Equals(alternative.Text, region.Text, StringComparison.Ordinal))
            .Select(static alternative => alternative.Confidence)
            .DefaultIfEmpty(0)
            .Max();

    private static OcrRegion MergeAlternatives(OcrRegion selected, OcrRegion? other)
    {
        OcrRecognitionAlternative[] alternatives = selected.Alternatives
            .Concat(other?.Alternatives ?? Array.Empty<OcrRecognitionAlternative>())
            .Where(static alternative =>
                !string.IsNullOrWhiteSpace(alternative.Text) &&
                double.IsFinite(alternative.Confidence) &&
                alternative.Confidence is >= 0 and <= 1)
            .GroupBy(
                static alternative => (alternative.Text, alternative.SourceImage),
                EqualityComparer<(string Text, OcrSourceImage SourceImage)>.Default)
            .Select(static group => group.OrderByDescending(item => item.Confidence).First())
            .OrderByDescending(static alternative => alternative.Confidence)
            .ThenBy(static alternative => alternative.Text, StringComparer.Ordinal)
            .ThenBy(static alternative => alternative.SourceImage)
            .ToArray();
        return selected with { Alternatives = OcrCollections.Freeze(alternatives) };
    }

    private static (int Width, int Height) OriginalBounds(OcrImage image)
    {
        int width = image.CanonicalOriginalWidth ?? image.Width;
        int height = image.CanonicalOriginalHeight ?? image.Height;
        return (width, height);
    }

    private static OcrCacheDiagnostics CombinedCache(
        OcrCacheDiagnostics official,
        OcrCacheDiagnostics numeric) =>
        new(
            official.CacheHit && numeric.CacheHit,
            HashStrings([official.CacheKey, numeric.CacheKey]),
            checked(official.CropCount + numeric.CropCount),
            checked(official.BatchCount + numeric.BatchCount),
            official.RecognitionCacheHit && numeric.RecognitionCacheHit,
            HashStrings([
                official.RecognitionCacheKey ?? string.Empty,
                numeric.RecognitionCacheKey ?? string.Empty,
            ]));

    private OcrResult FailureResult(
        OcrRequest request,
        OcrFailure failure,
        double totalMilliseconds,
        IEnumerable<string>? warnings = null,
        IReadOnlyList<OcrRegionFailure>? regionFailures = null,
        OcrCacheDiagnostics? cache = null) =>
        new(
            request.ContractVersion,
            Guid.NewGuid().ToString(),
            request.ProjectId,
            request.PanelId,
            OcrContract.Stage,
            options.StageVersion,
            request.InputSha256,
            OcrContract.CoordinateSpace,
            Array.Empty<OcrRegion>(),
            Array.Empty<OcrMask>(),
            new OcrTiming(0, 0, 0, totalMilliseconds),
            0,
            OcrCollections.Freeze(warnings ?? Array.Empty<string>()),
            cache ?? new OcrCacheDiagnostics(false, string.Empty, 0, 0),
            failure,
            OcrCollections.Freeze(regionFailures ?? Array.Empty<OcrRegionFailure>()));

    private static OcrFailure Error(string code, string technicalMessage, string suggestedAction) =>
        new(code, "error", "Errors." + code, technicalMessage, true, suggestedAction);

    private static void ValidateOptions(OcrV8ProductionCompositionOptions options)
    {
        bool reviewedV8 = string.Equals(
            options.CompositionId,
            OcrV8ProductionCompositionOptions.ReviewedCompositionId,
            StringComparison.Ordinal);
        bool candidateV11 = string.Equals(
            options.CompositionId,
            OcrV8ProductionCompositionOptions.CandidateV11CompositionId,
            StringComparison.Ordinal) &&
            string.Equals(options.StageVersion, "0.0.21-v11", StringComparison.Ordinal) &&
            options.OfficialMinimumConfidence == 0;
        bool candidateV9 = string.Equals(
            options.CompositionId,
            OcrV8ProductionCompositionOptions.CandidateV9CompositionId,
            StringComparison.Ordinal) &&
            string.Equals(options.StageVersion, "0.0.21-v9-p1", StringComparison.Ordinal) &&
            options.OfficialMinimumConfidence == 0.65;
        if ((!reviewedV8 && !candidateV11 && !candidateV9) ||
            string.IsNullOrWhiteSpace(options.StageVersion) ||
            options.DetectorThreshold != 0.95 ||
            options.OfficialRescueThreshold != 0.90 ||
            options.ConsensusRescueThreshold != 0.85 ||
            options.ZeroConsensusRescueThreshold != 0.82 ||
            options.NumericMinimumConfidence != 0.65 ||
            (reviewedV8 && options.OfficialMinimumConfidence != 0) ||
            options.MaskPaddingPixels != 1)
        {
            throw new ArgumentException(
                "OCR V8 production composition options do not match the frozen public-passing contract.",
                nameof(options));
        }
    }

    private static string WarningPrefix(string compositionId) => compositionId switch
    {
        OcrV8ProductionCompositionOptions.CandidateV11CompositionId => "ocr_v11_candidate",
        OcrV8ProductionCompositionOptions.CandidateV9CompositionId => "ocr_v9_candidate",
        _ => "ocr_v8",
    };

    private IEnumerable<string> FingerprintComponents()
    {
        yield return options.CompositionId;
        yield return options.StageVersion;
        yield return options.DetectorThreshold.ToString("R", CultureInfo.InvariantCulture);
        yield return options.OfficialRescueThreshold.ToString("R", CultureInfo.InvariantCulture);
        yield return options.ConsensusRescueThreshold.ToString("R", CultureInfo.InvariantCulture);
        yield return options.ZeroConsensusRescueThreshold.ToString("R", CultureInfo.InvariantCulture);
        yield return options.NumericMinimumConfidence.ToString("R", CultureInfo.InvariantCulture);

        // Keep the reviewed V8 and existing V11 fingerprints byte-for-byte
        // stable. The extra value belongs only to the new V9 candidate policy.
        if (options.OfficialMinimumConfidence > 0)
        {
            yield return options.OfficialMinimumConfidence.ToString("R", CultureInfo.InvariantCulture);
        }

        yield return options.MaskPaddingPixels.ToString("R", CultureInfo.InvariantCulture);
        yield return detector.ConfigurationFingerprint;
        yield return officialRecognizer.ModelId;
        yield return officialRecognizer.ModelVersion;
        yield return officialRecognizer.ModelSha256;
        yield return officialRecognizer.ConfigurationFingerprint;
        yield return numericRecognizer.ModelId;
        yield return numericRecognizer.ModelVersion;
        yield return numericRecognizer.ModelSha256;
        yield return numericRecognizer.ConfigurationFingerprint;
    }

    private static string HashStrings(IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string value in values)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
