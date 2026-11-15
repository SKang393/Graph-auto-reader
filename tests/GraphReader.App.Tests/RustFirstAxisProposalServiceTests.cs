// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class RustFirstAxisProposalServiceTests
{
    [TestMethod]
    [DataRow("NO_AXIS")]
    [DataRow("NO_INTERSECTION")]
    public async Task MissingRustGeometryInvokesNativeReviewOnlyOnce(string error)
    {
        byte[] image = [1, 2, 3];
        var primary = new Stub(new(null, error));
        var nativeResult = new RustAxisProposalResult(new(10, 90, 110, 10, .3), null)
            { Provider = "opencv-review", ReviewReasons = ["y_axis_partial"] };
        var fallback = new Stub(nativeResult);
        var service = new RustFirstAxisProposalService(primary, fallback);

        RustAxisProposalResult result = await service.SuggestAsync(image, CancellationToken.None);

        Assert.AreSame(nativeResult, result);
        Assert.AreEqual(1, primary.CallCount);
        Assert.AreEqual(1, fallback.CallCount);
        Assert.AreSame(image, fallback.Input);
        Assert.AreEqual(0, result.Geometry!.XTickPositions.Count);
        Assert.AreEqual(0, result.Geometry.YTickPositions.Count);
    }

    [TestMethod]
    [DataRow("INVALID_RESPONSE")]
    [DataRow("INVALID_INPUT")]
    [DataRow("INVALID_PROTOCOL")]
    [DataRow("CPU_BUDGET_UNAVAILABLE")]
    [DataRow("WORKER_TIMEOUT")]
    [DataRow("WORKER_START_OR_IO_FAILED")]
    [DataRow(null)]
    public async Task OtherRustFailuresNeverInvokeFallback(string? error)
    {
        var original = new RustAxisProposalResult(null, error);
        var primary = new Stub(original);
        var fallback = new Stub(new(new(10, 90, 110, 10, .3), null));

        RustAxisProposalResult result = await new RustFirstAxisProposalService(primary, fallback)
            .SuggestAsync([], CancellationToken.None);

        Assert.AreSame(original, result);
        Assert.AreEqual(0, fallback.CallCount);
    }

    [TestMethod]
    public async Task AmbiguousRustGeometryUsesOnlySuccessfulNativeReview()
    {
        var original = new RustAxisProposalResult(null, "AMBIGUOUS_AXIS");
        var native = new RustAxisProposalResult(new(10, 90, 110, 10, .3), null)
        { Provider = "opencv-review", ReviewReasons = ["y_axis_partial"] };
        var fallback = new Stub(native);
        RustAxisProposalResult result = await new RustFirstAxisProposalService(new Stub(original), fallback)
            .SuggestAsync([], CancellationToken.None);
        Assert.AreEqual(1, fallback.CallCount);
        Assert.AreEqual("opencv-review", result.Provider);
        CollectionAssert.Contains(result.ReviewReasons.ToArray(), "y_axis_partial");
        CollectionAssert.Contains(result.ReviewReasons.ToArray(), "rust_axis_ambiguous");
        Assert.AreEqual(0, result.Geometry!.XTickPositions.Count);
        Assert.AreEqual(0, result.Geometry.YTickPositions.Count);
    }

    [TestMethod]
    public async Task RejectedNativeReviewPreservesOriginalRustAmbiguity()
    {
        var original = new RustAxisProposalResult(null, "AMBIGUOUS_AXIS");
        var fallback = new Stub(new(null, "OPENCV_AXIS_AMBIGUOUS") { Provider = "opencv-review" });
        RustAxisProposalResult result = await new RustFirstAxisProposalService(new Stub(original), fallback)
            .SuggestAsync([], CancellationToken.None);
        Assert.AreSame(original, result);
        Assert.AreEqual(1, fallback.CallCount);
    }

    [TestMethod]
    [DataRow("native_pair_budget_exceeded")]
    [DataRow("native_envelope_budget_exceeded")]
    [DataRow("native_rotated_geometry")]
    public async Task FailedFallbackPreservesPrimaryAndRecognizedNativeDetail(string reason)
    {
        var original = new RustAxisProposalResult(null, "AMBIGUOUS_AXIS") { ReviewReasons = ["existing"] };
        var fallback = new Stub(new(null, "OPENCV_AXIS_AMBIGUOUS") { Provider = "opencv-review", ReviewReasons = [reason, "unrecognized"] });
        RustAxisProposalResult result = await new RustFirstAxisProposalService(new Stub(original), fallback).SuggestAsync([], CancellationToken.None);
        Assert.AreEqual(original.ErrorCode, result.ErrorCode);
        Assert.AreEqual(original.Provider, result.Provider);
        Assert.IsNull(result.Geometry);
        CollectionAssert.AreEqual(new[] { "existing", reason }, result.ReviewReasons.ToArray());
        Assert.AreEqual(1, fallback.CallCount);
    }

    [TestMethod]
    public async Task NativeTimeoutIsPreservedButMalformedOrUnexpectedReasonsAreIgnored()
    {
        var original = new RustAxisProposalResult(null, "NO_AXIS");
        var timeout = new Stub(new(null, "OPENCV_AXIS_TIMEOUT") { Provider = "opencv-review" });
        RustAxisProposalResult result = await new RustFirstAxisProposalService(new Stub(original), timeout).SuggestAsync([], CancellationToken.None);
        Assert.AreEqual("NO_AXIS", result.ErrorCode);
        CollectionAssert.Contains(result.ReviewReasons.ToArray(), "native_axis_timeout");
        var malformed = new Stub(new(null, "OPENCV_AXIS_AMBIGUOUS") { Provider = "opencv-review", ReviewReasons = [new string('x', 129), "native_pair_budget_exceeded"] });
        result = await new RustFirstAxisProposalService(new Stub(original), malformed).SuggestAsync([], CancellationToken.None);
        Assert.AreSame(original, result);
        var wrongProvider = new Stub(new(null, "OPENCV_AXIS_AMBIGUOUS") { Provider = "rust", ReviewReasons = ["native_pair_budget_exceeded"] });
        result = await new RustFirstAxisProposalService(new Stub(original), wrongProvider).SuggestAsync([], CancellationToken.None);
        Assert.AreSame(original, result);
    }

    [TestMethod]
    public async Task ExistingRustGeometryNeverInvokesFallback()
    {
        var original = new RustAxisProposalResult(new(10, 90, 110, 10, .9), null);
        var fallback = new Stub(new(null, "NO_AXIS"));
        RustAxisProposalResult result = await new RustFirstAxisProposalService(new Stub(original), fallback)
            .SuggestAsync([], CancellationToken.None);
        Assert.AreSame(original, result);
        Assert.AreEqual(0, fallback.CallCount);
    }

    [TestMethod]
    public async Task CancellationAfterRustResultDoesNotStartFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var primary = new Stub(new(null, "NO_AXIS")) { OnCall = cancellation.Cancel };
        var fallback = new Stub(new(null, "NO_AXIS"));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new RustFirstAxisProposalService(primary, fallback)
            .SuggestAsync([], cancellation.Token));
        Assert.AreEqual(0, fallback.CallCount);
    }

    [TestMethod]
    public async Task ExceptionFromRustNeverStartsFallback()
    {
        var primary = new Stub(new(null, "NO_AXIS")) { OnCall = () => throw new InvalidOperationException("owned") };
        var fallback = new Stub(new(null, "NO_AXIS"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new RustFirstAxisProposalService(primary, fallback)
            .SuggestAsync([], CancellationToken.None));
        Assert.AreEqual(0, fallback.CallCount);
    }

    private sealed class Stub(RustAxisProposalResult result) : IAxisProposalService
    {
        public int CallCount { get; private set; }
        public byte[]? Input { get; private set; }
        public Action? OnCall { get; init; }
        public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken)
        {
            CallCount++;
            Input = originalImage;
            OnCall?.Invoke();
            return Task.FromResult(result);
        }
    }
}
