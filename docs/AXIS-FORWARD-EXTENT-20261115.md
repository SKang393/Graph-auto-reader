<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Reject impossible forward extents before exact contact work

Baseline: `d279d384fff0c29ee0d897de255447535e3ff7f9`.

## Defect and repair

The bounded native search charged exact contact work to intersections whose
rightward or upward endpoint projection was nonpositive. Existing scoring can
only reduce these projections when clipping to the image, so these pairs can
never produce an admissible plot. The search now checks that necessary condition
before charging the exact-pair budget. It uses the same direction, projection
and maximum operations as scoring, without a new tolerance. Tiny positive spans
remain eligible. Unlimited enumeration, candidate order, scoring, geometry
tolerances and the shipped B=128 limit are unchanged.

Owned controls combine one real plot with 129 zero-right or zero-up contacts,
in both input orders. The unlimited oracle returns the one real candidate.
Independent source review found no actionable issue; no private answer was
used to define the change.

## Verification

Normal-host serial Debug VSTest with the existing dependency cache:

```powershell
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj --configuration Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter FullyQualifiedName~AxisPairEnvelopePruningTests --logger 'trx;LogFileName=focused.trx' --results-directory artifacts/axis-forward-extent-check
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj --configuration Debug --no-build --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --logger 'trx;LogFileName=full.trx' --results-directory artifacts/axis-forward-extent-check
```

| Run | Passed / failed / skipped | Test / wall time |
| --- | --- | --- |
| Four new controls before repair | 0 / 4 / 0 | 184 ms / 12.766 s |
| Focused parity, budget and cancellation controls | 34 / 0 / 0 | 862 ms / 11.583 s |
| Full Axis suite | 169 / 0 / 2 | About 2 s / 5.362 s |

The two skips are existing opt-in Rust worker tests. TRX SHA-256, in table order:
`fb38a95152df8c0d070cbfa1db021292a0433f50f6f130a6cd8bd5663719e849`,
`132b7418956bc9922ff6c8ead8f2d119208a73a23cf56e52a1657297d8ab9759`,
`95c48ff504b76ecdeec726d760d09540d8c3cfe53f4ccaa4bbfbc9443b8b6870`.

## Fixed development confirmation

A new isolated geometry-only probe inherits the sorted-spatial runtime pins and
changes only `GraphReader.Axis.dll` to
`a7768c53a96d9e9b6ad8e002afa883e90ee77973cc1fa4b3b8e66611811bf7f0`.
All twenty runtime files, reviewed OpenCV, App and Rust identities are checked
before payload reads. Historical routes and pins remain intact. Probe build:
13.21 s, zero warnings/errors; owned self-test: exit 0, 2.28 s, no private reads.
Probe DLL: `ad8eaa9429695698714fe1375d55c3f260a13df08eb2f6ec6dd2d9364ece681d`.

```text
GraphReader.CalibrationOcrProbe.exe --real-dev-forward-extent-geometry-self-test
GraphReader.CalibrationOcrProbe.exe --real-dev-forward-extent-geometry-trace <authorized-corpus> <pinned-App.exe> <reviewed-assets> <unchanged-Rust.exe> --explicit-opt-in
```

One run of the same first twelve development projects finished in **15.8301 s**,
exit 0, empty stderr. Eleven prepared panels and one full-image fallback were
attempted; all twelve finished. Seven returned geometry and five did not. No
unsupported dimensions, other panel failures, timeouts, unfinished or unattempted
panels occurred. All seven returned geometries and measured tick sets exactly
match the preceding geometry-only report. Assignment, selected inventory and
source digest are unchanged. The 51 reserved inputs remained excluded.

All five Rust failures remain `AMBIGUOUS_AXIS`. Native fallback now completes
enumeration on two of them, rejecting multiple ranked candidates; three still
exhaust the exact-pair budget. Previously these counts were one and four.
Automatic geometry coverage is therefore still **7/12**. There were zero OCR
calls, sealed payload reads, image writes, truth scoring, Apply or exports.
No new numeric-draft claim is made.

Axis time totaled 11,443.2132 ms (10,943.6104 ms on native-fallback panels and
499.6028 ms on Rust-only panels); preparation totaled 2,959.8764 ms. The previous
axis total was 27,093.4964 ms. This single paired diagnostic under varying host
load is not a controlled speed benchmark or a general latency claim.

The 14,337-byte report SHA-256 is
`a5211ad7290fd5e4d1a3f172f94550bc74a9ea97fa4d4099d423d35bef024a13`.
Only aggregates are tracked; no private pixels, case predictions or truth rows.

## Next product work and retention

Full Windows CI [43707486220](https://github.com/SKang393/Graph-auto-reader/actions/runs/43707486220)
completed successfully for exact source
`6e3d384d0514f4f153bdcfff8bd7954595532228`; build/test/packaging checks passed
and public-release jobs were skipped.

Normal Suggest must continue rejecting ambiguity. A separate explicit review
can retain completed native candidate enumeration, show candidate overlays with
no default selection, and reuse the existing source-bound OCR and calibration
review after the user chooses. Budget/cancel/failure paths must expose no partial
candidate set. No thresholds or numeric values need to change. This is the next
implementation batch, not an implemented capability in this checkpoint.

Project code remains Apache-2.0. No dependency, model, frozen schema or package
changes were made; the sole retained package is 0.4.52. Production automatic
acceptance remains incomplete.

Previous cleanup completed at `2026-11-15T10:56:01.0873584Z`: ten disposable files,
349,554 bytes, including newly created native-QA build-local Data and completed
prior diagnostics. Shared user Data, source originals, models, the sole package
and the specific geometry comparison report were preserved. After this outcome
is pushed and its consumers finish, retire both completed geometry reports and
the new TRXs; keep active implementation/test outputs only.
