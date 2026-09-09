// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;

namespace GraphReader.Inference;

public sealed class OnnxSessionRegistry : IAsyncDisposable
{
    private readonly IExecutionProviderDiscovery _discovery;
    private readonly WindowsExecutionProviderPolicy _policy;
    private readonly IInferenceSessionFactory _factory;
    private readonly CpuThreadConfiguration _cpuConfiguration;
    private readonly ConcurrentDictionary<string, Lazy<Task<DrainingInferenceSession>>> _sessions = new(StringComparer.Ordinal);
    private readonly object _lifecycleSync = new();
    private int _disposed;
    private int _createdSessionCount;

    public OnnxSessionRegistry(
        IExecutionProviderDiscovery discovery,
        WindowsExecutionProviderPolicy policy,
        IInferenceSessionFactory factory,
        CpuThreadConfiguration cpuConfiguration)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _cpuConfiguration = cpuConfiguration ?? throw new ArgumentNullException(nameof(cpuConfiguration));
    }

    public int CreatedSessionCount => Volatile.Read(ref _createdSessionCount);

    public ValueTask<SessionAcquisition> GetOrCreateAsync(
        ModelIdentity model,
        CancellationToken cancellationToken) =>
        GetOrCreateCoreAsync(model, allowedProviders: null, cancellationToken);

    public ValueTask<SessionAcquisition> GetOrCreateAsync(
        ModelIdentity model,
        IReadOnlyList<InferenceProvider> allowedProviders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allowedProviders);
        return GetOrCreateCoreAsync(
            model,
            Array.AsReadOnly(allowedProviders.ToArray()),
            cancellationToken);
    }

    public ValueTask<SessionAcquisition> GetOrCreateAsync(
        ResolvedProductionModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        return GetOrCreateCoreAsync(model.Identity, model.AvailableProviders, cancellationToken);
    }

    private async ValueTask<SessionAcquisition> GetOrCreateCoreAsync(
        ModelIdentity model,
        IReadOnlyList<InferenceProvider>? allowedProviders,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(model);
        model.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(model.FilePath))
        {
            return new SessionAcquisition(
                null,
                Array.Empty<ProviderAttempt>(),
                Error(
                    "MODEL_NOT_FOUND",
                    $"Model file does not exist: {model.FilePath}",
                    "download_model"));
        }

        var attempts = new List<ProviderAttempt>();
        var allowed = allowedProviders?.ToHashSet();
        if (allowed is not null &&
            (!allowed.Contains(InferenceProvider.Cpu) ||
             allowed.Any(static provider =>
                 provider is not (InferenceProvider.Cpu or InferenceProvider.DirectMl))))
        {
            throw new ArgumentException("Resolved production models must allow CPU and cannot allow Fake.", nameof(allowedProviders));
        }

        IReadOnlyList<string> discoveredProviders;
        if (allowed is not null && !allowed.Contains(InferenceProvider.DirectMl))
        {
            discoveredProviders = Array.Empty<string>();
        }
        else
        {
            try
            {
                discoveredProviders = _discovery.GetAvailableProviders();
            }
            catch (Exception exception)
            {
                attempts.Add(new ProviderAttempt(
                    InferenceProvider.DirectMl,
                    false,
                    $"Provider discovery failed; continuing with mandatory CPU fallback. {exception.Message}"));
                discoveredProviders = Array.Empty<string>();
            }
        }

        foreach (var provider in _policy.GetOrderedProviders(discoveredProviders)
                     .Where(provider => allowed is null || allowed.Contains(provider)))
        {
            var acquisition = await AcquireProviderAsync(model, provider, attempts, cancellationToken).ConfigureAwait(false);
            if (acquisition.Succeeded || acquisition.Error?.Code == "MODEL_CHECKSUM_MISMATCH")
            {
                return acquisition;
            }
        }

        return new SessionAcquisition(
            null,
            attempts.AsReadOnly(),
            Error(
                "INFERENCE_PROVIDER_UNAVAILABLE",
                "All ordered inference providers failed: " + string.Join(
                    "; ",
                    attempts.Select(attempt => $"{attempt.Provider}: {attempt.TechnicalMessage}")),
                "retry"));
    }

    public ValueTask<SessionAcquisition> GetOrCreateCpuAsync(
        ModelIdentity model,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(model);
        model.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(model.FilePath))
        {
            return ValueTask.FromResult(new SessionAcquisition(
                null,
                Array.Empty<ProviderAttempt>(),
                Error("MODEL_NOT_FOUND", $"Model file does not exist: {model.FilePath}", "download_model")));
        }

        return AcquireProviderAsync(model, InferenceProvider.Cpu, new List<ProviderAttempt>(), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Task<DrainingInferenceSession>[] materialized;
        lock (_lifecycleSync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // Acquisition starts every lazy while holding this same lock. The disposal
            // snapshot therefore cannot miss a lazy that starts session creation.
            materialized = _sessions.Values
                .Where(lazy => lazy.IsValueCreated)
                .Select(lazy => lazy.Value)
                .ToArray();
        }

        foreach (var task in materialized)
        {
            try
            {
                var session = await task.ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _sessions.Clear();
    }

    private async ValueTask<SessionAcquisition> AcquireProviderAsync(
        ModelIdentity model,
        InferenceProvider provider,
        List<ProviderAttempt> attempts,
        CancellationToken cancellationToken)
    {
        var key = string.Join(
            "|",
            Path.GetFullPath(model.FilePath),
            model.Sha256.ToUpperInvariant(),
            provider.ToString(),
            _cpuConfiguration.IntraOperationThreads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Lazy<Task<DrainingInferenceSession>> lazy;
        Task<DrainingInferenceSession> creation;
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            lazy = _sessions.GetOrAdd(
                key,
                _ => new Lazy<Task<DrainingInferenceSession>>(
                    () => CreateProviderSessionAsync(model, provider),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            creation = lazy.Value;
        }

        try
        {
            var session = await creation.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(OnnxSessionRegistry));
            }

            attempts.Add(new ProviderAttempt(provider, true, null));
            return new SessionAcquisition(session, attempts.AsReadOnly(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            _sessions.TryRemove(new KeyValuePair<string, Lazy<Task<DrainingInferenceSession>>>(key, lazy));
            return new SessionAcquisition(
                null,
                attempts.AsReadOnly(),
                Error("MODEL_CHECKSUM_MISMATCH", exception.Message, "download_model"));
        }
        catch (Exception exception)
        {
            _sessions.TryRemove(new KeyValuePair<string, Lazy<Task<DrainingInferenceSession>>>(key, lazy));
            attempts.Add(new ProviderAttempt(provider, false, exception.Message));
            return new SessionAcquisition(null, attempts.AsReadOnly(), null);
        }
    }

    private async Task<DrainingInferenceSession> CreateProviderSessionAsync(
        ModelIdentity model,
        InferenceProvider provider)
    {
        var session = await _factory.CreateAsync(
            model,
            provider,
            _cpuConfiguration,
            CancellationToken.None).ConfigureAwait(false);
        Interlocked.Increment(ref _createdSessionCount);
        return new DrainingInferenceSession(session);
    }

    private static InferenceError Error(string code, string technicalMessage, string action) =>
        new(code, "error", "Errors." + code, technicalMessage, true, action);

}

internal sealed class DrainingInferenceSession : IInferenceSession
{
    private readonly IInferenceSession _inner;
    private readonly object _sync = new();
    private TaskCompletionSource? _drained;
    private Task? _disposeTask;
    private int _activeRuns;
    private bool _draining;

    public DrainingInferenceSession(IInferenceSession inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public InferenceProvider Provider => _inner.Provider;

    public async ValueTask<InferenceExecution> RunAsync(
        InferenceInput input,
        CancellationToken cancellationToken)
    {
        using var lease = AcquireRunLease();
        return await _inner.RunAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _draining = true;
            var drainTask = _activeRuns == 0
                ? Task.CompletedTask
                : (_drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            _disposeTask = DisposeAfterDrainAsync(drainTask);
            return new ValueTask(_disposeTask);
        }
    }

    private RunLease AcquireRunLease()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_draining, this);
            _activeRuns++;
            return new RunLease(this);
        }
    }

    private void ReleaseRunLease()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            _activeRuns--;
            if (_draining && _activeRuns == 0)
            {
                drained = _drained;
            }
        }

        drained?.TrySetResult();
    }

    private async Task DisposeAfterDrainAsync(Task drainTask)
    {
        await drainTask.ConfigureAwait(false);
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class RunLease : IDisposable
    {
        private DrainingInferenceSession? _owner;

        public RunLease(DrainingInferenceSession owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseRunLease();
    }
}

public enum OnnxGraphOptimizationMode
{
    RuntimeDefault,
    Disabled,
}

public sealed class OnnxInferenceSessionFactory : IInferenceSessionFactory
{
    private readonly IUiThreadGuard _uiThreadGuard;

    public OnnxInferenceSessionFactory(
        IUiThreadGuard uiThreadGuard,
        OnnxGraphOptimizationMode graphOptimizationMode = OnnxGraphOptimizationMode.RuntimeDefault)
    {
        _uiThreadGuard = uiThreadGuard ?? throw new ArgumentNullException(nameof(uiThreadGuard));
        GraphOptimizationMode = graphOptimizationMode is
            OnnxGraphOptimizationMode.RuntimeDefault or OnnxGraphOptimizationMode.Disabled
            ? graphOptimizationMode
            : throw new ArgumentOutOfRangeException(
                nameof(graphOptimizationMode),
                graphOptimizationMode,
                "Unsupported ONNX graph optimization mode.");
    }

    public OnnxGraphOptimizationMode GraphOptimizationMode { get; }

    public async ValueTask<IInferenceSession> CreateAsync(
        ModelIdentity model,
        InferenceProvider provider,
        CpuThreadConfiguration cpuConfiguration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(cpuConfiguration);
        if (provider is not (InferenceProvider.DirectMl or InferenceProvider.Cpu))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), "The ONNX factory supports DirectML and CPU only.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await VerifyChecksumAsync(model, cancellationToken).ConfigureAwait(false);

        using SessionOptions options = CreateSessionOptions(provider, cpuConfiguration);
        var stopwatch = Stopwatch.StartNew();
        var session = new InferenceSession(model.FilePath, options);
        stopwatch.Stop();
        return new OnnxInferenceSession(session, provider, _uiThreadGuard, stopwatch.Elapsed.TotalMilliseconds);
    }

    internal SessionOptions CreateSessionOptions(
        InferenceProvider provider,
        CpuThreadConfiguration cpuConfiguration)
    {
        ArgumentNullException.ThrowIfNull(cpuConfiguration);
        if (provider is not (InferenceProvider.DirectMl or InferenceProvider.Cpu))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), "The ONNX factory supports DirectML and CPU only.");
        }

        var options = new SessionOptions();
        if (GraphOptimizationMode == OnnxGraphOptimizationMode.Disabled)
        {
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL;
        }

        if (provider == InferenceProvider.DirectMl)
        {
            // Required by the DirectML execution provider contract.
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.EnableMemoryPattern = false;
            options.AppendExecutionProvider_DML(0);
        }
        else
        {
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.IntraOpNumThreads = cpuConfiguration.IntraOperationThreads;
            options.InterOpNumThreads = cpuConfiguration.InterOperationThreads;
            options.AppendExecutionProvider_CPU(1);
        }

        return options;
    }

    private static async Task VerifyChecksumAsync(ModelIdentity model, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            model.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Model checksum mismatch for '{model.ModelId}'. Expected {model.Sha256}, found {actual}.");
        }
    }
}

public sealed class OnnxInferenceSession : IInferenceSession
{
    private readonly InferenceSession _session;
    private readonly IUiThreadGuard _uiThreadGuard;
    private readonly SerializedRunGate? _directMlRunGate;
    private readonly ReusableTensorPool _tensorPool = new();
    private readonly double _sessionCreationMilliseconds;
    private int _hasRun;
    private int _disposed;

    internal OnnxInferenceSession(
        InferenceSession session,
        InferenceProvider provider,
        IUiThreadGuard uiThreadGuard,
        double sessionCreationMilliseconds)
    {
        _session = session;
        Provider = provider;
        _uiThreadGuard = uiThreadGuard;
        _sessionCreationMilliseconds = sessionCreationMilliseconds;
        _directMlRunGate = provider == InferenceProvider.DirectMl ? new SerializedRunGate() : null;
    }

    public InferenceProvider Provider { get; }

    public async ValueTask<InferenceExecution> RunAsync(
        InferenceInput input,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(input);
        _uiThreadGuard.ThrowIfCurrentThreadIsUiThread();
        cancellationToken.ThrowIfCancellationRequested();

        var dimensions = input.Shape.ToArray();
        if (dimensions.Length == 0 || dimensions.Any(value => value <= 0))
        {
            throw new ArgumentException("Inference shape must contain positive dimensions.", nameof(input));
        }

        var expectedLength = dimensions.Aggregate(1L, checked((total, value) => total * value));
        if (expectedLength != input.Values.Length)
        {
            throw new ArgumentException("Inference value count does not match tensor shape.", nameof(input));
        }

        SerializedRunGate.Lease? runLease = null;
        if (_directMlRunGate is not null)
        {
            runLease = await _directMlRunGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }

        var managedBefore = GC.GetTotalMemory(forceFullCollection: false);
        var privateBefore = Process.GetCurrentProcess().PrivateMemorySize64;
        var totalStopwatch = Stopwatch.StartNew();
        var preprocessStopwatch = Stopwatch.StartNew();
        var tensorLease = _tensorPool.Rent(dimensions, input.Values.Length);
        var buffer = tensorLease.Buffer;
        try
        {
            input.Values.Span.CopyTo(buffer);
            var inputName = input.InputName ?? _session.InputNames[0];
            var outputNames = input.OutputName is null ? _session.OutputNames : new[] { input.OutputName };
            var inputs = new Dictionary<string, OrtValue>(StringComparer.Ordinal) { [inputName] = tensorLease.Value };
            preprocessStopwatch.Stop();

            using var runOptions = new RunOptions();
            using var cancellationRegistration = cancellationToken.Register(
                static state => ((RunOptions)state!).Terminate = true,
                runOptions);
            var inferenceStopwatch = Stopwatch.StartNew();
            IDisposableReadOnlyCollection<OrtValue> results;
            try
            {
                results = _session.Run(runOptions, inputs, outputNames);
            }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            inferenceStopwatch.Stop();
            using (results)
            {
                var postprocessStopwatch = Stopwatch.StartNew();
                var output = results[0].GetTensorDataAsSpan<float>().ToArray();
                postprocessStopwatch.Stop();
                totalStopwatch.Stop();
                var cold = Interlocked.Exchange(ref _hasRun, 1) == 0;
                return new InferenceExecution(
                    Array.AsReadOnly(output),
                    Provider,
                    new StageTiming(
                        preprocessStopwatch.Elapsed.TotalMilliseconds,
                        inferenceStopwatch.Elapsed.TotalMilliseconds,
                        postprocessStopwatch.Elapsed.TotalMilliseconds,
                        totalStopwatch.Elapsed.TotalMilliseconds,
                        cold ? _sessionCreationMilliseconds : 0,
                        cold,
                        CacheHit: false),
                    new MemoryDiagnostics(
                        managedBefore,
                        GC.GetTotalMemory(forceFullCollection: false),
                        privateBefore,
                        Process.GetCurrentProcess().PrivateMemorySize64,
                        buffer.Length,
                        tensorLease.WasReused));
            }
        }
        finally
        {
            tensorLease.Dispose();
            runLease?.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _session.Dispose();
            _directMlRunGate?.Dispose();
            _tensorPool.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class ReusableTensorPool : IDisposable
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<PooledTensor>> _pools = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private int _disposed;

    public TensorLease Rent(long[] shape, int valueCount)
    {
        var key = string.Join(",", shape.Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var pool = _pools.GetOrAdd(key, static _ => new ConcurrentBag<PooledTensor>());
            if (pool.TryTake(out var existing))
            {
                return new TensorLease(this, key, existing, wasReused: true);
            }
        }

        var buffer = ArrayPool<float>.Shared.Rent(valueCount);
        try
        {
            var value = OrtValue.CreateTensorValueFromMemory(
                OrtMemoryInfo.DefaultInstance,
                buffer.AsMemory(0, valueCount),
                shape);
            return new TensorLease(this, key, new PooledTensor(buffer, value, valueCount), wasReused: false);
        }
        catch
        {
            ArrayPool<float>.Shared.Return(buffer, clearArray: true);
            throw;
        }
    }

    public void Dispose()
    {
        List<PooledTensor> tensors = [];
        lock (_sync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var pool in _pools.Values)
            {
                while (pool.TryTake(out var tensor))
                {
                    tensors.Add(tensor);
                }
            }

            _pools.Clear();
        }

        foreach (var tensor in tensors)
        {
            tensor.Dispose();
        }
    }

    private void Return(string key, PooledTensor tensor)
    {
        Array.Clear(tensor.Buffer, 0, tensor.ValueCount);
        var dispose = false;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                dispose = true;
            }
            else
            {
                _pools.GetOrAdd(key, static _ => new ConcurrentBag<PooledTensor>()).Add(tensor);
            }
        }

        if (dispose)
        {
            tensor.Dispose();
        }
    }

    internal sealed class TensorLease : IDisposable
    {
        private readonly ReusableTensorPool _owner;
        private readonly string _key;
        private PooledTensor? _tensor;

        internal TensorLease(ReusableTensorPool owner, string key, PooledTensor tensor, bool wasReused)
        {
            _owner = owner;
            _key = key;
            _tensor = tensor;
            WasReused = wasReused;
        }

        public float[] Buffer => _tensor?.Buffer ?? throw new ObjectDisposedException(nameof(TensorLease));

        public OrtValue Value => _tensor?.Value ?? throw new ObjectDisposedException(nameof(TensorLease));

        public bool WasReused { get; }

        public void Dispose()
        {
            var tensor = Interlocked.Exchange(ref _tensor, null);
            if (tensor is not null)
            {
                _owner.Return(_key, tensor);
            }
        }
    }

    internal sealed class PooledTensor(float[] buffer, OrtValue value, int valueCount) : IDisposable
    {
        public float[] Buffer { get; } = buffer;

        public OrtValue Value { get; } = value;

        public int ValueCount { get; } = valueCount;

        public void Dispose()
        {
            Value.Dispose();
            ArrayPool<float>.Shared.Return(Buffer, clearArray: true);
        }
    }
}

internal sealed class SerializedRunGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private int _disposed;

    public async ValueTask<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
    }

    internal sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Lease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
