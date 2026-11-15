// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.Diagnostics;
using System.IO;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string ReviewRoutingTraceSchema = "graphreader.real-dev-review-routing-geometry-trace.v1";
    private const string ReviewRoutingAppExeHash = "539f56b547644666d9b18eeafb36aa495c9817a2fac1dbefbac1d56d9bef7689";
    private const string ReviewRoutingAppDllHash = "99e27730db36135bf6ddc914042e7c2a866e87b8a0a726b9875cbfdeb49d7b99";
    private const string ReviewRoutingRustHash = "9c5ac19abc3b08632d82b5590a9e59f2794d94f92f345fbc70741e181808c0d9";
    private static readonly FrozenDictionary<string, string> ReviewRoutingRuntimeLibraries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GraphReader.App.dll"] = "99e27730db36135bf6ddc914042e7c2a866e87b8a0a726b9875cbfdeb49d7b99",
        ["GraphReader.Axis.dll"] = "b505463f3b92d089acafe652cf2064595f4914533b28a6a16cde95fdc8391206",
        ["GraphReader.Domain.dll"] = "584ee70e8f3313182126f1f0c790385abfd7bc72b24c4adec0c903830d0ab5eb",
        ["GraphReader.Export.dll"] = "66e59d1bb16df0b675a2b03c319106d98e90e86864dbf9fa891989380769ef3d",
        ["GraphReader.Imaging.dll"] = "d31d80312d7f9c4bcc5b6a408416e251b80d577e09e74711a64f082c7a3ac2cb",
        ["GraphReader.Inference.dll"] = "a0fbcb6221f2a3d32c2d5d386846b54c934394700c154b86ddb81de73da8bd10",
        ["GraphReader.Legends.dll"] = "5eb8810ddbb1901915fce919ab04420bf396f9d3460926647c0fabf356158958",
        ["GraphReader.Markers.dll"] = "0af6dfec669b87de0a24aafe2592a06969b71b830fadd37d5b088a5ef6181d2d",
        ["GraphReader.Ocr.dll"] = "ebc6c6dea7021cc27064a55b2c3cc85b102d78b2c7fecf59b23c541ad8c5192c",
        ["GraphReader.Pdf.dll"] = "31832c681120947d0941470cab212b2217c0d03a0a5268f0380ad3c87b45c627",
        ["GraphReader.Phases.dll"] = "fc077101fcec21712ce802f4f0b79e977c8083fbc71408586a4ee41579a01972",
        ["GraphReader.SuperResolution.dll"] = "3f2db22ff1591d9a2a37fd11b198311bd505396d203a8c35c24a2fb020c6486d",
        ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
        ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
        ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
        ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
        ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
        ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
        ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
        ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object ReviewRoutingTraceIdentity() => new
    {
        kind = "unpackaged_review_source",
        app_exe_sha256 = ReviewRoutingAppExeHash,
        app_dll_sha256 = ReviewRoutingAppDllHash,
        rust_sha256 = ReviewRoutingRustHash,
        runtime_files = ReviewRoutingRuntimeLibraries,
        ocr_calls = 0,
        candidate_selected = false,
    };

    private sealed class ReviewRoutingObservedService(IAxisProposalService inner) : IAxisProposalService, IAxisCandidateReviewService
    {
        private readonly IAxisCandidateReviewService review = (IAxisCandidateReviewService)inner;
        internal int Calls { get; private set; }
        internal AxisCandidateReviewResult? Last { get; private set; }
        public bool CanReviewCandidates => review.CanReviewCandidates;
        public Task<RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token) => inner.SuggestAsync(image, token);
        public async Task<AxisCandidateReviewResult> ReviewCandidatesAsync(byte[] image, CancellationToken token)
        {
            Calls++;
            Last = null;
            return Last = await review.ReviewCandidatesAsync(image, token).ConfigureAwait(false);
        }
    }

    private sealed class ReviewRoutingTraceServices
    {
        private readonly ReviewRoutingObservedService primary;
        private readonly ReviewRoutingObservedService fallback;
        private readonly RustFirstAxisProposalService composite;

        internal ReviewRoutingTraceServices(string rust, string app) : this(new RustAxisProposalService(rust), new OpenCvAxisProposalService(app)) { }
        internal ReviewRoutingTraceServices(IAxisProposalService rust, IAxisProposalService native)
        {
            primary = new(rust); fallback = new(native);
            composite = new(primary, fallback);
        }

        internal async Task<TracePanelOutcome> RunAsync(byte[] image, int width, int height, CancellationToken token)
        {
            int priorPrimary = primary.Calls, priorFallback = fallback.Calls;
            var timer = Stopwatch.StartNew();
            AxisCandidateReviewResult result = await composite.ReviewCandidatesAsync(image, token).ConfigureAwait(false);
            timer.Stop();
            token.ThrowIfCancellationRequested();
            bool primaryCalled = primary.Calls > priorPrimary, fallbackCalled = fallback.Calls > priorFallback;
            if (!primaryCalled || result.Candidates is null || result.Candidates.Count > 128 ||
                result.ErrorCode is not null && result.Candidates.Count != 0 ||
                result.ErrorCode is null && !OpenCvAxisProposalService.ValidCandidates(result, width, height))
                throw new InvalidDataException();
            string status = result.ErrorCode is null ? "REVIEW_CHOICES_AVAILABLE" : "REVIEW_UNAVAILABLE";
            return new(status, new { status, candidate_count = result.Candidates.Count, result.Provider,
                error_code = SafeReviewError(result.ErrorCode),
                rust_attempted = primaryCalled, rust_error = SafeReviewError(primary.Last?.ErrorCode),
                rust_candidate_count = primary.Last?.Candidates.Count,
                native_attempted = fallbackCalled, native_error = fallbackCalled ? SafeReviewError(fallback.Last?.ErrorCode) : null,
                native_candidate_count = fallbackCalled ? fallback.Last?.Candidates.Count : null,
                milliseconds = timer.Elapsed.TotalMilliseconds, candidate_selected = false });
        }
    }

    private static string? SafeReviewError(string? error) => error switch
    {
        null => null,
        "NO_AXIS" or "NO_INTERSECTION" or "AMBIGUOUS_AXIS" or "CPU_BUDGET_UNAVAILABLE" or "INVALID_INPUT" or
        "INVALID_PROTOCOL" or "INVALID_RESPONSE" or "UNSUPPORTED_PLATFORM" or "WORKER_START_FAILED" or
        "WORKER_START_OR_IO_FAILED" or "WORKER_TIMEOUT" or "OPENCV_AXIS_UNAVAILABLE" or
        "OPENCV_AXIS_AMBIGUOUS" or "OPENCV_AXIS_TIMEOUT" => error,
        _ => "UNKNOWN_FAILURE",
    };

    private sealed class ReviewRoutingFake(AxisCandidateReviewResult result) : IAxisProposalService, IAxisCandidateReviewService
    {
        public int Calls { get; private set; }
        public Task<RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token) => throw new InvalidOperationException();
        public Task<AxisCandidateReviewResult> ReviewCandidatesAsync(byte[] image, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(result); }
    }

    internal static async Task<object> ReviewRoutingTraceSelfTestAsync(CancellationToken token)
    {
        try
        {
            RequireNativeRejectionPins("", "", "", new Dictionary<string, string>(StringComparer.Ordinal));
            throw new InvalidOperationException();
        }
        catch (InvalidDataException) { }
        AxisCandidateReviewResult invalid = await new RustAxisProposalService("unused").ReviewCandidatesAsync([], token).ConfigureAwait(false);
        if (invalid.ErrorCode != "INVALID_INPUT" || invalid.Candidates.Count != 0) throw new InvalidDataException();
        var geometry = new RustAxisGeometry(10, 90, 90, 10, 0, 10, 90)
            { XTickPositions = [10, 50, 90], YTickPositions = [10, 50, 90] };
        var primary = new ReviewRoutingFake(new AxisCandidateReviewResult([new(0, geometry)], null) { Provider = "rust-review" });
        var fallback = new ReviewRoutingFake(new AxisCandidateReviewResult([], "OPENCV_AXIS_AMBIGUOUS"));
        var route = new ReviewRoutingTraceServices(primary, fallback);
        TracePanelOutcome outcome = await route.RunAsync([1], 100, 100, token).ConfigureAwait(false);
        if (outcome.Status != "REVIEW_CHOICES_AVAILABLE" || primary.Calls != 1 || fallback.Calls != 0) throw new InvalidDataException();
        primary = new ReviewRoutingFake(new AxisCandidateReviewResult([], "NO_AXIS") { Provider = "rust-review" });
        route = new ReviewRoutingTraceServices(primary, fallback);
        outcome = await route.RunAsync([1], 100, 100, token).ConfigureAwait(false);
        if (outcome.Status != "REVIEW_UNAVAILABLE" || primary.Calls != 1 || fallback.Calls != 1) throw new InvalidDataException();
        Dictionary<string, int> counts = NewTracePanelStatusCounts(geometryOnly: true, reviewRouting: true);
        CountTracePanelStatus(counts, outcome);
        if (counts["REVIEW_UNAVAILABLE"] != 1 || counts.Values.Sum() != 1) throw new InvalidDataException();
        return new { schema = "graphreader.real-dev-review-routing-self-test.v1", input_refusal = true,
            list_accounting = true, no_candidate_selected = true, private_reads = 0 };
    }
}
