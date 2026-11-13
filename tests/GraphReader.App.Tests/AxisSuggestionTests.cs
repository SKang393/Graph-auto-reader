// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;
using GraphReader.Domain;
using PixelPoint = GraphReader.Axis.PixelPoint;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class AxisSuggestionTests
{
    [TestMethod]
    public void PairedTickPixelsReplaceLineEndpointsWithoutSupplyingNumericLabels() => WithImage(path =>
    {
        var provider = new ProposalStub { Geometry = new(8, 110, 155, 5, .8, FirstTickX: 20, LastTickX: 140) };
        var workspace = new ManualPreviewWorkspaceService(axisProposalService: provider);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        using var vm = new MainWindowViewModel(workspace);
        Pump(vm.SuggestAxesAsync(CancellationToken.None));
        Assert.IsTrue(vm.HasAxisSuggestion);
        Assert.IsNull(vm.SelectedTab!.Calibration);
        Assert.AreEqual("", vm.SuggestedYMaximum);
        Assert.AreEqual("", vm.SuggestedFirstSession);
        Assert.AreEqual("", vm.SuggestedFinalSession);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        vm.SuggestedYMaximum = "10";
        vm.SuggestedFirstSession = "1";
        vm.SuggestedFinalSession = "24";
        vm.AxisSuggestionReviewed = true;
        Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
        var calibration = vm.SelectedTab.Calibration!;
        Assert.AreEqual(new PixelPoint(20, 110), calibration.Session1Y0);
        Assert.AreEqual(new PixelPoint(20, 5), calibration.Session1YMaximum);
        Assert.AreEqual(new PixelPoint(140, 110), calibration.SessionMaximumY0);
        Assert.AreEqual(1d, calibration.FirstVisibleSession);
        Assert.AreEqual(24d, calibration.XMaximum);
    });

    [TestMethod]
    [DataRow(31)]
    [DataRow(4097)]
    public void UnsupportedOriginalDimensionsAreRejectedBeforeWorkerInvocation(int width) => StaTestHost.Run(() =>
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, 32, 96, 96, PixelFormats.Gray8, null,
            new byte[width * 32], width)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var service = new RustAxisProposalService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.exe"));
        RustAxisProposalResult result = Pump(service.SuggestAsync(stream.ToArray(), CancellationToken.None));
        Assert.IsNull(result.Geometry);
        Assert.AreEqual("INVALID_INPUT", result.ErrorCode);
    });

    [TestMethod]
    public void ProposalRequiresActualLabelsAndSupportsCorrectedAnchorsExportUndoAndReopen() => WithImage(path =>
    {
        var provider = new ProposalStub();
        var workspace = new ManualPreviewWorkspaceService(axisProposalService: provider);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        using var vm = new MainWindowViewModel(workspace);
        WorkspaceTabViewModel tab = vm.SelectedTab!;
        workspace.Calibrate(tab.TabId, Calibration());
        var old = tab.Calibration;
        var series = workspace.AddSeries(tab.TabId, new("Series", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        workspace.AddPoint(tab.TabId, series.SeriesId, 80, 55);
        double oldValue = tab.Points.Single().GraphY;
        Pump(vm.SuggestAxesAsync(CancellationToken.None));
        Assert.IsTrue(vm.HasAxisSuggestion);
        Assert.AreEqual("", vm.SuggestedYMaximum);
        Assert.AreEqual("", vm.SuggestedFirstSession);
        Assert.AreEqual("", vm.SuggestedFinalSession);
        Assert.AreSame(old, tab.Calibration);
        Assert.AreEqual(oldValue, tab.Points.Single().GraphY);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        CollectionAssert.AreEqual(File.ReadAllBytes(path), provider.Input!);

        // Axis endpoints need not coincide with labeled ticks. Correct all three in original pixels.
        vm.StartCalibrationCommand.Execute(null);
        Pump(vm.HandleCanvasPointAsync(new Point(10, 100)));
        vm.SuggestedYMaximum = "10";
        vm.SuggestedFirstSession = "1";
        vm.SuggestedFinalSession = "24";
        vm.AxisSuggestionReviewed = true;
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null), "Incomplete anchor correction cannot apply old endpoints.");
        Pump(vm.HandleCanvasPointAsync(new Point(10, 10)));
        Pump(vm.HandleCanvasPointAsync(new Point(150, 100)));
        Assert.AreSame(old, tab.Calibration, "Correcting proposal positions must not apply scientific values.");
        vm.SuggestedYMaximum = "10";
        vm.SuggestedFirstSession = "1";
        vm.SuggestedFinalSession = "24";
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        vm.AxisSuggestionReviewed = true;
        Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
        Assert.IsFalse(vm.HasAxisSuggestion);
        Assert.AreEqual(10d, tab.Calibration!.YMaximum);
        Assert.AreEqual(24d, tab.Calibration.XMaximum);
        Assert.AreEqual(new PixelPoint(150, 100), tab.Calibration.SessionMaximumY0);
        Assert.AreEqual(5d, tab.Points.Single().GraphY, 1e-9);
        Assert.AreEqual(80d, tab.Points.Single().PixelX);
        Assert.IsTrue(tab.IsDirty);
        var exported = Pump(workspace.ExportAsync(tab.TabId, Path.Combine(Path.GetDirectoryName(path)!, "export"), CancellationToken.None));
        Assert.IsTrue(exported.Succeeded, string.Join(";", exported.Failures));
        string project = Path.ChangeExtension(path, ".garproj");
        Assert.IsTrue(Pump(workspace.SaveProjectAsync(project, CancellationToken.None)).IsSuccess);
        var reopened = new ManualPreviewWorkspaceService();
        Pump(reopened.OpenProjectAsync(project, CancellationToken.None));
        WorkspaceTabViewModel restored = reopened.CreateWorkspace().Single();
        Assert.AreEqual(tab.SourceSha256, restored.SourceSha256);
        Assert.AreEqual(tab.Calibration, restored.Calibration);
        Assert.AreEqual(80d, restored.Points.Single().PixelX);
        vm.UndoCommand.Execute(null);
        Assert.AreEqual(old!.YMaximum, tab.Calibration!.YMaximum);
    });

    [TestMethod]
    public void DismissingPartialProposalCorrectionCancelsClicksWithoutChangingManualCalibration() => WithImage(path =>
    {
        var workspace = new ManualPreviewWorkspaceService(axisProposalService: new ProposalStub());
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        using var vm = new MainWindowViewModel(workspace);
        var tab = vm.SelectedTab!;
        workspace.Calibrate(tab.TabId, Calibration());
        var series = workspace.AddSeries(tab.TabId, new("Series", "●", MarkerShape.Circle,
            MarkerFill.Filled, SemanticRole.Intervention));
        var point = workspace.AddPoint(tab.TabId, series.SeriesId, 80, 55);
        var calibration = tab.Calibration;
        var originalValues = (point.PixelX, point.PixelY, point.GraphX, point.GraphY);
        bool wasDirty = tab.IsDirty;
        int auditCount = workspace.CurrentProject.Audit.Events.Count;

        Pump(vm.SuggestAxesAsync(CancellationToken.None));
        vm.StartCalibrationCommand.Execute(null);
        Pump(vm.HandleCanvasPointAsync(new Point(20, 110)));
        Pump(vm.HandleCanvasPointAsync(new Point(20, 20)));
        Assert.AreEqual(ManualEditorMode.Calibration, vm.EditorMode);
        vm.DismissAxisSuggestionCommand.Execute(null);
        Assert.IsFalse(vm.HasAxisSuggestion);
        Assert.AreEqual(ManualEditorMode.Select, vm.EditorMode);
        Pump(vm.HandleCanvasPointAsync(new Point(140, 110)));
        Assert.AreSame(calibration, tab.Calibration);
        Assert.HasCount(1, tab.Points);
        Assert.AreEqual(originalValues, (point.PixelX, point.PixelY, point.GraphX, point.GraphY));
        Assert.AreEqual(wasDirty, tab.IsDirty);
        Assert.AreEqual(auditCount, workspace.CurrentProject.Audit.Events.Count);

        // Ordinary manual calibration remains available after dismissing a proposal.
        vm.ManualYMaximum = 20;
        vm.ManualXMaximum = 30;
        vm.StartCalibrationCommand.Execute(null);
        Pump(vm.HandleCanvasPointAsync(new Point(10, 100)));
        Pump(vm.HandleCanvasPointAsync(new Point(10, 10)));
        Pump(vm.HandleCanvasPointAsync(new Point(150, 100)));
        Assert.AreEqual(20d, tab.Calibration!.YMaximum);
        Assert.AreEqual(30d, tab.Calibration.XMaximum);
        Assert.AreEqual(10d, point.GraphY, 1e-9);
    });

    [TestMethod]
    public void CancelledAndStaleProposalsCannotReplaceCalibration() => WithImage(path =>
    {
        var provider = new ProposalStub();
        var workspace = new ManualPreviewWorkspaceService(axisProposalService: provider);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        using var vm = new MainWindowViewModel(workspace);
        var tab = vm.SelectedTab!;
        workspace.Calibrate(tab.TabId, Calibration());
        var old = tab.Calibration;
        provider.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = vm.SuggestAxesAsync(CancellationToken.None);
        vm.SelectedTab = null;
        vm.SelectedTab = tab;
        provider.Pending.SetResult(new(new(8, 110, 155, 5, .8), null));
        Pump(pending);
        Assert.IsFalse(vm.HasAxisSuggestion);
        provider.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        pending = vm.SuggestAxesAsync(cancellation.Token);
        cancellation.Cancel();
        provider.Pending.SetResult(new(new(8, 110, 155, 5, .8), null));
        Pump(pending);
        Assert.IsFalse(vm.HasAxisSuggestion);
        Assert.AreSame(old, tab.Calibration);
        provider.Pending = null;
        Pump(vm.SuggestAxesAsync(CancellationToken.None));
        workspace.Calibrate(tab.TabId, Calibration() with { YMaximum = 30 });
        vm.SuggestedYMaximum = "10"; vm.SuggestedFirstSession = "1"; vm.SuggestedFinalSession = "24";
        vm.AxisSuggestionReviewed = true;
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
        Assert.AreEqual(30d, tab.Calibration!.YMaximum);
    });

    private static ManualCalibrationRequest Calibration() => new(new(10, 100), new(10, 10), new(150, 100), 100, 20);
    private sealed class ProposalStub : IAxisProposalService
    {
        public RustAxisGeometry Geometry { get; init; } = new(8, 110, 155, 5, .8);
        public byte[]? Input { get; private set; }
        public TaskCompletionSource<RustAxisProposalResult>? Pending { get; set; }
        public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken)
        {
            Input = originalImage;
            return Pending?.Task ?? Task.FromResult(new RustAxisProposalResult(Geometry, null));
        }
    }
    private static void WithImage(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "axis-proposal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { StaTestHost.Run(() =>
        {
            string path = Path.Combine(directory, "owned-test.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 120, 96, 96, PixelFormats.Gray8, null,
                Enumerable.Repeat((byte)255, 160 * 120).ToArray(), 160)));
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
            action(path);
        }); }
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
