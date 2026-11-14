// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ManualAxisDraftTests
{
    [TestMethod]
    public void ManualDraftAfterUnavailableGeometryIsReviewOnlyUntilExplicitApplyAndUndo()
    {
        WithImage(path =>
        {
            var reader = new OcrStub(ValidProposals());
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new UnavailableAxis(), calibrationOcrSuggestionService: reader);
            Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
            using var vm = new MainWindowViewModel(workspace);
            WorkspaceTabViewModel tab = vm.SelectedTab!;
            bool wasDirty = tab.IsDirty;
            int originalPointCount = tab.Points.Count;
            var originalProject = workspace.CurrentProject;
            Pump(vm.SuggestAxesAsync(CancellationToken.None));
            Assert.IsFalse(vm.HasAxisSuggestion);
            vm.ManualFirstVisibleSessionText = "invalid";
            vm.ManualYMaximum = 0;
            Assert.IsTrue(vm.BeginManualAxisDraftCommand.CanExecute(null));
            vm.BeginManualAxisDraftCommand.Execute(null);
            Assert.IsTrue(vm.IsCollectingManualAxisDraft);
            Pump(vm.HandleCanvasPointAsync(new Point(20, 100)));
            Pump(vm.HandleCanvasPointAsync(new Point(20, 20)));
            Pump(vm.HandleCanvasPointAsync(new Point(140, 100)));
            Assert.IsTrue(vm.IsManualAxisDraft);
            Assert.IsFalse(vm.IsCollectingManualAxisDraft);
            Assert.IsTrue(vm.HasAxisSuggestion);
            Assert.IsNull(tab.Calibration);
            Assert.AreEqual(wasDirty, tab.IsDirty);
            Assert.AreSame(originalProject, workspace.CurrentProject);
            Assert.AreEqual(originalPointCount, tab.Points.Count);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsTrue(vm.ReadCalibrationLabelsCommand.CanExecute(null));
            Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));
            vm.RefitSelectedCalibrationLabelsCommand.Execute(null);
            Assert.AreEqual(1, reader.CallCount);
            Assert.IsTrue(vm.HasCalibrationTickProposal);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsNull(tab.Calibration);
            vm.AxisSuggestionReviewed = true;
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.IsNotNull(tab.Calibration);
            Assert.IsTrue(tab.IsDirty);
            Assert.IsTrue(vm.UndoCommand.CanExecute(null));
            vm.UndoCommand.Execute(null);
            Assert.IsNull(tab.Calibration);
        });
    }

    [TestMethod]
    public void ManualDraftRejectsMalformedOriginalPixelAnchors()
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ValidProposals());
            string tab = workspace.CreateWorkspace()[0].TabId;
            ManualPreviewWorkspaceService service = workspace;
            Assert.IsTrue(service.CanCreateManualAxisDraft);
            Assert.IsNull(service.CreateManualAxisDraft(tab, []));
            Assert.IsNull(service.CreateManualAxisDraft(tab, [new(double.NaN, 100), new(20, 20), new(140, 100)]));
            Assert.IsNull(service.CreateManualAxisDraft(tab, [new(-1, 100), new(20, 20), new(140, 100)]));
            Assert.IsNull(service.CreateManualAxisDraft(tab, [new(20, 100), new(20, 100), new(140, 100)]));
            Assert.IsNull(service.CreateManualAxisDraft(tab, [new(20, 100), new(20, 20), new(20, 100)]));
            Assert.IsNull(service.CreateManualAxisDraft("missing-tab", [new(20, 100), new(20, 20), new(140, 100)]));
            WorkspaceAxisSuggestion? draft = service.CreateManualAxisDraft(tab, [new(20, 100), new(20, 20), new(140, 100)]);
            Assert.IsNotNull(draft);
            Assert.IsTrue(draft.IsUserDefined);
            Assert.AreEqual(0, draft.Geometry.XTickPositions.Count);
            Assert.AreEqual(0, draft.Geometry.YTickPositions.Count);
        });
    }

    [TestMethod]
    public void ManualDraftPartialClickAndCompletedDraftAreInvalidatedByTabChangeOrDismissal()
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ValidProposals());
            using var vm = new MainWindowViewModel(workspace);
            WorkspaceTabViewModel tab = vm.SelectedTab!;
            vm.BeginManualAxisDraftCommand.Execute(null);
            Pump(vm.HandleCanvasPointAsync(new Point(20, 100)));
            Assert.IsTrue(vm.IsCollectingManualAxisDraft);
            Assert.IsTrue(vm.CancelCommand.CanExecute(null));
            vm.CancelCommand.Execute(null);
            Assert.IsFalse(vm.IsCollectingManualAxisDraft);
            Assert.IsFalse(vm.HasAxisSuggestion);
            vm.BeginManualAxisDraftCommand.Execute(null);
            Pump(vm.HandleCanvasPointAsync(new Point(20, 100)));
            vm.SelectedTab = null;
            vm.SelectedTab = tab;
            Assert.IsFalse(vm.IsCollectingManualAxisDraft);
            Assert.IsFalse(vm.IsManualAxisDraft);
            Assert.IsFalse(vm.HasAxisSuggestion);
            Assert.IsNull(tab.Calibration);
            vm.BeginManualAxisDraftCommand.Execute(null);
            Pump(vm.HandleCanvasPointAsync(new Point(20, 100)));
            Pump(vm.HandleCanvasPointAsync(new Point(20, 20)));
            Pump(vm.HandleCanvasPointAsync(new Point(140, 100)));
            Assert.IsTrue(vm.IsManualAxisDraft);
            vm.DismissAxisSuggestionCommand.Execute(null);
            Assert.IsFalse(vm.IsManualAxisDraft);
            Assert.IsFalse(vm.HasAxisSuggestion);
            Assert.IsNull(tab.Calibration);
        });
    }

    [TestMethod]
    public void DraftRequiresReaderAndStaleCalibrationCancelsPendingSelection()
    {
        WithImage(path =>
        {
            using var noReader = new ManualPreviewWorkspaceService();
            Pump(noReader.ImportImagesAsync([path], CancellationToken.None));
            using var noReaderVm = new MainWindowViewModel(noReader);
            Assert.IsFalse(noReaderVm.BeginManualAxisDraftCommand.CanExecute(null));
            using var workspace = CreateWorkspace(path, ValidProposals());
            using var vm = new MainWindowViewModel(workspace);
            WorkspaceTabViewModel tab = vm.SelectedTab!;
            vm.BeginManualAxisDraftCommand.Execute(null);
            Pump(vm.HandleCanvasPointAsync(new Point(20, 100)));
            workspace.Calibrate(tab.TabId, new(new(10, 100), new(10, 20), new(140, 100), 10, 24, 1));
            var changedCalibration = tab.Calibration;
            Pump(vm.HandleCanvasPointAsync(new Point(20, 20)));
            Assert.IsFalse(vm.IsCollectingManualAxisDraft);
            Assert.IsFalse(vm.HasAxisSuggestion);
            Assert.AreSame(changedCalibration, tab.Calibration);
        });
    }

    [TestMethod]
    public void CancelPendingDraftAlsoCancelsActiveBusyOperation()
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ValidProposals());
            using var vm = new MainWindowViewModel(workspace);
            vm.BeginManualAxisDraftCommand.Execute(null);
            Pump(vm.HandleCanvasPointAsync(new Point(20, 100)));
            Assert.IsTrue(vm.IsCollectingManualAxisDraft);
            CancellationToken operationToken = default;
            Func<CancellationToken, Task> action = token =>
            {
                operationToken = token;
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            };
            MethodInfo runBusy = typeof(MainWindowViewModel).GetMethod("RunBusyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var operation = (Task)runBusy.Invoke(vm, [action, CancellationToken.None])!;
            Assert.IsTrue(vm.IsBusy);
            Assert.IsTrue(vm.CancelCommand.CanExecute(null));
            vm.CancelCommand.Execute(null);
            Pump(operation);
            Assert.IsTrue(operationToken.IsCancellationRequested);
            Assert.IsFalse(vm.IsBusy);
            Assert.IsFalse(vm.IsCollectingManualAxisDraft);
            Assert.IsNull(vm.SelectedTab!.Calibration);
        });
    }

    [TestMethod]
    public void OrdinaryThreeAnchorCalibrationStillWorksWithoutDraft()
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ValidProposals());
            using var vm = new MainWindowViewModel(workspace);
            vm.ManualYMaximum = 10;
            vm.ManualXMaximum = 24;
            vm.ManualFirstVisibleSessionText = "1";
            vm.StartCalibrationCommand.Execute(null);
            Pump(vm.HandleCanvasPointAsync(new Point(20, 100)));
            Pump(vm.HandleCanvasPointAsync(new Point(20, 20)));
            Pump(vm.HandleCanvasPointAsync(new Point(140, 100)));
            Assert.IsFalse(vm.IsManualAxisDraft);
            Assert.IsNotNull(vm.SelectedTab!.Calibration);
        });
    }

    private sealed class UnavailableAxis : IAxisProposalService
    {
        public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken) =>
            Task.FromResult(new RustAxisProposalResult(null, "NO_AXIS"));
    }

    private static ManualPreviewWorkspaceService CreateWorkspace(string path, IReadOnlyList<CalibrationOcrProposal> proposals,
        RustAxisGeometry? geometry = null) => CreateWorkspace(path, new OcrStub(proposals), geometry);

    private static RustAxisGeometry EstimatedGeometry(bool estimateY = true) => new(8, 110, 155, 5, .9)
    {
        XTickPositions = [],
        YTickPositions = estimateY ? [] : [20, 60, 100],
    };

    private static ManualPreviewWorkspaceService CreateWorkspace(string path, OcrStub reader, RustAxisGeometry? geometry = null,
        AxisStub? axis = null)
    {
        geometry ??= new RustAxisGeometry(8, 110, 155, 5, .9)
            { XTickPositions = [20, 80, 140], YTickPositions = [20, 60, 100] };
        var workspace = new ManualPreviewWorkspaceService(axisProposalService: axis ?? new AxisStub(geometry),
            calibrationOcrSuggestionService: reader);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        return workspace;
    }

    private static CalibrationOcrProposal[] ValidProposals() =>
    [
        Proposal("x-first", "1", 1, "XTick", "X", 20, 112),
        Proposal("x-mid", "12.5", 12.5, "XTick", "X", 80, 112),
        Proposal("x-final", "24", 24, "XTick", "X", 140, 112),
        Proposal("y-max", "10", 10, "YTick", "Y", 20, 2),
        Proposal("y-mid", "5", 5, "YTick", "Y", 60, 2),
        Proposal("y-zero", "0", 0, "YTick", "Y", 100, 2),
    ];

    private static CalibrationOcrProposal Proposal(string id, string text, double value, string role,
        string axis, int tick, int cross) => role == "XTick"
        ? new(id, [new(tick - 3, cross), new(tick + 3, cross), new(tick + 3, cross + 4)], text, .95,
            value, value.ToString("R", CultureInfo.InvariantCulture), true, role, axis, null)
        : new(id, [new(cross, tick - 2), new(cross + 5, tick - 2), new(cross + 5, tick + 2)], text, .95,
            value, value.ToString("R", CultureInfo.InvariantCulture), true, role, axis, null);

    private static CalibrationOcrResponse Response(CalibrationOcrRequest request,
        IReadOnlyList<CalibrationOcrProposal> proposals)
    {
        int xCount = proposals.Count(proposal => proposal.Role == "XTick");
        int yCount = proposals.Count(proposal => proposal.Role == "YTick");
        var xSequence = new CalibrationOcrSequenceCheck(false, xCount, xCount, 0, []);
        var ySequence = new CalibrationOcrSequenceCheck(false, yCount, yCount, 0, []);
        var runtime = new CalibrationOcrRuntime(
            GraphReader.App.Integration.CalibrationOcr.CalibrationOcrSession.ModelSha256,
            GraphReader.App.Integration.CalibrationOcr.CalibrationOcrSession.ManifestSha256, "Cpu", 1, 1, 1, 1, false,
            new(16, 312, .4992, true), new(100, 100, 100, 100, 100, 100),
            [new("GraphReader.App", new string('c', 64)), new("GraphReader.Inference", new string('d', 64)),
             new("GraphReader.Ocr", new string('e', 64)), new("onnxruntime.dll", new string('f', 64))]);
        return new(request, proposals, xSequence, ySequence, new(1, 1, 1, 1, 1, 5, false), runtime, null);
    }

    private static void WithImage(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "calibration-ticks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            StaTestHost.Run(() =>
            {
                string path = Path.Combine(directory, "owned-test.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 120, 96, 96, PixelFormats.Gray8, null,
                    Enumerable.Repeat((byte)255, 160 * 120).ToArray(), 160)));
                using (FileStream stream = File.Create(path)) encoder.Save(stream);
                action(path);
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

    private sealed class AxisStub(RustAxisGeometry geometry) : IAxisProposalService
    {
        public int CallCount { get; private set; }
        public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new RustAxisProposalResult(geometry, null));
        }
    }

    private sealed class OcrStub(IReadOnlyList<CalibrationOcrProposal> proposals) : ICalibrationOcrSuggestionService
    {
        public TaskCompletionSource<CalibrationOcrResponse>? Pending { get; set; }
        public CalibrationOcrRequest? LastRequest { get; private set; }
        public CalibrationOcrResponse? LastResponse { get; private set; }
        public int CallCount { get; private set; }

        public Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request,
            ReadOnlyMemory<byte> encodedOriginalPanel, CancellationToken cancellationToken)
        {
            LastRequest = request;
            CallCount++;
            CalibrationOcrResponse response = Response(request, proposals);
            LastResponse = response;
            return Pending?.Task ?? Task.FromResult(response);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
