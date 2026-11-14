// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.Inference;
using Microsoft.ML.OnnxRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Inference.Tests;

[TestClass]
public sealed class DxgiAdapterSelectionTests
{
    [TestMethod]
    public void SelectionUsesOriginalIndexAndLuidAndRejectsChangedOrSoftwareAdapter()
    {
        var inventory = new FakeInventory
        {
            Adapters = [new(0, "software", 1, 1, 10, true), new(2, "hardware", 2, 2, 20, false)],
        };
        var factory = Factory(new(2, 20), inventory);
        factory.ValidateDirectMlSelection();
        Assert.AreEqual("dxgi:2:0000000000000014", factory.DirectMlSelectionIdentity);
        Assert.ThrowsExactly<InvalidOperationException>(() => Factory(new(0, 10), inventory).ValidateDirectMlSelection());
        Assert.ThrowsExactly<InvalidOperationException>(() => Factory(new(1, 20), inventory).ValidateDirectMlSelection());
        inventory.Adapters = [new(2, "replaced", 2, 2, 21, false)];
        Assert.ThrowsExactly<InvalidOperationException>(factory.ValidateDirectMlSelection);
        Assert.AreNotEqual(factory.DirectMlSelectionIdentity, Factory(new(2, 21), inventory).DirectMlSelectionIdentity);
    }

    [TestMethod]
    public async Task MissingSelectedDeviceFallsBackToCpuWithoutModelInference()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var inventory = new FakeInventory { Adapters = [] };
        var selectedFactory = new SelectableFakeFactory(Factory(new(2, 20), inventory));
        await using var registry = new OnnxSessionRegistry(
            new FakeExecutionProviderDiscovery("DmlExecutionProvider", "CPUExecutionProvider"),
            new WindowsExecutionProviderPolicy(), selectedFactory, CpuThreadConfiguration.Create(1));
        SessionAcquisition result = await registry.GetOrCreateAsync(
            new ModelIdentity("test.identity", "1", model.Sha256, model.Path), CancellationToken.None);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(InferenceProvider.Cpu, result.Session!.Provider);
        Assert.IsTrue(result.Attempts.Any(attempt => attempt.Provider == InferenceProvider.DirectMl && !attempt.Succeeded));
    }

    [TestMethod]
    public async Task RemovalIsRecheckedEvenAfterGpuSessionWasCached()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var inventory = new FakeInventory { Adapters = [new(2, "hardware", 1, 2, 20, false)] };
        var selectedFactory = new SelectableFakeFactory(Factory(new(2, 20), inventory));
        await using var registry = new OnnxSessionRegistry(
            new FakeExecutionProviderDiscovery("DmlExecutionProvider", "CPUExecutionProvider"),
            new WindowsExecutionProviderPolicy(), selectedFactory, CpuThreadConfiguration.Create(1));
        var identity = new ModelIdentity("test.identity", "1", model.Sha256, model.Path);
        Assert.AreEqual(InferenceProvider.DirectMl,
            (await registry.GetOrCreateAsync(identity, CancellationToken.None)).Session!.Provider);
        inventory.Adapters = [];
        Assert.AreEqual(InferenceProvider.Cpu,
            (await registry.GetOrCreateAsync(identity, CancellationToken.None)).Session!.Provider);
    }

    [TestMethod]
    public void WindowsInventoryPreservesEnumerationIndices()
    {
        if (!OperatingSystem.IsWindows()) return;
        IReadOnlyList<DxgiAdapter> adapters = new WindowsDxgiAdapterInventory().Enumerate();
        CollectionAssert.AreEqual(Enumerable.Range(0, adapters.Count).ToArray(), adapters.Select(item => item.Index).ToArray());
    }

    [TestMethod]
    public void MissingWindowsAdapterDoesNotProduceEstimatedLocalMemoryBudget()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.IsNull(WindowsDxgiAdapterInventory.TryReadLocalMemoryBudget(
            new DirectMlAdapterSelection(int.MaxValue, 0)));
    }

    [TestMethod]
    public async Task UnsupportedSelectedGpuFallsBackAndDoesNotShareUnscopedCache()
    {
        using var model = TestOnnxModel.CreateIdentity();
        var inventory = new FakeInventory { Adapters = [new(2, "hardware", 1, 2, 20, false)] };
        var selectedFactory = new SelectableFakeFactory(Factory(new(2, 20), inventory), failGpu: true);
        var registry = new OnnxSessionRegistry(new FakeExecutionProviderDiscovery("DmlExecutionProvider", "CPUExecutionProvider"),
            new WindowsExecutionProviderPolicy(), selectedFactory, CpuThreadConfiguration.Create(1));
        var cache = new RecordingCache();
        await using var runtime = new InferenceRuntime(registry, new BoundedInferenceScheduler(2, 1), cache);
        var request = new InferenceRequest(new ModelIdentity("test.identity", "1", model.Sha256, model.Path),
            new InferenceInput(new float[] { 1 }, new long[] { 1 }),
            new StageCacheMaterial("input", "crop", "transform", "test", "1", new Dictionary<string, object?>(), 1),
            TimeSpan.FromSeconds(2));
        InferenceResponse response = await runtime.RunAsync(request, CancellationToken.None);
        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual(InferenceProvider.Cpu, response.Execution!.Provider);
        Assert.AreEqual(0, cache.Reads);
        Assert.AreEqual(0, cache.Writes);
    }

    private static OnnxInferenceSessionFactory Factory(DirectMlAdapterSelection selection, IDxgiAdapterInventory inventory) =>
        new(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.RuntimeDefault, OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
            selection, inventory);

    private sealed class FakeInventory : IDxgiAdapterInventory
    {
        public IReadOnlyList<DxgiAdapter> Adapters { get; set; } = [];
        public IReadOnlyList<DxgiAdapter> Enumerate() => Adapters;
    }

    private sealed class SelectableFakeFactory(OnnxInferenceSessionFactory selection, bool failGpu = false) : IInferenceSessionFactory, IInferenceAdapterSelection
    {
        private readonly FakeInferenceSessionFactory _inner = new(failingProviders: failGpu ? [InferenceProvider.DirectMl] : []);
        public string? DirectMlSelectionIdentity => selection.DirectMlSelectionIdentity;
        public void ValidateDirectMlSelection() => selection.ValidateDirectMlSelection();
        public ValueTask<IInferenceSession> CreateAsync(ModelIdentity model, InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration, CancellationToken cancellationToken) =>
            _inner.CreateAsync(model, provider, cpuConfiguration, cancellationToken);
    }

    private sealed class RecordingCache : IStageCache
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken)
        { Reads++; return ValueTask.FromResult<byte[]?>(null); }
        public ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        { Writes++; return ValueTask.CompletedTask; }
    }
}
