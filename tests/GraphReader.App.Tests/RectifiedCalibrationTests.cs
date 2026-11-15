// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Controls;
using GraphReader.App.Services;
using GraphReader.App.Localization;
using GraphReader.App.ViewModels;
using GraphReader.Axis;
using GraphReader.Domain;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class RectifiedCalibrationTests
{
    [TestMethod]
    [DataRow(-2d, false)]
    [DataRow(2d, false)]
    [DataRow(0d, true)]
    public void SameDerivedPixelsBindTicksAndRawReceiptWhileEveryProjectionReturnsOriginalPanelPixels(double tilt, bool shear) =>
        WithFixture(tilt, shear, (workspace, axis, ticks, reader, path) =>
        {
            var tab = workspace.CreateWorkspace().Single();
            byte[] original = File.ReadAllBytes(path);
            WorkspaceAxisSuggestion suggestion = Pump(workspace.SuggestAxesAsync(tab.TabId, CancellationToken.None))!;
            var result = Pump(workspace.ReadCalibrationLabelsAsync(suggestion, CancellationToken.None));
            Assert.IsNotNull(result);
            Assert.IsTrue(result.IsRectified);
            Assert.AreSame(suggestion, result.AxisSuggestion);
            Assert.IsNull(suggestion.Geometry);
            Assert.AreSame(reader.Response, result.Response);
            Assert.AreSame(ticks.Result, result.TickMeasurementReceipt);
            Assert.AreSame(ticks.Image, reader.Image, "Tick scanning and OCR must receive the identical encoded derived buffer.");
            Assert.AreNotEqual(suggestion.Binding, result.Response.Request!.Binding);
            Assert.AreEqual(Hash(reader.Image!), result.Response.Request.PanelImageSha256);
            Assert.AreEqual(Hash(original), result.WorkingFrame!.OriginalImageSha256);
            Assert.AreNotEqual(Hash(original), result.WorkingFrame.DerivedImageSha256);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(original, axis.Image!);
            Assert.IsTrue(workspace.IsCalibrationLabelSuggestionCurrent(result));
            var proposal = CalibrationTickProposal.CreateReviewDraft(result.FitGeometry, result.Response);
            Assert.IsTrue(proposal.IsComplete, string.Join(',', proposal.Reasons));
            Assert.HasCount(6, proposal.PairedTicks);
            Assert.IsFalse(proposal.UsesEstimatedPositions);
            var anchors = result.OriginalAnchors(proposal.Anchors!);
            AssertProjection(result, proposal.Anchors!.FirstSessionY0, anchors.FirstSessionY0);
            AssertProjection(result, proposal.Anchors.FirstSessionYMaximum, anchors.FirstSessionYMaximum);
            AssertProjection(result, proposal.Anchors.FinalSessionY0, anchors.FinalSessionY0);
            foreach (var row in result.Response.Proposals)
            {
                var polygon = result.OriginalPolygon(row);
                Assert.HasCount(row.Polygon.Count, polygon);
                for (int i = 0; i < polygon.Count; i++) AssertProjection(result, row.Polygon[i], polygon[i]);
            }
            foreach (var pair in proposal.PairedTicks)
                AssertProjection(result, pair.Axis == "X" ? new(pair.PixelPosition, result.FitGeometry.Bottom)
                    : new(result.FitGeometry.Left, pair.PixelPosition), result.OriginalTickPoint(pair.Axis, pair.PixelPosition));
            Assert.ThrowsExactly<ArgumentException>(() => result.OriginalPolygon(result.Response.Proposals[0] with { }));
            Assert.IsFalse(workspace.IsCalibrationLabelSuggestionCurrent(result with { WorkingFrameIdentity = null }));
            Assert.IsFalse(workspace.IsCalibrationLabelSuggestionCurrent(result with { WorkingFrame = null }));
            Assert.IsFalse(workspace.IsCalibrationLabelSuggestionCurrent(result with { FitGeometry = result.FitGeometry with { } }));
            Assert.IsFalse(workspace.IsCalibrationLabelSuggestionCurrent(result with { Response = result.Response with { } }));
            Assert.IsFalse(workspace.IsCalibrationLabelSuggestionCurrent(result with { AxisSuggestion = suggestion with { } }));
            result.Response.Request.MeasuredXTicks![0]++;
            Assert.IsFalse(workspace.IsCalibrationLabelSuggestionCurrent(result));
        });

    [TestMethod]
    public void CachedCorrectionPartialAndRefitMapWholeAnchorsWithoutRereadingAndRequireFreshReview() =>
        WithFixture(2, true, (workspace, axis, ticks, reader, _) =>
        {
            reader.InvalidMiddleY = true;
            using var vm = new MainWindowViewModel(workspace);
            var tab = vm.SelectedTab!;
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            Assert.AreEqual(1, axis.Calls);
            Assert.AreEqual(1, ticks.Calls);
            Assert.AreEqual(1, reader.Calls);
            Assert.AreEqual("1", vm.SuggestedFirstSession);
            Assert.AreEqual("3", vm.SuggestedFinalSession);
            Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
            Assert.IsTrue(vm.HasCalibrationLabelWarning);
            string raw = JsonSerializer.Serialize(reader.Response);
            var result = CurrentLabels(vm);
            var rawProposal = CalibrationTickProposal.CreateReviewDraft(result.FitGeometry, result.Response);
            Assert.IsNotNull(rawProposal.XReviewDraft);
            Assert.IsNull(rawProposal.YReviewDraft);
            var partial = Preview(tab);
            // The unchanged Y positions are merged with the validated X positions in the derived frame.
            var expected = result.OriginalAnchors(new(new(ticks.X[0], result.FitGeometry.Bottom),
                new(ticks.X[0], result.FitGeometry.Top), new(ticks.X[^1], result.FitGeometry.Bottom)));
            AssertAnchors(expected, partial);
            CalibrationLabelRowViewModel row = vm.CalibrationLabelRows.Single(item => item.Proposal.RegionId == "y1");
            Assert.AreSame(reader.Response!.Proposals.Single(item => item.RegionId == "y1"), row.Proposal);
            Assert.AreEqual(0d, row.Proposal.Confidence);
            row.CorrectedText = "5";
            vm.RefitSelectedCalibrationLabels();
            Assert.AreEqual("10", vm.SuggestedYMaximum);
            Assert.AreEqual("1", vm.SuggestedFirstSession);
            Assert.AreEqual("3", vm.SuggestedFinalSession);
            Assert.AreEqual(6, vm.CalibrationPairedTickRows.Count);
            Assert.IsFalse(vm.HasEstimatedCalibrationPositions);
            Assert.IsFalse(vm.AxisSuggestionReviewed);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsNull(tab.Calibration);
            Assert.AreEqual(raw, JsonSerializer.Serialize(reader.Response));
            var corrected = CalibrationTickProposal.CreateReviewDraft(result.FitGeometry, result.Response,
                vm.CalibrationLabelRows.Select(item => item.FitEvidence).ToArray());
            Assert.IsTrue(corrected.IsComplete);
            AssertAnchors(result.OriginalAnchors(corrected.Anchors!), Preview(tab));
            vm.AxisSuggestionReviewed = true;
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Pump(vm.ApplyAxisSuggestionAsync(CancellationToken.None));
            Assert.IsNotNull(tab.Calibration);
            Assert.IsFalse(ManualCalibrationMapper.IsAxisAligned(tab.Calibration));
            string series = workspace.AddSeries(tab.TabId, new("Owned", "●", MarkerShape.Circle,
                MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
            var p = result.WorkingFrame!.ToOriginal(new CalibrationOcrPoint(ticks.X[1], ticks.Y[1]));
            var point = workspace.AddPoint(tab.TabId, series, p.X, p.Y);
            Assert.AreEqual(2d, point.GraphX, 1e-9);
            Assert.AreEqual(5d, point.GraphY, 1e-9);
            vm.UndoCommand.Execute(null);
            Assert.IsNull(tab.Calibration);
            Assert.AreEqual(1, reader.Calls);
            Assert.AreEqual(1, ticks.Calls);
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PrintedOrEnteredOriginMapsDerivedFitAnchorsWithoutChangingRawPairs(bool printed) =>
        WithFixture(-2, false, (workspace, _, ticks, reader, _) =>
        {
            reader.FirstX = 0;
            using var vm = new MainWindowViewModel(workspace);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            var result = CurrentLabels(vm);
            var initial = CalibrationTickProposal.CreateReviewDraft(result.FitGeometry, result.Response);
            Assert.IsNotNull(initial.PrintedXReviewDraft);
            Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
            string raw = JsonSerializer.Serialize(reader.Response);
            if (printed)
            {
                vm.UsePrintedXCoordinatesCommand.Execute(null);
                Assert.IsTrue(vm.SuggestedUsesPrintedXCoordinates);
                Assert.AreEqual("0", vm.SuggestedFirstSession);
                AssertAnchors(result.OriginalAnchors(initial.PrintedXReviewDraft.Anchors), Preview(vm.SelectedTab!));
            }
            else
            {
                vm.SuggestedFirstSession = "1";
                vm.FitForEnteredFirstSessionCommand.Execute(null);
                var expected = CalibrationTickProposal.CreateReviewDraftForFirstSession(result.FitGeometry, result.Response,
                    vm.CalibrationLabelRows.Select(item => item.FitEvidence).ToArray(), 1);
                Assert.IsTrue(expected.IsComplete);
                AssertAnchors(result.OriginalAnchors(expected.Anchors!), Preview(vm.SelectedTab!));
                Assert.IsFalse(vm.SuggestedUsesPrintedXCoordinates);
            }
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.AreEqual(raw, JsonSerializer.Serialize(reader.Response));
            Assert.AreEqual(1, ticks.Calls);
            Assert.AreEqual(1, reader.Calls);
        });

    [TestMethod]
    public void CropOffsetIsAppliedOnceWhenRectifiedAnchorsAreSavedReopenedAndExported() =>
        WithFixture(2, false, (workspace, _, ticks, _, path) =>
        {
            var parent = workspace.CreateWorkspace().Single();
            var child = Pump(workspace.CreatePanelCropAsync(workspace.GetPanelCropContext(parent.TabId)!,
                new(20, 10, 420, 360), CancellationToken.None));
            var suggestion = Pump(workspace.SuggestAxesAsync(child.TabId, CancellationToken.None))!;
            var result = Pump(workspace.ReadCalibrationLabelsAsync(suggestion, CancellationToken.None))!;
            Assert.IsTrue(workspace.IsCalibrationLabelSuggestionCurrent(result));
            var fit = CalibrationTickProposal.CreateReviewDraft(result.FitGeometry, result.Response);
            Assert.IsTrue(fit.IsComplete);
            var a = result.OriginalAnchors(fit.Anchors!);
            workspace.Calibrate(child.TabId, new(new(a.FirstSessionY0.X, a.FirstSessionY0.Y),
                new(a.FirstSessionYMaximum.X, a.FirstSessionYMaximum.Y), new(a.FinalSessionY0.X, a.FinalSessionY0.Y),
                10, 3) { PreserveAffineGeometry = true });
            var panel = workspace.CurrentProject.Panels.Single(item => item.PanelId.Value.ToString("D") == child.PanelId);
            Assert.AreEqual(a.FirstSessionY0.X + 20, panel.Calibration!.Anchors[0].Screen.X, 1e-9);
            Assert.AreEqual(a.FirstSessionY0.Y + 10, panel.Calibration.Anchors[0].Screen.Y, 1e-9);
            string series = workspace.AddSeries(child.TabId, new("Owned", "●", MarkerShape.Circle,
                MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
            var p = result.WorkingFrame!.ToOriginal(new CalibrationOcrPoint(ticks.X[1], ticks.Y[1]));
            workspace.AddPoint(child.TabId, series, p.X, p.Y);
            string project = Path.Combine(Path.GetDirectoryName(path)!, "reviewed.garproj");
            Assert.IsTrue(Pump(workspace.SaveProjectAsync(project, CancellationToken.None)).IsSuccess);
            using var reopened = new ManualPreviewWorkspaceService();
            var restored = Pump(reopened.OpenProjectAsync(project, CancellationToken.None)).Single(item => item.TabId == child.TabId);
            Assert.AreEqual(2d, restored.Points.Single().GraphX, 1e-9);
            Assert.AreEqual(5d, restored.Points.Single().GraphY, 1e-9);
            string destination = Path.Combine(Path.GetDirectoryName(path)!, "export");
            Assert.IsTrue(Pump(reopened.ExportAsync(restored.TabId, destination, CancellationToken.None)).Succeeded);
            string csv = Directory.GetFiles(destination, "*.csv", SearchOption.AllDirectories)
                .Single(file => !Path.GetFileName(file).Contains("audit", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(5d, double.Parse(File.ReadAllLines(csv)[1].Split(',')[1], CultureInfo.InvariantCulture), 1e-9);
        });

    [TestMethod]
    [DataRow("binding")]
    [DataRow("cancel")]
    [DataRow("dispose")]
    public void StaleCancelledOrDisposedDerivedTickReplyCannotStartOcr(string change) =>
        WithFixture(2, false, (workspace, _, ticks, reader, _) =>
        {
            ticks.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var tab = workspace.CreateWorkspace().Single();
            var suggestion = Pump(workspace.SuggestAxesAsync(tab.TabId, CancellationToken.None))!;
            using var cancellation = new CancellationTokenSource();
            var pending = workspace.ReadCalibrationLabelsAsync(suggestion, cancellation.Token);
            WaitUntil(() => ticks.Calls == 1);
            if (change == "cancel") cancellation.Cancel();
            else if (change == "dispose") workspace.Dispose();
            else workspace.Calibrate(tab.TabId, new(new(70, 280), new(70, 60), new(330, 280), 100, 20));
            ticks.Pending.SetResult(ticks.Result!);
            if (change == "cancel") Assert.Throws<OperationCanceledException>(() => Pump(pending));
            else Assert.IsNull(Pump(pending));
            Assert.AreEqual(0, reader.Calls);
        });

    [TestMethod]
    public void OversizedDerivedExtentFailsBeforeAnyWorkerAndKeepsManualProposal() =>
        WithFixture(2, false, (workspace, axis, ticks, reader, _) =>
        {
            axis.Frame = new(new(50, 350), new(200, 200), new(201, 198), .5);
            var suggestion = Pump(workspace.SuggestAxesAsync(workspace.CreateWorkspace().Single().TabId, CancellationToken.None))!;
            var error = Assert.ThrowsExactly<GraphReader.App.Integration.Workflow.ProductionWorkflowStageException>(() =>
                Pump(workspace.ReadCalibrationLabelsAsync(suggestion, CancellationToken.None)));
            Assert.AreEqual("CALIBRATION_RECTIFICATION_INVALID_FRAME", error.Failure.Code);
            Assert.IsTrue(workspace.IsAxisSuggestionCurrent(suggestion));
            Assert.AreEqual(0, ticks.Calls);
            Assert.AreEqual(0, reader.Calls);
        });

    [TestMethod]
    public void LanguageRefreshPreservesRawRowsCorrectionsSelectionAndReviewedValues() =>
        WithFixture(2, false, (workspace, _, ticks, reader, _) =>
        {
            var localization = new TestLocalization();
            using var vm = new MainWindowViewModel(workspace, localization);
            Pump(vm.SuggestCalibrationFromTicksAsync(CancellationToken.None));
            var row = vm.CalibrationLabelRows.Single(item => item.Proposal.RegionId == "y1");
            vm.SelectedCalibrationLabel = row;
            row.CorrectedText = "5.0";
            vm.RefitSelectedCalibrationLabels();
            vm.AxisSuggestionReviewed = true;
            string first = vm.SuggestedFirstSession, final = vm.SuggestedFinalSession, maximum = vm.SuggestedYMaximum;
            string display = row.DisplayText, paired = vm.CalibrationPairedTickRows[0], warning = vm.CalibrationLabelWarningText;
            var raw = row.Proposal;
            var original = CurrentLabels(vm).OriginalPolygon(raw);
            Assert.Contains(original.Average(point => point.X).ToString("0.##", CultureInfo.CurrentCulture), display);
            localization.ApplyCulture(CultureInfo.GetCultureInfo("fr-FR"));
            Assert.AreSame(row, vm.SelectedCalibrationLabel);
            Assert.AreSame(raw, row.Proposal);
            Assert.AreEqual("5.0", row.CorrectedText);
            Assert.IsTrue(row.IsIncluded);
            Assert.AreNotEqual(display, row.DisplayText);
            Assert.AreNotEqual(paired, vm.CalibrationPairedTickRows[0]);
            Assert.AreNotEqual(warning, vm.CalibrationLabelWarningText);
            Assert.AreEqual(first, vm.SuggestedFirstSession);
            Assert.AreEqual(final, vm.SuggestedFinalSession);
            Assert.AreEqual(maximum, vm.SuggestedYMaximum);
            Assert.IsTrue(vm.AxisSuggestionReviewed);
            Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.AreEqual(1, ticks.Calls);
            Assert.AreEqual(1, reader.Calls);
        });

    [TestMethod]
    public void LateDerivedOcrAfterTabSwitchCannotPublishRowsOrCalibration() =>
        WithFixture(2, false, (workspace, _, _, reader, _) =>
        {
            reader.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var vm = new MainWindowViewModel(workspace);
            Task pending = vm.SuggestCalibrationFromTicksAsync(CancellationToken.None);
            WaitUntil(() => reader.Calls == 1);
            vm.SelectedTab = null;
            reader.Pending.SetResult(reader.Response!);
            Pump(pending);
            Assert.IsFalse(vm.HasCalibrationLabelSuggestion);
            Assert.IsFalse(vm.HasCalibrationTickProposal);
            Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
            Assert.IsEmpty(vm.CalibrationLabelRows);
            Assert.IsNull(workspace.CreateWorkspace().Single().Calibration);
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PolygonEdgesMayReachOriginalImageBoundaryButNeverExtendBeyondIt(bool outside) =>
        WithFixture(2, false, (workspace, axis, _, reader, _) =>
        {
            // Rational shear preserves these edge points exactly; the derived white margin
            // also contains the outside control, so raw frame bounds cannot hide a mapping error.
            axis.Frame = new(new(80, 300), new(280, 300), new(272, 44), .7);
            var suggestion = Pump(workspace.SuggestAxesAsync(workspace.CreateWorkspace().Single().TabId, CancellationToken.None))!;
            var setup = Pump(workspace.ReadCalibrationLabelsAsync(suggestion, CancellationToken.None))!;
            CalibrationOcrPoint[] edges = [new(472, 180), new(outside ? 480.25 : 480, 180),
                new(outside ? 480.25 : 480, 188), new(472, 188)];
            reader.AdditionalRow = new("edge", edges.Select(setup.WorkingFrame!.ToDerived).ToArray(), "note", .5,
                null, null, false, "Other", "X", null);
            var result = Pump(workspace.ReadCalibrationLabelsAsync(suggestion, CancellationToken.None));
            Assert.IsTrue(CalibrationOcrWorkerClient.IsValidResponse(reader.Response!.Request!, reader.Response),
                "Both controls must reach the original-coordinate edge check with valid derived receipts.");
            if (outside) Assert.IsNull(result);
            else
            {
                Assert.IsNotNull(result);
                Assert.IsTrue(workspace.IsCalibrationLabelSuggestionCurrent(result));
                var mapped = result.OriginalPolygon(reader.AdditionalRow);
                Assert.AreEqual(480d, mapped[1].X, 1e-9);
                Assert.IsFalse(setup.WorkingFrame!.ContainsOriginal(new(480, 180)), "A region edge is not a valid anchor center.");
            }
        });

    private static void AssertProjection(WorkspaceCalibrationLabelSuggestion result, CalibrationOcrPoint derived, CalibrationOcrPoint original)
    {
        var roundtrip = result.WorkingFrame!.ToDerived(original);
        Assert.AreEqual(derived.X, roundtrip.X, 1e-9);
        Assert.AreEqual(derived.Y, roundtrip.Y, 1e-9);
    }

    private static WorkspaceCalibrationLabelSuggestion CurrentLabels(MainWindowViewModel vm) =>
        (WorkspaceCalibrationLabelSuggestion)typeof(MainWindowViewModel).GetField("_calibrationLabelSuggestion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
    private static Point[] Preview(WorkspaceTabViewModel tab) =>
        (Point[])typeof(ManualReviewOverlay).GetField("_axisProposal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab.OverlayContent)!;
    private static void AssertAnchors(CalibrationTickAnchors expected, Point[] actual)
    {
        CalibrationOcrPoint[] points = [expected.FirstSessionY0, expected.FirstSessionYMaximum, expected.FinalSessionY0];
        Assert.HasCount(3, actual);
        for (int i = 0; i < points.Length; i++)
        { Assert.AreEqual(points[i].X, actual[i].X, 1e-9); Assert.AreEqual(points[i].Y, actual[i].Y, 1e-9); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void WithFixture(double tilt, bool shear, Action<ManualPreviewWorkspaceService, AxisStub, TickStub, ReaderStub, string> test) => StaTestHost.Run(() =>
    {
        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        string directory = Path.Combine(Path.GetTempPath(), "rectified-calibration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "owned.png");
            var encoder = new PngBitmapEncoder();
            byte[] gray = Enumerable.Repeat((byte)255, 480 * 400).ToArray();
            gray[50 * 480 + 30] = 0;
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(480, 400, 96, 96, PixelFormats.Gray8, null, gray, 480)));
            using (var output = File.Create(path)) encoder.Save(output);
            double angle = tilt * Math.PI / 180;
            var axis = new AxisStub { Frame = new(new(90, 290), new(90 + 260 * Math.Cos(angle), 290 + 260 * Math.Sin(angle)),
                new(90 + 220 * Math.Sin(angle) + (shear ? 15 : 0), 290 - 220 * Math.Cos(angle)), .7) };
            var ticks = new TickStub(); var reader = new ReaderStub(ticks);
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: axis,
                calibrationOcrSuggestionService: reader, axisTickMeasurementService: ticks);
            Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
            test(workspace, axis, ticks, reader, path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private sealed class AxisStub : IAxisProposalService
    {
        internal RustAffineAxisGeometry Frame { get; set; } = null!;
        internal int Calls { get; private set; }
        internal byte[]? Image { get; private set; }
        public Task<RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token)
        {
            Calls++; Image = image;
            return Task.FromResult(new RustAxisProposalResult(null, null) { Provider = "opencv-review", AffineGeometry = Frame,
                ReviewReasons = ["native_affine_geometry", "native_multiple_ranked_candidates"] });
        }
    }
    private sealed class TickStub : IAxisTickMeasurementService
    {
        internal int Calls { get; private set; }
        internal byte[]? Image { get; private set; }
        internal int[] X { get; private set; } = [];
        internal int[] Y { get; private set; } = [];
        internal AxisTickMeasurementResult? Result { get; private set; }
        internal TaskCompletionSource<AxisTickMeasurementResult>? Pending { get; set; }
        public Task<AxisTickMeasurementResult> MeasureAsync(byte[] image, RustAxisGeometry box, CancellationToken token)
        {
            Calls++; Image = image;
            X = [box.Left + 20, box.Left + 100, box.Left + 180]; Y = [box.Top + 20, box.Top + 100, box.Top + 180];
            using var input = new MemoryStream(image);
            var bitmap = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Result = new(Hash(image), bitmap.PixelWidth, bitmap.PixelHeight,
                new(box.Left, box.Bottom, box.Right, box.Top, X, Y), null);
            return Pending?.Task ?? Task.FromResult(Result);
        }
    }
    private sealed class ReaderStub(TickStub ticks) : ICalibrationOcrSuggestionService
    {
        internal int Calls { get; private set; }
        internal byte[]? Image { get; private set; }
        internal CalibrationOcrResponse? Response { get; private set; }
        internal bool InvalidMiddleY { get; set; }
        internal double FirstX { get; set; } = 1;
        internal TaskCompletionSource<CalibrationOcrResponse>? Pending { get; set; }
        internal CalibrationOcrProposal? AdditionalRow { get; set; }
        public Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request, ReadOnlyMemory<byte> image, CancellationToken token)
        {
            Calls++;
            Assert.IsTrue(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(image, out ArraySegment<byte> segment));
            Image = segment.Array;
            Assert.AreEqual(Hash(Image!), request.PanelImageSha256);
            var proposals = new List<CalibrationOcrProposal>();
            for (int i = 0; i < 3; i++)
            {
                proposals.Add(Row("x" + i, FirstX + i, "X", ticks.X[i], request.Plot.Bottom + 15));
                var y = Row("y" + i, (2 - i) * 5, "Y", request.Plot.Left - 20, ticks.Y[i]);
                if (InvalidMiddleY && i == 1) y = y with { Text = "S", NumericValue = null, NumericLiteral = false,
                    NormalizedText = null, Confidence = 0, FailureCode = CalibrationOcrReasonCodes.NonnumericReading };
                proposals.Add(y);
            }
            if (AdditionalRow is not null) proposals.Add(AdditionalRow);
            var sequence = new CalibrationOcrSequenceCheck(false, 3, 3, 0, []);
            Response = CalibrationOcrClientTests.Success(request) with { Proposals = proposals.AsReadOnly(), XSequence = sequence, YSequence = sequence };
            return Pending?.Task ?? Task.FromResult(Response);
        }
        private static CalibrationOcrProposal Row(string id, double value, string axis, double x, double y) =>
            new(id, [new(x - 4, y - 4), new(x + 4, y - 4), new(x + 4, y + 4), new(x - 4, y + 4)],
                value.ToString("R", CultureInfo.InvariantCulture), .9, value, value.ToString("R", CultureInfo.InvariantCulture),
                true, axis + "Tick", axis, null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    // Distinguishable test translations exercise culture events, not a claim of shipped French copy.
    private sealed class TestLocalization : ILocalizationService
    {
        private readonly ResourceDictionary english = new() { Source = new Uri(LocalizationService.EnglishSource, UriKind.Relative) };
        public CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
        public IReadOnlyList<CultureInfo> AvailableCultures { get; } = [CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("fr-FR")];
        public event EventHandler<LocalizationChangedEventArgs>? CultureChanged;
        public string GetString(string key) => CurrentCulture.Name + ": " + english[key];
        public void ApplyCulture(CultureInfo culture)
        { CurrentCulture = culture; CultureChanged?.Invoke(this, new(culture)); }
    }
    private static void WaitUntil(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), "Owned asynchronous stage did not start.");
            Pump(Task.Delay(1));
        }
    }
    private static void Pump(Task task) => Pump(Complete(task));
    private static async Task<bool> Complete(Task task) { await task; return true; }
    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }
}
