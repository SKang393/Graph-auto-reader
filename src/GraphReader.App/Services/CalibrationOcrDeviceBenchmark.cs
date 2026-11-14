// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;

namespace GraphReader.App.Services;

public sealed record CalibrationOcrDeviceMeasurement(CalibrationOcrDeviceSelection Device,
    double? ColdMilliseconds, double? WarmMedianMilliseconds, double? FourReadTotalMilliseconds,
    bool OutputMatchesCpu, string? FailureCode);
public sealed record CalibrationOcrDeviceBenchmarkResult(CalibrationOcrDeviceSelection SelectedDevice,
    IReadOnlyList<CalibrationOcrDeviceMeasurement> Measurements);

/// <summary>Bounded, review-only device comparison on two owned in-memory fixtures.</summary>
public sealed class CalibrationOcrDeviceBenchmark
{
    private readonly Func<CalibrationOcrDeviceSelection, ICalibrationOcrSuggestionService> _factory;
    private readonly TimeSpan _deviceTimeout;
    private readonly TimeSpan _totalTimeout;

    public CalibrationOcrDeviceBenchmark(Func<CalibrationOcrDeviceSelection, ICalibrationOcrSuggestionService> factory)
        : this(factory, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90)) { }

    internal CalibrationOcrDeviceBenchmark(Func<CalibrationOcrDeviceSelection, ICalibrationOcrSuggestionService> factory,
        TimeSpan deviceTimeout, TimeSpan totalTimeout)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        if (deviceTimeout <= TimeSpan.Zero || deviceTimeout > TimeSpan.FromSeconds(30) ||
            totalTimeout <= TimeSpan.Zero || totalTimeout > TimeSpan.FromSeconds(90))
            throw new ArgumentOutOfRangeException(nameof(deviceTimeout));
        _deviceTimeout = deviceTimeout;
        _totalTimeout = totalTimeout;
    }

    public async Task<CalibrationOcrDeviceBenchmarkResult> RunAsync(IReadOnlyList<CalibrationOcrDeviceSelection> devices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var fixtures = CalibrationOcrBenchmarkFixture.CreateWorkloads();
        var measurements = new List<CalibrationOcrDeviceMeasurement>();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(_totalTimeout);
        var (cpu, baseline) = await MeasureAsync(CalibrationOcrDeviceSelection.Cpu, fixtures, null, total.Token, cancellationToken).ConfigureAwait(false);
        measurements.Add(cpu);
        if (cpu.FailureCode is not null || baseline is null) return new(CalibrationOcrDeviceSelection.Cpu, measurements);
        foreach (CalibrationOcrDeviceSelection device in devices.Where(d => d.Provider == "DirectMl" &&
                     d.AdapterIndex is >= 0 && d.AdapterLuid is not null && !string.IsNullOrWhiteSpace(d.AdapterName))
                     .DistinctBy(d => (d.AdapterIndex, d.AdapterLuid)).Take(2))
        {
            if (total.IsCancellationRequested) break;
            var (measurement, _) = await MeasureAsync(device, fixtures, baseline, total.Token, cancellationToken).ConfigureAwait(false);
            measurements.Add(measurement);
        }
        cancellationToken.ThrowIfCancellationRequested();
        CalibrationOcrDeviceSelection selected = measurements.Skip(1)
            .Where(m => m.FailureCode is null && m.OutputMatchesCpu && m.FourReadTotalMilliseconds <= cpu.FourReadTotalMilliseconds * .85)
            .OrderBy(m => m.FourReadTotalMilliseconds).FirstOrDefault()?.Device ?? CalibrationOcrDeviceSelection.Cpu;
        return new(selected, measurements);
    }

    private async Task<(CalibrationOcrDeviceMeasurement Measurement, Dictionary<int, CalibrationOcrProposal[]>? Baseline)> MeasureAsync(CalibrationOcrDeviceSelection device,
        (CalibrationOcrRequest Request, byte[] Image)[] fixtures, Dictionary<int, CalibrationOcrProposal[]>? baseline,
        CancellationToken total, CancellationToken caller)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(total);
        deadline.CancelAfter(_deviceTimeout);
        try
        {
            await using ICalibrationOcrSuggestionService worker = _factory(device);
            var elapsed = new double[4];
            var expected = baseline is null
                ? new Dictionary<int, CalibrationOcrProposal[]>()
                : new Dictionary<int, CalibrationOcrProposal[]>(baseline);
            var all = Stopwatch.StartNew();
            for (int i = 0; i < elapsed.Length; i++)
            {
                // A/B/A/B: both fixtures repeat within the same persistent session.
                int fixtureIndex = i % fixtures.Length;
                var fixture = fixtures[fixtureIndex];
                var read = Stopwatch.StartNew();
                CalibrationOcrResponse response = await worker.SuggestAsync(fixture.Request, fixture.Image, deadline.Token)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                elapsed[i] = read.Elapsed.TotalMilliseconds;
                if (response.FailureCode is not null || response.Request != fixture.Request || response.Runtime is null ||
                    response.Timings is null || response.Timings.ReusedSession != (i > 0) || response.Runtime.SessionCreationCount != 1 ||
                    response.Runtime.Provider != device.Provider || response.Runtime.RequestedProvider != device.Provider ||
                    response.Runtime.FallbackReason is not null || (device.Provider == "DirectMl" &&
                    (response.Runtime.AdapterIndex != device.AdapterIndex || response.Runtime.AdapterLuid != device.AdapterLuid)))
                    return (Failed(device, response.FailureCode ?? "CALIBRATION_OCR_BENCHMARK_PROVIDER_MISMATCH"), null);
                CalibrationOcrProposal[] actual = response.Proposals.OrderBy(p => p.RegionId, StringComparer.Ordinal).ToArray();
                if (actual.Length < 3 || actual.Any(p => string.IsNullOrWhiteSpace(p.Text)) ||
                    actual.Select(p => p.RegionId).Distinct(StringComparer.Ordinal).Count() != actual.Length)
                    return (Failed(device, "CALIBRATION_OCR_BENCHMARK_INSUFFICIENT_OUTPUT"), null);
                if (!expected.TryGetValue(fixtureIndex, out CalibrationOcrProposal[]? reference))
                    expected.Add(fixtureIndex, actual);
                else if (!Matches(reference, actual))
                    return (Failed(device, "CALIBRATION_OCR_BENCHMARK_OUTPUT_MISMATCH"), null);
            }
            all.Stop();
            // Median of reused B/A/B reads, not per-fixture steady-state latency.
            double[] warm = elapsed[1..]; Array.Sort(warm);
            return (new(device, elapsed[0], warm[1], all.Elapsed.TotalMilliseconds, true, null), expected);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return (Failed(device, CalibrationOcrReasonCodes.Timeout), null);
        }
        catch (OperationCanceledException)
        {
            caller.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return (Failed(device, CalibrationOcrReasonCodes.Unavailable), null);
        }
    }

    private static CalibrationOcrDeviceMeasurement Failed(CalibrationOcrDeviceSelection device, string code) =>
        new(device, null, null, null, false, code);

    private static bool Matches(CalibrationOcrProposal[] left, CalibrationOcrProposal[] right) =>
        left.Length == right.Length && left.Zip(right).All(pair => pair.First.RegionId == pair.Second.RegionId &&
            pair.First.Text == pair.Second.Text && pair.First.NumericValue == pair.Second.NumericValue &&
            pair.First.FailureCode == pair.Second.FailureCode && pair.First.Role == pair.Second.Role &&
            pair.First.SuggestedAxis == pair.Second.SuggestedAxis && pair.First.NumericLiteral == pair.Second.NumericLiteral &&
            pair.First.NormalizedText == pair.Second.NormalizedText &&
            pair.First.Polygon.SequenceEqual(pair.Second.Polygon));
}
