// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Axis;
using GraphReader.Markers.Classification;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>In-memory diagnostic observation. No writer is installed by production or private acceptance.</summary>
internal sealed record ProductionCandidateCalibrationObservation(
    Guid RunId,
    Guid SourceId,
    Guid PanelId,
    string PanelSha256,
    AxisGeometryResult Axis,
    OcrResult Ocr,
    IReadOnlyList<ClassifiedMarker> AcceptedMarkers,
    SessionFirstCalibrationResult Calibration,
    IReadOnlyList<WorkflowVisionEnvelope> Provenance);
