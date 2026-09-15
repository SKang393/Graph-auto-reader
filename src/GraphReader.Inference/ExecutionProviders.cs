// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace GraphReader.Inference;

public sealed class OrtExecutionProviderDiscovery : IExecutionProviderDiscovery
{
    public IReadOnlyList<string> GetAvailableProviders() =>
        InferenceCollections.Freeze(OrtEnv.Instance().GetAvailableProviders());
}

public sealed class WindowsExecutionProviderPolicy
{
    private const string DirectMlProviderName = "DmlExecutionProvider";
    private readonly StringComparer _providerComparer = StringComparer.OrdinalIgnoreCase;

    public IReadOnlyList<InferenceProvider> GetOrderedProviders(IReadOnlyList<string> availableProviders)
    {
        ArgumentNullException.ThrowIfNull(availableProviders);
        var providers = new List<InferenceProvider>(2);
        if (availableProviders.Contains(DirectMlProviderName, _providerComparer))
        {
            providers.Add(InferenceProvider.DirectMl);
        }

        // CPU is mandatory even when runtime discovery is incomplete or a GPU session fails.
        providers.Add(InferenceProvider.Cpu);
        return providers.AsReadOnly();
    }
}

public sealed record CpuThreadConfiguration(int PhysicalCoreCount, int IntraOperationThreads, int InterOperationThreads)
{
    public static CpuThreadConfiguration Create(int? overrideThreadCount = null, IPhysicalCoreDetector? detector = null)
    {
        detector ??= new WindowsPhysicalCoreDetector();
        var cores = Math.Max(1, detector.GetPhysicalCoreCount());
        // Job CPU limits and process affinity can expose fewer processors than the machine has.
        var threadCount = overrideThreadCount ?? Math.Min(cores, Environment.ProcessorCount);
        if (threadCount < 1 || threadCount > Environment.ProcessorCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overrideThreadCount),
                $"CPU inference thread count must be between 1 and {Environment.ProcessorCount}.");
        }

        return new CpuThreadConfiguration(cores, threadCount, 1);
    }
}

public interface IPhysicalCoreDetector
{
    int GetPhysicalCoreCount();
}

public sealed class WindowsPhysicalCoreDetector : IPhysicalCoreDetector
{
    private const int RelationProcessorCore = 0;

    public int GetPhysicalCoreCount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Math.Max(1, Environment.ProcessorCount / 2);
        }

        uint length = 0;
        _ = GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0)
        {
            return Math.Max(1, Environment.ProcessorCount / 2);
        }

        var buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var count = 0;
            var offset = 0;
            while (offset < length)
            {
                var relationship = Marshal.ReadInt32(buffer, offset);
                var size = Marshal.ReadInt32(buffer, offset + sizeof(int));
                if (size <= 0 || offset + size > length)
                {
                    break;
                }

                if (relationship == RelationProcessorCore)
                {
                    count++;
                }

                offset += size;
            }

            return count > 0 ? count : Math.Max(1, Environment.ProcessorCount / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref uint returnedLength);
}
