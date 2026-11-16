// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

public sealed partial class RectifiedCalibrationTests
{
    [TestMethod]
    [DataRow(-2d, false)]
    [DataRow(2d, true)]
    public void AffineReviewPassesOriginalPixelsAndExactForwardTransformWithBoundReceipt(double tilt, bool shear) =>
        WithFixture(tilt, shear, (workspace, axis, ticks, reader, path) =>
        {
            byte[] original = File.ReadAllBytes(path);
            var suggestion = Pump(workspace.SuggestAxesAsync(workspace.CreateWorkspace().Single().TabId, CancellationToken.None))!;
            var result = Pump(workspace.ReadCalibrationLabelsAsync(suggestion, CancellationToken.None));
            Assert.IsNotNull(result);
            Assert.IsNotNull(ticks.OriginalSupport);
            RustAffineTickSource source = ticks.OriginalSupport;
            Assert.AreEqual(480, source.Width);
            Assert.AreEqual(400, source.Height);
            byte[] expected = Enumerable.Repeat((byte)255, 480 * 400).ToArray();
            expected[50 * 480 + 30] = 0;
            CollectionAssert.AreEqual(expected, source.Pixels.ToArray());
            var matrix = result.WorkingFrame!.TransformChain.OriginalToDerived;
            Assert.AreEqual(matrix.M11, source.M11);
            Assert.AreEqual(matrix.M12, source.M12);
            Assert.AreEqual(matrix.M13, source.M13);
            Assert.AreEqual(matrix.M21, source.M21);
            Assert.AreEqual(matrix.M22, source.M22);
            Assert.AreEqual(matrix.M23, source.M23);
            Assert.AreEqual(result.FitGeometry.Left, source.M11 * axis.Frame.Origin.X + source.M12 * axis.Frame.Origin.Y + source.M13, 1e-9);
            Assert.AreEqual(result.FitGeometry.Bottom, source.M21 * axis.Frame.Origin.X + source.M22 * axis.Frame.Origin.Y + source.M23, 1e-9);
            Assert.AreEqual(result.WorkingFrame.OriginalSupportBinding, result.TickMeasurementReceipt!.OriginalSupportBinding);
            Assert.AreEqual(1, ticks.Calls);
            Assert.AreEqual(1, ticks.ManualPartialCalls);
            Assert.AreSame(ticks.Image, reader.Image);
            Assert.IsTrue(workspace.IsCalibrationLabelSuggestionCurrent(result));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            Assert.IsNull(workspace.CreateWorkspace().Single().Calibration);
        });

    [TestMethod]
    public void WrongOriginalSupportBindingCannotSupplyMeasuredTicks() =>
        WithFixture(2, false, (workspace, _, ticks, reader, _) =>
        {
            ticks.EvidenceFault = "support";
            var suggestion = Pump(workspace.SuggestAxesAsync(workspace.CreateWorkspace().Single().TabId, CancellationToken.None))!;
            var result = Pump(workspace.ReadCalibrationLabelsAsync(suggestion, CancellationToken.None));
            Assert.IsNotNull(result);
            Assert.AreEqual("INVALID_TICK_EVIDENCE", result.TickMeasurementErrorCode);
            Assert.HasCount(0, result.FitGeometry.XTickPositions);
            Assert.HasCount(0, result.FitGeometry.YTickPositions);
            Assert.IsNull(reader.LastRequest!.MeasuredXTicks);
            Assert.IsNull(reader.LastRequest.MeasuredYTicks);
            Assert.IsNull(workspace.CreateWorkspace().Single().Calibration);
        });

    [TestMethod]
    public void LegacyTickAdapterDoesNotGainOriginalSupportImplicitly()
    {
        var adapter = new StrictOnlyTickAdapter();
        IAxisTickMeasurementService service = adapter;
        var source = new RustAffineTickSource(32, 32, new byte[32 * 32], 1, 0, 0, 0, 1, 0);
        AxisTickMeasurementResult result = service.MeasureAffineManualPartialAsync([1],
            new RustAxisGeometry(1, 9, 9, 1, 0), source, CancellationToken.None).GetAwaiter().GetResult();
        Assert.AreEqual(1, adapter.Calls);
        Assert.AreEqual("STRICT_ONLY", result.ErrorCode);
        Assert.IsNull(result.OriginalSupportBinding);
    }
}
