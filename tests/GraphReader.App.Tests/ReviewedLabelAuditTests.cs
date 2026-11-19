// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;

namespace GraphReader.App.Tests;

public sealed partial class CalibrationTickViewModelTests
{
    [TestMethod]
    public void ApplyingReviewedLabelsPreservesRawReadingsExclusionsAndConfirmedValues()
    {
        WithImage(path =>
        {
            var proposals = ProposalsWithExtraRow();
            proposals[1] = proposals[1] with { Text = "18", NormalizedText = "18", NumericValue = 18 };
            using var workspace = CreateWorkspace(path, proposals);
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            vm.CalibrationLabelRows.Single(row => row.Proposal.RegionId == "noise").IsIncluded = false;
            vm.CalibrationLabelRows.Single(row => row.Proposal.RegionId == "x-mid").CorrectedText = "12.5";
            vm.RefitSelectedCalibrationLabels();
            Assert.AreEqual("24", vm.SuggestedFinalSession);
            vm.SuggestedFinalSession = "25";
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.IsNull(vm.SelectedTab!.Calibration);
            vm.AxisSuggestionReviewed = true;
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            string saved = Path.ChangeExtension(path, ".garproj");
            Assert.IsTrue(Pump(workspace.SaveProjectAsync(saved, CancellationToken.None)).IsSuccess);
            using var reopened = new ManualPreviewWorkspaceService();
            var tab = Pump(reopened.OpenProjectAsync(saved, CancellationToken.None)).Single();
            Assert.AreEqual(25d, tab.Calibration!.XMaximum);
            var receipts = reopened.CurrentProject.Audit.Events.Select(item => item.Details).OfType<System.Text.Json.JsonElement>()
                .Where(item => item.TryGetProperty("method", out var method) && method.GetString() == "reviewed_ocr_labels").ToArray();
            Assert.HasCount(1, receipts);
            var receipt = receipts[0];
            Assert.IsFalse(receipt.GetProperty("fit_current_at_apply").GetBoolean());
            var rows = receipt.GetProperty("readings").EnumerateArray().ToDictionary(row => row.GetProperty("region_id").GetString()!);
            Assert.AreEqual("18", rows["x-mid"].GetProperty("raw_text").GetString());
            Assert.AreEqual("12.5", rows["x-mid"].GetProperty("edited_text").GetString());
            Assert.AreEqual(12.5, rows["x-mid"].GetProperty("pair").GetProperty("value").GetDouble());
            Assert.AreEqual("99", rows["noise"].GetProperty("raw_text").GetString());
            Assert.IsFalse(rows["noise"].GetProperty("included").GetBoolean());
            Assert.AreEqual(System.Text.Json.JsonValueKind.Null, rows["noise"].GetProperty("pair").ValueKind);
            Assert.AreEqual(System.Text.Json.JsonValueKind.Null, rows["y-zero"].GetProperty("edited_text").ValueKind);
            Assert.AreEqual(24d, rows["x-final"].GetProperty("pair").GetProperty("value").GetDouble());
            Assert.AreEqual(25d, receipt.GetProperty("confirmed").GetProperty("last_x").GetDouble());
            Assert.AreEqual(proposals[1].Polygon.Count, rows["x-mid"].GetProperty("original_polygon").GetArrayLength());
        });
    }

    [TestMethod]
    public void ManualValuesAfterChangingSelectionCannotInheritTheDiscardedLabelFit()
    {
        WithImage(path =>
        {
            using var workspace = CreateWorkspace(path, ValidProposals());
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            Assert.IsTrue(vm.HasCalibrationTickProposal);
            vm.CalibrationLabelRows.Single(row => row.Proposal.RegionId == "x-mid").IsIncluded = false;
            Assert.IsFalse(vm.HasCalibrationTickProposal);
            vm.SuggestedFirstSession = "1";
            vm.SuggestedFinalSession = "24";
            vm.SuggestedYMaximum = "10";
            vm.AxisSuggestionReviewed = true;
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.IsNotNull(vm.SelectedTab!.Calibration);
            Assert.IsFalse(workspace.CurrentProject.Audit.Events.Select(item => item.Details)
                .OfType<System.Text.Json.JsonElement>().Any(item => item.TryGetProperty("method", out var method) &&
                    method.GetString() == "reviewed_ocr_labels"));
        });
    }

}
