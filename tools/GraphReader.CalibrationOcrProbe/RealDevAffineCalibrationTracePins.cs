// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using GraphReader.App.Services;
namespace GraphReader.CalibrationOcrProbe;
internal static partial class RealDevCalibrationCheck
{
    internal const string AffineCalibrationTraceSchema = "graphreader.real-dev-affine-calibration-trace.v1";
    private const string AffineCalibrationExeHash = "9c3057f6598b0e5d963719119c9f45b5372f67e2806f82d98debefd8f3ef97a6";
    private const string AffineCalibrationDllHash = "144fdf02d0223eeb662a1b00a67a5e203e3057710334ed64f0f4521927a557fc";
    private const string AffineCalibrationRustHash = "2cc6f263a00544243291f2605031c4df7e147175bd345480bd0a44f92fde0b7a";
    private static readonly FrozenDictionary<string, string> AffineCalibrationRuntimeLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GraphReader.App.dll"] = "144fdf02d0223eeb662a1b00a67a5e203e3057710334ed64f0f4521927a557fc",
            ["GraphReader.Axis.dll"] = "918abeee242b2a72fbc6076960a6a72e8442580dd025d7e530b9bc975ca24128",
            ["GraphReader.Domain.dll"] = "a2448948765c3f9dab9d5f0fd042b4e73843d4a6c63965816a00a5e24d0fd7f9",
            ["GraphReader.Export.dll"] = "de0827c0fd78b86e37e89f8286afecb6b73e70be7a977f8a6e17f60e4387ae3e",
            ["GraphReader.Imaging.dll"] = "2e3af193741cbb9d24143f77c4555f723aeb9fd97541062a9157473478131e64",
            ["GraphReader.Inference.dll"] = "3e3dbdd47a674f09e12b5d36b0bb0e372e08a16321adc56418a58cda5d080d7c",
            ["GraphReader.Legends.dll"] = "6a4751afd3caa9fdc785e6ecf1022c44877b2e3f64b2450aa6785d52f49d403d",
            ["GraphReader.Markers.dll"] = "b428dda2fa0665e9cbfec2a2ddab91845f621dba0b0bd2f9f0713aa2eb6e5f12",
            ["GraphReader.Ocr.dll"] = "130304fd3d1fc0dd74d6e57e3d1e33f95dd0a69da1c41ca83310994546d3314a",
            ["GraphReader.Pdf.dll"] = "a208c5b56f6c4a350158e76a2edd78e4ef938f9715107246aec7a388537e7799",
            ["GraphReader.Phases.dll"] = "48626a442975f4a20316a3d44e08f78c07f93a84817e6a45f40835cc64ac8059",
            ["GraphReader.SuperResolution.dll"] = "47f488e60743fd9199cf9631524790bb14ffdb3e4f013a72128544488e158fbb",
            ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
            ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
            ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
            ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
            ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
            ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
            ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        }.ToFrozenDictionary(StringComparer.Ordinal);
    private sealed partial class Candidate
    {
        internal static Candidate VerifyAffineCalibration(string app, string assets, string rust, CancellationToken token)
        {
            RequireNativeRejectionPins(AffineCalibrationExeHash, AffineCalibrationDllHash, AffineCalibrationRustHash, AffineCalibrationRuntimeLibraries);
            return VerifyPinned(app, assets, rust, AffineCalibrationExeHash, AffineCalibrationDllHash, AffineCalibrationRustHash, AffineCalibrationRuntimeLibraries, token, requirePrintedXReviewDraft: true);
        }
    }
}
