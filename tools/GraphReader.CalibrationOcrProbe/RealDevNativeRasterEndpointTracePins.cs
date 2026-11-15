// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal const string NativeRasterEndpointTraceSchema = "graphreader.real-dev-native-raster-endpoint-trace.v1";
    private const string NativeRasterEndpointBaseCommit = "0642f21f42894cfc2997dab40dcce4ae932ad480";
    private const string NativeRasterEndpointAppExeHash = "a7c3a7757d2f70c3adb553b7f01e71538dcd603f55126f5bcf320e5ccd827444";
    private const string NativeRasterEndpointAppDllHash = "567eecf5ab59a2905b3a2feba5e7fe84a493c8b50f46ea42bf1f2668329dbd9a";
    private const string NativeRasterEndpointRustHash = "2cc6f263a00544243291f2605031c4df7e147175bd345480bd0a44f92fde0b7a";
    private const string NativeRasterEndpointRuntimePinsFileHash = "ca71652dec2bde05c55a1a4a41ce5b67d8f62a8f4e64a5ea29f3e3874f61069a";

    // Frozen from artifacts/native-endpoint-check/runtime-pins.json. Keep the
    // profile separate so earlier trace identities remain immutable.
    private static readonly FrozenDictionary<string, string> NativeRasterEndpointRuntimeLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GraphReader.App.dll"] = "567eecf5ab59a2905b3a2feba5e7fe84a493c8b50f46ea42bf1f2668329dbd9a",
            ["GraphReader.Axis.dll"] = "24793789f51a80e04e13d5638eaf406704b256b893263705b57e61953fc5ad96",
            ["GraphReader.Domain.dll"] = "1e015395a110240bb48b1b25bf4cf0d6dbf3d15c369159022d7d36cbf9a3f51c",
            ["GraphReader.Export.dll"] = "fd1f734eeb54d96e05e29915a96b1010acdb1fc915e1eb45eb32833d15ab8f29",
            ["GraphReader.Imaging.dll"] = "82d3499a30f1415a9dc4537b83fbe3ba3b7206062d21b985ae74bb6b35a82897",
            ["GraphReader.Inference.dll"] = "add233580fe15fd0accf4ad505b9d5cbf79cb52512ece6c9c05a7d61a471f1e9",
            ["GraphReader.Legends.dll"] = "dd2c23ad7e7864b4c527ec4a7e85eaadf3e3cd0fde18e63e082a5bea8f06cc1e",
            ["GraphReader.Markers.dll"] = "effcd46db3106d1227fe078ddc92c2e31c5ebba4dbe4788fa7ff0446e76e25d6",
            ["GraphReader.Ocr.dll"] = "b14d4e0ea006b25585e5a8d15eab77b11498c54207fb03b808db24ecb16ff78f",
            ["GraphReader.Pdf.dll"] = "5295c9da1f41f7ea57b1ba061d0f995582a2ec8a70a3872e7c74581016378316",
            ["GraphReader.Phases.dll"] = "17030f94d91109f0b1733a1c195b10c5584e3fed71d5ab2e47a91f4320c6df21",
            ["GraphReader.SuperResolution.dll"] = "a56bc2e0610ee0eab444433140a5679ee2e119d557a8a97923486d049e3a0084",
            ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
            ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
            ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
            ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
            ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
            ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
            ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object NativeRasterEndpointTraceIdentity() => new
    {
        kind = "unpackaged_native_raster_endpoint_source_runtime",
        base_source_commit = NativeRasterEndpointBaseCommit,
        app_exe_sha256 = NativeRasterEndpointAppExeHash,
        app_dll_sha256 = NativeRasterEndpointAppDllHash,
        rust_sha256 = NativeRasterEndpointRustHash,
        runtime_pins_file_sha256 = NativeRasterEndpointRuntimePinsFileHash,
        runtime_files = NativeRasterEndpointRuntimeLibraries,
        model_sha256 = Program.ModelHash,
        manifest_sha256 = Program.ManifestHash,
        diagnostic_scope = "fixed12_automatic_geometry_literal_ocr_exact_box_ticks_and_review_drafts",
        binding = "production_helper_on_transient_suggestion_not_workspace_currentness"
    };

    private sealed partial class Candidate
    {
        internal static Candidate VerifyNativeRasterEndpoint(string app, string assets, string rust, CancellationToken token)
        {
            RequireNativeRejectionPins(NativeRasterEndpointAppExeHash, NativeRasterEndpointAppDllHash,
                NativeRasterEndpointRustHash, NativeRasterEndpointRuntimeLibraries);
            return VerifyPinned(app, assets, rust, NativeRasterEndpointAppExeHash, NativeRasterEndpointAppDllHash,
                NativeRasterEndpointRustHash, NativeRasterEndpointRuntimeLibraries, token, requirePrintedXReviewDraft: true);
        }
    }

    internal static object NativeRasterEndpointTraceSelfTest()
    {
        _ = ExactBoxTickTraceSelfTest();
        try
        {
            RequireNativeRejectionPins("", NativeRasterEndpointAppDllHash, NativeRasterEndpointRustHash,
                NativeRasterEndpointRuntimeLibraries);
            throw new InvalidOperationException();
        }
        catch (InvalidDataException) { }

        if (NativeRasterEndpointRuntimeLibraries.Count != 20 ||
            NativeRasterEndpointRuntimeLibraries["GraphReader.App.dll"] != NativeRasterEndpointAppDllHash ||
            !NativeRasterEndpointRuntimeLibraries.TryGetValue("OpenCvSharpExtern.dll", out string? openCv) ||
            openCv != "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31")
            throw new InvalidDataException();

        return new
        {
            schema = "graphreader.real-dev-native-raster-endpoint-trace-self-test.v1",
            distinct_profile = true,
            runtime_pin_count = NativeRasterEndpointRuntimeLibraries.Count,
            historical_profiles_unchanged = true,
            private_reads = 0
        };
    }
}
