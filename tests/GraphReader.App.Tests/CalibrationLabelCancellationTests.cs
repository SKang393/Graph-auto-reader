// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;

namespace GraphReader.App.Tests;

public sealed partial class CalibrationTickViewModelTests
{
    [TestMethod]
    public void SwitchingTabsCancelsPendingLabelReadAndReleasesBusyState()
    {
        WithImage(path =>
        {
            string otherPath = Path.Combine(Path.GetDirectoryName(path)!, "other-owned.png");
            File.Copy(path, otherPath);
            var reader = new CooperativeLabelReader();
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new AxisStub(MeasuredGeometry()),
                calibrationOcrSuggestionService: reader);
            Pump(workspace.ImportImagesAsync([path, otherPath], CancellationToken.None));
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestAxesAsync(CancellationToken.None));
            var original = vm.SelectedTab!;
            var other = vm.Tabs.Single(tab => !ReferenceEquals(tab, original));
            var project = workspace.CurrentProject;
            using var caller = new CancellationTokenSource();
            Task pending = vm.ReadCalibrationLabelsAsync(caller.Token);
            Assert.AreEqual(1, reader.Calls);
            Assert.IsTrue(vm.IsBusy);
            vm.SelectedTab = other;
            bool cancelledBySwitch = reader.ObservedToken.IsCancellationRequested;
            if (!cancelledBySwitch) caller.Cancel(); // Unwind the pre-fix failure without waiting for OCR.
            Pump(pending);
            Assert.IsTrue(cancelledBySwitch, "Changing tabs must cancel the abandoned label request.");
            Assert.IsFalse(vm.IsBusy);
            Assert.AreSame(other, vm.SelectedTab);
            Assert.AreSame(project, workspace.CurrentProject);
            Assert.IsNull(original.Calibration);
            Assert.IsNull(other.Calibration);
            Assert.IsFalse(vm.HasCalibrationLabelSuggestion);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        });
    }

    [TestMethod]
    public void SelectingSameTabDoesNotCancelLabelRead()
    {
        WithImage(path =>
        {
            var reader = new CooperativeLabelReader();
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new AxisStub(MeasuredGeometry()),
                calibrationOcrSuggestionService: reader);
            Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestAxesAsync(CancellationToken.None));
            var tab = vm.SelectedTab!;
            Task pending = vm.ReadCalibrationLabelsAsync(CancellationToken.None);
            Assert.AreEqual(1, reader.Calls);
            vm.SelectedTab = tab;
            Assert.IsFalse(reader.ObservedToken.IsCancellationRequested);
            reader.Complete();
            Pump(pending);
            Assert.IsTrue(vm.HasCalibrationLabelSuggestion);
            Assert.IsFalse(vm.IsBusy);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExplicitCancellationStillCancelsLabelRead(bool useCommand)
    {
        WithImage(path =>
        {
            var reader = new CooperativeLabelReader();
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new AxisStub(MeasuredGeometry()),
                calibrationOcrSuggestionService: reader);
            Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestAxesAsync(CancellationToken.None));
            using var caller = new CancellationTokenSource();
            Task pending = vm.ReadCalibrationLabelsAsync(caller.Token);
            Assert.AreEqual(1, reader.Calls);
            if (useCommand) vm.CancelCommand.Execute(null); else caller.Cancel();
            Assert.IsTrue(reader.ObservedToken.IsCancellationRequested);
            Pump(pending);
            Assert.IsFalse(vm.IsBusy);
            Assert.IsFalse(vm.HasCalibrationLabelSuggestion);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        });
    }

    [TestMethod]
    public void SwitchingTabsDoesNotCancelUnrelatedAxisWork()
    {
        WithImage(path =>
        {
            string otherPath = Path.Combine(Path.GetDirectoryName(path)!, "other-owned.png");
            File.Copy(path, otherPath);
            var axis = new CooperativePendingAxis();
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: axis,
                calibrationOcrSuggestionService: new CooperativeLabelReader());
            Pump(workspace.ImportImagesAsync([path, otherPath], CancellationToken.None));
            using var vm = new MainWindowViewModel(workspace);
            var other = vm.Tabs.Single(tab => !ReferenceEquals(tab, vm.SelectedTab));
            Task pending = vm.SuggestAxesAsync(CancellationToken.None);
            Assert.AreEqual(1, axis.Calls);
            vm.SelectedTab = other;
            Assert.IsFalse(axis.ObservedToken.IsCancellationRequested);
            axis.Complete();
            Pump(pending);
            Assert.IsFalse(vm.IsBusy);
            Assert.AreSame(other, vm.SelectedTab);
        });
    }

    private sealed class CooperativeLabelReader : ICalibrationOcrSuggestionService
    {
        private readonly TaskCompletionSource<CalibrationOcrResponse> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ObservedToken { get; private set; }
        private CalibrationOcrRequest? _request;
        public int Calls { get; private set; }
        public Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request, ReadOnlyMemory<byte> image, CancellationToken token)
        {
            Calls++; ObservedToken = token; _request = request;
            token.Register(() => _completion.TrySetCanceled(token));
            return _completion.Task;
        }
        public void Complete() => _completion.SetResult(Response(_request!, ValidProposals()));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CooperativePendingAxis : IAxisProposalService
    {
        private readonly TaskCompletionSource<GraphReader.Axis.RustAxisProposalResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ObservedToken { get; private set; }
        public int Calls { get; private set; }
        public Task<GraphReader.Axis.RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token)
        { Calls++; ObservedToken = token; return _completion.Task; }
        public void Complete() => _completion.SetResult(new(MeasuredGeometry(), null));
    }
}
