// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string NativeEligibilityTraceSchema = "graphreader.real-dev-native-eligibility-geometry-trace.v1";
    // Frozen unpackaged native-eligibility source runtime, verified against the measured file map.
    private const string NativeEligibilityAppExeHash = "c62e0aff97f086955402be9f54ce526b6edc8be7cdce65c58fd62f89bade41fd";
    private const string NativeEligibilityAppDllHash = "3bbeef0c6562335cf988296169a25e1c52d9e12e739b94ba6c52f2c73be891c4";
    private const string NativeEligibilityRustHash = "9c5ac19abc3b08632d82b5590a9e59f2794d94f92f345fbc70741e181808c0d9";
    private static readonly FrozenDictionary<string, string> NativeEligibilityRuntimeLibraries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GraphReader.App.dll"] = "3bbeef0c6562335cf988296169a25e1c52d9e12e739b94ba6c52f2c73be891c4",
        ["GraphReader.Axis.dll"] = "25af744f72d9815dc8f930046b589a1bee12a6ecce58f7221a9557bafd752faa",
        ["GraphReader.Domain.dll"] = "ecb823f559a4ab8f148735be6e6b4563e8adbbc367a83f894622c64266df2244",
        ["GraphReader.Export.dll"] = "ce5f543c83cd740bfab0dde5c30ca323acf874712ff583bb8e964732c9d6efb3",
        ["GraphReader.Imaging.dll"] = "aa803c7e8f7a4c3d317777dcede95654c994319aabf03bd1e6c9835f38274b7a",
        ["GraphReader.Inference.dll"] = "efb9d064dcec93c10140b319a57fadda3018acad5ca8043c2df9348519343ac1",
        ["GraphReader.Legends.dll"] = "22956752536f2395cb18556555d4a8d0765bf789bc6146caea7d29d2f975fae8",
        ["GraphReader.Markers.dll"] = "28a8b2a979284058c645fd9b4bf84a1bd9f94a739483de3441ffe651910d1d56",
        ["GraphReader.Ocr.dll"] = "ac0069a067fe1fa185d834a62072436e334d8d3d6f6fa21ee1fad825f9c9e866",
        ["GraphReader.Pdf.dll"] = "fb321708d28d2e8db864a1e05bec589cb658ea44ae59b932bb895778b67c084d",
        ["GraphReader.Phases.dll"] = "5a275941957b6b2b5b092d164c49dae024d9393cbd14df9ce8d2835290758deb",
        ["GraphReader.SuperResolution.dll"] = "beed20e47b45e215e1d26ca9b3025772cd2a7c132cb518c13ffeacb0a8ca2fad",
        ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
        ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
        ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
        ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
        ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
        ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
        ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
        ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object NativeEligibilityTraceIdentity() => new
    {
        kind = "unpackaged_native_eligibility_source",
        app_exe_sha256 = NativeEligibilityAppExeHash,
        app_dll_sha256 = NativeEligibilityAppDllHash,
        rust_sha256 = NativeEligibilityRustHash,
        runtime_files = NativeEligibilityRuntimeLibraries,
        ocr_calls = 0,
        candidate_selected = false,
    };

    internal static async Task<object> NativeEligibilityTraceSelfTestAsync(CancellationToken token)
    {
        _ = await ReviewRoutingTraceSelfTestAsync(token).ConfigureAwait(false);
        try
        {
            RequireNativeRejectionPins("", "", "", new Dictionary<string, string>(StringComparer.Ordinal));
            throw new InvalidOperationException();
        }
        catch (InvalidDataException) { }
        return new { schema = "graphreader.real-dev-native-eligibility-self-test.v1",
            missing_pins_rejected_before_selector = true, shared_review_routing_accounting = true,
            candidate_selected = false, private_reads = 0 };
    }
}
