// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Reflection;
using System.Windows;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

public sealed partial class RectifiedCalibrationTests
{
    [TestMethod]
    public void AutomaticRectangleCorrectedToTiltedAnchorsPreservesPartialValuesAndReadsRectifiedEvidence() =>
        WithFixture(2, false, (workspace, axis, ticks, reader, _) =>
        {
            axis.Rectangle = new(90, 290, 350, 70, .7);
            reader.InvalidMiddleY = true;
            using var vm = new MainWindowViewModel(workspace);
            var tab = vm.SelectedTab!;
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            var oldLabels = CurrentLabels(vm);
            Assert.IsFalse(oldLabels.IsRectified);
            Assert.AreEqual("1", vm.SuggestedFirstSession);
            Assert.AreEqual("3", vm.SuggestedFinalSession);
            Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
            var project = workspace.CurrentProject;

            CorrectFrame(vm, axis.Frame);

            Assert.IsTrue(vm.HasAffineAxisSuggestion);
            Assert.IsTrue(vm.IsManualAxisDraft);
            Assert.IsFalse(vm.HasCalibrationLabelSuggestion, "The previous raw rectangle receipt cannot survive geometry correction.");
            Assert.IsFalse(vm.HasCalibrationTickProposal);
            Assert.AreEqual("1", vm.SuggestedFirstSession);
            Assert.AreEqual("3", vm.SuggestedFinalSession);
            Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.AreSame(project, workspace.CurrentProject);
            Assert.IsNull(tab.Calibration);
            reader.InvalidMiddleY = false;
            Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));
            var labels = CurrentLabels(vm);
            Assert.IsTrue(labels.IsRectified);
            Assert.AreNotSame(oldLabels, labels);
            Assert.AreEqual(axis.Frame with { Confidence = 0 }, labels.AxisSuggestion.AffineGeometry);
            Assert.AreEqual("manual", labels.AxisSuggestion.Provider);
            CollectionAssert.Contains(labels.AxisSuggestion.ReviewReasons.ToArray(), ManualCalibrationMapper.AffineReason);
            vm.RefitSelectedCalibrationLabels();
            Assert.HasCount(6, vm.CalibrationPairedTickRows);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsNull(tab.Calibration);
            vm.AxisSuggestionReviewed = true;
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.IsNotNull(tab.Calibration);
            Assert.IsFalse(ManualCalibrationMapper.IsAxisAligned(tab.Calibration));
            Assert.AreEqual(1, axis.Calls);
            Assert.AreEqual(2, ticks.Calls);
            Assert.AreEqual(2, reader.Calls);
        });

    [TestMethod]
    public void ManualCorrectionNotifiesGeometryKindInBothDirections() =>
        WithFixture(2, false, (workspace, axis, _, _, _) =>
        {
            using var vm = new MainWindowViewModel(workspace);
            CollectManualFrame(vm, axis.Frame);
            var changed = new List<string?>();
            vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
            CorrectFrame(vm, new(new(90, 290), new(350, 290), new(90, 70), 0));
            Assert.IsFalse(vm.HasAffineAxisSuggestion);
            Assert.IsTrue(vm.IsManualAxisDraft);
            CollectionAssert.Contains(changed, nameof(vm.HasAffineAxisSuggestion));
            CollectionAssert.Contains(changed, nameof(vm.IsManualAxisDraft));
            changed.Clear();
            CorrectFrame(vm, axis.Frame);
            Assert.IsTrue(vm.HasAffineAxisSuggestion);
            CollectionAssert.Contains(changed, nameof(vm.HasAffineAxisSuggestion));
            CollectionAssert.Contains(changed, nameof(vm.IsManualAxisDraft));
            Assert.IsNull(vm.SelectedTab!.Calibration);
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AutomaticRectangleCorrectionRejectsInvalidOrStaleTiltedFrame(bool stale) =>
        WithFixture(2, false, (workspace, axis, ticks, reader, _) =>
        {
            axis.Rectangle = new(90, 290, 350, 70, .7);
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestAxesAsync(CancellationToken.None));
            vm.StartCalibrationCommand.Execute(null);
            if (stale)
            {
                var originals = (Dictionary<string, GraphReader.Imaging.ImmutableImageBytes>)typeof(ManualPreviewWorkspaceService)
                    .GetField("_originalPanelBytesByTab", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
                string id = vm.SelectedTab!.TabId;
                originals[id] = new(originals[id].Copy());
            }
            var frame = stale ? axis.Frame : new(new(90, 290), new(350, 286), new(94, 300), 0);
            ClickFrame(vm, frame);
            Assert.IsFalse(vm.HasAxisSuggestion);
            Assert.IsFalse(vm.ReadCalibrationLabelsCommand.CanExecute(null));
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsNull(vm.SelectedTab!.Calibration);
            Assert.AreEqual(0, ticks.Calls);
            Assert.AreEqual(0, reader.Calls);
        });

    [TestMethod]
    [DataRow(2d, false)]
    [DataRow(-2d, false)]
    [DataRow(0d, true)]
    public void ManualTiltedFramePreservesExactOriginalAnchorsWithoutCreatingScientificState(double tilt, bool shear) =>
        WithFixture(tilt, shear, (workspace, axis, ticks, reader, _) =>
        {
            var tab = workspace.CreateWorkspace().Single();
            var project = workspace.CurrentProject;
            bool dirty = tab.IsDirty;
            var frame = axis.Frame;
            var draft = workspace.CreateManualAxisDraft(tab.TabId,
                [new(frame.Origin.X, frame.Origin.Y), new(frame.YEnd.X, frame.YEnd.Y), new(frame.XEnd.X, frame.XEnd.Y)]);
            Assert.IsNotNull(draft);
            Assert.IsNull(draft.Geometry);
            Assert.AreEqual(frame with { Confidence = 0 }, draft.AffineGeometry);
            Assert.AreEqual("manual", draft.Provider);
            Assert.IsTrue(draft.IsUserDefined);
            CollectionAssert.Contains(draft.ReviewReasons.ToArray(), ManualCalibrationMapper.AffineReason);
            Assert.IsTrue(workspace.IsAxisSuggestionCurrent(draft));
            Assert.AreSame(project, workspace.CurrentProject);
            Assert.AreEqual(dirty, tab.IsDirty);
            Assert.IsNull(tab.Calibration);
            Assert.IsEmpty(tab.Points);
            Assert.AreEqual(0, axis.Calls);
            Assert.AreEqual(0, ticks.Calls);
            Assert.AreEqual(0, reader.Calls);
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ManualAffineDraftReadsDerivedLabelsThenRequiresReviewBeforePersistingMappedAnchors(bool printed) =>
        WithFixture(2, false, (workspace, axis, ticks, reader, path) =>
        {
            if (printed) reader.FirstX = 0;
            using var vm = new MainWindowViewModel(workspace);
            vm.ManualUsesPrintedXCoordinates = printed;
            var tab = vm.SelectedTab!;
            var project = workspace.CurrentProject;
            CollectManualFrame(vm, axis.Frame);
            Assert.IsTrue(vm.IsManualAxisDraft);
            Assert.IsTrue(vm.HasAffineAxisSuggestion);
            Assert.IsFalse(vm.IsCollectingManualAxisDraft);
            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
            Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
            Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsTrue(vm.ReadCalibrationLabelsCommand.CanExecute(null));
            Assert.AreSame(project, workspace.CurrentProject);
            Assert.IsNull(tab.Calibration);
            var frame = axis.Frame;
            AssertAnchors(new(new(frame.Origin.X, frame.Origin.Y), new(frame.YEnd.X, frame.YEnd.Y),
                new(frame.XEnd.X, frame.XEnd.Y)), Preview(tab));
            Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));
            var labels = CurrentLabels(vm);
            Assert.IsTrue(labels.IsRectified);
            Assert.AreEqual("manual", labels.AxisSuggestion.Provider);
            Assert.IsTrue(workspace.IsCalibrationLabelSuggestionCurrent(labels));
            Assert.AreSame(reader.Response, labels.Response);
            Assert.IsTrue(labels.TickMeasurementAttempted);
            Assert.IsNull(labels.TickMeasurementErrorCode);
            CollectionAssert.AreEqual(ticks.X, labels.Response.Request!.MeasuredXTicks!);
            CollectionAssert.AreEqual(ticks.Y, labels.Response.Request.MeasuredYTicks!);
            vm.RefitSelectedCalibrationLabels();
            var fit = CalibrationTickProposal.CreateReviewDraft(labels.FitGeometry, labels.Response);
            Assert.HasCount(6, fit.PairedTicks);
            Assert.IsFalse(fit.UsesEstimatedPositions);
            CalibrationTickAnchors expected;
            if (printed)
            {
                Assert.IsNotNull(fit.PrintedXReviewDraft);
                Assert.IsTrue(vm.UsePrintedXCoordinatesCommand.CanExecute(null));
                vm.UsePrintedXCoordinatesCommand.Execute(null);
                expected = labels.OriginalAnchors(fit.PrintedXReviewDraft.Anchors);
                Assert.AreEqual("0", vm.SuggestedFirstSession);
            }
            else
            {
                Assert.IsTrue(fit.IsComplete);
                expected = labels.OriginalAnchors(fit.Anchors!);
                Assert.AreEqual("1", vm.SuggestedFirstSession);
            }
            AssertAnchors(expected, Preview(tab));
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsNull(tab.Calibration);
            vm.AxisSuggestionReviewed = true;
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.IsNotNull(tab.Calibration);
            Assert.IsFalse(ManualCalibrationMapper.IsAxisAligned(tab.Calibration));
            Assert.AreEqual(printed, tab.Calibration.UsesPrintedXCoordinates);
            var accepted = workspace.CurrentProject.Panels.Single().Calibration!;
            Assert.IsTrue(accepted.UserConfirmed);
            Assert.IsNull(accepted.SessionLattice);
            CollectionAssert.Contains(accepted.Reasons.ToArray(), ManualCalibrationMapper.AffineReason);
            Assert.AreEqual(expected.FirstSessionY0.X, accepted.Anchors[0].Screen.X, 1e-9);
            Assert.AreEqual(expected.FirstSessionY0.Y, accepted.Anchors[0].Screen.Y, 1e-9);
            string saved = Path.Combine(Path.GetDirectoryName(path)!, "manual-affine.garproj");
            Assert.IsTrue(Pump(workspace.SaveProjectAsync(saved, CancellationToken.None)).IsSuccess);
            using var reopened = new ManualPreviewWorkspaceService();
            var restored = Pump(reopened.OpenProjectAsync(saved, CancellationToken.None)).Single();
            Assert.IsNotNull(restored.Calibration);
            Assert.AreEqual(expected.FirstSessionY0.X, restored.Calibration.Session1Y0.X, 1e-9);
            Assert.AreEqual(expected.FirstSessionY0.Y, restored.Calibration.Session1Y0.Y, 1e-9);
            Assert.AreEqual(printed, restored.Calibration.UsesPrintedXCoordinates);
            vm.UndoCommand.Execute(null);
            Assert.IsNull(tab.Calibration);
            Assert.AreEqual(0, axis.Calls, "Manual geometry must never rerun automatic selection.");
            Assert.AreEqual(1, ticks.Calls);
            Assert.AreEqual(1, reader.Calls);
        });

    [TestMethod]
    [DataRow("source")]
    [DataRow("tab")]
    [DataRow("frame")]
    public void ManualAffineReadCannotReuseEvidenceAfterItsSourceTabOrFrameChanges(string change) =>
        WithFixture(2, false, (workspace, axis, ticks, reader, _) =>
        {
            using var vm = new MainWindowViewModel(workspace);
            CollectManualFrame(vm, axis.Frame);
            Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));
            var labels = CurrentLabels(vm);
            Assert.IsTrue(workspace.IsCalibrationLabelSuggestionCurrent(labels));
            if (change == "source")
            {
                var originals = (Dictionary<string, GraphReader.Imaging.ImmutableImageBytes>)typeof(ManualPreviewWorkspaceService)
                    .GetField("_originalPanelBytesByTab", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
                originals[labels.AxisSuggestion.TabId] = new(originals[labels.AxisSuggestion.TabId].Copy());
                Assert.IsFalse(workspace.IsAxisSuggestionCurrent(labels.AxisSuggestion));
                Assert.IsFalse(workspace.IsCalibrationLabelSuggestionCurrent(labels));
            }
            else if (change == "frame")
            {
                var frame = labels.AxisSuggestion.AffineGeometry!;
                var changed = labels.AxisSuggestion with { AffineGeometry = frame with { Origin = frame.Origin with { X = frame.Origin.X + .25 } } };
                Assert.IsFalse(workspace.IsCalibrationLabelSuggestionCurrent(labels with { AxisSuggestion = changed }));
                var invalid = changed with { AffineGeometry = frame with { XEnd = new(999, 999) } };
                Assert.IsFalse(workspace.IsAxisSuggestionCurrent(invalid));
                Assert.IsNull(Pump(workspace.ReadCalibrationLabelsAsync(invalid, CancellationToken.None)));
            }
            else
            {
                vm.SelectedTab = null;
                Assert.IsFalse(vm.HasAxisSuggestion);
                Assert.IsFalse(vm.HasCalibrationLabelSuggestion);
                Assert.IsFalse(vm.RefitSelectedCalibrationLabelsCommand.CanExecute(null));
            }
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.AreEqual(0, axis.Calls);
            Assert.AreEqual(1, ticks.Calls);
            Assert.AreEqual(1, reader.Calls);
        });

    private static void CollectManualFrame(MainWindowViewModel vm, RustAffineAxisGeometry frame)
    {
        vm.BeginManualAxisDraftCommand.Execute(null);
        Pump(vm.HandleCanvasPointAsync(new Point(frame.Origin.X, frame.Origin.Y)));
        Pump(vm.HandleCanvasPointAsync(new Point(frame.YEnd.X, frame.YEnd.Y)));
        Pump(vm.HandleCanvasPointAsync(new Point(frame.XEnd.X, frame.XEnd.Y)));
    }

    private static void CorrectFrame(MainWindowViewModel vm, RustAffineAxisGeometry frame)
    {
        vm.StartCalibrationCommand.Execute(null);
        ClickFrame(vm, frame);
    }

    private static void ClickFrame(MainWindowViewModel vm, RustAffineAxisGeometry frame)
    {
        Pump(vm.HandleCanvasPointAsync(new Point(frame.Origin.X, frame.Origin.Y)));
        Pump(vm.HandleCanvasPointAsync(new Point(frame.YEnd.X, frame.YEnd.Y)));
        Pump(vm.HandleCanvasPointAsync(new Point(frame.XEnd.X, frame.XEnd.Y)));
    }
}
