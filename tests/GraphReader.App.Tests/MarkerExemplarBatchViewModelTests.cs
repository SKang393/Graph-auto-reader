// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Integration.MarkerExemplar;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using AppPoint = GraphReader.App.Models.GraphPoint;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class MarkerExemplarBatchViewModelTests
{
    private static readonly string[] InspectorResources =
        ["Themes/DesignTokens", "Themes/LightTheme", "Themes/Controls", "Localization/Resources.en-US"];

    [TestMethod]
    public void RemainingCountBindingRefreshesAfterSearchAndLastRowHasAccurateStatus() => WithFixture(fixture =>
    {
        var text = new TextBlock { DataContext = fixture.Vm };
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(fixture.Vm.AcceptAllMarkerExemplarsText)));
        fixture.Matcher.Candidates = Candidates(1);
        fixture.Find();
        StringAssert.Contains(text.Text, "1");
        Assert.IsTrue(fixture.Vm.AcceptAllMarkerExemplarCandidatesCommand.CanExecute(null));
        fixture.Vm.AcceptMarkerExemplarCandidateCommand.Execute(fixture.Vm.MarkerExemplarCandidates.Single());
        StringAssert.Contains(fixture.Vm.MarkerExemplarStatusText, "Added 1");
        fixture.Find();
        fixture.Vm.RemoveMarkerExemplarCandidateCommand.Execute(fixture.Vm.MarkerExemplarCandidates.Single());
        Assert.AreEqual("No unreviewed matches remain.", fixture.Vm.MarkerExemplarStatusText);
        fixture.Matcher.Candidates = [];
        fixture.Find();
        StringAssert.Contains(fixture.Vm.MarkerExemplarStatusText, "No similar positions were found");
    });
    [TestMethod]
    public void ExplicitBatchSkipsExistingDuplicatesAndUsesSelectedSeriesWithOneUndoAndAutosave() => WithFixture(fixture =>
    {
        MarkerExemplarCandidate[] candidates = Candidates(3);
        fixture.Matcher.Candidates = candidates;
        fixture.Workspace.AddPoint(fixture.Tab.TabId, fixture.SeriesId, candidates[0].Center.X, candidates[0].Center.Y);
        string otherSeries = fixture.Workspace.AddSeries(fixture.Tab.TabId,
            new("Other", "■", MarkerShape.Square, MarkerFill.Open, SemanticRole.Unknown)).SeriesId;
        fixture.Workspace.AddPoint(fixture.Tab.TabId, otherSeries, candidates[1].Center.X, candidates[1].Center.Y);
        string before = ScientificState(fixture.Workspace);
        int count = fixture.Tab.Points.Count;
        fixture.Find();
        Assert.AreEqual(before, ScientificState(fixture.Workspace), "Find must not apply proposals.");
        Assert.AreEqual(0, fixture.Workspace.AutosavedStates.Count);
        Assert.HasCount(3, fixture.Vm.MarkerExemplarCandidates);
        Assert.IsTrue(fixture.Vm.MarkerExemplarCandidates[0].IsDuplicate);
        Assert.IsTrue(fixture.Vm.AcceptAllMarkerExemplarCandidatesCommand.CanExecute(null));

        fixture.Vm.AcceptAllMarkerExemplarCandidatesCommand.Execute(null);

        Assert.AreEqual(count + 2, fixture.Tab.Points.Count);
        foreach (MarkerExemplarCandidate candidate in candidates)
            Assert.AreEqual(1, fixture.Tab.Points.Count(point => point.SeriesId == fixture.SeriesId &&
                point.PixelX == candidate.Center.X && point.PixelY == candidate.Center.Y));
        Assert.AreEqual(1, fixture.Tab.Points.Count(point => point.SeriesId == otherSeries));
        Assert.AreEqual(fixture.Seed.PointId, fixture.Vm.SelectedPointId);
        Assert.AreEqual(1, fixture.Workspace.AutosavedStates.Count, "Batch queues one completed-state autosave.");
        string after = ScientificState(fixture.Workspace);
        Assert.AreEqual(after, fixture.Workspace.AutosavedStates[0]);
        StringAssert.Contains(fixture.Vm.MarkerExemplarStatusText, "Added 2");
        fixture.Vm.UndoCommand.Execute(null);
        Assert.AreEqual(before, ScientificState(fixture.Workspace), "One Undo removes the entire batch.");
        Assert.IsFalse(fixture.Vm.MarkerExemplarStatusText.Contains("Added 2", StringComparison.Ordinal));
        Assert.IsEmpty(fixture.Vm.MarkerExemplarCandidates);
        Assert.IsFalse(fixture.Vm.CanUndo, "Setup was outside the VM edit history.");
        fixture.Vm.RedoCommand.Execute(null);
        Assert.AreEqual(after, ScientificState(fixture.Workspace), "One Redo restores every batch point and its scientific coordinates.");
        Assert.IsFalse(fixture.Vm.MarkerExemplarStatusText.Contains("Added 2", StringComparison.Ordinal));
        Assert.IsEmpty(fixture.Vm.MarkerExemplarCandidates);
    });

    [TestMethod]
    public void AdjacentMatchesCanBeInspectedAndDecidedIndividually() => WithFixture(fixture =>
    {
        var (vm, workspace, tab) = (fixture.Vm, fixture.Workspace, fixture.Tab);
        var prior = workspace.AddPoint(tab.TabId, fixture.SeriesId, 90, 80);
        workspace.SetPointReviewStatus(tab.TabId, prior.PointId, ReviewStatus.Accepted);
        var other = workspace.AddSeries(tab.TabId,
            new("Crossing trace", "□", MarkerShape.Square, MarkerFill.Open, SemanticRole.Intervention));
        workspace.AddPoint(tab.TabId, other.SeriesId, 180, 94);
        fixture.Matcher.Candidates = [
            new("same mark", new(91, 80), .97),
            new("exact repeat", new(90, 80), .98),
            new("crossing", new(180, 94), .96),
            new("first close visit", new(220, 122), .95),
            new("next close visit", new(221, 121), .94),
        ];
        fixture.Find();
        var repeat = vm.MarkerExemplarCandidates[0];
        var exact = vm.MarkerExemplarCandidates[1];
        var first = vm.MarkerExemplarCandidates[3];
        var next = vm.MarkerExemplarCandidates[4];
        Assert.IsTrue(repeat.NeedsIndividualReview);
        Assert.IsTrue(exact.IsDuplicate);
        Assert.IsFalse(vm.AcceptMarkerExemplarCandidateCommand.CanExecute(exact));
        Assert.IsTrue(first.NeedsIndividualReview && next.NeedsIndividualReview);
        StringAssert.Contains(vm.AcceptAllMarkerExemplarsText, "(1)");
        var before = workspace.CurrentProject;

        var resources = new ResourceDictionary();
        foreach (string name in InspectorResources)
            resources.MergedDictionaries.Add(new() { Source = new Uri($"/GraphReader.App;component/{name}.xaml", UriKind.Relative) });
        var window = new MainWindow(null, resources);
        ((MainWindowViewModel)window.DataContext).Dispose();
        window.DataContext = vm;
        try
        {
            window.Show();
            window.UpdateLayout();
            var list = (ItemsControl)window.FindName("MarkerExemplarList");
            var presenter = (ContentPresenter)list.ItemContainerGenerator.ContainerFromItem(repeat);
            var inspect = (Button)list.ItemTemplate.FindName("InspectMatchButton", presenter);
            var remove = (Button)list.ItemTemplate.FindName("RemoveMatchButton", presenter);
            var notice = (TextBlock)list.ItemTemplate.FindName("NearbyMatchNotice", presenter);
            Assert.AreEqual(Visibility.Visible, notice.Visibility);
            StringAssert.Contains(notice.Text, "within one pixel");
            Assert.AreSame(repeat, inspect.CommandParameter);
            Assert.IsTrue(inspect.IsEnabled && remove.IsEnabled);
            inspect.Command.Execute(inspect.CommandParameter);
            var lens = (GraphReader.App.Controls.MagnifierControl)window.FindName("MagnifierInspector");
            Assert.AreEqual(new Point(91, 80), lens.PixelPosition);
            Assert.AreEqual(fixture.Seed.PointId, vm.SelectedPointId);
            Assert.AreEqual(fixture.SeriesId, vm.SelectedSeriesId);
            Assert.HasCount(5, vm.MarkerExemplarCandidates);
            Assert.AreSame(repeat, remove.CommandParameter);
            remove.Command.Execute(remove.CommandParameter);
            Assert.IsFalse(vm.InspectMarkerExemplarCandidateCommand.CanExecute(repeat));
            vm.HandleCanvasNavigation(new(30, 30));
            vm.InspectMarkerExemplarCandidateCommand.Execute(repeat);
            Assert.AreEqual(new Point(30, 30), lens.PixelPosition);
            Assert.AreSame(before, workspace.CurrentProject);

            vm.AcceptAllMarkerExemplarCandidatesCommand.Execute(null);
            Assert.HasCount(2, tab.Points.Where(p => p.PixelX == 180 && p.PixelY == 94).ToArray());
            CollectionAssert.AreEquivalent(new[] { exact, first, next }, vm.MarkerExemplarCandidates.ToArray());
            foreach (var visit in new[] { first, next })
            {
                Assert.IsTrue(vm.AcceptMarkerExemplarCandidateCommand.CanExecute(visit));
                vm.AcceptMarkerExemplarCandidateCommand.Execute(visit);
                var added = workspace.CurrentProject.Panels.Single().Points[^1];
                Assert.AreEqual(visit.Candidate.Center.X, added.OriginalPixel.X);
                Assert.AreEqual(visit.Candidate.Center.Y, added.OriginalPixel.Y);
                Assert.AreEqual(ReviewStatus.Corrected, added.ReviewStatus);
            }
            Assert.HasCount(1, vm.MarkerExemplarCandidates);
            Assert.AreSame(exact, vm.MarkerExemplarCandidates[0]);
            Assert.IsFalse(vm.AcceptAllMarkerExemplarCandidatesCommand.CanExecute(null));
            var accepted = workspace.CurrentProject.Panels.Single().Points.Single(p => p.ReviewStatus == ReviewStatus.Accepted);
            Assert.AreEqual(Guid.Parse(prior.PointId), accepted.PointId.Value);
            Assert.IsFalse(tab.Points.Any(p => p.PixelX == 91 && p.PixelY == 80));
        }
        finally
        {
            window.DataContext = null;
            window.Close();
        }
    });

    [TestMethod]
    public void StaleSeedBindingDisablesAndRefusesWholeBatch() => WithFixture(fixture =>
    {
        fixture.Matcher.Candidates = Candidates(2);
        fixture.Find();
        Assert.IsTrue(fixture.Vm.AcceptAllMarkerExemplarCandidatesCommand.CanExecute(null));
        // Service-only mutation deliberately bypasses the VM selection invalidation.
        fixture.Workspace.MovePoint(fixture.Tab.TabId, fixture.Seed.PointId, 31, 30);
        string before = ScientificState(fixture.Workspace);
        Assert.IsFalse(fixture.Vm.AcceptAllMarkerExemplarCandidatesCommand.CanExecute(null));
        fixture.Vm.AcceptAllMarkerExemplarCandidatesCommand.Execute(null);
        Assert.AreEqual(before, ScientificState(fixture.Workspace));
        Assert.AreEqual(0, fixture.Workspace.AutosavedStates.Count);
        Assert.IsFalse(fixture.Vm.CanUndo);
    });

    [TestMethod]
    public void SecondAddFailureRollsBackScientificStateAndCannotReappearAfterSaveReopen() => WithFixture(fixture =>
    {
        fixture.Matcher.Candidates = Candidates(3);
        fixture.Find();
        fixture.Tab.IsDirty = false;
        string before = ScientificState(fixture.Workspace);
        string seedId = fixture.Seed.PointId;
        fixture.Workspace.FailOnBatchAdd = 2;

        fixture.Vm.AcceptAllMarkerExemplarCandidatesCommand.Execute(null);

        Assert.AreEqual(2, fixture.Workspace.BatchAddCalls, "The interface fault must exercise a partially applied batch.");
        Assert.AreEqual(before, ScientificState(fixture.Workspace));
        Assert.AreEqual(seedId, fixture.Vm.SelectedPointId);
        Assert.AreEqual(fixture.SeriesId, fixture.Vm.SelectedSeriesId);
        Assert.IsFalse(fixture.Vm.CanUndo, "A rejected batch cannot create a successful edit-history item.");
        Assert.IsTrue(fixture.Workspace.AutosavedStates.All(state => state == before), "Never autosave the partial batch.");
        Assert.IsNotNull(fixture.Workspace.FirstBatchPointId);
        Guid failedPointId = Guid.Parse(fixture.Workspace.FirstBatchPointId);
        Assert.IsFalse(fixture.Workspace.CurrentProject.Panels.SelectMany(panel => panel.Points)
            .Any(point => point.PointId.Value == failedPointId));

        string savedPath = Path.Combine(fixture.Directory, "rollback.garproj");
        Assert.IsTrue(Pump(fixture.Workspace.SaveProjectAsync(savedPath, CancellationToken.None)).IsSuccess);
        using var reopened = new ManualPreviewWorkspaceService();
        WorkspaceTabViewModel tab = Pump(reopened.OpenProjectAsync(savedPath, CancellationToken.None)).Single();
        Assert.AreEqual(before, ScientificState(reopened), "Reopen must use the restored state, despite the preserved attempted-add audit.");
        Assert.IsFalse(tab.Points.Any(point => point.PointId == fixture.Workspace.FirstBatchPointId));
    });

    [TestMethod]
    public void SixtyFourRealWorkspaceEditsRemainOneUndoAndReportElapsedTime() => WithFixture(fixture =>
    {
        fixture.Matcher.Candidates = Candidates(64);
        fixture.Find();
        int before = fixture.Tab.Points.Count;
        int beforeAudit = AddedPointAudits(fixture.Workspace);
        var elapsed = Stopwatch.StartNew();
        fixture.Vm.AcceptAllMarkerExemplarCandidatesCommand.Execute(null);
        elapsed.Stop();
        Console.WriteLine("Marker exemplar batch: 64 real workspace edits in {0:F3} ms", elapsed.Elapsed.TotalMilliseconds);
        Assert.AreEqual(before + 64, fixture.Tab.Points.Count);
        Assert.AreEqual(beforeAudit + 64, AddedPointAudits(fixture.Workspace), "Each accepted point retains its existing per-point audit.");
        Assert.AreEqual(1, fixture.Workspace.AutosavedStates.Count);
        fixture.Vm.UndoCommand.Execute(null);
        Assert.AreEqual(before, fixture.Tab.Points.Count);
        fixture.Vm.RedoCommand.Execute(null);
        Assert.AreEqual(before + 64, fixture.Tab.Points.Count);
    });

    private static string ScientificState(ManualPreviewWorkspaceService workspace) => JsonSerializer.Serialize(
        workspace.CurrentProject.Panels.Select(panel => new
        {
            panel.PanelId, panel.Calibration, panel.Phases,
            Points = panel.Points.OrderBy(point => point.PointId.Value).Select(point => new
            {
                point.PointId, point.SeriesId, point.OriginalPixel, point.GraphX, point.GraphY,
                point.PhaseId, point.ObservationIndex, point.ReviewStatus, point.MarkerId,
            }),
        }));

    private static int AddedPointAudits(ManualPreviewWorkspaceService workspace) => workspace.CurrentProject.Audit.Events.Count(entry =>
        entry.Details is { } details && details.TryGetProperty("kind", out JsonElement kind) && kind.GetString() == "production_point_added");

    private static MarkerExemplarCandidate[] Candidates(int count) => Enumerable.Range(0, count)
        .Select(index => new MarkerExemplarCandidate("candidate-" + index,
            new(70 + index % 8 * 45, 60 + index / 8 * 25), .95)).ToArray();

    private static void WithFixture(Action<Fixture> action) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        action(fixture);
    });

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "marker-batch-" + Guid.NewGuid().ToString("N"));
        internal MatcherStub Matcher { get; } = new();
        internal FaultWorkspace Workspace { get; }
        internal WorkspaceTabViewModel Tab { get; }
        internal string SeriesId { get; }
        internal AppPoint Seed { get; }
        internal MainWindowViewModel Vm { get; }

        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            string path = Path.Combine(Directory, "owned.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(512, 320, 96, 96,
                PixelFormats.Gray8, null, Enumerable.Repeat((byte)255, 512 * 320).ToArray(), 512)));
            using (var stream = File.Create(path)) encoder.Save(stream);
            Workspace = new(Matcher);
            Tab = Pump(Workspace.ImportImagesAsync([path], CancellationToken.None)).Single();
            Workspace.Calibrate(Tab.TabId, new(new(10, 300), new(10, 10), new(500, 300), 10, 24));
            SeriesId = Workspace.AddSeries(Tab.TabId,
                new("Selected", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
            Seed = Workspace.AddPoint(Tab.TabId, SeriesId, 30, 30);
            Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
            Vm = new(Workspace, new LocalizationService(new ResourceDictionary())) { SelectedSeriesId = SeriesId, SelectedPointId = Seed.PointId };
        }

        internal void Find() => Pump(Vm.FindMarkerExemplarsAsync(CancellationToken.None));
        public void Dispose()
        {
            Vm.Dispose();
            Workspace.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
            GC.SuppressFinalize(this);
        }
    }

    private sealed class FaultWorkspace(IMarkerExemplarSuggestionService matcher) :
        ManualPreviewWorkspaceService(markerExemplarSuggestionService: matcher), IManualWorkspaceService
    {
        internal int FailOnBatchAdd { get; set; }
        internal int BatchAddCalls { get; private set; }
        internal string? FirstBatchPointId { get; private set; }
        internal List<string> AutosavedStates { get; } = [];
        AppPoint IManualWorkspaceService.AddPoint(string tabId, string seriesId, double pixelX, double pixelY)
        {
            BatchAddCalls++;
            if (FailOnBatchAdd > 0 && BatchAddCalls == FailOnBatchAdd)
                throw new InvalidOperationException("Owned fixture second-add failure.");
            AppPoint result = base.AddPoint(tabId, seriesId, pixelX, pixelY);
            FirstBatchPointId ??= result.PointId;
            return result;
        }

        Task<DomainResult<ProjectSnapshotReceipt>> IManualWorkspaceService.AutosaveAsync(
            SnapshotTrigger trigger, string? tabId, string? entityId, CancellationToken cancellationToken)
        {
            AutosavedStates.Add(ScientificState(this));
            return base.AutosaveAsync(trigger, tabId, entityId, cancellationToken);
        }
    }

    private sealed class MatcherStub : IMarkerExemplarSuggestionService
    {
        internal IReadOnlyList<MarkerExemplarCandidate> Candidates { get; set; } = [];
        public Task<MarkerExemplarResponse> FindAsync(MarkerExemplarRequest request, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new MarkerExemplarResponse(request, Candidates, new(1, 1, 2),
                new(new(2, 2500, .5, true), 1, MarkerExemplarValidation.Algorithm), null));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
