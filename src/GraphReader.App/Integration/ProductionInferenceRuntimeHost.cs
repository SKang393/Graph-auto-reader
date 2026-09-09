// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using GraphReader.Domain;
using GraphReader.Inference;

namespace GraphReader.App.Integration;

/// <summary>
/// Owns the production ONNX Runtime composition. Session creation, the bounded
/// worker queue, and the content-addressed cache are lazy so manual-only startup
/// does not allocate inference workers.
/// </summary>
public sealed class ProductionInferenceRuntimeHost : IAsyncDisposable
{
    public const int DefaultQueueCapacity = 8;
    public const int DefaultWorkerCount = 1;

    private readonly Lazy<InferenceRuntime> _runtime;
    private readonly object _lifecycleSync = new();
    private int _disposed;

    internal ProductionInferenceRuntimeHost(
        IExecutionProviderDiscovery discovery,
        WindowsExecutionProviderPolicy providerPolicy,
        IInferenceSessionFactory sessionFactory,
        CpuThreadConfiguration cpuThreadConfiguration,
        IReadOnlyList<InferenceProvider> providerOrder,
        string cacheRoot,
        int queueCapacity,
        int workerCount,
        IStageCache? stageCache = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(providerPolicy);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(cpuThreadConfiguration);
        ArgumentNullException.ThrowIfNull(providerOrder);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        if (!providerOrder.Contains(InferenceProvider.Cpu))
        {
            throw new ArgumentException("Production inference requires mandatory CPU fallback.", nameof(providerOrder));
        }

        ProviderOrder = Array.AsReadOnly(providerOrder.ToArray());
        CpuThreadConfiguration = cpuThreadConfiguration;
        CacheRoot = Path.GetFullPath(cacheRoot);
        QueueCapacity = queueCapacity;
        WorkerCount = workerCount;
        _runtime = new Lazy<InferenceRuntime>(
            () =>
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                IStageCache cache = stageCache ?? new ContentAddressedStageCache(CacheRoot);
                var registry = new OnnxSessionRegistry(
                    discovery,
                    providerPolicy,
                    sessionFactory,
                    cpuThreadConfiguration);
                var scheduler = new BoundedInferenceScheduler(queueCapacity, workerCount);
                return new InferenceRuntime(registry, scheduler, cache);
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public InferenceRuntime Runtime
    {
        get
        {
            lock (_lifecycleSync)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return _runtime.Value;
            }
        }
    }

    public IReadOnlyList<InferenceProvider> ProviderOrder { get; }

    public CpuThreadConfiguration CpuThreadConfiguration { get; }

    public string CacheRoot { get; }

    public int QueueCapacity { get; }

    public int WorkerCount { get; }

    public bool IsInitialized => _runtime.IsValueCreated;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public async ValueTask DisposeAsync()
    {
        InferenceRuntime? runtime;
        lock (_lifecycleSync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            runtime = _runtime.IsValueCreated ? _runtime.Value : null;
        }

        if (runtime is not null)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public static class ProductionInferenceRuntimeFactory
{
    public static DomainResult<ProductionInferenceRuntimeHost> Create(
        IApplicationPaths applicationPaths,
        IUiThreadGuard uiThreadGuard)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(uiThreadGuard);
        try
        {
            var discovery = new OrtExecutionProviderDiscovery();
            var providerPolicy = new WindowsExecutionProviderPolicy();
            IReadOnlyList<InferenceProvider> providerOrder =
                providerPolicy.GetOrderedProviders(discovery.GetAvailableProviders());
            if (providerOrder.Count == 0 || providerOrder[^1] != InferenceProvider.Cpu)
            {
                throw new InvalidOperationException("The production provider policy did not retain CPU fallback.");
            }

            CpuThreadConfiguration cpuThreadConfiguration = CpuThreadConfiguration.Create();
            string cacheRoot = Path.Combine(applicationPaths.CacheRoot, "Inference", "v1");
            var sessionFactory = new OnnxInferenceSessionFactory(uiThreadGuard);
            return DomainResult<ProductionInferenceRuntimeHost>.Success(
                new ProductionInferenceRuntimeHost(
                    discovery,
                    providerPolicy,
                    sessionFactory,
                    cpuThreadConfiguration,
                    providerOrder,
                    cacheRoot,
                    ProductionInferenceRuntimeHost.DefaultQueueCapacity,
                    ProductionInferenceRuntimeHost.DefaultWorkerCount));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return DomainResult<ProductionInferenceRuntimeHost>.Failure(new DomainError(
                "INFERENCE_RUNTIME_UNAVAILABLE",
                DomainErrorSeverity.Warning,
                "Errors.ProductionWorkflowUnavailable",
                $"The local ONNX Runtime composition failed: {exception.Message}",
                Recoverable: true,
                "continue_manual_or_repair_runtime"));
        }
    }
}
