// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Controls;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;
using GraphReader.Domain;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class AxisCandidateReviewTests
{
    private static readonly string[] ResourceSources =
        ["Themes/DesignTokens", "Themes/LightTheme", "Themes/Controls",
            "Localization/Resources.en-US", "Resources/Identity/IdentityAssets"];

    [TestMethod]
    [DataRow("LightTheme")]
    [DataRow("DarkTheme")]
    public void ActualListBindingHasNoDefaultAndCultureRefreshPreservesExplicitSelection(string theme) => WithWorkspace((workspace, originalVm) =>
    {
        originalVm.Dispose();
        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        var localization = new CandidateLocalization();
        using var vm = new MainWindowViewModel(workspace, localization);
        var resources = new ResourceDictionary();
        foreach (string source in ResourceSources)
            resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"/GraphReader.App;component/{(source == "Themes/LightTheme" ? $"Themes/{theme}" : source)}.xaml", UriKind.Relative) });
        var window = new MainWindow(themeService: null, resources);
        ((MainWindowViewModel)window.DataContext).Dispose();
        window.DataContext = vm;
        try
        {
            ListBox list = Descendants(window).OfType<ListBox>().Single(element =>
                AutomationProperties.GetAutomationId(element) == "Manual.AxisCandidates.List");
            Pump(vm.FindAxisCandidatesAsync(CancellationToken.None));
            FlushBindings();
            Assert.HasCount(2, list.Items);
            Assert.IsNull(list.SelectedItem);
            Assert.IsNull(vm.SelectedAxisCandidate);
            Color expectedSurface = ((SolidColorBrush)window.FindResource("App.Brush.Surface")).Color;
            Color expectedText = ((SolidColorBrush)window.FindResource("App.Brush.Text")).Color;
            Assert.AreEqual(expectedSurface, ((SolidColorBrush)list.Background).Color);
            Assert.AreEqual(expectedText, ((SolidColorBrush)list.Foreground).Color);
            list.SelectedItem = vm.AxisCandidates[1];
            FlushBindings();
            AxisReviewCandidateViewModel selected = vm.SelectedAxisCandidate!;
            string oldLabel = selected.DisplayText;
            list.Measure(new Size(400, 160));
            list.Arrange(new Rect(0, 0, 400, 160));
            list.UpdateLayout();
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(selected);
            Assert.IsNotNull(container);
            Assert.AreEqual(oldLabel, AutomationProperties.GetName(container));
            Assert.AreEqual(expectedText, ((SolidColorBrush)container.Foreground).Color);
            TextBlock label = VisualDescendants(container).OfType<TextBlock>().Single(text => text.Text == oldLabel);
            Assert.AreEqual(expectedText, ((SolidColorBrush)label.Foreground).Color);
            localization.ApplyCulture(CultureInfo.GetCultureInfo("fr-FR"));
            FlushBindings();
            Assert.AreSame(selected, vm.SelectedAxisCandidate);
            Assert.AreSame(selected, list.SelectedItem);
            Assert.AreEqual(1, selected.Index);
            Assert.AreNotEqual(oldLabel, selected.DisplayText);
            Assert.AreEqual(selected.DisplayText, AutomationProperties.GetName(container));
            Assert.AreEqual(selected.DisplayText, label.Text);
            Assert.AreEqual(expectedText, ((SolidColorBrush)label.Foreground).Color);
            Assert.IsTrue(vm.UseAxisCandidateCommand.CanExecute(null));
            Assert.IsFalse(vm.HasAxisSuggestion);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void ChoicesHaveNoDefaultAndPreviewDoesNotChangeCalibrationPointsOrHistory() => WithWorkspace((workspace, vm) =>
    {
        WorkspaceTabViewModel tab = vm.SelectedTab!;
        var calibration = tab.Calibration;
        var point = tab.Points.Single();
        var values = (point.PixelX, point.PixelY, point.GraphX, point.GraphY);
        int auditCount = workspace.CurrentProject.Audit.Events.Count;
        bool dirty = tab.IsDirty;

        Pump(vm.FindAxisCandidatesAsync(CancellationToken.None));

        Assert.HasCount(2, vm.AxisCandidates);
        Assert.IsTrue(vm.HasAxisCandidates);
        Assert.IsNull(vm.SelectedAxisCandidate);
        Assert.IsFalse(vm.UseAxisCandidateCommand.CanExecute(null));
        Assert.IsFalse(vm.HasAxisSuggestion);
        foreach (AxisReviewCandidateViewModel candidate in vm.AxisCandidates)
        {
            vm.SelectedAxisCandidate = candidate;
            Assert.IsTrue(vm.UseAxisCandidateCommand.CanExecute(null));
            RustAxisGeometry? geometry = workspace.LastReview!.Candidates[candidate.Index].Geometry;
            Assert.IsNotNull(geometry);
            CollectionAssert.AreEqual(new[] { new Point(geometry.Left, geometry.Bottom), new Point(geometry.Left, geometry.Top),
                new Point(geometry.Right, geometry.Bottom) }, Preview(tab));
            Assert.AreSame(calibration, tab.Calibration);
            Assert.AreEqual(values, (point.PixelX, point.PixelY, point.GraphX, point.GraphY));
            Assert.IsFalse(vm.HasAxisSuggestion);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
        }
        Assert.AreEqual(dirty, tab.IsDirty);
        Assert.AreEqual(auditCount, workspace.CurrentProject.Audit.Events.Count);
        Assert.IsFalse(vm.UndoCommand.CanExecute(null));
        Assert.AreEqual(0, workspace.Ocr.Calls);
    });

    [TestMethod]
    public void ExplicitChoiceStartsFreshBlankNativeDraftAndDismissPreservesSavedState() => WithWorkspace((workspace, vm) =>
    {
        WorkspaceTabViewModel tab = vm.SelectedTab!;
        var calibration = tab.Calibration;
        Pump(vm.FindAxisCandidatesAsync(CancellationToken.None));
        vm.SelectedAxisCandidate = vm.AxisCandidates[1];
        vm.UseAxisCandidateCommand.Execute(null);

        Assert.IsTrue(vm.HasAxisSuggestion);
        Assert.IsTrue(vm.HasAxisGeometryReviewWarning);
        Assert.IsFalse(vm.IsManualAxisDraft);
        Assert.IsFalse(vm.HasAxisCandidates);
        Assert.IsNull(vm.SelectedAxisCandidate);
        Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
        Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
        Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Assert.IsTrue(vm.ReadCalibrationLabelsCommand.CanExecute(null));
        Assert.AreEqual(1, workspace.SelectionCalls);
        Assert.AreEqual(0, workspace.Ocr.Calls);
        Assert.AreSame(calibration, tab.Calibration);
        Assert.IsFalse(vm.UndoCommand.CanExecute(null));

        vm.DismissAxisSuggestionCommand.Execute(null);
        Assert.IsFalse(vm.HasAxisSuggestion);
        Assert.HasCount(0, Preview(tab));
        Assert.AreSame(calibration, tab.Calibration);
        Assert.HasCount(1, tab.Points);
    });

    [TestMethod]
    public void CalibrationChangeInvalidatesAnExplicitCandidateSelection() => WithWorkspace((workspace, vm) =>
    {
        Pump(vm.FindAxisCandidatesAsync(CancellationToken.None));
        vm.SelectedAxisCandidate = vm.AxisCandidates[0];
        workspace.Calibrate(vm.SelectedTab!.TabId, new(new(10, 100), new(10, 10), new(150, 100), 20, 3));
        Assert.IsFalse(vm.UseAxisCandidateCommand.CanExecute(null));
        vm.UseAxisCandidateCommand.Execute(null);
        Assert.AreEqual(0, workspace.SelectionCalls);
        Assert.IsFalse(vm.HasAxisSuggestion);
    });

    [TestMethod]
    public void DismissAndTabChangeClearChoicesAndPreview() => WithWorkspace((workspace, vm) =>
    {
        WorkspaceTabViewModel tab = vm.SelectedTab!;
        Pump(vm.FindAxisCandidatesAsync(CancellationToken.None));
        vm.SelectedAxisCandidate = vm.AxisCandidates[0];
        vm.DismissAxisSuggestionCommand.Execute(null);
        Assert.IsFalse(vm.HasAxisCandidates);
        Assert.IsNull(vm.SelectedAxisCandidate);
        Assert.HasCount(0, Preview(tab));

        Pump(vm.FindAxisCandidatesAsync(CancellationToken.None));
        vm.SelectedAxisCandidate = vm.AxisCandidates[1];
        vm.SelectedTab = null;
        Assert.IsFalse(vm.HasAxisCandidates);
        Assert.HasCount(0, Preview(tab));
        vm.SelectedTab = tab;
        Assert.IsFalse(vm.UseAxisCandidateCommand.CanExecute(null));
        Assert.IsFalse(vm.HasAxisSuggestion);
    });

    [TestMethod]
    public void EscapeAndRealUndoRedoClearChooserWithoutAddingOrRecalibrating() => WithWorkspace((workspace, vm) =>
    {
        WorkspaceTabViewModel tab = vm.SelectedTab!;
        var calibration = tab.Calibration;
        string originalPointId = tab.Points.Single().PointId;
        vm.StartCalibrationCommand.Execute(null);
        Pump(vm.HandleCanvasPointAsync(new Point(20, 100)));
        Pump(vm.HandleCanvasPointAsync(new Point(20, 20)));
        Assert.AreEqual(ManualEditorMode.Calibration, vm.EditorMode);
        workspace.Unavailable = true;
        Pump(vm.FindAxisCandidatesAsync(CancellationToken.None));
        Assert.AreEqual(ManualEditorMode.Select, vm.EditorMode);
        Pump(vm.HandleCanvasPointAsync(new Point(140, 100)));
        AssertCleared(1);
        Assert.IsFalse(vm.UndoCommand.CanExecute(null));
        workspace.Unavailable = false;
        Pump(vm.FindAxisCandidatesAsync(CancellationToken.None));
        vm.SelectedAxisCandidate = vm.AxisCandidates[0];
        Assert.IsTrue(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        AssertCleared(1);

        vm.SelectedSeriesId = tab.Points.Single().SeriesId;
        Assert.IsTrue(vm.BeginAddPointCommand.CanExecute(null));
        vm.BeginAddPointCommand.Execute(null);
        Assert.AreEqual(ManualEditorMode.AddPoint, vm.EditorMode);
        Pump(vm.HandleCanvasPointAsync(new Point(100, 50)));
        Assert.HasCount(2, tab.Points);
        Assert.IsTrue(vm.UndoCommand.CanExecute(null));
        Pump(vm.FindAxisCandidatesAsync(CancellationToken.None));
        vm.SelectedAxisCandidate = vm.AxisCandidates[1];
        vm.UndoCommand.Execute(null);
        AssertCleared(1);
        Assert.AreEqual(originalPointId, tab.Points.Single().PointId);
        Assert.IsTrue(vm.RedoCommand.CanExecute(null));

        Pump(vm.FindAxisCandidatesAsync(CancellationToken.None));
        vm.SelectedAxisCandidate = vm.AxisCandidates[0];
        vm.RedoCommand.Execute(null);
        AssertCleared(2);

        void AssertCleared(int points)
        {
            Assert.IsFalse(vm.HasAxisCandidates);
            Assert.IsNull(vm.SelectedAxisCandidate);
            Assert.IsFalse(vm.HasAxisSuggestion);
            Assert.IsFalse(vm.UseAxisCandidateCommand.CanExecute(null));
            Assert.HasCount(0, Preview(tab));
            Assert.HasCount(points, tab.Points);
            Assert.AreEqual(calibration, tab.Calibration);
        }
    });

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("tab")]
    [DataRow("dispose")]
    public void LateCandidateReplyCannotPublishAfterCancellationTabChangeOrDisposal(string transition) => WithWorkspace((workspace, vm) =>
    {
        WorkspaceTabViewModel tab = vm.SelectedTab!;
        var calibration = tab.Calibration;
        workspace.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task operation = vm.FindAxisCandidatesAsync(cancellation.Token);
        Assert.IsTrue(vm.IsBusy);
        Assert.IsFalse(vm.FindAxisCandidatesCommand.CanExecute(null));
        Assert.IsFalse(vm.UseAxisCandidateCommand.CanExecute(null));
        if (transition == "cancel") cancellation.Cancel();
        else if (transition == "tab") vm.SelectedTab = null;
        else vm.Dispose();
        workspace.Pending.SetResult(workspace.LastReview);
        Pump(operation);
        Assert.IsFalse(vm.HasAxisCandidates);
        Assert.IsNull(vm.SelectedAxisCandidate);
        Assert.IsFalse(vm.HasAxisSuggestion);
        Assert.AreSame(calibration, tab.Calibration);
        Assert.HasCount(1, tab.Points);
        Assert.AreEqual(0, workspace.Ocr.Calls);
    });

    private sealed class CandidateWorkspace : ManualPreviewWorkspaceService, IAxisCandidateReviewWorkspace
    {
        internal OcrStub Ocr { get; }
        internal WorkspaceAxisCandidateReview? LastReview { get; private set; }
        internal TaskCompletionSource<WorkspaceAxisCandidateReview?>? Pending { get; set; }
        internal bool Unavailable { get; set; }
        internal int SelectionCalls { get; private set; }
        private WorkspaceAxisSuggestion? source;
        internal CandidateWorkspace() : this(new OcrStub()) { }
        private CandidateWorkspace(OcrStub ocr) : base(calibrationOcrSuggestionService: ocr) => Ocr = ocr;
        bool IAxisCandidateReviewWorkspace.CanReviewAxisCandidates => true;
        Task<WorkspaceAxisCandidateReview?> IAxisCandidateReviewWorkspace.ReviewAxisCandidatesAsync(string tabId, CancellationToken token)
        {
            if (Unavailable) return Task.FromResult<WorkspaceAxisCandidateReview?>(null);
            source = CreateManualAxisDraft(tabId, [new(10, 100), new(10, 10), new(150, 100)])!;
            LastReview = new(tabId, source.Binding, Array.AsReadOnly(new[]
            {
                new AxisReviewCandidate(0, new(10, 100, 150, 10, .5)),
                new AxisReviewCandidate(1, new(80, 100, 150, 10, .4)),
            }));
            return Pending?.Task ?? Task.FromResult<WorkspaceAxisCandidateReview?>(LastReview);
        }
        bool IAxisCandidateReviewWorkspace.IsAxisCandidateReviewCurrent(WorkspaceAxisCandidateReview review) =>
            ReferenceEquals(review, LastReview) && source is not null && IsAxisSuggestionCurrent(source);
        WorkspaceAxisSuggestion? IAxisCandidateReviewWorkspace.SelectAxisCandidate(WorkspaceAxisCandidateReview review, int index)
        {
            SelectionCalls++;
            if (!((IAxisCandidateReviewWorkspace)this).IsAxisCandidateReviewCurrent(review)) return null;
            return source! with { Geometry = review.Candidates[index].Geometry, IsUserDefined = false,
                Provider = "opencv-review", ReviewReasons = ["user_selected_candidate"] };
        }
    }

    private sealed class OcrStub : ICalibrationOcrSuggestionService
    {
        internal int Calls { get; private set; }
        public Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request, ReadOnlyMemory<byte> image, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("Candidate review must not request OCR.");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CandidateLocalization : ILocalizationService
    {
        private readonly ResourceDictionary english = new()
            { Source = new Uri(LocalizationService.EnglishSource, UriKind.Relative) };
        public CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
        public IReadOnlyList<CultureInfo> AvailableCultures { get; } =
            [CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("fr-FR")];
        public event EventHandler<LocalizationChangedEventArgs>? CultureChanged;
        public string GetString(string key) => $"{CurrentCulture.Name}: {english[key]}";
        public void ApplyCulture(CultureInfo culture)
        {
            CurrentCulture = culture;
            CultureChanged?.Invoke(this, new(culture));
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static void FlushBindings()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ContextIdle);
        Dispatcher.PushFrame(frame);
    }

    private static Point[] Preview(WorkspaceTabViewModel tab) =>
        (Point[])typeof(ManualReviewOverlay).GetField("_axisProposal", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(tab.OverlayContent)!;

    private static void WithWorkspace(Action<CandidateWorkspace, MainWindowViewModel> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "axis-candidate-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            StaTestHost.Run(() =>
            {
                string path = Path.Combine(directory, "owned.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 120, 96, 96, PixelFormats.Gray8, null,
                    Enumerable.Repeat((byte)255, 160 * 120).ToArray(), 160)));
                using (FileStream stream = File.Create(path)) encoder.Save(stream);
                using var workspace = new CandidateWorkspace();
                WorkspaceTabViewModel tab = Pump(workspace.ImportImagesAsync([path], CancellationToken.None)).Single();
                workspace.Calibrate(tab.TabId, new(new(10, 100), new(10, 10), new(150, 100), 10, 3));
                string series = workspace.AddSeries(tab.TabId, new("Owned", "●", MarkerShape.Circle, MarkerFill.Filled,
                    SemanticRole.Intervention)).SeriesId;
                workspace.AddPoint(tab.TabId, series, 80, 60);
                using var vm = new MainWindowViewModel(workspace);
                action(workspace, vm);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
