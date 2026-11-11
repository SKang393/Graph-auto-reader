// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GraphReader.App.Models;
using GraphReader.App.Services;
using GraphReader.Domain;
using AppGraphPoint = GraphReader.App.Models.GraphPoint;

namespace GraphReader.App.ViewModels;

public enum WorkflowStage
{
    Import,
    Prepare,
    Detect,
    Review,
    Export,
}

public sealed class MagnifierViewModel : ObservableObject
{
    private bool _isEnhanced;
    private bool _isCrosshairVisible;
    private Point _crosshairPosition = new(0.5, 0.5);
    private Point _pixelPosition;
    private Point? _graphPosition;
    private string? _nearestDetectionName;
    private double? _nearestDetectionConfidence;
    private double _zoomLevel = 3;

    public bool IsEnhanced
    {
        get => _isEnhanced;
        set => SetProperty(ref _isEnhanced, value);
    }

    public bool IsCrosshairVisible
    {
        get => _isCrosshairVisible;
        set => SetProperty(ref _isCrosshairVisible, value);
    }

    public Point CrosshairPosition
    {
        get => _crosshairPosition;
        set => SetProperty(ref _crosshairPosition, value);
    }

    public Point PixelPosition
    {
        get => _pixelPosition;
        set => SetProperty(ref _pixelPosition, value);
    }

    public Point? GraphPosition
    {
        get => _graphPosition;
        set => SetProperty(ref _graphPosition, value);
    }

    public string? NearestDetectionName
    {
        get => _nearestDetectionName;
        set => SetProperty(ref _nearestDetectionName, value);
    }

    public double? NearestDetectionConfidence
    {
        get => _nearestDetectionConfidence;
        set => SetProperty(ref _nearestDetectionConfidence, value);
    }

    public double ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            SetProperty(ref _zoomLevel, value);
        }
    }
}

public sealed class SeriesCardViewModel : ObservableObject
{
    private readonly ObservableCollection<AppGraphPoint> _points;
    private bool _isVisible = true;
    private string _symbol;
    private string _accessibleName;
    private string _label;
    private MarkerShape _shape;
    private MarkerFill _fill;
    private SemanticRole _semanticRole;
    private Action<string>? _selectAction;

    public SeriesCardViewModel(
        string seriesId,
        string symbol,
        string accessibleName,
        string label,
        double confidence,
        ObservableCollection<AppGraphPoint> points,
        MarkerShape shape = MarkerShape.Other,
        MarkerFill fill = MarkerFill.Unknown,
        SemanticRole semanticRole = SemanticRole.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessibleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence));
        }

        SeriesId = seriesId;
        _symbol = symbol;
        _accessibleName = accessibleName;
        _label = label;
        Confidence = confidence;
        _shape = shape;
        _fill = fill;
        _semanticRole = semanticRole;
        _points = points ?? throw new ArgumentNullException(nameof(points));
        SelectCommand = new RelayCommand(_ => _selectAction?.Invoke(SeriesId));
    }

    public string SeriesId { get; }

    public string Symbol
    {
        get => _symbol;
        internal set => SetProperty(ref _symbol, value);
    }

    public string AccessibleName
    {
        get => _accessibleName;
        internal set => SetProperty(ref _accessibleName, value);
    }

    public string Label
    {
        get => _label;
        internal set => SetProperty(ref _label, value);
    }

    public MarkerShape Shape
    {
        get => _shape;
        internal set => SetProperty(ref _shape, value);
    }

    public MarkerFill Fill
    {
        get => _fill;
        internal set => SetProperty(ref _fill, value);
    }

    public SemanticRole SemanticRole
    {
        get => _semanticRole;
        internal set => SetProperty(ref _semanticRole, value);
    }

    public int Count => _points.Count(point => point.SeriesId == SeriesId);

    public double Confidence { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public ICommand SelectCommand { get; }

    internal void SetSelectAction(Action<string> selectAction)
    {
        _selectAction = selectAction ?? throw new ArgumentNullException(nameof(selectAction));
    }

    internal void NotifyCountChanged() => OnPropertyChanged(nameof(Count));
}

public sealed class WorkspaceTabViewModel : ObservableObject
{
    private object? _overlayContent;
    private double _zoomLevel = 1;
    private bool _isDirty;
    private bool _analysisNeedsReview;
    private ImageSource? _enhancedImageSource;
    private EnhancementPreviewMode _enhancementPreviewMode;

    public WorkspaceTabViewModel(
        string tabId,
        string displayName,
        ObservableCollection<AppGraphPoint> points,
        ObservableCollection<SeriesCardViewModel> seriesCards,
        ImageSource? imageSource,
        ImageSource? enhancedImageSource,
        object? phaseOverlayContent,
        string? panelId = null,
        string? sourceId = null,
        string? sourcePath = null,
        string? sourceSha256 = null,
        int pixelWidth = 0,
        int pixelHeight = 0,
        ManualCalibrationState? calibration = null,
        ObservableCollection<EditablePhaseDivider>? phaseDividers = null,
        object? overlayContent = null,
        int? pageNumber = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (pageNumber is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        TabId = tabId;
        DisplayName = displayName;
        Points = points ?? throw new ArgumentNullException(nameof(points));
        SeriesCards = seriesCards ?? throw new ArgumentNullException(nameof(seriesCards));
        ImageSource = imageSource;
        _enhancedImageSource = enhancedImageSource;
        _overlayContent = overlayContent;
        PhaseOverlayContent = phaseOverlayContent;
        PanelId = panelId;
        SourceId = sourceId;
        SourcePath = sourcePath;
        SourceSha256 = sourceSha256;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        PageNumber = pageNumber;
        Calibration = calibration;
        PhaseDividers = phaseDividers ?? [];
    }

    public string TabId { get; }

    public string DisplayName { get; }

    public string TabHeader => $"{DisplayName}{(AnalysisNeedsReview ? " !" : "")}{(IsDirty ? " *" : "")}";

    public bool AnalysisNeedsReview
    {
        get => _analysisNeedsReview;
        internal set
        {
            if (SetProperty(ref _analysisNeedsReview, value))
                OnPropertyChanged(nameof(TabHeader));
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        internal set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(TabHeader));
            }
        }
    }

    public ObservableCollection<AppGraphPoint> Points { get; }

    public ObservableCollection<SeriesCardViewModel> SeriesCards { get; }

    public ImageSource? ImageSource { get; }

    public ImageSource? EnhancedImageSource
    {
        get => _enhancedImageSource;
        internal set
        {
            if (SetProperty(ref _enhancedImageSource, value))
            {
                OnPropertyChanged(nameof(HasEnhancedPreview));
                OnPropertyChanged(nameof(DisplayImageSource));
                OnPropertyChanged(nameof(ComparisonImageSource));
                if (value is null && EnhancementPreviewMode != EnhancementPreviewMode.Original)
                {
                    EnhancementPreviewMode = EnhancementPreviewMode.Original;
                }
            }
        }
    }

    public bool HasEnhancedPreview => EnhancedImageSource is not null;

    public EnhancementPreviewMode EnhancementPreviewMode
    {
        get => _enhancementPreviewMode;
        internal set
        {
            EnhancementPreviewMode normalized = value != EnhancementPreviewMode.Original &&
                EnhancedImageSource is null
                    ? EnhancementPreviewMode.Original
                    : value;
            if (SetProperty(ref _enhancementPreviewMode, normalized))
            {
                OnPropertyChanged(nameof(DisplayImageSource));
                OnPropertyChanged(nameof(ComparisonImageSource));
                OnPropertyChanged(nameof(IsComparisonPreview));
            }
        }
    }

    public ImageSource? DisplayImageSource =>
        EnhancementPreviewMode == EnhancementPreviewMode.Enhanced && EnhancedImageSource is not null
            ? EnhancedImageSource
            : ImageSource;

    public ImageSource? ComparisonImageSource =>
        EnhancementPreviewMode == EnhancementPreviewMode.Comparison ? EnhancedImageSource : null;

    public bool IsComparisonPreview =>
        EnhancementPreviewMode == EnhancementPreviewMode.Comparison && EnhancedImageSource is not null;

    public object? OverlayContent => _overlayContent;

    internal void SetOverlayContent(object? overlayContent)
    {
        if (!ReferenceEquals(_overlayContent, overlayContent))
        {
            _overlayContent = overlayContent;
            OnPropertyChanged(nameof(OverlayContent));
        }
    }

    public object? PhaseOverlayContent { get; }

    public string? PanelId { get; }

    public string? SourceId { get; }

    public string? SourcePath { get; }

    public string? SourceSha256 { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public int? PageNumber { get; }

    public ManualCalibrationState? Calibration { get; internal set; }

    public ObservableCollection<EditablePhaseDivider> PhaseDividers { get; }

    public double ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            SetProperty(ref _zoomLevel, value);
        }
    }
}

public sealed record MovePointRequest(string PointId, double PixelX, double PixelY);

public sealed record SeriesPairRequest(string SourceSeriesId, string TargetSeriesId);

public sealed record SplitSeriesRequest(string SourceSeriesId, IReadOnlyCollection<string> PointIds);

public sealed record ReassignPointRequest(string PointId, string TargetSeriesId);
