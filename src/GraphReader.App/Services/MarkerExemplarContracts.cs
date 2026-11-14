// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
namespace GraphReader.App.Services;

/// <summary>Review-only image matches. No points or scientific values are applied.</summary>
public interface IMarkerExemplarSuggestionService : IAsyncDisposable
{
    Task<MarkerExemplarResponse> FindAsync(MarkerExemplarRequest request,
        ReadOnlyMemory<byte> encodedOriginalPanel, CancellationToken cancellationToken);
}

public sealed record MarkerExemplarPoint(double X, double Y);
public sealed record MarkerExemplarRectangle(int X, int Y, int Width, int Height);
public sealed record MarkerExemplarRequest(string RequestId, string Binding, string SourceFileSha256,
    string PanelImageSha256, string PanelId, int Width, int Height,
    MarkerExemplarRectangle Exemplar, MarkerExemplarRectangle Plot,
    string SelectedPointId, string SelectedSeriesId, MarkerExemplarPoint SelectedPoint)
{
    public int ProtocolVersion { get; init; } = 1;
}
public sealed record MarkerExemplarCandidate(string CandidateId, MarkerExemplarPoint Center, double Similarity);
public sealed record MarkerExemplarTimings(double DecodeMilliseconds, double MatchingMilliseconds, double TotalMilliseconds);
public sealed record MarkerExemplarRuntime(CalibrationOcrCpuBudget CpuBudget, int OpenCvThreads, string Algorithm);
public sealed record MarkerExemplarResponse(MarkerExemplarRequest? Request,
    IReadOnlyList<MarkerExemplarCandidate> Candidates, MarkerExemplarTimings? Timings,
    MarkerExemplarRuntime? Runtime, string? FailureCode)
{
    public static MarkerExemplarResponse Failure(MarkerExemplarRequest? request, string reason) =>
        new(request, [], null, null, reason);
}

public static class MarkerExemplarReasonCodes
{
    public const string InvalidInput = "MARKER_EXEMPLAR_INVALID_INPUT";
    public const string InvalidFrame = "MARKER_EXEMPLAR_INVALID_FRAME";
    public const string ResourceUnavailable = "MARKER_EXEMPLAR_RESOURCE_UNAVAILABLE";
    public const string Unavailable = "MARKER_EXEMPLAR_UNAVAILABLE";
    public const string Timeout = "MARKER_EXEMPLAR_TIMEOUT";
    public const string UninformativeExemplar = "MARKER_EXEMPLAR_UNINFORMATIVE";
    public const string TooManyCandidates = "MARKER_EXEMPLAR_TOO_MANY_CANDIDATES";
    public const string MatchingFailed = "MARKER_EXEMPLAR_MATCHING_FAILED";
}
