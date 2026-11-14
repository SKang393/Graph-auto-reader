// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using GraphReader.App.Services;
using GraphReader.Inference;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationOcrDeviceOptimizerTests
{
    [TestMethod]
    public async Task CancellingDuringIdentityReadDoesNotRestartTheAutomaticAttempt()
    {
        await WithSettings(async settings =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int benchmarks = 0;
            using var optimizer = Create(settings, (_, _) =>
            {
                Interlocked.Increment(ref benchmarks);
                throw new InvalidOperationException("No benchmark may start after identity cancellation.");
            }, async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new string('a', 64);
            }, _ => Task.CompletedTask);
            optimizer.Start();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task active = optimizer.CheckAsync(CancellationToken.None);
            optimizer.SetForegroundBusy(true);
            await active;
            optimizer.SetForegroundBusy(false);
            Assert.AreEqual(CalibrationOcrDeviceOptimizerState.Cancelled, optimizer.State);
            Assert.AreEqual(0, benchmarks);
            Assert.IsFalse(settings.ProfileIsCurrent);
            Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        });
    }

    [TestMethod]
    public async Task ForegroundCancelsTheSingleAutomaticAttemptWithoutSavingOrRetrying()
    {
        await WithSettings(async settings =>
        {
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            using var optimizer = Create(settings, async (_, token) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException();
            }, _ => Task.FromResult(new string('a', 64)), token => idle.Task.WaitAsync(token));
            optimizer.Start();
            idle.SetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task active = optimizer.CheckAsync(CancellationToken.None); // Joins, never overlaps.
            optimizer.SetForegroundBusy(true);
            await active;
            optimizer.SetForegroundBusy(false);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(CalibrationOcrDeviceOptimizerState.Cancelled, optimizer.State);
            Assert.IsNull(optimizer.ErrorCode);
            Assert.IsFalse(settings.ProfileIsCurrent);
            Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CallerCancellationAndDisposalStopOneSerializedCheck(bool dispose)
    {
        await WithSettings(async settings =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            using var caller = new CancellationTokenSource();
            using var optimizer = Create(settings, async (_, token) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException();
            });
            Task first = optimizer.CheckAsync(caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task second = optimizer.CheckAsync(CancellationToken.None);
            if (dispose) optimizer.Dispose(); else caller.Cancel();
            await Task.WhenAll(first, second);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(CalibrationOcrDeviceOptimizerState.Cancelled, optimizer.State);
            Assert.IsNull(optimizer.ErrorCode);
            Assert.IsFalse(settings.ProfileIsCurrent);
        });
    }

    [TestMethod]
    public async Task ManualProfileNeverReplacesAnExplicitChoiceMadeDuringTheCheck()
    {
        await WithSettings(async settings =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<CalibrationOcrDeviceBenchmarkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var optimizer = Create(settings, (_, _) => { entered.TrySetResult(); return finish.Task; });
            Task check = optimizer.CheckAsync(CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(settings.TrySelect(CalibrationOcrDeviceSelection.Cpu, out _));
            CalibrationOcrDeviceSelection gpu = settings.AvailableDevices.Single(device => device.Provider == "DirectMl");
            finish.SetResult(Success(gpu));
            await check;
            Assert.AreEqual(CalibrationOcrDeviceOptimizerState.Ready, optimizer.State);
            Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.Preference);
            Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
            Assert.IsTrue(settings.TrySelect(CalibrationOcrDeviceSelection.Automatic, out _));
            Assert.AreEqual(gpu, settings.SelectedDevice, "The measured profile is cached without replacing the explicit override.");
        });
    }

    [TestMethod]
    public async Task FailedCpuBaselineDoesNotBecomeACurrentProfile()
    {
        await WithSettings(async settings =>
        {
            using var optimizer = Create(settings, (_, _) => Task.FromResult(new CalibrationOcrDeviceBenchmarkResult(
                CalibrationOcrDeviceSelection.Cpu, [new(CalibrationOcrDeviceSelection.Cpu, null, null, null, false, "fixture_failure")])));
            await optimizer.CheckAsync(CancellationToken.None);
            Assert.AreEqual(CalibrationOcrDeviceOptimizerState.Failed, optimizer.State);
            Assert.AreEqual(CalibrationOcrDeviceOptimizer.BaselineFailed, optimizer.ErrorCode);
            Assert.IsFalse(settings.ProfileIsCurrent);
            Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        });
    }

    [TestMethod]
    public async Task ChangedRuntimeIdentityDiscardsTheMeasuredResult()
    {
        await WithSettings(async settings =>
        {
            int identities = 0;
            using var optimizer = Create(settings, (_, _) => Task.FromResult(Success(settings.AvailableDevices.Single(device => device.Provider == "DirectMl"))),
                _ => Task.FromResult(new string(Interlocked.Increment(ref identities) == 1 ? 'a' : 'b', 64)));
            await optimizer.CheckAsync(CancellationToken.None);
            Assert.AreEqual(CalibrationOcrDeviceOptimizer.IdentityChanged, optimizer.ErrorCode);
            Assert.IsFalse(settings.ProfileIsCurrent);
            Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, settings.SelectedDevice);
        });
    }

    private static CalibrationOcrDeviceOptimizer Create(CalibrationOcrDeviceSettings settings,
        Func<IReadOnlyList<CalibrationOcrDeviceSelection>, CancellationToken, Task<CalibrationOcrDeviceBenchmarkResult>> benchmark,
        Func<CancellationToken, Task<string>>? identity = null, Func<CancellationToken, Task>? idle = null) =>
        new(settings, benchmark, identity ?? (_ => Task.FromResult(new string('a', 64))),
            idle ?? (token => Task.Delay(Timeout.Infinite, token)));

    internal static CalibrationOcrDeviceBenchmarkResult Success(CalibrationOcrDeviceSelection gpu) => new(gpu,
        [new(CalibrationOcrDeviceSelection.Cpu, 10, 2, 16, true, null), new(gpu, 8, 1, 11, true, null)]);

    private static async Task WithSettings(Func<CalibrationOcrDeviceSettings, Task> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "device-optimizer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await action(new CalibrationOcrDeviceSettings(root, new TestInventory())); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestInventory : IDxgiAdapterInventory
    {
        public IReadOnlyList<DxgiAdapter> Enumerate() => [new(1, "Fixture GPU", 1, 1, 100, false)];
    }
}
