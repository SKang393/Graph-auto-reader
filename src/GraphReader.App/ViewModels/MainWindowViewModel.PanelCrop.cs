// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Windows;
using System.Windows.Input;
using GraphReader.App.Controls;
using GraphReader.App.Localization;
using GraphReader.App.Services;

namespace GraphReader.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private PanelCropContext? _panelCropContext;
    private Point? _panelCropFirstCorner;
    private MarkerExemplarRectangle? _panelCropRectangle;
    private bool _panelCropRectangleIsValid;
    private bool _panelCropCreating;
    private string? _panelCropStatusText;

    public ICommand BeginPanelCropCommand { get; private set; } = null!;
    public ICommand CreatePanelCropCommand { get; private set; } = null!;
    public bool IsPanelCropActive => EditorMode == ManualEditorMode.PanelCrop;
    public bool HasPanelCropRectangle => _panelCropRectangle is not null;
    public string PanelCropStatusText => _panelCropStatusText ?? string.Empty;

    private void InitializePanelCropCommands()
    {
        BeginPanelCropCommand = new RelayCommand(_ => BeginPanelCrop(), _ => CanBeginPanelCrop());
        CreatePanelCropCommand = new AsyncRelayCommand(CreatePanelCropAsync, CanCreatePanelCrop);
    }

    private bool CanBeginPanelCrop() => !IsBusy && SelectedTab is { } tab &&
        _workspaceService is IPanelCropWorkspace { CanCreatePanelCrops: true } workspace &&
        workspace.GetPanelCropContext(tab.TabId) is { } context &&
        context.TabId == tab.TabId && context.Width == tab.PixelWidth && context.Height == tab.PixelHeight &&
        workspace.IsPanelCropContextCurrent(context);

    private void BeginPanelCrop()
    {
        if (IsBusy || SelectedTab is not { } tab ||
            _workspaceService is not IPanelCropWorkspace { CanCreatePanelCrops: true } workspace ||
            workspace.GetPanelCropContext(tab.TabId) is not { } context || context.TabId != tab.TabId ||
            context.Width != tab.PixelWidth || context.Height != tab.PixelHeight ||
            !workspace.IsPanelCropContextCurrent(context))
        {
            SetPanelCropStatus(GetLocalizedString(LocalizationKeys.ManualPanelCropUnavailable));
            return;
        }

        ClearPanelCropState();
        _panelCropContext = context;
        _calibrationAnchors.Clear();
        EditorMode = ManualEditorMode.PanelCrop;
        OnPropertyChanged(nameof(IsPanelCropActive));
        OnPropertyChanged(nameof(EditorInstruction));
        SetPanelCropStatus(GetLocalizedString(LocalizationKeys.ManualPanelCropFirstCorner));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void HandlePanelCropCanvasPoint(WorkspaceTabViewModel tab, Point point)
    {
        if (_panelCropContext is not { } context ||
            _workspaceService is not IPanelCropWorkspace workspace ||
            !ReferenceEquals(SelectedTab, tab) || context.TabId != tab.TabId ||
            !workspace.IsPanelCropContextCurrent(context))
        {
            CancelPanelCrop();
            SetPanelCropStatus(GetLocalizedString(LocalizationKeys.ManualPanelCropStale));
            return;
        }

        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 ||
            point.X >= context.Width || point.Y >= context.Height)
        {
            SetPanelCropStatus(GetLocalizedString(LocalizationKeys.ManualPanelCropInsideImage));
            return;
        }

        if (_panelCropFirstCorner is null || _panelCropRectangle is not null)
        {
            _panelCropFirstCorner = point;
            _panelCropRectangle = null;
            _panelCropRectangleIsValid = false;
            SetPanelCropOverlay(null);
            SetPanelCropStatus(GetLocalizedString(LocalizationKeys.ManualPanelCropSecondCorner));
        }
        else
        {
            MarkerExemplarRectangle rectangle = CreatePanelCropRectangle(_panelCropFirstCorner.Value, point);
            _panelCropRectangle = rectangle;
            _panelCropRectangleIsValid = IsPanelCropRectangleValid(rectangle, context);
            SetPanelCropOverlay(rectangle);
            SetPanelCropStatus(GetLocalizedString(_panelCropRectangleIsValid
                ? LocalizationKeys.ManualPanelCropReview
                : LocalizationKeys.ManualPanelCropInvalid));
        }

        OnPropertyChanged(nameof(HasPanelCropRectangle));
        OnPropertyChanged(nameof(EditorInstruction));
        RelayCommand.RaiseCanExecuteChanged();
        AsyncRelayCommand.RaiseCanExecuteChanged();
    }

    public async Task CreatePanelCropAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || _panelCropContext is not { } context || _panelCropRectangle is not { } rectangle ||
            !IsPanelCropRectangleValid(rectangle, context) ||
            _workspaceService is not IPanelCropWorkspace { CanCreatePanelCrops: true } workspace ||
            SelectedTab is not { } parentTab || parentTab.TabId != context.TabId ||
            !workspace.IsPanelCropContextCurrent(context))
        {
            SetPanelCropStatus(GetLocalizedString(LocalizationKeys.ManualPanelCropStale));
            return;
        }

        _panelCropCreating = true;
        try
        {
            await RunBusyAsync(async token =>
            {
                try
                {
                    WorkspaceTabViewModel cropped = await workspace.CreatePanelCropAsync(context, rectangle, token);
                    bool keepCurrentSelection = ReferenceEquals(SelectedTab, parentTab) &&
                        ReferenceEquals(_panelCropContext, context);
                    _panelCropCreating = false;
                    if (keepCurrentSelection)
                    {
                        ClearPanelCropState();
                        EditorMode = ManualEditorMode.Select;
                        AddImportedTabs([cropped]);
                    }
                    else
                    {
                        // The workspace registered the child before returning. Keep it visible
                        // without stealing focus if the user changed tabs while creation finished.
                        AddImportedTabs([cropped], selectLast: false);
                    }
                    SetPanelCropStatus(GetLocalizedString(LocalizationKeys.ManualPanelCropCreated));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (InvalidOperationException)
                {
                    CancelPanelCrop();
                    SetPanelCropStatus(GetLocalizedString(LocalizationKeys.ManualPanelCropStale));
                }
            }, cancellationToken);
        }
        finally
        {
            _panelCropCreating = false;
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    private bool CanCreatePanelCrop() => !IsBusy && _panelCropContext is { } context &&
        _panelCropRectangle is { } rectangle && _panelCropRectangleIsValid &&
        _workspaceService is IPanelCropWorkspace workspace && SelectedTab?.TabId == context.TabId &&
        IsPanelCropRectangleValid(rectangle, context) && workspace.IsPanelCropContextCurrent(context);

    private static MarkerExemplarRectangle CreatePanelCropRectangle(Point first, Point second)
    {
        int x1 = (int)Math.Floor(Math.Min(first.X, second.X));
        int y1 = (int)Math.Floor(Math.Min(first.Y, second.Y));
        int x2 = (int)Math.Floor(Math.Max(first.X, second.X));
        int y2 = (int)Math.Floor(Math.Max(first.Y, second.Y));
        return new(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
    }

    private static bool IsPanelCropRectangleValid(MarkerExemplarRectangle rectangle, PanelCropContext context) =>
        rectangle.X >= 0 && rectangle.Y >= 0 && rectangle.Width >= PanelCropLimits.MinimumSide &&
        rectangle.Height >= PanelCropLimits.MinimumSide && rectangle.Width <= PanelCropLimits.MaximumSide &&
        rectangle.Height <= PanelCropLimits.MaximumSide &&
        (long)rectangle.Width * rectangle.Height <= PanelCropLimits.MaximumPixels &&
        (long)rectangle.X + rectangle.Width <= context.Width &&
        (long)rectangle.Y + rectangle.Height <= context.Height;

    private void CancelPanelCrop()
    {
        if (_panelCropCreating) _activeOperation?.Cancel();
        ClearPanelCropState();
        if (EditorMode == ManualEditorMode.PanelCrop) EditorMode = ManualEditorMode.Select;
        OnPropertyChanged(nameof(IsPanelCropActive));
        OnPropertyChanged(nameof(EditorInstruction));
        RelayCommand.RaiseCanExecuteChanged();
        AsyncRelayCommand.RaiseCanExecuteChanged();
    }

    private void ClearPanelCropState()
    {
        _panelCropContext = null;
        _panelCropFirstCorner = null;
        _panelCropRectangle = null;
        _panelCropRectangleIsValid = false;
        SetPanelCropOverlay(null);
        OnPropertyChanged(nameof(HasPanelCropRectangle));
    }

    private void SetPanelCropOverlay(MarkerExemplarRectangle? rectangle)
    {
        if (SelectedTab?.OverlayContent is ManualReviewOverlay overlay)
            overlay.SetPanelCrop(rectangle, _panelCropRectangleIsValid);
    }

    private void SetPanelCropStatus(string status)
    {
        _panelCropStatusText = status;
        OnPropertyChanged(nameof(PanelCropStatusText));
        SetStatus(status);
    }

    private void OnPanelCropModeExited()
    {
        if (_panelCropCreating)
        {
            _activeOperation?.Cancel();
        }
        ClearPanelCropState();
        OnPropertyChanged(nameof(IsPanelCropActive));
    }
}
