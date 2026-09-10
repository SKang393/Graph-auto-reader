// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;

namespace GraphReader.Ocr;

public sealed record OcrPipelineOptions
{
    public string StageVersion { get; init; } = "0.3.0";

    public int BatchSize { get; init; } = 16;

    public int CropWidth { get; init; } = 128;

    public int CropHeight { get; init; } = 32;

    public OcrCropWidthMode CropWidthMode { get; init; } = OcrCropWidthMode.Fixed;

    public int MaximumCropWidth { get; init; } = 4096;

    public double CropPaddingPixels { get; init; } = 1;

    public double? CropHorizontalPaddingPixels { get; init; }

    public double? CropVerticalPaddingPixels { get; init; }

    public double CropVerticalContentPaddingRatio { get; init; }

    public OcrCropResizeMode CropResizeMode { get; init; } =
        OcrCropResizeMode.PreserveAspectRatioPad;

    public float CropPaddingValue { get; init; } = 0.5f;

    public double MaskPaddingPixels { get; init; } = 1;

    public double MinimumMaskRecognitionConfidence { get; init; } = 0.55;

    public int MaximumTickCombinationEvaluations { get; init; } = 4096;

    /// <summary>
    /// Infers a vertical orientation for detector regions whose height is more
    /// than 1.4 times their width. Disable this for proposal models that emit
    /// tight horizontal graph-label boxes, where a single digit can be tall
    /// without being rotated text.
    /// </summary>
    public bool InferVerticalOrientationForTallRegions { get; init; } = true;
}

public sealed class OcrPipeline
{
    private readonly ITextRegionDetector _detector;
    private readonly ITextRecognizer _recognizer;
    private readonly IOcrResultCache _cache;
    private readonly OcrPipelineOptions _options;

    public OcrPipeline(
        ITextRegionDetector detector,
        ITextRecognizer recognizer,
        IOcrResultCache cache,
        int batchSize = 16)
        : this(detector, recognizer, cache, new OcrPipelineOptions { BatchSize = batchSize })
    {
    }

    public OcrPipeline(
        ITextRegionDetector detector,
        ITextRecognizer recognizer,
        IOcrResultCache cache,
        OcrPipelineOptions options)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.BatchSize <= 0 || _options.CropWidth <= 0 || _options.CropHeight <= 0 ||
            _options.MaximumCropWidth < _options.CropWidth ||
            !Enum.IsDefined(_options.CropWidthMode) ||
            _options.CropPaddingPixels < 0 ||
            (_options.CropHorizontalPaddingPixels.HasValue &&
             (!double.IsFinite(_options.CropHorizontalPaddingPixels.Value) ||
              _options.CropHorizontalPaddingPixels.Value < 0)) ||
            (_options.CropVerticalPaddingPixels.HasValue &&
             (!double.IsFinite(_options.CropVerticalPaddingPixels.Value) ||
              _options.CropVerticalPaddingPixels.Value < 0)) ||
            !double.IsFinite(_options.CropVerticalContentPaddingRatio) ||
            _options.CropVerticalContentPaddingRatio < 0 ||
            !Enum.IsDefined(_options.CropResizeMode) ||
            !float.IsFinite(_options.CropPaddingValue) ||
            _options.CropPaddingValue is < 0 or > 1 ||
            _options.MaskPaddingPixels < 0 ||
            _options.MinimumMaskRecognitionConfidence is < 0 or > 1 ||
            _options.MaximumTickCombinationEvaluations <= 0 ||
            string.IsNullOrWhiteSpace(_options.StageVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public async ValueTask<OcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var totalStopwatch = Stopwatch.StartNew();
        var inputFailure = ValidateRequest(request);
        if (inputFailure is not null)
        {
            return FailureResult(request, inputFailure, totalStopwatch.Elapsed.TotalMilliseconds);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var preprocessStopwatch = Stopwatch.StartNew();
        var requestCacheKey = OcrCacheKeyDeriver.CreateRequestAlias(
            request,
            _recognizer,
            _options,
            _detector.ConfigurationFingerprint);
        try
        {
            var requestCached = await _cache.TryGetAsync(requestCacheKey, cancellationToken).ConfigureAwait(false);
            if (requestCached is not null &&
                (requestCached.RegionFailures is null || requestCached.RegionFailures.Count == 0))
            {
                preprocessStopwatch.Stop();
                totalStopwatch.Stop();
                var elapsed = Math.Max(
                    totalStopwatch.Elapsed.TotalMilliseconds,
                    preprocessStopwatch.Elapsed.TotalMilliseconds);
                return SuccessResult(
                    request,
                    requestCached.Regions,
                    requestCached.Masks,
                    requestCached.Confidence,
                    requestCached.Warnings,
                    new OcrTiming(preprocessStopwatch.Elapsed.TotalMilliseconds, 0, 0, elapsed),
                    new OcrCacheDiagnostics(
                        true,
                        requestCached.ContentCacheKey ?? requestCacheKey,
                        requestCached.CropCount,
                        requestCached.BatchCount,
                        RecognitionCacheHit: true,
                        requestCached.RecognitionCacheKey),
                    requestCached.RegionFailures ?? Array.Empty<OcrRegionFailure>());
            }

            if (requestCached is not null)
            {
                warnings.Add("ocr_result_cache_ignored_failure_payload");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            warnings.Add("ocr_cache_read_failed");
        }

        IReadOnlyList<OcrDetectedRegion> detectedRegions;
        try
        {
            detectedRegions = request.DetectedRegions ?? await DetectRegionsAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateDetectedRegions(detectedRegions);
            detectedRegions = EnrichGeometry(detectedRegions, request.PlotBounds, _options);
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
                totalStopwatch.Elapsed.TotalMilliseconds);
        }

        var cropOptions = new OcrCropBatcherOptions
        {
            TargetWidth = _options.CropWidth,
            TargetHeight = _options.CropHeight,
            WidthMode = _options.CropWidthMode,
            MaximumTargetWidth = _options.MaximumCropWidth,
            BatchSize = _options.BatchSize,
            PaddingPixels = _options.CropPaddingPixels,
            HorizontalPaddingPixels = _options.CropHorizontalPaddingPixels,
            VerticalPaddingPixels = _options.CropVerticalPaddingPixels,
            VerticalContentPaddingRatio = _options.CropVerticalContentPaddingRatio,
            ResizeMode = _options.CropResizeMode,
            PaddingValue = _options.CropPaddingValue,
        };

        IReadOnlyList<IReadOnlyList<OcrCrop>> originalBatches;
        IReadOnlyList<IReadOnlyList<OcrCrop>> enhancedBatches = Array.Empty<IReadOnlyList<OcrCrop>>();
        try
        {
            originalBatches = OcrCropBatcher.CreateBatches(
                request.OriginalImage,
                detectedRegions,
                cropOptions,
                cancellationToken);
            if (request.EnhancedImage is not null)
            {
                enhancedBatches = OcrCropBatcher.CreateBatches(
                    request.EnhancedImage,
                    detectedRegions,
                    cropOptions,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return FailureResult(
                request,
                Error("OCR_CROP_PREPROCESS_FAILED", exception.Message, "retry"),
                totalStopwatch.Elapsed.TotalMilliseconds);
        }

        var batches = originalBatches.Concat(enhancedBatches).ToArray();
        var crops = batches.SelectMany(static batch => batch).ToArray();
        var cacheKey = OcrCacheKeyDeriver.Create(
            crops,
            _recognizer,
            request,
            _options,
            _detector.ConfigurationFingerprint);
        preprocessStopwatch.Stop();
        if (detectedRegions.Count == 0)
        {
            warnings.Add("no_text_regions_detected");
            return await CacheAndReturnAsync(
                request,
                requestCacheKey,
                cacheKey,
                Array.Empty<OcrRegion>(),
                Array.Empty<OcrMask>(),
                0,
                warnings,
                Array.Empty<OcrRegionFailure>(),
                crops.Length,
                batches.Length,
                preprocessStopwatch.Elapsed.TotalMilliseconds,
                0,
                0,
                false,
                OcrCacheKeyDeriver.CreateRecognition(
                    crops,
                    _recognizer,
                    request.ContractVersion,
                    request.TransformChain),
                totalStopwatch,
                cancellationToken).ConfigureAwait(false);
        }

        var recognitionCacheKey = OcrCacheKeyDeriver.CreateRecognition(
            crops,
            _recognizer,
            request.ContractVersion,
            request.TransformChain);
        var recognitionResults = new List<OcrRecognition>(crops.Length);
        var inferenceMilliseconds = 0d;
        var recognitionCacheHit = false;
        try
        {
            var cachedRecognition = await _cache
                .TryGetRecognitionAsync(recognitionCacheKey, cancellationToken)
                .ConfigureAwait(false);
            if (cachedRecognition is not null &&
                cachedRecognition.Recognitions.All(static recognition => recognition.Failure is null))
            {
                recognitionResults.AddRange(cachedRecognition.Recognitions);
                recognitionCacheHit = true;
            }

            else if (cachedRecognition is not null)
            {
                warnings.Add("ocr_recognition_cache_ignored_failure_payload");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            warnings.Add("ocr_recognition_cache_read_failed");
        }

        if (!recognitionCacheHit)
        {
            foreach (var batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var recognized = await _recognizer
                        .RecognizeBatchAsync(batch, cancellationToken)
                        .ConfigureAwait(false);
                    recognitionResults.AddRange(recognized);
                    inferenceMilliseconds += recognized.Sum(static result => result.InferenceMilliseconds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    warnings.Add("ocr_recognition_batch_failed");
                    recognitionResults.AddRange(batch.Select(crop => new OcrRecognition(
                        crop.RegionId,
                        crop.SourceImage,
                        Array.Empty<OcrRecognitionAlternative>(),
                        0,
                        Error("OCR_RECOGNITION_FAILED", exception.Message, "retry"))));
                }
            }

            if (recognitionResults.All(static recognition => recognition.Failure is null))
            {
                try
                {
                    await _cache.PutRecognitionAsync(
                        recognitionCacheKey,
                        new OcrRecognitionCachePayload(OcrCollections.Freeze(recognitionResults)),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    warnings.Add("ocr_recognition_cache_write_failed");
                }
            }
            else
            {
                warnings.Add("ocr_recognition_cache_skipped_due_to_failure");
            }
        }

        var postprocessStopwatch = Stopwatch.StartNew();
        var regionFailures = ExtractRegionFailures(recognitionResults, warnings);
        var regions = MergeResults(detectedRegions, recognitionResults, request.PlotBounds, warnings);
        regions = ResolveTickAlternatives(regions, detectedRegions, warnings, _options);
        var detectedById = detectedRegions.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        var maskRegionIds = regions
            .Where(region => IsCredibleTextMask(
                region,
                detectedById[region.RegionId],
                request.PlotBounds,
                _options))
            .Select(static region => region.RegionId)
            .ToHashSet(StringComparer.Ordinal);
        if (maskRegionIds.Count < regions.Count)
        {
            warnings.Add("ocr_mask_suppressed_low_text_evidence");
        }

        var (canonicalWidth, canonicalHeight) = CanonicalOriginalBounds(request.OriginalImage);
        var masks = OcrMaskBuilder.Build(
            detectedRegions.Where(region => maskRegionIds.Contains(region.RegionId)).ToArray(),
            canonicalWidth,
            canonicalHeight,
            _options.MaskPaddingPixels);
        var confidence = regions.Count == 0 ? 0 : regions.Average(static region => region.Confidence);
        postprocessStopwatch.Stop();

        if (regions.Count == 0 && recognitionResults.Any(static result => result.Failure is not null))
        {
            var failures = recognitionResults
                .Where(static result => result.Failure is not null)
                .Select(static result => result.Failure!)
                .ToArray();
            var failure = failures.Length > 0 &&
                failures.All(candidate =>
                    string.Equals(candidate.Code, failures[0].Code, StringComparison.Ordinal) &&
                    string.Equals(candidate.UserMessageKey, failures[0].UserMessageKey, StringComparison.Ordinal) &&
                    string.Equals(candidate.SuggestedAction, failures[0].SuggestedAction, StringComparison.Ordinal))
                ? failures[0]
                : Error(
                    "OCR_RECOGNITION_FAILED",
                    "No detected text region produced a recognition result.",
                    "retry");
            return FailureResult(
                request,
                failure,
                totalStopwatch.Elapsed.TotalMilliseconds,
                warnings,
                regionFailures,
                new OcrCacheDiagnostics(
                    false,
                    cacheKey,
                    crops.Length,
                    batches.Length,
                    recognitionCacheHit,
                    recognitionCacheKey));
        }

        return await CacheAndReturnAsync(
            request,
            requestCacheKey,
            cacheKey,
            regions,
            masks,
            confidence,
            warnings,
            regionFailures,
            crops.Length,
            batches.Length,
            preprocessStopwatch.Elapsed.TotalMilliseconds,
            inferenceMilliseconds,
            postprocessStopwatch.Elapsed.TotalMilliseconds,
            recognitionCacheHit,
            recognitionCacheKey,
            totalStopwatch,
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectRegionsAsync(
        OcrRequest request,
        CancellationToken cancellationToken)
    {
        if (_detector is IDualInputTextRegionDetector dualInputDetector &&
            request.DetectorImage is not null)
        {
            return dualInputDetector.DetectAsync(
                request.OriginalImage,
                request.DetectorImage.Image,
                cancellationToken);
        }

        return _detector.DetectAsync(
            request.DetectorImage?.Image ?? request.OriginalImage,
            cancellationToken);
    }

    private async ValueTask<OcrResult> CacheAndReturnAsync(
        OcrRequest request,
        string requestCacheKey,
        string cacheKey,
        IReadOnlyList<OcrRegion> regions,
        IReadOnlyList<OcrMask> masks,
        double confidence,
        IReadOnlyList<string> warnings,
        IReadOnlyList<OcrRegionFailure> regionFailures,
        int cropCount,
        int batchCount,
        double preprocessMilliseconds,
        double inferenceMilliseconds,
        double postprocessMilliseconds,
        bool recognitionCacheHit,
        string recognitionCacheKey,
        Stopwatch totalStopwatch,
        CancellationToken cancellationToken)
    {
        var cacheable = regionFailures.Count == 0;
        var frozenWarnings = OcrCollections.Freeze(
            (cacheable ? warnings : warnings.Concat(["ocr_result_cache_skipped_due_to_failure"]))
            .Distinct(StringComparer.Ordinal));
        var payload = new OcrCachedPayload(
            OcrCollections.Freeze(regions),
            OcrCollections.Freeze(masks),
            confidence,
            frozenWarnings,
            cropCount,
            batchCount,
            cacheKey,
            OcrCollections.Freeze(regionFailures),
            recognitionCacheKey);
        if (cacheable)
        {
            try
            {
                await _cache.PutAsync(requestCacheKey, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                frozenWarnings = OcrCollections.Freeze(frozenWarnings.Concat(["ocr_cache_write_failed"]));
            }
        }

        totalStopwatch.Stop();
        var componentTotal = preprocessMilliseconds + inferenceMilliseconds + postprocessMilliseconds;
        var reportedTotal = Math.Max(totalStopwatch.Elapsed.TotalMilliseconds, componentTotal);
        return SuccessResult(
            request,
            payload.Regions,
            payload.Masks,
            payload.Confidence,
            frozenWarnings,
            new OcrTiming(
                preprocessMilliseconds,
                inferenceMilliseconds,
                postprocessMilliseconds,
                reportedTotal),
            new OcrCacheDiagnostics(
                false,
                cacheKey,
                cropCount,
                batchCount,
                recognitionCacheHit,
                recognitionCacheKey),
            regionFailures);
    }

    private static IReadOnlyList<OcrRegion> MergeResults(
        IReadOnlyList<OcrDetectedRegion> detectedRegions,
        IReadOnlyList<OcrRecognition> recognitions,
        OcrRectangle plotBounds,
        List<string> warnings)
    {
        var byRegion = recognitions
            .GroupBy(static recognition => recognition.RegionId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var output = new List<OcrRegion>();
        foreach (var detected in detectedRegions.OrderBy(static item => item.RegionId, StringComparer.Ordinal))
        {
            if (!byRegion.TryGetValue(detected.RegionId, out var recognized))
            {
                warnings.Add("ocr_region_result_missing");
                continue;
            }

            var alternatives = recognized
                .SelectMany(static item => item.Alternatives)
                .Where(static alternative =>
                    !string.IsNullOrWhiteSpace(alternative.Text) &&
                    double.IsFinite(alternative.Confidence) &&
                    alternative.Confidence is >= 0 and <= 1)
                .GroupBy(
                    static alternative => (alternative.Text, alternative.SourceImage),
                    EqualityComparer<(string Text, OcrSourceImage SourceImage)>.Default)
                .Select(static group => group.OrderByDescending(item => item.Confidence).First())
                .OrderByDescending(static item => item.Confidence)
                .ThenBy(static item => item.Text, StringComparer.Ordinal)
                .ThenBy(static item => item.SourceImage)
                .ToArray();
            if (alternatives.Length == 0)
            {
                continue;
            }

            var scoredTexts = alternatives
                .GroupBy(static alternative => alternative.Text, StringComparer.Ordinal)
                .Select(group =>
                {
                    var best = group.Max(static alternative => alternative.Confidence);
                    var consensus = group.Select(static alternative => alternative.SourceImage).Distinct().Count() > 1;
                    return new TextScore(group.Key, Math.Clamp(best + (consensus ? 0.05 : 0), 0, 1), consensus);
                })
                .OrderByDescending(static score => score.Score)
                .ThenBy(static score => score.Text, StringComparer.Ordinal)
                .ToArray();
            var bestText = scoredTexts[0];
            var bestAlternative = alternatives
                .Where(alternative => string.Equals(alternative.Text, bestText.Text, StringComparison.Ordinal))
                .OrderByDescending(static alternative => alternative.Confidence)
                .ThenBy(static alternative => alternative.SourceImage)
                .First();
            var originalBest = alternatives
                .Where(static alternative => alternative.SourceImage == OcrSourceImage.Original)
                .OrderByDescending(static alternative => alternative.Confidence)
                .FirstOrDefault();
            var enhancedBest = alternatives
                .Where(static alternative => alternative.SourceImage == OcrSourceImage.Enhanced)
                .OrderByDescending(static alternative => alternative.Confidence)
                .FirstOrDefault();
            var disagreement = originalBest is not null && enhancedBest is not null &&
                !string.Equals(originalBest.Text, enhancedBest.Text, StringComparison.Ordinal);
            if (disagreement)
            {
                warnings.Add("original_enhanced_text_disagreement");
            }

            var role = GraphTextRoleClassifier.Classify(detected, bestText.Text, plotBounds);
            if (role.Reasons.Contains(
                    "ambiguous_text_above_plot_requires_review",
                    StringComparer.Ordinal))
            {
                warnings.Add($"ocr_role_needs_review:{detected.RegionId}:ambiguous_above_plot_text");
            }
            if (role.Reasons.Contains(
                    "ambiguous_peripheral_text_requires_review",
                    StringComparer.Ordinal))
            {
                warnings.Add($"ocr_role_needs_review:{detected.RegionId}:ambiguous_peripheral_text");
            }

            var combined = Math.Pow(
                Math.Clamp(detected.DetectionConfidence, 0, 1) *
                bestText.Score *
                role.Confidence,
                1d / 3d);
            if (disagreement)
            {
                combined *= 0.80;
            }

            output.Add(new OcrRegion(
                detected.RegionId,
                detected.Polygon,
                bestText.Text,
                OcrCollections.Freeze(alternatives),
                role.Role,
                Math.Clamp(combined, 0, 1),
                bestAlternative.SourceImage,
                OcrReviewStatus.Unreviewed));
        }

        return OcrCollections.Freeze(output);
    }

    private static IReadOnlyList<OcrDetectedRegion> EnrichGeometry(
        IReadOnlyList<OcrDetectedRegion> regions,
        OcrRectangle plotBounds,
        OcrPipelineOptions options) =>
        OcrCollections.Freeze(regions.Select(region =>
        {
            var bounds = region.Polygon.Bounds;
            var orientation = options.InferVerticalOrientationForTallRegions &&
                Math.Abs(region.OrientationDegrees) <= double.Epsilon &&
                bounds.Height > bounds.Width * 1.4
                ? -90d
                : region.OrientationDegrees;
            var context = region.Context ?? new OcrRegionContext();
            if (GraphTextRoleClassifier.GetOrientation(orientation) is
                    OcrOrientation.RotatedClockwise or OcrOrientation.RotatedCounterClockwise &&
                bounds.Center.X < plotBounds.Left)
            {
                context = context with { AxisTitleExpected = true };
            }

            return region with { OrientationDegrees = orientation, Context = context };
        }));

    private static IReadOnlyList<OcrRegionFailure> ExtractRegionFailures(
        IReadOnlyList<OcrRecognition> recognitions,
        List<string> warnings)
    {
        var failures = recognitions
            .Where(static recognition => recognition.Failure is not null)
            .Select(static recognition => new OcrRegionFailure(
                recognition.RegionId,
                recognition.SourceImage,
                recognition.Failure!))
            .GroupBy(
                static failure => (failure.RegionId, failure.SourceImage, failure.Failure.Code),
                EqualityComparer<(string RegionId, OcrSourceImage SourceImage, string Code)>.Default)
            .Select(static group => group.First())
            .OrderBy(static failure => failure.RegionId, StringComparer.Ordinal)
            .ThenBy(static failure => failure.SourceImage)
            .ThenBy(static failure => failure.Failure.Code, StringComparer.Ordinal)
            .ToArray();
        foreach (var failure in failures)
        {
            warnings.Add($"ocr_region_failure:{failure.RegionId}:{failure.SourceImage}:{failure.Failure.Code}");
        }

        return OcrCollections.Freeze(failures);
    }

    private static bool IsCredibleTextMask(
        OcrRegion region,
        OcrDetectedRegion detected,
        OcrRectangle plotBounds,
        OcrPipelineOptions options)
    {
        var recognitionConfidence = region.Alternatives.Count == 0
            ? 0
            : region.Alternatives.Max(static alternative => alternative.Confidence);
        if (recognitionConfidence < options.MinimumMaskRecognitionConfidence ||
            detected.DetectionConfidence < 0.45)
        {
            return false;
        }

        var likelyGraphStructure = detected.Evidence?.LikelyGraphStructure == true;
        var componentCount = detected.Evidence?.ComponentCount ?? 0;
        var center = detected.Polygon.Bounds.Center;
        var centerInsidePlot = center.X >= plotBounds.Left && center.X <= plotBounds.Right &&
            center.Y >= plotBounds.Top && center.Y <= plotBounds.Bottom;
        if (likelyGraphStructure && componentCount > 1)
        {
            if (centerInsidePlot)
            {
                return false;
            }

            return region.Text.Trim().Length >= 2;
        }

        if (region.Role is OcrTextRole.XTick or OcrTextRole.YTick)
        {
            return GraphNumericParser.Parse(region.Text).IsSuccess;
        }

        if (likelyGraphStructure)
        {
            return false;
        }

        if (region.Role != OcrTextRole.Other)
        {
            return true;
        }

        return detected.Evidence is null ||
            (detected.Evidence.ComponentCount >= 2 &&
             detected.Evidence.TextLikelihood >= 0.60 &&
             region.Text.Trim().Length >= 2);
    }

    private static (int? Width, int? Height) CanonicalOriginalBounds(OcrImage image)
    {
        if (image.CanonicalOriginalWidth.HasValue && image.CanonicalOriginalHeight.HasValue)
        {
            return (image.CanonicalOriginalWidth, image.CanonicalOriginalHeight);
        }

        var transform = image.OriginalToImage;
        var identity = Math.Abs(transform.ScaleX - 1) <= double.Epsilon &&
            Math.Abs(transform.ScaleY - 1) <= double.Epsilon &&
            Math.Abs(transform.OffsetX) <= double.Epsilon &&
            Math.Abs(transform.OffsetY) <= double.Epsilon;
        return identity ? (image.Width, image.Height) : (null, null);
    }

    private static IReadOnlyList<OcrRegion> ResolveTickAlternatives(
        IReadOnlyList<OcrRegion> regions,
        IReadOnlyList<OcrDetectedRegion> detectedRegions,
        List<string> warnings,
        OcrPipelineOptions options)
    {
        var output = regions.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        var detectedById = detectedRegions.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        ResolveAxis(OcrTextRole.XTick, TickAxisDirection.IncreasingWithPixels, static bounds => bounds.Center.X);
        ResolveAxis(OcrTextRole.YTick, TickAxisDirection.DecreasingWithPixels, static bounds => bounds.Center.Y);
        return OcrCollections.Freeze(regions.Select(region => output[region.RegionId]));

        void ResolveAxis(
            OcrTextRole role,
            TickAxisDirection direction,
            Func<OcrRectangle, double> pixelSelector)
        {
            var tickRegions = regions
                .Where(region => region.Role == role)
                .OrderBy(region => pixelSelector(region.Polygon.Bounds))
                .ToArray();
            if (tickRegions.Length == 0)
            {
                return;
            }

            var choices = tickRegions.Select(region => NumericChoices(region)).ToArray();
            var hasConflict = choices.Any(static candidates =>
                candidates.Select(static candidate => candidate.Value).Distinct().Skip(1).Any());
            if (!hasConflict)
            {
                return;
            }

            if (tickRegions.Length < 2 || choices.Any(static candidates => candidates.Count == 0))
            {
                warnings.Add($"ocr_tick_sequence_needs_review:{role}:insufficient_numeric_evidence");
                return;
            }

            if (CombinationCountExceedsLimit(choices, options.MaximumTickCombinationEvaluations))
            {
                warnings.Add($"ocr_tick_sequence_needs_review:{role}:combination_search_incomplete");
                return;
            }

            var combinations = new List<TickCombination>();
            var selection = new NumericChoice[tickRegions.Length];
            Enumerate(0);
            var ranked = combinations
                .OrderByDescending(static combination => combination.Score)
                .ThenBy(static combination => combination.Key, StringComparer.Ordinal)
                .ToArray();
            if (ranked.Length == 0)
            {
                warnings.Add($"ocr_tick_sequence_needs_review:{role}:no_valid_combination");
                return;
            }

            var best = ranked[0];
            var ambiguous = ranked.Length > 1 &&
                Math.Abs(best.Score - ranked[1].Score) <= 0.02 &&
                !string.Equals(best.Key, ranked[1].Key, StringComparison.Ordinal);
            if (ambiguous || best.Resolution.NeedsReview)
            {
                warnings.Add($"ocr_tick_sequence_needs_review:{role}:ambiguous_or_irregular_spacing");
                return;
            }

            for (var index = 0; index < tickRegions.Length; index++)
            {
                var region = tickRegions[index];
                var chosen = best.Choices[index];
                var selectedAlternative = region.Alternatives
                    .Where(alternative =>
                        string.Equals(alternative.Text, chosen.Text, StringComparison.Ordinal) &&
                        alternative.SourceImage == chosen.SourceImage)
                    .OrderByDescending(static alternative => alternative.Confidence)
                    .First();
                output[region.RegionId] = region with
                {
                    Text = chosen.Text,
                    SourceImage = selectedAlternative.SourceImage,
                    Confidence = Math.Clamp((region.Confidence + chosen.Confidence + best.Resolution.Confidence) / 3d, 0, 1),
                };
            }

            warnings.Add($"ocr_tick_alternative_resolved_by_monotonic_spacing:{role}");

            void Enumerate(int index)
            {
                if (combinations.Count >= options.MaximumTickCombinationEvaluations)
                {
                    return;
                }

                if (index == choices.Length)
                {
                    var selected = (NumericChoice[])selection.Clone();
                    var candidates = selected.Select((choice, candidateIndex) => new TickCandidate(
                        tickRegions[candidateIndex].RegionId,
                        pixelSelector(detectedById[tickRegions[candidateIndex].RegionId].Polygon.Bounds),
                        choice.Value,
                        choice.Confidence)).ToArray();
                    var resolution = MonotonicTickResolver.Resolve(candidates, direction);
                    var rejectedPenalty = resolution.RejectedTicks.Count / (double)tickRegions.Length;
                    var score = resolution.Confidence + selected.Average(static choice => choice.Confidence) -
                        rejectedPenalty - (resolution.NeedsReview ? 0.5 : 0);
                    combinations.Add(new TickCombination(
                        selected,
                        resolution,
                        score,
                        string.Join('|', selected.Select(static choice =>
                            FormattableString.Invariant($"{choice.Value:R}:{choice.Text}:{choice.SourceImage}")))));
                    return;
                }

                foreach (var choice in choices[index])
                {
                    selection[index] = choice;
                    Enumerate(index + 1);
                }
            }
        }

        static IReadOnlyList<NumericChoice> NumericChoices(OcrRegion region) =>
            OcrCollections.Freeze(region.Alternatives
                .Select(alternative => (Alternative: alternative, Parsed: GraphNumericParser.Parse(alternative.Text)))
                .Where(static item => item.Parsed.IsSuccess && item.Parsed.Value.HasValue)
                .Select(static item => new NumericChoice(
                    item.Alternative.Text,
                    item.Parsed.Value!.Value,
                    item.Alternative.Confidence * item.Parsed.Confidence,
                    item.Alternative.SourceImage))
                .GroupBy(static choice => choice.Value)
                .Select(static group => group
                    .OrderByDescending(static choice => choice.Confidence)
                    .ThenBy(static choice => choice.Text, StringComparer.Ordinal)
                    .ThenBy(static choice => choice.SourceImage)
                    .First())
                .OrderByDescending(static choice => choice.Confidence)
                .ThenBy(static choice => choice.Value)
                .ThenBy(static choice => choice.Text, StringComparer.Ordinal)
                .ThenBy(static choice => choice.SourceImage));

        static bool CombinationCountExceedsLimit(
            IReadOnlyList<IReadOnlyList<NumericChoice>> choices,
            int maximumEvaluations)
        {
            var count = 1L;
            foreach (var candidates in choices)
            {
                if (candidates.Count == 0 || count > maximumEvaluations / (long)candidates.Count)
                {
                    return true;
                }

                count *= candidates.Count;
            }

            return count > maximumEvaluations;
        }
    }

    private OcrResult SuccessResult(
        OcrRequest request,
        IReadOnlyList<OcrRegion> regions,
        IReadOnlyList<OcrMask> masks,
        double confidence,
        IEnumerable<string> warnings,
        OcrTiming timing,
        OcrCacheDiagnostics cache,
        IReadOnlyList<OcrRegionFailure>? regionFailures = null) =>
        new(
            request.ContractVersion,
            Guid.NewGuid().ToString(),
            request.ProjectId,
            request.PanelId,
            OcrContract.Stage,
            _options.StageVersion,
            request.InputSha256,
            OcrContract.CoordinateSpace,
            regions,
            masks,
            timing,
            Math.Clamp(confidence, 0, 1),
            OcrCollections.Freeze(warnings.Distinct(StringComparer.Ordinal)),
            cache,
            null,
            OcrCollections.Freeze(regionFailures ?? Array.Empty<OcrRegionFailure>()));

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
            _options.StageVersion,
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

    internal static OcrFailure? ValidateRequest(OcrRequest request)
    {
        if (request.ContractVersion != OcrContract.Version)
        {
            return Error("OCR_CONTRACT_UNSUPPORTED", "Only OCR contract version 1 is supported.", "upgrade_project");
        }

        if (string.IsNullOrWhiteSpace(request.ProjectId) || string.IsNullOrWhiteSpace(request.PanelId) ||
            request.InputSha256.Length != 64 || !request.InputSha256.All(Uri.IsHexDigit) ||
            !request.PlotBounds.IsValid)
        {
            return Error("OCR_INPUT_INVALID", "OCR request identifiers, SHA-256, or plot bounds are invalid.", "retry");
        }

        if (request.OriginalImage.SourceImage != OcrSourceImage.Original)
        {
            return Error("OCR_INPUT_INVALID", "OriginalImage must identify the immutable original source.", "retry");
        }

        if (request.EnhancedImage is { SourceImage: not OcrSourceImage.Enhanced })
        {
            return Error("OCR_INPUT_INVALID", "EnhancedImage must identify an enhanced derivative.", "retry");
        }

        if (!HasValidCanonicalDimensions(request.OriginalImage) ||
            (request.EnhancedImage is not null && !HasValidCanonicalDimensions(request.EnhancedImage)) ||
            (request.DetectorImage is not null && !HasValidCanonicalDimensions(request.DetectorImage.Image)))
        {
            return Error(
                "OCR_INPUT_INVALID",
                "Canonical original dimensions must be supplied together and must be positive.",
                "retry");
        }

        if (request.DetectorImage is not null && !ValidDetectorImage(request))
        {
            return Error(
                "OCR_INPUT_INVALID",
                "DetectorImage must be a checksum-matched, same-size original-pixel derivative with the original transform.",
                "retry");
        }

        return null;
    }

    private static bool ValidDetectorImage(OcrRequest request)
    {
        OcrDetectorImage detector = request.DetectorImage!;
        OcrImage image = detector.Image;
        OcrImage original = request.OriginalImage;
        if (string.IsNullOrWhiteSpace(detector.PixelSha256) ||
            detector.PixelSha256.Length != 64 || !detector.PixelSha256.All(Uri.IsHexDigit) ||
            image.SourceImage != OcrSourceImage.Original ||
            image.Width != original.Width || image.Height != original.Height ||
            image.Stride != original.Stride || image.Stride < image.Width ||
            image.Pixels.Length != checked(image.Stride * image.Height) ||
            image.OriginalToImage != original.OriginalToImage ||
            !string.Equals(image.CoordinateSpace, original.CoordinateSpace, StringComparison.Ordinal) ||
            image.CanonicalOriginalWidth != original.CanonicalOriginalWidth ||
            image.CanonicalOriginalHeight != original.CanonicalOriginalHeight ||
            (image.BgrPixels is null) != (original.BgrPixels is null))
        {
            return false;
        }

        string actual = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(image.Pixels.Span));
        if (!string.Equals(actual, detector.PixelSha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (image.BgrPixels is null)
        {
            return detector.BgrPixelSha256 is null;
        }

        OcrBgrBytePixels bgr = image.BgrPixels;
        if (bgr.Stride < checked(image.Width * 3) ||
            bgr.Pixels.Length != checked(bgr.Stride * image.Height) ||
            string.IsNullOrWhiteSpace(detector.BgrPixelSha256) ||
            detector.BgrPixelSha256.Length != 64 ||
            !detector.BgrPixelSha256.All(Uri.IsHexDigit))
        {
            return false;
        }

        string actualBgr = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(bgr.Pixels.Span));
        return string.Equals(
            actualBgr,
            detector.BgrPixelSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidateDetectedRegions(IReadOnlyList<OcrDetectedRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Any(static region =>
                string.IsNullOrWhiteSpace(region.RegionId) ||
                region.CoordinateSpace != OcrContract.CoordinateSpace ||
                !double.IsFinite(region.DetectionConfidence) ||
                region.DetectionConfidence is < 0 or > 1))
        {
            throw new ArgumentException("Detected OCR regions contain invalid values.", nameof(regions));
        }

        if (regions.Select(static region => region.RegionId).Distinct(StringComparer.Ordinal).Count() != regions.Count)
        {
            throw new ArgumentException("Detected OCR region IDs must be unique.", nameof(regions));
        }
    }

    private static OcrFailure Error(string code, string technicalMessage, string suggestedAction) =>
        new(code, "error", "Errors." + code, technicalMessage, true, suggestedAction);

    private static bool HasValidCanonicalDimensions(OcrImage image) =>
        image.CanonicalOriginalWidth.HasValue == image.CanonicalOriginalHeight.HasValue &&
        image.CanonicalOriginalWidth is not <= 0 && image.CanonicalOriginalHeight is not <= 0;

    private sealed record TextScore(string Text, double Score, bool Consensus);

    private sealed record NumericChoice(
        string Text,
        double Value,
        double Confidence,
        OcrSourceImage SourceImage);

    private sealed record TickCombination(
        NumericChoice[] Choices,
        TickResolutionResult Resolution,
        double Score,
        string Key);
}
