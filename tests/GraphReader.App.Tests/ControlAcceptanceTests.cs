// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using GraphReader.App.Controls;
using GraphReader.App.Models;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ControlAcceptanceTests
{
    [TestMethod]
    public void EditingControlPropertiesHaveSafeDefaultsAndTwoWayMetadata()
    {
        StaTestHost.Run(
            () =>
            {
                var canvas = new GraphCanvasControl();
                var magnifier = new MagnifierControl();
                var series = new SeriesCardControl();

                Assert.IsTrue(canvas.PhaseOverlayVisible);
                Assert.IsFalse(canvas.ShowCrosshair);
                Assert.IsFalse(canvas.IsComparisonVisible);
                Assert.IsNull(canvas.CoordinateReferenceSource);
                Assert.IsNull(canvas.ComparisonImageSource);
                Assert.AreEqual(1d, canvas.ZoomLevel);
                Assert.AreEqual(new Point(0.5, 0.5), canvas.CrosshairPosition);
                Assert.IsTrue(((FrameworkPropertyMetadata)GraphCanvasControl.PhaseOverlayVisibleProperty.GetMetadata(typeof(GraphCanvasControl))).BindsTwoWayByDefault);

                Assert.IsFalse(magnifier.ShowEnhanced);
                Assert.IsTrue(magnifier.ShowCrosshair);
                Assert.AreEqual(2d, magnifier.ZoomLevel);
                Assert.IsTrue(((FrameworkPropertyMetadata)MagnifierControl.ShowEnhancedProperty.GetMetadata(typeof(MagnifierControl))).BindsTwoWayByDefault);

                Assert.AreEqual(string.Empty, series.SymbolGlyph);
                Assert.AreEqual(0, series.MarkerCount);
                Assert.AreEqual(0d, series.Confidence);
                Assert.IsTrue(series.IsSeriesVisible);
                Assert.IsTrue(((FrameworkPropertyMetadata)SeriesCardControl.IsSeriesVisibleProperty.GetMetadata(typeof(SeriesCardControl))).BindsTwoWayByDefault);
            });
    }

    [TestMethod]
    public void CanvasKeyboardMovesCursorInPixelStepsAndInvokesCurrentEditAction()
    {
        StaTestHost.Run(
            () =>
            {
                var canvas = new GraphCanvasControl
                {
                    ImageSource = new WriteableBitmap(100, 50, 96, 96, PixelFormats.Bgra32, null),
                    CrosshairPosition = new Point(0.5, 0.5),
                };
                Point? navigated = null;
                Point? invoked = null;
                canvas.ImagePointNavigated += (_, args) => navigated = args.ImagePoint;
                canvas.ImagePointInvoked += (_, args) => invoked = args.ImagePoint;

                Assert.IsTrue(canvas.TryHandleKeyboardInput(System.Windows.Input.Key.Right, System.Windows.Input.ModifierKeys.None));
                Assert.AreEqual(new Point(0.51, 0.5), canvas.CrosshairPosition);
                Assert.AreEqual(new Point(51, 25), navigated);
                Assert.IsNull(invoked);
                Assert.IsTrue(canvas.ShowCrosshair);

                Assert.IsTrue(canvas.TryHandleKeyboardInput(System.Windows.Input.Key.Up, System.Windows.Input.ModifierKeys.Shift));
                Assert.AreEqual(new Point(0.51, 0.3), canvas.CrosshairPosition);
                Assert.AreEqual(new Point(51, 15), navigated);

                Assert.IsTrue(canvas.TryHandleKeyboardInput(System.Windows.Input.Key.Home, System.Windows.Input.ModifierKeys.None));
                Assert.AreEqual(new Point(0.5, 0.5), canvas.CrosshairPosition);
                Assert.AreEqual(new Point(50, 25), navigated);

                Assert.IsTrue(canvas.TryHandleKeyboardInput(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None));
                Assert.AreEqual(new Point(50, 25), invoked);
                Assert.IsFalse(canvas.TryHandleKeyboardInput(System.Windows.Input.Key.F1, System.Windows.Input.ModifierKeys.None));
            });
    }

    [TestMethod]
    public void SeriesCardBindingUpdatesCountAndAccessibleSymbolImmediately()
    {
        StaTestHost.Run(
            () =>
            {
                var source = new MutableCount(2);
                var card = new SeriesCardControl
                {
                    AccessibleSymbolName = "Filled circle",
                    SymbolGlyph = "●",
                    InferredLabel = "Intervention",
                };
                BindingOperations.SetBinding(
                    card,
                    SeriesCardControl.MarkerCountProperty,
                    new Binding(nameof(MutableCount.Count)) { Source = source });

                Assert.AreEqual(2, card.MarkerCount);
                source.Count = 5;

                Assert.AreEqual(5, card.MarkerCount);
                Assert.AreEqual("Filled circle", AutomationProperties.GetName(card));
            });
    }

    [TestMethod]
    public void CanvasTogglesPhaseAndCrosshairPresentationWithoutColorIdentity()
    {
        StaTestHost.Run(
            () =>
            {
                var canvas = new GraphCanvasControl
                {
                    PhaseOverlayContent = new PhaseDivider(0.5, 0.1, 0.9),
                    PhaseOverlayVisible = false,
                    ShowCrosshair = true,
                    CrosshairPosition = new Point(0.25, 0.75),
                };

                var phasePresenter = (ContentControl)canvas.FindName("PhaseOverlayPresenter");
                var crosshair = (CrosshairOverlay)canvas.FindName("Crosshair");

                Assert.AreEqual(Visibility.Collapsed, phasePresenter.Visibility);
                Assert.IsInstanceOfType<PhaseDivider>(phasePresenter.Content);
                Assert.AreEqual(Visibility.Visible, crosshair.Visibility);
                Assert.AreEqual(new Point(0.25, 0.75), crosshair.Position);
            });
    }

    [TestMethod]
    public void CanvasOverlaysShareUniformImageCoordinateSurface()
    {
        StaTestHost.Run(
            () =>
            {
                var canvas = new GraphCanvasControl
                {
                    ImageSource = new WriteableBitmap(520, 280, 96, 96, PixelFormats.Bgra32, null),
                    PhaseOverlayContent = new PhaseDivider(0.47, 0.1, 0.9),
                    ShowCrosshair = true,
                };

                canvas.Measure(new Size(520, 520));
                canvas.Arrange(new Rect(0, 0, 520, 520));
                canvas.UpdateLayout();

                var image = (Image)canvas.FindName("GraphImage");
                var coordinateSurface = (FrameworkElement)canvas.FindName("ImageCoordinateSurface");
                var phasePresenter = (ContentControl)canvas.FindName("PhaseOverlayPresenter");
                var crosshair = (CrosshairOverlay)canvas.FindName("Crosshair");

                Assert.AreEqual(520d, coordinateSurface.ActualWidth, 0.01);
                Assert.AreEqual(280d, coordinateSurface.ActualHeight, 0.01);
                Assert.AreEqual(image.ActualWidth, phasePresenter.ActualWidth, 0.01);
                Assert.AreEqual(image.ActualHeight, phasePresenter.ActualHeight, 0.01);
                Assert.AreEqual(HorizontalAlignment.Stretch, phasePresenter.HorizontalContentAlignment);
                Assert.AreEqual(VerticalAlignment.Stretch, phasePresenter.VerticalContentAlignment);
                var phaseOverlay = FindVisualDescendant<PhaseDividerOverlay>(phasePresenter);
                Assert.IsNotNull(phaseOverlay);
                Assert.AreEqual(image.ActualWidth, phaseOverlay.ActualWidth, 0.01);
                Assert.AreEqual(image.ActualHeight, phaseOverlay.ActualHeight, 0.01);
                Assert.AreEqual(image.ActualWidth, crosshair.ActualWidth, 0.01);
                Assert.AreEqual(image.ActualHeight, crosshair.ActualHeight, 0.01);
            });
    }

    [TestMethod]
    public void MagnifierSwitchesImageAndFormatsCoordinatesAndDetectionPlaceholder()
    {
        StaTestHost.Run(
            () =>
            {
                var magnifier = new MagnifierControl();
                magnifier.Resources.MergedDictionaries.Add(
                    new ResourceDictionary
                    {
                        Source = new Uri(
                            "/GraphReader.App;component/Localization/Resources.en-US.xaml",
                            UriKind.RelativeOrAbsolute),
                    });
                var original = new WriteableBitmap(8, 8, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                var enhanced = new WriteableBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);

                magnifier.OriginalImageSource = original;
                magnifier.EnhancedImageSource = enhanced;
                magnifier.PixelPosition = new Point(12.5, 42.25);
                magnifier.GraphPosition = new Point(3, 18.5);
                magnifier.NearestDetectionName = "Open circle";
                magnifier.NearestDetectionConfidence = 0.875;
                magnifier.ZoomLevel = 3;
                magnifier.ShowEnhanced = true;

                var viewportImage = (Image)magnifier.FindName("ViewportImage");
                Assert.AreSame(enhanced, viewportImage.Source);
                Assert.AreEqual("12.5, 42.25", ((TextBlock)magnifier.FindName("PixelPositionText")).Text);
                Assert.AreEqual("3, 18.5", ((TextBlock)magnifier.FindName("GraphPositionText")).Text);
                Assert.AreEqual("Open circle", ((TextBlock)magnifier.FindName("NearestDetectionText")).Text);
                Assert.AreEqual(Visibility.Collapsed, ((TextBlock)magnifier.FindName("NoDetectionText")).Visibility);

                magnifier.NearestDetectionName = null;
                Assert.AreEqual(Visibility.Visible, ((TextBlock)magnifier.FindName("NoDetectionText")).Visibility);
            });
    }

    [TestMethod]
    [DataRow(600, 160)]
    [DataRow(160, 600)]
    public void MagnifierCentersTheSelectedOriginalPixelAtTheDisplayedScale(int width, int height)
    {
        StaTestHost.Run(
            () =>
            {
                var magnifier = new MagnifierControl
                {
                    OriginalImageSource = new WriteableBitmap(width, height, 144, 144, PixelFormats.Bgra32, null),
                    EnhancedImageSource = new WriteableBitmap(width * 2, height * 2, 96, 96, PixelFormats.Bgra32, null),
                    CrosshairPosition = new Point(0.17, 0.83),
                    ZoomLevel = 2,
                };
                var image = (Image)magnifier.FindName("ViewportImage");
                AssertRegistration(286);

                magnifier.ShowEnhanced = true;
                magnifier.ZoomLevel = 4;
                magnifier.CrosshairPosition = new Point(0.995, 0.01);
                AssertRegistration(340);

                void AssertRegistration(double inspectorWidth)
                {
                    magnifier.Measure(new Size(inspectorWidth, 480));
                    magnifier.Arrange(new Rect(0, 0, inspectorWidth, 480));
                    magnifier.UpdateLayout();
                    var crosshair = FindVisualDescendant<CrosshairOverlay>(magnifier);
                    Assert.IsNotNull(crosshair);
                    Assert.IsTrue(crosshair.ActualWidth > 0 && crosshair.ActualHeight > 0);
                    Assert.AreEqual(new Point(0.5, 0.5), crosshair.Position);

                    var cursor = magnifier.CrosshairPosition;
                    var sample = new Point(cursor.X * image.ActualWidth, cursor.Y * image.ActualHeight);
                    var at = image.TranslatePoint(sample, crosshair);
                    Assert.AreEqual(crosshair.ActualWidth / 2, at.X, 1e-6);
                    Assert.AreEqual(crosshair.ActualHeight / 2, at.Y, 1e-6);

                    var onePixel = new Vector(image.ActualWidth / width, image.ActualHeight / height);
                    var step = image.TranslatePoint(sample + onePixel, crosshair) - at;
                    Assert.AreEqual(magnifier.ZoomLevel, step.X, 1e-6);
                    Assert.AreEqual(magnifier.ZoomLevel, step.Y, 1e-6);
                }
            });
    }

    [TestMethod]
    public void InvalidControlValuesAreRejected()
    {
        StaTestHost.Run(
            () =>
            {
                var canvas = new GraphCanvasControl();
                var magnifier = new MagnifierControl();
                var series = new SeriesCardControl();

                Assert.ThrowsExactly<ArgumentException>(() => canvas.ZoomLevel = 0);
                Assert.ThrowsExactly<ArgumentException>(() => magnifier.ZoomLevel = double.NaN);
                Assert.ThrowsExactly<ArgumentException>(() => series.Confidence = -0.01);
                Assert.ThrowsExactly<ArgumentException>(() => series.Confidence = 1.01);
            });
    }

    [TestMethod]
    public void ComparisonUsesOriginalCoordinateSizeAndKeepsEditingOnOriginalPane()
    {
        StaTestHost.Run(
            () =>
            {
                var original = new WriteableBitmap(400, 200, 192, 192, PixelFormats.Bgra32, null);
                var enhanced = new WriteableBitmap(800, 400, 192, 192, PixelFormats.Bgra32, null);
                var canvas = new GraphCanvasControl
                {
                    ImageSource = original,
                    CoordinateReferenceSource = original,
                    ComparisonImageSource = enhanced,
                    IsComparisonVisible = true,
                };

                canvas.Measure(new Size(900, 500));
                canvas.Arrange(new Rect(0, 0, 900, 500));
                canvas.UpdateLayout();
                canvas.RecalculateViewport();

                var editable = (Grid)canvas.FindName("EditableImageCoordinateSurface");
                var comparison = (Image)canvas.FindName("ComparisonImage");
                var comparisonPane = (Border)canvas.FindName("ComparisonPane");
                Assert.AreEqual(400d, editable.Width);
                Assert.AreEqual(200d, editable.Height);
                Assert.AreEqual(400d, comparison.Width);
                Assert.AreEqual(200d, comparison.Height);
                Assert.AreEqual(Visibility.Visible, comparisonPane.Visibility);

                canvas.IsComparisonVisible = false;
                Assert.AreEqual(Visibility.Collapsed, comparisonPane.Visibility);
            });
    }

    [TestMethod]
    public void CanvasFitsAfterResizeAndZoomsOnFirstRelativeStep()
    {
        StaTestHost.Run(
            () =>
            {
                var canvas = new GraphCanvasControl
                {
                    ImageSource = new WriteableBitmap(800, 400, 96, 96, PixelFormats.Bgra32, null),
                };

                canvas.Measure(new Size(400, 300));
                canvas.Arrange(new Rect(0, 0, 400, 300));
                canvas.UpdateLayout();
                canvas.RecalculateViewport();
                double initialFit = canvas.FitScale;
                double initialEffective = canvas.EffectiveScale;

                canvas.ZoomBy(1.25);
                Assert.AreEqual(1.25, canvas.ZoomLevel, 0.0001);
                Assert.AreEqual(initialEffective * 1.25, canvas.EffectiveScale, 0.0001);

                canvas.ResetView();
                canvas.ZoomBy(0.8);
                canvas.RecalculateViewport();
                double belowFitScale = canvas.FitScale;
                double belowFitEffectiveScale = canvas.EffectiveScale;
                Assert.AreEqual(belowFitScale * 0.8, belowFitEffectiveScale, 0.0001);
                Assert.IsLessThan(belowFitScale, belowFitEffectiveScale);

                canvas.Measure(new Size(800, 600));
                canvas.Arrange(new Rect(0, 0, 800, 600));
                canvas.UpdateLayout();
                canvas.RecalculateViewport();
                Assert.IsGreaterThan(initialFit, canvas.FitScale);
                Assert.AreEqual(canvas.FitScale * canvas.ZoomLevel, canvas.EffectiveScale, 0.0001);
            });
    }

    [TestMethod]
    public void PaneWidthPersistenceClampsAndFailsSoft()
    {
        string root = Path.Combine(Path.GetTempPath(), $"graph-reader-ui-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "window-layout.txt");
        MethodInfo read = typeof(MainWindow).GetMethod("ReadInspectorWidth", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo write = typeof(MainWindow).GetMethod("WriteInspectorWidth", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo readSeries = typeof(MainWindow).GetMethod("ReadSeriesWidth", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo writeSeries = typeof(MainWindow).GetMethod("WriteSeriesWidth", BindingFlags.NonPublic | BindingFlags.Static)!;

        try
        {
            write.Invoke(null, [path, 512d]);
            Assert.AreEqual(512d, (double)read.Invoke(null, [path, 384d])!);

            File.WriteAllText(path, "99999");
            Assert.AreEqual(800d, (double)read.Invoke(null, [path, 384d])!);

            File.WriteAllText(path, "not-a-width");
            Assert.AreEqual(384d, (double)read.Invoke(null, [path, 384d])!);
            Assert.AreEqual(384d, (double)read.Invoke(null, [null, 384d])!);

            string seriesPath = Path.Combine(root, "series-pane-width.txt");
            writeSeries.Invoke(null, [seriesPath, 310d]);
            Assert.AreEqual(310d, (double)readSeries.Invoke(null, [seriesPath, 272d])!);

            File.WriteAllText(seriesPath, "99999");
            Assert.AreEqual(560d, (double)readSeries.Invoke(null, [seriesPath, 272d])!);

            File.WriteAllText(seriesPath, "not-a-width");
            Assert.AreEqual(272d, (double)readSeries.Invoke(null, [seriesPath, 272d])!);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void RelativeZoomPreservesTwoWayBindingForWheelEquivalentSteps()
    {
        StaTestHost.Run(
            () =>
            {
                var source = new MutableZoom(1);
                var canvas = new GraphCanvasControl();
                BindingOperations.SetBinding(
                    canvas,
                    GraphCanvasControl.ZoomLevelProperty,
                    new Binding(nameof(MutableZoom.ZoomLevel))
                    {
                        Source = source,
                        Mode = BindingMode.TwoWay,
                    });

                canvas.ZoomBy(1.25);
                Assert.AreEqual(1.25, source.ZoomLevel, 0.0001);

                canvas.ZoomBy(0.8);
                Assert.AreEqual(1, source.ZoomLevel, 0.0001);
            });
    }

    [TestMethod]
    public void ControlXamlContainsNoVisibleHardCodedStrings()
    {
        var controlsDirectory = Path.Combine(RepositoryTestPaths.Root, "src", "GraphReader.App", "Controls");
        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(controlsDirectory, "*.xaml", SearchOption.TopDirectoryOnly))
        {
            var document = XDocument.Load(path);
            foreach (var attribute in document.Descendants().Attributes())
            {
                bool isAutomationName = attribute.Name.LocalName == "Name"
                    && attribute.Name.NamespaceName.Contains("System.Windows.Automation", StringComparison.Ordinal);
                bool isVisibleAttribute = isAutomationName || new[] { "Text", "Content", "Header", "ToolTip" }
                    .Contains(attribute.Name.LocalName, StringComparer.Ordinal);
                if (!isVisibleAttribute || string.IsNullOrWhiteSpace(attribute.Value))
                {
                    continue;
                }

                if (!attribute.Value.StartsWith("{DynamicResource ", StringComparison.Ordinal)
                    && !attribute.Value.StartsWith("{Binding ", StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetFileName(path)}: {attribute.Name.LocalName}='{attribute.Value}'");
                }
            }
        }

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private sealed class MutableCount(int count) : INotifyPropertyChanged
    {
        private int _count = count;

        public int Count
        {
            get => _count;
            set
            {
                if (_count == value)
                {
                    return;
                }

                _count = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class MutableZoom(double zoomLevel) : INotifyPropertyChanged
    {
        private double _zoomLevel = zoomLevel;

        public double ZoomLevel
        {
            get => _zoomLevel;
            set
            {
                if (Math.Abs(_zoomLevel - value) < 0.0001)
                {
                    return;
                }

                _zoomLevel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ZoomLevel)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
