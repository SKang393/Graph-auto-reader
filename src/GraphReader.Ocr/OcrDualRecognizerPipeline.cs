// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

public sealed record OcrDualRecognizerPipelineOptions
{
    public const string ReviewedCompositionId =
        "graphreader-v8-official-english-v5-numeric-composition-v1";

    public string CompositionId { get; init; } = ReviewedCompositionId;

    public string StageVersion { get; init; } = "0.1.0-dev";

    public double NumericSpecialistMinimumConfidence { get; init; } = 0.65d;
}

/// <summary>
/// Runs one checksum-bound detector and two independently preprocessed text
/// recognizers. The general recognizer owns words and unconstrained text. The
/// numeric specialist may replace it only where geometry or an explicit role
/// says a graph number is expected. This prevents numeric normalization from
/// turning participant, phase, legend, or annotation text into fake values.
/// </summary>
public sealed class OcrDualRecognizerPipeline
{
    private const string CompositionWarning = "ocr_dual_recognizer_composition_v1";
    private readonly ITextRegionDetector detector;
    private readonly OcrPipeline generalPipeline;
    private readonly OcrPipeline numericPipeline;
    private readonly ITextRecognizer generalRecognizer;
    private readonly ITextRecognizer numericRecognizer;
    private readonly OcrDualRecognizerPipelineOptions options;

    public OcrDualRecognizerPipeline(
        ITextRegionDetector detector,
        ITextRecognizer generalRecognizer,
        IOcrResultCache generalCache,
        OcrPipelineOptions generalPipelineOptions,
        ITextRecognizer numericRecognizer,
        IOcrResultCache numericCache,
        OcrPipelineOptions numericPipelineOptions,
        OcrDualRecognizerPipelineOptions? options = null)
    {
        this.detector = detector ?? throw new ArgumentNullException(nameof(detector));
        this.generalRecognizer = generalRecognizer ??
            throw new ArgumentNullException(nameof(generalRecognizer));
        this.numericRecognizer = numericRecognizer ??
            throw new ArgumentNullException(nameof(numericRecognizer));
        ArgumentNullException.ThrowIfNull(generalCache);
        ArgumentNullException.ThrowIfNull(generalPipelineOptions);
        ArgumentNullException.ThrowIfNull(numericCache);
        ArgumentNullException.ThrowIfNull(numericPipelineOptions);
        this.options = options ?? new OcrDualRecognizerPipelineOptions();
        ValidateOptions(this.options);

        generalPipeline = new OcrPipeline(
            detector,
            generalRecognizer,
            generalCache,
            generalPipelineOptions);
        numericPipeline = new OcrPipeline(
            detector,
            numericRecognizer,
            numericCache,
            numericPipelineOptions);
    }

    public string CompositionId => options.CompositionId;

    public string ConfigurationFingerprint => HashStrings(
    [
        options.CompositionId,
        options.StageVersion,
        options.NumericSpecialistMinimumConfidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        detector.ConfigurationFingerprint,
        generalRecognizer.ModelId,
        generalRecognizer.ModelVersion,
        generalRecognizer.ModelSha256,
        generalRecognizer.ConfigurationFingerprint,
        numericRecognizer.ModelId,
        numericRecognizer.ModelVersion,
        numericRecognizer.ModelSha256,
        numericRecognizer.ConfigurationFingerprint,
    ]);

    public ValueTask<OcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default) =>
        RecognizeCoreAsync(request, cancellationToken);

    private async ValueTask<OcrResult> RecognizeCoreAsync(
        OcrRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var total = Stopwatch.StartNew();
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
        IReadOnlyList<OcrDetectedRegion> detectedRegions;
        OcrRequestImageSnapshot recognitionSnapshot;
        var detection = Stopwatch.StartNew();
        try
        {
            detectedRegions = request.DetectedRegions ??
                await detector.DetectAsync(
                        request.DetectorImage?.Image ?? request.OriginalImage,
                        cancellationToken)
                    .ConfigureAwait(false);
            OcrPipeline.ValidateDetectedRegions(detectedRegions);
            recognitionSnapshot = snapshot.WithDetectedRegions(detectedRegions, cancellationToken);
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

        OcrResult general = await generalPipeline
            .RecognizeSnapshotAsync(recognitionSnapshot, cancellationToken)
            .ConfigureAwait(false);
        OcrResult numeric = await numericPipeline
            .RecognizeSnapshotAsync(recognitionSnapshot, cancellationToken)
            .ConfigureAwait(false);

        var warnings = new List<string> { CompositionWarning };
        warnings.AddRange(general.Warnings);
        warnings.AddRange(numeric.Warnings);
        if (general.Failure is not null)
        {
            warnings.Add($"ocr_general_pipeline_failed:{general.Failure.Code}");
        }

        if (numeric.Failure is not null)
        {
            warnings.Add($"ocr_numeric_pipeline_failed:{numeric.Failure.Code}");
        }

        var generalById = general.Regions.ToDictionary(static item => item.RegionId, StringComparer.Ordinal);
        var numericById = numeric.Regions.ToDictionary(static item => item.RegionId, StringComparer.Ordinal);
        var selected = new List<OcrRegion>();
        var numericRegionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (OcrDetectedRegion detected in detectedRegions.OrderBy(static item => item.RegionId, StringComparer.Ordinal))
        {
            generalById.TryGetValue(detected.RegionId, out OcrRegion? generalRegion);
            numericById.TryGetValue(detected.RegionId, out OcrRegion? numericRegion);
            if (ShouldSelectNumeric(detected, generalRegion, numericRegion, options))
            {
                numericRegionIds.Add(detected.RegionId);
                selected.Add(MergeAlternatives(numericRegion!, generalRegion));
                warnings.Add($"ocr_numeric_specialist_selected:{detected.RegionId}");
            }
            else if (generalRegion is not null)
            {
                selected.Add(MergeAlternatives(generalRegion, numericRegion));
            }
            else if (numericRegion is not null)
            {
                warnings.Add($"ocr_numeric_specialist_suppressed_without_numeric_context:{detected.RegionId}");
            }
        }

        OcrMask[] masks = selected
            .Select(region => SelectMask(
                region.RegionId,
                numericRegionIds.Contains(region.RegionId) ? numeric.Masks : general.Masks))
            .Where(static mask => mask is not null)
            .Cast<OcrMask>()
            .ToArray();
        OcrRegionFailure[] regionFailures = general.RegionFailures
            .OrEmpty()
            .Concat(numeric.RegionFailures.OrEmpty())
            .Distinct()
            .OrderBy(static failure => failure.RegionId, StringComparer.Ordinal)
            .ThenBy(static failure => failure.SourceImage)
            .ThenBy(static failure => failure.Failure.Code, StringComparer.Ordinal)
            .ToArray();

        if (detectedRegions.Count > 0 && selected.Count == 0 &&
            (general.Failure is not null || numeric.Failure is not null))
        {
            OcrFailure failure = general.Failure is not null && numeric.Failure is not null
                ? Error(
                    "OCR_RECOGNITION_ENSEMBLE_FAILED",
                    $"General recognizer: {general.Failure.TechnicalMessage} Numeric recognizer: {numeric.Failure.TechnicalMessage}",
                    "repair_ocr_recognizer_pair")
                : general.Failure ?? numeric.Failure!;
            return FailureResult(
                request,
                failure,
                total.Elapsed.TotalMilliseconds,
                warnings,
                regionFailures,
                CombinedCache(general.Cache, numeric.Cache));
        }

        total.Stop();
        double preprocess = detection.Elapsed.TotalMilliseconds +
            general.Timing.PreprocessMilliseconds + numeric.Timing.PreprocessMilliseconds;
        double inference = general.Timing.InferenceMilliseconds + numeric.Timing.InferenceMilliseconds;
        double postprocess = general.Timing.PostprocessMilliseconds + numeric.Timing.PostprocessMilliseconds;
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
            OcrCollections.Freeze(masks),
            new OcrTiming(
                preprocess,
                inference,
                postprocess,
                Math.Max(total.Elapsed.TotalMilliseconds, componentTotal)),
            selected.Count == 0 ? 0 : selected.Average(static region => region.Confidence),
            OcrCollections.Freeze(warnings.Distinct(StringComparer.Ordinal)),
            CombinedCache(general.Cache, numeric.Cache),
            null,
            OcrCollections.Freeze(regionFailures));
    }

    private static bool ShouldSelectNumeric(
        OcrDetectedRegion detected,
        OcrRegion? general,
        OcrRegion? numeric,
        OcrDualRecognizerPipelineOptions options)
    {
        if (numeric is null || !GraphNumericParser.Parse(numeric.Text).IsSuccess)
        {
            return false;
        }

        double confidence = numeric.Alternatives
            .Where(alternative => string.Equals(alternative.Text, numeric.Text, StringComparison.Ordinal))
            .Select(static alternative => alternative.Confidence)
            .DefaultIfEmpty(0)
            .Max();
        if (confidence < options.NumericSpecialistMinimumConfidence)
        {
            return false;
        }

        OcrRegionContext? context = detected.Context;
        if (context?.ExplicitRoleHint is { } explicitRole &&
            explicitRole is not OcrTextRole.XTick and not OcrTextRole.YTick)
        {
            return false;
        }

        return context?.NumericExpected == true ||
            context?.ExplicitRoleHint is OcrTextRole.XTick or OcrTextRole.YTick ||
            numeric.Role is OcrTextRole.XTick or OcrTextRole.YTick ||
            general?.Role is OcrTextRole.XTick or OcrTextRole.YTick;
    }

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

    private static OcrMask? SelectMask(string regionId, IReadOnlyList<OcrMask> masks) =>
        masks
            .Where(mask => string.Equals(mask.RegionId, regionId, StringComparison.Ordinal))
            .OrderByDescending(static mask => mask.Confidence)
            .FirstOrDefault();

    private static OcrCacheDiagnostics CombinedCache(
        OcrCacheDiagnostics general,
        OcrCacheDiagnostics numeric) =>
        new(
            general.CacheHit && numeric.CacheHit,
            HashStrings([general.CacheKey, numeric.CacheKey]),
            checked(general.CropCount + numeric.CropCount),
            checked(general.BatchCount + numeric.BatchCount),
            general.RecognitionCacheHit && numeric.RecognitionCacheHit,
            HashStrings([general.RecognitionCacheKey ?? string.Empty, numeric.RecognitionCacheKey ?? string.Empty]));

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

    private static void ValidateOptions(OcrDualRecognizerPipelineOptions options)
    {
        if (!string.Equals(
                options.CompositionId,
                OcrDualRecognizerPipelineOptions.ReviewedCompositionId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(options.StageVersion) ||
            !double.IsFinite(options.NumericSpecialistMinimumConfidence) ||
            options.NumericSpecialistMinimumConfidence is < 0 or > 1)
        {
            throw new ArgumentException("OCR dual-recognizer composition options are invalid.", nameof(options));
        }
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

internal static class OcrNullableCollections
{
    public static IReadOnlyList<T> OrEmpty<T>(this IReadOnlyList<T>? values) =>
        values ?? Array.Empty<T>();
}
