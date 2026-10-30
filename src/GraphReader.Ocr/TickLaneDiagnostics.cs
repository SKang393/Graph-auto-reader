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
    IReadOnlyList<OcrTickLaneCropDiagnostic> ProposedCrops);

internal sealed record OcrTickLaneRecoveryDiagnostic(
    bool ZeroAnchorSeedAttempted,
    int ExistingXAxisAnchorCount,
    int ConnectedComponentCount,
    OcrTickLaneSelectionDiagnostic? ZeroAnchorSelection);

internal sealed record OcrTickLaneRecognitionDiagnostic(
    string RegionId,
    IReadOnlyList<string> LiteralAlternatives,
    string? FailureCode);

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
    IReadOnlyList<string> FinalSeedXTickRegionIds);
