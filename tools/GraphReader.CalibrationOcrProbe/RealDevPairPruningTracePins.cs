// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string PairPruningTraceSchema = "graphreader.real-dev-pair-pruning-geometry-trace.v1";
    private const string PairPruningAxisDllHash = "6da1e2e71f1ba72205f0bf579c73c92a36ec24f66be57b39db2346ecb925aa2d";

    // The existing diagnostic App and all other runtime files are unchanged.
    // Only the separately compiled, binary-compatible Axis library is replaced.
    private static readonly FrozenDictionary<string, string> PairPruningRuntimeLibraries =
        NativeRejectionRuntimeLibraries.ToFrozenDictionary(pair => pair.Key,
            pair => pair.Key == "GraphReader.Axis.dll" ? PairPruningAxisDllHash : pair.Value,
            StringComparer.Ordinal);

    internal static object PairPruningTraceSelfTest()
    {
        _ = NativeRejectionTraceSelfTest();
        RequireNativeRejectionPins(NativeRejectionAppExeHash, NativeRejectionAppDllHash,
            NativeRejectionRustHash, PairPruningRuntimeLibraries);
        string[] changed = PairPruningRuntimeLibraries
            .Where(pair => NativeRejectionRuntimeLibraries[pair.Key] != pair.Value)
            .Select(pair => pair.Key).ToArray();
        if (!changed.SequenceEqual(["GraphReader.Axis.dll"])) throw new InvalidDataException();
        return new { schema = "graphreader.real-dev-pair-pruning-geometry-self-test.v1",
            changed_runtime_files = changed, geometry_only_accounting = true,
            missing_pins_rejected_before_selector = true, private_reads = 0 };
    }
}
