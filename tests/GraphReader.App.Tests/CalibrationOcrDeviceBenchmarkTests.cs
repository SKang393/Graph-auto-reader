// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Services;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationOcrDeviceBenchmarkTests
{
    private static readonly CalibrationOcrDeviceSelection Gpu = new("DirectMl", 0, 42, "Fixture GPU");

    [TestMethod]
    public async Task MatchingDeviceMeasuresFourReadsAndReusesWorker()
    {
        var workers = new List<Fake>();
        var benchmark = new CalibrationOcrDeviceBenchmark(d => { var worker = new Fake(d); workers.Add(worker); return worker; });
        CalibrationOcrDeviceBenchmarkResult result = await benchmark.RunAsync([Gpu], CancellationToken.None);
        Assert.AreEqual(2, result.Measurements.Count);
        Assert.IsTrue(result.Measurements[1].OutputMatchesCpu);
        Assert.IsNull(result.Measurements[1].FailureCode);
        Assert.IsTrue(workers.All(worker => worker.Disposed));
    }

    [TestMethod]
    public async Task MismatchedGpuOutputIsNeverEligible()
    {
        var benchmark = new CalibrationOcrDeviceBenchmark(d => new Fake(d, mismatch: d.Provider == "DirectMl"));
        CalibrationOcrDeviceBenchmarkResult result = await benchmark.RunAsync([Gpu], CancellationToken.None);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, result.SelectedDevice);
        Assert.IsNull(result.Measurements[0].FailureCode);
        Assert.AreEqual("CALIBRATION_OCR_BENCHMARK_OUTPUT_MISMATCH", result.Measurements[1].FailureCode);
        Assert.IsFalse(result.Measurements[1].OutputMatchesCpu);
    }

    [TestMethod]
    public async Task DeviceDeadlineCancelsAndDisposesWorker()
    {
        Fake? worker = null;
        var benchmark = new CalibrationOcrDeviceBenchmark(d => worker = new Fake(d, block: true),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2));
        CalibrationOcrDeviceBenchmarkResult result = await benchmark.RunAsync([], CancellationToken.None);
        Assert.AreEqual(CalibrationOcrReasonCodes.Timeout, result.Measurements[0].FailureCode);
        Assert.IsTrue(worker!.Disposed);
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesAndDisposesWorker()
    {
        Fake? worker = null;
        var benchmark = new CalibrationOcrDeviceBenchmark(d => worker = new Fake(d, block: true));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await benchmark.RunAsync([], cancellation.Token));
        Assert.IsTrue(worker!.Disposed);
    }

    private sealed class Fake(CalibrationOcrDeviceSelection device, bool mismatch = false, bool block = false) : ICalibrationOcrSuggestionService
    {
        private int _reads;
        public bool Disposed { get; private set; }
        public async Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request, ReadOnlyMemory<byte> image,
            CancellationToken cancellationToken)
        {
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            CalibrationOcrResponse response = CalibrationOcrClientTests.Success(request);
            CalibrationOcrProposal original = response.Proposals[0];
            CalibrationOcrProposal[] rows = Enumerable.Range(0, 3).Select(i => original with
                { RegionId = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Text = mismatch && i == 0 ? "9" : "1" }).ToArray();
            return response with { Proposals = rows, Timings = response.Timings! with { ReusedSession = _reads++ > 0 }, Runtime = response.Runtime! with
                { Provider = device.Provider, RequestedProvider = device.Provider, AdapterIndex = device.AdapterIndex,
                    AdapterLuid = device.AdapterLuid, AdapterName = device.AdapterName } };
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
