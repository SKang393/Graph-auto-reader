// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
namespace GraphReader.App.Services;

public enum CalibrationOcrDeviceOptimizerState { Idle, Waiting, Checking, Ready, Cancelled, Failed }

/// <summary>One idle automatic attempt per launch; explicit checks use the same serialized path.</summary>
public sealed class CalibrationOcrDeviceOptimizer : IDisposable
{
    public const string BaselineFailed = "CALIBRATION_OCR_DEVICE_CHECK_BASELINE_FAILED";
    public const string IdentityChanged = "CALIBRATION_OCR_DEVICE_CHECK_IDENTITY_CHANGED";
    public const string CheckFailed = "CALIBRATION_OCR_DEVICE_CHECK_FAILED";
    public const string CheckTimedOut = "CALIBRATION_OCR_DEVICE_CHECK_TIMEOUT";
    public const string GpuFallback = "CALIBRATION_OCR_DEVICE_CHECK_GPU_FALLBACK";
    private readonly object sync = new();
    private readonly CalibrationOcrDeviceSettings settings;
    private readonly Func<IReadOnlyList<CalibrationOcrDeviceSelection>, CancellationToken, Task<CalibrationOcrDeviceBenchmarkResult>> benchmark;
    private readonly Func<CancellationToken, Task<string>> identityReader;
    private readonly Func<CancellationToken, Task> idleWait;
    private CancellationTokenSource? idleCancellation;
    private CancellationTokenSource? activeCancellation;
    private Task? activeTask;
    private bool started, automaticAttempted, foregroundBusy, activeAutomatic, disposed;
    private CalibrationOcrDeviceOptimizerState state;
    private string? errorCode;
    private CalibrationOcrDeviceBenchmarkResult? result;

    public CalibrationOcrDeviceOptimizer(CalibrationOcrDeviceSettings settings, CalibrationOcrDeviceBenchmark benchmark,
        Func<CancellationToken, Task<string>> identityReader)
        : this(settings, benchmark.RunAsync, identityReader, token => Task.Delay(TimeSpan.FromSeconds(5), token)) { }

    internal CalibrationOcrDeviceOptimizer(CalibrationOcrDeviceSettings settings,
        Func<IReadOnlyList<CalibrationOcrDeviceSelection>, CancellationToken, Task<CalibrationOcrDeviceBenchmarkResult>> benchmark,
        Func<CancellationToken, Task<string>> identityReader, Func<CancellationToken, Task> idleWait)
    {
        this.settings = settings;
        this.benchmark = benchmark;
        this.identityReader = identityReader;
        this.idleWait = idleWait;
        settings.Changed += OnSettingsChanged;
    }

    public event EventHandler? Changed;
    public CalibrationOcrDeviceOptimizerState State { get { lock (sync) return state; } }
    public string? ErrorCode { get { lock (sync) return errorCode; } }
    public CalibrationOcrDeviceBenchmarkResult? Result { get { lock (sync) return result; } }
    public bool IsChecking { get { lock (sync) return activeCancellation is not null; } }

    public void Start()
    {
        lock (sync)
        {
            if (disposed || started) return;
            started = true;
            ScheduleLocked();
        }
        Notify();
    }

    public void SetForegroundBusy(bool busy)
    {
        lock (sync)
        {
            if (disposed) return;
            foregroundBusy = busy;
            if (busy)
            {
                idleCancellation?.Cancel();
                activeCancellation?.Cancel();
            }
            else ScheduleLocked();
        }
        Notify();
    }

    public Task CheckAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (sync)
        {
            if (disposed || foregroundBusy) return Task.CompletedTask;
            if (activeTask is { IsCompleted: false }) return activeTask.WaitAsync(cancellationToken);
            idleCancellation?.Cancel();
            task = BeginLocked(automatic: false, cancellationToken);
        }
        Notify();
        return task;
    }

    public void Cancel()
    {
        lock (sync)
        {
            if (disposed) return;
            automaticAttempted = true;
            idleCancellation?.Cancel();
            activeCancellation?.Cancel();
            state = CalibrationOcrDeviceOptimizerState.Cancelled;
            errorCode = null;
        }
        Notify();
    }

    private void ScheduleLocked()
    {
        if (!started || disposed || foregroundBusy || automaticAttempted || activeCancellation is not null ||
            idleCancellation is { IsCancellationRequested: false } || settings.Preference.Provider != "Auto" || !settings.NeedsAutomaticProfile) return;
        var source = new CancellationTokenSource();
        idleCancellation = source;
        state = CalibrationOcrDeviceOptimizerState.Waiting;
        _ = Task.Run(async () =>
        {
            try
            {
                await idleWait(source.Token).ConfigureAwait(false);
                lock (sync)
                {
                    if (disposed || foregroundBusy || source.IsCancellationRequested || automaticAttempted ||
                        activeCancellation is not null || settings.Preference.Provider != "Auto" || !settings.NeedsAutomaticProfile) return;
                    _ = BeginLocked(automatic: true, CancellationToken.None);
                }
                Notify();
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (sync)
                {
                    if (ReferenceEquals(idleCancellation, source)) idleCancellation = null;
                }
                source.Dispose();
            }
        });
    }

    private Task BeginLocked(bool automatic, CancellationToken caller)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(caller);
        activeCancellation = source;
        activeAutomatic = automatic;
        if (automatic) automaticAttempted = true;
        state = CalibrationOcrDeviceOptimizerState.Checking;
        errorCode = null;
        activeTask = Task.Run(() => RunCheckAsync(automatic, source), CancellationToken.None);
        return activeTask;
    }

    private async Task RunCheckAsync(bool automatic, CancellationTokenSource source)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            string identity = await identityReader(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            settings.SetProfileIdentity(identity);
            if (automatic && !settings.NeedsAutomaticProfile)
            {
                SetOutcome(CalibrationOcrDeviceOptimizerState.Ready, null);
                return;
            }
            lock (sync)
            {
                if (disposed || foregroundBusy || (automatic && settings.Preference.Provider != "Auto")) source.Cancel();
            }
            deadline.Token.ThrowIfCancellationRequested();
            CalibrationOcrDeviceBenchmarkResult measured = await benchmark(
                settings.AvailableDevices.Where(device => device.Provider != "Auto").ToArray(), deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            lock (sync) result = measured;
            CalibrationOcrDeviceMeasurement[] cpu = measured.Measurements.Where(row => row.Device.Provider == "Cpu").ToArray();
            if (cpu.Length != 1 || !ValidMeasurement(cpu[0]) ||
                !measured.Measurements.Any(row => row.Device == measured.SelectedDevice && ValidMeasurement(row)))
            {
                SetOutcome(CalibrationOcrDeviceOptimizerState.Failed, BaselineFailed);
                return;
            }
            string currentIdentity = await identityReader(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (!string.Equals(identity, currentIdentity, StringComparison.OrdinalIgnoreCase))
            {
                settings.SetProfileIdentity(currentIdentity);
                SetOutcome(CalibrationOcrDeviceOptimizerState.Failed, IdentityChanged);
                return;
            }
            // Preference is independently owned by settings; saving a profile never replaces an explicit override.
            if (!settings.TrySaveMeasuredProfile(identity, measured.SelectedDevice, out string? failure))
                SetOutcome(CalibrationOcrDeviceOptimizerState.Failed, failure ?? CheckFailed);
            else
                SetOutcome(CalibrationOcrDeviceOptimizerState.Ready,
                    measured.Measurements.Any(row => row.Device.Provider == "DirectMl" && !ValidMeasurement(row)) ? GpuFallback : null);
        }
        catch (OperationCanceledException)
        {
            SetOutcome(source.IsCancellationRequested ? CalibrationOcrDeviceOptimizerState.Cancelled : CalibrationOcrDeviceOptimizerState.Failed,
                source.IsCancellationRequested ? null : CheckTimedOut);
        }
        catch (Exception)
        {
            SetOutcome(CalibrationOcrDeviceOptimizerState.Failed, CheckFailed);
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(activeCancellation, source)) activeCancellation = null;
                activeAutomatic = false;
            }
            source.Dispose();
            Notify();
        }
    }

    private static bool ValidMeasurement(CalibrationOcrDeviceMeasurement row) => row.FailureCode is null && row.OutputMatchesCpu &&
        row.ColdMilliseconds is { } cold && double.IsFinite(cold) && cold >= 0 &&
        row.WarmMedianMilliseconds is { } warm && double.IsFinite(warm) && warm >= 0 &&
        row.FourReadTotalMilliseconds is { } total && double.IsFinite(total) && total >= 0;

    private void SetOutcome(CalibrationOcrDeviceOptimizerState next, string? failure)
    {
        lock (sync) { state = next; errorCode = failure; }
    }

    private void OnSettingsChanged(object? sender, EventArgs args)
    {
        lock (sync)
        {
            if (disposed) return;
            if (settings.Preference.Provider != "Auto")
            {
                idleCancellation?.Cancel();
                if (activeAutomatic) activeCancellation?.Cancel();
            }
            else ScheduleLocked();
        }
        Notify();
    }

    private void Notify()
    {
        lock (sync) { if (disposed) return; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            idleCancellation?.Cancel();
            activeCancellation?.Cancel();
        }
        settings.Changed -= OnSettingsChanged;
        GC.SuppressFinalize(this);
    }
}
