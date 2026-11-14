// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationLabelViewModelTests
{
    [TestMethod]
    [DataRow("Cpu", null, LocalizationKeys.SettingsAxisLabelsLastCpu)]
    [DataRow("DirectMl", null, LocalizationKeys.SettingsAxisLabelsLastGpu)]
    [DataRow("Cpu", "CALIBRATION_OCR_ADAPTER_UNAVAILABLE", LocalizationKeys.SettingsAxisLabelsFallbackAdapter)]
    [DataRow("Cpu", "CALIBRATION_OCR_GPU_MEMORY_BUDGET_UNAVAILABLE", LocalizationKeys.SettingsAxisLabelsFallbackMemoryCheck)]
    [DataRow("Cpu", "CALIBRATION_OCR_GPU_MEMORY_HEADROOM_LOW", LocalizationKeys.SettingsAxisLabelsFallbackMemoryLow)]
    [DataRow("Cpu", "CALIBRATION_OCR_DIRECTML_SESSION_FAILED", LocalizationKeys.SettingsAxisLabelsFallbackStartup)]
    [DataRow("Cpu", "CALIBRATION_OCR_DIRECTML_INFERENCE_FAILED", LocalizationKeys.SettingsAxisLabelsFallbackReading)]
    public void LastRuntimeShowsActualDeviceAndCpuFallback(string provider, string? fallbackCode, string expectedKey) => WithImage(path =>
    {
        var reader = new CalibrationOcrStub(request =>
        {
            CalibrationOcrResponse response = ResponseFor(DefaultProposals(), request);
            return Task.FromResult(response with
            {
                Runtime = response.Runtime! with
                {
                    Provider = provider,
                    RequestedProvider = provider == "DirectMl" || fallbackCode is not null ? "DirectMl" : "Cpu",
                    AdapterIndex = provider == "DirectMl" ? 1 : null,
                    AdapterLuid = provider == "DirectMl" ? 200 : null,
                    AdapterName = provider == "DirectMl" ? "Test graphics" : null,
                    PacingRatio = provider == "DirectMl" ? 1 : 0,
                    FallbackReason = fallbackCode,
                },
            });
        });
        using var workspace = new ManualPreviewWorkspaceService(
            axisProposalService: new AxisProposalStub(), calibrationOcrSuggestionService: reader);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        var localization = new LocalizationService(new ResourceDictionary());
        using var vm = new MainWindowViewModel(workspace, localization);
        Assert.IsFalse(vm.HasCalibrationDeviceRuntime);
        Pump(vm.SuggestAxesAsync(CancellationToken.None));
        Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));
        Assert.IsTrue(vm.HasCalibrationDeviceRuntime);
        Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, localization.GetString(expectedKey), "Test graphics"),
            vm.CalibrationDeviceRuntimeText);
        vm.DismissAxisSuggestionCommand.Execute(null);
        Assert.IsTrue(vm.HasCalibrationDeviceRuntime, "The last completed read remains identifiable after dismissing a proposal.");
    });

    [TestMethod]
    public void LabelsAreReviewOnlyUntilAUserSelectsAndConfirmsValues() => WithImage(path =>
    {
        var reader = new CalibrationOcrStub();
        using var workspace = new ManualPreviewWorkspaceService(
            axisProposalService: new AxisProposalStub(), calibrationOcrSuggestionService: reader);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        using var vm = new MainWindowViewModel(workspace);
        WorkspaceTabViewModel tab = vm.SelectedTab!;

        Pump(vm.SuggestAxesAsync(CancellationToken.None));
        Assert.IsTrue(vm.ReadCalibrationLabelsCommand.CanExecute(null));
        Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));

        Assert.HasCount(3, vm.CalibrationLabelRows);
        Assert.IsNull(vm.SelectedCalibrationLabel, "Reading labels must not select a row automatically.");
        Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
        Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
        Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
        Assert.IsNull(tab.Calibration, "Reading labels must not change calibration.");
        Assert.IsFalse(vm.AxisSuggestionReviewed);

        vm.SelectedCalibrationLabel = vm.CalibrationLabelRows.Single(row => row.Proposal.RegionId == "y-max");
        Assert.AreEqual(string.Empty, vm.SuggestedYMaximum, "Selecting a row must not write a calibration field.");
        vm.UseCalibrationLabelForYMaximumCommand.Execute(null);
        Assert.AreEqual("10", vm.SuggestedYMaximum);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.IsNull(tab.Calibration, "Choosing a label must not apply calibration.");

        vm.SelectedCalibrationLabel = vm.CalibrationLabelRows.Single(row => row.Proposal.RegionId == "x-first");
        vm.UseCalibrationLabelForFirstSessionCommand.Execute(null);
        Assert.AreEqual("1", vm.SuggestedFirstSession);
        vm.SelectedCalibrationLabel = vm.CalibrationLabelRows.Single(row => row.Proposal.RegionId == "x-final");
        vm.UseCalibrationLabelForFinalSessionCommand.Execute(null);
        Assert.AreEqual("24", vm.SuggestedFinalSession);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.IsNull(tab.Calibration);

        vm.AxisSuggestionReviewed = true;
        Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
        Assert.AreEqual(10d, tab.Calibration!.YMaximum);
        Assert.AreEqual(1d, tab.Calibration.FirstVisibleSession);
        Assert.AreEqual(24d, tab.Calibration.XMaximum);
    });

    [TestMethod]
    public void AnyEditedCalibrationFieldResetsTheReviewConfirmation() => WithImage(path =>
    {
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new AxisProposalStub());
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        using var vm = new MainWindowViewModel(workspace);
        Pump(vm.SuggestAxesAsync(CancellationToken.None));

        vm.AxisSuggestionReviewed = true;
        vm.SuggestedYMaximum = "25";
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        vm.AxisSuggestionReviewed = true;
        vm.SuggestedFirstSession = "2";
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        vm.AxisSuggestionReviewed = true;
        vm.SuggestedFinalSession = "30";
        Assert.IsFalse(vm.AxisSuggestionReviewed);
    });

    [TestMethod]
    public void CorrectionClearsOldLabelsAndReadUsesCorrectedOriginalPixelGeometry() => WithImage(path =>
    {
        var reader = new CalibrationOcrStub();
        using var workspace = new ManualPreviewWorkspaceService(
            axisProposalService: new AxisProposalStub(), calibrationOcrSuggestionService: reader);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        using var vm = new MainWindowViewModel(workspace);
        WorkspaceTabViewModel tab = vm.SelectedTab!;
        Pump(vm.SuggestAxesAsync(CancellationToken.None));
        Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));
        vm.SelectedCalibrationLabel = vm.CalibrationLabelRows[0];
        vm.SuggestedYMaximum = "10";
        vm.SuggestedFirstSession = "1";
        vm.SuggestedFinalSession = "24";
        vm.AxisSuggestionReviewed = true;

        vm.StartCalibrationCommand.Execute(null);
        Assert.IsFalse(vm.HasCalibrationLabelSuggestion);
        Assert.AreEqual(0, vm.CalibrationLabelRows.Count);
        Assert.IsNull(vm.SelectedCalibrationLabel);
        Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
        Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
        Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.IsNull(tab.Calibration);

        Pump(vm.HandleCanvasPointAsync(new Point(10, 100)));
        Pump(vm.HandleCanvasPointAsync(new Point(10, 20)));
        Pump(vm.HandleCanvasPointAsync(new Point(150, 100)));
        Assert.IsTrue(vm.ReadCalibrationLabelsCommand.CanExecute(null), "A completed anchor correction must allow a fresh read.");
        Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));
        Assert.AreEqual(new CalibrationOcrPlot(10, 20, 150, 100), reader.Requests[^1].Plot);
        Assert.IsNull(tab.Calibration, "Correcting proposal geometry must not apply scientific values.");
    });

    [TestMethod]
    public void SequenceWarningAndRecognitionFailuresRemainVisibleForManualReview() => WithImage(path =>
    {
        var reader = new CalibrationOcrStub(request => Task.FromResult(ResponseFor(
            [
                Proposal("y-max", "O", null, false, "YTick", "Y", CalibrationOcrReasonCodes.RecognitionFailed),
                Proposal("x-first", "1", 1, true, "XTick", "X"),
                Proposal("x-final", "24", 24, true, "XTick", "X"),
            ], request, sequenceNeedsReview: true)));
        using var workspace = new ManualPreviewWorkspaceService(
            axisProposalService: new AxisProposalStub(), calibrationOcrSuggestionService: reader);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        using var vm = new MainWindowViewModel(workspace);
        Pump(vm.SuggestAxesAsync(CancellationToken.None));
        Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));

        Assert.HasCount(3, vm.CalibrationLabelRows, "Nonnumeric and failed rows must not be dropped.");
        Assert.IsTrue(vm.HasCalibrationLabelWarning);
        Assert.IsTrue(vm.CalibrationLabelWarningText.Length > 0);
        CalibrationLabelRowViewModel failed = vm.CalibrationLabelRows.Single(row => row.Proposal.RegionId == "y-max");
        Assert.AreEqual(LocalizationKeys.ManualCalibrationLabelsRowWithFailure, failed.DisplayText);
        Assert.IsNull(vm.SelectedCalibrationLabel);
        vm.SelectedCalibrationLabel = failed;
        Assert.IsFalse(vm.UseCalibrationLabelForYMaximumCommand.CanExecute(null));
        Assert.IsNull(vm.SelectedTab!.Calibration);
    });

    [TestMethod]
    public void StaleReadCannotReappearAfterTabSelectionChanges() => WithImage(path =>
    {
        var reader = new CalibrationOcrStub();
        using var workspace = new ManualPreviewWorkspaceService(
            axisProposalService: new AxisProposalStub(), calibrationOcrSuggestionService: reader);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        using var vm = new MainWindowViewModel(workspace);
        WorkspaceTabViewModel tab = vm.SelectedTab!;
        Pump(vm.SuggestAxesAsync(CancellationToken.None));
        reader.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = vm.ReadCalibrationLabelsAsync(CancellationToken.None);
        CalibrationOcrRequest request = reader.LastRequest!;
        vm.SelectedTab = null;
        vm.SelectedTab = tab;
        reader.Pending.SetResult(ResponseFor(DefaultProposals(), request));
        Pump(pending);

        Assert.IsFalse(vm.HasCalibrationLabelSuggestion);
        Assert.AreEqual(0, vm.CalibrationLabelRows.Count);
        Assert.IsNull(vm.SelectedCalibrationLabel);
        Assert.IsNull(tab.Calibration);
    });

    [TestMethod]
    public void MissingReaderKeepsManualEntryAvailableAndExplainsFallback() => WithImage(path =>
    {
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new AxisProposalStub());
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        using var vm = new MainWindowViewModel(workspace);
        WorkspaceTabViewModel tab = vm.SelectedTab!;
        Pump(vm.SuggestAxesAsync(CancellationToken.None));

        Assert.IsFalse(vm.ReadCalibrationLabelsCommand.CanExecute(null));
        Assert.AreEqual(LocalizationKeys.ManualCalibrationLabelsReaderUnavailable, vm.CalibrationLabelStatusText);
        vm.SuggestedYMaximum = "10";
        vm.SuggestedFirstSession = "1";
        vm.SuggestedFinalSession = "24";
        vm.AxisSuggestionReviewed = true;
        Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
        Assert.AreEqual(10d, tab.Calibration!.YMaximum);
    });

    private static IReadOnlyList<CalibrationOcrProposal> DefaultProposals() =>
    [
        Proposal("y-max", "10", 10, true, "YTick", "Y"),
        Proposal("x-first", "1", 1, true, "XTick", "X"),
        Proposal("x-final", "24", 24, true, "XTick", "X"),
    ];

    private static CalibrationOcrProposal Proposal(string id, string text, double? value, bool literal,
        string role, string axis, string? failure = null) => new(id,
        [new(20, 30), new(22, 30), new(22, 32)], text, .95, value,
        value?.ToString("R", CultureInfo.InvariantCulture), literal, role, axis, failure);

    private static CalibrationOcrResponse ResponseFor(IReadOnlyList<CalibrationOcrProposal> proposals,
        CalibrationOcrRequest? request = null, bool sequenceNeedsReview = false)
    {
        int count = proposals.Count;
        var sequence = new CalibrationOcrSequenceCheck(sequenceNeedsReview, count, count, 0, []);
        var runtime = new CalibrationOcrRuntime(
            GraphReader.App.Integration.CalibrationOcr.CalibrationOcrSession.ModelSha256,
            GraphReader.App.Integration.CalibrationOcr.CalibrationOcrSession.ManifestSha256, "Cpu", 1, 1, 1, 1, false,
            new(16, 312, .4992, true), new(100, 100, 100, 100, 100, 100),
            [new("GraphReader.App", new string('c', 64)), new("GraphReader.Inference", new string('d', 64)),
             new("GraphReader.Ocr", new string('e', 64)), new("onnxruntime.dll", new string('f', 64))]);
        return new(request, proposals, sequence, sequence, new(1, 1, 1, 1, 1, 5, false), runtime, null);
    }

    private static void WithImage(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "calibration-labels-" + Guid.NewGuid().ToString("N"));
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

    private sealed class AxisProposalStub : IAxisProposalService
    {
        public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken) =>
            Task.FromResult(new RustAxisProposalResult(new(8, 110, 155, 5, .9), null));
    }

    private sealed class CalibrationOcrStub(Func<CalibrationOcrRequest, Task<CalibrationOcrResponse>>? response = null)
        : ICalibrationOcrSuggestionService
    {
        private readonly Func<CalibrationOcrRequest, Task<CalibrationOcrResponse>> _response = response ??
            (request => Task.FromResult(ResponseFor(DefaultProposals(), request)));
        public List<CalibrationOcrRequest> Requests { get; } = [];
        public TaskCompletionSource<CalibrationOcrResponse>? Pending { get; set; }
        public CalibrationOcrRequest? LastRequest => Requests.LastOrDefault();

        public Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request,
            ReadOnlyMemory<byte> encodedOriginalPanel, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Pending?.Task ?? _response(request);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
