// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

public sealed partial class CalibrationTickViewModelTests
{
    [TestMethod]
    public void StaleLabelReadDoesNotSetCalibrationStatusOnAnotherTab()
    {
        WithImage(path =>
        {
            string secondPath = Path.Combine(Path.GetDirectoryName(path)!, "second-owned.png");
            File.Copy(path, secondPath);
            var reader = new OcrStub(ValidProposals()) { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new AxisStub(MeasuredGeometry()),
                calibrationOcrSuggestionService: reader);
            Pump(workspace.ImportImagesAsync([path, secondPath], CancellationToken.None));
            using var vm = new GraphReader.App.ViewModels.MainWindowViewModel(workspace);
            var originalTab = vm.SelectedTab!;
            var otherTab = vm.Tabs.Single(tab => !ReferenceEquals(tab, originalTab));
            var project = workspace.CurrentProject;
            Task operation = vm.SuggestCalibrationFromTicksAsync(CancellationToken.None);
            Assert.IsNotNull(reader.LastRequest);
            Assert.IsFalse(operation.IsCompleted);
            vm.SelectedTab = otherTab;
            string statusBefore = vm.CalibrationTickProposalStatusText;
            reader.Pending!.SetResult(Response(reader.LastRequest!, ValidProposals()));
            Pump(operation);
            Assert.AreSame(otherTab, vm.SelectedTab);
            Assert.AreEqual(statusBefore, vm.CalibrationTickProposalStatusText);
            Assert.AreSame(project, workspace.CurrentProject);
            Assert.IsNull(originalTab.Calibration);
            Assert.IsNull(otherTab.Calibration);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StaleFailureDoesNotOverwriteAnotherTabOrStartOcr(bool combined)
    {
        WithImage(path =>
        {
            string secondPath = Path.Combine(Path.GetDirectoryName(path)!, "second-owned.png");
            File.Copy(path, secondPath);
            var reader = new OcrStub(ValidProposals());
            var pending = new PendingFailureAxis();
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: pending, calibrationOcrSuggestionService: reader);
            Pump(workspace.ImportImagesAsync([path, secondPath], CancellationToken.None));
            using var vm = new GraphReader.App.ViewModels.MainWindowViewModel(workspace);
            Assert.AreEqual(2, vm.Tabs.Count);
            var originalTab = vm.SelectedTab!;
            var otherTab = vm.Tabs.Single(tab => !ReferenceEquals(tab, originalTab));
            var project = workspace.CurrentProject;
            var originalCalibration = originalTab.Calibration;
            var otherCalibration = otherTab.Calibration;
            Task operation = combined ? vm.SuggestCalibrationFromTicksAsync(CancellationToken.None) : vm.SuggestAxesAsync(CancellationToken.None);
            Assert.AreEqual(1, pending.Calls);
            vm.SelectedTab = otherTab;
            string statusBefore = vm.StatusMessageKey;
            var operationStatus = vm.LastOperationStatus;
            pending.Completion.SetResult(new RustAxisProposalResult(null, "OPENCV_AXIS_AMBIGUOUS")
                { Provider = "opencv-review", ReviewReasons = ["native_pair_budget_exceeded"] });
            Pump(operation);
            Assert.AreEqual(statusBefore, vm.StatusMessageKey);
            Assert.AreSame(operationStatus, vm.LastOperationStatus);
            Assert.AreSame(otherTab, vm.SelectedTab);
            Assert.AreEqual(0, reader.CallCount);
            Assert.AreSame(project, workspace.CurrentProject);
            Assert.AreSame(originalCalibration, originalTab.Calibration);
            Assert.AreSame(otherCalibration, otherTab.Calibration);
            Assert.IsFalse(vm.HasAxisSuggestion);
        });
    }

    private sealed class PendingFailureAxis : IAxisProposalService
    {
        internal TaskCompletionSource<RustAxisProposalResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken)
        {
            Calls++;
            return Completion.Task;
        }
    }

    [TestMethod]
    [DataRow("native_pair_budget_exceeded", LocalizationKeys.ManualAxisPairBudgetExceeded, "WORKFLOW_AXIS_PAIR_BUDGET_EXCEEDED")]
    [DataRow("native_envelope_budget_exceeded", LocalizationKeys.ManualAxisPairBudgetExceeded, "WORKFLOW_AXIS_PAIR_BUDGET_EXCEEDED")]
    [DataRow("native_rotated_geometry", LocalizationKeys.ManualAxisRotatedUnsupported, "WORKFLOW_AXIS_ROTATED_UNSUPPORTED")]
    [DataRow("native_axis_timeout", LocalizationKeys.ManualAxisTimeout, "WORKFLOW_AXIS_TIMEOUT")]
    public void MissingGeometryPreservesFailureThroughBothCommandsWithoutOcrOrMutation(string reason, string messageKey, string code)
    {
        WithImage(path =>
        {
            var reader = new OcrStub(ValidProposals());
            var primary = new ResultAxisStub(new RustAxisProposalResult(null, "AMBIGUOUS_AXIS"));
            var fallback = new ResultAxisStub(new RustAxisProposalResult(null, reason == "native_axis_timeout" ? "OPENCV_AXIS_TIMEOUT" : "OPENCV_AXIS_AMBIGUOUS")
                { Provider = "opencv-review", ReviewReasons = reason == "native_axis_timeout" ? [] : [reason] });
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new RustFirstAxisProposalService(primary, fallback), calibrationOcrSuggestionService: reader);
            Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
            using var vm = new GraphReader.App.ViewModels.MainWindowViewModel(workspace);
            var tab = vm.SelectedTab!;
            var project = workspace.CurrentProject;
            var calibration = tab.Calibration;
            var error = Assert.ThrowsExactly<ProductionWorkflowStageException>(() => Pump(workspace.SuggestAxesAsync(tab.TabId, CancellationToken.None)));
            Assert.AreEqual(code, error.Failure.Code);
            Assert.AreEqual(messageKey, error.Failure.UserMessageKey);
            Assert.IsTrue(error.Failure.Recoverable);
            Pump(vm.SuggestAxesAsync(CancellationToken.None));
            Assert.AreEqual(messageKey, vm.StatusMessageKey);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            Assert.AreEqual(messageKey, vm.StatusMessageKey);
            Assert.AreEqual(0, reader.CallCount);
            Assert.AreSame(project, workspace.CurrentProject);
            Assert.AreSame(tab, vm.SelectedTab);
            Assert.AreSame(calibration, tab.Calibration);
            Assert.IsFalse(vm.HasAxisSuggestion);
        });
    }
}
