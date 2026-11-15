// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;
using GraphReader.App.Integration.NativeAxis;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string NativeReviewObservationSchema = "graphreader.real-dev-native-review-observation-trace.v1";
    // Frozen unpackaged observer runtime, verified against the measured file map.
    private const string NativeReviewObservationExeHash = "4243aec98df86037cc0bf98260caa7b4b828f20aa25e910a64245efa64facb6a";
    private const string NativeReviewObservationDllHash = "ba0bb13e945344420a58ffae5f00662aa97990cae1b3788a03d310c3f204997d";
    private const string NativeReviewObservationRustHash = "9c5ac19abc3b08632d82b5590a9e59f2794d94f92f345fbc70741e181808c0d9";
    private static readonly FrozenDictionary<string, string> NativeReviewObservationLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GraphReader.App.dll"] = "ba0bb13e945344420a58ffae5f00662aa97990cae1b3788a03d310c3f204997d",
            ["GraphReader.Axis.dll"] = "906b1f300c2557bb8bc7f5d475bbf75b0c58ab2bca10569b183b51f33ed27c17",
            ["GraphReader.Domain.dll"] = "cc6d71a2a4e1658150863ee337db983085280078035afebffbd57fb93e276a70",
            ["GraphReader.Export.dll"] = "6c829261ac207fabb68b1d022070e7bdfd7b8a53020d46720deb4db2b13c1cef",
            ["GraphReader.Imaging.dll"] = "2c89a9cf8579a223d84c2707752706e32c1fd4a56047b38ede59a00e8f2d82b4",
            ["GraphReader.Inference.dll"] = "94f6816bdc62751f8a5abd88a6548e918394a6fd3d89751b964c4cec163ef1d1",
            ["GraphReader.Legends.dll"] = "4b27ae00cf2196dd05a922cf2edcfe246f65614f6f01c96e268166081cd9e0af",
            ["GraphReader.Markers.dll"] = "c2d3979d9a47bdfec56b4f81243d91cc181a65a43d886d0fd47c3bd144a11aba",
            ["GraphReader.Ocr.dll"] = "38c345dc913e095568a91bbb753cb2fca04c5642ab68ca462ed4eef372c93f7c",
            ["GraphReader.Pdf.dll"] = "4b78f0dd27b454b807b33ea05e36f2c05077a3b00998ad7c5fa78555ef145dbf",
            ["GraphReader.Phases.dll"] = "895c6c1421956bde96dc118a720ee7a3e805fc365531ab70690321bac95582a1",
            ["GraphReader.SuperResolution.dll"] = "ba28120e8cc9e0a5da29415723deec1a5df08d188545ec3678754fa6979dc1e7",
            ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
            ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
            ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
            ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
            ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
            ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
            ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object NativeReviewObservationIdentity() => new {
        app_exe_sha256 = NativeReviewObservationExeHash, app_dll_sha256 = NativeReviewObservationDllHash,
        rust_sha256 = NativeReviewObservationRustHash, runtime_files = NativeReviewObservationLibraries,
        native_execution = "in_process", native_timing_comparable_to_child = false,
        candidate_selected = false, ocr_calls = 0 };

    private sealed class NativeReviewObservationService(bool includeRounded = false) : IAxisProposalService, IAxisCandidateReviewService
    {
        private readonly List<NativeAxisCandidateReviewObservation?> observations = [];
        internal object? Last { get; private set; }
        internal object Summary => new {
            attempts = observations.Count, observed = observations.Count(value => value is not null),
            enumerations_completed = observations.Count(value => value?.EnumerationCompleted == true),
            enumerations_incomplete = observations.Count(value => value?.EnumerationCompleted != true),
            cancelled = observations.Count(value => value?.Cancelled == true),
            raw_snapshots = observations.Sum(value => value?.RawSnapshotCount ?? 0),
            checked_snapshots = observations.Sum(value => value?.CheckedCount ?? 0),
            eligible = observations.Sum(value => value?.EligibleCount ?? 0),
            rotated = observations.Sum(value => value?.RotatedCount ?? 0),
            stages = observations.GroupBy(value => SafeNativeStage(value?.Stage) ?? "missing")
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            failures = observations.GroupBy(value => SafeNativeDetail(value?.FailureDetail) ?? "none")
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            hard_rejections = observations.GroupBy(value => SafeNativeDetail(value?.HardGeometryRejection) ?? "none")
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal) };
        internal object RoundedSummary => new { observation = Summary,
            rounded_exclusions = observations.Sum(value => value?.RoundedExclusionCount ?? 0),
            duplicate_exclusions = observations.Sum(value => value?.DuplicateExclusionCount ?? 0) };
        public Task<RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token) => throw new InvalidOperationException();
        public async Task<AxisCandidateReviewResult> ReviewCandidatesAsync(byte[] image, CancellationToken token)
        {
            NativeAxisCandidateReviewObservation? snapshot = null;
            Last = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                token.ThrowIfCancellationRequested();
                if (image is null || image.Length == 0 || image.Length > NativeAxisWorker.MaximumImageBytes)
                    return new([], "INVALID_INPUT");
                return await NativeAxisCandidateReview.ProcessAsync((byte[])image.Clone(), value => snapshot = value, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return new([], "OPENCV_AXIS_TIMEOUT");
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                return new([], "OPENCV_AXIS_UNAVAILABLE");
            }
            finally
            {
                Last = includeRounded ? RoundedSnapshot(snapshot) : Snapshot(snapshot);
                observations.Add(snapshot);
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static object RoundedSnapshot(NativeAxisCandidateReviewObservation? value) => new {
        observation = Snapshot(value), rounded_exclusion_count = value?.RoundedExclusionCount,
        duplicate_exclusion_count = value?.DuplicateExclusionCount };

    private static object Snapshot(NativeAxisCandidateReviewObservation? value) => new {
        observed = value is not null,
        stage = SafeNativeStage(value?.Stage),
        enumeration_completed = value?.EnumerationCompleted,
        cancelled = value?.Cancelled,
        raw_snapshot_count = value?.RawSnapshotCount,
        checked_count = value?.CheckedCount,
        eligible_count = value?.EligibleCount,
        rotated_count = value?.RotatedCount,
        failure_detail = SafeNativeDetail(value?.FailureDetail),
        hard_geometry_rejection = SafeNativeDetail(value?.HardGeometryRejection),
        work = value?.Work };

    private static string? SafeNativeStage(string? value) => value switch {
        null => null, "decode" or "line_detection" or "enumeration" or "snapshot_validation" or "geometry_eligibility" or "complete" => value,
        _ => "unknown" };
    private static string? SafeNativeDetail(string? value) => value switch {
        null => null, "INVALID_INPUT" or "cancelled" or "native_no_line_evidence" or "native_missing_orientation" or
        "native_envelope_budget_exceeded" or "native_pair_budget_exceeded" or "native_no_intersecting_pair" or "native_geometry_unavailable" or
        "native_no_ranked_candidates" or "native_snapshot_limit_exceeded" or "native_no_eligible_candidates" or
        "native_nonfinite_geometry" or "native_invalid_corner_bounds" or "native_invalid_rounded_geometry" or
        "native_decode_failed" or "native_line_detection_failed" or "native_enumeration_failed" or
        "native_snapshot_validation_failed" or "native_geometry_eligibility_failed" or "native_complete_failed" => value,
        _ => "unknown" };

    internal static async Task<object> NativeReviewObservationSelfTestAsync(CancellationToken token)
    {
        _ = await ReviewRoutingTraceSelfTestAsync(token).ConfigureAwait(false);
        try { RequireNativeRejectionPins("", "", "", new Dictionary<string, string>(StringComparer.Ordinal)); throw new InvalidOperationException(); }
        catch (InvalidDataException) { }
        NativeAxisCandidateReviewObservation? observed = null;
        AxisCandidateReviewResult plain = await NativeAxisCandidateReview.ProcessLinesAsync([], 100, 100, token).ConfigureAwait(false);
        AxisCandidateReviewResult traced = await NativeAxisCandidateReview.ProcessLinesAsync([], 100, 100, value => observed = value, token).ConfigureAwait(false);
        if (plain.ErrorCode != traced.ErrorCode || plain.Candidates.Count != traced.Candidates.Count || observed is null ||
            observed.EligibleCount > observed.CheckedCount || observed.CheckedCount > observed.RawSnapshotCount) throw new InvalidDataException();
        return new { schema = "graphreader.native-review-observation-self-test.v1", parity = true, missing_pins_rejected = true, private_reads = 0 };
    }
}
