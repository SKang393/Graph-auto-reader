// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

public sealed partial class RectifiedCalibrationTests
{
    [TestMethod]
    [DataRow("X")]
    [DataRow("Y")]
    public void ManualAffinePartialEvidenceKeepsOnlyObservedSideAndRequiresMissingValuesAndReview(string side) =>
        WithFixture(2, false, (workspace, axis, ticks, reader, _) =>
        {
            ticks.PartialSide = side; reader.OnlyAxis = side;
            using var vm = new MainWindowViewModel(workspace);
            var tab = vm.SelectedTab!; var original = workspace.CurrentProject;
            CollectManualFrame(vm, axis.Frame);
            Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));
            var labels = CurrentLabels(vm);
            Assert.AreEqual(1, ticks.ManualPartialCalls);
            Assert.AreEqual(0, axis.Calls);
            Assert.IsNotNull(reader.LastRequest);
            if (side == "X")
            {
                CollectionAssert.AreEqual(ticks.X, reader.LastRequest.MeasuredXTicks!);
                Assert.IsNull(reader.LastRequest.MeasuredYTicks);
            }
            else
            {
                Assert.IsNull(reader.LastRequest.MeasuredXTicks);
                CollectionAssert.AreEqual(ticks.Y, reader.LastRequest.MeasuredYTicks!);
            }
            Assert.IsTrue(workspace.IsCalibrationLabelSuggestionCurrent(labels));
            vm.RefitSelectedCalibrationLabels();
            var fit = CalibrationTickProposal.CreateReviewDraft(labels.FitGeometry, labels.Response);
            Assert.IsFalse(fit.IsComplete);
            Assert.AreEqual(side == "X", fit.XReviewDraft is not null);
            Assert.AreEqual(side == "Y", fit.YReviewDraft is not null);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            if (side == "X") vm.SuggestedYMaximum = "10";
            else { vm.SuggestedFirstSession = "1"; vm.SuggestedFinalSession = "3"; }
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            vm.AxisSuggestionReviewed = true;
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.AreSame(original, workspace.CurrentProject);
            Assert.IsNull(tab.Calibration);
        });

    [TestMethod]
    [DataRow("hash")]
    [DataRow("box")]
    [DataRow("none")]
    public void ManualAffineInvalidPartialReceiptNeverBecomesTickEvidence(string fault) =>
        WithFixture(2, false, (workspace, axis, ticks, reader, _) =>
        {
            ticks.PartialSide = fault == "none" ? "none" : "X"; ticks.EvidenceFault = fault;
            var tab = workspace.CreateWorkspace().Single(); var frame = axis.Frame;
            var draft = workspace.CreateManualAxisDraft(tab.TabId,
                [new(frame.Origin.X, frame.Origin.Y), new(frame.YEnd.X, frame.YEnd.Y), new(frame.XEnd.X, frame.XEnd.Y)])!;
            var result = Pump(workspace.ReadCalibrationLabelsAsync(draft, CancellationToken.None))!;
            Assert.AreEqual(1, ticks.ManualPartialCalls);
            Assert.AreEqual(fault == "none" ? "AXIS_BOX_NOT_OBSERVED" : "INVALID_TICK_EVIDENCE", result.TickMeasurementErrorCode);
            Assert.IsNull(reader.LastRequest!.MeasuredXTicks);
            Assert.IsNull(reader.LastRequest.MeasuredYTicks);
            Assert.IsEmpty(result.FitGeometry.XTickPositions);
            Assert.IsEmpty(result.FitGeometry.YTickPositions);
        });

    [TestMethod]
    public void ManualAffineObservedSpansWithNoTicksRetainSuccessfulEmptyReceipt() =>
        WithFixture(2, false, (workspace, axis, ticks, reader, _) =>
        {
            ticks.PartialSide = "none";
            var tab = workspace.CreateWorkspace().Single(); var frame = axis.Frame;
            var draft = workspace.CreateManualAxisDraft(tab.TabId,
                [new(frame.Origin.X, frame.Origin.Y), new(frame.YEnd.X, frame.YEnd.Y), new(frame.XEnd.X, frame.XEnd.Y)])!;
            var result = Pump(workspace.ReadCalibrationLabelsAsync(draft, CancellationToken.None))!;
            Assert.AreEqual(1, ticks.ManualPartialCalls);
            Assert.IsNull(result.TickMeasurementErrorCode);
            Assert.IsNull(reader.LastRequest!.MeasuredXTicks);
            Assert.IsNull(reader.LastRequest.MeasuredYTicks);
            Assert.IsTrue(workspace.IsCalibrationLabelSuggestionCurrent(result));
        });

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("stale")]
    public void PendingManualPartialReceiptCannotStartOcrAfterCancellationOrBindingChange(string action) =>
        WithFixture(2, false, (workspace, axis, ticks, reader, _) =>
        {
            ticks.PartialSide = "X"; ticks.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var tab = workspace.CreateWorkspace().Single(); var frame = axis.Frame;
            var draft = workspace.CreateManualAxisDraft(tab.TabId,
                [new(frame.Origin.X, frame.Origin.Y), new(frame.YEnd.X, frame.YEnd.Y), new(frame.XEnd.X, frame.XEnd.Y)])!;
            using var cancel = new CancellationTokenSource();
            var pending = workspace.ReadCalibrationLabelsAsync(draft, cancel.Token);
            WaitUntil(() => ticks.ManualPartialCalls == 1);
            if (action == "cancel") cancel.Cancel();
            else workspace.Calibrate(tab.TabId, new(new(70, 280), new(70, 60), new(330, 280), 100, 20));
            ticks.Pending.SetResult(ticks.Result!);
            if (action == "cancel") Assert.Throws<OperationCanceledException>(() => Pump(pending));
            else Assert.IsNull(Pump(pending));
            Assert.AreEqual(0, reader.Calls);
        });

    [TestMethod]
    public void AutomaticAffineStillUsesStrictTickMeasurement() =>
        WithFixture(2, false, (workspace, _, ticks, _, _) =>
        {
            var tab = workspace.CreateWorkspace().Single();
            var suggestion = Pump(workspace.SuggestAxesAsync(tab.TabId, CancellationToken.None))!;
            Assert.IsNotNull(Pump(workspace.ReadCalibrationLabelsAsync(suggestion, CancellationToken.None)));
            Assert.AreEqual(1, ticks.Calls);
            Assert.AreEqual(0, ticks.ManualPartialCalls);
        });

    [TestMethod]
    public void ExistingTickAdapterDefaultsToStrictMeasurement()
    {
        var adapter = new StrictOnlyTickAdapter();
        IAxisTickMeasurementService service = adapter;
        AxisTickMeasurementResult result = service.MeasureManualPartialAsync([1], new RustAxisGeometry(1, 9, 9, 1, 0), CancellationToken.None).GetAwaiter().GetResult();
        Assert.AreEqual(1, adapter.Calls);
        Assert.AreEqual("STRICT_ONLY", result.ErrorCode);
    }

    private sealed class StrictOnlyTickAdapter : IAxisTickMeasurementService
    {
        internal int Calls { get; private set; }
        public Task<AxisTickMeasurementResult> MeasureAsync(byte[] image, RustAxisGeometry geometry, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new AxisTickMeasurementResult(string.Empty, 0, 0, null, "STRICT_ONLY"));
        }
    }
}
