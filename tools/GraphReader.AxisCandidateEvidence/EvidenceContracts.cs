// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json.Serialization;
using GraphReader.Axis;

namespace GraphReader.AxisCandidateEvidence;

public sealed record AxisCandidateEvidenceInputBinding(
    string Schema,
    string Scope,
    string SourceManifestPath,
    string SourceManifestSha256,
    string SourceSplit,
    string SourceId,
    string PanelId,
    string SourceImagePath,
    string SourceImageSha256,
    long SourceImageByteLength,
    string PanelSha256,
    string CoordinateSpace,
    OpenCvLineCandidateOptions OpenCvOptions,
    AxisGeometryOptions GeometryOptions,
    IReadOnlyDictionary<string, string> SourceBindings,
    IReadOnlyList<string> Claims);

public sealed record AxisCandidateEvidenceSource(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("source_manifest_path")] string SourceManifestPath,
    [property: JsonPropertyName("source_manifest_sha256")] string SourceManifestSha256,
    [property: JsonPropertyName("split")] string Split,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("panel_id")] string PanelId,
    [property: JsonPropertyName("source_image_path")] string SourceImagePath,
    [property: JsonPropertyName("source_sha256")] string SourceSha256,
    [property: JsonPropertyName("panel_sha256")] string PanelSha256,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("coordinate_space")] string CoordinateSpace);

public sealed record AxisCandidateEvidenceRuntime(
    [property: JsonPropertyName("tool_assembly_path")] string ToolAssemblyPath,
    [property: JsonPropertyName("tool_assembly_sha256")] string ToolAssemblySha256,
    [property: JsonPropertyName("axis_assembly_path")] string AxisAssemblyPath,
    [property: JsonPropertyName("axis_assembly_sha256")] string AxisAssemblySha256,
    [property: JsonPropertyName("open_cv_managed_assembly_path")] string OpenCvManagedAssemblyPath,
    [property: JsonPropertyName("open_cv_managed_assembly_sha256")] string OpenCvManagedAssemblySha256,
    [property: JsonPropertyName("open_cv_native_module_path")] string OpenCvNativeModulePath,
    [property: JsonPropertyName("open_cv_native_module_sha256")] string OpenCvNativeModuleSha256,
    [property: JsonPropertyName("source_bindings")] IReadOnlyDictionary<string, string> SourceBindings,
    [property: JsonPropertyName("tool_source_bindings")] IReadOnlyDictionary<string, string> ToolSourceBindings,
    [property: JsonPropertyName("open_cv_options")] OpenCvLineCandidateOptions OpenCvOptions,
    [property: JsonPropertyName("geometry_options")] AxisGeometryOptions GeometryOptions);

public sealed record AxisCandidateEvidenceFailure(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("exception_type")] string ExceptionType,
    [property: JsonPropertyName("message")] string? Message);

public sealed record AxisCandidateEvidenceFeatureRow(
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("features")] IReadOnlyList<double> Features);

public sealed record AxisCandidateEvidenceFeatureMatrix(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("feature_names")] IReadOnlyList<string> FeatureNames,
    [property: JsonPropertyName("rows")] IReadOnlyList<AxisCandidateEvidenceFeatureRow> Rows);

public sealed record AxisCandidateEvidenceReport(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("source")] AxisCandidateEvidenceSource Source,
    [property: JsonPropertyName("runtime")] AxisCandidateEvidenceRuntime? Runtime,
    [property: JsonPropertyName("candidate_snapshots")] IReadOnlyList<AxisGeometryCandidateSnapshot> CandidateSnapshots,
    [property: JsonPropertyName("normalized_features")] AxisCandidateEvidenceFeatureMatrix? NormalizedFeatures,
    [property: JsonPropertyName("failure")] AxisCandidateEvidenceFailure? Failure);
