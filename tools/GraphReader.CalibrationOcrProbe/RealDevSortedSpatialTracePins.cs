// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string SortedSpatialTraceSchema = "graphreader.real-dev-sorted-spatial-geometry-trace.v1";
    private const string SortedSpatialTraceBaseCommit = "7d9e5f31fc2dd22af7038089b3f70519b6c5081d";
    private const string SortedSpatialAxisHash = "cdf244fd92b3e82fc033a4c636d229f74e07ee0abb4e5d12cfdd23bc40ab8a52";
    private const string SortedSpatialOpenCvExternHash = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31";

    private static FrozenDictionary<string, string> SortedSpatialRuntimeLibraries => CreateSortedSpatialRuntimeLibraries();

    private static FrozenDictionary<string, string> CreateSortedSpatialRuntimeLibraries()
    {
        var libraries = SpatialRuntimeLibraries.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        libraries["GraphReader.Axis.dll"] = SortedSpatialAxisHash;
        libraries["OpenCvSharpExtern.dll"] = SortedSpatialOpenCvExternHash;
        return libraries.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static object SortedSpatialTraceIdentity() => new
    {
        kind = "unpackaged_source_runtime",
        base_source_commit = SortedSpatialTraceBaseCommit,
        source_change = "sorted_spatial_axis_candidate_merge_repair",
        app_exe_sha256 = SpatialAppExeHash,
        app_dll_sha256 = SpatialAppDllHash,
        axis_dll_sha256 = SortedSpatialAxisHash,
        rust_sha256 = SpatialRustHash,
        runtime_files = SortedSpatialRuntimeLibraries,
        reviewed_opencv_extern_sha256 = SortedSpatialOpenCvExternHash,
        model_sha256 = Program.ModelHash,
        manifest_sha256 = Program.ManifestHash,
        ocr_calls = 0,
    };
}
