// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using GraphReader.Inference;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Inference.Tests;

[TestClass]
public sealed class InferenceRuntimeTests
{
    [TestMethod]
    public async Task RuntimeCachesDeterministicOutputAndRecordsStageTiming()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var cacheRoot = TempDirectory();
        try
        {
            var factory = new FakeInferenceSessionFactory(scale: 3);
            await using var runtime = CreateRuntime(model, cacheRoot, factory);
            var request = Request(model, TimeSpan.FromSeconds(2));

            var cold = await runtime.RunAsync(request, CancellationToken.None);
            var cached = await runtime.RunAsync(request, CancellationToken.None);

            Assert.IsTrue(cold.Succeeded);
            Assert.IsTrue(cold.Execution!.Timing.ColdSession);
            Assert.IsFalse(cold.Execution.Timing.CacheHit);
            CollectionAssert.AreEqual(new float[] { 3, 6, 9 }, cold.Execution.Output.ToArray());
            Assert.IsTrue(cold.Execution.Timing.TotalMilliseconds >= 0);
            Assert.IsTrue(cached.Succeeded);
            Assert.IsTrue(cached.Execution!.Timing.CacheHit);
            CollectionAssert.AreEqual(cold.Execution.Output.ToArray(), cached.Execution.Output.ToArray());
            Assert.AreEqual(1, factory.Sessions.Single().RunCount);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeReturnsStructuredTimeout()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var cacheRoot = TempDirectory();
        try
        {
            await using var runtime = CreateRuntime(
                model,
                cacheRoot,
                new FakeInferenceSessionFactory(runDelay: TimeSpan.FromSeconds(5)));
            var stopwatch = Stopwatch.StartNew();

            var result = await runtime.RunAsync(Request(model, TimeSpan.FromMilliseconds(50)), CancellationToken.None);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("INFERENCE_TIMEOUT", result.Error!.Code);
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimePropagatesCallerCancellationPromptly()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var cacheRoot = TempDirectory();
        try
        {
            await using var runtime = CreateRuntime(
                model,
                cacheRoot,
                new FakeInferenceSessionFactory(runDelay: TimeSpan.FromSeconds(5)));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
                await runtime.RunAsync(Request(model, TimeSpan.FromSeconds(10)), cancellation.Token));
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task DirectMlRunFailureRetriesMandatoryCpuProvider()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var cacheRoot = TempDirectory();
        try
        {
            var factory = new FakeInferenceSessionFactory(
                runFailingProviders: new[] { InferenceProvider.DirectMl },
                scale: 4);
            var registry = new OnnxSessionRegistry(
                new FakeExecutionProviderDiscovery("DmlExecutionProvider", "CPUExecutionProvider"),
                new WindowsExecutionProviderPolicy(),
                factory,
                CpuThreadConfiguration.Create(1, new FixedCoreDetector()));
            await using var runtime = new InferenceRuntime(
                registry,
                new BoundedInferenceScheduler(2, 1),
                new ContentAddressedStageCache(cacheRoot));

            var result = await runtime.RunAsync(Request(model, TimeSpan.FromSeconds(2)), CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(InferenceProvider.Cpu, result.Execution!.Provider);
            CollectionAssert.AreEqual(new float[] { 4, 8, 12 }, result.Execution.Output.ToArray());
            Assert.AreEqual(2, factory.CreatedCount);
            Assert.IsTrue(result.ProviderAttempts.Any(attempt =>
                attempt.Provider == InferenceProvider.DirectMl && !attempt.Succeeded));
            Assert.IsTrue(result.ProviderAttempts.Any(attempt =>
                attempt.Provider == InferenceProvider.Cpu && attempt.Succeeded));
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task CacheBypassForcesCurrentSessionExecutionAfterAValidEntryExists()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var cacheRoot = TempDirectory();
        try
        {
            var factory = new FakeInferenceSessionFactory(scale: 3);
            await using var runtime = CreateRuntime(model, cacheRoot, factory);
            InferenceRequest request = Request(model, TimeSpan.FromSeconds(2));

            InferenceResponse cold = await runtime.RunAsync(request, CancellationToken.None);
            InferenceResponse cached = await runtime.RunAsync(request, CancellationToken.None);
            InferenceResponse forced = await runtime.RunAsync(
                request with { BypassCache = true },
                CancellationToken.None);

            Assert.IsTrue(cold.Succeeded);
            Assert.IsTrue(cached.Execution?.Timing.CacheHit);
            Assert.IsTrue(forced.Succeeded);
            Assert.IsFalse(forced.Execution?.Timing.CacheHit);
            Assert.AreEqual(2, factory.Sessions.Single().RunCount);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExplicitCpuPolicySkipsDirectMlAndUsesASeparateCacheIdentity()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var cacheRoot = TempDirectory();
        try
        {
            var factory = new FakeInferenceSessionFactory(scale: 4);
            var registry = new OnnxSessionRegistry(
                new FakeExecutionProviderDiscovery("DmlExecutionProvider", "CPUExecutionProvider"),
                new WindowsExecutionProviderPolicy(),
                factory,
                CpuThreadConfiguration.Create(1, new FixedCoreDetector()));
            await using var runtime = new InferenceRuntime(
                registry,
                new BoundedInferenceScheduler(2, 1),
                new ContentAddressedStageCache(cacheRoot));
            InferenceRequest defaultPolicy = Request(model, TimeSpan.FromSeconds(2));
            InferenceRequest cpuPolicy = defaultPolicy with
            {
                AllowedProviders = [InferenceProvider.Cpu],
            };

            InferenceResponse directMl = await runtime.RunAsync(defaultPolicy, CancellationToken.None);
            InferenceResponse cpu = await runtime.RunAsync(cpuPolicy, CancellationToken.None);
            InferenceResponse cpuCached = await runtime.RunAsync(cpuPolicy, CancellationToken.None);

            Assert.AreEqual(InferenceProvider.DirectMl, directMl.Execution?.Provider);
            Assert.AreEqual(InferenceProvider.Cpu, cpu.Execution?.Provider);
            Assert.IsFalse(cpu.Execution?.Timing.CacheHit);
            Assert.IsTrue(cpuCached.Execution?.Timing.CacheHit);
            CollectionAssert.AreEquivalent(
                new[] { InferenceProvider.DirectMl, InferenceProvider.Cpu },
                factory.Sessions.Select(static session => session.Provider).ToArray());
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HostCpuPolicyCannotBeBroadenedOrReuseGpuCache(bool explicitBroadRequest)
    {
        using var model = TestOnnxModel.CreateIdentity();
        string cacheRoot = TempDirectory();
        try
        {
            var factory = new FakeInferenceSessionFactory();
            OnnxSessionRegistry Registry() => new(
                new FakeExecutionProviderDiscovery("DmlExecutionProvider", "CPUExecutionProvider"),
                new WindowsExecutionProviderPolicy(), factory,
                CpuThreadConfiguration.Create(1, new FixedCoreDetector()));
            await using var normal = new InferenceRuntime(Registry(), new BoundedInferenceScheduler(2, 1),
                new ContentAddressedStageCache(cacheRoot));
            InferenceRequest request = Request(model, TimeSpan.FromSeconds(2));
            Assert.AreEqual(InferenceProvider.DirectMl,
                (await normal.RunAsync(request, CancellationToken.None)).Execution?.Provider);
            var hostProviders = new List<InferenceProvider> { InferenceProvider.Cpu };
            await using var cpuOnly = new InferenceRuntime(Registry(), new BoundedInferenceScheduler(2, 1),
                new ContentAddressedStageCache(cacheRoot), allowedProviders: hostProviders);
            hostProviders.Add(InferenceProvider.DirectMl); // Constructor must retain its own policy snapshot.
            if (explicitBroadRequest)
                request = request with { AllowedProviders = [InferenceProvider.DirectMl, InferenceProvider.Cpu] };

            InferenceResponse cold = await cpuOnly.RunAsync(request, CancellationToken.None);
            InferenceResponse cached = await cpuOnly.RunAsync(request, CancellationToken.None);

            Assert.AreEqual(InferenceProvider.Cpu, cold.Execution?.Provider);
            Assert.IsFalse(cold.Execution?.Timing.CacheHit);
            Assert.AreEqual(InferenceProvider.Cpu, cached.Execution?.Provider);
            Assert.IsTrue(cached.Execution?.Timing.CacheHit);
            Assert.AreEqual(2, factory.CreatedCount);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task RequestCanNarrowTheHostPolicyToCpu()
    {
        using var model = TestOnnxModel.CreateIdentity();
        string cacheRoot = TempDirectory();
        try
        {
            var factory = new FakeInferenceSessionFactory();
            var registry = new OnnxSessionRegistry(
                new FakeExecutionProviderDiscovery("DmlExecutionProvider", "CPUExecutionProvider"),
                new WindowsExecutionProviderPolicy(), factory,
                CpuThreadConfiguration.Create(1, new FixedCoreDetector()));
            await using var runtime = new InferenceRuntime(registry, new BoundedInferenceScheduler(2, 1),
                new ContentAddressedStageCache(cacheRoot),
                allowedProviders: [InferenceProvider.DirectMl, InferenceProvider.Cpu]);

            InferenceResponse result = await runtime.RunAsync(
                Request(model, TimeSpan.FromSeconds(2)) with { AllowedProviders = [InferenceProvider.Cpu] },
                CancellationToken.None);

            Assert.AreEqual(InferenceProvider.Cpu, result.Execution?.Provider);
            Assert.AreEqual(InferenceProvider.Cpu, factory.Sessions.Single().Provider);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExplicitProviderPolicyRejectsMissingCpuFakeAndDuplicates()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var cacheRoot = TempDirectory();
        try
        {
            await using var runtime = CreateRuntime(
                model,
                cacheRoot,
                new FakeInferenceSessionFactory());
            InferenceRequest request = Request(model, TimeSpan.FromSeconds(2));

            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                runtime.RunAsync(
                    request with { AllowedProviders = [InferenceProvider.DirectMl] },
                    CancellationToken.None).AsTask());
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                runtime.RunAsync(
                    request with { AllowedProviders = [InferenceProvider.Cpu, InferenceProvider.Fake] },
                    CancellationToken.None).AsTask());
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                runtime.RunAsync(
                    request with { AllowedProviders = [InferenceProvider.Cpu, InferenceProvider.Cpu] },
                    CancellationToken.None).AsTask());
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                runtime.RunAsync(
                    request with { AllowedProviders = [InferenceProvider.Cpu, (InferenceProvider)99] },
                    CancellationToken.None).AsTask());
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [TestMethod]
    public void DerivedCacheKeyIsolatedByActualTensorAndModelIdentity()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var baseline = Request(model, TimeSpan.FromSeconds(1));
        var otherInput = baseline with
        {
            Input = new InferenceInput(new float[] { 1, 2, 4 }, new long[] { 1, 3 })
        };
        var otherModel = baseline with
        {
            Model = baseline.Model with { Sha256 = new string('a', 64) }
        };

        Assert.AreNotEqual(
            InferenceCacheKeyDeriver.Derive(baseline),
            InferenceCacheKeyDeriver.Derive(otherInput));
        Assert.AreNotEqual(
            InferenceCacheKeyDeriver.Derive(baseline),
            InferenceCacheKeyDeriver.Derive(otherModel));
    }

    [TestMethod]
    public async Task CorruptCachePayloadFailsIntegrityAndIsRecomputed()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var cacheRoot = TempDirectory();
        try
        {
            var factory = new FakeInferenceSessionFactory(scale: 3);
            await using var runtime = CreateRuntime(model, cacheRoot, factory);
            var request = Request(model, TimeSpan.FromSeconds(2));
            _ = await runtime.RunAsync(request, CancellationToken.None);
            var cacheFile = Directory.GetFiles(cacheRoot, "*.bin", SearchOption.AllDirectories).Single();
            var bytes = await File.ReadAllBytesAsync(cacheFile);
            bytes[^1] ^= 0xff;
            await File.WriteAllBytesAsync(cacheFile, bytes);

            var recomputed = await runtime.RunAsync(request, CancellationToken.None);
            var cached = await runtime.RunAsync(request, CancellationToken.None);

            Assert.IsTrue(recomputed.Succeeded);
            Assert.IsFalse(recomputed.Execution!.Timing.CacheHit);
            Assert.IsTrue(cached.Execution!.Timing.CacheHit);
            Assert.AreEqual(2, factory.Sessions.Single().RunCount);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeDisposalIsBoundedWhileNativeRunDrainsBeforeSessionDisposal()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var cacheRoot = TempDirectory();
        try
        {
            var factory = new ControlledIgnoringSessionFactory();
            var registry = new OnnxSessionRegistry(
                new FakeExecutionProviderDiscovery("CPUExecutionProvider"),
                new WindowsExecutionProviderPolicy(),
                factory,
                CpuThreadConfiguration.Create(1, new FixedCoreDetector()));
            var runtime = new InferenceRuntime(
                registry,
                new BoundedInferenceScheduler(
                    capacity: 1,
                    workerCount: 1,
                    shutdownTimeout: TimeSpan.FromMilliseconds(30)),
                new ContentAddressedStageCache(cacheRoot),
                disposalTimeout: TimeSpan.FromMilliseconds(70));
            var run = runtime.RunAsync(Request(model, Timeout.InfiniteTimeSpan), CancellationToken.None).AsTask();
            await factory.Session.Started.Task;
            var stopwatch = Stopwatch.StartNew();

            await runtime.DisposeAsync();

            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.IsFalse(factory.Session.IsDisposed);
            factory.Session.Release.SetResult();
            await factory.Session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsTrue(factory.Session.IsDisposed);
            _ = await run;
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    private static InferenceRuntime CreateRuntime(
        TestOnnxModel model,
        string cacheRoot,
        FakeInferenceSessionFactory factory)
    {
        var registry = new OnnxSessionRegistry(
            new FakeExecutionProviderDiscovery("CPUExecutionProvider"),
            new WindowsExecutionProviderPolicy(),
            factory,
            CpuThreadConfiguration.Create(1, new FixedCoreDetector()));
        return new InferenceRuntime(
            registry,
            new BoundedInferenceScheduler(capacity: 2, workerCount: 1),
            new ContentAddressedStageCache(cacheRoot));
    }

    private static InferenceRequest Request(TestOnnxModel model, TimeSpan timeout) =>
        new(
            new ModelIdentity("fake", "1", model.Sha256, model.Path),
            new InferenceInput(new float[] { 1, 2, 3 }, new long[] { 1, 3 }),
            new StageCacheMaterial(
                "input",
                "crop",
                "transform",
                "test",
                "1",
                new Dictionary<string, object?> { ["scale"] = 3 },
                1),
            timeout);

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "GraphReaderInferenceRuntimeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedCoreDetector : IPhysicalCoreDetector
    {
        public int GetPhysicalCoreCount() => 1;
    }

    private sealed class ControlledIgnoringSessionFactory : IInferenceSessionFactory
    {
        public ControlledIgnoringSession Session { get; } = new();

        public ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IInferenceSession>(Session);
    }

    private sealed class ControlledIgnoringSession : IInferenceSession
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InferenceProvider Provider => InferenceProvider.Cpu;

        public bool IsDisposed { get; private set; }

        public async ValueTask<InferenceExecution> RunAsync(
            InferenceInput input,
            CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Release.Task;
            return new InferenceExecution(
                Array.AsReadOnly(input.Values.ToArray()),
                Provider,
                new StageTiming(0, 0, 0, 0, 0, true, false),
                new MemoryDiagnostics(0, 0, 0, 0, 0));
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
