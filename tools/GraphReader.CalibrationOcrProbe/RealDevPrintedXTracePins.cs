// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string PrintedXTraceBaseCommit = "6c450ab8b3ca9b1965444c107f146121d31f73c8";
    // Current source Debug runtime for the printed-X review-draft trace.
    // OpenCvSharpExtern is deliberately pinned to the reviewed native build,
    // not the NuGet copy that may be present after a source build.
    private const string PrintedXAppExeHash = "648912cd16835729e8b0ae440441041170547b5bf0ec8ce032616a80550c99ff";
    private const string PrintedXAppDllHash = "f09b4e7e2121e8362157dfd516c8e46d2be7bcf5cf9a7eda438b643b5201fd87";
    private const string PrintedXRustHash = "cfc8e59a08b4aeaad84cc4742aa8a8c2f211a8f575f0d6bcfb6beb5f5a1880fd";
    private const string PrintedXOpenCvExternHash = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31";

    private static readonly FrozenDictionary<string, string> PrintedXRuntimeLibraries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GraphReader.App.dll"] = "f09b4e7e2121e8362157dfd516c8e46d2be7bcf5cf9a7eda438b643b5201fd87",
        ["GraphReader.Axis.dll"] = "ecf7cf7b06f19ef0d3d4de8b60107268b46936efd0518c3fb3b24a341729ee24",
        ["GraphReader.Domain.dll"] = "47a2fc1529a35ed267d7e079dc4b2fa57d49e099abf120b6c89b64bd8363499f",
        ["GraphReader.Export.dll"] = "8a1e9415477e3eedbfd08cc96f19539d73488298fe4f9a57e08a29d84bdb9dfe",
        ["GraphReader.Imaging.dll"] = "0a60ce9be623ca8307648f826a0fafc64e9b688b08dac8926ec9fa1ab4e19d51",
        ["GraphReader.Inference.dll"] = "278c359ab3ad86c99c8205401a2b71163842a3f7d5dcf7c5b8d04caa17736280",
        ["GraphReader.Legends.dll"] = "8672ad26dbc3c72f889ffeb7a008d52f56c6b55bb1325a9aee73bb48245a63ea",
        ["GraphReader.Markers.dll"] = "a40ca607d00e9f575f8608432642a4cba9f5bc1f0feb96c5cd8c89576d33b030",
        ["GraphReader.Ocr.dll"] = "d7700f81450b933c73bc8d7a5a6105e324111c87112589c105dc9015822b5983",
        ["GraphReader.Pdf.dll"] = "58473b32b19d36564d279eb5405891210448234cd959e7f627ab428a18039b70",
        ["GraphReader.Phases.dll"] = "75d2fb8c0510abe2dd37e9abb20d20105062090bac95fbd88238d2423f0ef825",
        ["GraphReader.SuperResolution.dll"] = "60a9feaaa73b398c5745a0ca9b98bbe013e5ed8e60657c03939a64d28ca7cab4",
        ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
        ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
        ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
        ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
        ["OpenCvSharpExtern.dll"] = PrintedXOpenCvExternHash,
        ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
        ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
        ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object PrintedXTraceIdentity() => new
    {
        kind = "unpackaged_source_runtime",
        base_source_commit = PrintedXTraceBaseCommit,
        app_exe_sha256 = PrintedXAppExeHash,
        app_dll_sha256 = PrintedXAppDllHash,
        rust_sha256 = PrintedXRustHash,
        runtime_files = PrintedXRuntimeLibraries,
        reviewed_opencv_extern_sha256 = PrintedXOpenCvExternHash,
        model_sha256 = Program.ModelHash,
        manifest_sha256 = Program.ManifestHash,
        measured_tick_context = "X_and_Y",
        proposal_scope = "printed_x_review_draft_only_not_complete_or_applied_calibration",
    };
}
