// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    // Exact retained 0.4.51 package, source e54917917d1acdc3aed2c556f3d2faf0bcbff253.
    // Reachable only through the explicit 451 prepared diagnostic CLI mode.
    private const string App451DllHash = "5be01923609dfc144f407db353bc7097f47c773bc44140b87af04d387ce041ff";
    private const string App451ExeHash = "144d4cfab07d81429de6366a76a211fe515fa8227578b9c857ed76e5cca6efed";
    private const string Rust451Hash = "efed45d5c1de473599cf94ace56aa1e1b3bfd40228289db23ecce755b66e9794";
    private static readonly FrozenDictionary<string, string> Delivered451RuntimeLibraries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GraphReader.App.dll"] = App451DllHash,
        ["GraphReader.Axis.dll"] = "b44184f9c4c14ecd1bb51d2671951edbfc914eeb9313465c57327e8c30ec854a",
        ["GraphReader.Domain.dll"] = "74f07c416c9a5d1fe081f7c2190a8dc36e94981427de75300addc8398c40fe42",
        ["GraphReader.Export.dll"] = "7282c77d7d3a3d395cbaf0cf83133bcb57c419acf6bf0b7aec0dd9c208e1197d",
        ["GraphReader.Imaging.dll"] = "a8ca51581ad8e8533658707afedcb03448a61931a53bf584553f0485ac286339",
        ["GraphReader.Inference.dll"] = "c36f486b4b539803973b1b385df340f00f5d6e3000939d491e82a577eb7fe551",
        ["GraphReader.Legends.dll"] = "bb863ed89ca29a26b2595ea7e668baa587c1bffe5b26ce538c03e1ea6fe64713",
        ["GraphReader.Markers.dll"] = "6a483ef7817c0e6e7cc7744a3a1f24822c92eca3d131fa9d8328193902317688",
        ["GraphReader.Ocr.dll"] = "1abf49cbb93f068307ec1e3f6bd1e90be5d5ee9949d72ca3bdf501d25dc8a43d",
        ["GraphReader.Pdf.dll"] = "54c6bba24e6f5e9e8ba1e796766c767ce20c6b475e53f5ca0349504bd0bc37eb",
        ["GraphReader.Phases.dll"] = "6a4ee7769a22963a20f4af3c61b0cf751bb73a49bc5160205a7d0ce65586a29f",
        ["GraphReader.SuperResolution.dll"] = "cf49f07f696935fee9b933845c38355981c2ea3c1b9cf3f001adae80b51c2188",
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
