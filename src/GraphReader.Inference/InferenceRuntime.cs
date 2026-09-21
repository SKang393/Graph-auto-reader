// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace GraphReader.Inference;

public sealed class InferenceRuntime : IAsyncDisposable
{
    private readonly OnnxSessionRegistry _registry;
    private readonly BoundedInferenceScheduler _queue;
    private readonly IStageCache _cache;
    private readonly TimeSpan _disposalTimeout;
    private readonly IReadOnlyList<InferenceProvider>? _allowedProviders;
    private int _disposed;

    public InferenceRuntime(
        OnnxSessionRegistry registry,
        BoundedInferenceScheduler queue,
        IStageCache cache,
        TimeSpan? disposalTimeout = null,
        IReadOnlyList<InferenceProvider>? allowedProviders = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _allowedProviders = allowedProviders is null ? null : Array.AsReadOnly(allowedProviders.ToArray());
        ValidateAllowedProviders(_allowedProviders);
        _disposalTimeout = disposalTimeout ?? TimeSpan.FromSeconds(2);
        if (_disposalTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(disposalTimeout));
        }
    }

    public async ValueTask<InferenceResponse> RunAsync(
        InferenceRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.AllowedProviders is null
            ? request
            : request with
            {
                AllowedProviders = Array.AsReadOnly(request.AllowedProviders.ToArray()),
            };
        ValidateAllowedProviders(request.AllowedProviders);
        if (_allowedProviders is not null)
        {
            // A stage may narrow the host's execution policy, never widen it.
            // Apply the effective policy before both cache and session lookup.
            request = request with
            {
                AllowedProviders = request.AllowedProviders is null
                    ? _allowedProviders
                    : Array.AsReadOnly(_allowedProviders.Intersect(request.AllowedProviders).ToArray()),
            };
        }

        var cacheKey = InferenceCacheKeyDeriver.Derive(request);
        if (!request.BypassCache)
        {
            var cacheStopwatch = Stopwatch.StartNew();
            var cached = await _cache.TryGetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                try
                {
                    var cacheResult = CacheCodec.Decode(cached, cacheStopwatch.Elapsed.TotalMilliseconds);
                    if (request.AllowedProviders is null ||
                        request.AllowedProviders.Contains(cacheResult.Provider))
                    {
                        return new InferenceResponse(true, cacheResult, null, Array.Empty<ProviderAttempt>());
                    }
                }
                catch (InvalidDataException)
                {
                    // A corrupt entry is treated as a miss and overwritten by a successful run.
                }
            }
        }

        var acquisition = request.AllowedProviders is null
            ? await _registry.GetOrCreateAsync(request.Model, cancellationToken).ConfigureAwait(false)
            : await _registry.GetOrCreateAsync(
                request.Model,
                request.AllowedProviders,
                cancellationToken).ConfigureAwait(false);
        if (!acquisition.Succeeded)
        {
            return InferenceResponse.Failure(acquisition.Error!, acquisition.Attempts);
        }

        var attempts = acquisition.Attempts.ToList();
        var executionStopwatch = Stopwatch.StartNew();
        try
        {
            var execution = await _queue.EnqueueAsync(
                token => acquisition.Session!.RunAsync(request.Input, token),
                request.Timeout,
                cancellationToken).ConfigureAwait(false);
            await _cache.PutAsync(cacheKey, CacheCodec.Encode(execution), cancellationToken).ConfigureAwait(false);
            return new InferenceResponse(true, execution, null, attempts.AsReadOnly());
        }
        catch (TimeoutException exception)
        {
            return InferenceResponse.Failure(
                Error("INFERENCE_TIMEOUT", exception.Message, "retry"),
                attempts.AsReadOnly());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            acquisition.Session!.Provider == InferenceProvider.DirectMl &&
            exception is not UiThreadInferenceException)
        {
            attempts.Add(new ProviderAttempt(
                InferenceProvider.DirectMl,
                false,
                "DirectML run failed; retrying with mandatory CPU fallback. " + exception.Message));
            var remaining = request.Timeout == Timeout.InfiniteTimeSpan
                ? Timeout.InfiniteTimeSpan
                : request.Timeout - executionStopwatch.Elapsed;
            if (remaining != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero)
            {
                return InferenceResponse.Failure(
                    Error("INFERENCE_TIMEOUT", "Inference timeout expired before CPU fallback.", "retry"),
                    attempts.AsReadOnly());
            }

            using var fallbackTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (remaining != Timeout.InfiniteTimeSpan)
            {
                fallbackTimeout.CancelAfter(remaining);
            }

            SessionAcquisition cpuAcquisition;
            try
            {
                cpuAcquisition = await _registry.GetOrCreateCpuAsync(
                    request.Model,
                    fallbackTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                fallbackTimeout.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                return InferenceResponse.Failure(
                    Error("INFERENCE_TIMEOUT", "Inference timeout expired while acquiring CPU fallback.", "retry"),
                    attempts.AsReadOnly());
            }

            attempts.AddRange(cpuAcquisition.Attempts);
            if (!cpuAcquisition.Succeeded)
            {
                return InferenceResponse.Failure(
                    cpuAcquisition.Error ?? Error("INFERENCE_PROVIDER_UNAVAILABLE", "CPU fallback failed.", "retry"),
                    attempts.AsReadOnly());
            }

            try
            {
                remaining = request.Timeout == Timeout.InfiniteTimeSpan
                    ? Timeout.InfiniteTimeSpan
                    : request.Timeout - executionStopwatch.Elapsed;
                if (remaining != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException("Inference timeout expired before CPU fallback execution.");
                }

                var cpuExecution = await _queue.EnqueueAsync(
                    token => cpuAcquisition.Session!.RunAsync(request.Input, token),
                    remaining,
                    cancellationToken).ConfigureAwait(false);
                await _cache.PutAsync(cacheKey, CacheCodec.Encode(cpuExecution), cancellationToken).ConfigureAwait(false);
                return new InferenceResponse(true, cpuExecution, null, attempts.AsReadOnly());
            }
            catch (TimeoutException timeoutException)
            {
                return InferenceResponse.Failure(
                    Error("INFERENCE_TIMEOUT", timeoutException.Message, "retry"),
                    attempts.AsReadOnly());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception cpuException)
            {
                attempts.Add(new ProviderAttempt(InferenceProvider.Cpu, false, cpuException.Message));
                return InferenceResponse.Failure(
                    Error("INFERENCE_FAILED", cpuException.Message, "retry"),
                    attempts.AsReadOnly());
            }
        }
        catch (Exception exception)
        {
            return InferenceResponse.Failure(
                Error("INFERENCE_FAILED", exception.Message, "retry"),
                attempts.AsReadOnly());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var cleanup = DisposeCoreAsync();
        try
        {
            await cleanup.WaitAsync(_disposalTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = ObserveDetachedCleanupAsync(cleanup);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _queue.DisposeAsync().ConfigureAwait(false);
        await _registry.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task ObserveDetachedCleanupAsync(Task cleanup)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static InferenceError Error(string code, string technicalMessage, string action) =>
        new(code, "error", "Errors." + code, technicalMessage, true, action);

    private static void ValidateAllowedProviders(IReadOnlyList<InferenceProvider>? providers)
    {
        if (providers is null)
        {
            return;
        }

        if (providers.Count == 0 ||
            providers.Any(static provider =>
                provider is not (InferenceProvider.Cpu or InferenceProvider.DirectMl)) ||
            !providers.Contains(InferenceProvider.Cpu) ||
            providers.Distinct().Count() != providers.Count)
        {
            throw new ArgumentException(
                "An explicit inference provider policy must be unique, production-only, and retain CPU fallback.",
                nameof(providers));
        }
    }

    private static class CacheCodec
    {
        private const uint Magic = 0x31495247; // GRI1
        private const byte Version = 1;
        private const int HeaderLength = 42;

        public static byte[] Encode(InferenceExecution execution)
        {
            var bytes = new byte[checked(HeaderLength + execution.Output.Count * sizeof(float))];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), Magic);
            bytes[4] = Version;
            bytes[5] = checked((byte)execution.Provider);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(6, 4), execution.Output.Count);
            for (var index = 0; index < execution.Output.Count; index++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(
                    bytes.AsSpan(HeaderLength + index * sizeof(float), sizeof(float)),
                    execution.Output[index]);
            }

            SHA256.HashData(bytes.AsSpan(HeaderLength), bytes.AsSpan(10, 32));

            return bytes;
        }

        public static InferenceExecution Decode(ReadOnlySpan<byte> bytes, double cacheReadMilliseconds)
        {
            if (bytes.Length < HeaderLength ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0, 4)) != Magic ||
                bytes[4] != Version)
            {
                throw new InvalidDataException("Unsupported inference cache entry.");
            }

            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(6, 4));
            if (count < 0 || bytes.Length != HeaderLength + count * sizeof(float))
            {
                throw new InvalidDataException("Invalid inference cache entry length.");
            }

            Span<byte> actualHash = stackalloc byte[32];
            SHA256.HashData(bytes.Slice(HeaderLength), actualHash);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, bytes.Slice(10, 32)))
            {
                throw new InvalidDataException("Inference cache payload integrity check failed.");
            }

            var output = new float[count];
            for (var index = 0; index < count; index++)
            {
                output[index] = BinaryPrimitives.ReadSingleLittleEndian(
                    bytes.Slice(HeaderLength + index * sizeof(float), sizeof(float)));
            }

            var provider = Enum.IsDefined(typeof(InferenceProvider), (int)bytes[5])
                ? (InferenceProvider)bytes[5]
                : throw new InvalidDataException("Invalid inference provider in cache entry.");
            return new InferenceExecution(
                Array.AsReadOnly(output),
                provider,
                new StageTiming(0, 0, 0, cacheReadMilliseconds, 0, false, CacheHit: true),
                new MemoryDiagnostics(0, 0, 0, 0, 0));
        }
    }
}
