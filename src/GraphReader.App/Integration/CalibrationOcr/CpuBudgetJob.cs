// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.ComponentModel;
using System.Runtime.InteropServices;
using GraphReader.App.Services;
using Microsoft.Win32.SafeHandles;

namespace GraphReader.App.Integration.CalibrationOcr;

internal sealed class CpuBudgetJob : IDisposable
{
    private const uint EnabledHardCap = 1 | 4;
    private const uint BelowNormal = 0x4000;
    private readonly SafeFileHandle handle;

    private CpuBudgetJob(SafeFileHandle handle, uint logical, uint rate)
    {
        this.handle = handle;
        Budget = new(logical, rate, logical * rate / 10000d, true);
    }

    internal CalibrationOcrCpuBudget Budget { get; }

    internal static CpuBudgetJob Apply()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        uint logical = GetActiveProcessorCount(0xffff);
        uint rate = logical == 0 ? 0 : 5000 / logical;
        if (rate == 0) throw new InvalidOperationException(CalibrationOcrReasonCodes.ResourceUnavailable);
        SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var info = new CpuRate { Flags = EnabledHardCap, Rate = rate };
            uint size = (uint)Marshal.SizeOf<CpuRate>();
            IntPtr process = GetCurrentProcess();
            if (!SetInformationJobObject(job, 15, ref info, size) ||
                !AssignProcessToJobObject(job, process) || !SetPriorityClass(process, BelowNormal) ||
                !QueryInformationJobObject(job, 15, out CpuRate actual, size, out _) ||
                actual.Flags != EnabledHardCap || actual.Rate != rate ||
                !IsProcessInJob(process, job, out bool assigned) || !assigned ||
                GetPriorityClass(process) != BelowNormal)
                throw new InvalidOperationException(CalibrationOcrReasonCodes.ResourceUnavailable);
            return new(job, logical, rate);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRate { public uint Flags; public uint Rate; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref CpuRate info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out CpuRate info, uint length, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr process, uint priority);
    [DllImport("kernel32.dll")]
    private static extern uint GetPriorityClass(IntPtr process);
}
