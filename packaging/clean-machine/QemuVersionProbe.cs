// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace GraphReader.CleanMachine
{
    public static class QemuVersionProbe
    {
        private const uint KillOnJobClose = 0x00002000;
        private const int OutputLimitCharacters = 16384;

        private sealed class CapturedOutput
        {
            public string Text;
            public bool Exceeded;
        }

        private static async Task<CapturedOutput> ReadBoundedAsync(StreamReader reader)
        {
            var text = new StringBuilder();
            var buffer = new char[1024];
            bool exceeded = false;
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                int remaining = OutputLimitCharacters - text.Length;
                int keep = Math.Min(remaining, count);
                if (keep > 0) text.Append(buffer, 0, keep);
                if (count > keep) exceeded = true;
            }
            return new CapturedOutput { Text = text.ToString(), Exceeded = exceeded };
        }

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
            public BasicLimits BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int informationClass,
            ref ExtendedLimits limits, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public static string ReadVersion(string executablePath, string projectTemporaryPath, int timeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            IntPtr job = IntPtr.Zero;
            Process process = null;
            bool assignedToJob = false;
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var limits = new ExtendedLimits();
                limits.BasicLimitInformation.LimitFlags = KillOnJobClose;
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                // The wrapper waits for a line on stdin before spawning QEMU. Assigning
                // it to the job before that line ensures every QEMU descendant belongs
                // to the job, including one that keeps an output pipe open.
                var start = new ProcessStartInfo();
                start.FileName = Path.Combine(Environment.SystemDirectory,
                    @"WindowsPowerShell\v1.0\powershell.exe");
                start.Arguments = "-NoLogo -NoProfile -NonInteractive -Command " +
                    "\"$line=[Console]::In.ReadLine(); if ($line -cne 'start') { exit 2 }; " +
                    "& $env:GRAPHREADER_QEMU_PROBE_PATH -version; exit $LASTEXITCODE\"";
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardInput = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.EnvironmentVariables["GRAPHREADER_QEMU_PROBE_PATH"] = executablePath;
                start.EnvironmentVariables["TEMP"] = projectTemporaryPath;
                start.EnvironmentVariables["TMP"] = projectTemporaryPath;
                process = Process.Start(start);
                if (process == null) throw new InvalidOperationException("Could not start the QEMU version wrapper.");
                if (!AssignProcessToJobObject(job, process.Handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                assignedToJob = true;
                Task<CapturedOutput> stdout = ReadBoundedAsync(process.StandardOutput);
                Task<CapturedOutput> stderr = ReadBoundedAsync(process.StandardError);
                Stopwatch timer = Stopwatch.StartNew();
                process.StandardInput.WriteLine("start");
                process.StandardInput.Close();
                if (!process.WaitForExit(timeoutMilliseconds))
                    throw new TimeoutException("QEMU version check timed out.");
                int remaining = Math.Max(0, timeoutMilliseconds - (int)timer.ElapsedMilliseconds);
                if (!Task.WaitAll(new Task[] { stdout, stderr }, remaining))
                    throw new TimeoutException("QEMU version output did not close before the timeout.");
                if (stdout.Result.Exceeded || stderr.Result.Exceeded)
                    throw new InvalidDataException("QEMU version output exceeded 16384 characters.");
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("QEMU version check exited unsuccessfully.");
                using (var lines = new StringReader(stdout.Result.Text))
                    return lines.ReadLine();
            }
            finally
            {
                // If job assignment failed, the wrapper is still waiting on
                // stdin. Stop it before disposing the pipe so it cannot launch.
                if (process != null && !assignedToJob)
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
                if (job != IntPtr.Zero)
                {
                    TerminateJobObject(job, 1);
                    CloseHandle(job);
                }
                if (process != null) process.Dispose();
            }
        }
    }
}
