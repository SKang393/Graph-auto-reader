# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
# 80% aggregate backstop. Per-core cycle average requires cooperative training work/rest blocks.
param(
    [Parameter(Mandatory=$true)][string]$Program,
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$ProgramArguments
)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Goal22CpuBudget {
    [StructLayout(LayoutKind.Sequential)] public struct CpuRate { public uint Flags; public uint Rate; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr CreateJobObject(IntPtr security, string name);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref CpuRate info, uint length);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out CpuRate info, uint length, IntPtr returnedLength);
}
"@
$taskHostProcess=[System.Diagnostics.Process]::GetCurrentProcess()
$taskHostProcess.PriorityClass=[System.Diagnostics.ProcessPriorityClass]::Idle
$taskLogicalCount=[Environment]::ProcessorCount
if ($taskLogicalCount -lt 1 -or $taskLogicalCount -gt 63) { throw 'Unsupported CPU topology.' }
$taskAffinity=[long]([math]::Pow(2,$taskLogicalCount)-1)
$taskCpuRate=[uint32]8000
$taskHostProcess.ProcessorAffinity=[IntPtr]$taskAffinity
$taskJob=[Goal22CpuBudget]::CreateJobObject([IntPtr]::Zero,$null)
if ($taskJob -eq [IntPtr]::Zero) { throw 'Could not create background CPU budget.' }
$taskRate=[Goal22CpuBudget+CpuRate]::new()
$taskRate.Flags=5
$taskRate.Rate=$taskCpuRate
if (-not [Goal22CpuBudget]::SetInformationJobObject($taskJob,15,[ref]$taskRate,8)) { throw "Could not set CPU ceiling: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
if (-not [Goal22CpuBudget]::AssignProcessToJobObject($taskJob,$taskHostProcess.Handle)) { throw "Could not apply CPU ceiling: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
$verifiedRate=[Goal22CpuBudget+CpuRate]::new()
if (-not [Goal22CpuBudget]::QueryInformationJobObject($taskJob,15,[ref]$verifiedRate,8,[IntPtr]::Zero) -or $verifiedRate.Flags -ne 5 -or $verifiedRate.Rate -ne $taskCpuRate) { throw 'CPU ceiling verification failed.' }
$taskHostProcess.Refresh()
if ($taskHostProcess.ProcessorAffinity.ToInt64() -ne $taskAffinity -or $taskHostProcess.PriorityClass -ne 'Idle') { throw 'Scheduling verification failed.' }
$env:OMP_NUM_THREADS=[string]$taskLogicalCount
$env:MKL_NUM_THREADS=[string]$taskLogicalCount
$env:OPENBLAS_NUM_THREADS=[string]$taskLogicalCount
$env:NUMEXPR_NUM_THREADS=[string]$taskLogicalCount
$env:GOAL22_CPU_CEILING_PERCENT=[string]($taskCpuRate / 100)
$env:GOAL22_TRAINING_DUTY_PERCENT='80'
& $Program @ProgramArguments
exit $LASTEXITCODE
