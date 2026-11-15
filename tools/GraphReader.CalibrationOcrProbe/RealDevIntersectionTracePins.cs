// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string IntersectionTraceSchema = "graphreader.real-dev-intersection-geometry-trace.v1";
    private const string IntersectionAppExeHash = "b4046b9f3a444779aa1054c6eeed31688ad01031ffa5e88030e405154ced05b8";
    private const string IntersectionAppDllHash = "81aa39ff9f882744f65d95cec36f24e35d454e9dc51efd58d19ee3ec1dce190c";
    private const string IntersectionAxisDllHash = "5c3003bc9049f16256cf058427a8378fd9539941d5256894e9d20f046ea46dcb";

    private static FrozenDictionary<string, string> IntersectionRuntimeLibraries => IntersectionRuntimeLibrariesLazy.Value;
    private static readonly Lazy<FrozenDictionary<string, string>> IntersectionRuntimeLibrariesLazy = new(CreateIntersectionRuntimeLibraries);

    private static FrozenDictionary<string, string> CreateIntersectionRuntimeLibraries() =>
        PairPruningRuntimeLibraries.ToFrozenDictionary(pair => pair.Key,
            pair => pair.Key switch
            {
                "GraphReader.App.dll" => IntersectionAppDllHash,
                "GraphReader.Axis.dll" => IntersectionAxisDllHash,
                _ => pair.Value,
            }, StringComparer.Ordinal);

    internal static object IntersectionTraceSelfTest()
    {
        _ = NativeRejectionTraceSelfTest();
        RequireNativeRejectionPins(IntersectionAppExeHash, IntersectionAppDllHash,
            NativeRejectionRustHash, IntersectionRuntimeLibraries);
        string[] changed = IntersectionRuntimeLibraries
            .Where(pair => PairPruningRuntimeLibraries[pair.Key] != pair.Value)
            .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
        if (!changed.SequenceEqual(["GraphReader.App.dll", "GraphReader.Axis.dll"])) throw new InvalidDataException();
        return new { schema = "graphreader.real-dev-intersection-geometry-self-test.v1",
            changed_runtime_files = changed, geometry_only_accounting = true,
            missing_pins_rejected_before_selector = true, private_reads = 0 };
    }
}
