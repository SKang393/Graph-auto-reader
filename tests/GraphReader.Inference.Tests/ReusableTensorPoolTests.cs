// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Inference.Tests;

[TestClass]
public sealed class ReusableTensorPoolTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SequentialOwnedOcrShapesDoNotRetainMoreThanEightIdleTensors()
    {
        using var pool = new ReusableTensorPool();
        long[] firstShape = [1, 3, 48, 320];
        using (var first = pool.Rent(firstShape, 3 * 48 * 320)) first.Buffer[0] = 17;
        using (var repeat = pool.Rent(firstShape, 3 * 48 * 320))
        {
            Assert.IsTrue(repeat.WasReused);
            Assert.AreEqual(0f, repeat.Buffer[0]);
        }
        var lengths = new List<int>();
        for (int width = 320; width <= 576; width += 32)
        {
            using var lease = pool.Rent([1, 3, 48, width], 3 * 48 * width);
            lengths.Add(lease.Buffer.Length);
        }
        var held = new List<ReusableTensorPool.TensorLease>();
        try
        {
            for (int width = 320; width <= 576; width += 32)
                held.Add(pool.Rent([1, 3, 48, width], 3 * 48 * width));
            int reused = held.Count(lease => lease.WasReused);
            long reusedBytes = held.Where(lease => lease.WasReused).Sum(lease => (long)lease.Buffer.Length * sizeof(float));
            TestContext.WriteLine($"widths=320..576 step 32; rented_float_lengths={string.Join(',', lengths)}; rerented={held.Count}; reused={reused}; reused_bytes={reusedBytes}");
            Assert.IsLessThanOrEqualTo(8, reused, "Idle retained tensor count must be bounded.");
            Assert.IsLessThanOrEqualTo(16L * 1024 * 1024, reusedBytes, "Idle retained buffer bytes must be bounded.");
        }
        finally { foreach (var lease in held) lease.Dispose(); }
    }

    [TestMethod]
    public void RecentlyReusedShapeSurvivesEntryEviction()
    {
        using var pool = new ReusableTensorPool(maximumIdleEntries: 2, maximumIdleBytes: 4096);
        using (var a = pool.Rent([4], 4)) a.Buffer[0] = 42;
        using (pool.Rent([5], 5)) { }
        using (var a = pool.Rent([4], 4)) { Assert.IsTrue(a.WasReused); Assert.AreEqual(0f, a.Buffer[0]); }
        using (pool.Rent([6], 6)) { }
        using var old = pool.Rent([5], 5);
        using var recent = pool.Rent([4], 4);
        using var newest = pool.Rent([6], 6);
        Assert.IsFalse(old.WasReused);
        Assert.IsTrue(recent.WasReused);
        Assert.IsTrue(newest.WasReused);
    }

    [TestMethod]
    public void BufferLargerThanByteBudgetRemainsUsableButIsNotRetained()
    {
        using var pool = new ReusableTensorPool(maximumIdleEntries: 8, maximumIdleBytes: 4);
        int actual;
        using (var lease = pool.Rent([17], 17))
        {
            actual = lease.Buffer.Length;
            Assert.IsGreaterThan(4L, (long)actual * sizeof(float));
            lease.Buffer[16] = 7;
            Assert.AreEqual(7f, lease.Buffer[16]);
        }
        using var next = pool.Rent([17], 17);
        Assert.IsFalse(next.WasReused);
        Assert.AreEqual(0f, next.Buffer[16]);
    }

    [TestMethod]
    public void RetentionUsesActualRentedLengthRatherThanRequestedLength()
    {
        int firstLength;
        using (var measuring = new ReusableTensorPool(maximumIdleEntries: 0))
        using (var lease = measuring.Rent([17], 17)) firstLength = lease.Buffer.Length;
        long byteLimit = ((long)firstLength * sizeof(float)) - 1;
        using var pool = new ReusableTensorPool(maximumIdleEntries: 2, maximumIdleBytes: byteLimit);
        int actualLength;
        using (var lease = pool.Rent([17], 17)) actualLength = lease.Buffer.Length;
        using var next = pool.Rent([17], 17);
        TestContext.WriteLine($"requested_floats=17; rented_floats={actualLength}; idle_byte_limit={byteLimit}");
        Assert.AreEqual((long)actualLength * sizeof(float) <= byteLimit, next.WasReused);
    }

    [TestMethod]
    public void CumulativeByteLimitEvictsOldestWhileEachBufferFits()
    {
        long firstBytes;
        long secondBytes;
        using (var measuring = new ReusableTensorPool(maximumIdleEntries: 0))
        using (var first = measuring.Rent([17], 17))
        using (var second = measuring.Rent([1, 17], 17))
        {
            firstBytes = (long)first.Buffer.Length * sizeof(float);
            secondBytes = (long)second.Buffer.Length * sizeof(float);
        }
        long limit = Math.Max(firstBytes, secondBytes);
        using var pool = new ReusableTensorPool(maximumIdleEntries: 8, maximumIdleBytes: limit);
        using (var first = pool.Rent([17], 17))
        {
            firstBytes = (long)first.Buffer.Length * sizeof(float);
            Assert.IsLessThanOrEqualTo(limit, firstBytes);
        }
        using (var second = pool.Rent([1, 17], 17))
        {
            secondBytes = (long)second.Buffer.Length * sizeof(float);
            Assert.IsLessThanOrEqualTo(limit, secondBytes);
        }
        Assert.IsGreaterThan(limit, firstBytes + secondBytes);
        using var oldest = pool.Rent([17], 17);
        using var newest = pool.Rent([1, 17], 17);
        Assert.IsFalse(oldest.WasReused);
        Assert.IsTrue(newest.WasReused);
    }

    [TestMethod]
    public async Task ActiveLeasesRemainDistinctAndCanReturnAfterConcurrentDisposal()
    {
        var pool = new ReusableTensorPool(maximumIdleEntries: 2, maximumIdleBytes: 4096);
        const int count = 6;
        using var ready = new CountdownEvent(count);
        using var release = new ManualResetEventSlim();
        var buffers = new float[count][];
        Task[] tasks = Enumerable.Range(0, count).Select(index => Task.Factory.StartNew(() =>
        {
            using var lease = pool.Rent([16], 16);
            buffers[index] = lease.Buffer;
            lease.Buffer[0] = index + 1;
            ready.Signal();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)));
            Assert.AreEqual((float)(index + 1), lease.Buffer[0]);
            Assert.AreEqual((float)(index + 1), lease.Value.GetTensorDataAsSpan<float>()[0]);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try
        {
            Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(count, buffers.Distinct(ReferenceEqualityComparer.Instance).Count());
            pool.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => pool.Rent([16], 16));
        }
        finally { release.Set(); }
        await Task.WhenAll(tasks);
        pool.Dispose();
    }
}
