// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class CalibrationInlierRoutingTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RejectedPrintedLabelCannotMoveTheOriginOrReappearInAssignments(bool reverse)
    {
        PrintedXTickEvidence[] ticks = XTicks();
        var request = new SessionLatticeRequest
        {
            PrintedTicks = reverse ? ticks.Reverse().ToArray() : ticks,
            MarkerColumns = Enumerable.Range(0, 21).Select(static i => new MarkerColumnEvidence(100 + (25 * i))).ToArray(),
        };
        SessionLatticeResult result = SessionLattice.Fit(request);

        Assert.AreEqual(CalibrationValidity.Valid, result.Validity);
        Assert.AreEqual(100d, result.Session1PixelX);
        Assert.AreEqual(25d, result.PitchPixels);
        Assert.AreEqual(6, result.Diagnostics.PrintedTickCount, "Input diagnostics retain the rejected observation count.");
        Assert.IsTrue(result.Diagnostics.Warnings.Any(static warning => warning.Contains("Rejected 1", StringComparison.Ordinal)));
        SessionXEvidence point = result.Assignments.Single(static item => item.PixelX == 250);
        Assert.IsNull(point.PrintedX, "The rejected 100 is not printed evidence for this point.");
        Assert.AreEqual(7d, point.EstimatedX);
        Assert.IsFalse(result.Assignments.Any(static item => item.PrintedX == 100));
    }

    [TestMethod]
    public void AutomaticAnchorsUseAcceptedMaximaAndRetainOriginalRejectionEvidence()
    {
        SessionFirstCalibrationResult result = RobustCalibration.FitSessionFirst(Request());

        Assert.AreEqual(CalibrationValidity.Valid, result.Validity);
        Assert.IsNotNull(result.XTransform);
        CollectionAssert.Contains(result.XTransform.Diagnostics.OutlierIds.ToArray(), "x-ocr-100");
        CollectionAssert.Contains(result.YTransform.Diagnostics.OutlierIds.ToArray(), "y-ocr-600");
        Assert.AreEqual(5, result.Lattice.Diagnostics.PrintedTickCount);
        CalibrationAnchor xMax = result.Anchors.Single(static a => a.Kind == CalibrationAnchorKind.SessionMaximumY0);
        CalibrationAnchor yMax = result.Anchors.Single(static a => a.Kind == CalibrationAnchorKind.Session1YMaximum);
        Assert.AreEqual(21d, xMax.GraphX);
        Assert.AreEqual(600d, xMax.Screen.X, 1e-9);
        Assert.AreEqual(100d, yMax.GraphY);
        Assert.AreEqual(50d, yMax.Screen.Y, 1e-9);
    }

    [TestMethod]
    public void ExplicitCallerMaximaRemainExplicitAuthority()
    {
        SessionFirstCalibrationResult result = RobustCalibration.FitSessionFirst(Request() with { XMaximum = 25, YMaximum = 120 });
        Assert.AreEqual(CalibrationValidity.Valid, result.Validity);
        Assert.AreEqual(25d, result.Anchors.Single(static a => a.Kind == CalibrationAnchorKind.SessionMaximumY0).GraphX);
        Assert.AreEqual(120d, result.Anchors.Single(static a => a.Kind == CalibrationAnchorKind.Session1YMaximum).GraphY);
    }

    [TestMethod]
    public void CompetingPrintedFitsCannotBecomeValidByDiscardingOneSide()
    {
        var request = new SessionLatticeRequest
        {
            PrintedTicks = [new("a1", 100, 1), new("a2", 200, 5), new("b1", 150, 1), new("b2", 250, 5)],
            MarkerColumns = [new(100), new(125), new(150), new(175), new(200)],
        };
        SessionLatticeResult result = SessionLattice.Fit(request);
        Assert.AreNotEqual(CalibrationValidity.Valid, result.Validity);
        Assert.IsTrue(result.Reasons.Any(static reason => reason.Contains("ambiguous", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RemovingRejectedLabelsDoesNotOverrideIndependentOriginEvidence()
    {
        SessionFirstCalibrationRequest request = Request();
        SessionFirstCalibrationResult result = RobustCalibration.FitSessionFirst(request with
        {
            Lattice = request.Lattice with { ExpectedSession1PixelX = 150 },
        });
        Assert.AreEqual(CalibrationValidity.InvalidSessionOrigin, result.Validity);
    }

    private static PrintedXTickEvidence[] XTicks() =>
    [
        new("x1", 100, 1), new("x6", 225, 6), new("x11", 350, 11),
        new("x16", 475, 16), new("x21", 600, 21), new("x-ocr-100", 250, 100, 0.95),
    ];

    private static SessionFirstCalibrationRequest Request() => new()
    {
        PrintedXTicks = XTicks(),
        YTicks = [new("y20", 250, 20), new("y40", 200, 40), new("y60", 150, 60),
            new("y80", 100, 80), new("y100", 50, 100), new("y-ocr-600", 120, 600, 0.95)],
        Lattice = new SessionLatticeRequest
        {
            MarkerColumns = Enumerable.Range(0, 21).Select(static i => new MarkerColumnEvidence(100 + (25 * i))).ToArray(),
        },
    };
}
