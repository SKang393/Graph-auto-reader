<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Reuse the CPU budget across sequential commands, 2026-09-30

The shared CPU helper now creates one Windows job per PowerShell host and
reuses it for later commands. Previously, each call assigned the launcher to
another job. Windows applies nested CPU rates relative to their parent, so
successive 80% limits compounded instead of preserving one 80% ceiling.
See the [Windows CPU-rate contract](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information).

Every invocation verifies actual job membership, flags and the exact configured
rate before starting the child. Active hardware processor count supplies
affinity and library thread counts; a quota-limited runtime processor count
must not progressively remove eligible CPUs. Idle priority and the cooperative
80% training duty setting remain unchanged.

A fresh Windows PowerShell 7 host passed eight sequential calls in 6.52 seconds.
All calls retained one job handle, flags 5, rate 8000 and affinity 4095 across
the machine's 12 CPUs. Child processes retained Idle priority, all four library
thread counts and budget settings. Both exit codes 0 and 7 propagated exactly.
Changing the operating-system cap to 70% caused the next call to fail before
starting its child; the check then restored 80%.

The installed helper is byte-identical to the tested proposal. The
[evidence index](GOAL-22-CPU-BUDGET-REUSE.json) binds both source files, all
receipts, the prior helper and the exact command. The reusable check is
`tools/Test-TrainingCpuBudget.ps1`; run it in a fresh PowerShell host with a
new `-OutputDirectory` under `artifacts/`.

Earlier elapsed times remain honest wall times, but multi-call runs had
compounded quotas and cannot establish controlled speed comparisons. Their
completed accuracy results remain intact. This repair makes no speedup claim,
and no accuracy experiment was repeated for it.

The helper still supports at most 63 active CPUs in one affinity mask. External
parent jobs can impose stricter limits. The cap is aggregate, not an
instantaneous per-core guarantee. Existing running hosts were allowed to
finish; future launchers use the repaired helper in a fresh host.

This is project-owned Apache-2.0 code using existing Windows APIs. No new
dependency, model, private/sealed input, training, production activation or
package. Build 433 remains 0.4.33; Goal 22 remains incomplete.
