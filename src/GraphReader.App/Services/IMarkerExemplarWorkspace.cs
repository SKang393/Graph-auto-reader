// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
namespace GraphReader.App.Services;

public sealed record WorkspaceMarkerExemplarSuggestion(string TabId, MarkerExemplarRequest Request, MarkerExemplarResponse Response)
{
    internal object? OriginalImageIdentity { get; init; }
}

public interface IMarkerExemplarWorkspace
{
    bool CanFindMarkerExamples { get; }
    Task<WorkspaceMarkerExemplarSuggestion?> FindMarkerExamplesAsync(string tabId, string pointId, string seriesId,
        int exampleSize, CancellationToken cancellationToken);
    bool IsMarkerExemplarSuggestionCurrent(WorkspaceMarkerExemplarSuggestion suggestion);
}
