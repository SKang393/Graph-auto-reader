// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string ForwardExtentTraceSchema = "graphreader.real-dev-forward-extent-geometry-trace.v1";
    private const string ForwardExtentTraceBaseCommit = "d279d384fff0c29ee0d897de255447535e3ff7f9";
    private const string ForwardExtentAxisHash = "a7768c53a96d9e9b6ad8e002afa883e90ee77973cc1fa4b3b8e66611811bf7f0";
    private const string ForwardExtentOpenCvExternHash = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31";

    private static FrozenDictionary<string, string> ForwardExtentRuntimeLibraries => CreateForwardExtentRuntimeLibraries();

    private static FrozenDictionary<string, string> CreateForwardExtentRuntimeLibraries()
    {
        var libraries = SortedSpatialRuntimeLibraries.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        libraries["GraphReader.Axis.dll"] = ForwardExtentAxisHash;
        libraries["OpenCvSharpExtern.dll"] = ForwardExtentOpenCvExternHash;
        return libraries.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static object ForwardExtentTraceIdentity() => new
    {
        kind = "unpackaged_source_runtime",
        base_source_commit = ForwardExtentTraceBaseCommit,
        source_change = "zero_forward_extent_geometry_repair",
        app_exe_sha256 = SpatialAppExeHash,
        app_dll_sha256 = SpatialAppDllHash,
        axis_dll_sha256 = ForwardExtentAxisHash,
        rust_sha256 = SpatialRustHash,
        runtime_files = ForwardExtentRuntimeLibraries,
        reviewed_opencv_extern_sha256 = ForwardExtentOpenCvExternHash,
        ocr_calls = 0,
    };
}
