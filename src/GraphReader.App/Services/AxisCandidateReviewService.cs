// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.Axis;

namespace GraphReader.App.Services;

public sealed record AxisReviewCandidate(int Index, RustAxisGeometry Geometry);
public sealed record AxisCandidateReviewResult(IReadOnlyList<AxisReviewCandidate> Candidates, string? ErrorCode);

public interface IAxisCandidateReviewService
{
    bool CanReviewCandidates => true;
    Task<AxisCandidateReviewResult> ReviewCandidatesAsync(byte[] originalImage, CancellationToken cancellationToken);
}
