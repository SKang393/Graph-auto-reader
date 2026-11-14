// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    // Exact hosted 0.4.50 bytes from source 99864561c4f4e18e6e39b34e258a0ed7434ed623.
    // These pins are reachable only through explicit 450 diagnostic CLI modes.
    private const string App450DllHash = "49e375f3110f62c386245210d091010f3ccd722216f26ca440c6958afdf9855a";
    private const string App450ExeHash = "da892f5a59faddfe24a42bf48ecca1548f6b2851921383d91c0f133421bc885e";
    private const string Rust450Hash = "efed45d5c1de473599cf94ace56aa1e1b3bfd40228289db23ecce755b66e9794";
    private static readonly FrozenDictionary<string, string> Delivered450RuntimeLibraries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GraphReader.App.dll"] = App450DllHash,
        ["GraphReader.Axis.dll"] = "c5f6e5544c79eed7e0f90e9aeb0bab1d3be67aea0694272ccccc449e2499f2d4",
        ["GraphReader.Domain.dll"] = "8398ea83573767445e9a8ed04d27cf967d85e1d2ef8d81b68c375122eb2c93db",
        ["GraphReader.Export.dll"] = "47613ec2bd9674b1a3fb92b5ccd62fb9222e9275d6dddc997e4a00f5b1af8837",
        ["GraphReader.Imaging.dll"] = "2e38c909cec00cb54154ed0cb4af681e90eba718b2c0ad935bfa683b7502f67c",
        ["GraphReader.Inference.dll"] = "495c4d5b397572cb27f8f964000f09956df8f774190a39fa877ebbee86e42114",
        ["GraphReader.Legends.dll"] = "f7c6476de071e4462bb0a93064b281dcc9b83ea6d85736b0238be2abf2dcb560",
        ["GraphReader.Markers.dll"] = "01d5dd6758aeec46e021ef643f2ef065f6cc2d46f9d23aa61ab864838b075754",
        ["GraphReader.Ocr.dll"] = "56f5cbcf49a707e4480ed9132f002586142981d494cb98fc4f176ce0a96cd687",
        ["GraphReader.Pdf.dll"] = "94183f6bbaae1560b6ea544fe693a1855dfb73d4a56f0725b6d2371b69101c17",
        ["GraphReader.Phases.dll"] = "e08bb783e1333a987c2fa581f339a4a962f0253619f5a3bc6d1a20f7e7aa455a",
        ["GraphReader.SuperResolution.dll"] = "e5b12bcb05157884ac5379d02cffd4f772a4a16f2c24e82c45f6ae5cdff05f93",
        ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
        ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
        ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
        ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
        ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
        ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
        ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
        ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        ["vcruntime140_cor3.dll"] = "d5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066",
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
