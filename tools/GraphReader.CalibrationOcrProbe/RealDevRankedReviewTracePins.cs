// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal const string RankedReviewTraceSchema = "graphreader.real-dev-ranked-review-trace.v1";

    private const string RankedReviewTraceBaseCommit = "2ca493364947243deb4fcff84eaa605efcd2c223";
    private const string RankedReviewAppExeHash = "8e6df066fb568b598b75fd6ffe029570226c5d5196184ea3189e9caeab890024";
    private const string RankedReviewAppDllHash = "b40dc90263c7e143337fb10d7092dd37b34557020923f5e168b6f7f5fce646b4";
    private const string RankedReviewRustHash = "9c5ac19abc3b08632d82b5590a9e59f2794d94f92f345fbc70741e181808c0d9";
    private static readonly FrozenDictionary<string, string> RankedReviewRuntimeLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GraphReader.App.dll"] = RankedReviewAppDllHash,
            ["GraphReader.Axis.dll"] = "2c91b74ee9f77efe1422964c3eb68152482ff4c6a0f5449954b07fc39cd3ebd3",
            ["GraphReader.Domain.dll"] = "f4ee35b2728b48544916234d8d66bd599f9198a2392854ddb331acf06c733987",
            ["GraphReader.Export.dll"] = "bc0c1ed622206e90431aa761b8bf350bd2a1b46b61c414043df93b7da3da96ce",
            ["GraphReader.Imaging.dll"] = "40a762a577713b509d6f580a785af8a05359c3d18676ad0eba63d709f5fc678a",
            ["GraphReader.Inference.dll"] = "cf5c5bd62de7fcfd6e263e4a0783c66ca6273eac105873cc6b162cf8c3ed720f",
            ["GraphReader.Legends.dll"] = "834eda0631cf8bcd46dbba1c412b1a551b09f360d544cfd01779273c6fbf5f0a",
            ["GraphReader.Markers.dll"] = "c03247d5562074fed03a0e2dd865257a20abca28e02e62c685ef9fe04e566401",
            ["GraphReader.Ocr.dll"] = "b7e41198d087de589cc564514bf0f83ba2dcbc25a7395f3de3a4dcdf711a043f",
            ["GraphReader.Pdf.dll"] = "45166935bcc85a8f3c20f52ecafe5ce6827c25f6499588c79a85c9831ac99543",
            ["GraphReader.Phases.dll"] = "b1a163ea3a0c1e7d758926f83a2098c0e277a6f2526342d72131c4b03c28741c",
            ["GraphReader.SuperResolution.dll"] = "a805ef30d0e18f23471f6e242640645998b0db6ea82cb04a8db6f529389bfa92",
            ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
            ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
            ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
            ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
            ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
            ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
            ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static bool RankedReviewPinsReady() =>
        NativeRejectionPinsReady(RankedReviewAppExeHash, RankedReviewAppDllHash, RankedReviewRustHash,
            RankedReviewRuntimeLibraries) && RankedReviewTraceBaseCommit.Length == 40 &&
        RankedReviewTraceBaseCommit.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireRankedReviewPins()
    {
        if (!RankedReviewPinsReady()) throw new InvalidDataException();
    }

    private static object RankedReviewTraceIdentity() => new
    {
        kind = "unpackaged_ranked_native_review_source_runtime",
        base_source_commit = RankedReviewTraceBaseCommit,
        app_exe_sha256 = RankedReviewAppExeHash,
        app_dll_sha256 = RankedReviewAppDllHash,
        rust_sha256 = RankedReviewRustHash,
        runtime_files = RankedReviewRuntimeLibraries,
        model_sha256 = Program.ModelHash,
        manifest_sha256 = Program.ManifestHash,
        diagnostic_scope = "fixed_dev_automatic_geometry_literal_ocr_association_and_review_drafts",
    };

    private static void ValidateRankedReviewTraceFlags(bool sourcePostGrouping, bool geometryOnly,
        bool endpointTrace, bool incompatible)
    {
        if (!sourcePostGrouping || geometryOnly || !endpointTrace || incompatible) throw new InvalidDataException();
    }

    internal static object RankedReviewTraceSelfTest()
    {
        _ = EndpointTraceSelfTest();
        ValidateRankedReviewTraceFlags(true, false, true, false);
        foreach (var flags in new[]
        {
            (false, false, true, false),
            (true, true, true, false),
            (true, false, false, false),
            (true, false, true, true),
        })
        {
            try
            {
                ValidateRankedReviewTraceFlags(flags.Item1, flags.Item2, flags.Item3, flags.Item4);
                throw new InvalidOperationException();
            }
            catch (InvalidDataException) { }
        }

        bool missingPinsRejected = !NativeRejectionPinsReady("", "", "",
            new Dictionary<string, string>(StringComparer.Ordinal));
        if (!missingPinsRejected) throw new InvalidDataException();
        return new
        {
            schema = "graphreader.real-dev-ranked-review-trace-self-test.v1",
            incompatible_modes_rejected = true,
            missing_identity_pins_rejected_before_selector = true,
            profile_identity_ready = RankedReviewPinsReady(),
            private_reads = 0,
        };
    }
}
