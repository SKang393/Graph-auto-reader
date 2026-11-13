// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.App.ViewModels;
using GraphReader.Axis;

namespace GraphReader.App.Services;

public sealed record WorkspaceAxisSuggestion(string TabId, string Binding, RustAxisGeometry Geometry)
{
    internal object? OriginalImageIdentity { get; init; }
}

public interface IAxisSuggestionWorkspace
{
    bool CanSuggestAxes { get; }
    Task<WorkspaceAxisSuggestion?> SuggestAxesAsync(string tabId, CancellationToken cancellationToken);
    bool IsAxisSuggestionCurrent(WorkspaceAxisSuggestion suggestion);
}

public partial class ManualPreviewWorkspaceService
{
    private readonly IAxisProposalService? _axisProposalService;
    public bool CanSuggestAxes => _axisProposalService is not null &&
        RuntimeEnvironment == GraphReader.App.Integration.Workflow.WorkflowRuntimeEnvironment.ManualPreview;

    public async Task<WorkspaceAxisSuggestion?> SuggestAxesAsync(string tabId, CancellationToken cancellationToken)
    {
        if (!CanSuggestAxes) return null;
        WorkspaceTabViewModel tab = RequireTab(tabId);
        string binding = AxisSuggestionBinding(tab);
        var originalImage = _originalPanelBytesByTab[tabId];
        byte[] bytes = originalImage.Copy();
        RustAxisProposalResult result = await _axisProposalService!.SuggestAsync(bytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Geometry is not { } geometry || geometry.Left < 0 || geometry.Right >= tab.PixelWidth ||
            geometry.Top < 0 || geometry.Bottom >= tab.PixelHeight || geometry.Left >= geometry.Right ||
            geometry.Top >= geometry.Bottom || !double.IsFinite(geometry.Confidence) || geometry.Confidence is < 0 or > 1)
            return null;
        var suggestion = new WorkspaceAxisSuggestion(tabId, binding, geometry) { OriginalImageIdentity = originalImage };
        return IsAxisSuggestionCurrent(suggestion) ? suggestion : null;
    }

    public bool IsAxisSuggestionCurrent(WorkspaceAxisSuggestion suggestion)
    {
        WorkspaceTabViewModel? tab = _tabs.SingleOrDefault(tab => tab.TabId == suggestion.TabId);
        return tab is not null && _originalPanelBytesByTab.TryGetValue(tab.TabId, out var image) &&
            ReferenceEquals(image, suggestion.OriginalImageIdentity) && suggestion.Binding == AxisSuggestionBinding(tab);
    }

    private string AxisSuggestionBinding(WorkspaceTabViewModel tab)
    {
        var panel = CurrentProject.Panels.Single(panel => panel.PanelId.Value.ToString() == tab.PanelId);
        var source = CurrentProject.Sources.Single(source => source.SourceId == panel.SourceId);
        string payload = JsonSerializer.Serialize(new
        {
            CurrentProject.ProjectId, panel.PanelId, panel.SourceId, source.Sha256,
            tab.SourceSha256, tab.PixelWidth, tab.PixelHeight, panel.PageNumber, panel.Crop, panel.Transforms, tab.Calibration,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
