// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using GraphReader.App.Integration;
using GraphReader.App.Integration.Workflow;
using GraphReader.Domain;
using GraphReader.Inference;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ApplicationCompositionLifecycleTests
{
    [TestMethod]
    public void DispatcherGuardRejectsOnlyTheActualWpfDispatcherThread()
    {
        var guard = new DispatcherUiThreadGuard(Dispatcher.CurrentDispatcher);

        Assert.ThrowsExactly<UiThreadInferenceException>(
            guard.ThrowIfCurrentThreadIsUiThread);
        Task.Run(guard.ThrowIfCurrentThreadIsUiThread).GetAwaiter().GetResult();
    }

    [TestMethod]
    public async Task CanceledCompositionDisposesInitializedOwnedInferenceRuntime()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "GraphReader.ApplicationComposition.Lifecycle",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ProductionInferenceRuntimeHost? host = null;
        try
        {
            DomainResult<ProductionInferenceRuntimeHost> created =
                ProductionInferenceRuntimeFactory.Create(
                    new TestApplicationPaths(root),
                    CapturedUiThreadGuard.CaptureCurrentThread());
            host = created.Value ?? throw new AssertFailedException(
                string.Join(" | ", created.Errors.Select(static error => error.TechnicalMessage)));
            _ = host.Runtime;
            Assert.IsTrue(host.IsInitialized);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
                ApplicationComposition.CompleteWithOwnedInferenceAsync(
                    host,
                    () => Task.FromCanceled<ApplicationCompositionResult>(cancellation.Token)));

            Assert.IsTrue(host.IsDisposed);
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = host.Runtime);
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OriginalDbRuntimeMatchesEvidenceAndSeparatesItsCache()
    {
        string root = Path.Combine(Path.GetTempPath(), "GraphReader.RuntimeProfile", Guid.NewGuid().ToString("N"));
        var paths = new TestApplicationPaths(root);
        await using ProductionInferenceRuntimeHost normal = ProductionInferenceRuntimeFactory.Create(
            paths, NoUiThreadGuard.Instance).Value ?? throw new AssertFailedException("Default runtime unavailable.");
        await using ProductionInferenceRuntimeHost original = ProductionInferenceRuntimeFactory.Create(
            paths, NoUiThreadGuard.Instance, originalDbEvidenceRuntime: true).Value ??
            throw new AssertFailedException("Original-DB runtime unavailable.");

        Assert.AreEqual(OnnxGraphOptimizationMode.RuntimeDefault, normal.GraphOptimizationMode);
        Assert.AreEqual(ProductionInferenceRuntimeHost.DefaultQueueCapacity, normal.QueueCapacity);
        Assert.ThrowsExactly<InvalidDataException>(() => ProductionOcrAdapter.ValidateApprovedOriginalDbRuntime(normal));
        ProductionOcrAdapter.ValidateApprovedOriginalDbRuntime(original);
        Assert.AreNotEqual(normal.CacheRoot, original.CacheRoot);
        Assert.IsFalse(normal.IsInitialized);
        Assert.IsFalse(original.IsInitialized);
    }

    [TestMethod]
    [DataRow(OnnxGraphOptimizationMode.RuntimeDefault, 1, 1, 1, 1, false)]
    [DataRow(OnnxGraphOptimizationMode.Disabled, 2, 1, 1, 1, false)]
    [DataRow(OnnxGraphOptimizationMode.Disabled, 1, 2, 1, 1, false)]
    [DataRow(OnnxGraphOptimizationMode.Disabled, 1, 1, 8, 1, false)]
    [DataRow(OnnxGraphOptimizationMode.Disabled, 1, 1, 1, 2, false)]
    [DataRow(OnnxGraphOptimizationMode.Disabled, 1, 1, 1, 1, true)]
    [DataRow(OnnxGraphOptimizationMode.Disabled, 1, 1, 1, 1, false)]
    public async Task OriginalDbRuntimeRejectsChangedExecutionSettings(
        OnnxGraphOptimizationMode optimization, int intra, int inter, int queue, int workers, bool directMl)
    {
        await using var host = new ProductionInferenceRuntimeHost(
            new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(),
            new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance, optimization),
            new CpuThreadConfiguration(2, intra, inter),
            directMl ? [InferenceProvider.DirectMl, InferenceProvider.Cpu] : [InferenceProvider.Cpu],
            Path.Combine(Path.GetTempPath(), "GraphReader.RuntimeProfile", Guid.NewGuid().ToString("N"),
                optimization == OnnxGraphOptimizationMode.Disabled && intra == 1 && inter == 1 &&
                queue == 1 && workers == 1 && !directMl ? "v1" :
                ProductionInferenceRuntimeFactory.OriginalDbEvidenceCacheNamespace),
            queue, workers);
        Assert.ThrowsExactly<InvalidDataException>(() => ProductionOcrAdapter.ValidateApprovedOriginalDbRuntime(host));
        Assert.IsFalse(host.IsInitialized);
    }

    [TestMethod]
    public async Task HostCpuProfileConstrainsStagesThatOmitOrBroadenTheirProviderChoice()
    {
        string root = Path.Combine(Path.GetTempPath(), "GraphReader.HostCpuPolicy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string modelPath = Path.Combine(root, "fake.bin");
            File.WriteAllBytes(modelPath, [1, 3, 5]);
            var model = new ModelIdentity("fake", "1",
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(modelPath))), modelPath);
            var factory = new FakeInferenceSessionFactory();
            await using var host = new ProductionInferenceRuntimeHost(
                new FakeExecutionProviderDiscovery("DmlExecutionProvider", "CPUExecutionProvider"),
                new WindowsExecutionProviderPolicy(), factory, CpuThreadConfiguration.Create(1),
                [InferenceProvider.Cpu], Path.Combine(root, "cache"), 2, 1);
            var request = new InferenceRequest(model, new InferenceInput(new float[] { 1, 2, 3 }, new long[] { 1, 3 }),
                new StageCacheMaterial("input", "crop", "original", "host-test", "1", new Dictionary<string, object?>(), 1),
                TimeSpan.FromSeconds(2));

            InferenceResponse automatic = await host.Runtime.RunAsync(request, CancellationToken.None);
            InferenceResponse broadened = await host.Runtime.RunAsync(request with
            {
                AllowedProviders = [InferenceProvider.DirectMl, InferenceProvider.Cpu], BypassCache = true,
            }, CancellationToken.None);

            Assert.AreEqual(InferenceProvider.Cpu, automatic.Execution?.Provider);
            Assert.AreEqual(InferenceProvider.Cpu, broadened.Execution?.Provider);
            Assert.AreEqual(InferenceProvider.Cpu, factory.Sessions.Single().Provider);
            Assert.AreEqual(2, factory.Sessions.Single().RunCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestApplicationPaths(string root) : IApplicationPaths
    {
        public DistributionMode Mode => DistributionMode.Portable;

        public string SettingsRoot { get; } = Path.Combine(root, "Data", "Settings");

        public string AutosaveRoot { get; } = Path.Combine(root, "Data", "Autosave");

        public string CacheRoot { get; } = Path.Combine(root, "Data", "Cache");

        public string LogsRoot { get; } = Path.Combine(root, "Data", "Logs");

        public string RecoveryRoot { get; } = Path.Combine(root, "Data", "Recovery");

        public string ModelRoot { get; } = Path.Combine(root, "Data", "Models");
    }
}
