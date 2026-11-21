// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GraphReader.App.Controls;

public partial class GraphCanvasControl : UserControl
{
    public static readonly DependencyProperty ImageSourceProperty = DependencyProperty.Register(
        nameof(ImageSource),
        typeof(ImageSource),
        typeof(GraphCanvasControl),
        new PropertyMetadata(null, OnPresentationChanged));

    public static readonly DependencyProperty CoordinateReferenceSourceProperty = DependencyProperty.Register(
        nameof(CoordinateReferenceSource),
        typeof(ImageSource),
        typeof(GraphCanvasControl),
        new PropertyMetadata(null, OnPresentationChanged));

    public static readonly DependencyProperty ComparisonImageSourceProperty = DependencyProperty.Register(
        nameof(ComparisonImageSource),
        typeof(ImageSource),
        typeof(GraphCanvasControl),
        new PropertyMetadata(null, OnPresentationChanged));

    public static readonly DependencyProperty IsComparisonVisibleProperty = DependencyProperty.Register(
        nameof(IsComparisonVisible),
        typeof(bool),
        typeof(GraphCanvasControl),
        new PropertyMetadata(false, OnPresentationChanged));

    public static readonly DependencyProperty OverlayContentProperty = DependencyProperty.Register(
        nameof(OverlayContent),
        typeof(object),
        typeof(GraphCanvasControl));

    public static readonly DependencyProperty PhaseOverlayContentProperty = DependencyProperty.Register(
        nameof(PhaseOverlayContent),
        typeof(object),
        typeof(GraphCanvasControl));

    public static readonly DependencyProperty PhaseOverlayVisibleProperty = DependencyProperty.Register(
        nameof(PhaseOverlayVisible),
        typeof(bool),
        typeof(GraphCanvasControl),
        new FrameworkPropertyMetadata(
            true,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnPresentationChanged));

    public static readonly DependencyProperty ShowCrosshairProperty = DependencyProperty.Register(
        nameof(ShowCrosshair),
        typeof(bool),
        typeof(GraphCanvasControl),
        new PropertyMetadata(false, OnPresentationChanged));

    public static readonly DependencyProperty CrosshairPositionProperty = DependencyProperty.Register(
        nameof(CrosshairPosition),
        typeof(Point),
        typeof(GraphCanvasControl),
        new PropertyMetadata(new Point(0.5, 0.5)));

    public static readonly DependencyProperty ZoomLevelProperty = DependencyProperty.Register(
        nameof(ZoomLevel),
        typeof(double),
        typeof(GraphCanvasControl),
        new FrameworkPropertyMetadata(
            1d,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnZoomLevelChanged),
        IsValidZoomLevel);

    private const double MinimumZoomLevel = 0.25;
    private const double MaximumZoomLevel = 8;
    private const double ZoomInFactor = 1.25;
    private const double ZoomOutFactor = 0.8;
    private double _fitScale = 1;

    public GraphCanvasControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
        EditableImageCoordinateSurface.MouseLeftButtonDown += OnImageMouseLeftButtonDown;
    }

    public event EventHandler<GraphImagePointEventArgs>? ImagePointInvoked;

    public event EventHandler<GraphImagePointEventArgs>? ImagePointNavigated;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new GraphCanvasControlAutomationPeer(this);

    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    /// <summary>Gets or sets the immutable original image used to define public image coordinates.</summary>
    public ImageSource? CoordinateReferenceSource
    {
        get => (ImageSource?)GetValue(CoordinateReferenceSourceProperty);
        set => SetValue(CoordinateReferenceSourceProperty, value);
    }

    public ImageSource? ComparisonImageSource
    {
        get => (ImageSource?)GetValue(ComparisonImageSourceProperty);
        set => SetValue(ComparisonImageSourceProperty, value);
    }

    public bool IsComparisonVisible
    {
        get => (bool)GetValue(IsComparisonVisibleProperty);
        set => SetValue(IsComparisonVisibleProperty, value);
    }

    public object? OverlayContent
    {
        get => GetValue(OverlayContentProperty);
        set => SetValue(OverlayContentProperty, value);
    }

    public object? PhaseOverlayContent
    {
        get => GetValue(PhaseOverlayContentProperty);
        set => SetValue(PhaseOverlayContentProperty, value);
    }

    public bool PhaseOverlayVisible
    {
        get => (bool)GetValue(PhaseOverlayVisibleProperty);
        set => SetValue(PhaseOverlayVisibleProperty, value);
    }

    public bool ShowCrosshair
    {
        get => (bool)GetValue(ShowCrosshairProperty);
        set => SetValue(ShowCrosshairProperty, value);
    }

    /// <summary>Gets or sets the normalized canvas location of the crosshair.</summary>
    public Point CrosshairPosition
    {
        get => (Point)GetValue(CrosshairPositionProperty);
        set => SetValue(CrosshairPositionProperty, value);
    }

    public double ZoomLevel
    {
        get => (double)GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, value);
    }

    /// <summary>Gets the scale required to fit the image inside the current viewport.</summary>
    public double FitScale => _fitScale;

    /// <summary>Gets the effective image scale after fit and user zoom are combined.</summary>
    public double EffectiveScale => _fitScale * ZoomLevel;

    /// <summary>Recomputes the fit scale for the current image and viewport.</summary>
    public void RecalculateViewport()
    {
        if (!IsInitialized)
        {
            return;
        }

        (double imageWidth, double imageHeight) = GetCoordinateDimensions(
            CoordinateReferenceSource ?? ImageSource);
        double presentationWidth = IsComparisonVisible && ComparisonImageSource is not null
            ? (imageWidth * 2) + 16
            : imageWidth;
        double viewportWidth = Math.Max(0, ViewportScrollViewer.ActualWidth - 2);
        double viewportHeight = Math.Max(0, ViewportScrollViewer.ActualHeight - 2);

        if (presentationWidth <= 0 || imageHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
        {
            _fitScale = 1;
        }
        else
        {
            _fitScale = Math.Min(viewportWidth / presentationWidth, viewportHeight / imageHeight);
        }

        ViewportScaleTransform.ScaleX = EffectiveScale;
        ViewportScaleTransform.ScaleY = EffectiveScale;
        CanvasSurface.MinWidth = ImageSource is null ? viewportWidth : 0;
        CanvasSurface.MinHeight = ImageSource is null ? viewportHeight : 0;
    }

    /// <summary>Applies one button-equivalent relative zoom step.</summary>
    public void ZoomBy(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        SetCurrentValue(
            ZoomLevelProperty,
            Math.Clamp(ZoomLevel * factor, MinimumZoomLevel, MaximumZoomLevel));
    }

    /// <summary>Returns the image to fit scale and resets scroll offsets.</summary>
    public void ResetView()
    {
        SetCurrentValue(ZoomLevelProperty, 1d);
        RecalculateViewport();
        ViewportScrollViewer.ScrollToHorizontalOffset(0);
        ViewportScrollViewer.ScrollToVerticalOffset(0);
    }

    private static bool IsValidZoomLevel(object value) =>
        value is double zoom && double.IsFinite(zoom) && zoom > 0;

    private static void OnPresentationChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        _ = args;
        ((GraphCanvasControl)dependencyObject).RefreshPresentation();
    }

    private static void OnZoomLevelChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        _ = args;
        ((GraphCanvasControl)dependencyObject).RecalculateViewport();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _ = sender;
        _ = args;
        RefreshPresentation();
        RecalculateViewport();
    }

    private void RefreshPresentation()
    {
        if (!IsInitialized)
        {
            return;
        }

        EmptyStateText.Visibility = ImageSource is null ? Visibility.Visible : Visibility.Collapsed;
        ImageCoordinateSurface.Visibility = ImageSource is null ? Visibility.Collapsed : Visibility.Visible;
        (double coordinateWidth, double coordinateHeight) = GetCoordinateDimensions(
            CoordinateReferenceSource ?? ImageSource);
        EditableImageCoordinateSurface.Width = coordinateWidth;
        EditableImageCoordinateSurface.Height = coordinateHeight;
        ComparisonImage.Width = coordinateWidth;
        ComparisonImage.Height = coordinateHeight;
        ComparisonPane.Visibility = IsComparisonVisible && ComparisonImageSource is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        PhaseOverlayPresenter.Visibility = PhaseOverlayVisible ? Visibility.Visible : Visibility.Collapsed;
        Crosshair.Visibility = ShowCrosshair ? Visibility.Visible : Visibility.Collapsed;
        RecalculateViewport();
        Dispatcher.BeginInvoke(RecalculateViewport, DispatcherPriority.Loaded);
    }

    private static (double Width, double Height) GetCoordinateDimensions(ImageSource? source) =>
        source switch
        {
            BitmapSource bitmap when bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0 =>
                (bitmap.PixelWidth, bitmap.PixelHeight),
            not null when source.Width > 0 && source.Height > 0 =>
                (source.Width, source.Height),
            _ => (0d, 0d),
        };

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        RecalculateViewport();
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _ = sender;
        if (ImageSource is null || e.Delta == 0)
        {
            return;
        }

        ZoomAtViewportPoint(e.Delta > 0 ? ZoomInFactor : ZoomOutFactor, e.GetPosition(ViewportScrollViewer));
        e.Handled = true;
    }

    internal void ZoomAtViewportPoint(double factor, Point pointer)
    {
        Point anchor = ViewportScrollViewer.TranslatePoint(pointer, ImageCoordinateSurface);
        double previousZoom = ZoomLevel;
        ZoomBy(factor);
        if (ZoomLevel == previousZoom || !new Rect(ImageCoordinateSurface.RenderSize).Contains(anchor))
        {
            return;
        }

        // Layout determines both the scaled image origin and the scrollable extent.
        ViewportScrollViewer.UpdateLayout();
        Point scaled = ImageCoordinateSurface.TranslatePoint(anchor, ViewportScrollViewer);
        ViewportScrollViewer.ScrollToHorizontalOffset(ViewportScrollViewer.HorizontalOffset + scaled.X - pointer.X);
        ViewportScrollViewer.ScrollToVerticalOffset(ViewportScrollViewer.VerticalOffset + scaled.Y - pointer.Y);
        // ScrollViewer queues offset changes. Apply them before another wheel event reads the anchor.
        ViewportScrollViewer.UpdateLayout();
    }

    private void OnImageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (ImageSource is null)
        {
            return;
        }

        Point point = e.GetPosition(GraphImage);
        if (point.X < 0 || point.Y < 0 || point.X > GraphImage.ActualWidth || point.Y > GraphImage.ActualHeight)
        {
            return;
        }

        ImagePointInvoked?.Invoke(this, new GraphImagePointEventArgs(point));
        e.Handled = true;
    }

    private sealed class GraphCanvasControlAutomationPeer(GraphCanvasControl owner)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Pane;

        protected override string GetClassNameCore() => nameof(GraphCanvasControl);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        e.Handled = TryHandleKeyboardInput(e.Key, Keyboard.Modifiers);
    }

    internal bool TryHandleKeyboardInput(Key key, ModifierKeys modifiers)
    {
        (double width, double height) = GetCoordinateDimensions(CoordinateReferenceSource ?? ImageSource);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        if (key is Key.Enter or Key.Space)
        {
            ImagePointInvoked?.Invoke(this, new GraphImagePointEventArgs(ToImagePoint(width, height)));
            return true;
        }

        double step = modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        double x = CrosshairPosition.X;
        double y = CrosshairPosition.Y;
        switch (key)
        {
            case Key.Left:
                x -= step / width;
                break;
            case Key.Right:
                x += step / width;
                break;
            case Key.Up:
                y -= step / height;
                break;
            case Key.Down:
                y += step / height;
                break;
            case Key.Home:
                x = 0.5;
                y = 0.5;
                break;
            default:
                return false;
        }

        SetCurrentValue(
            CrosshairPositionProperty,
            new Point(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)));
        SetCurrentValue(ShowCrosshairProperty, true);
        ImagePointNavigated?.Invoke(this, new GraphImagePointEventArgs(ToImagePoint(width, height)));
        return true;
    }

    private Point ToImagePoint(double width, double height) =>
        new(
            Math.Clamp(CrosshairPosition.X, 0, 1) * width,
            Math.Clamp(CrosshairPosition.Y, 0, 1) * height);
}

public sealed class GraphImagePointEventArgs(Point imagePoint) : EventArgs
{
    public Point ImagePoint { get; } = imagePoint;
}
