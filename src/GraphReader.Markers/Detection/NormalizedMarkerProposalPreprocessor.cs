// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace GraphReader.Markers.Detection;

/// <summary>
/// Frozen proposal and input-preprocessing contract for the normalized-training
/// marker-center candidate. This contract is intentionally separate from the
/// older full-frame marker runtime.
/// </summary>
public static class NormalizedMarkerProposalContract
{
    public const string RuntimeRevision = "marker-center-normalized-proposal-runtime-v1";
    public const string PreprocessRevision = "patch-ink-median-background-subtraction-v1";
    public const string InputName = "candidate_patches";
    public const string InputLayout = "NCHW";
    public const string InputDtype = "float32";
    public const string InputChannelOrder = "ink_probability,text_mask,artifact_mask";
    public const int ChannelCount = 3;
    public const int PatchSize = 33;
    public const int PatchRadius = PatchSize / 2;
    public const int ProposalStride = 4;
    public const int InkSupportWindow = 17;
    public const int InkSupportRadius = InkSupportWindow / 2;
    public const float InkSupportThreshold = 0.11f;
    public const int MaskRejectionRadius = 2;
    // The frozen Python post-filter converts the tensor maximum to binary64
    // before comparing it with the literal 0.35. Keep this binary64 boundary
    // so float32 0.35 remains just below the rejection threshold.
    public const double MaskRejectionThreshold = 0.35;

    public static string CacheMaterial { get; } = string.Join(
        '|',
        RuntimeRevision,
        PreprocessRevision,
        InputName,
        InputLayout,
        InputDtype,
        InputChannelOrder,
        FormattableString.Invariant($"patch={PatchSize}"),
        FormattableString.Invariant($"stride={ProposalStride}"),
        FormattableString.Invariant($"support_window={InkSupportWindow}"),
        FormattableString.Invariant($"support_threshold={InkSupportThreshold:R}"),
        FormattableString.Invariant($"mask_radius={MaskRejectionRadius}"),
        FormattableString.Invariant($"mask_threshold={MaskRejectionThreshold:R}"));
}

/// <summary>
/// Immutable proposal tensor and its original tensor-grid coordinates.
/// </summary>
public sealed class NormalizedMarkerProposalBatch
{
    private readonly float[] tensor;
    private readonly MarkerPoint[] coordinates;

    internal NormalizedMarkerProposalBatch(float[] tensor, MarkerPoint[] coordinates)
    {
        this.tensor = tensor;
        this.coordinates = coordinates;
        TensorSha256 = HashFloat32LittleEndian(tensor);
    }

    public int Count => coordinates.Length;

    public ReadOnlyMemory<float> Tensor => tensor;

    public IReadOnlyList<MarkerPoint> Coordinates => Array.AsReadOnly(coordinates);

    public IReadOnlyList<long> Shape =>
        [Count, NormalizedMarkerProposalContract.ChannelCount, NormalizedMarkerProposalContract.PatchSize, NormalizedMarkerProposalContract.PatchSize];

    public string TensorSha256 { get; }

    public string CacheMaterial => string.Create(
        CultureInfo.InvariantCulture,
        $"{NormalizedMarkerProposalContract.CacheMaterial}|count={Count}|tensor_sha256={TensorSha256}");

    private static string HashFloat32LittleEndian(ReadOnlySpan<float> values)
    {
        var bytes = new byte[checked(values.Length * sizeof(float))];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(values[index]));
        }

        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

/// <summary>
/// Reproduces the preregistered PyTorch proposal extraction and per-patch ink
/// median subtraction without executing or approving a model.
/// </summary>
public static class NormalizedMarkerProposalPreprocessor
{
    public static NormalizedMarkerProposalBatch Prepare(
        MarkerImageFrame frame,
        CancellationToken cancellationToken) =>
        PrepareFrame(frame, null, cancellationToken);

    /// <summary>
    /// Prepare only requested eligible grid locations using the identical runtime
    /// preprocessing contract. Useful when only a small subset is needed.
    /// </summary>
    public static NormalizedMarkerProposalBatch PrepareSelected(
        MarkerImageFrame frame,
        IReadOnlySet<MarkerPoint> requested,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requested);
        return PrepareFrame(frame, new HashSet<MarkerPoint>(requested), cancellationToken);
    }

    private static NormalizedMarkerProposalBatch PrepareFrame(
        MarkerImageFrame frame,
        IReadOnlySet<MarkerPoint>? requested,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidatePlane(
            frame.ChannelsFirstPixels,
            frame.Width,
            frame.Height,
            frame.ChannelCount,
            nameof(frame));
        ValidateMask(frame.OcrMask, frame.Width, frame.Height, nameof(frame.OcrMask));
        ValidateMask(frame.ArtifactMask, frame.Width, frame.Height, nameof(frame.ArtifactMask));

        var inkProbability = new float[checked(frame.Width * frame.Height)];
        ReadOnlySpan<float> luminance = frame.ChannelsFirstPixels.Span;
        for (var y = 0; y < frame.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rowOffset = y * frame.Width;
            for (var x = 0; x < frame.Width; x++)
            {
                int index = rowOffset + x;
                inkProbability[index] = 1f - luminance[index];
            }
        }

        return PrepareCore(
            frame.Width,
            frame.Height,
            inkProbability,
            frame.OcrMask,
            frame.ArtifactMask,
            requested,
            cancellationToken);
    }

    public static NormalizedMarkerProposalBatch Prepare(
        int width,
        int height,
        ReadOnlyMemory<float> inkProbability,
        MarkerMask textMask,
        MarkerMask artifactMask,
        CancellationToken cancellationToken) =>
        PrepareCore(width, height, inkProbability, textMask, artifactMask, null, cancellationToken);

    private static NormalizedMarkerProposalBatch PrepareCore(
        int width,
        int height,
        ReadOnlyMemory<float> inkProbability,
        MarkerMask textMask,
        MarkerMask artifactMask,
        IReadOnlySet<MarkerPoint>? requested,
        CancellationToken cancellationToken)
    {
        ValidatePlane(inkProbability, width, height, 1, nameof(inkProbability));
        ValidateMask(textMask, width, height, nameof(textMask));
        ValidateMask(artifactMask, width, height, nameof(artifactMask));
        cancellationToken.ThrowIfCancellationRequested();

        ReadOnlySpan<float> ink = inkProbability.Span;
        ReadOnlySpan<float> text = textMask.Values.Span;
        ReadOnlySpan<float> artifact = artifactMask.Values.Span;
        int[] inkSupport = BuildThresholdIntegral(
            ink,
            width,
            height,
            NormalizedMarkerProposalContract.InkSupportThreshold,
            cancellationToken);
        int[] textRejection = BuildThresholdIntegral(
            text,
            width,
            height,
            NormalizedMarkerProposalContract.MaskRejectionThreshold,
            cancellationToken);
        int[] artifactRejection = BuildThresholdIntegral(
            artifact,
            width,
            height,
            NormalizedMarkerProposalContract.MaskRejectionThreshold,
            cancellationToken);

        var eligible = new List<MarkerPoint>();
        for (var y = 0; y < height; y += NormalizedMarkerProposalContract.ProposalStride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x += NormalizedMarkerProposalContract.ProposalStride)
            {
                if (requested is not null && !requested.Contains(new MarkerPoint(x, y)))
                {
                    continue;
                }
                if (!HasThresholdHit(
                        inkSupport,
                        width,
                        height,
                        x,
                        y,
                        NormalizedMarkerProposalContract.InkSupportRadius) ||
                    HasThresholdHit(
                        textRejection,
                        width,
                        height,
                        x,
                        y,
                        NormalizedMarkerProposalContract.MaskRejectionRadius) ||
                    HasThresholdHit(
                        artifactRejection,
                        width,
                        height,
                        x,
                        y,
                        NormalizedMarkerProposalContract.MaskRejectionRadius))
                {
                    continue;
                }

                eligible.Add(new MarkerPoint(x, y));
            }
        }

        int patchPixelCount = checked(
            NormalizedMarkerProposalContract.PatchSize *
            NormalizedMarkerProposalContract.PatchSize);
        int valuesPerProposal = checked(
            patchPixelCount * NormalizedMarkerProposalContract.ChannelCount);
        var tensor = new float[checked(eligible.Count * valuesPerProposal)];
        var medianScratch = new float[patchPixelCount];
        for (var proposalIndex = 0; proposalIndex < eligible.Count; proposalIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkerPoint coordinate = eligible[proposalIndex];
            int centerX = checked((int)coordinate.X);
            int centerY = checked((int)coordinate.Y);
            int proposalOffset = checked(proposalIndex * valuesPerProposal);
            CopyPatch(ink, width, height, centerX, centerY, tensor, proposalOffset);
            CopyPatch(
                text,
                width,
                height,
                centerX,
                centerY,
                tensor,
                proposalOffset + patchPixelCount);
            CopyPatch(
                artifact,
                width,
                height,
                centerX,
                centerY,
                tensor,
                proposalOffset + (2 * patchPixelCount));

            tensor.AsSpan(proposalOffset, patchPixelCount).CopyTo(medianScratch);
            Array.Sort(medianScratch);
            float background = medianScratch[patchPixelCount / 2];
            Span<float> normalizedInk = tensor.AsSpan(proposalOffset, patchPixelCount);
            for (var index = 0; index < normalizedInk.Length; index++)
            {
                normalizedInk[index] = Math.Clamp(normalizedInk[index] - background, 0f, 1f);
            }
        }

        return new NormalizedMarkerProposalBatch(tensor, eligible.ToArray());
    }

    private static int[] BuildThresholdIntegral(
        ReadOnlySpan<float> values,
        int width,
        int height,
        double threshold,
        CancellationToken cancellationToken)
    {
        int integralWidth = checked(width + 1);
        var integral = new int[checked(integralWidth * (height + 1))];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rowCount = 0;
            for (var x = 0; x < width; x++)
            {
                if (values[(y * width) + x] >= threshold)
                {
                    rowCount++;
                }

                integral[((y + 1) * integralWidth) + x + 1] =
                    integral[(y * integralWidth) + x + 1] + rowCount;
            }
        }

        return integral;
    }

    private static bool HasThresholdHit(
        int[] integral,
        int width,
        int height,
        int centerX,
        int centerY,
        int radius)
    {
        int left = Math.Max(0, centerX - radius);
        int top = Math.Max(0, centerY - radius);
        int right = Math.Min(width, centerX + radius + 1);
        int bottom = Math.Min(height, centerY + radius + 1);
        int stride = width + 1;
        int count = integral[(bottom * stride) + right] -
            integral[(top * stride) + right] -
            integral[(bottom * stride) + left] +
            integral[(top * stride) + left];
        return count != 0;
    }

    private static void CopyPatch(
        ReadOnlySpan<float> source,
        int width,
        int height,
        int centerX,
        int centerY,
        float[] destination,
        int destinationOffset)
    {
        for (var patchY = 0; patchY < NormalizedMarkerProposalContract.PatchSize; patchY++)
        {
            int sourceY = centerY + patchY - NormalizedMarkerProposalContract.PatchRadius;
            if (sourceY < 0 || sourceY >= height)
            {
                continue;
            }

            for (var patchX = 0; patchX < NormalizedMarkerProposalContract.PatchSize; patchX++)
            {
                int sourceX = centerX + patchX - NormalizedMarkerProposalContract.PatchRadius;
                if (sourceX >= 0 && sourceX < width)
                {
                    destination[destinationOffset +
                        (patchY * NormalizedMarkerProposalContract.PatchSize) + patchX] =
                        source[(sourceY * width) + sourceX];
                }
            }
        }
    }

    private static void ValidateMask(MarkerMask mask, int width, int height, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(mask, parameterName);
        if (mask.Width != width || mask.Height != height)
        {
            throw new ArgumentException(
                "Marker proposal mask dimensions must match the input planes.",
                parameterName);
        }

        ValidatePlane(mask.Values, width, height, 1, parameterName);
    }

    private static void ValidatePlane(
        ReadOnlyMemory<float> values,
        int width,
        int height,
        int channelCount,
        string parameterName)
    {
        if (width <= 0 || height <= 0 || channelCount != 1)
        {
            throw new ArgumentException(
                "Marker proposal preprocessing requires one positive-sized probability plane.",
                parameterName);
        }

        int expectedLength;
        try
        {
            expectedLength = checked(width * height * channelCount);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("Marker proposal plane dimensions overflow.", parameterName, exception);
        }

        if (values.Length != expectedLength)
        {
            throw new ArgumentException(
                "Marker proposal plane length does not match its dimensions.",
                parameterName);
        }

        foreach (float value in values.Span)
        {
            if (!float.IsFinite(value) || value < 0 || value > 1)
            {
                throw new ArgumentException(
                    "Marker proposal probability planes must contain finite values in [0,1].",
                    parameterName);
            }
        }
    }
}
