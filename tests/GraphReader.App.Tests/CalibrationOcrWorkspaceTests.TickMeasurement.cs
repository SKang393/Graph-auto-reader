// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

public sealed partial class CalibrationOcrWorkspaceTests
{
    [TestMethod]
    public void NativeTickEvidenceUsesOriginalImageAndKeepsSuggestionWhileRefitEvidenceIsImmutable() => WithTickImage(path =>
    {
        int[] x = [20, 60, 100], y = [10, 50, 100];
        var scan = new TickScanStub((image, box) => Measurement(image, box, x, y));
        var ocr = new FakeOcr();
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new NativeTickAxes(),
            calibrationOcrSuggestionService: ocr, axisTickMeasurementService: scan);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        var tab = workspace.CreateWorkspace().Single();
        WorkspaceAxisSuggestion suggestion = Pump(workspace.SuggestAxesAsync(tab.TabId, CancellationToken.None))!;
        Assert.IsNotNull(suggestion.Geometry);
        Assert.IsNull(suggestion.AffineGeometry);
        Task<WorkspaceCalibrationLabelSuggestion?> pending = workspace.ReadCalibrationLabelsAsync(suggestion, CancellationToken.None);
        Assert.AreEqual(1, scan.Calls);
        CollectionAssert.AreEqual(File.ReadAllBytes(path), scan.Image!);
        Assert.AreSame(suggestion.Geometry, scan.Box);
        Assert.IsNotNull(ocr.Request);
        CollectionAssert.AreEqual(ExpectedXTicks, ocr.Request.MeasuredXTicks!);
        CollectionAssert.AreEqual(ExpectedYTicks, ocr.Request.MeasuredYTicks!);
        x[0] = 21; y[0] = 11;
        ocr.Pending.SetResult(CalibrationOcrClientTests.Success(ocr.Request));
        WorkspaceCalibrationLabelSuggestion result = Pump(pending)!;
        Assert.IsNotNull(result);
        Assert.AreSame(suggestion, result.AxisSuggestion);
        Assert.AreEqual("opencv-review", result.AxisSuggestion.Provider);
        CollectionAssert.Contains(result.AxisSuggestion.ReviewReasons.ToArray(), "native_multiple_ranked_candidates");
        Assert.IsEmpty(suggestion.Geometry.XTickPositions);
        Assert.IsEmpty(suggestion.Geometry.YTickPositions);
        Assert.IsTrue(result.TickMeasurementAttempted);
        Assert.IsNull(result.TickMeasurementErrorCode);
        CollectionAssert.AreEqual(ExpectedXTicks, result.FitGeometry.XTickPositions.ToArray());
        CollectionAssert.AreEqual(ExpectedYTicks, result.FitGeometry.YTickPositions.ToArray());
        Assert.IsTrue(((IList<int>)result.FitGeometry.XTickPositions).IsReadOnly);
        Assert.IsTrue(workspace.IsCalibrationLabelSuggestionCurrent(result));
        Assert.IsFalse(workspace.IsCalibrationLabelSuggestionCurrent(result with { FitGeometry = result.FitGeometry with { Left = 9 } }));
        Assert.IsFalse(workspace.IsCalibrationLabelSuggestionCurrent(result with
            { AxisSuggestion = suggestion with { Geometry = suggestion.Geometry with { Left = 9 } } }));
        ocr.Request.MeasuredXTicks![0] = 21;
        Assert.IsFalse(workspace.IsCalibrationLabelSuggestionCurrent(result));
    });

    [TestMethod]
    [DataRow("WORKER_START_FAILED")]
    [DataRow("wrong_image")]
    [DataRow("wrong_box")]
    [DataRow("invalid_ticks")]
    public void InvalidNativeTickEvidenceLeavesOriginalGeometryAndContinuesOcr(string failure) => WithTickImage(path =>
    {
        var scan = new TickScanStub((image, box) =>
        {
            AxisTickMeasurementResult valid = Measurement(image, box, ExpectedXTicks, ExpectedYTicks);
            return failure switch
            {
                "wrong_image" => valid with { PanelImageSha256 = new string('f', 64) },
                "wrong_box" => valid with { Evidence = valid.Evidence! with { Left = box.Left + 1 } },
                "invalid_ticks" => valid with { Evidence = valid.Evidence! with { XTickPositions = [20, 21, 100] } },
                _ => valid with { Evidence = null, ErrorCode = failure },
            };
        });
        var ocr = new FakeOcr();
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new NativeTickAxes(),
            calibrationOcrSuggestionService: ocr, axisTickMeasurementService: scan);
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        WorkspaceAxisSuggestion suggestion = Pump(workspace.SuggestAxesAsync(workspace.CreateWorkspace().Single().TabId, CancellationToken.None))!;
        Assert.IsNotNull(suggestion.Geometry);
        Assert.IsNull(suggestion.AffineGeometry);
        Task<WorkspaceCalibrationLabelSuggestion?> pending = workspace.ReadCalibrationLabelsAsync(suggestion, CancellationToken.None);
        Assert.IsNotNull(ocr.Request);
        Assert.IsNull(ocr.Request.MeasuredXTicks);
        Assert.IsNull(ocr.Request.MeasuredYTicks);
        ocr.Pending.SetResult(CalibrationOcrClientTests.Success(ocr.Request));
        WorkspaceCalibrationLabelSuggestion result = Pump(pending)!;
        Assert.AreSame(suggestion, result.AxisSuggestion);
        Assert.AreSame(suggestion.Geometry, result.FitGeometry);
        Assert.AreEqual(failure == "WORKER_START_FAILED" ? failure : "INVALID_TICK_EVIDENCE", result.TickMeasurementErrorCode);
        Assert.IsTrue(workspace.IsCalibrationLabelSuggestionCurrent(result));
        Assert.AreEqual(1, scan.Calls);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StaleOrCancelledTickScanCannotStartOcr(bool cancel) => WithTickImage(path =>
    {
        var scan = new TickScanStub((image, box) => Measurement(image, box, ExpectedXTicks, ExpectedYTicks))
            { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var ocr = new FakeOcr();
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new NativeTickAxes(),
            calibrationOcrSuggestionService: ocr, axisTickMeasurementService: scan);
        using var cancellation = new CancellationTokenSource();
        Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
        var tab = workspace.CreateWorkspace().Single();
        WorkspaceAxisSuggestion suggestion = Pump(workspace.SuggestAxesAsync(tab.TabId, CancellationToken.None))!;
        Assert.IsNotNull(suggestion.Geometry);
        Assert.IsNull(suggestion.AffineGeometry);
        Task<WorkspaceCalibrationLabelSuggestion?> pending = workspace.ReadCalibrationLabelsAsync(suggestion, cancellation.Token);
        Assert.IsNull(ocr.Request);
        if (cancel) cancellation.Cancel();
        else workspace.Calibrate(tab.TabId, new(new(10, 100), new(10, 10), new(150, 100), 100, 20));
        scan.Pending.SetResult(Measurement(scan.Image!, scan.Box!, ExpectedXTicks, ExpectedYTicks));
        if (cancel) Assert.Throws<OperationCanceledException>(() => Pump(pending));
        else Assert.IsNull(Pump(pending));
        Assert.IsNull(ocr.Request);
        Assert.IsEmpty(suggestion.Geometry.XTickPositions);
    });

    private static AxisTickMeasurementResult Measurement(byte[] image, RustAxisGeometry box,
        IReadOnlyList<int> x, IReadOnlyList<int> y) => new(Convert.ToHexStringLower(SHA256.HashData(image)), 160, 120,
            new(box.Left, box.Bottom, box.Right, box.Top, x, y), null);

    private sealed class NativeTickAxes : IAxisProposalService
    {
        public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken) =>
            Task.FromResult(new RustAxisProposalResult(new(8, 110, 155, 5, .8), null)
                { Provider = "opencv-review", ReviewReasons = ["native_multiple_ranked_candidates"] });
    }

    private sealed class TickScanStub(Func<byte[], RustAxisGeometry, AxisTickMeasurementResult> measure) : IAxisTickMeasurementService
    {
        public int Calls { get; private set; }
        public byte[]? Image { get; private set; }
        public RustAxisGeometry? Box { get; private set; }
        public TaskCompletionSource<AxisTickMeasurementResult>? Pending { get; init; }
        public Task<AxisTickMeasurementResult> MeasureAsync(byte[] originalImage, RustAxisGeometry geometry, CancellationToken cancellationToken)
        {
            Calls++; Image = originalImage; Box = geometry;
            return Pending?.Task ?? Task.FromResult(measure(originalImage, geometry));
        }
    }

    private static void WithTickImage(Action<string> action) => StaTestHost.Run(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "tick-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "owned.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 120, 96, 96, PixelFormats.Gray8, null,
                Enumerable.Repeat((byte)255, 160 * 120).ToArray(), 160)));
            using (var stream = File.Create(path)) encoder.Save(stream);
            action(path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    });
}
