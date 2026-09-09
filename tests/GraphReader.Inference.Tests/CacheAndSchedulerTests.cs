// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using GraphReader.Inference;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Inference.Tests;

[TestClass]
public sealed class CacheAndSchedulerTests
{
    [TestMethod]
    public void CacheKeyIsCanonicalAndInvalidatesForModelOrParameterChanges()
    {
        var parametersA = new Dictionary<string, object?> { ["threshold"] = 0.5, ["mode"] = "fast" };
        var parametersReordered = new Dictionary<string, object?> { ["mode"] = "fast", ["threshold"] = 0.5 };
        var baseline = Key("a", parametersA);

        Assert.AreEqual(baseline, Key("a", parametersReordered));
        Assert.AreNotEqual(baseline, Key("b", parametersA));
        Assert.AreNotEqual(
            baseline,
            Key("a", new Dictionary<string, object?> { ["threshold"] = 0.6, ["mode"] = "fast" }));
    }

    [TestMethod]
    public async Task ContentAddressedCachePersistsHitAndMiss()
    {
        var root = TempDirectory();
        try
        {
            var cache = new ContentAddressedStageCache(root);
            var key = Key("model", new Dictionary<string, object?>());
            Assert.IsNull(await cache.TryGetAsync(key, CancellationToken.None));

            await cache.PutAsync(key, new byte[] { 1, 2, 3 }, CancellationToken.None);

            CollectionAssert.AreEqual(
                new byte[] { 1, 2, 3 },
                await cache.TryGetAsync(key, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SchedulerBoundsConcurrentRequests()
    {
        await using var scheduler = new BoundedInferenceScheduler(capacity: 3, workerCount: 2);
        var running = 0;
        var maximum = 0;
        var tasks = Enumerable.Range(0, 8).Select(index => scheduler.EnqueueAsync(
            async token =>
            {
                var current = Interlocked.Increment(ref running);
                UpdateMaximum(ref maximum, current);
                try
                {
                    await Task.Delay(30, token);
                    return index;
                }
                finally
                {
                    Interlocked.Decrement(ref running);
                }
            },
            TimeSpan.FromSeconds(2),
            CancellationToken.None).AsTask()).ToArray();

        var results = await Task.WhenAll(tasks);

        CollectionAssert.AreEquivalent(Enumerable.Range(0, 8).ToArray(), results);
        Assert.IsTrue(maximum <= 2);
        Assert.IsTrue(scheduler.MaximumObservedRunning <= 2);
    }

    [TestMethod]
    public async Task QueuedCancellationReturnsPromptly()
    {
        await using var scheduler = new BoundedInferenceScheduler(capacity: 1, workerCount: 1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await scheduler.EnqueueAsync(
                async token =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    return 1;
                },
                TimeSpan.FromSeconds(10),
                cancellation.Token));

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task SchedulerTimeoutReturnsPromptly()
    {
        await using var scheduler = new BoundedInferenceScheduler(capacity: 1, workerCount: 1);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsExactlyAsync<TimeoutException>(async () =>
            await scheduler.EnqueueAsync(
                async token =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    return 1;
                },
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task IgnoredCancellationRemainsInWorkerAccountingUntilOperationExits()
    {
        await using var scheduler = new BoundedInferenceScheduler(capacity: 1, workerCount: 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = scheduler.EnqueueAsync(
            async _ =>
            {
                await release.Task;
                return 1;
            },
            TimeSpan.FromMilliseconds(40),
            CancellationToken.None).AsTask();

        await Assert.ThrowsExactlyAsync<TimeoutException>(async () => await timedOut);
        Assert.AreEqual(1, scheduler.RunningCount);

        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = scheduler.EnqueueAsync(
            _ =>
            {
                secondStarted.SetResult();
                return ValueTask.FromResult(2);
            },
            TimeSpan.FromSeconds(1),
            CancellationToken.None).AsTask();
        await Task.Delay(30);
        Assert.IsFalse(secondStarted.Task.IsCompleted);

        release.SetResult();
        Assert.AreEqual(2, await second);
        Assert.AreEqual(0, scheduler.RunningCount);
    }

    [TestMethod]
    public async Task TimeoutDeadlineIncludesBoundedChannelAdmissionWait()
    {
        await using var scheduler = new BoundedInferenceScheduler(capacity: 1, workerCount: 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = scheduler.EnqueueAsync(
            async _ =>
            {
                started.SetResult();
                await release.Task;
                return 1;
            },
            Timeout.InfiniteTimeSpan,
            CancellationToken.None).AsTask();
        await started.Task;
        var second = scheduler.EnqueueAsync(
            _ => ValueTask.FromResult(2),
            Timeout.InfiniteTimeSpan,
            CancellationToken.None).AsTask();
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsExactlyAsync<TimeoutException>(async () =>
            await scheduler.EnqueueAsync(
                _ => ValueTask.FromResult(3),
                TimeSpan.FromMilliseconds(40),
                CancellationToken.None));

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        release.SetResult();
        Assert.AreEqual(1, await first);
        Assert.AreEqual(2, await second);
    }

    [TestMethod]
    public async Task DisposalIsBoundedWhenOperationIgnoresCancellation()
    {
        var scheduler = new BoundedInferenceScheduler(
            capacity: 1,
            workerCount: 1,
            shutdownTimeout: TimeSpan.FromMilliseconds(40));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = scheduler.EnqueueAsync(
            async _ =>
            {
                started.SetResult();
                await release.Task;
                finished.SetResult();
                return 1;
            },
            Timeout.InfiniteTimeSpan,
            CancellationToken.None).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Verify disposal can finish while native work remains blocked. The outer
            // deadline catches an unbounded join without grading shared CI CPU latency.
            await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.IsFalse(finished.Task.IsCompleted);
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
                await work.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestMethod]
    public async Task ConcurrentAtomicOverwriteNeverProducesTornCachePayload()
    {
        var root = TempDirectory();
        try
        {
            var cache = new ContentAddressedStageCache(root);
            var key = Key("atomic", new Dictionary<string, object?>());
            var first = Enumerable.Repeat((byte)0x2a, 32 * 1024).ToArray();
            var second = Enumerable.Repeat((byte)0xd4, 32 * 1024).ToArray();

            await Task.WhenAll(
                cache.PutAsync(key, first, CancellationToken.None).AsTask(),
                cache.PutAsync(key, second, CancellationToken.None).AsTask());
            var result = await cache.TryGetAsync(key, CancellationToken.None);

            Assert.IsNotNull(result);
            Assert.IsTrue(result.SequenceEqual(first) || result.SequenceEqual(second));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CanceledCacheOverwritePreservesExistingPayloadAndRemovesTemporaryFile()
    {
        var root = TempDirectory();
        try
        {
            var cache = new ContentAddressedStageCache(root);
            var key = Key("cancel", new Dictionary<string, object?>());
            await cache.PutAsync(key, new byte[] { 1, 2, 3 }, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
                await cache.PutAsync(key, new byte[1024], cancellation.Token));

            CollectionAssert.AreEqual(
                new byte[] { 1, 2, 3 },
                await cache.TryGetAsync(key, CancellationToken.None));
            Assert.IsEmpty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StageCacheKey Key(string modelHashSeed, IReadOnlyDictionary<string, object?> parameters) =>
        StageCacheKey.Create("input", "0,0,100,100", "identity", "markers", "1", modelHashSeed, parameters, 1);

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "GraphReaderInferenceCacheTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void UpdateMaximum(ref int maximum, int current)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (observed >= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maximum, current, observed) != observed);
    }
}
