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
public sealed class CalibrationTickViewModelTests
{
    [TestMethod]
    public void TickWorkflowFillsOnlyReviewableProposalAndDoesNotApplyCalibration()
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ValidProposals());
            using var vm = new MainWindowViewModel(workspace);
            WorkspaceTabViewModel tab = vm.SelectedTab!;

            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));

            Assert.IsTrue(vm.HasAxisSuggestion);
            Assert.IsTrue(vm.HasCalibrationTickProposal);
            Assert.IsFalse(vm.HasEstimatedCalibrationPositions);
            Assert.AreEqual("10", vm.SuggestedYMaximum);
            Assert.AreEqual("1", vm.SuggestedFirstSession);
            Assert.AreEqual("24", vm.SuggestedFinalSession);
            Assert.AreEqual(6, vm.CalibrationPairedTickRows.Count);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsNull(tab.Calibration, "Tick OCR must never apply a calibration.");
            vm.AxisSuggestionReviewed = true;
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null), "The existing explicit review step enables Apply.");
            Assert.IsNull(tab.Calibration, "Reviewing the proposal alone must not apply it.");
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EstimatedDraftLabelsPositionsAndRequiresExplicitReviewAndApply(bool estimateBothAxes)
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ValidProposals(), EstimatedGeometry(estimateBothAxes));
            Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
            var localization = new LocalizationService(new ResourceDictionary());
            using var vm = new MainWindowViewModel(workspace, localization);
            WorkspaceTabViewModel tab = vm.SelectedTab!;

            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));

            Assert.IsTrue(vm.HasEstimatedCalibrationPositions);
            Assert.IsTrue(vm.HasCalibrationTickProposal);
            Assert.AreEqual("10", vm.SuggestedYMaximum);
            Assert.AreEqual("1", vm.SuggestedFirstSession);
            Assert.AreEqual("24", vm.SuggestedFinalSession);
            CollectionAssert.Contains(vm.CalibrationPairedTickRows, string.Format(CultureInfo.CurrentCulture,
                localization.GetString(LocalizationKeys.ManualCalibrationTicksEstimatedRow),
                localization.GetString(LocalizationKeys.ManualCalibrationTicksAxisX), "1", 20));
            CollectionAssert.Contains(vm.CalibrationPairedTickRows, string.Format(CultureInfo.CurrentCulture,
                localization.GetString(estimateBothAxes ? LocalizationKeys.ManualCalibrationTicksEstimatedRow
                    : LocalizationKeys.ManualCalibrationTicksPairedRow),
                localization.GetString(LocalizationKeys.ManualCalibrationTicksAxisY), "10", 20));
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsNull(tab.Calibration);
            vm.AxisSuggestionReviewed = true;
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsNull(tab.Calibration, "Confirming review alone must not change calibration.");

            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));

            Assert.AreEqual(10d, tab.Calibration!.YMaximum);
            Assert.AreEqual(24d, tab.Calibration.XMaximum);
            Assert.AreEqual(new PixelPoint(140, 100), tab.Calibration.SessionMaximumY0);
            Assert.IsFalse(vm.HasEstimatedCalibrationPositions);
            Assert.IsFalse(vm.HasAxisSuggestion);
        });
    }

    [TestMethod]
    public void EstimatedWarningSurvivesValueAndAnchorCorrectionsUntilDismissed()
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ValidProposals(), EstimatedGeometry());
            using var vm = new MainWindowViewModel(workspace);
            WorkspaceTabViewModel tab = vm.SelectedTab!;
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            vm.AxisSuggestionReviewed = true;

            vm.SuggestedYMaximum = "12";

            Assert.IsTrue(vm.HasEstimatedCalibrationPositions);
            Assert.IsFalse(vm.HasCalibrationTickProposal, "Editing clears the old fit proof, not the position warning.");
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            vm.StartCalibrationCommand.Execute(null);
            Pump(vm.HandleCanvasPointAsync(new Point(21, 100)));
            Assert.IsTrue(vm.HasEstimatedCalibrationPositions);
            Pump(vm.HandleCanvasPointAsync(new Point(21, 20)));
            Pump(vm.HandleCanvasPointAsync(new Point(140, 100)));
            Assert.IsTrue(vm.HasEstimatedCalibrationPositions);
            Assert.AreEqual(ManualEditorMode.Select, vm.EditorMode);
            Assert.IsNull(tab.Calibration);
            vm.SuggestedYMaximum = "10";
            vm.SuggestedFirstSession = "1";
            vm.SuggestedFinalSession = "24";
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsTrue(vm.HasEstimatedCalibrationPositions);

            vm.DismissAxisSuggestionCommand.Execute(null);

            Assert.IsFalse(vm.HasEstimatedCalibrationPositions);
            Assert.IsNull(tab.Calibration);
            Pump(vm.SuggestAxesAsync(CancellationToken.None));
            Assert.IsTrue(vm.HasAxisSuggestion);
            Assert.IsFalse(vm.HasEstimatedCalibrationPositions, "New geometry alone is not an estimated label draft.");
        });
    }

    [TestMethod]
    public void EstimatedDraftCannotApplyAfterBindingOrTabChanges()
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ValidProposals(), EstimatedGeometry());
            using var vm = new MainWindowViewModel(workspace);
            WorkspaceTabViewModel tab = vm.SelectedTab!;
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            vm.AxisSuggestionReviewed = true;
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));

            workspace.Calibrate(tab.TabId, new(new(10, 100), new(10, 20), new(150, 100), 20, 30, 1));
            var changed = tab.Calibration;
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.AreSame(changed, tab.Calibration);

            vm.SelectedTab = null;
            vm.SelectedTab = tab;

            Assert.IsFalse(vm.HasEstimatedCalibrationPositions);
            Assert.IsFalse(vm.HasAxisSuggestion);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.AreSame(changed, tab.Calibration);
        });
    }

    [TestMethod]
    public void MissingObservedZeroRetainsValidXAndLeavesYUnconfirmed()
    {
        WithImage(path =>
        {
            var proposals = ValidProposals();
            proposals[3] = Proposal("y-max", "12", 12, "YTick", "Y", 20, 2);
            proposals[4] = Proposal("y-mid", "7", 7, "YTick", "Y", 60, 2);
            proposals[5] = Proposal("y-low", "2", 2, "YTick", "Y", 100, 2);
            using var workspace = CreateWorkspace(path, proposals);
            Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
            var localization = new LocalizationService(new ResourceDictionary());
            using var vm = new MainWindowViewModel(workspace, localization);
            WorkspaceTabViewModel tab = vm.SelectedTab!;

            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));

            Assert.IsTrue(vm.HasCalibrationTickProposal);
            Assert.IsTrue(vm.HasCalibrationTickProposalStatus);
            Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
            Assert.AreEqual("1", vm.SuggestedFirstSession);
            Assert.AreEqual("24", vm.SuggestedFinalSession);
            Assert.AreEqual(localization.GetString(LocalizationKeys.ManualCalibrationTicksPartialX), vm.CalibrationTickProposalStatusText);
            Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, localization.GetString(LocalizationKeys.ManualAxisPositions),
                20d, 110d, 20d, 5d, 140d, 110d), vm.AxisSuggestionSummary,
                "Only validated X components replace the original geometry.");
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsNull(tab.Calibration);
            vm.AxisSuggestionReviewed = true;
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null), "Review cannot supply the missing Y range.");
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.IsNull(tab.Calibration);
            vm.StartCalibrationCommand.Execute(null);
            Assert.AreEqual(ManualEditorMode.Calibration, vm.EditorMode);
            Assert.AreEqual("1", vm.SuggestedFirstSession, "Anchor correction retains the validated partial range.");
            Assert.AreEqual("24", vm.SuggestedFinalSession);
            Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
            Assert.IsFalse(vm.HasCalibrationTickProposal);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ValidYRetainsItsPositionsWhileInvalidXNeedsManualValues(bool estimatedY)
    {
        WithImage(path =>
        {
            var proposals = ValidProposals();
            proposals[1] = Proposal("x-mid", "0", 0, "XTick", "X", 80, 112);
            var geometry = new RustAxisGeometry(8, 110, 155, 5, .9)
                { XTickPositions = [20, 80, 140], YTickPositions = estimatedY ? [] : [20, 60, 100] };
            using var workspace = CreateWorkspace(path, proposals, geometry);
            Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
            var localization = new LocalizationService(new ResourceDictionary());
            using var vm = new MainWindowViewModel(workspace, localization);

            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));

            Assert.AreEqual("10", vm.SuggestedYMaximum);
            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
            Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
            Assert.AreEqual(localization.GetString(LocalizationKeys.ManualCalibrationTicksPartialY), vm.CalibrationTickProposalStatusText);
            Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, localization.GetString(LocalizationKeys.ManualAxisPositions),
                8d, 100d, 8d, 20d, 155d, 100d), vm.AxisSuggestionSummary);
            Assert.AreEqual(estimatedY, vm.HasEstimatedCalibrationPositions);
            vm.AxisSuggestionReviewed = true;
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.IsNull(vm.SelectedTab!.Calibration);
            vm.StartCalibrationCommand.Execute(null);
            Assert.AreEqual("10", vm.SuggestedYMaximum);
            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
            Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
            Assert.IsFalse(vm.HasCalibrationTickProposal);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.AreEqual(estimatedY, vm.HasEstimatedCalibrationPositions);

            vm.SuggestedFirstSession = "1";
            vm.SuggestedFinalSession = "24";

            Assert.IsFalse(vm.AxisSuggestionReviewed, "Manually completing values requires a fresh review.");
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.AreEqual(estimatedY, vm.HasEstimatedCalibrationPositions);
            Assert.IsNull(vm.SelectedTab.Calibration);
        });
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void StaleBindingDuringTickReadCannotPublishOrApplyProposal(bool estimated, bool partial)
    {
        WithImage(path =>
        {
            CalibrationOcrProposal[] proposals = partial
                ? ValidProposals().Where(row => row.RegionId != "y-zero").ToArray() : ValidProposals();
            var reader = new OcrStub(proposals) { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var workspace = CreateWorkspace(path, reader, estimated ? EstimatedGeometry() : null);
            using var vm = new MainWindowViewModel(workspace);
            WorkspaceTabViewModel tab = vm.SelectedTab!;
            workspace.Calibrate(tab.TabId, new(new(10, 100), new(10, 20), new(150, 100), 10, 24, 1));

            Task pending = vm.SuggestCalibrationFromTicksAsync(CancellationToken.None);
            Assert.IsNotNull(reader.LastRequest);
            workspace.Calibrate(tab.TabId, new(new(11, 100), new(11, 20), new(150, 100), 10, 24, 1));
            var changedCalibration = tab.Calibration;
            reader.Pending!.SetResult(Response(reader.LastRequest!, proposals));
            Pump(pending);

            Assert.IsFalse(vm.HasCalibrationTickProposal);
            Assert.IsFalse(vm.HasEstimatedCalibrationPositions);
            Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
            Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.AreSame(changedCalibration, tab.Calibration);
        });
    }

    [TestMethod]
    public void CancelledTickReadCannotPublishOrApplyProposal()
    {
        WithImage(path =>
        {
            var reader = new OcrStub(ValidProposals()) { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var workspace = CreateWorkspace(path, reader);
            using var vm = new MainWindowViewModel(workspace);
            using var cancellation = new CancellationTokenSource();

            Task pending = vm.SuggestCalibrationFromTicksAsync(cancellation.Token);
            Assert.IsNotNull(reader.LastRequest);
            cancellation.Cancel();
            reader.Pending!.SetResult(Response(reader.LastRequest!, ValidProposals()));
            try { Pump(pending); }
            catch (OperationCanceledException) { }

            Assert.IsFalse(vm.HasCalibrationTickProposal);
            Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
            Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsNull(vm.SelectedTab!.Calibration);
        });
    }

    [TestMethod]
    public void ManualValueEditClearsTickProofAndPairedRows()
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ValidProposals());
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            Assert.IsTrue(vm.HasCalibrationTickProposal);
            Assert.IsTrue(vm.CalibrationPairedTickRows.Count > 0);

            vm.SuggestedYMaximum = "12";

            Assert.IsFalse(vm.HasCalibrationTickProposal);
            Assert.AreEqual(0, vm.CalibrationPairedTickRows.Count);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        });
    }

    [TestMethod]
    public void ExcludingExtraLabelRefitsCachedEvidenceWithoutRereadingOrRemovingRawRows()
    {
        WithImage(path =>
        {
            var reader = new OcrStub(ProposalsWithExtraRow());
            var axis = new AxisStub(MeasuredGeometry());
            using var workspace = CreateWorkspace(path, reader, axis: axis);
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            Assert.IsTrue(vm.CalibrationLabelRows.All(row => row.IsIncluded));
            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession, "Conflicting evidence must not be salvaged automatically.");
            CalibrationOcrResponse raw = reader.LastResponse!;
            CalibrationLabelRowViewModel extra = vm.CalibrationLabelRows.Single(row => row.Proposal.RegionId == "noise");

            extra.IsIncluded = false;
            vm.RefitSelectedCalibrationLabelsCommand.Execute(null);

            Assert.AreEqual("10", vm.SuggestedYMaximum);
            Assert.AreEqual("1", vm.SuggestedFirstSession);
            Assert.AreEqual("24", vm.SuggestedFinalSession);
            Assert.AreEqual(6, vm.CalibrationPairedTickRows.Count);
            Assert.AreEqual(7, vm.CalibrationLabelRows.Count);
            Assert.IsFalse(extra.IsIncluded);
            Assert.AreEqual(7, raw.Proposals.Count);
            Assert.AreEqual(4, raw.XSequence!.CandidateCount);
            Assert.AreSame(raw, reader.LastResponse);
            Assert.AreEqual(1, reader.CallCount);
            Assert.AreEqual(1, axis.CallCount);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            vm.AxisSuggestionReviewed = true;
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsNull(vm.SelectedTab!.Calibration);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InvalidOrInsufficientIncludedRowsCannotCompleteCalibration(bool retainInvalidMiddle)
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ProposalsWithExtraRow());
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            vm.CalibrationLabelRows.Single(row => row.Proposal.RegionId == "x-mid").IsIncluded = false;
            if (!retainInvalidMiddle)
                vm.CalibrationLabelRows.Single(row => row.Proposal.RegionId == "noise").IsIncluded = false;

            vm.RefitSelectedCalibrationLabelsCommand.Execute(null);

            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
            Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
            Assert.AreEqual("10", vm.SuggestedYMaximum, "Independent valid Y evidence remains usable.");
            vm.AxisSuggestionReviewed = true;
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.IsNull(vm.SelectedTab!.Calibration);
        });
    }

    [TestMethod]
    public void ChangingInclusionClearsReviewedValuesAndFitPositionsImmediately()
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ValidProposals(), EstimatedGeometry());
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestAxesAsync(CancellationToken.None));
            string originalAnchors = vm.AxisSuggestionSummary;
            Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));
            vm.RefitSelectedCalibrationLabelsCommand.Execute(null);
            vm.AxisSuggestionReviewed = true;
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));

            vm.CalibrationLabelRows[0].IsIncluded = false;

            Assert.IsFalse(vm.HasCalibrationTickProposal);
            Assert.AreEqual(0, vm.CalibrationPairedTickRows.Count);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
            Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
            Assert.AreEqual(originalAnchors, vm.AxisSuggestionSummary);
            Assert.IsTrue(vm.HasEstimatedCalibrationPositions, "Position guidance persists until Apply or Dismiss.");
            vm.AxisSuggestionReviewed = true;
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.IsNull(vm.SelectedTab!.Calibration);
        });
    }

    [TestMethod]
    public void NewReadResetsInclusionsWithoutKeepingPreviouslyRefittedValues()
    {
        WithImage(path =>
        {
            var reader = new OcrStub(ProposalsWithExtraRow());
            using var workspace = CreateWorkspace(path, reader);
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            CalibrationLabelRowViewModel old = vm.CalibrationLabelRows.Single(row => row.Proposal.RegionId == "noise");
            old.IsIncluded = false;
            vm.RefitSelectedCalibrationLabelsCommand.Execute(null);
            vm.AxisSuggestionReviewed = true;

            Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));

            Assert.AreEqual(2, reader.CallCount);
            Assert.IsTrue(vm.CalibrationLabelRows.All(row => row.IsIncluded));
            Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
            Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            old.IsIncluded = true;
            Assert.IsFalse(old.IsIncluded, "Detached rows cannot change current evidence.");
            vm.RefitSelectedCalibrationLabelsCommand.Execute(null);
            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
        });
    }

    [TestMethod]
    public void StaleBindingTabChangeAndDismissDisableCachedRefit()
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ValidProposals());
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            WorkspaceTabViewModel tab = vm.SelectedTab!;
            CalibrationLabelRowViewModel old = vm.CalibrationLabelRows[0];
            workspace.Calibrate(tab.TabId, new(new(10, 100), new(10, 20), new(150, 100), 20, 30, 1));
            var changed = tab.Calibration;

            Assert.IsFalse(vm.RefitSelectedCalibrationLabelsCommand.CanExecute(null));
            int rejectedSelectionNotifications = 0;
            old.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(old.IsIncluded)) rejectedSelectionNotifications++;
            };
            old.IsIncluded = false;
            Assert.IsTrue(old.IsIncluded);
            Assert.AreEqual(1, rejectedSelectionNotifications, "Two-way controls must redraw rejected selection changes.");
            vm.RefitSelectedCalibrationLabels();
            vm.AxisSuggestionReviewed = true;
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.AreSame(changed, tab.Calibration);
            vm.SelectedTab = null;
            vm.SelectedTab = tab;
            Assert.AreEqual(0, vm.CalibrationLabelRows.Count);
            Assert.IsFalse(vm.RefitSelectedCalibrationLabelsCommand.CanExecute(null));
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            Assert.IsTrue(vm.CalibrationLabelRows.All(row => row.IsIncluded));
            vm.DismissAxisSuggestionCommand.Execute(null);
            Assert.AreEqual(0, vm.CalibrationLabelRows.Count);
            Assert.IsFalse(vm.RefitSelectedCalibrationLabelsCommand.CanExecute(null));
        });
    }

    [TestMethod]
    public void PendingReadRejectsRefitAndOldRowChanges()
    {
        WithImage(path =>
        {
            var reader = new OcrStub(ValidProposals());
            using var workspace = CreateWorkspace(path, reader);
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            CalibrationLabelRowViewModel old = vm.CalibrationLabelRows[0];
            reader.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task pending = vm.ReadCalibrationLabelsAsync(CancellationToken.None);

            Assert.IsTrue(vm.IsBusy);
            Assert.IsFalse(vm.RefitSelectedCalibrationLabelsCommand.CanExecute(null));
            old.IsIncluded = false;
            Assert.IsTrue(old.IsIncluded);
            vm.RefitSelectedCalibrationLabels();
            Assert.IsFalse(vm.HasCalibrationTickProposal);
            reader.Pending.SetResult(Response(reader.LastRequest!, ValidProposals()));
            Pump(pending);
            Assert.IsTrue(vm.CalibrationLabelRows.All(row => row.IsIncluded));
            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
        });
    }

    private static CalibrationOcrProposal[] ProposalsWithExtraRow() =>
        [.. ValidProposals(), Proposal("noise", "99", 99, "XTick", "X", 80, 112)];

    private static RustAxisGeometry MeasuredGeometry() => new(8, 110, 155, 5, .9)
        { XTickPositions = [20, 80, 140], YTickPositions = [20, 60, 100] };

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
