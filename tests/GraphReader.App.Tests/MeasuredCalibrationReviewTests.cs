// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;

namespace GraphReader.App.Tests;

public sealed partial class CalibrationTickViewModelTests
{
    [TestMethod]
    public void MeasuredCalibrationRequiresBothPrintedValuesAndExplicitReview()
    {
        WithImage(path =>
        {
            using var workspace = new ProductionWorkspaceService();
            var tab = Pump(workspace.ImportImagesAsync([path], CancellationToken.None)).Single();
            System.Windows.Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
            var localization = new GraphReader.App.Localization.LocalizationService(new System.Windows.ResourceDictionary());
            using var vm = new MainWindowViewModel(workspace, localization);
            var evidence = new WorkflowVisionEnvelope(1, Guid.NewGuid(), workspace.CurrentProject.ProjectId.Value,
                Guid.Parse(tab.PanelId!), "markers", "test", tab.SourceSha256!, null, new(0, 0, 0, 0), 1);
            var draft = new MeasuredCalibrationDraft(evidence, 10.5, 120.5, 90.5, 20.5, 45, "2 / 20", "8 / 80");
            var failure = new ProductionWorkflowStageException(new(ProductionWorkflowFailureCodes.RecalibrationRequired,
                "Errors.CalibrationInvalid", "ambiguous X scale", true, "review"), [evidence]) { CalibrationDrafts = [draft] };
            Assert.IsTrue(vm.SelectCalibrationFailurePanel(failure));
            Assert.IsTrue(vm.ReviewMeasuredTicksCommand.CanExecute(null));
            vm.ReviewMeasuredTicksCommand.Execute(null);
            Assert.IsTrue(vm.SuggestedUsesPrintedXCoordinates);
            Assert.AreEqual("", vm.SuggestedFirstSession);
            Assert.AreEqual("", vm.SuggestedFinalSession);
            Assert.AreEqual("45", vm.SuggestedYMaximum);
            Assert.IsTrue(vm.AxisSuggestionSummary.Contains("2 / 20", StringComparison.Ordinal));
            vm.AxisSuggestionReviewed = true;
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            vm.SuggestedFirstSession = "-2.5";
            vm.SuggestedFinalSession = "4.5";
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsNull(tab.Calibration);
            Assert.IsEmpty(tab.Points);

            vm.AxisSuggestionReviewed = true;
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.IsTrue(tab.Calibration!.UsesPrintedXCoordinates);
            Assert.AreEqual(-2.5, tab.Calibration.FirstVisibleSession);
            Assert.AreEqual(10.5, tab.Calibration.Session1Y0.X);
            Assert.AreEqual(4.5, tab.Calibration.XMaximum);
            Assert.IsFalse(tab.HasMeasuredSuggestion);
            Assert.IsFalse(vm.ReviewMeasuredTicksCommand.CanExecute(null));
            Assert.IsEmpty(tab.Points);
            string saved = Path.ChangeExtension(path, ".garproj");
            Assert.IsTrue(Pump(workspace.SaveProjectAsync(saved, CancellationToken.None)).IsSuccess);
            using var reopened = new ProductionWorkspaceService();
            var restored = Pump(reopened.OpenProjectAsync(saved, CancellationToken.None)).Single();
            Assert.IsTrue(restored.Calibration!.UsesPrintedXCoordinates);
            var details = reopened.CurrentProject.Audit.Events.Select(e => e.Details)
                .Single(d => d is { } json && json.TryGetProperty("method", out var method) &&
                    method.GetString() == "reviewed_measured_ticks")!.Value;
            Assert.AreEqual(evidence.RunId, details.GetProperty("run_id").GetGuid());
            Assert.AreEqual("8 / 80", details.GetProperty("measured").GetProperty("right_readings").GetString());
            Assert.AreEqual(4.5, details.GetProperty("confirmed").GetProperty("last_x").GetDouble());
        });
    }

    [TestMethod]
    public void MeasuredCalibrationRejectsUnboundRepeatedOrInvalidDrafts()
    {
        WithImage(path =>
        {
            using var workspace = new ProductionWorkspaceService();
            var tab = Pump(workspace.ImportImagesAsync([path], CancellationToken.None)).Single();
            using var vm = new MainWindowViewModel(workspace);
            WorkflowVisionEnvelope Evidence(Guid run) => new(1, run, workspace.CurrentProject.ProjectId.Value,
                Guid.Parse(tab.PanelId!), "markers", "test", tab.SourceSha256!, null, new(0, 0, 0, 0), 1);
            var evidence = Evidence(Guid.NewGuid());
            var draft = new MeasuredCalibrationDraft(evidence, 10, 110, 90, 20, 50, "2", "8");
            foreach (var rejected in new MeasuredCalibrationDraft[][] {
                [draft with { Evidence = Evidence(Guid.NewGuid()) }], [draft, draft],
                [draft with { RightX = double.NaN }], [draft with { ZeroY = 128 }], [] })
            {
                var failure = new ProductionWorkflowStageException(new(ProductionWorkflowFailureCodes.RecalibrationRequired,
                    "Errors.CalibrationInvalid", "review", true, "review"), [evidence]) { CalibrationDrafts = rejected };
                Assert.IsTrue(vm.SelectCalibrationFailurePanel(failure));
                Assert.IsFalse(tab.HasMeasuredSuggestion);
                Assert.IsFalse(vm.ReviewMeasuredTicksCommand.CanExecute(null));
                Assert.IsNull(tab.Calibration);
            }
        });
    }
}
