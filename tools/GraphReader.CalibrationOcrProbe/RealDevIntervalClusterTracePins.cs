// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal const string IntervalClusterTraceSchema = "graphreader.real-dev-interval-cluster-trace.v1";
    private const string IntervalClusterBaseCommit = "1fe50b58b07ef9be7d23f8252016ad86ce7ee32e";
    private const string IntervalClusterAppExeHash = "e21d8dd49fb85c8d5563c9199c00286d28e1269a13a2db3b4533494c9a478b4e";
    private const string IntervalClusterAppDllHash = "7c8df79baaaad3a4b3992e9b97d4fb8843cd9eca494086a8ee9ac39a8d1f056b";
    private const string IntervalClusterRustHash = "2cc6f263a00544243291f2605031c4df7e147175bd345480bd0a44f92fde0b7a";
    private const string IntervalClusterRuntimePinsFileHash = "60007b67f65b98a51ee1710234cc71a1b421c519cd553f237e21ab3d31131f04";

    // Frozen from artifacts/interval-cluster-containment-check/runtime-pins.json. Keep the
    // profile separate so earlier trace identities remain immutable.
    private static readonly FrozenDictionary<string, string> IntervalClusterRuntimeLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GraphReader.App.dll"] = "7c8df79baaaad3a4b3992e9b97d4fb8843cd9eca494086a8ee9ac39a8d1f056b",
            ["GraphReader.Axis.dll"] = "d946441adcef59a717e11d12df740c5300f369a7e873b37344bc2ab6a647df8d",
            ["GraphReader.Domain.dll"] = "ac26d3409bec3dc832a0708b2d3d5c5e91fac8b88e8d5c2c03f2c64c0388ad75",
            ["GraphReader.Export.dll"] = "2d84b15a28a62bd5953eeb68c835fd73c69698c8b4ce4056e0f302a1c343810e",
            ["GraphReader.Imaging.dll"] = "4c8cf4534a9857067c8998eadc17457a5b75533f82b2aaba9b81c29d864f37e2",
            ["GraphReader.Inference.dll"] = "25bfdcf59d3dd97ff72e2cbf9f42b8ab1c64bdfa92b5a902f59275b3a0f09cd1",
            ["GraphReader.Legends.dll"] = "8fa02e83f731d9092f0dba9af913de3362a9cef6a189941bfc1af2b62725e52d",
            ["GraphReader.Markers.dll"] = "6b03654ae353263f7332b166a9066a0d41882befbff011781c228818424b2d88",
            ["GraphReader.Ocr.dll"] = "f533f0b5ecdf38b6943ddfd1b6de03d8249fff49e9b38e689358887ad796e09b",
            ["GraphReader.Pdf.dll"] = "93f2e4bacc8e9b05a0b27a226942da9aa06c469b2065983258910961628f5353",
            ["GraphReader.Phases.dll"] = "dbbc020fcfddccfeebb0238fd9b968c4d167f489d8fd225b07c59c93db29bc94",
            ["GraphReader.SuperResolution.dll"] = "a8ffc4f0ade7cc2a6f8918c99f96893c62ba6988f75fcbb7b0399e4d13075a55",
            ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
            ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
            ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
            ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
            ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
            ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
            ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object IntervalClusterTraceIdentity() => new
    {
        kind = "unpackaged_interval_cluster_source_runtime",
        base_source_commit = IntervalClusterBaseCommit,
        app_exe_sha256 = IntervalClusterAppExeHash,
        app_dll_sha256 = IntervalClusterAppDllHash,
        rust_sha256 = IntervalClusterRustHash,
        runtime_pins_file_sha256 = IntervalClusterRuntimePinsFileHash,
        runtime_files = IntervalClusterRuntimeLibraries,
        model_sha256 = Program.ModelHash,
        manifest_sha256 = Program.ManifestHash,
        diagnostic_scope = "fixed12_automatic_geometry_literal_ocr_exact_box_ticks_and_review_drafts",
        binding = "production_helper_on_transient_suggestion_not_workspace_currentness",
        interval_x_provenance = "review_only_estimated_positions_with_ambiguous_cluster_blocking",
        geometry_search = "grid_subset_completion",
        normal_native_pair_capacity = 256,
        explicit_candidate_pair_capacity = 128
    };

    private sealed partial class Candidate
    {
        internal static Candidate VerifyIntervalCluster(string app, string assets, string rust, CancellationToken token)
        {
            RequireNativeRejectionPins(IntervalClusterAppExeHash, IntervalClusterAppDllHash,
                IntervalClusterRustHash, IntervalClusterRuntimeLibraries);
            return VerifyPinned(app, assets, rust, IntervalClusterAppExeHash, IntervalClusterAppDllHash,
                IntervalClusterRustHash, IntervalClusterRuntimeLibraries, token, requirePrintedXReviewDraft: true);
        }
    }

    internal static object IntervalClusterTraceSelfTest()
    {
        _ = IntervalLabelTraceSelfTest();
        try
        {
            RequireNativeRejectionPins("", IntervalClusterAppDllHash, IntervalClusterRustHash,
                IntervalClusterRuntimeLibraries);
            throw new InvalidOperationException();
        }
        catch (InvalidDataException) { }

        try
        {
            RequireNativeRejectionPins("not-a-sha256", IntervalClusterAppDllHash, IntervalClusterRustHash,
                IntervalClusterRuntimeLibraries);
            throw new InvalidOperationException();
        }
        catch (InvalidDataException) { }

        try
        {
            var missingLibrary = IntervalClusterRuntimeLibraries.Where(pair => pair.Key != "OpenCvSharpExtern.dll")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            RequireNativeRejectionPins(IntervalClusterAppExeHash, IntervalClusterAppDllHash, IntervalClusterRustHash, missingLibrary);
            throw new InvalidOperationException();
        }
        catch (InvalidDataException) { }

        if (IntervalClusterRuntimeLibraries.Count != 20 ||
            IntervalClusterRuntimeLibraries["GraphReader.App.dll"] != IntervalClusterAppDllHash ||
            !IntervalClusterRuntimeLibraries.TryGetValue("OpenCvSharpExtern.dll", out string? openCv) ||
            openCv != "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31")
            throw new InvalidDataException();

        return new
        {
            schema = "graphreader.real-dev-interval-cluster-trace-self-test.v1",
            distinct_profile = true,
            runtime_pin_count = IntervalClusterRuntimeLibraries.Count,
            historical_profiles_unchanged = true,
            private_reads = 0
        };
    }
}
