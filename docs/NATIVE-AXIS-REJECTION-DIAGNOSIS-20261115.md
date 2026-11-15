<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native axis rejection diagnosis

## Finding

All five native failures in the fixed 12-project development sample exhausted
the existing **128-pair examination limit**. They did not reach the competing
candidate, rotation or corner-bounds rejection branches. This directs the next
repair toward bounded pair enumeration, not ambiguity thresholds or OCR models.

The detector currently charges every horizontal/vertical family combination
before checking whether the observed segments can meet. The next owned repair
must preserve every contact-eligible candidate and the existing ranking,
ambiguity and geometric tolerances. Merely raising the limit or moving its
counter behind an unbounded Cartesian scan would not establish that property.

## Implemented diagnostics

`AxisGeometryDetectionException` now carries an additive typed reason while
retaining its original constructor and `AXIS_GEOMETRY_NOT_FOUND` code. The native
worker keeps `OPENCV_AXIS_AMBIGUOUS` and reports a fixed detail through the existing
bounded `ReviewReasons` field. Rejection order and acceptance are unchanged.
The Rust-first service still returns the original Rust failure when native
review fails; the diagnostic observer captures native details separately.

A new explicitly opted-in geometry-only trace reuses the existing frozen first
12 of 120 development projects, preparation, original-image binding, deadlines
and failure accounting. It creates no OCR client. Twenty runtime-library hashes,
App executable/DLL and Rust hashes are checked before loading the selector.
Historical 451, 452 and post-grouping runtime bindings remain separate.

## Verification

| Check | Result | Timing |
| --- | --- | --- |
| Focused Axis exception/contact/budget tests | 15 passed, zero failed/skipped | 196 ms tests; 10.040 s command |
| Owned divider detector controls | 2 passed, zero failed/skipped | 441 ms tests; 9.449 s command |
| App compile to isolated temporary output | zero warnings/errors | 84.921 s |
| Focused native worker/client/Rust-first tests | 22 passed, zero failed/skipped | 267 ms tests; 5.705 s command |
| Probe-only compile | zero warnings/errors | 10.587 s |
| Geometry accounting and missing-pin self-test | passed, no private reads | included in preflight |
| Actual native worker on five owned controls | all five accounted; no violations, exceptions or cancellations | 6.627 s wall |

The owned worker returned the clean plot and rejected blank, horizontal-only,
bottom-touching-divider and two-panel controls with their expected typed reasons.
The separate detector control shows that a bottom-touching divider can produce
two distinct axis pairs even when the reasoner selects the full plot. That owned
finding does **not** explain the five development failures, which stopped earlier
at the pair budget. No divider-related acceptance change was made.

```text
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AxisPairBudgetTests|FullyQualifiedName~AxisCandidateSnapshotObserverTests|FullyQualifiedName~AxisGeometryPairContactTests"
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-build --no-restore -p:OutDir=<isolated-runtime> --filter "FullyQualifiedName~NativeAxisWorkerDetectionFailureTests|FullyQualifiedName~OpenCvAxisProposalTests|FullyQualifiedName~RustFirstAxisProposalServiceTests"
GraphReader.CalibrationOcrProbe.exe --real-dev-native-rejection-geometry-self-test
GraphReader.CalibrationOcrProbe.exe --owned-native-rejection-check <isolated-App.exe>
GraphReader.CalibrationOcrProbe.exe --real-dev-native-rejection-geometry-trace <authorized-corpus> <isolated-App.exe> <retained-452-ocr-assets> <isolated-Rust.exe> --explicit-opt-in
```

## Fixed development result

One new instrumented run completed **12/12 projects and 12/12 effective panels**:
11 prepared panels plus one genuine preparation fallback, seven geometry returns,
five geometry failures, zero unsupported panels or panel exceptions, and zero
unfinished or unattempted panels. All five native calls reported
`native_pair_budget_exceeded`. Wall time was **15.8841803 s**, with 12,994.6002 ms
in geometry calls and 1,321.4804 ms in preparation. This geometry-only duration
is not a speed comparison against the prior OCR-inclusive trace.

There were **zero OCR calls, truth scoring, image writes or sealed-payload reads**.
The 51 reserved projects remained excluded. Source digest
`4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1`
matches the earlier fixed trace. Assignment SHA-256 remains
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`;
selected inventory remains
`03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`.

| Evidence identity | SHA-256 |
| --- | --- |
| App DLL, base `7f0b537d` plus typed diagnostics | `a1666193c979641c1b268e76d6cf4477d8e3b9f7e0df65cdbe8e69451fd7b418` |
| Probe DLL | `bbdda964d96d9975bd28d719284985d8109fcd385fe0cf679077fb04134487ec` |
| Axis test result | `a38388e208967a5be6c17db15cc524806299efdbf139376c282c6437b5536375` |
| App test result | `07353ba55b0a74a88b084433136fce419b45b9c40255cea2e19efd502df6f041` |
| Owned divider test result | `cc3c0121ba9f79921362fc54eb79ca7b1133a8c55b0d0bb37d53fa1b08a88773` |
| Pure geometry self-test | `0c5f5727cc05bec59d94ca502a7fa77c3d9a3d66b0f479edc924758ceb11fd96` |
| Actual owned worker result | `4ec9a95afa910c30d578beb3914d64162f95da7340c7088f43e727c4094c207e` |
| Development result, 13,975 bytes | `5855778182bc5ffb53588fbbba916e01fcf6076dd5eb26ae3924fc270cb9f76b` |

Reviewed OpenCV `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`
was restaged after compilation and verified before/after native execution.
All actual probe runs exited 0 with empty stderr. Independent source review found
no material issue in split exclusion, zero-OCR control flow or failure accounting.

## Status and retention

**PASS for the diagnostic batch.** Automatic calibration availability remains
unmet: the last OCR-inclusive trace still has zero complete drafts out of 12.
No model, threshold, dependency, package or production-approval change was made.
The new entered-origin native UI check is separately waiting at its file dialog
in [REV-011](REVIEW_QUEUE.md).

After source/outcome `9630c6d6` reached `origin/main`, guarded cleanup at
2026-11-15T08:13:10.4781621Z retired the nine completed files / 72,050 bytes in
`artifacts/native-axis-reasons-check` and the one file / 5,710 bytes in
`artifacts/native-divider-owned-check`. Scope, hashes recorded above, absence of
reparse points and zero active consumers were checked. No raw development report
is needed by the next owned pair-search repair. The 220-file / 53,226,692-byte temporary
compiler output remains the active headless runtime for that repair; retire it
after its final consumer finishes. The open source UI, its isolated Data and
temporary OCR hardlinks, originals, shared user Data and sole 0.4.52 package
remain intact.
