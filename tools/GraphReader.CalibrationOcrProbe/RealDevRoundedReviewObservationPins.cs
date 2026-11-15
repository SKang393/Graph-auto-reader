// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;
using GraphReader.App.Integration.NativeAxis;
using GraphReader.App.Services;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string RoundedReviewObservationSchema = "graphreader.real-dev-rounded-review-observation-trace.v1";
    // Frozen unpackaged rounded and duplicate review runtime, independently hashed.
    private const string RoundedReviewObservationExeHash = "f2b147da1520b78ef53a2d30d7d81c3601354b6a74b5799e38826a63fff2b16e";
    private const string RoundedReviewObservationDllHash = "8e25f20587f4390587159d4c9e09103a2277108af75eb1527bb068a8392f7c60";
    private const string RoundedReviewObservationRustHash = "9c5ac19abc3b08632d82b5590a9e59f2794d94f92f345fbc70741e181808c0d9";
    private static readonly FrozenDictionary<string, string> RoundedReviewObservationLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GraphReader.App.dll"] = "8e25f20587f4390587159d4c9e09103a2277108af75eb1527bb068a8392f7c60",
            ["GraphReader.Axis.dll"] = "f9152a45c9ee00d951973d5318db32c0ecd0335356e48c96b8abf8db49a4cb2d",
            ["GraphReader.Domain.dll"] = "e80934db49296e4dde52424eacbc5b1052e6e15e2aded4a2b7ba2ddc10c53a48",
            ["GraphReader.Export.dll"] = "d018420a8c50758e01af00d78a7692e0ffe6a2f3770ed72740d4a7ae4176d278",
            ["GraphReader.Imaging.dll"] = "a1b7c8f5c7cd9d5a6e8f9d39bdb755aaa58d0fe400a3181a6c319314020a8b1c",
            ["GraphReader.Inference.dll"] = "1f73e2da4b0ea299a035ee7f9e93a4db055b3e618f8c7fed2893f454c756f8e1",
            ["GraphReader.Legends.dll"] = "cc594ae7c94bfa640deaffcce6ee2524013a6129609483940de22a4644df25ca",
            ["GraphReader.Markers.dll"] = "bfb5a3e72ead0420bb0bf5c2f039a09b82e3e92b48753aca82186753662fe7f8",
            ["GraphReader.Ocr.dll"] = "ffd3dc64c502407632a67e276af081be6b96259e17e4801c84bab38e5f6c66bc",
            ["GraphReader.Pdf.dll"] = "218194fbf2fec56ca91ecf245a6a7308b42e8ce21a95107df73ea7329ea1e140",
            ["GraphReader.Phases.dll"] = "e26756bdb82751deab30061fdfe1090f4e3ae891aaea9a8c4e8b521f271ff3a2",
            ["GraphReader.SuperResolution.dll"] = "5e238f1549f8d0fa8a08a98931559f347766f58e8b74b9de6a74c798392d2412",
            ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
            ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
            ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
            ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
            ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
            ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
            ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object RoundedReviewObservationIdentity() => new {
        app_exe_sha256 = RoundedReviewObservationExeHash, app_dll_sha256 = RoundedReviewObservationDllHash,
        rust_sha256 = RoundedReviewObservationRustHash, runtime_files = RoundedReviewObservationLibraries,
        prior_rounded_profile_identity = "0f3ecac21f312a7fb0661820bd42981cda3ba8922f384ae1b0d19edd8dbd09a5",
        native_execution = "in_process", native_timing_comparable_to_child = false,
        candidate_selected = false, ocr_calls = 0 };

    internal static async Task<object> RoundedReviewObservationSelfTestAsync(CancellationToken token)
    {
        RequireNativeRejectionPins(RoundedReviewObservationExeHash, RoundedReviewObservationDllHash,
            RoundedReviewObservationRustHash, RoundedReviewObservationLibraries);
        _ = await NativeReviewObservationSelfTestAsync(token).ConfigureAwait(false);
        try { RequireNativeRejectionPins("", "", "", new Dictionary<string, string>(StringComparer.Ordinal)); throw new InvalidOperationException(); }
        catch (InvalidDataException) { }
        NativeAxisCandidateReviewObservation? observation = null;
        AxisCandidateReviewResult result = await NativeAxisCandidateReview.ProcessLinesAsync([], 100, 100, value => observation = value, token).ConfigureAwait(false);
        if (result.Candidates.Count != 0 || observation is null || observation.RoundedExclusionCount != 0 || observation.DuplicateExclusionCount != 0 ||
            observation.RoundedExclusionCount > observation.CheckedCount) throw new InvalidDataException();
        return new { schema = "graphreader.rounded-review-observation-self-test.v1", parity = true,
            missing_pins_rejected = true, rounded_count_checked = true, duplicate_count_checked = true, private_reads = 0 };
    }
}
