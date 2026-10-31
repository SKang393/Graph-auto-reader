// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json.Serialization;
using GraphReader.Axis;

namespace GraphReader.AxisCandidateEvidence;

public sealed record AxisCandidatePanelBatchBinding(
    string Schema,
    string Scope,
    string Split,
    string PanelizationAdmissionBindingPath,
    string PanelizationAdmissionBindingSha256,
    string PanelizationReceiptPath,
    string PanelizationReceiptSha256,
    string PanelizationRunRoot,
    int ExpectedSourceCount,
    int ExpectedDetectedSourceCount,
    int ExpectedFallbackSourceCount,
    int ExpectedFailedSourceCount,
    int ExpectedPanelCount,
    IReadOnlyList<AxisCandidatePanelSelection>? SelectedPanels,
    IReadOnlyDictionary<string, string> ProductionSourceBindings,
    IReadOnlyDictionary<string, string> PanelizerRuntimeBindings,
    IReadOnlyDictionary<string, string> ToolSourceBindings,
    IReadOnlyDictionary<string, string> ToolRuntimeBindings,
    OpenCvLineCandidateOptions OpenCvOptions,
    AxisGeometryOptions GeometryOptions);

public sealed record AxisCandidatePanelSelection(
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("panel_id")] string PanelId);

public sealed record AxisCandidatePanelRect(
    [property: JsonPropertyName("X")] double X,
    [property: JsonPropertyName("Y")] double Y,
    [property: JsonPropertyName("Width")] double Width,
    [property: JsonPropertyName("Height")] double Height,
    [property: JsonPropertyName("Right")] double Right,
    [property: JsonPropertyName("Bottom")] double Bottom,
    [property: JsonPropertyName("Area")] double Area,
    [property: JsonPropertyName("IsValid")] bool IsValid);

public sealed record AxisCandidatePanelSourceSummary(
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("source_sha256")] string SourceSha256,
    [property: JsonPropertyName("source_width")] int SourceWidth,
    [property: JsonPropertyName("source_height")] int SourceHeight,
    [property: JsonPropertyName("source_status")] string SourceStatus,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("panel_count")] int PanelCount,
    [property: JsonPropertyName("selected_panel_count")] int SelectedPanelCount);

public sealed record AxisCandidatePanelResult(
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("source_image_path")] string SourceImagePath,
    [property: JsonPropertyName("source_sha256")] string SourceSha256,
    [property: JsonPropertyName("source_byte_length")] long SourceByteLength,
    [property: JsonPropertyName("source_width")] int SourceWidth,
    [property: JsonPropertyName("source_height")] int SourceHeight,
    [property: JsonPropertyName("panel_id")] string PanelId,
    [property: JsonPropertyName("panel_origin_kind")] string PanelOriginKind,
    [property: JsonPropertyName("panel_image_path")] string PanelImagePath,
    [property: JsonPropertyName("panel_sha256")] string PanelSha256,
    [property: JsonPropertyName("panel_byte_length")] long PanelByteLength,
    [property: JsonPropertyName("panel_width")] int PanelWidth,
    [property: JsonPropertyName("panel_height")] int PanelHeight,
    [property: JsonPropertyName("requested_crop_in_source_pixels")] AxisCandidatePanelRect RequestedCrop,
    [property: JsonPropertyName("encoded_crop_in_source_pixels")] AxisCandidatePanelRect EncodedCrop,
    [property: JsonPropertyName("source_to_panel_matrix_row_major")] IReadOnlyList<double> SourceToPanelMatrix,
    [property: JsonPropertyName("panel_to_source_matrix_row_major")] IReadOnlyList<double> PanelToSourceMatrix,
    [property: JsonPropertyName("panelization_warnings")] IReadOnlyList<string> PanelizationWarnings,
    [property: JsonPropertyName("coordinate_space")] string CoordinateSpace,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("candidate_snapshots")] IReadOnlyList<AxisGeometryCandidateSnapshot> CandidateSnapshots,
    [property: JsonPropertyName("normalized_features")] AxisCandidateEvidenceFeatureMatrix? NormalizedFeatures,
    [property: JsonPropertyName("failure")] AxisCandidateEvidenceFailure? Failure);

public sealed record AxisCandidatePanelEvidenceRuntime(
    [property: JsonPropertyName("tool_assembly_path")] string ToolAssemblyPath,
    [property: JsonPropertyName("tool_assembly_sha256")] string ToolAssemblySha256,
    [property: JsonPropertyName("app_assembly_path")] string AppAssemblyPath,
    [property: JsonPropertyName("app_assembly_sha256")] string AppAssemblySha256,
    [property: JsonPropertyName("axis_assembly_path")] string AxisAssemblyPath,
    [property: JsonPropertyName("axis_assembly_sha256")] string AxisAssemblySha256,
    [property: JsonPropertyName("open_cv_managed_assembly_path")] string OpenCvManagedAssemblyPath,
    [property: JsonPropertyName("open_cv_managed_assembly_sha256")] string OpenCvManagedAssemblySha256,
    [property: JsonPropertyName("open_cv_native_module_path")] string OpenCvNativeModulePath,
    [property: JsonPropertyName("open_cv_native_module_sha256")] string OpenCvNativeModuleSha256,
    [property: JsonPropertyName("production_source_bindings")] IReadOnlyDictionary<string, string> ProductionSourceBindings,
    [property: JsonPropertyName("panelizer_runtime_bindings")] IReadOnlyDictionary<string, string> PanelizerRuntimeBindings,
    [property: JsonPropertyName("tool_source_bindings")] IReadOnlyDictionary<string, string> ToolSourceBindings,
    [property: JsonPropertyName("tool_runtime_bindings")] IReadOnlyDictionary<string, string> ToolRuntimeBindings,
    [property: JsonPropertyName("open_cv_options")] OpenCvLineCandidateOptions OpenCvOptions,
    [property: JsonPropertyName("geometry_options")] AxisGeometryOptions GeometryOptions);

public sealed record AxisCandidatePanelBatchFailure(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("exception_type")] string ExceptionType,
    [property: JsonPropertyName("message")] string? Message);

public sealed record AxisCandidatePanelBatchReport(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("split")] string Split,
    [property: JsonPropertyName("input_manifest_path")] string InputManifestPath,
    [property: JsonPropertyName("input_manifest_sha256")] string InputManifestSha256,
    [property: JsonPropertyName("panelization_admission_binding_sha256")] string PanelizationAdmissionBindingSha256,
    [property: JsonPropertyName("panelization_receipt_sha256")] string PanelizationReceiptSha256,
    [property: JsonPropertyName("source_count")] int SourceCount,
    [property: JsonPropertyName("detected_source_count")] int DetectedSourceCount,
    [property: JsonPropertyName("fallback_source_count")] int FallbackSourceCount,
    [property: JsonPropertyName("failed_source_count")] int FailedSourceCount,
    [property: JsonPropertyName("panel_count")] int PanelCount,
    [property: JsonPropertyName("selected_panel_count")] int SelectedPanelCount,
    [property: JsonPropertyName("source_summaries")] IReadOnlyList<AxisCandidatePanelSourceSummary> SourceSummaries,
    [property: JsonPropertyName("panel_results")] IReadOnlyList<AxisCandidatePanelResult> PanelResults,
    [property: JsonPropertyName("runtime")] AxisCandidatePanelEvidenceRuntime? Runtime,
    [property: JsonPropertyName("failure")] AxisCandidatePanelBatchFailure? Failure);
