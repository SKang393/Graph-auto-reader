// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string NativeRejectionTraceSchema = "graphreader.real-dev-native-rejection-geometry-trace.v1";
    // Source runtime built for typed native rejection diagnostics; not the delivered 452 package.
    private const string NativeRejectionAppExeHash = "d3744c77b22b81665c7b8f9368cef04bedf14ad7683efb8b46da065d858f0930";
    private const string NativeRejectionAppDllHash = "a1666193c979641c1b268e76d6cf4477d8e3b9f7e0df65cdbe8e69451fd7b418";
    private const string NativeRejectionRustHash = "cfc8e59a08b4aeaad84cc4742aa8a8c2f211a8f575f0d6bcfb6beb5f5a1880fd";
    private static readonly FrozenDictionary<string, string> NativeRejectionRuntimeLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GraphReader.App.dll"] = "a1666193c979641c1b268e76d6cf4477d8e3b9f7e0df65cdbe8e69451fd7b418",
            ["GraphReader.Axis.dll"] = "819310adfd593feaf0658ae76db70b2fefcd4d6cd285739576d519e7fc20d4eb",
            ["GraphReader.Domain.dll"] = "a41ac359018304c41fd36aaa7611625fe6f34453c38e6488f04366e8d4995d72",
            ["GraphReader.Export.dll"] = "06d7cb847057daf9c466e411fdd4c6b6b0dc071d0f187c75377c04c90d004a36",
            ["GraphReader.Imaging.dll"] = "4d506ffe48e03dbc617a6908ef8856b1b391cad348b2a556458f951c8559ecc1",
            ["GraphReader.Inference.dll"] = "98b1b313e6acf1eefd5dd5a029128cc7e911496bff6858d161be7a49ca332d25",
            ["GraphReader.Legends.dll"] = "324288d11d72fa751e97e5524b4a65f9810b677d8c5f2ef010b7183932fe7aea",
            ["GraphReader.Markers.dll"] = "558f65a82c719b58d7a931e027c9dcf883750a3dd9821a488799f9476fdc1216",
            ["GraphReader.Ocr.dll"] = "35d4cc0efe717c1d0a091663ca0e729224d6f14fc77780f1781d3ec2d8592a00",
            ["GraphReader.Pdf.dll"] = "d75c4f3f5d0b7d4591db1c4c6edcd05021d5f1dc96521caf3a10e828ef568bde",
            ["GraphReader.Phases.dll"] = "762412f747c82fbf1bd94c988c651530d971519d1b6c82547f784301f46570e3",
            ["GraphReader.SuperResolution.dll"] = "45f2b0a088278eb52ccf3c170c2569a277a64a5e0ac1aa4f9a2fa53f7d8741d9",
            ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
            ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
            ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
            ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
            ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
            ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
            ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static bool NativeRejectionPinsReady(string exe, string dll, string rust, IReadOnlyDictionary<string, string> libraries)
    {
        static bool Sha(string value) => value.Length == 64 && value.Any(c => c != '0') &&
            value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        return Sha(exe) && Sha(dll) && Sha(rust) && libraries.Count == PostGroupingRuntimeLibraries.Count &&
            PostGroupingRuntimeLibraries.Keys.All(key => libraries.TryGetValue(key, out string? value) && value is not null && Sha(value)) &&
            libraries.TryGetValue("GraphReader.App.dll", out string? appHash) && appHash == dll;
    }

    private static void RequireNativeRejectionPins(string exe, string dll, string rust, IReadOnlyDictionary<string, string> libraries)
    {
        if (!NativeRejectionPinsReady(exe, dll, rust, libraries)) throw new InvalidDataException();
    }
}
