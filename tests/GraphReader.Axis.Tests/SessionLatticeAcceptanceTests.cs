// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class SessionLatticeAcceptanceTests
{
    private static readonly double[] ExpectedSparseProbeSessions = [3d, 8d, 13d];
    private static readonly double[] ExpectedSharedPanelSessions = [4d, 5d, 7d, 9d];
    private static readonly SessionLatticeSource[] ExpectedManualMixedSources =
    [
        SessionLatticeSource.Manual,
        SessionLatticeSource.SharedPanel,
        SessionLatticeSource.MarkerLattice,
    ];

    [TestMethod]
    [DataRow(3000000000d)]
    [DataRow(-3000000000d)]
    public void OutOfRangeFirstSessionReturnsInsufficientEvidenceWithoutInventingValues(double printedValue)
    {
        var request = new SessionLatticeRequest
        {
            PrintedTicks = [new("extreme-ocr-tick", 100, printedValue)],
            MarkerColumns = Columns(100, 125, 150),
        };

        SessionLatticeResult result = SessionLattice.Fit(request);

        Assert.AreEqual(CalibrationValidity.InsufficientEvidence, result.Validity);
        Assert.IsFalse(result.HasAbsoluteSessionOrigin);
        Assert.IsTrue(result.Assignments.All(point => point.PrintedX is null && point.EstimatedX is null));
        Assert.IsTrue(result.Reasons.Any(reason => reason.Contains("range", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void PitchAtIntegerQuantizationBoundaryReturnsInsufficientEvidence()
    {
        var request = new SessionLatticeRequest
        {
            SharedPanels = [new("extreme-pitch", 100, (double)long.MaxValue / 1000d)],
        };

        SessionLatticeResult result = SessionLattice.Fit(request);

        Assert.AreEqual(CalibrationValidity.InsufficientEvidence, result.Validity);
        Assert.IsFalse(result.HasAbsoluteSessionOrigin);
    }

    [TestMethod]
    public void MissingLabelsRecoverHeldOutPitchWithinTwoPercentWithoutInventingExactX()
    {
        var request = new SessionLatticeRequest
        {
            MarkerColumns = Columns(100, 125, 175, 250, 150, 275, 400),
            ConnectedSequences =
            [
                new("main", [100, 125, 175, 250]),
                new("probes", [150, 275, 400], 0.9),
            ],
        };

        SessionLatticeResult result = SessionLattice.Fit(request);

        Assert.IsNotNull(result.PitchPixels);
        Assert.AreEqual(25, result.PitchPixels.Value, 0.5, "Held-out pitch must be within 2 percent.");
        Assert.IsTrue(result.IsOrdinalOnly);
        Assert.IsFalse(result.HasAbsoluteSessionOrigin);
        Assert.IsTrue(result.Assignments.All(point => point.PrintedX is null));
        Assert.IsTrue(result.Assignments.All(point => point.EstimatedX is null));
        Assert.IsTrue(result.Assignments.All(point => point.EvidenceKind == SessionXEvidenceKind.OrdinalOnly));
    }

    [TestMethod]
    public void UnlabeledAxisTicksAloneRecoverPitchWithoutInventingExactX()
    {
        var request = new SessionLatticeRequest
        {
            UnlabeledTicks =
            [
                new("tick-1", 100),
                new("tick-2", 125),
                new("tick-4", 175),
                new("tick-7", 250),
            ],
        };

        SessionLatticeResult result = SessionLattice.Fit(request);

        Assert.AreEqual(SessionLatticeSource.AxisTicks, result.Source);
        Assert.IsNotNull(result.PitchPixels);
        Assert.AreEqual(25, result.PitchPixels.Value, 0.5);
        Assert.IsTrue(result.IsOrdinalOnly);
        Assert.IsFalse(result.HasAbsoluteSessionOrigin);
        Assert.AreEqual(4, result.Diagnostics.UnlabeledTickCount);
        Assert.IsTrue(result.Assignments.All(point => point.PrintedX is null));
        Assert.IsTrue(result.Assignments.All(point => point.EstimatedX is null));
        Assert.IsTrue(result.Assignments.All(point => point.EvidenceKind == SessionXEvidenceKind.OrdinalOnly));
    }

    [TestMethod]
    public void SharedPanelResolvesSparseMultiProbeGapsWithoutHarmonicAlias()
    {
        var request = new SessionLatticeRequest
        {
            MarkerColumns = Columns(150, 275, 400),
            SharedPanels = [new("panel-a", 100, 25)],
            RequireFirstObservedSessionOne = false,
        };

        SessionLatticeResult result = SessionLattice.Fit(request);

        Assert.AreEqual(CalibrationValidity.Valid, result.Validity);
        Assert.AreEqual(SessionLatticeSource.Mixed, result.Source);
        Assert.AreEqual(100, result.Session1PixelX);
        Assert.AreEqual(25, result.PitchPixels);
        CollectionAssert.AreEqual(
            ExpectedSparseProbeSessions,
            result.Assignments.Select(point => point.EstimatedX!.Value).ToArray());
        Assert.IsFalse(result.Uncertainty.HarmonicAmbiguity);
    }

    [TestMethod]
    public void IrregularPhaseBlankGapsRetainTruePitch()
    {
        var request = new SessionLatticeRequest
        {
            MarkerColumns = Columns(100, 125, 150, 250, 275, 400, 425),
            ConnectedSequences =
            [
                new("baseline", [100, 125, 150]),
                new("intervention", [250, 275]),
                new("maintenance", [400, 425]),
            ],
        };

        SessionLatticeResult result = SessionLattice.Fit(request);

        Assert.IsNotNull(result.PitchPixels);
        Assert.AreEqual(25, result.PitchPixels.Value, 0.5);
        Assert.IsTrue(result.IsOrdinalOnly);
    }

    [TestMethod]
    public void SharedPanelWithDifferentFirstObservedColumnPreservesSessionOneOrigin()
    {
        var request = new SessionLatticeRequest
        {
            MarkerColumns = Columns(175, 200, 250, 300),
            SharedPanels = [new("aligned-panel", 100, 25, 0.98)],
            RequireFirstObservedSessionOne = false,
        };

        SessionLatticeResult result = SessionLattice.Fit(request);

        Assert.AreEqual(100, result.Session1PixelX);
        Assert.AreEqual(25, result.PitchPixels);
        Assert.IsTrue(result.HasAbsoluteSessionOrigin);
        CollectionAssert.AreEqual(
            ExpectedSharedPanelSessions,
            result.Assignments.Select(point => point.EstimatedX!.Value).ToArray());

        SessionLatticeResult profileDefault = SessionLattice.Fit(request with
        {
            RequireFirstObservedSessionOne = true,
        });
        Assert.AreEqual(CalibrationValidity.InvalidSessionOrigin, profileDefault.Validity);
    }

    [TestMethod]
    public void ConflictingSharedPanelPitchesRequireReviewWithExplicitReason()
    {
        var request = new SessionLatticeRequest
        {
            MarkerColumns = Columns(100, 125, 150, 175, 200),
            SharedPanels =
            [
                new("panel-25", 100, 25),
                new("panel-50", 100, 50),
            ],
        };

        SessionLatticeResult result = SessionLattice.Fit(request);

        Assert.AreEqual(CalibrationValidity.NeedsReview, result.Validity);
        Assert.IsTrue(result.Reasons.Any(reason =>
            reason.Contains("shared", StringComparison.OrdinalIgnoreCase) &&
            reason.Contains("pitch", StringComparison.OrdinalIgnoreCase) &&
            (reason.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
             reason.Contains("disagree", StringComparison.OrdinalIgnoreCase))));
    }

    [TestMethod]
    public void StaggeredStartReturnsInvalidSessionOriginUntilExplicitOverride()
    {
        var request = new SessionLatticeRequest
        {
            MarkerColumns = Columns(150, 175, 200, 225),
            SharedPanels = [new("aligned-panel", 100, 25)],
        };

        SessionLatticeResult rejected = SessionLattice.Fit(request);

        Assert.AreEqual(CalibrationValidity.InvalidSessionOrigin, rejected.Validity);
        Assert.AreEqual(CalibrationStatusCodes.InvalidSessionOrigin, rejected.StatusCode);
        Assert.IsTrue(rejected.Reasons.Count > 0);

        SessionLatticeResult overridden = SessionLattice.Fit(request with
        {
            OriginOverride = new SessionOriginOverride(150),
        });
        Assert.AreEqual(CalibrationValidity.Valid, overridden.Validity);
        Assert.AreEqual(SessionLatticeSource.Mixed, overridden.Source);
        CollectionAssert.IsSubsetOf(
            ExpectedManualMixedSources,
            overridden.ContributingSources.ToArray());
        Assert.AreEqual(150, overridden.Session1PixelX);
        Assert.IsTrue(overridden.Diagnostics.Warnings.Any(
            warning => warning.Contains("override", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ManualOriginWithIndependentPitchEvidencePreservesAllProvenance()
    {
        var manualOverride = new SessionOriginOverride(
            100,
            CoordinateSpace: AxisGeometryCoordinateSpaces.OriginalPixels,
            ProvenanceId: "review-393",
            Reason: "Reviewer confirmed the first session column.",
            ConfirmedAtUtc: new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
        var request = new SessionLatticeRequest
        {
            MarkerColumns = Columns(100, 125, 150, 175),
            SharedPanels = [new("shared", 100, 25)],
            OriginOverride = manualOverride,
        };

        SessionLatticeResult result = SessionLattice.Fit(request);

        Assert.AreEqual(CalibrationValidity.Valid, result.Validity);
        Assert.AreEqual(SessionLatticeSource.Mixed, result.Source);
        Assert.IsTrue(result.UsedManualOriginOverride);
        Assert.AreEqual(manualOverride, result.ManualOriginOverride);
        CollectionAssert.IsSubsetOf(
            ExpectedManualMixedSources,
            result.ContributingSources.ToArray());
    }

    [TestMethod]
    public void PrintedTickMaximumIsLastSessionColumnRatherThanPlotBorder()
    {
        var request = new SessionLatticeRequest
        {
            PrintedTicks =
            [
                new("x1", 100, 1),
                new("x11", 350, 11),
                new("x21", 600, 21),
            ],
            MarkerColumns = Columns(100, 350, 600),
        };

        SessionLatticeResult result = SessionLattice.Fit(request);

        SessionXEvidence last = result.Assignments.OrderBy(point => point.PrintedX).Last();
        Assert.AreEqual(21, last.PrintedX);
        Assert.AreEqual(600, last.PixelX, 0.01);
        Assert.AreNotEqual(700, last.PixelX, "The plot border is not the final printed session column.");
    }

    [TestMethod]
    public void ContradictoryPrintedTicksAndMarkerSpacingRequireReview()
    {
        var request = new SessionLatticeRequest
        {
            PrintedTicks =
            [
                new("x1", 100, 1),
                new("x5", 200, 5),
                new("x9", 300, 9),
            ],
            MarkerColumns = Columns(100, 130, 160, 190, 220, 250, 280),
        };

        SessionLatticeResult result = SessionLattice.Fit(request);

        Assert.AreEqual(CalibrationValidity.NeedsReview, result.Validity);
        Assert.IsTrue(result.Reasons.Any(reason =>
            reason.Contains("disagree", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("conflict", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void SessionLatticeHonorsCancellationAndRejectsDegenerateEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            SessionLattice.Fit(new SessionLatticeRequest(), cancellation.Token));

        SessionLatticeResult duplicateOnly = SessionLattice.Fit(new SessionLatticeRequest
        {
            MarkerColumns = Columns(100, 100, 100),
        });
        Assert.AreEqual(CalibrationValidity.InsufficientEvidence, duplicateOnly.Validity);
        Assert.IsNull(duplicateOnly.PitchPixels);

        Assert.Throws<ArgumentException>(() => SessionLattice.Fit(new SessionLatticeRequest
        {
            SharedPanels = [new("bad", 100, 0)],
        }));
        Assert.Throws<ArgumentException>(() => SessionLattice.Fit(new SessionLatticeRequest
        {
            SharedPanels = [new("bad", 100, -25)],
        }));
        Assert.ThrowsExactly<ArgumentException>(() => SessionLattice.Fit(new SessionLatticeRequest
        {
            MarkerColumns = [new MarkerColumnEvidence(double.NaN)],
        }));
    }

    [TestMethod]
    public void SessionLatticeRejectsNonOriginalRequestAndEvidenceCoordinates()
    {
        Assert.Throws<ArgumentException>(() => SessionLattice.Fit(new SessionLatticeRequest
        {
            CoordinateSpace = "enhanced_pixels",
            MarkerColumns = Columns(100, 125),
        }));

        Assert.Throws<ArgumentException>(() => SessionLattice.Fit(new SessionLatticeRequest
        {
            MarkerColumns =
            [
                new MarkerColumnEvidence(100),
                new MarkerColumnEvidence(125, CoordinateSpace: "deskewed_pixels"),
            ],
        }));
    }

    private static MarkerColumnEvidence[] Columns(params double[] xs) =>
        xs.Select(x => new MarkerColumnEvidence(x)).ToArray();
}
