// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.Axis;
using GraphReader.App.Integration;

namespace GraphReader.App.Services;

public interface IAxisProposalService
{
    Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken);
}

/// <summary>Uses strict native review geometry when Rust cannot resolve an axis.</summary>
public sealed class RustFirstAxisProposalService(IAxisProposalService primary, IAxisProposalService fallback) : IAxisProposalService, IAxisCandidateReviewService
{
    private readonly IAxisProposalService _primary = primary ?? throw new ArgumentNullException(nameof(primary));
    private readonly IAxisProposalService _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));

    public bool CanReviewCandidates => _primary is IAxisCandidateReviewService { CanReviewCandidates: true } ||
        _fallback is IAxisCandidateReviewService { CanReviewCandidates: true };

    public async Task<AxisCandidateReviewResult> ReviewCandidatesAsync(byte[] originalImage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_primary is IAxisCandidateReviewService { CanReviewCandidates: true } primary)
        {
            AxisCandidateReviewResult result = await primary.ReviewCandidatesAsync(originalImage, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Provider != "rust-review" || result.Candidates is null ||
                result.ErrorCode is null && result.Candidates.Count == 0 ||
                result.ErrorCode is not null && result.Candidates.Count != 0)
                return new AxisCandidateReviewResult(Array.Empty<AxisReviewCandidate>(), "INVALID_RESPONSE") { Provider = "rust-review" };
            if (result.ErrorCode is null) return result;
            if (result.ErrorCode is not ("NO_AXIS" or "UNSUPPORTED_PLATFORM" or "WORKER_START_FAILED")) return result;
        }
        return _fallback is IAxisCandidateReviewService { CanReviewCandidates: true } review
            ? await review.ReviewCandidatesAsync(originalImage, cancellationToken).ConfigureAwait(false)
            : new AxisCandidateReviewResult(Array.Empty<AxisReviewCandidate>(), "OPENCV_AXIS_UNAVAILABLE");
    }

    public async Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RustAxisProposalResult result = await _primary.SuggestAsync(originalImage, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Geometry is not null || result.ErrorCode is not ("NO_AXIS" or "NO_INTERSECTION" or "AMBIGUOUS_AXIS")) return result;
        RustAxisProposalResult review = await _fallback.SuggestAsync(originalImage, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ErrorCode == "AMBIGUOUS_AXIS")
        {
            if (review.Geometry is null) return result;
            return review with { ReviewReasons = review.ReviewReasons.Append("rust_axis_ambiguous").Distinct(StringComparer.Ordinal).ToArray() };
        }
        return review;
    }
}

public sealed class RustAxisProposalService(string workerPath) : IAxisProposalService, IAxisCandidateReviewService
{
    public Task<AxisCandidateReviewResult> ReviewCandidatesAsync(byte[] originalImage, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<AxisCandidateReviewResult>(cancellationToken);
        if (originalImage is null || originalImage.Length == 0 || originalImage.Length > 32 * 1024 * 1024)
            return Task.FromResult(new AxisCandidateReviewResult(Array.Empty<AxisReviewCandidate>(), "INVALID_INPUT") { Provider = "rust-review" });
        byte[] snapshot = (byte[])originalImage.Clone();
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(snapshot, writable: false);
            BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if (frame.PixelWidth is < 32 or > 4096 || frame.PixelHeight is < 32 or > 4096 ||
                (long)frame.PixelWidth * frame.PixelHeight > 16_777_216)
                return new AxisCandidateReviewResult(Array.Empty<AxisReviewCandidate>(), "INVALID_INPUT") { Provider = "rust-review" };
            var gray = new FormatConvertedBitmap(WhiteBackgroundRaster.Normalize(frame, cancellationToken), PixelFormats.Gray8, null, 0);
            byte[] pixels = new byte[checked(gray.PixelWidth * gray.PixelHeight)];
            gray.CopyPixels(pixels, gray.PixelWidth, 0);
            RustAxisCandidateReviewResult result = await RustAxisCandidateReview.RunAsync(workerPath, gray.PixelWidth, gray.PixelHeight,
                pixels, cancellationToken).ConfigureAwait(false);
            return new AxisCandidateReviewResult(Array.AsReadOnly(result.Candidates.Select((geometry, index) => new AxisReviewCandidate(index, geometry)).ToArray()),
                result.ErrorCode) { Provider = "rust-review" };
        }, cancellationToken);
    }

    public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(originalImage, writable: false);
            BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            if (frame.PixelWidth is < 32 or > 4096 || frame.PixelHeight is < 32 or > 4096 ||
                (long)frame.PixelWidth * frame.PixelHeight > 16_777_216)
                return new RustAxisProposalResult(null, "INVALID_INPUT");
            var gray = new FormatConvertedBitmap(WhiteBackgroundRaster.Normalize(frame, cancellationToken), PixelFormats.Gray8, null, 0);
            byte[] pixels = new byte[checked(gray.PixelWidth * gray.PixelHeight)];
            gray.CopyPixels(pixels, gray.PixelWidth, 0);
            return await RustAxisProposal.RunAsync(workerPath, gray.PixelWidth, gray.PixelHeight,
                pixels, cancellationToken: cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
}
