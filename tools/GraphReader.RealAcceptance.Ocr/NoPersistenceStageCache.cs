// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Inference;

namespace GraphReader.RealAcceptance.Ocr;

/// <summary>Acceptance inference never persists or retrieves case-level tensors.</summary>
internal sealed class NoPersistenceStageCache : IStageCache
{
    public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<byte[]?>(null);
    }

    public ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
