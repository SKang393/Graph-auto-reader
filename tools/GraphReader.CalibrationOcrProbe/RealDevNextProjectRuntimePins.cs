// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal const string NextProjectRuntimePinsHash = "e134a9bbb478442499a76384a54d07a4b7ec68d7586dce15bc3b304fe3132407";
    internal const string NextRoutingRuntimePinsHash = "950cf8f66bd6d131435495d40521806e222bc16bc7e283bfc24cb15d32e4c47a";
    private const string NextProjectAppExeHash = "4762124654ee5357621bdd9e0acddd828c3aefcae96589722847ce606e0af700";
    private const string NextProjectAppDllHash = "b8401a6432d41b7ac7cb9c4437069392d7cc28f9ac138f63cf8ac198b467d410";
    private const string NextProjectRustHash = "08538da44ff7f680f951c7cbe08c806801b74d9c44cff982bf507ae0f995edca";
    private const string NextRoutingRustHash = "eeb2515a48546c9b50f9c8221f334c028a9dadce34cb1fa32ca7c5d477e87430";

    // Fixed before the one-project diagnostic. Historical profiles remain unchanged.
    private static readonly FrozenDictionary<string, string> NextProjectRuntimeLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GraphReader.App.dll"] = "b8401a6432d41b7ac7cb9c4437069392d7cc28f9ac138f63cf8ac198b467d410",
            ["GraphReader.Axis.dll"] = "8773a9f6330adecedbace5243de895948c3855aa1f0d616d74388f05fb52aa53",
            ["GraphReader.Domain.dll"] = "03b0a79cddbc500c5cf74fdcd42dbb30cef42965f899618c00f55fb58f7625a3",
            ["GraphReader.Export.dll"] = "8e45de51a19d0db761742683fa8a52ff10dcae096466fcfc4761aa595390ea1a",
            ["GraphReader.Imaging.dll"] = "232efc033ade629b49f9c322fa0119554d04288884b2b0f2f1ef1293920d1647",
            ["GraphReader.Inference.dll"] = "94caf20ce3b445f9cfbd398ebcf208ad599c07602ea50e76381e39a5e92d049f",
            ["GraphReader.Legends.dll"] = "45d67a76279357e603c6d94e81996de685f7645a2229a1a8abb63bc2b48bed14",
            ["GraphReader.Markers.dll"] = "f749a91fa3af0e005d31d8aa9a3c0dd248f7a93f8addfab3e6eec98876eced91",
            ["GraphReader.Ocr.dll"] = "a5437c316d80961bcaa3ba1048a94ddf4c8ac2288dc3b0a61af65e5ec73cfaf0",
            ["GraphReader.Pdf.dll"] = "d04613d66f834c63502ef55e12f9043408bd91d9094dbc325a8e1dcf72611570",
            ["GraphReader.Phases.dll"] = "a738f709622d1c1263f563ef7ccc600dd4c6a0d8fb8c59b9ded7c27d51ece691",
            ["GraphReader.SuperResolution.dll"] = "09e7cf7b531da1c01d59e1898ac10e5eb63458b22530362b9afe7970da056fe2",
            ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
            ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
            ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
            ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
            ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
            ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
            ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private sealed partial class Candidate
    {
        internal static Candidate VerifyNextProject(string app, string assets, string rust, CancellationToken token) =>
            VerifyPinned(app, assets, rust, NextProjectAppExeHash, NextProjectAppDllHash,
                NextProjectRustHash, NextProjectRuntimeLibraries, token, requirePrintedXReviewDraft: true);

        internal static Candidate VerifyNextProjectRouting(string app, string assets, string rust, CancellationToken token) =>
            VerifyPinned(app, assets, rust, NextProjectAppExeHash, NextProjectAppDllHash,
                NextRoutingRustHash, NextProjectRuntimeLibraries, token, requirePrintedXReviewDraft: true);
    }
}
