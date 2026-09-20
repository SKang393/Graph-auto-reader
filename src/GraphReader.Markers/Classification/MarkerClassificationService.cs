// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using GraphReader.Inference;

namespace GraphReader.Markers.Classification;

/// <summary>
/// Classifies marker patches without owning the injected inference runtime or model files.
/// </summary>
public sealed class MarkerClassificationService : IMarkerClassificationService
{
    private readonly IMarkerPatchExtractor _patchExtractor;
    private readonly IMarkerClassificationInferenceRunner _inference;

    public MarkerClassificationService(InferenceRuntime runtime)
        : this(new MarkerPatchExtractor(), new InferenceRuntimeMarkerClassificationRunner(runtime))
    {
    }

    public MarkerClassificationService(IMarkerClassificationInferenceRunner inference)
        : this(new MarkerPatchExtractor(), inference)
    {
    }

    public MarkerClassificationService(
        IMarkerPatchExtractor patchExtractor,
        IMarkerClassificationInferenceRunner inference)
    {
        _patchExtractor = patchExtractor ?? throw new ArgumentNullException(nameof(patchExtractor));
        _inference = inference ?? throw new ArgumentNullException(nameof(inference));
    }

    public async ValueTask<MarkerClassificationResult> ClassifyAsync(
        MarkerClassificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var totalStopwatch = Stopwatch.StartNew();
        var runId = Guid.NewGuid().ToString();
        var validationFailure = Validate(request);
        if (validationFailure is not null)
        {
            totalStopwatch.Stop();
            return FailureResult(request, runId, validationFailure, totalStopwatch.Elapsed.TotalMilliseconds);
        }

        IReadOnlyList<MarkerPatch> patches;
        var extractionStopwatch = Stopwatch.StartNew();
        try
        {
            patches = _patchExtractor.Extract(
                request.Image,
                request.Markers,
                CreatePatchOptions(request.Options),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or ArithmeticException or InvalidOperationException)
        {
            extractionStopwatch.Stop();
            totalStopwatch.Stop();
            return FailureResult(
                request,
                runId,
                Error(
                    "MARKER_CLASSIFICATION_INVALID_PATCH",
                    exception.Message,
                    recoverable: true,
                    "review_marker_geometry"),
                totalStopwatch.Elapsed.TotalMilliseconds,
                extractionMilliseconds: extractionStopwatch.Elapsed.TotalMilliseconds);
        }

        extractionStopwatch.Stop();
        if (patches.Count != request.Markers.Count)
        {
            totalStopwatch.Stop();
            return FailureResult(
                request,
                runId,
                Error(
                    "MARKER_CLASSIFICATION_INVALID_PATCH",
                    $"Patch extractor returned {patches.Count} patches for {request.Markers.Count} markers.",
                    recoverable: false,
                    "report_diagnostic"),
                totalStopwatch.Elapsed.TotalMilliseconds,
                extractionMilliseconds: extractionStopwatch.Elapsed.TotalMilliseconds);
        }

        if (patches.Count == 0)
        {
            totalStopwatch.Stop();
            return SuccessResult(
                request,
                runId,
                Array.Empty<ClassifiedMarker>(),
                Array.Empty<MarkerClassificationBatchReport>(),
                Array.Empty<string>(),
                extractionStopwatch.Elapsed.TotalMilliseconds,
                0,
                0,
                totalStopwatch.Elapsed.TotalMilliseconds);
        }

        var classifications = new List<ClassifiedMarker>(patches.Count);
        var reports = new List<MarkerClassificationBatchReport>();
        var warnings = new List<string>();
        var inferenceMilliseconds = 0d;
        var postprocessMilliseconds = 0d;
        for (var batchStart = 0; batchStart < patches.Count; batchStart += request.Options.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchIndex = batchStart / request.Options.BatchSize;
            var batchCount = Math.Min(request.Options.BatchSize, patches.Count - batchStart);
            var batchStopwatch = Stopwatch.StartNew();
            InferenceResponse response;
            try
            {
                var inferenceRequest = CreateInferenceRequest(request, patches, batchStart, batchCount);
                response = await _inference.RunAsync(inferenceRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                batchStopwatch.Stop();
                var failure = Error(
                    "MARKER_CLASSIFICATION_INFERENCE_FAILED",
                    exception.Message,
                    recoverable: true,
                    "retry");
                reports.Add(new MarkerClassificationBatchReport(
                    batchIndex,
                    batchCount,
                    null,
                    Array.Empty<ProviderAttempt>(),
                    new MarkerClassificationTiming(0, 0, 0, batchStopwatch.Elapsed.TotalMilliseconds),
                    cacheHit: false,
                    failure));
                totalStopwatch.Stop();
                return PartialFailureResult(
                    request,
                    runId,
                    classifications,
                    reports,
                    warnings,
                    failure,
                    extractionStopwatch.Elapsed.TotalMilliseconds,
                    inferenceMilliseconds,
                    postprocessMilliseconds,
                    totalStopwatch.Elapsed.TotalMilliseconds);
            }

            if (!response.Succeeded || response.Execution is null)
            {
                batchStopwatch.Stop();
                var failure = FromInferenceError(response.Error);
                reports.Add(new MarkerClassificationBatchReport(
                    batchIndex,
                    batchCount,
                    null,
                    response.ProviderAttempts,
                    new MarkerClassificationTiming(0, 0, 0, batchStopwatch.Elapsed.TotalMilliseconds),
                    cacheHit: false,
                    failure));
                totalStopwatch.Stop();
                return PartialFailureResult(
                    request,
                    runId,
                    classifications,
                    reports,
                    warnings,
                    failure,
                    extractionStopwatch.Elapsed.TotalMilliseconds,
                    inferenceMilliseconds,
                    postprocessMilliseconds,
                    totalStopwatch.Elapsed.TotalMilliseconds);
            }

            inferenceMilliseconds += response.Execution.Timing.InferenceMilliseconds;
            var postprocessStopwatch = Stopwatch.StartNew();
            IReadOnlyList<ClassifiedMarker> decoded;
            try
            {
                decoded = DecodeBatch(
                    response.Execution.Output,
                    patches,
                    batchStart,
                    batchCount,
                    request.Options.TensorContract,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException or ArithmeticException or InvalidOperationException)
            {
                postprocessStopwatch.Stop();
                postprocessMilliseconds += postprocessStopwatch.Elapsed.TotalMilliseconds;
                batchStopwatch.Stop();
                var failure = Error(
                    "MARKER_CLASSIFICATION_INVALID_MODEL_OUTPUT",
                    exception.Message,
                    recoverable: false,
                    "verify_model_manifest");
                reports.Add(new MarkerClassificationBatchReport(
                    batchIndex,
                    batchCount,
                    response.Execution.Provider,
                    response.ProviderAttempts,
                    new MarkerClassificationTiming(
                        0,
                        response.Execution.Timing.InferenceMilliseconds,
                        postprocessStopwatch.Elapsed.TotalMilliseconds,
                        batchStopwatch.Elapsed.TotalMilliseconds),
                    response.Execution.Timing.CacheHit,
                    failure));
                totalStopwatch.Stop();
                return PartialFailureResult(
                    request,
                    runId,
                    classifications,
                    reports,
                    warnings,
                    failure,
                    extractionStopwatch.Elapsed.TotalMilliseconds,
                    inferenceMilliseconds,
                    postprocessMilliseconds,
                    totalStopwatch.Elapsed.TotalMilliseconds);
            }

            postprocessStopwatch.Stop();
            postprocessMilliseconds += postprocessStopwatch.Elapsed.TotalMilliseconds;
            classifications.AddRange(decoded);
            batchStopwatch.Stop();
            reports.Add(new MarkerClassificationBatchReport(
                batchIndex,
                batchCount,
                response.Execution.Provider,
                response.ProviderAttempts,
                new MarkerClassificationTiming(
                    0,
                    response.Execution.Timing.InferenceMilliseconds,
                    postprocessStopwatch.Elapsed.TotalMilliseconds,
                    batchStopwatch.Elapsed.TotalMilliseconds),
                response.Execution.Timing.CacheHit,
                null));
        }

        totalStopwatch.Stop();
        return SuccessResult(
            request,
            runId,
            classifications,
            reports,
            warnings,
            extractionStopwatch.Elapsed.TotalMilliseconds,
            inferenceMilliseconds,
            postprocessMilliseconds,
            totalStopwatch.Elapsed.TotalMilliseconds);
    }

    private static MarkerPatchExtractionOptions CreatePatchOptions(MarkerClassificationOptions options) =>
        new(
            options.TensorContract.PatchWidth,
            options.TensorContract.PatchHeight,
            options.TensorContract.InputChannelCount)
        {
            RadiusScale = options.PatchRadiusScale,
            MinimumHalfExtentFramePixels = options.MinimumPatchHalfExtentFramePixels,
            PaddingValue = options.PatchPaddingValue,
            OriginalPixelContentBounds = options.OriginalPixelContentBounds,
        };

    private static InferenceRequest CreateInferenceRequest(
        MarkerClassificationRequest request,
        IReadOnlyList<MarkerPatch> patches,
        int batchStart,
        int batchCount)
    {
        var contract = request.Options.TensorContract;
        var patchLength = checked(contract.PatchWidth * contract.PatchHeight * contract.InputChannelCount);
        var values = new float[checked(batchCount * patchLength)];
        for (var batchOffset = 0; batchOffset < batchCount; batchOffset++)
        {
            var source = patches[batchStart + batchOffset].ChannelsFirstPixels.Span;
            if (source.Length != patchLength)
            {
                throw new ArgumentException(
                    $"Classifier patch {batchStart + batchOffset} has {source.Length} values; expected {patchLength}.",
                    nameof(patches));
            }

            var destination = values.AsSpan(batchOffset * patchLength, patchLength);
            for (var valueIndex = 0; valueIndex < source.Length; valueIndex++)
            {
                destination[valueIndex] =
                    (source[valueIndex] - contract.NormalizeMean) * contract.NormalizeScale;
            }
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["batch_count"] = batchCount,
            ["batch_index"] = batchStart / request.Options.BatchSize,
            ["embedding_length"] = contract.EmbeddingLength,
            ["fill_order"] = MarkerClassificationContract.FillOutputOrder,
            ["patch_height"] = contract.PatchHeight,
            ["patch_radius_scale"] = request.Options.PatchRadiusScale,
            ["patch_width"] = contract.PatchWidth,
            ["output_encoding"] = contract.OutputEncoding.ToString(),
            ["shape_order"] = MarkerClassificationContract.ShapeOutputOrder,
        };
        var markerMaterial = new object?[batchCount];
        for (var batchOffset = 0; batchOffset < batchCount; batchOffset++)
        {
            var marker = patches[batchStart + batchOffset].Marker;
            markerMaterial[batchOffset] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["center_x"] = marker.Center.X,
                ["center_y"] = marker.Center.Y,
                ["coordinate_space"] = marker.CoordinateSpace,
                ["marker_id"] = marker.MarkerId,
                ["radius"] = marker.Radius,
                ["source_image"] = marker.SourceImage.ToString(),
            };
        }

        parameters["markers"] = markerMaterial;
        if (request.Options.OriginalPixelContentBounds is { Count: > 0 } bounds)
        {
            parameters["content_isolation_version"] = "original-pixel-symbol-bounds-v1";
            parameters["original_pixel_content_bounds"] = bounds.OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => new object[] { item.Key, item.Value.X, item.Value.Y, item.Value.Width, item.Value.Height })
                .ToArray();
        }
        return new InferenceRequest(
            request.Model,
            new InferenceInput(
                values,
                Array.AsReadOnly<long>(
                [batchCount, contract.InputChannelCount, contract.PatchHeight, contract.PatchWidth]),
                contract.InputName,
                contract.OutputName),
            new StageCacheMaterial(
                request.InputSha256,
                $"frame:0,0,{request.Image.Width},{request.Image.Height}",
                request.TransformChain,
                MarkerClassificationContract.Stage,
                request.Options.StageVersion,
                parameters,
                request.ContractVersion),
            request.Options.Timeout);
    }

    private static IReadOnlyList<ClassifiedMarker> DecodeBatch(
        IReadOnlyList<float> output,
        IReadOnlyList<MarkerPatch> patches,
        int batchStart,
        int batchCount,
        MarkerClassifierTensorContract contract,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        var expectedCount = checked(batchCount * contract.ValuesPerMarker);
        if (output.Count != expectedCount)
        {
            throw new ArgumentException(
                $"Classifier output has {output.Count} values; expected {expectedCount}.",
                nameof(output));
        }

        var classified = new ClassifiedMarker[batchCount];
        var values = new float[contract.ValuesPerMarker];
        for (var batchOffset = 0; batchOffset < batchCount; batchOffset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
            {
                var value = output[(batchOffset * values.Length) + valueIndex];
                if (!float.IsFinite(value))
                {
                    throw new ArgumentException("Classifier output values must be finite.", nameof(output));
                }

                values[valueIndex] = value;
            }

            var shapeValues = values.AsSpan(
                MarkerClassifierTensorContract.ShapeOffset,
                MarkerClassificationContract.ShapeClassCount);
            var fillValues = values.AsSpan(
                MarkerClassifierTensorContract.FillOffset,
                MarkerClassificationContract.FillClassCount);
            var shapeProbabilities = contract.OutputEncoding == MarkerClassifierOutputEncoding.Probabilities
                ? ValidateProbabilities(shapeValues, "shape")
                : Softmax(shapeValues);
            var fillProbabilities = contract.OutputEncoding == MarkerClassifierOutputEncoding.Probabilities
                ? ValidateProbabilities(fillValues, "fill")
                : Softmax(fillValues);
            var shapeIndex = MaximumIndex(shapeProbabilities);
            var fillIndex = MaximumIndex(fillProbabilities);
            var shape = (MarkerShape)shapeIndex;
            var fill = (MarkerFill)fillIndex;
            var descriptor = MarkerSymbolMap.Describe(shape, fill);
            var embedding = NormalizeEmbedding(
                values.AsSpan(MarkerClassifierTensorContract.EmbeddingOffset, contract.EmbeddingLength));
            classified[batchOffset] = new ClassifiedMarker(
                patches[batchStart + batchOffset].Marker,
                shape,
                fill,
                descriptor.Symbol,
                descriptor.AccessibleName,
                DecodeArtifactProbability(
                    values[MarkerClassifierTensorContract.ArtifactOffset],
                    contract.OutputEncoding),
                shapeProbabilities[shapeIndex],
                fillProbabilities[fillIndex],
                embedding);
        }

        return ClassificationCollections.Freeze(classified);
    }

    private static float[] Softmax(ReadOnlySpan<float> logits)
    {
        var maximum = float.NegativeInfinity;
        foreach (var logit in logits)
        {
            maximum = Math.Max(maximum, logit);
        }

        var probabilities = new float[logits.Length];
        var total = 0d;
        for (var index = 0; index < logits.Length; index++)
        {
            probabilities[index] = (float)Math.Exp(logits[index] - maximum);
            total += probabilities[index];
        }

        if (!double.IsFinite(total) || total <= 0)
        {
            throw new InvalidOperationException("Classifier logits cannot be converted to probabilities.");
        }

        for (var index = 0; index < probabilities.Length; index++)
        {
            probabilities[index] = (float)(probabilities[index] / total);
        }

        return probabilities;
    }

    private static float[] ValidateProbabilities(ReadOnlySpan<float> values, string headName)
    {
        const double sumTolerance = 1e-4;
        var probabilities = values.ToArray();
        var total = 0d;
        foreach (var probability in probabilities)
        {
            if (probability < 0 || probability > 1)
            {
                throw new ArgumentException(
                    $"Classifier {headName} probability values must be in [0,1].",
                    nameof(values));
            }

            total += probability;
        }

        if (!double.IsFinite(total) || Math.Abs(total - 1) > sumTolerance)
        {
            throw new ArgumentException(
                $"Classifier {headName} probabilities must sum to 1 within {sumTolerance:R}.",
                nameof(values));
        }

        return probabilities;
    }

    private static double DecodeArtifactProbability(
        float value,
        MarkerClassifierOutputEncoding outputEncoding)
    {
        if (outputEncoding != MarkerClassifierOutputEncoding.Probabilities)
        {
            return Sigmoid(value);
        }

        if (value < 0 || value > 1)
        {
            throw new ArgumentException(
                "Classifier artifact probability must be in [0,1].",
                nameof(value));
        }

        return value;
    }

    private static float[] NormalizeEmbedding(ReadOnlySpan<float> values)
    {
        var squaredNorm = 0d;
        foreach (var value in values)
        {
            squaredNorm += value * (double)value;
        }

        if (!double.IsFinite(squaredNorm) || squaredNorm <= 1e-20)
        {
            throw new InvalidOperationException("Classifier embedding must have a finite non-zero norm.");
        }

        var inverseNorm = 1d / Math.Sqrt(squaredNorm);
        var normalized = new float[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            normalized[index] = (float)(values[index] * inverseNorm);
        }

        return normalized;
    }

    private static double Sigmoid(float value)
    {
        if (value >= 0)
        {
            var exponential = Math.Exp(-value);
            return 1 / (1 + exponential);
        }

        var negativeExponential = Math.Exp(value);
        return negativeExponential / (1 + negativeExponential);
    }

    private static int MaximumIndex(float[] values)
    {
        var maximumIndex = 0;
        for (var index = 1; index < values.Length; index++)
        {
            if (values[index] > values[maximumIndex])
            {
                maximumIndex = index;
            }
        }

        return maximumIndex;
    }

    private static MarkerClassificationFailure? Validate(MarkerClassificationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectId) ||
            string.IsNullOrWhiteSpace(request.PanelId) ||
            string.IsNullOrWhiteSpace(request.TransformChain))
        {
            return Error(
                "MARKER_CLASSIFICATION_INVALID_REQUEST",
                "Project ID, panel ID, and transform chain are required.",
                recoverable: true,
                "review_input");
        }

        if (request.InputSha256 is null ||
            request.InputSha256.Length != 64 ||
            !request.InputSha256.All(Uri.IsHexDigit))
        {
            return Error(
                "MARKER_CLASSIFICATION_INVALID_REQUEST",
                "Input SHA-256 must contain exactly 64 hexadecimal characters.",
                recoverable: true,
                "review_input");
        }

        if (request.ContractVersion != MarkerClassificationContract.Version)
        {
            return Error(
                "MARKER_CLASSIFICATION_CONTRACT_UNSUPPORTED",
                $"Contract version {request.ContractVersion} is unsupported.",
                recoverable: false,
                "upgrade_application");
        }

        try
        {
            request.Model.Validate();
        }
        catch (ArgumentException exception)
        {
            return Error(
                "MARKER_CLASSIFICATION_INVALID_MODEL",
                exception.Message,
                recoverable: true,
                "verify_model_manifest");
        }

        var optionsFailure = ValidateOptions(request.Options);
        if (optionsFailure is not null)
        {
            return optionsFailure;
        }

        var markerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var marker in request.Markers)
        {
            if (marker is null ||
                string.IsNullOrWhiteSpace(marker.MarkerId) ||
                !markerIds.Add(marker.MarkerId))
            {
                return Error(
                    "MARKER_CLASSIFICATION_INVALID_REQUEST",
                    "Markers must be non-null and have unique, non-empty IDs.",
                    recoverable: true,
                    "review_markers");
            }
        }

        return null;
    }

    private static MarkerClassificationFailure? ValidateOptions(MarkerClassificationOptions options)
    {
        var contract = options.TensorContract;
        if (contract is null ||
            string.IsNullOrWhiteSpace(contract.InputName) ||
            string.IsNullOrWhiteSpace(contract.OutputName) ||
            contract.PatchWidth <= 0 || contract.PatchHeight <= 0 ||
            contract.InputChannelCount != 1 ||
            contract.EmbeddingLength <= 0 || contract.EmbeddingLength > 256 ||
            !Enum.IsDefined(contract.OutputEncoding) ||
            !float.IsFinite(contract.NormalizeMean) ||
            !float.IsFinite(contract.NormalizeScale) || contract.NormalizeScale == 0)
        {
            return Error(
                "MARKER_CLASSIFICATION_INVALID_OPTIONS",
                "Tensor names, dimensions, one ink-probability channel, normalization, and a 1-256 value embedding are required.",
                recoverable: true,
                "verify_model_manifest");
        }

        if (options.BatchSize <= 0 || options.BatchSize > 4096 ||
            !double.IsFinite(options.PatchRadiusScale) || options.PatchRadiusScale <= 0 ||
            !double.IsFinite(options.MinimumPatchHalfExtentFramePixels) ||
            options.MinimumPatchHalfExtentFramePixels <= 0 ||
            !float.IsFinite(options.PatchPaddingValue) ||
            options.PatchPaddingValue < 0 || options.PatchPaddingValue > 1 ||
            string.IsNullOrWhiteSpace(options.StageVersion) ||
            (options.Timeout != Timeout.InfiniteTimeSpan && options.Timeout <= TimeSpan.Zero))
        {
            return Error(
                "MARKER_CLASSIFICATION_INVALID_OPTIONS",
                "Batch, patch sampling, stage version, and timeout options are invalid.",
                recoverable: true,
                "review_settings");
        }

        try
        {
            _ = checked(
                contract.PatchWidth * contract.PatchHeight * contract.InputChannelCount * options.BatchSize);
            _ = contract.ValuesPerMarker;
        }
        catch (OverflowException)
        {
            return Error(
                "MARKER_CLASSIFICATION_INVALID_OPTIONS",
                "Classifier tensor dimensions exceed supported memory limits.",
                recoverable: true,
                "review_settings");
        }

        return null;
    }

    private static MarkerClassificationResult SuccessResult(
        MarkerClassificationRequest request,
        string runId,
        IEnumerable<ClassifiedMarker> markers,
        IEnumerable<MarkerClassificationBatchReport> reports,
        IEnumerable<string> warnings,
        double extractionMilliseconds,
        double inferenceMilliseconds,
        double postprocessMilliseconds,
        double totalMilliseconds)
    {
        var markerList = ClassificationCollections.Freeze(markers);
        var reportList = ClassificationCollections.Freeze(reports);
        var providers = reportList
            .Select(static report => report.Provider)
            .Where(static provider => provider is not null)
            .Distinct()
            .ToArray();
        var provider = providers.Length == 1 ? providers[0] : null;
        var confidence = markerList.Count == 0
            ? 0
            : markerList.Average(static marker => marker.Confidence);
        return new MarkerClassificationResult(
            request.ContractVersion,
            runId,
            request.ProjectId,
            request.PanelId,
            MarkerClassificationContract.Stage,
            request.Options.StageVersion,
            request.InputSha256,
            MarkerClassificationContract.CoordinateSpace,
            markerList,
            new MarkerClassificationTiming(
                extractionMilliseconds,
                inferenceMilliseconds,
                postprocessMilliseconds,
                totalMilliseconds),
            Math.Clamp(confidence, 0, 1),
            warnings,
            reportList,
            new MarkerClassificationModelReport(
                request.Model.ModelId,
                request.Model.Version,
                request.Model.Sha256,
                provider),
            null);
    }

    private static MarkerClassificationResult PartialFailureResult(
        MarkerClassificationRequest request,
        string runId,
        IEnumerable<ClassifiedMarker> markers,
        IEnumerable<MarkerClassificationBatchReport> reports,
        List<string> warnings,
        MarkerClassificationFailure failure,
        double extractionMilliseconds,
        double inferenceMilliseconds,
        double postprocessMilliseconds,
        double totalMilliseconds)
    {
        if (markers.Any())
        {
            warnings.Add("classification_partial_evidence");
        }

        var markerList = ClassificationCollections.Freeze(markers);
        var reportList = ClassificationCollections.Freeze(reports);
        var confidence = markerList.Count == 0
            ? 0
            : markerList.Average(static marker => marker.Confidence);
        return new MarkerClassificationResult(
            request.ContractVersion,
            runId,
            request.ProjectId,
            request.PanelId,
            MarkerClassificationContract.Stage,
            request.Options.StageVersion,
            request.InputSha256,
            MarkerClassificationContract.CoordinateSpace,
            markerList,
            new MarkerClassificationTiming(
                extractionMilliseconds,
                inferenceMilliseconds,
                postprocessMilliseconds,
                totalMilliseconds),
            Math.Clamp(confidence, 0, 1),
            warnings,
            reportList,
            new MarkerClassificationModelReport(
                request.Model.ModelId,
                request.Model.Version,
                request.Model.Sha256,
                null),
            failure);
    }

    private static MarkerClassificationResult FailureResult(
        MarkerClassificationRequest request,
        string runId,
        MarkerClassificationFailure failure,
        double totalMilliseconds,
        double extractionMilliseconds = 0)
    {
        var stageVersion = request.Options?.StageVersion ?? string.Empty;
        return new MarkerClassificationResult(
            request.ContractVersion,
            runId,
            request.ProjectId,
            request.PanelId,
            MarkerClassificationContract.Stage,
            stageVersion,
            request.InputSha256,
            MarkerClassificationContract.CoordinateSpace,
            Array.Empty<ClassifiedMarker>(),
            new MarkerClassificationTiming(extractionMilliseconds, 0, 0, totalMilliseconds),
            0,
            Array.Empty<string>(),
            Array.Empty<MarkerClassificationBatchReport>(),
            new MarkerClassificationModelReport(
                request.Model.ModelId,
                request.Model.Version,
                request.Model.Sha256,
                null),
            failure);
    }

    private static MarkerClassificationFailure FromInferenceError(InferenceError? error) =>
        error is null
            ? Error(
                "MARKER_CLASSIFICATION_INFERENCE_FAILED",
                "The inference runtime returned no classifier execution or diagnostic.",
                recoverable: true,
                "retry")
            : new MarkerClassificationFailure(
                error.Code,
                error.Severity,
                error.UserMessageKey,
                error.TechnicalMessage,
                error.Recoverable,
                error.SuggestedAction);

    private static MarkerClassificationFailure Error(
        string code,
        string technicalMessage,
        bool recoverable,
        string suggestedAction) =>
        new(
            code,
            "error",
            "Errors." + code,
            technicalMessage,
            recoverable,
            suggestedAction);
}
