// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GraphReader.Inference;

namespace GraphReader.CalibrationOcrProbe;

internal static class OwnedInferenceKeyCheck
{
    private static readonly long[][] Shapes = [[1, 3, 48, 320], [16, 3, 48, 320]];
    private static readonly long[] WarmupShape = [1, 1, 1, 32];
    private static readonly InferenceProvider[] CpuOnly = [InferenceProvider.Cpu];
    private static readonly ModelIdentity DummyModel = new("owned.cache-key-only", "1",
        Program.Hash("owned-cache-key-dummy-model-v1"u8.ToArray()), "owned-not-loaded.onnx");
    private static readonly StageCacheMaterial CacheMaterial = new(
        Program.Hash("owned-cache-key-input-v1"u8.ToArray()), "0,0,320,48", "identity", "owned-tensor-key", "1",
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["normalization"] = "finite-normalized-v1", ["layout"] = "NCHW",
        }, 1);

    internal static (object Report, bool Completed) Run(string expectedInferenceHash, CpuBudgetJob budget,
        CancellationToken token)
    {
        var total = Stopwatch.StartNew();
        var rows = new List<object>(Shapes.Length);
        int attempted = 0, completed = 0, failed = 0, unattempted = 0, deriveCalls = 0;
        bool warmupAttempted = false, warmupCompleted = false;
        string? actualInferenceHash = null, preflightFailure = null;
        long tensorBytes = 0;
        FileStream? assemblyPin = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (expectedInferenceHash.Length != 64 || !expectedInferenceHash.All(Uri.IsHexDigit) ||
                !BitConverter.IsLittleEndian || budget.MaximumLogicalCores > .5)
                throw new InvalidDataException();
            assemblyPin = new FileStream(typeof(InferenceCacheKeyDeriver).Assembly.Location,
                FileMode.Open, FileAccess.Read, FileShare.Read);
            actualInferenceHash = Convert.ToHexStringLower(SHA256.HashData(assemblyPin));
            if (!string.Equals(actualInferenceHash, expectedInferenceHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
            float[] warmup = Values(32, token);
            InferenceRequest warmupRequest = Request(warmup, WarmupShape);
            warmupAttempted = true; deriveCalls++;
            _ = InferenceCacheKeyDeriver.Derive(warmupRequest);
            warmupCompleted = true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            preflightFailure = error is OperationCanceledException ? "CANCELLED" :
                warmupAttempted ? "WARMUP_FAILED" : "INFERENCE_ASSEMBLY_GUARD_FAILED";
        }

        try
        {
            foreach (long[] shape in Shapes)
            {
                int batch = checked((int)shape[0]);
                if (preflightFailure is not null || token.IsCancellationRequested)
                {
                    unattempted++;
                    rows.Add(new { batch, shape, status = "unattempted", error = preflightFailure ?? "CANCELLED", derive_attempted = false });
                    continue;
                }
                attempted++;
                string stage = "construct_and_hash";
                bool deriveAttempted = false;
                string? key = null, beforeHash = null, afterHash = null;
                long? callAllocation = null;
                var call = new Stopwatch();
                try
                {
                    token.ThrowIfCancellationRequested();
                    int count = checked((int)shape.Aggregate(1L, (product, dimension) => checked(product * dimension)));
                    float[] values = Values(count, token);
                    tensorBytes += checked((long)count * sizeof(float));
                    beforeHash = HashValues(values);
                    InferenceRequest request = Request(values, shape);
                    stage = "derive";
                    token.ThrowIfCancellationRequested();
                    deriveAttempted = true; deriveCalls++;
                    long allocationStart = GC.GetAllocatedBytesForCurrentThread();
                    call.Start();
                    try { key = InferenceCacheKeyDeriver.Derive(request).Value; }
                    finally
                    {
                        call.Stop();
                        callAllocation = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                    }
                    stage = "preservation";
                    afterHash = HashValues(values);
                    if (beforeHash != afterHash || key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new InvalidDataException();
                    token.ThrowIfCancellationRequested();
                    completed++;
                    rows.Add(new
                    {
                        batch, shape, values = count, bytes = checked(count * sizeof(float)), status = "completed", cache_key = key,
                        tensor_values_sha256 = beforeHash, tensor_values_sha256_after = afterHash, value_buffer_unchanged = true,
                        derive_attempted = true, derive_wall_milliseconds = call.Elapsed.TotalMilliseconds,
                        derive_thread_allocated_bytes = callAllocation,
                    });
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    failed++;
                    rows.Add(new { batch, shape, status = error is OperationCanceledException ? "cancelled" : "failed", stage,
                        error = error is OperationCanceledException ? "CANCELLED" : error.GetType().Name,
                        derive_attempted = deriveAttempted, cache_key = key, tensor_values_sha256 = beforeHash,
                        tensor_values_sha256_after = afterHash, derive_wall_milliseconds = deriveAttempted ? (double?)call.Elapsed.TotalMilliseconds : null,
                        derive_thread_allocated_bytes = callAllocation });
                }
            }
        }
        finally { assemblyPin?.Dispose(); }

        bool done = preflightFailure is null && warmupCompleted && completed == Shapes.Length && failed == 0 && unattempted == 0;
        return (new
        {
            schema = "graphreader.owned-inference-key-check.v1", status = done ? "completed" : "refused_or_failed",
            declared = Shapes.Length, attempted, completed, failed, unattempted, preflight_failure = preflightFailure,
            warmup_values = 32, warmup_attempted = warmupAttempted, warmup_completed = warmupCompleted, total_derive_calls = deriveCalls,
            inference_assembly_sha256 = actualInferenceHash, required_inference_assembly_sha256 = expectedInferenceHash,
            request = new { DummyModel.ModelId, DummyModel.Version, DummyModel.Sha256, model_path_is_dummy_and_never_opened = true,
                CacheMaterial.InputSha256, CacheMaterial.PanelCrop, CacheMaterial.TransformChain, CacheMaterial.StageName,
                CacheMaterial.StageVersion, CacheMaterial.Parameters, CacheMaterial.ContractVersion, allowed_providers = CpuOnly.Select(p => p.ToString()).ToArray(),
                input_name = "x", output_name = "y", timeout_seconds = 30, bypass_cache = false },
            tensor_values = "((index % 2049) - 1024) / 1024f; finite normalized [-1,1]; SHA256 of little-endian float32 value bytes",
            timing_scope = "Exactly one Derive per fixed tensor, after one 32-value warmup; construction and pre/post buffer hashes excluded",
            cases = rows, tensor_bytes = tensorBytes, wall_seconds = total.Elapsed.TotalSeconds,
            budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores,
            model_file_accesses = 0, model_loads = 0, inference_calls = 0, private_reads = 0, worker_calls = 0, image_activity = 0,
        }, done);
    }

    private static InferenceRequest Request(float[] values, long[] shape) => new(DummyModel,
        new(values, shape, "x", "y"), CacheMaterial, TimeSpan.FromSeconds(30), CpuOnly);

    private static float[] Values(int count, CancellationToken token)
    {
        var values = new float[count];
        for (int index = 0; index < count; index++)
        {
            if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
            values[index] = ((index % 2049) - 1024) / 1024f;
        }
        return values;
    }

    private static string HashValues(float[] values) =>
        Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));
}
