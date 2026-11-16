// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using GraphReader.App.Services;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string ChoiceAvailabilitySource = "96f89b0418e605183ce1040ec57c8093cba6cc6f";
    private const string ChoiceAvailabilityManifest = "716eef1c5dd666c11b186b425eda6d65afdc4683ea54c27099382d70bfd00058";
    private const string ChoiceAvailabilityApp = "530cacafba075bd373895aa41f81835429f76c66de31aa5828b7a8dcc263de07";
    private const string ChoiceAvailabilityRust = "19e24a703907281341b183ffc0c16a6a7ab12bb1c4a2b4e97f41e16a76268347";
    private static readonly FrozenDictionary<string, string> ChoiceAvailabilityLibraries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GraphReader.App.dll"] = "8115f14a7c4747293f7605ad3d5118ae0e9ce9191ba201c728db325d52a20d8d",
        ["GraphReader.Axis.dll"] = "450ce9dd93523773cece0947e741813bdb94ec28fed6814706125241c58cb4ee",
        ["GraphReader.Domain.dll"] = "6b782afbdc35adce66a75f4c3f51997fbf122d94d343cebef7a20a783293a3db",
        ["GraphReader.Export.dll"] = "bfd476a5dc3a330802cd1366717e72d48bd27d8baab30ec7c0540037ac44d644",
        ["GraphReader.Imaging.dll"] = "75e4646dc5b55d156f7bdc4fdaa104cffdb82ad7f4c38d135e438629b6a573b5",
        ["GraphReader.Inference.dll"] = "fb00ea3a5ee76a2482aa4de2279fe167029c1f2ae301605511cde64fd5cba207",
        ["GraphReader.Legends.dll"] = "95271ef4a21900be94839eb0dcdc2cd292e44c2781af9f2dad498f7b95dc8019",
        ["GraphReader.Markers.dll"] = "3c1f1c63381fad0d2f5fd18f306807fd7b69a646484ed2f004d48255e8d40809",
        ["GraphReader.Ocr.dll"] = "ed44edf394097f1cda4504665486e8c17e5e01ddad7e69512c42c7a271a496c6",
        ["GraphReader.Pdf.dll"] = "16f26f2b3015f4905a3f52a897ef62d4144e9feacaabaf4bb941f3c5251977cb",
        ["GraphReader.Phases.dll"] = "6fb12e55b923378108fa864d5eb9d4c7f0f44c3e10bd5c85ae960bf70b7d055c",
        ["GraphReader.SuperResolution.dll"] = "5f1e8917d927453874e05c4895e0268ecd25719384717c01fbfaf9960fc60dba",
        ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
        ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
        ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
        ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
        ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
        ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
        ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static void RequireChoicePins(string app, string rust, IReadOnlyDictionary<string, string> libraries)
    {
        if (app != ChoiceAvailabilityApp || rust != ChoiceAvailabilityRust || libraries.Count != 20 ||
            ChoiceAvailabilityLibraries.Any(pair => !libraries.TryGetValue(pair.Key, out string? value) || value != pair.Value))
            throw new InvalidDataException("CHOICE_RUNTIME_PINS");
    }

    private sealed partial class Candidate
    {
        internal static Candidate VerifyChoiceAvailability(string app, string rust, CancellationToken token)
        {
            RequireChoicePins(ChoiceAvailabilityApp, ChoiceAvailabilityRust, ChoiceAvailabilityLibraries);
            var result = new Candidate();
            try
            {
                result.Pin(app, ChoiceAvailabilityApp, token); result.Pin(rust, ChoiceAvailabilityRust, token);
                string directory = Path.GetDirectoryName(app)!;
                foreach (var library in ChoiceAvailabilityLibraries)
                {
                    result.Pin(Path.Combine(directory, library.Key), library.Value, token);
                    result.Pin(Path.Combine(AppContext.BaseDirectory, library.Key), library.Value, token);
                    if (library.Key.StartsWith("GraphReader.", StringComparison.Ordinal) || library.Key is "Microsoft.ML.OnnxRuntime.dll" or "OpenCvSharp.dll")
                    {
                        Assembly assembly = Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(library.Key)));
                        result.Pin(assembly.Location, library.Value, token);
                    }
                    result.Files[library.Key] = library.Value;
                }
                OpenCvSharp.Cv2.SetNumThreads(1);
                if (OpenCvSharp.Cv2.GetNumThreads() != 1) throw new InvalidDataException("CHOICE_NATIVE_THREADS");
                using Process process = Process.GetCurrentProcess();
                string[] modules = process.Modules.Cast<ProcessModule>().Select(module => module.FileName)
                    .Where(path => Path.GetFileName(path).Equals("OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (modules.Length != 1) throw new InvalidDataException("CHOICE_NATIVE_MODULE");
                result.Pin(modules[0], ChoiceAvailabilityLibraries["OpenCvSharpExtern.dll"], token);
                result.Files["diagnostic_adapter"] = result.Pin(typeof(RealDevCalibrationCheck).Assembly.Location, null, token);
                return result;
            }
            catch { result.Dispose(); throw; }
        }
    }
}