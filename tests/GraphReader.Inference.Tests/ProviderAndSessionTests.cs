// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Inference;
using Microsoft.ML.OnnxRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Inference.Tests;

[TestClass]
public sealed class ProviderAndSessionTests
{
    private static readonly string[] CpuAndDirectMlProviders = ["CPUExecutionProvider", "DmlExecutionProvider"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void OnnxFactoryPreservesRuntimeDefaultGraphOptimizationForExistingCallers()
    {
        var factory = new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance);
        using var defaults = new SessionOptions();
        using SessionOptions actual = factory.CreateSessionOptions(
            InferenceProvider.Cpu,
            CpuThreadConfiguration.Create(1, new FixedCoreDetector(2)));

        Assert.AreEqual(defaults.GraphOptimizationLevel, actual.GraphOptimizationLevel);
        Assert.AreEqual(OnnxGraphOptimizationMode.RuntimeDefault, factory.GraphOptimizationMode);
    }

    [TestMethod]
    public void OnnxFactoryCanBindDisabledGraphOptimizationWithoutRunningInference()
    {
        var factory = new OnnxInferenceSessionFactory(
            NoUiThreadGuard.Instance,
            OnnxGraphOptimizationMode.Disabled);
        using SessionOptions actual = factory.CreateSessionOptions(
            InferenceProvider.Cpu,
            CpuThreadConfiguration.Create(1, new FixedCoreDetector(2)));

        Assert.AreEqual(GraphOptimizationLevel.ORT_DISABLE_ALL, actual.GraphOptimizationLevel);
        Assert.AreEqual(OnnxGraphOptimizationMode.Disabled, factory.GraphOptimizationMode);
    }

    [TestMethod]
    public void OnnxFactoryRejectsUnknownGraphOptimizationMode()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new OnnxInferenceSessionFactory(
                NoUiThreadGuard.Instance,
                (OnnxGraphOptimizationMode)int.MaxValue));
    }

    [TestMethod]
    public void RuntimeProviderDiscoveryFindsMandatoryCpuProvider()
    {
        var providers = new OrtExecutionProviderDiscovery().GetAvailableProviders();

        CollectionAssert.Contains(providers.ToArray(), "CPUExecutionProvider");
        TestContext.WriteLine("available_providers=" + string.Join(",", providers));
    }

    [TestMethod]
    public void WindowsPolicyOrdersDirectMlBeforeMandatoryCpuAndIsDistributionNeutral()
    {
        var policy = new WindowsExecutionProviderPolicy();
        var installed = policy.GetOrderedProviders(CpuAndDirectMlProviders);
        var portable = policy.GetOrderedProviders(CpuAndDirectMlProviders);

        CollectionAssert.AreEqual(new[] { InferenceProvider.DirectMl, InferenceProvider.Cpu }, installed.ToArray());
        CollectionAssert.AreEqual(installed.ToArray(), portable.ToArray());
        CollectionAssert.AreEqual(
            new[] { InferenceProvider.Cpu },
            policy.GetOrderedProviders(Array.Empty<string>()).ToArray());
    }

    [TestMethod]
    public void CpuThreadConfigurationHonorsPhysicalCoreAwareDefaultAndOverride()
    {
        var detector = new FixedCoreDetector(3);

        var defaults = CpuThreadConfiguration.Create(detector: detector);
        var overridden = CpuThreadConfiguration.Create(2, detector);

        Assert.AreEqual(3, defaults.PhysicalCoreCount);
        Assert.AreEqual(Math.Min(3, Environment.ProcessorCount), defaults.IntraOperationThreads);
        Assert.AreEqual(2, overridden.IntraOperationThreads);
        Assert.AreEqual(1, overridden.InterOperationThreads);
    }

    [TestMethod]
    public async Task MissingGpuFallsBackToCpuAndRunsRealOnnxModel()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var actualFactory = new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance);
        var factory = new FailDirectMlFactory(actualFactory);
        await using var registry = CreateRegistry(
            new FakeExecutionProviderDiscovery("DmlExecutionProvider", "CPUExecutionProvider"),
            factory);

        var acquisition = await registry.GetOrCreateAsync(Model(model), CancellationToken.None);
        Assert.IsTrue(acquisition.Succeeded);
        Assert.AreEqual(InferenceProvider.Cpu, acquisition.Session!.Provider);
        Assert.AreEqual(2, acquisition.Attempts.Count);
        Assert.IsFalse(acquisition.Attempts[0].Succeeded);
        Assert.IsTrue(acquisition.Attempts[1].Succeeded);

        var execution = await Task.Run(async () => await acquisition.Session.RunAsync(
            new InferenceInput(new float[] { 1, 2, 3 }, new long[] { 1, 3 }),
            CancellationToken.None));
        CollectionAssert.AreEqual(new float[] { 1, 2, 3 }, execution.Output.ToArray());
    }

    [TestMethod]
    public async Task RealCpuSessionIsReusedAndProducesDeterministicColdAndWarmOutput()
    {
        using var model = TestOnnxModel.CreateIdentity();
        await using var registry = CreateRegistry(
            new FakeExecutionProviderDiscovery("CPUExecutionProvider"),
            new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance));
        var identity = Model(model);

        var firstAcquisition = await registry.GetOrCreateAsync(identity, CancellationToken.None);
        var secondAcquisition = await registry.GetOrCreateAsync(identity, CancellationToken.None);
        Assert.AreSame(firstAcquisition.Session, secondAcquisition.Session);
        Assert.AreEqual(1, registry.CreatedSessionCount);

        var input = new InferenceInput(new float[] { 4, 5, 6 }, new long[] { 1, 3 });
        var first = await Task.Run(async () => await firstAcquisition.Session!.RunAsync(input, CancellationToken.None));
        var second = await Task.Run(async () => await secondAcquisition.Session!.RunAsync(input, CancellationToken.None));

        CollectionAssert.AreEqual(first.Output.ToArray(), second.Output.ToArray());
        Assert.IsTrue(first.Timing.ColdSession);
        Assert.IsFalse(second.Timing.ColdSession);
        Assert.IsTrue(first.Timing.TotalMilliseconds >= 0);
        Assert.IsTrue(second.Timing.InferenceMilliseconds >= 0);
        Assert.IsTrue(first.Memory.RentedBufferLength >= 3);
        Assert.IsFalse(first.Memory.ReusedTensorBuffer);
        Assert.IsTrue(second.Memory.ReusedTensorBuffer);
        TestContext.WriteLine(
            $"cold_session_creation_ms={first.Timing.SessionCreationMilliseconds:F3}; " +
            $"cold_total_ms={first.Timing.TotalMilliseconds:F3}; " +
            $"warm_total_ms={second.Timing.TotalMilliseconds:F3}; " +
            $"warm_tensor_reused={second.Memory.ReusedTensorBuffer}");
    }

    [TestMethod]
    public async Task RealFactoryHonorsPreCanceledCreation()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var factory = new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await factory.CreateAsync(
                Model(model),
                InferenceProvider.Cpu,
                CpuThreadConfiguration.Create(1, new FixedCoreDetector(2)),
                cancellation.Token));
    }

    [TestMethod]
    public async Task DisposedRealCpuSessionRejectsFurtherRuns()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var factory = new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance);
        var session = await factory.CreateAsync(
            Model(model),
            InferenceProvider.Cpu,
            CpuThreadConfiguration.Create(1, new FixedCoreDetector(2)),
            CancellationToken.None);

        await session.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await session.RunAsync(
                new InferenceInput(new float[] { 1, 2, 3 }, new long[] { 1, 3 }),
                CancellationToken.None));
    }

    [TestMethod]
    public async Task CapturedUiThreadIsRejectedBeforeRealInference()
    {
        using var model = TestOnnxModel.CreateIdentity();
        await using var registry = CreateRegistry(
            new FakeExecutionProviderDiscovery("CPUExecutionProvider"),
            new OnnxInferenceSessionFactory(new RejectingUiThreadGuard()));
        var acquisition = await registry.GetOrCreateAsync(Model(model), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await acquisition.Session!.RunAsync(
                new InferenceInput(new float[] { 1, 2, 3 }, new long[] { 1, 3 }),
                CancellationToken.None));
    }

    [TestMethod]
    public void CapturedUiThreadGuardRejectsItsCapturedThread()
    {
        var guard = CapturedUiThreadGuard.CaptureCurrentThread();

        Assert.ThrowsExactly<UiThreadInferenceException>(guard.ThrowIfCurrentThreadIsUiThread);
    }

    [TestMethod]
    public async Task CancellationBeforeDirectMlGateAcquisitionDoesNotOverReleaseGate()
    {
        using var gate = new SerializedRunGate();
        using var firstLease = await gate.AcquireAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await gate.AcquireAsync(cancellation.Token));
        firstLease.Dispose();

        using var thirdLease = await gate.AcquireAsync(CancellationToken.None);
        Assert.IsNotNull(thirdLease);
    }

    [TestMethod]
    public async Task RegistryDisposesCreatedSessionMemoryOwner()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var factory = new FakeInferenceSessionFactory();
        var registry = CreateRegistry(new FakeExecutionProviderDiscovery("CPUExecutionProvider"), factory);
        _ = await registry.GetOrCreateAsync(Model(model), CancellationToken.None);

        await registry.DisposeAsync();

        Assert.HasCount(1, factory.Sessions);
        Assert.IsTrue(factory.Sessions[0].IsDisposed);
    }

    [TestMethod]
    public async Task AllProviderFailuresReturnStructuredRecoverableError()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var factory = new FakeInferenceSessionFactory(
            new[] { InferenceProvider.DirectMl, InferenceProvider.Cpu });
        await using var registry = CreateRegistry(
            new FakeExecutionProviderDiscovery("DmlExecutionProvider"),
            factory);

        var result = await registry.GetOrCreateAsync(Model(model), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("INFERENCE_PROVIDER_UNAVAILABLE", result.Error!.Code);
        Assert.IsTrue(result.Error.Recoverable);
        Assert.HasCount(2, result.Attempts);
    }

    [TestMethod]
    public async Task ProviderDiscoveryFailureStillUsesMandatoryCpuFallback()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var factory = new FakeInferenceSessionFactory();
        await using var registry = CreateRegistry(new ThrowingDiscovery(), factory);

        var result = await registry.GetOrCreateAsync(Model(model), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(InferenceProvider.Cpu, result.Session!.Provider);
        Assert.HasCount(2, result.Attempts);
        Assert.IsFalse(result.Attempts[0].Succeeded);
        Assert.IsTrue(result.Attempts[1].Succeeded);
    }

    [TestMethod]
    public async Task ChecksumMismatchReturnsStructuredErrorWithoutCreatingSession()
    {
        using var model = TestOnnxModel.CreateIdentity();
        await using var registry = CreateRegistry(
            new FakeExecutionProviderDiscovery("CPUExecutionProvider"),
            new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance));
        var invalidIdentity = new ModelIdentity("test.identity", "1", new string('0', 64), model.Path);

        var result = await registry.GetOrCreateAsync(invalidIdentity, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("MODEL_CHECKSUM_MISMATCH", result.Error!.Code);
        Assert.AreEqual(0, registry.CreatedSessionCount);
    }

    [TestMethod]
    public async Task RegistryDisposalDrainsActiveDirectMlRunBeforeDisposingSession()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var factory = new BlockingSessionFactory();
        var registry = CreateRegistry(
            new FakeExecutionProviderDiscovery("DmlExecutionProvider", "CPUExecutionProvider"),
            factory);
        var acquisition = await registry.GetOrCreateAsync(Model(model), CancellationToken.None);
        var run = acquisition.Session!.RunAsync(
            new InferenceInput(new float[] { 1, 2, 3 }, new long[] { 1, 3 }),
            CancellationToken.None).AsTask();
        await factory.Session.Started.Task;

        var disposal = registry.DisposeAsync().AsTask();
        await Task.Delay(30);
        Assert.IsFalse(disposal.IsCompleted);
        Assert.IsFalse(factory.Session.IsDisposed);

        factory.Session.Release.SetResult();
        _ = await run;
        await disposal;
        Assert.IsTrue(factory.Session.IsDisposed);
    }

    [TestMethod]
    public async Task RegistryDisposalCannotMissDelayedSessionCreationOrReturnLateSession()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var factory = new DelayedSessionFactory();
        var registry = CreateRegistry(new FakeExecutionProviderDiscovery("CPUExecutionProvider"), factory);
        var acquisition = registry.GetOrCreateAsync(Model(model), CancellationToken.None).AsTask();
        await factory.Started.Task;

        var disposal = registry.DisposeAsync().AsTask();
        factory.Release.SetResult();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await acquisition);
        await disposal;
        Assert.IsTrue(factory.Session.IsDisposed);
        Assert.AreEqual(1, registry.CreatedSessionCount);
    }

    private static OnnxSessionRegistry CreateRegistry(
        IExecutionProviderDiscovery discovery,
        IInferenceSessionFactory factory) =>
        new(
            discovery,
            new WindowsExecutionProviderPolicy(),
            factory,
            CpuThreadConfiguration.Create(1, new FixedCoreDetector(2)));

    private static ModelIdentity Model(TestOnnxModel model) =>
        new("test.identity", "1", model.Sha256, model.Path);

    private sealed class FixedCoreDetector(int count) : IPhysicalCoreDetector
    {
        public int GetPhysicalCoreCount() => Math.Min(count, Environment.ProcessorCount);
    }

    private sealed class FailDirectMlFactory(IInferenceSessionFactory inner) : IInferenceSessionFactory
    {
        public ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken) =>
            provider == InferenceProvider.DirectMl
                ? ValueTask.FromException<IInferenceSession>(new InvalidOperationException("No compatible GPU adapter."))
                : inner.CreateAsync(model, provider, cpuConfiguration, cancellationToken);
    }

    private sealed class RejectingUiThreadGuard : IUiThreadGuard
    {
        public void ThrowIfCurrentThreadIsUiThread() =>
            throw new InvalidOperationException("Simulated UI thread.");
    }

    private sealed class ThrowingDiscovery : IExecutionProviderDiscovery
    {
        public IReadOnlyList<string> GetAvailableProviders() =>
            throw new InvalidOperationException("Simulated provider discovery failure.");
    }

    private sealed class BlockingSessionFactory : IInferenceSessionFactory
    {
        public BlockingSession Session { get; } = new(InferenceProvider.DirectMl);

        public ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IInferenceSession>(Session);
    }

    private sealed class DelayedSessionFactory : IInferenceSessionFactory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingSession Session { get; } = new(InferenceProvider.Cpu);

        public async ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return Session;
        }
    }

    private sealed class BlockingSession(InferenceProvider provider) : IInferenceSession
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InferenceProvider Provider { get; } = provider;

        public bool IsDisposed { get; private set; }

        public async ValueTask<InferenceExecution> RunAsync(
            InferenceInput input,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new InferenceExecution(
                Array.AsReadOnly(input.Values.ToArray()),
                Provider,
                new StageTiming(0, 0, 0, 0, 0, true, false),
                new MemoryDiagnostics(0, 0, 0, 0, 0));
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
