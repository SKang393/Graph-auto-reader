<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Bounded axis pair enumeration

## Defect and repair

The [fixed development diagnosis](NATIVE-AXIS-REJECTION-DIAGNOSIS-20261115.md)
identified pair-budget exhaustion in all five native failures. An owned
reproduction then combined one valid plot with twelve detached horizontal and
twelve detached vertical strokes. Its 169 family combinations exhausted the old
128-pair limit even though unrestricted enumeration returned exactly one plot.
Both original and reversed input orders failed before the repair.

Bounded searches now cache each family's envelope from **all observed member
endpoints**, expanded by the existing contact tolerance. Strictly disjoint
envelopes cannot share a point satisfying both finite-segment contact checks.
Their pairs are skipped before exact examination. Overlap alone does not imply
contact; the existing intersection, observed-contact, scoring and ordering rules
still run unchanged.

The resource semantics are explicit: a requested limit B permits at most B
exact pair examinations and B squared cheap envelope comparisons, computed in
`long`. At the native worker's B=128 these limits are 128 and 16,384. Cancellation
is checked during envelope construction and pair iteration. Either exhaustion
fails closed before publishing any partial geometry or candidate list.
Unrestricted callers retain the original enumeration path. No geometric or
score threshold changed, and the native uniqueness gate remains in place.

## Owned verification

The acceptance property is full candidate identity/order and selected-result
parity with unrestricted enumeration, excluding elapsed time. This is a runtime
invariant check, not a scientific accuracy gate. All inputs are owned deterministic
controls; the sealed-run budget is zero.

The two failing-before checks took 16.2806997 seconds including compilation.
After repair, the focused Axis build completed with zero warnings/errors in
10.2200435 seconds. **89 tests passed, zero failed/skipped**, in 3.9752062 seconds
wall time. The 18 new cases cover input order, dividers, grids, slanted and
multi-member support, contact-tolerance boundaries, the 128/129 exact-pair
boundary, the B-squared comparison ceiling, large-budget arithmetic and
cancellation. Reviewed OpenCV was staged after compilation and unchanged after
execution.

Only the repaired Axis DLL was then compiled into the existing temporary App
runtime, in 1.6457583 seconds. Hash verification proved that the App executable
and the other 19 runtime libraries were unchanged. The probe-only build completed
with zero warnings/errors in 11.2845424 seconds.

Six actual native-worker controls completed in **4.5478492 seconds**, exit 0,
empty stderr, no violations, exceptions or cancellations. The clean and detached
clutter plots returned their expected bounds without inferred ticks. Blank,
horizontal-only, bottom-touching-divider and two-panel inputs retained their
existing rejection behavior. The last two still report multiple ranked
candidates. No calibration was applied.

| Evidence | SHA-256 |
| --- | --- |
| Failing-before test result | `3bcef89b2027479317fa737ea5a6f391900fe74350986fa2d27dd6b3d59515a3` |
| Passing Axis test result | `7ce823aa4fa4d8ce26b7c2ae620158522d003a7504a0f18d389e9bc655da0114` |
| Unchanged App DLL | `a1666193c979641c1b268e76d6cf4477d8e3b9f7e0df65cdbe8e69451fd7b418` |
| Previous Axis DLL | `819310adfd593feaf0658ae76db70b2fefcd4d6cd285739576d519e7fc20d4eb` |
| Repaired Axis DLL, base `9630c6d6` plus pruning | `6da1e2e71f1ba72205f0bf579c73c92a36ec24f66be57b39db2346ecb925aa2d` |
| Probe DLL | `b3ea5939c080261b4c3cc580917a2bf45075464dd22fe0cff2070069be88c86e` |
| Geometry-only profile self-test | `5be1f3769e1fa508e110571dfd91d9992f258d1227b4249789ead138405fc7ec` |
| Actual six-control result | `b9830c7feaaceb09bb1148b8c4272d70b371d5cde10905a570848f387626f6ab` |

```text
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~AxisPairEnvelopePruningTests|FullyQualifiedName~AxisPairBudgetTests|FullyQualifiedName~AxisCandidateSnapshotObserverTests|FullyQualifiedName~AxisGeometryPairContactTests|FullyQualifiedName~AxisGeometryAcceptanceTests|FullyQualifiedName~NativeDividerCandidateDiagnosticTests|FullyQualifiedName~OpenCvLineCandidateProviderTests"
GraphReader.CalibrationOcrProbe.exe --real-dev-pair-pruning-geometry-self-test
GraphReader.CalibrationOcrProbe.exe --owned-native-pruning-check <isolated-App.exe>
```

## Development confirmation and completion

One separately pinned geometry-only confirmation completed all 12 development
projects and 12 effective panels: 11 prepared panels plus one genuine fallback.
Geometry availability remained **7/12**, with five failures and no unsupported,
exception, unfinished or unattempted panels. All five native calls still failed:
four reported `native_pair_budget_exceeded` and one reached
`native_multiple_ranked_candidates`. The previous run had five pair-budget
failures. The four remaining budget details do not distinguish the exact-pair
limit from the newly bounded cheap-comparison stage; neither cause is inferred.

Wall time was **19.5561165 seconds**, geometry calls 15,578.7846 ms and preparation
1,649.5416 ms. This single run does not establish a speed improvement. It made
zero OCR calls, truth scores, image writes or sealed-payload reads, excluding
the same 51 reserved projects. Source digest remained
`4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1`;
assignment and selected-inventory hashes match the preceding diagnosis.
The 13,980-byte report SHA-256 is
`e081be83b1ebb18b1ceef0871d4fb065b529c7a7fea657269ae88e08876172a8`.
Exit code was 0 with empty stderr. Independent source review found no material
issue in the pruning proof, work bounds or zero-OCR trace path.

```text
GraphReader.CalibrationOcrProbe.exe --real-dev-pair-pruning-geometry-trace <authorized-corpus> <isolated-App.exe> <retained-452-ocr-assets> <isolated-Rust.exe> --explicit-opt-in
```

**PASS for the owned repair and bounded runtime behavior; real geometry
availability did not improve.** The last OCR-inclusive automatic-calibration
result remains zero complete drafts out of 12. The next source investigation
tests whether a fitted intersection outside an observed-member envelope can
be excluded before costly contact checks, and separates the two budget causes.
It must retain the candidate set and existing limits, not guess a winning plot.

No model, dependency, package or production approval changed. After outcome
commit `6e40b21f` reached `origin/main`, guarded cleanup at
2026-11-15T08:37:06.4810121Z removed all eight completed reports / 152,755 bytes
under `artifacts/axis-pair-envelope-check`. Canonical scope, reparse and active
consumer checks preceded removal. The existing headless compiler output and
then-open isolated native UI remained active consumers; originals and the
retained 0.4.52 package remained unchanged.
