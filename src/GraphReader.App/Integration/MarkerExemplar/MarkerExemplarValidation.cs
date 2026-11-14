// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Security.Cryptography;
using GraphReader.App.Services;

namespace GraphReader.App.Integration.MarkerExemplar;

internal static class MarkerExemplarValidation
{
    internal const int MaximumPixels = 4_194_304;
    internal const int MaximumCandidates = 128;
    internal const string Algorithm = "original-panel-user-exemplar-gaussian-single-scale-v2";
    internal static bool ValidRequest(MarkerExemplarRequest? request, ReadOnlyMemory<byte> image) =>
        ValidMetadata(request) && image.Length is > 0 and <= MarkerExemplarWire.MaximumImageBytes &&
        string.Equals(Convert.ToHexStringLower(SHA256.HashData(image.Span)), request!.PanelImageSha256,
            StringComparison.OrdinalIgnoreCase);

    internal static bool ValidMetadata(MarkerExemplarRequest? request)
    {
        if (request is null || request.ProtocolVersion != 1 || !Identifier(request.RequestId) ||
            !Identifier(request.PanelId) || !Identifier(request.SelectedPointId) || !Identifier(request.SelectedSeriesId) ||
            !Sha(request.Binding) || !Sha(request.SourceFileSha256) || !Sha(request.PanelImageSha256) ||
            request.Width is < 1 or > 4096 || request.Height is < 1 or > 4096 ||
            (long)request.Width * request.Height > MaximumPixels || request.Exemplar is not { } exemplar ||
            request.Plot is not { } plot || request.SelectedPoint is not { } point ||
            !InsideImage(exemplar, request.Width, request.Height) || !InsideImage(plot, request.Width, request.Height) ||
            exemplar.Width != exemplar.Height || exemplar.Width is < 5 or > 65 ||
            !Inside(point, exemplar) || !MarkerExemplarGeometry.ContainsCenter(plot, point)) return false;
        return true;
    }

    internal static bool Inside(MarkerExemplarPoint point, MarkerExemplarRectangle box) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) && point.X >= box.X && point.Y >= box.Y &&
        point.X < (long)box.X + box.Width && point.Y < (long)box.Y + box.Height;

    private static bool InsideImage(MarkerExemplarRectangle box, int width, int height) =>
        box.X >= 0 && box.Y >= 0 && box.Width > 0 && box.Height > 0 &&
        (long)box.X + box.Width <= width && (long)box.Y + box.Height <= height;
    private static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
    private static bool Sha(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static bool ValidResponse(MarkerExemplarRequest request, MarkerExemplarResponse? response)
    {
        if (response is null || response.Request != request || response.Candidates is null) return false;
        if (response.FailureCode is not null)
            return KnownFailure(response.FailureCode) && response.Candidates.Count == 0 && response.Runtime is null && response.Timings is null;
        if (response.Runtime is not { } runtime || runtime.CpuBudget is not { } budget ||
            !budget.Verified || budget.LogicalProcessors == 0 || budget.Rate == 0 || budget.Rate != 5000 / budget.LogicalProcessors ||
            budget.MaximumLogicalCores != budget.LogicalProcessors * budget.Rate / 10000d ||
            budget.MaximumLogicalCores is <= 0 or > .5 || budget.PriorityClass != "BelowNormal" ||
            runtime.OpenCvThreads != 1 || runtime.Algorithm != Algorithm || response.Timings is not { } timings ||
            !Time(timings.DecodeMilliseconds) || !Time(timings.MatchingMilliseconds) || !Time(timings.TotalMilliseconds) ||
            timings.TotalMilliseconds < timings.DecodeMilliseconds + timings.MatchingMilliseconds ||
            response.Candidates.Count > MaximumCandidates) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (MarkerExemplarCandidate candidate in response.Candidates)
        {
            if (candidate is null || !Identifier(candidate.CandidateId) || !ids.Add(candidate.CandidateId) ||
                candidate.Center is null || !MarkerExemplarGeometry.ContainsCenter(request.Plot, candidate.Center) || !double.IsFinite(candidate.Similarity) ||
                candidate.Similarity is < .9 or > 1 ||
                DistanceSquared(candidate.Center, request.SelectedPoint) < .25) return false;
        }
        return true;
    }

    internal static double DistanceSquared(MarkerExemplarPoint a, MarkerExemplarPoint b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
    private static bool Time(double value) => double.IsFinite(value) && value is >= 0 and <= 15_000;
    internal static bool KnownFailure(string? code) => code is MarkerExemplarReasonCodes.InvalidInput or
        MarkerExemplarReasonCodes.InvalidFrame or MarkerExemplarReasonCodes.ResourceUnavailable or
        MarkerExemplarReasonCodes.Unavailable or MarkerExemplarReasonCodes.Timeout or
        MarkerExemplarReasonCodes.UninformativeExemplar or MarkerExemplarReasonCodes.TooManyCandidates or
        MarkerExemplarReasonCodes.MatchingFailed;
}
