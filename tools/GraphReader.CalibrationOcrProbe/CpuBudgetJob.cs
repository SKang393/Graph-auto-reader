// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace GraphReader.CalibrationOcrProbe;

internal sealed class CpuBudgetJob : IDisposable
{
    private const uint EnabledHardCap = 1 | 4;
    private const uint BelowNormal = 0x4000;
    private const uint SilentBreakaway = 0x1000;
    private readonly SafeFileHandle handle;

    private CpuBudgetJob(SafeFileHandle handle, uint logical, uint rate, bool silentBreakawayVerified)
    {
        this.handle = handle;
        LogicalProcessors = logical;
        Rate = rate;
        SilentBreakawayVerified = silentBreakawayVerified;
    }

    public uint LogicalProcessors { get; }
    public uint Rate { get; }
    public double MaximumLogicalCores => LogicalProcessors * Rate / 10000d;
    public bool SilentBreakawayVerified { get; }

    public static CpuBudgetJob Apply(bool allowChildBreakaway = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        uint logical = GetActiveProcessorCount(0xffff);
        uint rate = logical == 0 ? 0 : 5000 / logical;
        if (rate == 0) throw new InvalidOperationException("CPU_BUDGET_UNAVAILABLE");
        SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (allowChildBreakaway)
            {
                // Microsoft requires EXTENDED_LIMIT_INFORMATION for SILENT_BREAKAWAY_OK.
                uint limitSize = (uint)Marshal.SizeOf<ExtendedLimits>();
                if (IntPtr.Size != 8 || limitSize != 144 || Marshal.OffsetOf<BasicLimits>(nameof(BasicLimits.LimitFlags)).ToInt32() != 16)
                    throw new InvalidOperationException("CPU_BUDGET_LAYOUT_INVALID");
                var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = SilentBreakaway } };
                if (!SetInformationJobObject(job, 9, ref limits, limitSize) ||
                    !QueryInformationJobObject(job, 9, out ExtendedLimits actualLimits, limitSize, out uint returned) ||
                    returned != limitSize || actualLimits.Basic.LimitFlags != SilentBreakaway)
                    throw new InvalidOperationException("CPU_BUDGET_BREAKAWAY_NOT_VERIFIED");
            }
            var info = new CpuRate { Flags = EnabledHardCap, Rate = rate };
            uint size = (uint)Marshal.SizeOf<CpuRate>();
            IntPtr process = GetCurrentProcess();
            if (!SetInformationJobObject(job, 15, ref info, size) ||
                !AssignProcessToJobObject(job, process) || !SetPriorityClass(process, BelowNormal) ||
                !QueryInformationJobObject(job, 15, out CpuRate actual, size, out _) ||
                actual.Flags != EnabledHardCap || actual.Rate != rate ||
                !IsProcessInJob(process, job, out bool assigned) || !assigned ||
                GetPriorityClass(process) != BelowNormal)
                throw new InvalidOperationException("CPU_BUDGET_VERIFICATION_FAILED");
            return new(job, logical, rate, allowChildBreakaway);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public void Dispose() => handle.Dispose();

    private bool Contains(Process process)
    {
        if (!IsProcessInJob(process.Handle, handle, out bool assigned)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return assigned;
    }

    internal static async Task<object> RunChildBudgetSelfTestAsync(CancellationToken token)
    {
        using CpuBudgetJob parent = Apply(allowChildBreakaway: true);
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("SELF_TEST_HOST_UNAVAILABLE");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--cpu-budget-child-self-test");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var child = new Process { StartInfo = start };
        if (!child.Start()) throw new InvalidOperationException("SELF_TEST_CHILD_START_FAILED");
        using var kill = deadline.Token.Register(() =>
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        try
        {
            string line = await child.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("SELF_TEST_CHILD_NO_EVIDENCE");
            if (line.Length > 4096) throw new InvalidDataException("SELF_TEST_CHILD_OUTPUT_LIMIT");
            // Child holds its queried job until our acknowledgement, so membership is observed while alive.
            bool inherited = parent.Contains(child);
            ChildBudget evidence = JsonSerializer.Deserialize<ChildBudget>(line, Program.Json) ?? throw new InvalidDataException();
            if (inherited || evidence.LogicalProcessors != parent.LogicalProcessors || evidence.Rate != parent.Rate ||
                evidence.MaximumLogicalCores != parent.MaximumLogicalCores || evidence.SilentBreakawayVerified ||
                evidence.PriorityClass != "BelowNormal") throw new InvalidOperationException("SELF_TEST_CHILD_BUDGET_INVALID");
            await child.StandardInput.BaseStream.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
            await child.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
            child.StandardInput.Close();
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if (child.ExitCode != 0) throw new InvalidOperationException("SELF_TEST_CHILD_FAILED");
            return new { schema = "graphreader.independent-child-cpu-budget-self-test.v1", source_payload_reads = 0,
                parent_silent_breakaway_verified = parent.SilentBreakawayVerified, parent_contains_child = inherited,
                parent_maximum_logical_cores = parent.MaximumLogicalCores, child_budget = evidence,
                child_exited = child.HasExited };
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); _ = child.WaitForExit(1000); }
            catch (InvalidOperationException) { }
        }
    }

    private sealed record ChildBudget(uint LogicalProcessors, uint Rate, double MaximumLogicalCores,
        bool SilentBreakawayVerified, string PriorityClass);

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRate { public uint Flags; public uint Rate; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

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
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out ExtendedLimits info, uint length, out uint returned);
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
