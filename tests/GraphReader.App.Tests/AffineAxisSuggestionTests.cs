// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Reflection;
using System.Windows;
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
public sealed class AffineAxisSuggestionTests
{
    // Deliberate sub-two-pixel tilt: Apply must retain the supplied frame, rather than
    // use the default manual-click tolerance. No geometry is supplied to an OCR worker.
    private static readonly RustAffineAxisGeometry Frame = new(new(90, 290), new(350, 291), new(92, 70), .6);

    [TestMethod]
    public void ExplicitCoordinateModeSwitchClearsXValuesAndReviewWithoutRereadingGeometryOrLabels() => WithWorkspace((workspace, axes, ocr, ticks, _) =>
    {
        using var vm = new MainWindowViewModel(workspace);
        Pump(vm.SuggestAxesAsync(CancellationToken.None));
        vm.SuggestedYMaximum = "10";
        vm.SuggestedFirstSession = "1";
        vm.SuggestedFinalSession = "5";
        vm.AxisSuggestionReviewed = true;
        vm.ManualUsesPrintedXCoordinates = true;
        Assert.IsTrue(vm.HasAffineAxisSuggestion);
        Assert.IsTrue(vm.SuggestedUsesPrintedXCoordinates);
        Assert.AreEqual("10", vm.SuggestedYMaximum);
        Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
        Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        vm.SuggestedFirstSession = "0";
        vm.SuggestedFinalSession = "4";
        vm.AxisSuggestionReviewed = true;
        vm.ManualUsesPrintedXCoordinates = false;
        Assert.IsFalse(vm.SuggestedUsesPrintedXCoordinates);
        Assert.AreEqual("10", vm.SuggestedYMaximum);
        Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
        Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.AreEqual(1, axes.Calls);
        Assert.AreEqual(0, ocr.Calls);
        Assert.AreEqual(0, ticks.Calls);
        Assert.IsNull(vm.SelectedTab!.Calibration);
    });

    [TestMethod]
    public void CombinedStageStopsAtAffineManualDraftAndDismissLeavesProjectUntouched() => WithWorkspace((workspace, axes, ocr, ticks, _) =>
    {
        using var vm = new MainWindowViewModel(workspace);
        var project = workspace.CurrentProject;
        Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
        Assert.IsTrue(vm.HasAffineAxisSuggestion);
        Assert.AreEqual(1, axes.Calls);
        Assert.AreEqual(0, ocr.Calls);
        Assert.AreEqual(0, ticks.Calls);
        Assert.IsNull(vm.SelectedTab!.Calibration);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
        Assert.AreEqual(LocalizationKeys.ManualAffineAxisSuggestionNotice, vm.StatusMessage);
        vm.DismissAxisSuggestionCommand.Execute(null);
        Assert.IsFalse(vm.HasAxisSuggestion);
        Assert.IsFalse(vm.HasAffineAxisSuggestion);
        Assert.AreSame(project, workspace.CurrentProject);
        Assert.IsFalse(vm.UndoCommand.CanExecute(null));
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeFrameNeedsManualValuesAndFreshReviewThenApplyUndoMapsActualAnchors(bool printed) => WithWorkspace((workspace, axes, ocr, ticks, path) =>
    {
        var tab = workspace.CreateWorkspace().Single();
        workspace.Calibrate(tab.TabId, new(new(70, 280), new(70, 60), new(330, 280), 100, 20));
        string series = workspace.AddSeries(tab.TabId, new("Owned", "●", MarkerShape.Circle,
            MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
        var point = workspace.AddPoint(tab.TabId, series, 220.8, 202.5);
        var originalCalibration = tab.Calibration;
        double originalY = point.GraphY;
        using var vm = new MainWindowViewModel(workspace);
        vm.ManualUsesPrintedXCoordinates = printed;
        var originalProject = workspace.CurrentProject;
        bool dirty = tab.IsDirty;
        Pump(vm.SuggestAxesAsync(CancellationToken.None));
        Assert.IsTrue(vm.HasAffineAxisSuggestion);
        Assert.AreEqual(printed, vm.SuggestedUsesPrintedXCoordinates);
        Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
        Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
        Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Assert.IsFalse(vm.ReadCalibrationLabelsCommand.CanExecute(null));
        Assert.IsFalse(vm.SuggestCalibrationFromTicksCommand.CanExecute(null));
        Assert.AreSame(originalProject, workspace.CurrentProject);
        Assert.AreSame(originalCalibration, tab.Calibration);
        Assert.AreEqual(dirty, tab.IsDirty);
        Assert.IsFalse(vm.UndoCommand.CanExecute(null));
        CollectionAssert.AreEqual(File.ReadAllBytes(path), axes.Image!);
        Point[] preview = (Point[])typeof(ManualReviewOverlay).GetField("_axisProposal", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(tab.OverlayContent)!;
        CollectionAssert.AreEqual(new[] { new Point(90, 290), new Point(92, 70), new Point(350, 291) }, preview);
        Assert.AreEqual(LocalizationKeys.ManualAffineAxisSuggestionNotice, vm.StatusMessage);
        vm.SuggestedYMaximum = "10";
        vm.SuggestedFirstSession = printed ? "0" : "1";
        vm.SuggestedFinalSession = printed ? "4" : "5";
        vm.AxisSuggestionReviewed = true;
        Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
        Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));
        Assert.AreEqual(1, axes.Calls, "An existing affine proposal must not trigger another automatic geometry pass.");
        Assert.AreEqual(0, ocr.Calls);
        Assert.AreEqual(0, ticks.Calls);
        Assert.AreEqual("10", vm.SuggestedYMaximum);
        Assert.IsTrue(vm.AxisSuggestionReviewed, "Blocked OCR actions must not clear an otherwise current manual draft.");
        vm.SuggestedYMaximum = "11";
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        vm.SuggestedYMaximum = "10";
        vm.AxisSuggestionReviewed = true;
        Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
        Assert.IsFalse(vm.HasAxisSuggestion);
        Assert.IsFalse(vm.HasAffineAxisSuggestion);
        Assert.AreEqual(new GraphReader.Axis.PixelPoint(90, 290), tab.Calibration!.Session1Y0);
        Assert.AreEqual(new GraphReader.Axis.PixelPoint(92, 70), tab.Calibration.Session1YMaximum);
        Assert.AreEqual(new GraphReader.Axis.PixelPoint(350, 291), tab.Calibration.SessionMaximumY0);
        Assert.IsNull(tab.Calibration.XTransform);
        Assert.IsNull(tab.Calibration.YTransform);
        Assert.AreEqual(printed ? 2 : 3, point.GraphX, 1e-9);
        Assert.AreEqual(4d, point.GraphY, 1e-9);
        var calibration = workspace.CurrentProject.Panels.Single().Calibration!;
        Assert.IsTrue(calibration.UserConfirmed);
        Assert.IsNull(calibration.SessionLattice);
        Assert.IsTrue(calibration.Anchors.All(anchor => anchor.EvidenceRegionId is null));
        CollectionAssert.Contains(calibration.Reasons.ToArray(), ManualCalibrationMapper.AffineReason);
        Assert.IsTrue(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        Assert.AreEqual(originalCalibration, tab.Calibration);
        Assert.AreEqual(originalY, tab.Points.Single().GraphY);
        vm.RedoCommand.Execute(null);
        Assert.AreEqual(4d, tab.Points.Single().GraphY, 1e-9);
        Assert.AreEqual(0, ocr.Calls);
        Assert.AreEqual(0, ticks.Calls);
    });

    [TestMethod]
    public void DirectWorkspaceReadRejectsAffineAndMixedContextsBeforeAnyConsumer() => WithWorkspace((workspace, _, ocr, ticks, _) =>
    {
        var tab = workspace.CreateWorkspace().Single();
        var suggestion = Pump(workspace.SuggestAxesAsync(tab.TabId, CancellationToken.None));
        Assert.IsNotNull(suggestion);
        Assert.IsNull(suggestion.Geometry);
        Assert.AreEqual(Frame, suggestion.AffineGeometry);
        Assert.IsTrue(workspace.IsAxisSuggestionCurrent(suggestion));
        Assert.IsNull(Pump(workspace.ReadCalibrationLabelsAsync(suggestion, CancellationToken.None)));
        var mixed = suggestion with { Geometry = new(90, 290, 350, 70, .6) };
        Assert.IsFalse(workspace.IsAxisSuggestionCurrent(mixed));
        Assert.IsNull(Pump(workspace.ReadCalibrationLabelsAsync(mixed, CancellationToken.None)));
        Assert.IsFalse(workspace.IsAxisSuggestionCurrent(suggestion with { AffineGeometry = Frame with { XEnd = new(1000, 291) } }));
        Assert.AreEqual(0, ocr.Calls);
        Assert.AreEqual(0, ticks.Calls);
        var request = new CalibrationOcrRequest("owned", suggestion.Binding, new string('a', 64), new string('b', 64), 480, 400, new(90, 70, 350, 290));
        Assert.ThrowsExactly<ArgumentException>(() => new WorkspaceCalibrationLabelSuggestion(suggestion,
            CalibrationOcrResponse.Failure(request, CalibrationOcrReasonCodes.InvalidInput)));
    });

    [TestMethod]
    [DataRow("mixed")]
    [DataRow("bounds")]
    [DataRow("nonfinite")]
    [DataRow("provider")]
    [DataRow("error")]
    [DataRow("null_reasons")]
    [DataRow("oversized_reasons")]
    public void InvalidNativeFrameCannotEstablishWorkspaceContext(string invalid) => WithWorkspace((workspace, axes, ocr, ticks, _) =>
    {
        axes.Result = invalid switch
        {
            "mixed" => axes.Result with { Geometry = new(90, 290, 350, 70, .6) },
            "bounds" => axes.Result with { AffineGeometry = Frame with { XEnd = new(470, 291), YEnd = new(100, 70) } },
            "nonfinite" => axes.Result with { AffineGeometry = Frame with { Origin = new(double.NaN, 290) } },
            "provider" => axes.Result with { Provider = "rust" },
            "null_reasons" => axes.Result with { ReviewReasons = null! },
            "oversized_reasons" => axes.Result with { ReviewReasons = Enumerable.Repeat("native_affine_geometry", 33).ToArray() },
            _ => axes.Result with { ErrorCode = "OPENCV_AXIS_AMBIGUOUS" },
        };
        var tab = workspace.CreateWorkspace().Single();
        var before = workspace.CurrentProject;
        Assert.IsNull(Pump(workspace.SuggestAxesAsync(tab.TabId, CancellationToken.None)));
        Assert.AreSame(before, workspace.CurrentProject);
        Assert.IsNull(tab.Calibration);
        Assert.AreEqual(0, ocr.Calls);
        Assert.AreEqual(0, ticks.Calls);
    });

    [TestMethod]
    [DataRow("binding")]
    [DataRow("tab")]
    [DataRow("cancel")]
    public void LateAffineReplyCannotPublishAfterBindingTabOrCancellationChanges(string change) => WithWorkspace((workspace, axes, ocr, ticks, _) =>
    {
        axes.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = new MainWindowViewModel(workspace);
        var tab = vm.SelectedTab!;
        using var cancellation = new CancellationTokenSource();
        var operation = vm.SuggestAxesAsync(cancellation.Token);
        Assert.AreEqual(1, axes.Calls);
        if (change == "tab") vm.SelectedTab = null;
        else if (change == "cancel") cancellation.Cancel();
        else workspace.Calibrate(tab.TabId, new(new(70, 280), new(70, 60), new(330, 280), 100, 20));
        var project = workspace.CurrentProject;
        axes.Pending.SetResult(axes.Result);
        Pump(operation);
        Assert.IsFalse(vm.HasAxisSuggestion);
        Assert.IsFalse(vm.HasAffineAxisSuggestion);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Assert.AreSame(project, workspace.CurrentProject);
        Assert.AreEqual(0, ocr.Calls);
        Assert.AreEqual(0, ticks.Calls);
    });

    private static void WithWorkspace(Action<ManualPreviewWorkspaceService, AxisStub, OcrStub, TickStub, string> action) => StaTestHost.Run(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "affine-proposal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "owned.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(480, 400, 96, 96, PixelFormats.Gray8, null,
                Enumerable.Repeat((byte)255, 480 * 400).ToArray(), 480)));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var axes = new AxisStub(); var ocr = new OcrStub(); var ticks = new TickStub();
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: axes,
                calibrationOcrSuggestionService: ocr, axisTickMeasurementService: ticks);
            Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
            action(workspace, axes, ocr, ticks, path);
        }
        finally { Directory.Delete(directory, true); }
    });

    private sealed class AxisStub : IAxisProposalService
    {
        internal RustAxisProposalResult Result { get; set; } = new(null, null)
            { Provider = "opencv-review", AffineGeometry = Frame, ReviewReasons = ["native_affine_geometry"] };
        internal TaskCompletionSource<RustAxisProposalResult>? Pending { get; set; }
        internal int Calls { get; private set; }
        internal byte[]? Image { get; private set; }
        public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken)
        { Calls++; Image = originalImage; return Pending?.Task ?? Task.FromResult(Result); }
    }

    private sealed class OcrStub : ICalibrationOcrSuggestionService
    {
        internal int Calls { get; private set; }
        public Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request, ReadOnlyMemory<byte> image, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Affine proposals cannot call OCR."); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TickStub : IAxisTickMeasurementService
    {
        internal int Calls { get; private set; }
        public Task<AxisTickMeasurementResult> MeasureAsync(byte[] image, RustAxisGeometry geometry, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Affine proposals cannot call the rectangular tick scanner."); }
    }

    private static void Pump(Task task) => Pump(Complete(task));
    private static async Task<bool> Complete(Task task) { await task; return true; }
    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }
}
