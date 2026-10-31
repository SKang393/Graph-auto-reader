// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

/// <summary>
/// Optional local diagnostic data for tick-lane recovery. The records contain
/// only seed geometry and recognition literals, never image pixels or truth.
/// </summary>
internal sealed record OcrTickLaneCropDiagnostic(
    string RegionId,
    OcrRectangle Bounds,
    string TickId);

/// <summary>
/// Independent failed bounds checks. One rejected component can increment
/// several counters; their sum is not a count of distinct components.
/// </summary>
internal sealed record OcrTickLaneSeedBoundsRejections(
    int InvalidBounds,
    int NotBelowPlot,
    int LeftOfLane,
    int RightOfLane,
    int TooShort,
    int TooNarrow,
    int TooWide,
    int TooFarBelowPlot);

/// <summary>
/// BoundsRejections is null when no component bounds were classified because
/// the plot was invalid or no usable tick strokes were available. When present,
/// each value is an independently counted condition and values may overlap.
/// The synthetic diagnostic sidecar adds this optional field to schema v1;
/// earlier v1 sidecars do not contain it.
/// </summary>
internal sealed record OcrTickLaneSelectionDiagnostic(
    int ComponentCount,
    int UsableStrokeCount,
    int RejectedOutsideSeedBounds,
    int RejectedCoveredByDetected,
    int RejectedCoveredByRecovered,
    int RejectedCoveredByProposal,
    int RejectedWithoutUniqueTick,
    int RejectedWithoutMatchingStroke,
    int RejectedWithAmbiguousStroke,
    IReadOnlyList<OcrTickLaneCropDiagnostic> ProposedCrops,
    OcrTickLaneSeedBoundsRejections? BoundsRejections = null);

internal sealed record OcrTickLaneRecoveryDiagnostic(
    bool ZeroAnchorSeedAttempted,
    int ExistingXAxisAnchorCount,
    int ConnectedComponentCount,
    OcrTickLaneSelectionDiagnostic? ZeroAnchorSelection);

internal sealed record OcrTickLaneRecognitionDiagnostic(
    string RegionId,
    IReadOnlyList<string> LiteralAlternatives,
    string? FailureCode);

/// <summary>
/// Independent failed predicates for one supplied stroke. Nonfinite numeric
/// values are represented as null so optional JSON diagnostics remain valid.
/// Multiple rejection flags can be true for the same stroke.
/// </summary>
internal sealed record OcrTickLaneStrokeDiagnostic(
    int SuppliedIndex,
    string? TickId,
    double? CenterX,
    double? CenterY,
    double? LineStartX,
    double? LineStartY,
    double? LineEndX,
    double? LineEndY,
    double? Confidence,
    IReadOnlyList<string>? SupportingCandidateIds,
    double? PlotBottom,
    double? AllowedAxisDistance,
    bool MissingStroke,
    bool InvalidTickId,
    bool NonfiniteCenter,
    bool NonfiniteLineStart,
    bool NonfiniteLineEnd,
    bool InvalidConfidence,
    bool MissingSupportingCandidates,
    bool InvalidSupportingCandidateId,
    bool OutsideAxisDistance,
    bool NotNearVertical,
    bool Usable);

internal sealed record OcrTickLaneDiagnostic(
    string ProjectId,
    string PanelId,
    string InputSha256,
    bool CacheHit,
    bool RecoveryAnalysisPerformed,
    int SuppliedStrokeCount,
    int UsableStrokeCount,
    int DetectedTextRegionCount,
    OcrTickLaneRecoveryDiagnostic? Recovery,
    IReadOnlyList<OcrTickLaneRecognitionDiagnostic> Recognitions,
    IReadOnlyList<string> FinalSeedRegionIds,
    IReadOnlyList<string> FinalSeedXTickRegionIds,
    IReadOnlyList<OcrTickLaneStrokeDiagnostic>? XAxisStrokeDiagnostics = null);
