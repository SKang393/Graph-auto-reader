<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve printed-session evidence ownership

## Reproduction and impact

The current lattice accepted printed ticks `(pixel 10.1, session 1)` and
`(pixel 13.3, session 3)` with marker columns at `10`, `11.6` and `13.2`.
The result was valid with pitch 1.6, but assigned printed sessions `[1, 1, 3]`
and estimated sessions `[1, 2, 3]`. Each column independently selected a nearby
tick, so the existing 1.5-pixel matching tolerance let the middle column reuse
the first tick. Production projection and export prefer printed evidence;
the middle column could therefore become session 1 despite supported session 2.

This is a deterministic synthetic reproduction inside the supported parameter
range. It does not establish frequency on real graphs. No private or sealed
input, model inference or training was used.

## Repair

`SessionLattice.BuildAssignments` assigns a printed tick to at most one uniquely
nearest observed column within the existing tolerance. It keeps that printed
value only when it agrees with the column's supported integer session.
Equidistant column ownership is left unassigned. The reproduced printed values
are now `[1, null, 3]`; estimated values remain `[1, 2, 3]`. A later starting
session retains the valid `[29, null, 31]` printed and `[29, 30, 31]` estimated
mapping when the existing first-observation policy permits it.

Ordinal-only unknown values, fit tolerances, contracts, model bars and public
APIs are unchanged. This prevents unsupported printed evidence; it does not
invent a printed label for a missing tick or approve automatic calibration.

## Verification

```powershell
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
dotnet test tests/GraphReader.Integration.Tests/GraphReader.Integration.Tests.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~AdjacentColumnDoesNotExportAnotherColumnsPrintedSession' --verbosity minimal
```

With the existing E-local SDK/package/temp paths and authorized host IPC, Axis
passed 126/126 in one second of test time. The focused integration test passed
1/1 in 155 ms. Total wall times were not recorded. The actual ExportService
receives the lattice's missing printed value and estimated value 2; its audit
records `XValue=2` and `XSource=Estimated`. This test does not exercise complete
project serialization or model accuracy. The regressions also cover reversed
tick/column input order and later first-session values.

Unsupported or duplicated printed evidence is zero in these regressions.
Independent source review found no concrete ownership, order, first-session or
tie regression. `git diff --check` passed. No dependency or license changed.
Only the lattice, focused Axis/export tests and this outcome record changed.
Routine compiler and test outputs are disposable when their current consumers
finish; the source and results are retained in Git.
