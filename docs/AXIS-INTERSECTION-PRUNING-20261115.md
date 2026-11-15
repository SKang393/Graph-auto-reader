<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Bounded observed-intersection pruning

## Defect and scope

The [first pruning confirmation](AXIS-PAIR-PRUNING-20261115.md) left four
development native calls at an undifferentiated budget limit and one at
multiple ranked candidates. Geometry availability was 7/12. This batch tests
a source-level necessary condition for finite-segment contact, using owned
controls before any new development confirmation.

An owned slanted-clutter case has overlapping member envelopes but fitted
intersections outside one of those envelopes. Such a point cannot satisfy the
existing contact predicate. Both forward and reversed controls failed against
unchanged `6e40b21f`, exhausting the 128-pair limit despite an unrestricted
single valid plot. The failing-before run took 10.6242159 seconds.

The bounded path now performs the existing unique-intersection and frame check,
then tests that point against both inclusive expanded member envelopes, before
charging member-contact/scoring work. It retains the existing geometric
tolerances, ordering, scores, uniqueness gate and unrestricted path. B=128
still bounds member-contact examinations; B squared=16,384 bounds cheap pair
checks, including intersection/frame/envelope work. Either exhaustion fails
before publishing partial candidates. Cancellation remains checked per pair.

`AxisGeometryBudgetKind` distinguishes exact-contact and envelope-comparison
exhaustion while retaining the existing exception constructors, generic reason
and public error code. The worker reports `native_envelope_budget_exceeded`
for the cheap limit and retains `native_pair_budget_exceeded` for exact or
legacy unspecified limits. This does not retroactively classify old reports.

Acceptance is full candidate identity/order and selected-result parity with
unrestricted enumeration, excluding timing, plus unchanged rejection controls
and complete failure accounting. This is a runtime invariant, not an accuracy
threshold. No model/threshold selection, training or sealed run is involved;
the sealed-run budget is zero.

## Source verification

The Axis build passed with zero warnings/errors in 10.5459609 seconds.
**93 focused Axis tests passed, zero failed/skipped**, in 5.2965409 seconds wall
time (about one second in tests). Coverage includes forward/reversed slanted
clutter, all-member envelopes, tolerance boundaries, complete candidate parity,
128/129 exact contacts, the B-squared ceiling, large arithmetic and cancellation.
Reviewed OpenCV was restaged after compilation and unchanged after execution.

The repaired Axis was compiled into the isolated headless runtime in
2.1882347 seconds. App compilation took 33.5434622 seconds and its test assembly
9.4962044 seconds, all with zero warnings/errors. The focused OpenCV integration
tests passed 5/5 in 2.7658935 seconds wall time. An initial filter omitted the
actual `RustFirstAxisProposalServiceTests` class name; only that missing class
was then run, passing 14/14 in 2.4540287 seconds. No passing tests were repeated.
The separately open source UI was not overwritten.

The new development profile uses separate immutable runtime identities and the
existing first-12 sorted development loop. It verifies all executable/library
pins before loading the selector, creates no OCR client and preserves all
terminal/failure counts. Historical profiles remain unchanged. Two probe
compiles stopped before execution with CS8604 in the new partial-class field
initializer. A method-backed lazy factory resolves both initialization order
and conservative nullable analysis; neither failed compile read private data.
The final probe build passed with zero warnings/errors in 9.5339096 seconds.
Its pure profile self-test passed in 0.5211426 seconds with zero private reads.

| Evidence | SHA-256 |
| --- | --- |
| Failing-before Axis TRX | `da5bbc1cc7dac1c7f85606f042346a3d55fb3d3c297e8153a5e3486e37be432f` |
| Passing Axis TRX | `f5bca92a2b96a1893b6fbf6e65abe216b07e0ffbedc1969541b79ff24e7b253c` |
| Axis DLL | `5c3003bc9049f16256cf058427a8378fd9539941d5256894e9d20f046ea46dcb` |
| App executable | `b4046b9f3a444779aa1054c6eeed31688ad01031ffa5e88030e405154ced05b8` |
| App DLL | `81aa39ff9f882744f65d95cec36f24e35d454e9dc51efd58d19ee3ec1dce190c` |
| Unchanged Rust executable | `cfc8e59a08b4aeaad84cc4742aa8a8c2f211a8f575f0d6bcfb6beb5f5a1880fd` |
| Probe DLL | `4cfa57ffdb2f717620c0b3d91ca8f87ceea1f59a582e81b09c2119032342fdcb` |
| OpenCV integration TRX | `115c19295620b77451410c9b752b13cdf3d8b33d04c65a7b49f08681e82ce222` |
| Rust-first integration TRX | `5d8ad3eeb1db9a5007d20a08d96c9362fcef13a1134352ba631812787eac1c89` |
| Pure profile self-test | `e73ed2777e8f762142c545b379eca3cbf06328a0966b52343290ee1143d36b30` |
| Actual seven-control result | `678393d06c888bc1e8a0ef71665245fbd77a6730ddb88c38b03e14d6eabb93a7` |

```text
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~AxisPairEnvelopePruningTests|FullyQualifiedName~AxisPairBudgetTests|FullyQualifiedName~AxisCandidateSnapshotObserverTests|FullyQualifiedName~AxisGeometryPairContactTests|FullyQualifiedName~AxisGeometryAcceptanceTests|FullyQualifiedName~NativeDividerCandidateDiagnosticTests|FullyQualifiedName~OpenCvLineCandidateProviderTests"
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~OpenCvAxisProposalTests" -p:OutDir=<isolated-runtime>
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~RustFirstAxisProposalServiceTests" -p:OutDir=<isolated-runtime>
```

## Actual worker and fixed development confirmation

Seven actual native-worker controls finished in **9.3562054 seconds**, exit 0,
empty stderr: three expected geometries, four expected rejections, zero
violations, exceptions or cancellations. The new 3600 x 1800 slanted-clutter
image returned exactly `(100, 1600, 3300, 1000)` in 2582.359 ms. The existing
clean/detached controls and blank/horizontal/divider/two-panel rejection
behaviors were unchanged. No ticks were invented and no calibration applied.
The parent and workers retained their existing independent CPU limits; this
run did not measure combined child scheduling.

Only then did one pinned geometry-only confirmation process the same twelve
development projects. All twelve projects and panels completed, with eleven
prepared panels and one genuine fallback. **Geometry availability remains
7/12**, with five failures and zero exceptions, unfinished panels, unsupported
dimensions or unattempted cases. All five native calls failed. Their bounded
details now identify two envelope-comparison limits, two exact-contact limits,
and one multiple-candidate rejection. This clarifies the remaining limits but
does not establish an accuracy or availability gain.

The confirmation took **16.5628638 seconds** wall time, with 12,894.4142 ms in
geometry and 1937.4132 ms in preparation. These single-run timings do not prove
a speed improvement over the prior 19.5561165-second trace. It made zero OCR
calls, truth scores, image writes or sealed-payload reads and excluded the same
51 reserved projects. Assignment, selected inventory and twelve-source digests
are unchanged from the prior records. The 13,985-byte report SHA-256 is
`befb9ad4a1e96ff2b8964aba127b9d130bd66cedfda50e44c771806ece0ece98`.
Exit code was 0 with empty stderr.

```text
GraphReader.CalibrationOcrProbe.exe --real-dev-intersection-geometry-self-test
GraphReader.CalibrationOcrProbe.exe --owned-native-intersection-check <isolated-App.exe>
GraphReader.CalibrationOcrProbe.exe --real-dev-intersection-geometry-trace <authorized-corpus> <isolated-App.exe> <retained-452-ocr-assets> <isolated-Rust.exe> --explicit-opt-in
```

Independent source review found no remaining material issue after the profile
initialization correction. **PASS for the owned invariant and bounded runtime
repair; automatic-calibration product acceptance remains FAIL.** The last
OCR-inclusive result is still 0/12 complete automatic drafts. Further unchanged
geometry runs are not planned. Next work targets a defensible label-association
defect on owned controls; neither budget is increased to fit private answers.

The previously pending [native entered-session check](CALIBRATION-ENTERED-ORIGIN-20261115.md)
also completed fit, review gating, Apply and Undo in its isolated source window.
That assistance is distinct from automatic calibration.

After this outcome reaches `origin/main`, retire ten completed files / 187,470
bytes under `artifacts/axis-pair-intersection-check`. Also retire the closed
UI check's owned 4092-byte PNG, five temporary model-asset hardlinks and isolated
source-bin Data. The retained 0.4.52 assets are the checksum-verified hardlink
keepers; shared user Data and original inputs are outside this cleanup scope.
The current headless runtime remains an active compiler/test dependency for
the next source batch. No package, dependency, model or production switch changed.
