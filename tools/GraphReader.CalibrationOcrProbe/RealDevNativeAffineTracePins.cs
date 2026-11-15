// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Collections.Frozen;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal const string NativeAffineTraceSchema = "graphreader.real-dev-native-affine-geometry-trace.v1";
    private const string NativeAffineExeHash = "6cf05cdbe23a5809051674222be0513377e63a05ec3e9282c4f0db5a1a19834d";
    private const string NativeAffineDllHash = "35f947a2daa5add97d7a6a0b6e02d5298c8213a19063fd0706526a2d021836d9";
    private const string NativeAffineRustHash = "2cc6f263a00544243291f2605031c4df7e147175bd345480bd0a44f92fde0b7a";
    private static readonly FrozenDictionary<string, string> NativeAffineRuntimeLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GraphReader.App.dll"] = "35f947a2daa5add97d7a6a0b6e02d5298c8213a19063fd0706526a2d021836d9",
            ["GraphReader.Axis.dll"] = "7713bc1903fee9ab379420075e6052eebf7e96d8b918ba704aad65ecaa8bdbd0",
            ["GraphReader.Domain.dll"] = "0bc575c6f1aec756ce22f8dd77bebc8910b041b110b0ad83e2b5cd25c7246e5f",
            ["GraphReader.Export.dll"] = "ee3c684e243bbd4b2e904edb72a0c1ed2a724be3f450624dd4eb83ece96f4225",
            ["GraphReader.Imaging.dll"] = "dc5720320fcbbd89fb449199a6963b6db74868d8916ff399c7c8afe8c04ab8d3",
            ["GraphReader.Inference.dll"] = "c7fae7440db7eae82c6b85f5d06d48c726ae96c804524085ac71af562327bec5",
            ["GraphReader.Legends.dll"] = "752e35919a40d4ac25df78ff83e76f8b882bea006fea66dab539ec09ad2100e1",
            ["GraphReader.Markers.dll"] = "0afa8370c0c771c8fcac2bc81c5c6ff629bbe2690a254f0de5034e766894d12e",
            ["GraphReader.Ocr.dll"] = "ca7fa4ce9780c49ece06ceb92489ca959b440a6a2b7e35f711e22eda2f0f6433",
            ["GraphReader.Pdf.dll"] = "b28e2a01916fd9c71b78297dcf3717ed1064798bbd687ed3ae315d8c18230859",
            ["GraphReader.Phases.dll"] = "bee729fbaf51df1bdd8259bd11b583656a5a7d8de46f664002b99de31f2d893b",
            ["GraphReader.SuperResolution.dll"] = "b1f8cdf4e153a42e038f92f3723c4ddea852d5f99a8eac4068fa670b50d6edb9",
            ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
            ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
            ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
            ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
            ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
            ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
            ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object NativeAffineTraceIdentity() => new
    {
        kind = "unpackaged_native_affine_geometry_source_runtime",
        base_source_commit = "28c7fbb0ce6b639c938160387c20435849e66557", source_diff = "native_affine_review_proposal",
        app_exe_sha256 = NativeAffineExeHash, app_dll_sha256 = NativeAffineDllHash,
        rust_sha256 = NativeAffineRustHash,
        runtime_pins_file_sha256 = "8d92fce739435ba03671d5dcf925685551005bd08c2fb142dbad2f0ce728edf2",
        runtime_files = NativeAffineRuntimeLibraries,
        baseline = "interval_cluster_fixed12_rust_2cc", geometry_only = true,
    };
    private sealed partial class Candidate
    {
        internal static Candidate VerifyNativeAffine(string app, string assets, string rust, CancellationToken token)
        {
            RequireNativeRejectionPins(NativeAffineExeHash, NativeAffineDllHash, NativeAffineRustHash, NativeAffineRuntimeLibraries);
            return VerifyPinned(app, assets, rust, NativeAffineExeHash, NativeAffineDllHash, NativeAffineRustHash,
                NativeAffineRuntimeLibraries, token, requirePrintedXReviewDraft: true);
        }
    }

    internal static object NativeAffineTraceSelfTest()
    {
        CheckNativeAffineGeometryControl();
        try { RequireNativeRejectionPins("", NativeAffineDllHash, NativeAffineRustHash, NativeAffineRuntimeLibraries); throw new InvalidOperationException(); }
        catch (InvalidDataException) { }
        if (NativeAffineRuntimeLibraries.Count != 20 || NativeAffineRuntimeLibraries["GraphReader.App.dll"] != NativeAffineDllHash ||
            NativeAffineRuntimeLibraries["OpenCvSharpExtern.dll"] != "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31") throw new InvalidDataException();
        var counts = NewTracePanelStatusCounts(geometryOnly: true, nativeAffine: true);
        CountTracePanelStatus(counts, new TracePanelOutcome("AFFINE_GEOMETRY_RETURNED", new { }));
        if (counts["AFFINE_GEOMETRY_RETURNED"] != 1 || counts["GEOMETRY_RETURNED"] != 0) throw new InvalidDataException();
        return new { schema = "graphreader.real-dev-native-affine-geometry-trace-self-test.v1", valid_affine_checked = true,
            malformed_affine_refused = true, missing_pins_refused = true, runtime_pin_count = 20,
            affine_counting_checked = true, historical_profiles_unchanged = true, private_reads = 0 };
    }


    private static bool NativeAffineTraceAccepted(RustAxisProposalResult result, int width, int height) =>
        result.AffineGeometry is not null && OpenCvAxisProposalService.Valid(result, width, height);

    private static void CheckNativeAffineGeometryControl()
    {
        var frame = new RustAffineAxisGeometry(new(10, 90), new(80, 85), new(15, 10), .7);
        var affine = new RustAxisProposalResult(null, null)
        {
            Provider = "opencv-review", ReviewReasons = ["native_affine_geometry"], AffineGeometry = frame,
        };
        if (!NativeAffineTraceAccepted(affine, 100, 100) ||
            NativeAffineTraceAccepted(affine with { ErrorCode = "OPENCV_AXIS_AMBIGUOUS" }, 100, 100) ||
            NativeAffineTraceAccepted(affine with { Geometry = new(10, 90, 80, 10, .7) }, 100, 100) ||
            NativeAffineTraceAccepted(affine with { Provider = "rust" }, 100, 100) ||
            NativeAffineTraceAccepted(affine with { AffineGeometry = frame with { XEnd = new(100, 85) } }, 100, 100))
            throw new InvalidDataException();
    }
}
