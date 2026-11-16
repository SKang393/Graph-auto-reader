// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;
using GraphReader.Domain;
using PixelPoint = GraphReader.Axis.PixelPoint;

namespace GraphReader.App.Tests;

public sealed partial class AxisCandidateWorkspaceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SelectedAffineChoiceReadsRectifiedReceiptAndRequiresFreshReviewBeforeApplyUndo(bool incomplete) => WithImage(path =>
    {
        byte[] originalBytes = File.ReadAllBytes(path);
        var provider = new CandidateProvider([new(0, null) { AffineGeometry = CandidateAffineFrame }]) { Incomplete = incomplete };
        var ticks = new AffineChoiceTicks(); var reader = new AffineChoiceReader(ticks);
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService: provider,
            axisTickMeasurementService: ticks, calibrationOcrSuggestionService: reader);
        var tab = Pump(workspace.ImportImagesAsync([path], CancellationToken.None)).Single();
        workspace.Calibrate(tab.TabId, Calibration());
        var previousCalibration = tab.Calibration;
        string series = workspace.AddSeries(tab.TabId, new("Owned", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
        using var vm = new MainWindowViewModel(workspace);
        var before = workspace.CurrentProject;
        Pump(Completed(vm.FindAxisCandidatesAsync(CancellationToken.None)));
        Assert.IsNull(vm.SelectedAxisCandidate);
        Assert.IsFalse(vm.UseAxisCandidateCommand.CanExecute(null));
        vm.SelectedAxisCandidate = vm.AxisCandidates.Single();
        vm.UseAxisCandidateCommand.Execute(null);
        Assert.IsTrue(vm.HasAffineAxisSuggestion);
        Assert.IsFalse(vm.IsManualAxisDraft);
        Assert.IsTrue(vm.HasAxisGeometryReviewWarning);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
        Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
        Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Assert.AreSame(before, workspace.CurrentProject);
        Assert.AreSame(previousCalibration, tab.Calibration);
        Assert.AreEqual(0, reader.Calls);
        Pump(Completed(vm.ReadCalibrationLabelsAsync(CancellationToken.None)));
        var labels = (WorkspaceCalibrationLabelSuggestion?)typeof(MainWindowViewModel)
            .GetField("_calibrationLabelSuggestion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
        Assert.IsNotNull(labels);
        Assert.IsTrue(labels.IsRectified);
        Assert.AreEqual(CandidateAffineFrame, labels.AxisSuggestion.AffineGeometry);
        Assert.IsNull(labels.AxisSuggestion.Geometry);
        Assert.AreEqual(incomplete, labels.AxisSuggestion.ReviewReasons.Contains("native_incomplete_candidate_search"));
        Assert.IsTrue(workspace.IsCalibrationLabelSuggestionCurrent(labels));
        Assert.AreEqual(1, reader.Calls);
        Assert.AreEqual(1, ticks.Calls);
        Assert.AreEqual(0, ticks.StrictCalls);
        Assert.AreEqual(1, provider.CandidateCalls);
        Assert.AreEqual(0, provider.SuggestCalls);
        Assert.AreEqual(string.Empty, vm.SuggestedFirstSession, "Reading labels does not itself fit the draft.");
        Assert.AreEqual(0, vm.CalibrationPairedTickRows.Count);
        string rawReceipt = JsonSerializer.Serialize(labels.Response);
        vm.RefitSelectedCalibrationLabels();
        Assert.AreEqual("1", vm.SuggestedFirstSession);
        Assert.AreEqual("3", vm.SuggestedFinalSession);
        Assert.AreEqual("10", vm.SuggestedYMaximum);
        Assert.HasCount(6, vm.CalibrationPairedTickRows);
        Assert.IsFalse(vm.HasEstimatedCalibrationPositions);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Assert.IsTrue(vm.HasAxisGeometryReviewWarning);
        Assert.AreEqual(rawReceipt, JsonSerializer.Serialize(labels.Response));
        Assert.IsNotNull(labels.WorkingFrame);
        CalibrationOcrPoint originalCenter = labels.WorkingFrame.ToOriginal(new CalibrationOcrPoint(ticks.X[1], ticks.Y[1]));
        var point = workspace.AddPoint(tab.TabId, series, originalCenter.X, originalCenter.Y);
        double previousY = point.GraphY;
        vm.AxisSuggestionReviewed = true;
        vm.SuggestedYMaximum = "11";
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        vm.SuggestedYMaximum = "10";
        vm.AxisSuggestionReviewed = true;
        Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        CalibrationTickProposalResult proposal = CalibrationTickProposal.CreateReviewDraft(labels.FitGeometry, labels.Response);
        Assert.IsTrue(proposal.IsComplete);
        CalibrationTickAnchors expected = labels.OriginalAnchors(proposal.Anchors!);
        Pump(Completed(vm.ApplyAxisSuggestionAsync(CancellationToken.None)));
        Assert.AreEqual(new PixelPoint(expected.FirstSessionY0.X, expected.FirstSessionY0.Y), tab.Calibration!.Session1Y0);
        Assert.AreEqual(new PixelPoint(expected.FirstSessionYMaximum.X, expected.FirstSessionYMaximum.Y), tab.Calibration.Session1YMaximum);
        Assert.AreEqual(new PixelPoint(expected.FinalSessionY0.X, expected.FinalSessionY0.Y), tab.Calibration.SessionMaximumY0);
        Assert.IsNull(tab.Calibration.XTransform);
        Assert.IsNull(tab.Calibration.YTransform);
        Assert.AreEqual(2d, point.GraphX, 1e-9);
        Assert.AreEqual(5d, point.GraphY, 1e-9);
        Assert.IsTrue(workspace.CurrentProject.Panels.Single().Calibration!.UserConfirmed);
        Assert.IsTrue(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        Assert.AreEqual(previousCalibration, tab.Calibration);
        Assert.AreEqual(previousY, tab.Points.Single().GraphY);
        Assert.AreEqual(1, reader.Calls);
        Assert.AreEqual(1, ticks.Calls);
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(path));
    });

    private sealed class AffineChoiceTicks : IAxisTickMeasurementService
    {
        internal int Calls { get; private set; }
        internal int StrictCalls { get; private set; }
        internal int[] X { get; private set; } = [];
        internal int[] Y { get; private set; } = [];
        public Task<AxisTickMeasurementResult> MeasureAsync(byte[] image, RustAxisGeometry geometry, CancellationToken token)
        { StrictCalls++; throw new InvalidOperationException("Affine review must use the explicit partial route."); }
        public Task<AxisTickMeasurementResult> MeasureManualPartialAsync(byte[] image, RustAxisGeometry box, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            X = [box.Left + 10, box.Left + 30, box.Left + 50]; Y = [box.Top + 10, box.Top + 30, box.Top + 50];
            using var input = new MemoryStream(image);
            BitmapFrame decoded = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            return Task.FromResult(new AxisTickMeasurementResult(Convert.ToHexStringLower(SHA256.HashData(image)), decoded.PixelWidth,
                decoded.PixelHeight, new(box.Left, box.Bottom, box.Right, box.Top, X, Y), null));
        }
    }

    private sealed class AffineChoiceReader(AffineChoiceTicks ticks) : ICalibrationOcrSuggestionService
    {
        internal int Calls { get; private set; }
        public Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request, ReadOnlyMemory<byte> image, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(image.Span)), request.PanelImageSha256);
            var rows = new List<CalibrationOcrProposal>();
            for (int index = 0; index < 3; index++)
            {
                rows.Add(Row("x" + index, index + 1, "X", ticks.X[index], request.Plot.Bottom + 8));
                rows.Add(Row("y" + index, 10 - 5 * index, "Y", request.Plot.Left - 8, ticks.Y[index]));
            }
            var sequence = new CalibrationOcrSequenceCheck(false, 3, 3, 0, []);
            return Task.FromResult(CalibrationOcrClientTests.Success(request) with { Proposals = rows.AsReadOnly(), XSequence = sequence, YSequence = sequence });
        }
        private static CalibrationOcrProposal Row(string id, double value, string axis, double x, double y) =>
            new(id, [new(x - 2, y - 2), new(x + 2, y - 2), new(x + 2, y + 2), new(x - 2, y + 2)],
                value.ToString(CultureInfo.InvariantCulture), .9, value, value.ToString(CultureInfo.InvariantCulture), true, axis + "Tick", axis, null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
