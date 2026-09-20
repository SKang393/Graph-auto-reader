// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class SessionLatticeRobustnessTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OneStrayColumnDoesNotInvalidateAConsistentScaleOrAcquireAnInventedSession(bool reverse)
    {
        double[] pixels = Enumerable.Range(0, 21).Select(static index => 100d + (25d * index)).Append(338.5).ToArray();
        SessionLatticeRequest request = Request(reverse ? pixels.Reverse().ToArray() : pixels);
        SessionLatticeResult result = SessionLattice.Fit(request);

        Assert.AreEqual(CalibrationValidity.Valid, result.Validity);
        Assert.AreEqual(100d, result.Session1PixelX);
        Assert.AreEqual(25d, result.PitchPixels);
        Assert.HasCount(22, result.Assignments);
        SessionXEvidence stray = result.Assignments.Single(static point => point.PixelX == 338.5);
        Assert.IsNull(stray.PrintedX);
        Assert.IsNull(stray.EstimatedX);
        Assert.IsTrue(result.Diagnostics.Warnings.Any(static warning => warning.Contains("unknown x", StringComparison.Ordinal)));
        foreach (SessionXEvidence point in result.Assignments.Where(static point => point.PixelX != 338.5))
            Assert.AreEqual(1d + ((point.PixelX - 100d) / 25d), point.PrintedX ?? point.EstimatedX);
    }

    [TestMethod]
    public void OneNearDuplicateDoesNotChangeThePrintedScale()
    {
        double[] pixels = Enumerable.Range(0, 21).Select(static index => 100d + (25d * index)).Append(352.5).ToArray();
        SessionLatticeResult result = SessionLattice.Fit(Request(pixels));
        Assert.AreEqual(CalibrationValidity.Valid, result.Validity);
        Assert.AreEqual(25d, result.PitchPixels);
        Assert.HasCount(22, result.Assignments, "Calibration must not silently delete a detection.");
    }

    [TestMethod]
    public void ACoherentConflictingMinorityStillRequiresReview()
    {
        double[] pixels = Enumerable.Range(0, 21).Select(static index => 100d + (25d * index))
            .Concat([630d, 660d, 690d]).ToArray();
        SessionLatticeResult result = SessionLattice.Fit(Request(pixels));
        Assert.AreEqual(CalibrationValidity.NeedsReview, result.Validity);
        Assert.IsTrue(result.Reasons.Any(static reason => reason.Contains("pitch evidence disagree", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(12.5, false)]
    [DataRow(50.0, false)]
    [DataRow(50.0, true)]
    public void ARegularHalfOrDoublePitchCannotBeHiddenByAStrayColumn(double gap, bool includeStray)
    {
        double[] pixels = Enumerable.Range(0, 9).Select(index => 100d + (gap * index)).ToArray();
        if (includeStray) pixels = [.. pixels, pixels[^1] + 7d];
        SessionLatticeResult result = SessionLattice.Fit(Request(pixels));
        Assert.AreEqual(CalibrationValidity.NeedsReview, result.Validity);
        Assert.IsTrue(result.Uncertainty.HarmonicAmbiguity);
    }

    [TestMethod]
    public void MissingSessionsRemainAllowedWithoutACompetingDenseGrid()
    {
        SessionLatticeResult result = SessionLattice.Fit(Request([100, 125, 175, 250, 350, 375, 425, 550, 600]));
        Assert.AreEqual(CalibrationValidity.Valid, result.Validity);
        Assert.AreEqual(25d, result.PitchPixels);
        Assert.IsFalse(result.Uncertainty.HarmonicAmbiguity);
        Assert.AreEqual(7d, result.Assignments.Single(static point => point.PixelX == 250).EstimatedX);
    }

    [TestMethod]
    public void AShortGapTieIsInsufficientToDiscardConflictingEvidence()
    {
        SessionLatticeResult result = SessionLattice.Fit(Request([100, 125, 155]));
        Assert.AreEqual(CalibrationValidity.NeedsReview, result.Validity);
        Assert.IsTrue(result.Reasons.Any(static reason => reason.Contains("pitch evidence disagree", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RobustGapEvidenceDoesNotWaiveTheFirstSessionRequirement()
    {
        double[] pixels = Enumerable.Range(1, 20).Select(static index => 100d + (25d * index)).Append(338.5).ToArray();
        SessionLatticeResult result = SessionLattice.Fit(Request(pixels));
        Assert.AreEqual(CalibrationValidity.InvalidSessionOrigin, result.Validity);
        Assert.IsTrue(result.Reasons.Any(static reason => reason.Contains("session 2, not session 1", StringComparison.Ordinal)));
    }

    private static SessionLatticeRequest Request(double[] pixels) => new()
    {
        PrintedTicks = [new("first", 100, 1), new("middle", 350, 11), new("last", 600, 21)],
        MarkerColumns = pixels.Select(static pixel => new MarkerColumnEvidence(pixel)).ToArray(),
    };
}
