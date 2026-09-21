// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

/// <summary>
/// Optional in-memory evidence before assembly or recognition. Contains no
/// raster pixels and is not part of the project or vision-result contracts.
/// </summary>
public sealed record OcrDetectionObservation(
    string ProjectId,
    string PanelId,
    string InputSha256,
    int Width,
    int Height,
    bool SuppliedRegions,
    IReadOnlyList<OcrDetectedRegion> RawDetectorRegions);
