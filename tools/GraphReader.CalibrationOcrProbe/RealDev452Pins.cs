// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    // Verified 0.4.52 package, source 68781bcce22cf925bff2f99b21c139b622117c06.
    private const string App452ExeHash = "c7979e7d88482fb239e6655d4939aea15755d605a472730c116776cda1da8e27";
    private const string App452DllHash = "c04429796bc17ee3f44cd1b7e548e3c524a3733910af2b2dc33440ef4d8e33e1";
    private const string Rust452Hash = "efed45d5c1de473599cf94ace56aa1e1b3bfd40228289db23ecce755b66e9794";
    private static readonly FrozenDictionary<string, string> Delivered452RuntimeLibraries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GraphReader.App.dll"] = "c04429796bc17ee3f44cd1b7e548e3c524a3733910af2b2dc33440ef4d8e33e1",
        ["GraphReader.Axis.dll"] = "09aae3481863b065d25c0604f13283b06f21355327fb8fbf34952b3664091873",
        ["GraphReader.Domain.dll"] = "67baeef51881ae35952f45fd4edfcc863968bfe8bf495007c204a9aad32d9860",
        ["GraphReader.Export.dll"] = "b13bcf918f50708f3b201b49cd1cbabafd626f81a98aa5d34adba2fe21bf9dd2",
        ["GraphReader.Imaging.dll"] = "c802cfbdccd47cb23dde76938ddfe6ae8f79ffb0ca63af5c450d9785d63abc4e",
        ["GraphReader.Inference.dll"] = "dcb265da05a04d86646c7a10ca05d9672727b00b31c671c6b0d1d6bd161951d8",
        ["GraphReader.Legends.dll"] = "f2d5b5a6b93cb1b9806bf100140970275374715716d36187e944b6f94c901580",
        ["GraphReader.Markers.dll"] = "86d05017e40d8798e3ede027c462a35713310c05da279122bd699b12a8a1142e",
        ["GraphReader.Ocr.dll"] = "8fe0c4ee9573f8b21e959284e35588a47eaa31b680f043b5972c81f7e9ecdc7d",
        ["GraphReader.Pdf.dll"] = "a19f43f1e042ad152ae3337247631517d23639f996a029596363fca6a4fad757",
        ["GraphReader.Phases.dll"] = "0b9dcbc4ff38025aba0b76e8ff1a4724e6f760c17c27845d4b1f1e5fbca9d6ac",
        ["GraphReader.SuperResolution.dll"] = "2f43542209a2c30afe004b3ea414756de4793fd385ad692af71c1f3506dc44ca",
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
