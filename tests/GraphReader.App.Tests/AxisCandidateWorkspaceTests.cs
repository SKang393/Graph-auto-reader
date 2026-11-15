// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;
using GraphReader.Domain;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class AxisCandidateWorkspaceTests
{
    private static readonly string[] FailClosedRustErrors = ["AMBIGUOUS_AXIS", "CPU_BUDGET_UNAVAILABLE", "INVALID_RESPONSE", "WORKER_TIMEOUT"];
    private static readonly int[] RustXTicks = [10, 50, 100];
    private static readonly int[] RustYTicks = [10, 50, 100];

    [TestMethod]
    public void RustChoiceRequiresReviewAndForwardsMeasuredTicksOnlyOnExplicitRead() => WithImage(path =>
    {
        var candidate = new AxisReviewCandidate(0, new RustAxisGeometry(10, 100, 100, 10, 0, 10, 100)
            { XTickPositions = RustXTicks, YTickPositions = RustYTicks });
        var provider = new CandidateProvider([candidate], provider: "rust-review");
        var ocr = new InspectingOcr();
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService: provider, calibrationOcrSuggestionService: ocr);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        using var vm = new MainWindowViewModel(workspace);
        Pump(Completed(vm.FindAxisCandidatesAsync(CancellationToken.None)));
        Assert.IsNull(vm.SelectedAxisCandidate);
        Assert.IsFalse(vm.UseAxisCandidateCommand.CanExecute(null));
        vm.SelectedAxisCandidate = vm.AxisCandidates.Single();
        vm.UseAxisCandidateCommand.Execute(null);
        Assert.IsTrue(vm.HasAxisSuggestion);
        Assert.IsTrue(vm.HasAxisGeometryReviewWarning);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
        Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
        Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
        Assert.IsNull(ocr.Request);
        Pump(Completed(vm.ReadCalibrationLabelsAsync(CancellationToken.None)));
        Assert.IsNotNull(ocr.Request);
        CollectionAssert.AreEqual(RustXTicks, ocr.Request.MeasuredXTicks!);
        CollectionAssert.AreEqual(RustYTicks, ocr.Request.MeasuredYTicks!);
        Assert.IsTrue(vm.HasAxisGeometryReviewWarning);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
    });

    [TestMethod]
    public void RustReviewPreservesObservedTicksAndBindsProviderToSnapshot() => WithImage(path =>
    {
        int[] x = [10, 50, 100], y = [10, 50, 100];
        var candidate = new AxisReviewCandidate(0, new RustAxisGeometry(10, 100, 100, 10, 0, 10, 100)
            { XTickPositions = x, YTickPositions = y });
        var provider = new CandidateProvider([candidate], provider: "rust-review");
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService: provider);
        var tab = Pump(workspace.ImportImagesAsync([path], CancellationToken.None)).Single();
        WorkspaceAxisCandidateReview review = Pump(workspace.ReviewAxisCandidatesAsync(tab.TabId, CancellationToken.None))!;
        Assert.AreEqual("rust-review", review.Provider);
        x[1] = 60; y[1] = 60;
        Assert.AreEqual(50, review.Candidates[0].Geometry.XTickPositions[1]);
        Assert.AreEqual(50, review.Candidates[0].Geometry.YTickPositions[1]);
        Assert.IsFalse(workspace.IsAxisCandidateReviewCurrent(review with { Provider = "opencv-review" }));
        Assert.IsFalse(workspace.IsAxisCandidateReviewCurrent(review with { Candidates = [candidate] }));
        WorkspaceAxisSuggestion selected = workspace.SelectAxisCandidate(review, 0)!;
        Assert.AreEqual("rust-review", selected.Provider);
        Assert.AreEqual(50, selected.Geometry.XTickPositions[1]);
        Assert.AreEqual(50, selected.Geometry.YTickPositions[1]);
        Assert.IsTrue(workspace.IsAxisSuggestionCurrent(selected));
    });
    [TestMethod]
    public void ReviewIsSourceBoundDefensiveAndSelectionDoesNotMutateProject() => WithImage(path =>
    {
        var provider = new CandidateProvider();
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService: provider);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        WorkspaceTabViewModel tab = workspace.CreateWorkspace().Single();
        workspace.Calibrate(tab.TabId, Calibration());
        var series = workspace.AddSeries(tab.TabId, new("Series", "●", MarkerShape.Circle,
            MarkerFill.Filled, SemanticRole.Intervention));
        var point = workspace.AddPoint(tab.TabId, series.SeriesId, 80, 55);
        var calibration = tab.Calibration;
        var graphValue = (point.GraphX, point.GraphY);
        int auditCount = workspace.CurrentProject.Audit.Events.Count;
        bool wasDirty = tab.IsDirty;

        WorkspaceAxisCandidateReview review = Pump(workspace.ReviewAxisCandidatesAsync(tab.TabId, CancellationToken.None))!;
        Assert.IsTrue(workspace.CanReviewAxisCandidates);
        Assert.IsTrue(workspace.IsAxisCandidateReviewCurrent(review));
        CollectionAssert.AreEqual(File.ReadAllBytes(path), provider.ReviewInput!);
        Assert.HasCount(2, review.Candidates, "The workspace must retain the full returned candidate list.");
        Assert.AreEqual(0, review.Candidates[0].Geometry.XTickPositions.Count);
        Assert.AreEqual(0, review.Candidates[0].Geometry.YTickPositions.Count);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<AxisReviewCandidate>)review.Candidates)[0] = new(0, new(2, 100, 80, 5, 0)));

        WorkspaceAxisSuggestion selected = workspace.SelectAxisCandidate(review, 1)!;
        Assert.AreEqual(20, selected.Geometry.Left);
        Assert.AreEqual("opencv-review", selected.Provider);
        Assert.IsTrue(workspace.IsAxisSuggestionCurrent(selected));
        Assert.AreSame(calibration, tab.Calibration);
        Assert.AreEqual(graphValue, (point.GraphX, point.GraphY));
        Assert.AreEqual(auditCount, workspace.CurrentProject.Audit.Events.Count);
        Assert.AreEqual(wasDirty, tab.IsDirty);
    });

    [TestMethod]
    public void ReviewBecomesStaleWhenCalibrationBindingChangesAndRejectsSelection() => WithImage(path =>
    {
        var provider = new CandidateProvider();
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService: provider);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        WorkspaceTabViewModel tab = workspace.CreateWorkspace().Single();
        WorkspaceAxisCandidateReview review = Pump(workspace.ReviewAxisCandidatesAsync(tab.TabId, CancellationToken.None))!;
        Assert.IsNotNull(workspace.SelectAxisCandidate(review, 0));

        Assert.IsFalse(workspace.IsAxisCandidateReviewCurrent(review with { Binding = "different-source-or-project" }));
        Assert.IsFalse(workspace.IsAxisCandidateReviewCurrent(review with { TabId = "different-tab" }));

        workspace.Calibrate(tab.TabId, Calibration());

        Assert.IsFalse(workspace.IsAxisCandidateReviewCurrent(review));
        Assert.IsNull(workspace.SelectAxisCandidate(review, 0));
        Assert.IsNull(workspace.SelectAxisCandidate(review, -1));
        Assert.IsNull(workspace.SelectAxisCandidate(review, review.Candidates.Count));
    });

    [TestMethod]
    public void InvalidCandidateResponsesAreRejectedAsAWhole() => WithImage(path =>
    {
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService:
            new CandidateProvider([new(1, new(4, 100, 140, 5, 0))]));
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        string tabId = workspace.CreateWorkspace().Single().TabId;
        Assert.IsNull(Pump(workspace.ReviewAxisCandidatesAsync(tabId, CancellationToken.None)),
            "Out-of-order candidate indices invalidate the response rather than silently reordering it.");
    });

    [TestMethod]
    public void WorkspaceCancellationDoesNotPublishPendingCandidates() => WithImage(path =>
    {
        var provider = new CandidateProvider
        {
            Pending = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService: provider);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        string tabId = workspace.CreateWorkspace().Single().TabId;
        using var cancellation = new CancellationTokenSource();

        Task<WorkspaceAxisCandidateReview?> pending = workspace.ReviewAxisCandidatesAsync(tabId, cancellation.Token);
        cancellation.Cancel();
        provider.Pending.SetResult(new(provider.Candidates, null));

        Assert.Throws<OperationCanceledException>(() => Pump(pending));
        Assert.AreEqual(1, provider.CandidateCalls);
    });

    [TestMethod]
    public void CandidateReviewRequiresManualPreviewAndExplicitFallbackOnly()
    {
        using var noProvider = new ManualPreviewWorkspaceService();
        Assert.IsFalse(noProvider.CanReviewAxisCandidates);

        var unavailable = new CandidateProvider(canReview: false);
        using var unavailableWorkspace = new ManualPreviewWorkspaceService(axisProposalService: unavailable);
        Assert.IsFalse(unavailableWorkspace.CanReviewAxisCandidates);

        var primary = new CandidateProvider(canReview: false);
        var fallback = new CandidateProvider();
        var composite = new RustFirstAxisProposalService(primary, fallback);
        using var production = new ManualPreviewWorkspaceService(axisProposalService: composite,
            runtimeEnvironment: WorkflowRuntimeEnvironment.Production);
        Assert.IsFalse(production.CanReviewAxisCandidates);

        var disposed = new ManualPreviewWorkspaceService(axisProposalService: new CandidateProvider());
        Assert.IsTrue(disposed.CanReviewAxisCandidates);
        disposed.Dispose();
        Assert.IsFalse(disposed.CanReviewAxisCandidates);

        Assert.IsTrue(composite.CanReviewCandidates);
        AxisCandidateReviewResult result = composite.ReviewCandidatesAsync([1, 2, 3], CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.AreEqual(1, fallback.CandidateCalls);
        Assert.AreEqual(0, primary.CandidateCalls);
        Assert.AreEqual(0, primary.SuggestCalls);
        Assert.AreEqual(0, fallback.SuggestCalls);
        Assert.HasCount(2, result.Candidates);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            composite.ReviewCandidatesAsync([1, 2, 3], canceled.Token).GetAwaiter().GetResult());
        Assert.AreEqual(1, fallback.CandidateCalls, "Cancellation must stop before invoking the fallback worker.");
    }

    [TestMethod]
    public async Task CompositePrefersRustAndFallsBackOnlyForDeclaredUnavailableCases()
    {
        var primary = new CandidateProvider(provider: "rust-review");
        var fallback = new CandidateProvider();
        var result = await new RustFirstAxisProposalService(primary, fallback).ReviewCandidatesAsync([1], CancellationToken.None);
        Assert.AreEqual("rust-review", result.Provider);
        Assert.HasCount(2, result.Candidates);
        Assert.AreEqual(0, fallback.CandidateCalls);

        var noAxis = new CandidateProvider(candidates: Array.Empty<AxisReviewCandidate>(), errorCode: "NO_AXIS", provider: "rust-review");
        result = await new RustFirstAxisProposalService(noAxis, fallback).ReviewCandidatesAsync([1], CancellationToken.None);
        Assert.AreEqual("opencv-review", result.Provider);
        Assert.AreEqual(1, fallback.CandidateCalls);

        foreach (string error in FailClosedRustErrors)
        {
            var failed = new CandidateProvider(candidates: Array.Empty<AxisReviewCandidate>(), errorCode: error, provider: "rust-review");
            result = await new RustFirstAxisProposalService(failed, fallback).ReviewCandidatesAsync([1], CancellationToken.None);
            Assert.AreEqual(error, result.ErrorCode);
            Assert.AreEqual(1, fallback.CandidateCalls);
        }
    }

    private static ManualCalibrationRequest Calibration() =>
        new(new(10, 100), new(10, 10), new(150, 100), 100, 20);

    private sealed class CandidateProvider : IAxisProposalService, IAxisCandidateReviewService
    {
        private readonly IReadOnlyList<AxisReviewCandidate> _candidates;
        private readonly string? _errorCode;
        private readonly bool _canReview;
        private readonly string _provider;

        public CandidateProvider(IReadOnlyList<AxisReviewCandidate>? candidates = null, string? errorCode = null,
            bool canReview = true, string provider = "opencv-review")
        {
            _candidates = candidates ??
            [
                new(0, new(10, 100, 100, 10, 0)),
                new(1, new(20, 105, 140, 8, 0)),
            ];
            _errorCode = errorCode;
            _canReview = canReview;
            _provider = provider;
        }

        public bool CanReviewCandidates => _canReview;
        public int CandidateCalls { get; private set; }
        public int SuggestCalls { get; private set; }
        public byte[]? ReviewInput { get; private set; }
        public TaskCompletionSource<AxisCandidateReviewResult>? Pending { get; set; }
        public IReadOnlyList<AxisReviewCandidate> Candidates => _candidates;

        public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken)
        {
            SuggestCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RustAxisProposalResult(null, "AMBIGUOUS_AXIS"));
        }

        public Task<AxisCandidateReviewResult> ReviewCandidatesAsync(byte[] originalImage, CancellationToken cancellationToken)
        {
            CandidateCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            ReviewInput = (byte[])originalImage.Clone();
            return Pending?.Task ?? Task.FromResult(new AxisCandidateReviewResult(_candidates, _errorCode) { Provider = _provider });
        }
    }

    private static void WithImage(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "axis-candidate-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            StaTestHost.Run(() =>
            {
                string path = Path.Combine(directory, "owned-test.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 120, 96, 96, PixelFormats.Gray8,
                    null, Enumerable.Repeat((byte)255, 160 * 120).ToArray(), 160)));
                using (FileStream stream = File.Create(path)) encoder.Save(stream);
                action(path);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static T Pump<T>(Task<T> task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(_ => System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) System.Windows.Threading.Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private static async Task<bool> Completed(Task task) { await task; return true; }

    private sealed class InspectingOcr : ICalibrationOcrSuggestionService
    {
        internal CalibrationOcrRequest? Request { get; private set; }
        public Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request, ReadOnlyMemory<byte> image, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(CalibrationOcrClientTests.Success(request));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
