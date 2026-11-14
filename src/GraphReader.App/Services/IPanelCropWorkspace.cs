// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.ViewModels;

namespace GraphReader.App.Services;

public sealed record PanelCropContext(string TabId, string Binding, int Width, int Height)
{
    internal object? OriginalImageIdentity { get; init; }
}

public static class PanelCropLimits
{
    public const int MinimumSide = 32;
    public const int MaximumSide = 4096;
    public const int MaximumPixels = 4_194_304;
}

public interface IPanelCropWorkspace
{
    bool CanCreatePanelCrops { get; }
    bool HasDependentPanelCrops(string tabId);
    PanelCropContext? GetPanelCropContext(string tabId);
    bool IsPanelCropContextCurrent(PanelCropContext context);
    Task<WorkspaceTabViewModel> CreatePanelCropAsync(PanelCropContext context,
        MarkerExemplarRectangle rectangle, CancellationToken cancellationToken);
}
