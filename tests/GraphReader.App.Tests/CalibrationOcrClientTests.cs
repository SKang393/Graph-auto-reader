// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using GraphReader.App.Services;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationOcrClientTests
{
    [TestMethod]
    public void DirectMlResponseMustMatchSelectionOrExposeCpuFallback()
    {
        CalibrationOcrRequest request = Request();
        CalibrationOcrResponse original = Success(request);
        var selection = new CalibrationOcrDeviceSelection("DirectMl", 2, 42, "Fixture GPU");
        CalibrationOcrRuntime gpu = original.Runtime! with { RequestedProvider = "DirectMl", Provider = "DirectMl",
            AdapterIndex = 2, AdapterLuid = 42, AdapterName = "Fixture GPU", PacingRatio = 1 };
        Assert.IsTrue(CalibrationOcrWorkerClient.IsValidResponse(request, original with { Runtime = gpu }, selection));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, original with { Runtime = gpu with { AdapterLuid = 43 } }, selection));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, original with { Runtime = gpu with { PacingRatio = 0 } }, selection));
        CalibrationOcrRuntime fallback = original.Runtime! with { RequestedProvider = "DirectMl", FallbackReason = "CALIBRATION_OCR_DIRECTML_SESSION_FAILED" };
        Assert.IsTrue(CalibrationOcrWorkerClient.IsValidResponse(request, original with { Runtime = fallback }, selection));
        Assert.IsTrue(CalibrationOcrWorkerClient.IsValidResponse(request, original with { Runtime = fallback with { SessionCreationCount = 2 } }, selection));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, original with { Runtime = fallback with { SessionCreationCount = 3 } }, selection));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, original with { Runtime = gpu with { SessionCreationCount = 2 } }, selection));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, original with { Runtime = fallback with { FallbackReason = null } }, selection));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request,
            original with { Runtime = fallback with { FallbackReason = "unknown_test_reason" } }, selection),
            "Unknown fallback reasons must be rejected before the UI can claim an actual runtime.");
        Assert.IsFalse(CalibrationOcrWorkerClient.ValidSelection(new("DirectMl", -1, 42, "GPU")));
    }
    [TestMethod]
    public void RejectsChangedIdentityUnreviewedOutOfBoundsAndUnknownRoles()
    {
        CalibrationOcrRequest request = Request();
        CalibrationOcrResponse response = Success(request);
        Assert.IsTrue(CalibrationOcrWorkerClient.IsValidResponse(request, response));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, response with { Request = request with { Binding = new string('b', 64) } }));
        CalibrationOcrProposal proposal = response.Proposals[0];
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, response with { Proposals = [proposal with { RequiresReview = false }] }));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, response with { Proposals = [proposal with { Role = "fake" }] }));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, response with { Proposals = [proposal with { NumericValue = double.NaN }] }));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, response with { Proposals = [proposal with { Polygon = [new(-1, 1), new(2, 1), new(2, 2)] }] }));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, response with { Runtime = response.Runtime! with { Provider = "DirectMl" } }));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, response with { Runtime = response.Runtime! with { ModelSha256 = new string('f', 64) } }));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, response with { Proposals = [proposal with { Text = "O", NumericLiteral = false }] }));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, response with { Proposals = [proposal with { FailureCode = CalibrationOcrReasonCodes.RecognitionFailed }] }));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, response with { Proposals = [proposal with { NormalizedText = "2" }] }));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, response with { Proposals = Enumerable.Repeat(proposal, 129).ToArray() }));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, response with { Proposals = [proposal with { Polygon = [new(1,1),new(2,1),new(3,1),new(3,2),new(1,2)] }] }));
    }

    [TestMethod]
    public async Task MissingWorkerReturnsStructuredFailureAndCancellationPropagates()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe");
        await using var client = new CalibrationOcrWorkerClient(missing, Path.GetTempPath());
        byte[] image = [1, 2, 3];
        CalibrationOcrRequest request = Request() with { PanelImageSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant() };
        CalibrationOcrResponse result = await client.SuggestAsync(request, image, CancellationToken.None);
        Assert.AreEqual(CalibrationOcrReasonCodes.Unavailable, result.FailureCode);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await client.SuggestAsync(request, image, cancelled.Token));
    }

    internal static CalibrationOcrRequest Request() => new("fixture", new string('a', 64), new string('b', 64),
        new string('c', 64), 160, 120, new(8, 5, 155, 110));

    internal static CalibrationOcrResponse Success(CalibrationOcrRequest request)
    {
        var candidate = new CalibrationOcrProposal("fixture", [new(10, 10), new(11, 10), new(11, 11)],
            "1", .8, 1, "1", true, "XTick", "X", null);
        var sequence = new CalibrationOcrSequenceCheck(true, 1, 1, 0, []);
        var timing = new CalibrationOcrTimings(1, 1, 1, 1, 1, 5, false);
        var budget = new CalibrationOcrCpuBudget(10, 500, .5, true);
        var memory = new CalibrationOcrProcessMemory(1, 1, 1, 1, 1, 1);
        CalibrationOcrRuntimeFile[] files = [new("GraphReader.App", new string('d', 64)), new("GraphReader.Inference", new string('d', 64)),
            new("GraphReader.Ocr", new string('d', 64)), new("onnxruntime.dll", new string('d', 64))];
        var runtime = new CalibrationOcrRuntime(GraphReader.App.Integration.CalibrationOcr.CalibrationOcrSession.ModelSha256,
            GraphReader.App.Integration.CalibrationOcr.CalibrationOcrSession.ManifestSha256, "Cpu", 1, 1, 1, 1, false, budget, memory, files);
        return new(request, [candidate], sequence, sequence, timing, runtime, null);
    }
}
