// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.IO;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using GraphReader.Export;

namespace GraphReader.App.Tests;

public sealed partial class CalibrationTickViewModelTests
{
    [TestMethod]
    public void ManualPrintedXClicksPreserveSkippedSessionsAndSmallAxisTilt()
    {
        WithImage(path =>
        {
            using var workspace = new ManualPreviewWorkspaceService();
            var tab = Pump(workspace.ImportImagesAsync([path], CancellationToken.None)).Single();
            using var vm = new MainWindowViewModel(workspace);
            Assert.IsFalse(vm.ManualUsesPrintedXCoordinates);
            vm.ManualUsesPrintedXCoordinates = true;
            vm.ManualFirstVisibleSessionText = "1";
            vm.ManualXMaximum = 11;
            vm.ManualYMaximum = 40;
            vm.StartCalibrationCommand.Execute(null);
            Pump(vm.HandleCanvasPointAsync(new(20, 100)));
            Pump(vm.HandleCanvasPointAsync(new(21, 20)));
            Pump(vm.HandleCanvasPointAsync(new(120, 101)));
            string series = workspace.AddSeries(tab.TabId, new("Owned", "O", MarkerShape.Circle,
                MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
            // Original pixels for (1, 10), (4, 30), (10, 20) in this three-anchor basis.
            workspace.AddPoint(tab.TabId, series, 20.25, 80);
            workspace.AddPoint(tab.TabId, series, 50.75, 40.3);
            workspace.AddPoint(tab.TabId, series, 110.5, 60.9);
            string project = Path.ChangeExtension(path, ".garproj");
            Assert.IsTrue(Pump(workspace.SaveProjectAsync(project, CancellationToken.None)).IsSuccess);
            using var reopened = new ManualPreviewWorkspaceService();
            var restored = Pump(reopened.OpenProjectAsync(project, CancellationToken.None)).Single();
            var export = Pump(reopened.ExportAsync(restored.TabId, Path.Combine(Path.GetDirectoryName(path)!, "export"), CancellationToken.None));
            Assert.IsTrue(export.Succeeded);
            Assert.AreEqual(ExportMode.PrintedSession, export.Mode);
            double[] expectedX = [1, 4, 10], expectedY = [10, 30, 20];
            foreach (var audit in export.AuditArtifacts)
            {
                var rows = audit.Rows.OrderBy(row => row.XValue).ToArray();
                Assert.HasCount(3, rows);
                for (int i = 0; i < rows.Length; i++)
                {
                    Assert.AreEqual(expectedX[i], rows[i].XValue, 1e-9);
                    Assert.AreEqual(expectedY[i], rows[i].YValue, 1e-9);
                    Assert.AreEqual(ExportXValueSource.Estimated, rows[i].XSource);
                }
            }
            Assert.HasCount(2, export.AuditArtifacts);
        });
    }

    [TestMethod]
    [DataRow(0d, 5d, 10d, false)]
    [DataRow(-10d, -5d, 0d, false)]
    [DataRow(.5d, 1.5d, 2.5d, false)]
    [DataRow(1d, 6d, 11d, true)]
    public void PrintedXDraftRequiresExplicitChoiceAndReviewThenSupportsUndoRedo(double first, double middle, double final, bool sessionFit)
    {
        WithImage(path =>
        {
            CalibrationOcrProposal[] proposals = ZeroOriginProposals();
            proposals[0] = Proposal("x-first", first.ToString("R", CultureInfo.InvariantCulture), first, "XTick", "X", 20, 112);
            proposals[1] = Proposal("x-mid", middle.ToString("R", CultureInfo.InvariantCulture), middle, "XTick", "X", 80, 112);
            proposals[2] = Proposal("x-final", final.ToString("R", CultureInfo.InvariantCulture), final, "XTick", "X", 140, 112);
            var reader = new OcrStub(proposals);
            using var workspace = CreateWorkspace(path, reader, MeasuredGeometry());
            using var vm = new MainWindowViewModel(workspace);
            var tab = vm.SelectedTab!;
            bool choicePublished = false;
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(vm.HasPrintedXReviewDraft) && vm.HasPrintedXReviewDraft)
                    choicePublished = true;
            };
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            Assert.IsTrue(choicePublished);
            Assert.IsTrue(vm.HasPrintedXReviewDraft);
            Assert.IsFalse(vm.SuggestedUsesPrintedXCoordinates);
            Assert.AreEqual(sessionFit ? first.ToString("R", CultureInfo.InvariantCulture) : string.Empty, vm.SuggestedFirstSession);
            Assert.IsNull(tab.Calibration);
            vm.AxisSuggestionReviewed = true;
            Assert.AreEqual(sessionFit, vm.ApplyAxisSuggestionCommand.CanExecute(null));
            vm.UsePrintedXCoordinatesCommand.Execute(null);
            Assert.IsTrue(vm.SuggestedUsesPrintedXCoordinates);
            Assert.IsFalse(vm.HasPrintedXReviewDraft);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsFalse(vm.FitForEnteredFirstSessionCommand.CanExecute(null));
            Assert.AreEqual(first.ToString("R", CultureInfo.InvariantCulture), vm.SuggestedFirstSession);
            Assert.AreEqual(final.ToString("R", CultureInfo.InvariantCulture), vm.SuggestedFinalSession);
            Assert.HasCount(6, vm.CalibrationPairedTickRows);
            Assert.AreEqual(1, reader.CallCount, "Changing coordinate mode must not rerun OCR.");
            vm.AxisSuggestionReviewed = true;
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.IsTrue(tab.Calibration!.UsesPrintedXCoordinates);
            Assert.AreEqual(first, tab.Calibration.FirstVisibleSession);
            Assert.AreEqual(final, tab.Calibration.XMaximum);
            Assert.IsTrue(vm.ManualUsesPrintedXCoordinates);
            Assert.AreEqual(first.ToString("R", CultureInfo.InvariantCulture), vm.ManualFirstVisibleSessionText);
            Assert.IsFalse(vm.HasAxisSuggestion);
            Assert.IsNull(workspace.CurrentProject.Panels.Single().Calibration!.SessionLattice);
            Assert.IsTrue(workspace.CurrentProject.Panels.Single().Calibration!.Anchors.All(a => a.Kind == CalibrationAnchorKind.Manual));
            vm.UndoCommand.Execute(null);
            Assert.IsNull(tab.Calibration);
            Assert.IsFalse(vm.ManualUsesPrintedXCoordinates);
            vm.RedoCommand.Execute(null);
            Assert.IsTrue(tab.Calibration!.UsesPrintedXCoordinates);
            Assert.IsTrue(vm.ManualUsesPrintedXCoordinates);
            Assert.AreEqual(first.ToString("R", CultureInfo.InvariantCulture), vm.ManualFirstVisibleSessionText);
        });
    }

    [TestMethod]
    public void PrintedXNumericEditInvalidatesReviewAndEvidenceEditRequiresRefit()
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ZeroOriginProposals(), MeasuredGeometry());
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            vm.UsePrintedXCoordinatesCommand.Execute(null);
            vm.AxisSuggestionReviewed = true;
            vm.SuggestedFirstSession = "-0.5";
            Assert.IsTrue(vm.SuggestedUsesPrintedXCoordinates);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.HasCalibrationTickProposal);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            vm.CalibrationLabelRows.Single(row => row.Proposal.RegionId == "x-mid").CorrectedText = "8";
            Assert.IsFalse(vm.SuggestedUsesPrintedXCoordinates);
            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
            vm.RefitSelectedCalibrationLabels();
            Assert.IsFalse(vm.HasPrintedXReviewDraft);
            Assert.IsFalse(vm.UsePrintedXCoordinatesCommand.CanExecute(null));
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        });
    }

    [TestMethod]
    public void PrintedXChoiceIsInvalidatedByTabChangeAndSessionRefitRemainsAvailable()
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ZeroOriginProposals(), MeasuredGeometry());
            using var vm = new MainWindowViewModel(workspace);
            var tab = vm.SelectedTab!;
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            vm.UsePrintedXCoordinatesCommand.Execute(null);
            vm.RefitSelectedCalibrationLabels();
            Assert.IsFalse(vm.SuggestedUsesPrintedXCoordinates);
            Assert.IsTrue(vm.HasPrintedXReviewDraft);
            vm.SuggestedFirstSession = "1";
            vm.FitForEnteredFirstSessionCommand.Execute(null);
            Assert.IsFalse(vm.SuggestedUsesPrintedXCoordinates);
            Assert.AreEqual("1", vm.SuggestedFirstSession);
            Assert.AreEqual("10", vm.SuggestedFinalSession);
            vm.AxisSuggestionReviewed = true;
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            vm.SelectedTab = null;
            vm.SelectedTab = tab;
            Assert.IsFalse(vm.HasPrintedXReviewDraft);
            Assert.IsFalse(vm.SuggestedUsesPrintedXCoordinates);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsNull(tab.Calibration);
        });
    }
}
