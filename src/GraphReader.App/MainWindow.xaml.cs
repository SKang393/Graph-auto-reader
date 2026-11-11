// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Threading;
using GraphReader.App.Appearance;
using GraphReader.App.Controls;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;

namespace GraphReader.App;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> SupportedDropExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".jpeg", ".jpg", ".pdf", ".png", ".tif", ".tiff",
    };
    private const double DefaultSeriesWidth = 272;
    private const double DefaultInspectorWidth = 384;
    private const double MaximumSeriesWidth = 560;
    private const double MaximumInspectorWidth = 800;
    private const double MinimumSeriesWidth = 220;
    private const double MinimumInspectorWidth = 340;
    private const string SeriesLayoutFileName = "series-pane-width.txt";
    private const string WindowLayoutFileName = "window-layout.txt";
    private readonly IThemeService? _themeService;
    private readonly DispatcherTimer _autosaveTimer;
    private readonly string? _seriesLayoutPath;
    private readonly string? _windowLayoutPath;
    private double _lastSeriesWidth;
    private double _lastInspectorWidth;

    public MainWindow()
        : this((Application.Current as App)?.AvailableThemeService)
    {
    }

    public MainWindow(IThemeService? themeService)
        : this(themeService, windowResources: null)
    {
    }

    internal MainWindow(IThemeService? themeService, ResourceDictionary? windowResources)
    {
        _themeService = themeService;
        if (windowResources is not null)
        {
            Resources = windowResources;
        }
        InitializeComponent();
        _seriesLayoutPath = ResolveLayoutPath(SeriesLayoutFileName);
        _windowLayoutPath = ResolveLayoutPath(WindowLayoutFileName);
        _lastSeriesWidth = ReadSeriesWidth(_seriesLayoutPath, DefaultSeriesWidth);
        _lastInspectorWidth = ReadInspectorWidth(_windowLayoutPath, DefaultInspectorWidth);
        SeriesColumn.Width = new GridLength(_lastSeriesWidth);
        InspectorColumn.Width = new GridLength(_lastInspectorWidth);
        DataContextChanged += OnDataContextChanged;
        App? application = Application.Current as App;
        DataContext = new MainWindowViewModel(
            application?.WorkspaceService ?? new UnavailableWorkspaceService(),
            application?.LocalizationService,
            application?.StartupErrorMessageKey,
            new WindowsWorkspaceDialogService(application?.LocalizationService),
            ResolveLayoutPath("recent-projects.txt"));
        _autosaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _autosaveTimer.Tick += OnAutosaveTimerTick;
        _autosaveTimer.Start();
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.OldValue is INotifyPropertyChanged previous)
        {
            previous.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is MainWindowViewModel current)
        {
            current.PropertyChanged += OnViewModelPropertyChanged;
            _themeService?.ApplyTheme(current.AppearanceMode);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.AppearanceMode)
            && sender is MainWindowViewModel viewModel)
        {
            _themeService?.ApplyTheme(viewModel.AppearanceMode);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedTab))
        {
            Dispatcher.BeginInvoke(GraphCanvasHost.ResetView, DispatcherPriority.Loaded);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && !viewModel.CanCloseWindow())
        {
            e.Cancel = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        WriteSeriesWidth(_seriesLayoutPath, SeriesColumn.ActualWidth >= MinimumSeriesWidth
            ? SeriesColumn.ActualWidth
            : _lastSeriesWidth);
        WriteInspectorWidth(_windowLayoutPath, InspectorColumn.ActualWidth >= MinimumInspectorWidth
            ? InspectorColumn.ActualWidth
            : _lastInspectorWidth);
        _autosaveTimer.Stop();
        _autosaveTimer.Tick -= OnAutosaveTimerTick;
        DataContextChanged -= OnDataContextChanged;
        if (DataContext is IDisposable disposable)
        {
            if (DataContext is INotifyPropertyChanged current)
            {
                current.PropertyChanged -= OnViewModelPropertyChanged;
            }

            disposable.Dispose();
        }
    }

    private async void OnAutosaveTimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.RunTimerAutosaveAsync();
        }
    }

    private async void OnGraphImagePointInvoked(object sender, GraphImagePointEventArgs e)
    {
        _ = sender;
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.HandleCanvasPointAsync(e.ImagePoint);
        }
    }

    private void OnGraphImagePointNavigated(object sender, GraphImagePointEventArgs e)
    {
        _ = sender;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.HandleCanvasNavigation(e.ImagePoint);
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.BeginInvoke(GraphCanvasHost.RecalculateViewport, DispatcherPriority.Loaded);
    }

    private void OnFitGraphClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        GraphCanvasHost.ResetView();
    }

    private void OnResetViewClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        GraphCanvasHost.ResetView();
    }

    private void OnToggleSeriesPaneClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (SeriesColumn.ActualWidth >= MinimumSeriesWidth)
        {
            _lastSeriesWidth = SeriesColumn.ActualWidth;
            SeriesPane.Visibility = Visibility.Collapsed;
            SeriesColumn.MinWidth = 0;
            SeriesColumn.Width = new GridLength(0);
        }
        else
        {
            SeriesColumn.MinWidth = 240;
            SeriesColumn.Width = new GridLength(Math.Clamp(_lastSeriesWidth, MinimumSeriesWidth, MaximumSeriesWidth));
            SeriesPane.Visibility = Visibility.Visible;
        }
    }

    private void OnToggleInspectorPaneClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (InspectorColumn.ActualWidth >= MinimumInspectorWidth)
        {
            _lastInspectorWidth = InspectorColumn.ActualWidth;
            InspectorPane.Visibility = Visibility.Collapsed;
            InspectorColumn.MinWidth = 0;
            InspectorColumn.Width = new GridLength(0);
        }
        else
        {
            InspectorColumn.MinWidth = MinimumInspectorWidth;
            InspectorColumn.Width = new GridLength(Math.Clamp(_lastInspectorWidth, MinimumInspectorWidth, MaximumInspectorWidth));
            InspectorPane.Visibility = Visibility.Visible;
        }
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        _ = sender;
        e.Effects = GetSupportedDroppedPaths(e.Data).Length > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFilesDropped(object sender, DragEventArgs e)
    {
        _ = sender;
        string[] paths = GetSupportedDroppedPaths(e.Data);
        e.Handled = true;
        if (paths.Length > 0 && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ImportPathsAsync(paths);
        }
    }

    private static string[] GetSupportedDroppedPaths(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return Array.Empty<string>();
        }

        return paths
            .Where(path => File.Exists(path) && SupportedDropExtensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static double ReadInspectorWidth(string? path, double fallback)
    {
        return ReadPaneWidth(path, fallback, MinimumInspectorWidth, MaximumInspectorWidth);
    }

    private static double ReadSeriesWidth(string? path, double fallback)
    {
        return ReadPaneWidth(path, fallback, MinimumSeriesWidth, MaximumSeriesWidth);
    }

    private static double ReadPaneWidth(string? path, double fallback, double minimum, double maximum)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return fallback;
        }

        try
        {
            string value = File.ReadAllText(path).Trim();
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double width)
                && double.IsFinite(width)
                ? Math.Clamp(width, minimum, maximum)
                : fallback;
        }
        catch (Exception exception) when (IsExpectedSettingsException(exception))
        {
            return fallback;
        }
    }

    private static void WriteInspectorWidth(string? path, double width)
    {
        WritePaneWidth(path, width, MinimumInspectorWidth, MaximumInspectorWidth);
    }

    private static void WriteSeriesWidth(string? path, double width)
    {
        WritePaneWidth(path, width, MinimumSeriesWidth, MaximumSeriesWidth);
    }

    private static void WritePaneWidth(string? path, double width, double minimum, double maximum)
    {
        if (string.IsNullOrWhiteSpace(path) || !double.IsFinite(width) || width < minimum)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                path,
                Math.Clamp(width, minimum, maximum)
                    .ToString("R", CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (IsExpectedSettingsException(exception))
        {
            // Layout persistence is intentionally fail-soft and cannot block the workspace.
        }
    }

    private static bool IsExpectedSettingsException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;

    private static string? ResolveLayoutPath(string fileName)
    {
        string? settingsRoot = (Application.Current as App)?.ApplicationPaths?.SettingsRoot;
        return string.IsNullOrWhiteSpace(settingsRoot)
            ? null
            : Path.Combine(settingsRoot, fileName);
    }
}
