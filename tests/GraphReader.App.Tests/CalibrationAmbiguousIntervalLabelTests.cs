// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Reflection;
using System.Text;
using GraphReader.App.Integration.CalibrationOcr;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Ocr;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationAmbiguousIntervalLabelTests
{
    [TestMethod]
    [DataRow("XTick", "X")]
    [DataRow("Other", "X")]
    [DataRow("Other", "")]
    public void NumericAmbiguousRowsBlockXEvenWhenOtherPairsAreValid(string role, string suggestedAxis)
    {
        var response = Response(role, suggestedAxis);
        foreach (var result in new[] { CalibrationTickProposal.Create(Geometry(), response), CalibrationTickProposal.CreateReviewDraft(Geometry(), response) })
        {
            Assert.IsFalse(result.IsComplete);
            Assert.IsNull(result.XReviewDraft);
            Assert.IsNull(result.PrintedXReviewDraft);
            Assert.IsFalse(result.PairedTicks.Any(pair => pair.Label.RegionId == "ambiguous"));
            CollectionAssert.Contains(result.Reasons.ToArray(), CalibrationTickProposalReasons.AmbiguousAssociation);
        }
        Assert.IsNotNull(CalibrationTickProposal.CreateReviewDraft(Geometry(), response).YReviewDraft);
    }

    [TestMethod]
    public void CorrectionsPreserveBlockAndExplicitExclusionCanRestoreReview()
    {
        var response = Response("XTick", "X");
        var evidence = response.Proposals.Select(row => new CalibrationTickFitEvidence(row, row.RegionId == "ambiguous" ? "2" : null)).ToArray();
        Assert.IsTrue(evidence.Single(item => item.RawProposal.RegionId == "ambiguous").EffectiveProposal.IsAmbiguousIntervalXLabel);
        var blocked = CalibrationTickProposal.CreateReviewDraft(Geometry(), response, evidence);
        Assert.IsFalse(blocked.IsComplete);
        Assert.IsNull(blocked.XReviewDraft);
        var included = evidence.Where(item => item.RawProposal.RegionId != "ambiguous").ToArray();
        var recovered = CalibrationTickProposal.CreateReviewDraft(Geometry(), response, included);
        Assert.IsTrue(recovered.IsComplete, string.Join(',', recovered.Reasons));
        Assert.HasCount(7, response.Proposals);
    }

    [TestMethod]
    public void ContradictoryAndWrongAxisProvenanceFailClosed()
    {
        var response = Response("XTick", "X");
        var row = response.Proposals.Single(item => item.RegionId == "ambiguous");
        foreach (var replacement in new[] { row with { IsIntervalXLabel = true }, row with { Role = "YTick", SuggestedAxis = "Y" } })
        {
            var invalid = response with { Proposals = response.Proposals.Select(item => item.RegionId == "ambiguous" ? replacement : item).ToArray() };
            var result = CalibrationTickProposal.CreateReviewDraft(Geometry(), invalid);
            Assert.IsFalse(result.IsComplete);
            Assert.IsNull(result.XReviewDraft);
            Assert.IsNull(result.YReviewDraft);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AmbiguousAndValidYDuplicateIdentitySuppressesAllDrafts(bool ambiguousFirst)
    {
        var response = Response("XTick", "X");
        var rows = response.Proposals.Select(row => row.RegionId == "ambiguous" ? row with { RegionId = "y1" } : row).ToList();
        if (ambiguousFirst)
        {
            var ambiguous = rows.Single(row => row.IsAmbiguousIntervalXLabel);
            rows.Remove(ambiguous);
            rows.Insert(0, ambiguous);
        }
        var result = CalibrationTickProposal.CreateReviewDraft(Geometry(), response with { Proposals = rows });
        Assert.IsFalse(result.IsComplete);
        Assert.IsNull(result.XReviewDraft);
        Assert.IsNull(result.YReviewDraft);
        Assert.IsNull(result.PrintedXReviewDraft);
        CollectionAssert.Contains(result.Reasons.ToArray(), CalibrationTickProposalReasons.DuplicateAssociation);
    }

    [TestMethod]
    public async Task PrivateWireRoundTripsFlagAndOmitsLegacyDefault()
    {
        var response = Response("XTick", "X");
        using var stream = new MemoryStream();
        await CalibrationOcrWire.WriteResponseAsync(stream, response, CancellationToken.None);
        stream.Position = 0;
        var actual = await CalibrationOcrWire.ReadResponseAsync(stream, CancellationToken.None);
        Assert.IsTrue(actual.Proposals.Single(row => row.RegionId == "ambiguous").IsAmbiguousIntervalXLabel);
        using var legacy = new MemoryStream();
        await CalibrationOcrWire.WriteResponseAsync(legacy, response with { Proposals = response.Proposals.Select(row => row with { IsAmbiguousIntervalXLabel = false }).ToArray() }, CancellationToken.None);
        Assert.IsFalse(Encoding.UTF8.GetString(legacy.ToArray()).Contains("is_ambiguous_interval_x_label", StringComparison.Ordinal));
        legacy.Position = 0;
        actual = await CalibrationOcrWire.ReadResponseAsync(legacy, CancellationToken.None);
        Assert.IsFalse(actual.Proposals.Any(row => row.IsAmbiguousIntervalXLabel));
    }

    [TestMethod]
    public void SessionCarriesAmbiguousReasonWithoutModifyingRecognition()
    {
        const string id = "ambiguous";
        var candidate = new OcrDetectedRegion(id, OcrPolygon.FromRectangle(new(35, 75, 30, 12)), 0, .9,
            Evidence: new(4, .5, .9, 0, false, [ConnectedComponentTextRegionDetector.AmbiguousIntervalXLabelReason]));
        var reading = new OcrRecognition(id, OcrSourceImage.Original, [new("12", .9, OcrSourceImage.Original)], 0);
        MethodInfo method = typeof(CalibrationOcrSession).GetMethod("Proposal", BindingFlags.Static | BindingFlags.NonPublic)!;
        var proposal = (CalibrationOcrProposal)method.Invoke(null, [candidate, reading, new OcrRectangle(30, 10, 60, 55)])!;
        Assert.IsTrue(proposal.IsAmbiguousIntervalXLabel);
        Assert.AreEqual("12", proposal.Text);
        Assert.AreEqual(12d, proposal.NumericValue);
        Assert.AreEqual(.9, proposal.Confidence);
    }

    private static RustAxisGeometry Geometry() => new(30, 65, 90, 10, .9) { XTickPositions = [30, 50, 70, 90], YTickPositions = [15, 40, 65] };
    private static CalibrationOcrResponse Response(string role, string suggestedAxis)
    {
        CalibrationOcrProposal[] rows = [Label("x1", "X", 30, 1), Label("x2", "X", 50, 2), Label("x3", "X", 70, 3),
            Label("y1", "Y", 15, 10), Label("y2", "Y", 40, 5), Label("y3", "Y", 65, 0),
            Label("ambiguous", "X", 60, 2) with { Role = role, SuggestedAxis = suggestedAxis, IsAmbiguousIntervalXLabel = true }];
        return new(new("owned", "binding", new string('a', 64), new string('b', 64), 128, 110, new(30, 10, 90, 65))
            { MeasuredXTicks = [30, 50, 70, 90], MeasuredYTicks = [15, 40, 65] }, rows, null, null, null, null, null);
    }
    private static CalibrationOcrProposal Label(string id, string axis, int position, int value)
    {
        double x = axis == "X" ? position : 17, y = axis == "X" ? 81 : position;
        string text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new(id, [new(x - 4, y - 6), new(x + 4, y - 6), new(x + 4, y + 6), new(x - 4, y + 6)], text, .9, value, text, true, axis == "X" ? "XTick" : "YTick", axis, null);
    }
}

public sealed partial class CalibrationTickViewModelTests
{
    [TestMethod]
    public void AmbiguousLabelDisplaysGuidanceAndCannotCopyEvenAfterCorrection()
    {
        WithImage(path =>
        {
            System.Windows.Application.ResourceAssembly ??= typeof(GraphReader.App.MainWindow).Assembly;
            var localization = new GraphReader.App.Localization.LocalizationService(new System.Windows.ResourceDictionary());
            localization.ApplyCulture(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
            CalibrationOcrProposal ambiguous = Proposal("ambiguous", "12", 12, "XTick", "X", 80, 112) with
                { IsAmbiguousIntervalXLabel = true };
            using var workspace = CreateWorkspace(path, [.. ValidProposals(), ambiguous], MeasuredGeometry());
            using var vm = new GraphReader.App.ViewModels.MainWindowViewModel(workspace, localization);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            var row = vm.CalibrationLabelRows.Single(item => item.Proposal.RegionId == "ambiguous");
            vm.SelectedCalibrationLabel = row;
            string warning = localization.GetString(GraphReader.App.Localization.LocalizationKeys.ManualCalibrationLabelsAmbiguousInterval);
            StringAssert.Contains(row.DisplayText, warning);
            Assert.IsNull(row.Proposal.FailureCode);
            Assert.AreEqual("12", row.Proposal.Text);
            Assert.AreEqual(.95, row.Proposal.Confidence);
            vm.SuggestedFirstSession = "1";
            vm.SuggestedFinalSession = "24";
            Assert.IsFalse(vm.UseCalibrationLabelForYMaximumCommand.CanExecute(null));
            Assert.IsFalse(vm.UseCalibrationLabelForFirstSessionCommand.CanExecute(null));
            Assert.IsFalse(vm.UseCalibrationLabelForFinalSessionCommand.CanExecute(null));
            row.CorrectedText = "13";
            Assert.IsTrue(row.HasValidCorrection);
            Assert.IsFalse(vm.UseCalibrationLabelForYMaximumCommand.CanExecute(null));
            Assert.IsFalse(vm.UseCalibrationLabelForFirstSessionCommand.CanExecute(null));
            Assert.IsFalse(vm.UseCalibrationLabelForFinalSessionCommand.CanExecute(null));
            localization.ApplyCulture(System.Globalization.CultureInfo.GetCultureInfo("ko-KR"));
            Assert.AreEqual("en-US", localization.CurrentCulture.Name);
            StringAssert.Contains(row.DisplayText, warning);
            Assert.IsTrue(row.IsIncluded);
            Assert.IsTrue(row.Proposal.IsAmbiguousIntervalXLabel);
            Assert.AreEqual("12", row.Proposal.Text);
        });
    }

    [TestMethod]
    public void AmbiguousOtherRoleRowCanBeExcludedFromReviewWithoutLosingRawEvidence()
    {
        WithImage(path =>
        {
            CalibrationOcrProposal ambiguous = Proposal("ambiguous", "12", 12, "XTick", "X", 80, 112) with
                { Role = "Other", IsAmbiguousIntervalXLabel = true };
            using var workspace = CreateWorkspace(path, [.. ValidProposals(), ambiguous], MeasuredGeometry());
            using var vm = new GraphReader.App.ViewModels.MainWindowViewModel(workspace);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            var row = vm.CalibrationLabelRows.Single(item => item.Proposal.RegionId == "ambiguous");
            Assert.AreEqual("Other", row.Proposal.Role);
            Assert.AreEqual("X", row.Proposal.SuggestedAxis);
            Assert.IsTrue(row.IsIncluded);
            vm.RefitSelectedCalibrationLabels();
            Assert.IsTrue(vm.HasCalibrationTickProposalStatus);
            Assert.IsFalse(string.IsNullOrWhiteSpace(vm.CalibrationTickProposalStatusText));
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            row.IsIncluded = false;
            Assert.IsFalse(row.IsIncluded);
            vm.RefitSelectedCalibrationLabels();
            Assert.AreEqual("1", vm.SuggestedFirstSession);
            Assert.AreEqual("10", vm.SuggestedYMaximum);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsTrue(row.Proposal.IsAmbiguousIntervalXLabel);
            Assert.AreEqual("12", row.Proposal.Text);
            Assert.IsTrue(vm.CalibrationLabelRows.Contains(row));
        });
    }
}
