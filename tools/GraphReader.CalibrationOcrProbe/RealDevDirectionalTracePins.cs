// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string DirectionalTraceSchema = "graphreader.real-dev-directional-association-trace.v1";
    private const string DirectionalAppExeHash = "53f881c44dbfb676402d60d09473fa638ee2029555145d51effce8b1bb81a1d8";
    private const string DirectionalAppDllHash = "23d1f98707730c0918cc7aef3cf4a0f27cf05582561115cc1047ab63dcf3b901";
    private const string DirectionalRustHash = "cfc8e59a08b4aeaad84cc4742aa8a8c2f211a8f575f0d6bcfb6beb5f5a1880fd";
    private static readonly FrozenDictionary<string, string> DirectionalRuntimeLibraries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GraphReader.App.dll"] = "23d1f98707730c0918cc7aef3cf4a0f27cf05582561115cc1047ab63dcf3b901",
        ["GraphReader.Axis.dll"] = "8c99b49d3d978b283f27b99f90ff2a13a51d86ef3d229c3ee2904444263fe1c8",
        ["GraphReader.Domain.dll"] = "f3179e7bb32813467072103bee2918289d3067f4de668ce889876adbbf9e8924",
        ["GraphReader.Export.dll"] = "18ec4661044375b7c978f936a4b2f9d7b45383bb0c2004a5bf420ef68da662c6",
        ["GraphReader.Imaging.dll"] = "7ed6ad8b99955afa84bf119fd0c3c95cb4aae74cbd5bcd3a2ccfbf8f63cb8a19",
        ["GraphReader.Inference.dll"] = "f9564c3cf15193e27051dece06f52e92a6e0a01523d5c02029e99321a3efea4e",
        ["GraphReader.Legends.dll"] = "ef747fdad129cfa3ccec2e732d51e8b499b09e2a3e67336eaea482e8ef7eb90e",
        ["GraphReader.Markers.dll"] = "99cb2a18f4ebcea6e07097c247f834da16be9bc7295e9f9107e0de7f66081609",
        ["GraphReader.Ocr.dll"] = "25a975ea8dd769466c107ff6e595d2ff085cda0346357f7c51ae53c9b90fefe5",
        ["GraphReader.Pdf.dll"] = "c4acdd1a04e1311d96961bf220b6b2244649a25353018da8471fca077b461bb8",
        ["GraphReader.Phases.dll"] = "e1377c8f12ca71d1a076fe8526fd5f6571b9974b3f17698ea78bb3a4445d53d2",
        ["GraphReader.SuperResolution.dll"] = "13a512b19f55ea50ce6f6baf9839d27caa8c81c0b36f6cc5a20aa4fbcf472709",
        ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
        ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
        ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
        ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
        ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
        ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
        ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
        ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object DirectionalTraceIdentity() => new
    {
        kind = "unpackaged_current_source",
        runtime_deployment = "framework_dependent_net_windows_desktop",
        app_exe_sha256 = DirectionalAppExeHash,
        app_dll_sha256 = DirectionalAppDllHash,
        rust_sha256 = DirectionalRustHash,
        runtime_files = DirectionalRuntimeLibraries,
        measured_tick_context = "X_and_Y",
    };

    internal static object DirectionalTraceSelfTest()
    {
        _ = PostGroupingTraceSelfTest();
        RequireNativeRejectionPins(DirectionalAppExeHash, DirectionalAppDllHash, DirectionalRustHash, DirectionalRuntimeLibraries);
        try
        {
            RequireNativeRejectionPins("", DirectionalAppDllHash, DirectionalRustHash, DirectionalRuntimeLibraries);
            throw new InvalidOperationException();
        }
        catch (InvalidDataException) { }
        return new { schema = "graphreader.real-dev-directional-association-self-test.v1",
            runtime_file_count = DirectionalRuntimeLibraries.Count, missing_pins_rejected_before_selector = true,
            x_y_tick_context = true, private_reads = 0 };
    }
}
