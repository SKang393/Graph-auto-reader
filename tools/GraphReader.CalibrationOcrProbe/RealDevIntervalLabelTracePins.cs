// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal const string IntervalLabelTraceSchema = "graphreader.real-dev-interval-label-trace.v1";
    private const string IntervalLabelBaseCommit = "11196b00d4afa18c208700ddae456d4236c74b40";
    private const string IntervalLabelAppExeHash = "528db47ea435ca63834a8175bd0b660202742e211b3e7983bad1d6e4cd4b3b92";
    private const string IntervalLabelAppDllHash = "d932a86015c0fe3a1d8b50a15ec5f5d8ceebea5d36e9f8b2f3f2b25b0a120f43";
    private const string IntervalLabelRustHash = "2cc6f263a00544243291f2605031c4df7e147175bd345480bd0a44f92fde0b7a";
    private const string IntervalLabelRuntimePinsFileHash = "01614dd862d032225d715e21e7540725d877b50ab4e2463531bc2027029e3115";

    // Frozen from artifacts/interval-label-grouping-check/runtime-pins.json. Keep the
    // profile separate so earlier trace identities remain immutable.
    private static readonly FrozenDictionary<string, string> IntervalLabelRuntimeLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GraphReader.App.dll"] = "d932a86015c0fe3a1d8b50a15ec5f5d8ceebea5d36e9f8b2f3f2b25b0a120f43",
            ["GraphReader.Axis.dll"] = "47073b055d1d3e37c0eae4c273aa15d8d72d62e874d60d4c9f80bc05613e91a6",
            ["GraphReader.Domain.dll"] = "4228e7a54688e3a4ad47b91997bc6c5f4bda1ec3f55e265de7f78b81e7af3446",
            ["GraphReader.Export.dll"] = "2cb6731e467101e3c5dd785e9bc7ab7bb74ce9bec56f12b26418ebefa29593b0",
            ["GraphReader.Imaging.dll"] = "3e1e8771bb32a028df229bcfadb085b5b2f7b88518a75fb845cf8351cfb2f59c",
            ["GraphReader.Inference.dll"] = "1df69fac52c862b24e05b9b28a8fd30640e28439dcf5d31299ce889f795a4f11",
            ["GraphReader.Legends.dll"] = "8b0b138d089004d12bb170d45f1cb2c793634fc68ef5dee57c33ef15e33d3286",
            ["GraphReader.Markers.dll"] = "6775018b3bd79f79fae7a553f9893910c5f522f1fb2a8fa148e8b32615621694",
            ["GraphReader.Ocr.dll"] = "a9e3eb452c7a4f399ab7e0b4deb6d3808f3856b577cae2c6c745361913c1334d",
            ["GraphReader.Pdf.dll"] = "d3665bdbcf720f9592e66f903ee608998273d6effb4e2bbc5c4e217465f14e31",
            ["GraphReader.Phases.dll"] = "915a8bde15e7866384d0ab5e073ee77ed6c609af7b798d15a1d487e2fb996598",
            ["GraphReader.SuperResolution.dll"] = "64b5c8ba67734ee4a5c2f370d28da0121f27fcb108517a69349351e45cfb674f",
            ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
            ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
            ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
            ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
            ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
            ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
            ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object IntervalLabelTraceIdentity() => new
    {
        kind = "unpackaged_interval_label_source_runtime",
        base_source_commit = IntervalLabelBaseCommit,
        app_exe_sha256 = IntervalLabelAppExeHash,
        app_dll_sha256 = IntervalLabelAppDllHash,
        rust_sha256 = IntervalLabelRustHash,
        runtime_pins_file_sha256 = IntervalLabelRuntimePinsFileHash,
        runtime_files = IntervalLabelRuntimeLibraries,
        model_sha256 = Program.ModelHash,
        manifest_sha256 = Program.ManifestHash,
        diagnostic_scope = "fixed12_automatic_geometry_literal_ocr_exact_box_ticks_and_review_drafts",
        binding = "production_helper_on_transient_suggestion_not_workspace_currentness",
        interval_x_provenance = "review_only_estimated_positions",
        normal_native_pair_capacity = 256,
        explicit_candidate_pair_capacity = 128
    };

    private sealed partial class Candidate
    {
        internal static Candidate VerifyIntervalLabel(string app, string assets, string rust, CancellationToken token)
        {
            RequireNativeRejectionPins(IntervalLabelAppExeHash, IntervalLabelAppDllHash,
                IntervalLabelRustHash, IntervalLabelRuntimeLibraries);
            return VerifyPinned(app, assets, rust, IntervalLabelAppExeHash, IntervalLabelAppDllHash,
                IntervalLabelRustHash, IntervalLabelRuntimeLibraries, token, requirePrintedXReviewDraft: true);
        }
    }

    internal static object IntervalLabelTraceSelfTest()
    {
        _ = ExactBoxTickTraceSelfTest();
        try
        {
            RequireNativeRejectionPins("", IntervalLabelAppDllHash, IntervalLabelRustHash,
                IntervalLabelRuntimeLibraries);
            throw new InvalidOperationException();
        }
        catch (InvalidDataException) { }

        try
        {
            RequireNativeRejectionPins("not-a-sha256", IntervalLabelAppDllHash, IntervalLabelRustHash,
                IntervalLabelRuntimeLibraries);
            throw new InvalidOperationException();
        }
        catch (InvalidDataException) { }

        try
        {
            var missingLibrary = IntervalLabelRuntimeLibraries.Where(pair => pair.Key != "OpenCvSharpExtern.dll")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            RequireNativeRejectionPins(IntervalLabelAppExeHash, IntervalLabelAppDllHash, IntervalLabelRustHash, missingLibrary);
            throw new InvalidOperationException();
        }
        catch (InvalidDataException) { }

        if (IntervalLabelRuntimeLibraries.Count != 20 ||
            IntervalLabelRuntimeLibraries["GraphReader.App.dll"] != IntervalLabelAppDllHash ||
            !IntervalLabelRuntimeLibraries.TryGetValue("OpenCvSharpExtern.dll", out string? openCv) ||
            openCv != "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31")
            throw new InvalidDataException();

        return new
        {
            schema = "graphreader.real-dev-interval-label-trace-self-test.v1",
            distinct_profile = true,
            runtime_pin_count = IntervalLabelRuntimeLibraries.Count,
            historical_profiles_unchanged = true,
            private_reads = 0
        };
    }
}
