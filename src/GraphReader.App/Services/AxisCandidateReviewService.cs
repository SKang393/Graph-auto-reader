// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.Axis;
using System.Text.Json.Serialization;

namespace GraphReader.App.Services;

public sealed record AxisReviewCandidate(int Index, RustAxisGeometry Geometry);
public sealed record AxisCandidateReviewResult(IReadOnlyList<AxisReviewCandidate> Candidates, string? ErrorCode)
{
    [JsonIgnore]
    public string Provider { get; init; } = "opencv-review";
}

public interface IAxisCandidateReviewService
{
    bool CanReviewCandidates => true;
    Task<AxisCandidateReviewResult> ReviewCandidatesAsync(byte[] originalImage, CancellationToken cancellationToken);
}
