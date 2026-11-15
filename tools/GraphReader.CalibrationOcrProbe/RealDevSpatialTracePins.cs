// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string SpatialTraceBaseCommit = "d61e223c27a38ac4798043e2daf5a677104a8acc";
    private const string SpatialAppExeHash = "de9bcaa28b26887bd78fa0db269bb0a2aeb1897887f29f29e6fcb925098cd663";
    private const string SpatialAppDllHash = "fb18b1223b0070df823936e7b14710edb056825a13dea6d28f13d63e0cc77bfa";
    private const string SpatialRustHash = "cfc8e59a08b4aeaad84cc4742aa8a8c2f211a8f575f0d6bcfb6beb5f5a1880fd";
    private const string SpatialOpenCvExternHash = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31";

    private static readonly FrozenDictionary<string, string> SpatialRuntimeLibraries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GraphReader.App.dll"] = SpatialAppDllHash,
        ["GraphReader.Axis.dll"] = "7c3a0b8fbf256d5c001f21a3bf78d9af675f729a04cc450961d7be7225cdd1fd",
        ["GraphReader.Domain.dll"] = "1d73b47107e41974d2c15806fb246e6a83a38dc2ac2137f36429a4cdb6da5b9c",
        ["GraphReader.Export.dll"] = "14c5647e9fc00fb3e9b638418a5f7d226e6fd76b67571ae03ef472f4abcd0deb",
        ["GraphReader.Imaging.dll"] = "7088f3e7b88ab9a5d1d11960183f78129ff1ea79617c347e9d3b8462985b9c51",
        ["GraphReader.Inference.dll"] = "d51a4df0d46e4b409b14dad15be3b4997f1f559ddf429595f044a3980f08fee1",
        ["GraphReader.Legends.dll"] = "81ae9a2d920860494252001478cd9b65fbc27133b629d4ca48476dc8aa518fb7",
        ["GraphReader.Markers.dll"] = "12dd9b1bda514fd209abef2e5e341bddcd1a669114d92f16e500b6412d63ec42",
        ["GraphReader.Ocr.dll"] = "43a692c26a0c557903cbd0a59d880e61b72e00968d83b82292844664a6851e0d",
        ["GraphReader.Pdf.dll"] = "10b8d999fcafcbe0813560575b6f1337d0aebc9cc7f2993d4e7ac86f977e1bfd",
        ["GraphReader.Phases.dll"] = "9f8192af6b8506bb0687c2f3f8c6e63bc2b21c77305f42a5dbeb058566e05b2f",
        ["GraphReader.SuperResolution.dll"] = "9efa1da6545f45d5ce0f3424d3acbcd74c026cdfc53f9753ad1acaf48e7dccad",
        ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
        ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
        ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
        ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
        ["OpenCvSharpExtern.dll"] = SpatialOpenCvExternHash,
        ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
        ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
        ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object SpatialTraceIdentity() => new
    {
        kind = "unpackaged_source_runtime",
        base_source_commit = SpatialTraceBaseCommit,
        app_exe_sha256 = SpatialAppExeHash,
        app_dll_sha256 = SpatialAppDllHash,
        rust_sha256 = SpatialRustHash,
        runtime_files = SpatialRuntimeLibraries,
        reviewed_opencv_extern_sha256 = SpatialOpenCvExternHash,
        model_sha256 = Program.ModelHash,
        manifest_sha256 = Program.ManifestHash,
        diagnostic_scope = "spatial_axis_geometry_and_jitter_aware_ocr_with_printed_x_review_availability",
    };
}
