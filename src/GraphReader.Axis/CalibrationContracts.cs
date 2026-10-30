// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Axis;

public static class CalibrationStatusCodes
{
    public const string Valid = "valid";
    public const string InvalidSessionOrigin = "invalid_session_origin";
    public const string NeedsReview = "needs_review";
    public const string InsufficientEvidence = "insufficient_evidence";
}

public enum CalibrationValidity
{
    Valid,
    InvalidSessionOrigin,
    NeedsReview,
    InsufficientEvidence,
}

public enum SessionLatticeSource
{
    None,
    OcrTicks,
    AxisTicks,
    MarkerLattice,
    SharedPanel,
    Manual,
    Mixed,
}

public enum SessionXEvidenceKind
{
    OrdinalOnly,
    Estimated,
    Printed,
    Manual,
}

public enum CalibrationAnchorKind
{
    Session1Y0,
    Session1YMaximum,
    SessionMaximumY0,
    FirstVisibleSessionY0,
    FirstVisibleSessionYMaximum,
}

public sealed record NumericTickEvidence(
    string Id,
    double PixelPosition,
    double Value,
    double Confidence = 1d,
    string CoordinateSpace = AxisGeometryCoordinateSpaces.OriginalPixels);

public sealed record PrintedXTickEvidence(
    string Id,
    double PixelX,
    double PrintedValue,
    double Confidence = 1d,
    string CoordinateSpace = AxisGeometryCoordinateSpaces.OriginalPixels);

public sealed record UnlabeledXTickEvidence(
    string Id,
    double PixelX,
    double Confidence = 1d,
    string CoordinateSpace = AxisGeometryCoordinateSpaces.OriginalPixels);

public sealed record MarkerColumnEvidence(
    double PixelX,
    double Confidence = 1d,
    string? PanelId = null,
    string CoordinateSpace = AxisGeometryCoordinateSpaces.OriginalPixels);

public sealed record ConnectedSequenceEvidence(
    string SequenceId,
    IReadOnlyList<double> PixelXs,
    double Confidence = 1d,
    string CoordinateSpace = AxisGeometryCoordinateSpaces.OriginalPixels);

public sealed record SharedPanelLatticeEvidence(
    string PanelId,
    double Session1PixelX,
    double PitchPixels,
    double Confidence = 1d,
    string CoordinateSpace = AxisGeometryCoordinateSpaces.OriginalPixels);

public sealed record SessionOriginOverride(
    double Session1PixelX,
    double Session1Value = 1d,
    double Confidence = 1d,
    string CoordinateSpace = AxisGeometryCoordinateSpaces.OriginalPixels,
    string? ProvenanceId = null,
    string? Reason = null,
    DateTimeOffset? ConfirmedAtUtc = null);

public sealed record RobustFitOptions
{
    public double InlierTolerancePixels { get; init; } = 2d;

    public double MinimumInlierWeightFraction { get; init; } = 0.5d;

    public int MaximumRefinementIterations { get; init; } = 3;
}

public sealed record SessionLatticeRequest
{
    public string CoordinateSpace { get; init; } = AxisGeometryCoordinateSpaces.OriginalPixels;

    public IReadOnlyList<PrintedXTickEvidence> PrintedTicks { get; init; } = [];

    public IReadOnlyList<UnlabeledXTickEvidence> UnlabeledTicks { get; init; } = [];

    public IReadOnlyList<MarkerColumnEvidence> MarkerColumns { get; init; } = [];

    public IReadOnlyList<ConnectedSequenceEvidence> ConnectedSequences { get; init; } = [];

    public IReadOnlyList<SharedPanelLatticeEvidence> SharedPanels { get; init; } = [];

    public SessionOriginOverride? OriginOverride { get; init; }

    /// <summary>
    /// A trusted pixel location for session 1, such as a user-confirmed anchor.
    /// It is validation evidence and does not silently replace a conflicting fit.
    /// </summary>
    public double? ExpectedSession1PixelX { get; init; }

    /// <summary>
    /// Requires the first observed column to be session 1. Disable this only
    /// for a known sparse-probe or staggered profile whose absolute origin is
    /// established independently.
    /// </summary>
    public bool RequireFirstObservedSessionOne { get; init; } = true;

    public int MaxSessionGap { get; init; } = 64;

    public double AlignmentToleranceFraction { get; init; } = 0.12d;

    public double DuplicateColumnTolerancePixels { get; init; } = 1.5d;
}

public sealed record LinearAxisTransform(double Slope, double Intercept)
{
    public double PixelToGraph(double pixelPosition) => (Slope * pixelPosition) + Intercept;

    public double GraphToPixel(double graphValue)
    {
        if (!double.IsFinite(Slope) || Math.Abs(Slope) <= 1e-12)
        {
            throw new InvalidOperationException("The fitted transform is not invertible.");
        }

        return (graphValue - Intercept) / Slope;
    }
}

public sealed record CalibrationUncertainty(
    double RootMeanSquareErrorPixels,
    double MaximumResidualPixels,
    double InlierWeightFraction,
    double SlopeStandardError,
    bool ExtrapolatesBeyondEvidence);

public sealed record CalibrationDiagnostics(
    int InputCount,
    int InlierCount,
    IReadOnlyList<string> InlierIds,
    IReadOnlyList<string> OutlierIds,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed);

public sealed record LinearTransformFitResult(
    LinearAxisTransform? Transform,
    double Confidence,
    CalibrationValidity Validity,
    IReadOnlyList<string> Reasons,
    CalibrationUncertainty Uncertainty,
    CalibrationDiagnostics Diagnostics)
{
    public bool IsValid => Transform is not null && Validity == CalibrationValidity.Valid;
}

public sealed record SessionXEvidence(
    double PixelX,
    int Ordinal,
    double? PrintedX,
    double? EstimatedX,
    SessionXEvidenceKind EvidenceKind,
    double Confidence);

public sealed record SessionLatticeUncertainty(
    double RootMeanSquareAlignmentErrorPixels,
    double MaximumAlignmentErrorPixels,
    double RelativePitchUncertainty,
    double AlternativeScoreMargin,
    bool HarmonicAmbiguity);

public sealed record SessionLatticeDiagnostics(
    int CandidatePitchCount,
    int UniqueColumnCount,
    int PrintedTickCount,
    int ConnectedSequenceCount,
    int SharedPanelCount,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed,
    int UnlabeledTickCount = 0);

public sealed record SessionLatticeResult(
    double? Session1PixelX,
    double? PitchPixels,
    double Confidence,
    SessionLatticeSource Source,
    CalibrationValidity Validity,
    IReadOnlyList<string> Reasons,
    bool HasAbsoluteSessionOrigin,
    bool IsOrdinalOnly,
    IReadOnlyList<SessionXEvidence> Assignments,
    SessionLatticeUncertainty Uncertainty,
    SessionLatticeDiagnostics Diagnostics,
    bool UsedManualOriginOverride,
    IReadOnlyList<SessionLatticeSource> ContributingSources,
    SessionOriginOverride? ManualOriginOverride,
    string CoordinateSpace = AxisGeometryCoordinateSpaces.OriginalPixels)
{
    public string StatusCode => Validity switch
    {
        CalibrationValidity.Valid => CalibrationStatusCodes.Valid,
        CalibrationValidity.InvalidSessionOrigin => CalibrationStatusCodes.InvalidSessionOrigin,
        CalibrationValidity.NeedsReview => CalibrationStatusCodes.NeedsReview,
        _ => CalibrationStatusCodes.InsufficientEvidence,
    };
}

public sealed record CalibrationAnchor(
    CalibrationAnchorKind Kind,
    PixelPoint Screen,
    double? GraphX,
    double GraphY,
    double Confidence,
    bool IsExact,
    string CoordinateSpace = AxisGeometryCoordinateSpaces.OriginalPixels);

public sealed record SessionFirstCalibrationRequest
{
    public IReadOnlyList<NumericTickEvidence> YTicks { get; init; } = [];

    public IReadOnlyList<PrintedXTickEvidence> PrintedXTicks { get; init; } = [];

    public SessionLatticeRequest Lattice { get; init; } = new();

    public double? YMaximum { get; init; }

    public double? XMaximum { get; init; }

    public RobustFitOptions? FitOptions { get; init; }
}

public sealed record SessionFirstCalibrationResult(
    LinearTransformFitResult YTransform,
    LinearTransformFitResult? XTransform,
    SessionLatticeResult Lattice,
    IReadOnlyList<CalibrationAnchor> Anchors,
    CalibrationValidity Validity,
    IReadOnlyList<string> Reasons,
    double Confidence,
    TimeSpan Elapsed,
    string CoordinateSpace = AxisGeometryCoordinateSpaces.OriginalPixels);
