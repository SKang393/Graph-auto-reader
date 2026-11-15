// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Integration.Workflow;
using GraphReader.App.ViewModels;

namespace GraphReader.App.Services;

public sealed record WorkspaceAxisCandidateReview(string TabId, string Binding, IReadOnlyList<AxisReviewCandidate> Candidates)
{
    internal object? OriginalImageIdentity { get; init; }
}

public interface IAxisCandidateReviewWorkspace
{
    bool CanReviewAxisCandidates { get; }
    Task<WorkspaceAxisCandidateReview?> ReviewAxisCandidatesAsync(string tabId, CancellationToken cancellationToken);
    bool IsAxisCandidateReviewCurrent(WorkspaceAxisCandidateReview review);
    WorkspaceAxisSuggestion? SelectAxisCandidate(WorkspaceAxisCandidateReview review, int index);
}

public partial class ManualPreviewWorkspaceService : IAxisCandidateReviewWorkspace
{
    private static readonly IReadOnlyList<string> SelectedAxisCandidateReasons =
        Array.AsReadOnly(new[] { "native_user_selected_candidate" });

    public bool CanReviewAxisCandidates =>
        RuntimeEnvironment == WorkflowRuntimeEnvironment.ManualPreview &&
        Volatile.Read(ref _calibrationOcrDisposed) == 0 &&
        _axisProposalService is IAxisCandidateReviewService { CanReviewCandidates: true };

    public async Task<WorkspaceAxisCandidateReview?> ReviewAxisCandidatesAsync(string tabId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanReviewAxisCandidates) return null;
        WorkspaceTabViewModel tab = RequireTab(tabId);
        string binding = AxisSuggestionBinding(tab);
        var originalImage = _originalPanelBytesByTab[tabId];
        var service = (IAxisCandidateReviewService)_axisProposalService!;
        AxisCandidateReviewResult result = await service.ReviewCandidatesAsync(originalImage.Copy(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ErrorCode is not null || !OpenCvAxisProposalService.ValidCandidates(result, tab.PixelWidth, tab.PixelHeight))
            return null;
        // The review list belongs to this image snapshot, not the worker's mutable response.
        var candidates = Array.AsReadOnly(result.Candidates.Select(candidate => candidate with
        {
            Geometry = candidate.Geometry with { XTickPositions = Array.Empty<int>(), YTickPositions = Array.Empty<int>() },
        }).ToArray());
        var review = new WorkspaceAxisCandidateReview(tabId, binding, candidates) { OriginalImageIdentity = originalImage };
        return IsAxisCandidateReviewCurrent(review) ? review : null;
    }

    public bool IsAxisCandidateReviewCurrent(WorkspaceAxisCandidateReview review)
    {
        if (!CanReviewAxisCandidates || review is null) return false;
        WorkspaceTabViewModel? tab = _tabs.SingleOrDefault(item => item.TabId == review.TabId);
        return tab is not null && _originalPanelBytesByTab.TryGetValue(tab.TabId, out var image) &&
            ReferenceEquals(image, review.OriginalImageIdentity) && review.Binding == AxisSuggestionBinding(tab) &&
            OpenCvAxisProposalService.ValidCandidates(new(review.Candidates, null), tab.PixelWidth, tab.PixelHeight);
    }

    public WorkspaceAxisSuggestion? SelectAxisCandidate(WorkspaceAxisCandidateReview review, int index)
    {
        if (!IsAxisCandidateReviewCurrent(review) || index < 0 || index >= review.Candidates.Count) return null;
        AxisReviewCandidate candidate = review.Candidates[index];
        return new WorkspaceAxisSuggestion(review.TabId, review.Binding, candidate.Geometry)
        {
            OriginalImageIdentity = review.OriginalImageIdentity,
            Provider = "opencv-review",
            ReviewReasons = SelectedAxisCandidateReasons,
        };
    }
}
