<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Gray-axis review fallback

## Defect and fixed owned comparison

The [development diagnosis](AUTOMATIC-CALIBRATION-DIAGNOSIS-20261115.md) identified
neutral-gray axes outside the Rust detector's fixed dark-pixel predicate. Its
single histogram experiment failed owned negative controls and was removed.
This batch retains that Rust detector and tests the existing reviewed OpenCV
path. No private image, label answer, model, optimizer or threshold sweep is
used to choose the repair.

Four fixed in-memory controls were sent once to the actual native worker and
once through the unchanged Rust-first composition at source `bc513f79`:

| Owned input | Direct OpenCV | Prior composed behavior |
| --- | --- | --- |
| Black axes, ticks, black marker | Exact plot, no ticks | Rust exact plot, 3 X and 3 Y ticks |
| Gray axes/ticks (192), black marker | Exact plot, no ticks | Rust `AMBIGUOUS_AXIS`; native not called |
| Faint axes (128), no ticks | Review plot, no ticks | Native review plot, no ticks |
| Two plots | `OPENCV_AXIS_AMBIGUOUS` | Rust `AMBIGUOUS_AXIS` |

The first three controls are 128 x 128 with plot bounds `(20, 10)-(115, 100)`.
The two-plot control is 512 x 160. These are generated geometry controls without
numeric labels. A faint no-tick plot is not a validated calibration. Native
geometry carries no measured ticks or inferred numeric values.

The before check exited 0 with empty stderr and no exceptions in 5.0760635
seconds; its safety assertions passed. Direct gray geometry took 1123.6119 ms;
the prior Rust-only gray rejection took 50.2486 ms. Cold process timings on four
controls do not establish representative throughput. Report SHA-256:
`8039602b5d4402d628793b0ac870020710831c016725d8644534385a9196e249`.
Probe DLL: `97c663db76a1cd9808ff36b0a294b447423c91493811b1edd61994f879d376bf`.

## Runtime and license identity

This is an unpackaged source-test runtime, not the retained 0.4.52 package. The
probe-only compile took 8.38 seconds with zero warnings/errors and reused the
existing App output. App, OCR and Rust hashes were unchanged by that compile.
The adjacent compiler-output OpenCV DLL was replaced with the reviewed runtime
from the sole retained 0.4.52 package after checking the tracked provenance
policy. Its SHA-256 is
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`.
The package was not modified. No dependency, model or runtime license changed.
The worker retains its existing CPU budget and 12-second watchdog.

## Integration verification

The composition now invokes the existing strict native reviewer for explicit
Rust `AMBIGUOUS_AXIS` as well as the prior `NO_AXIS` / `NO_INTERSECTION` cases.
A successful ambiguity fallback carries `opencv-review` and the additional
`rust_axis_ambiguous` review reason. If native detection also rejects it, the
original Rust ambiguity is returned. Valid Rust geometry and input, protocol,
budget and timeout failures retain their previous short-circuit behavior.
No geometry detector, threshold, model, numeric association rule, image binding
or Apply guard changed. Native geometry still has no measured ticks; the
workspace rejects native results that claim otherwise. The existing localized
warning and explicit review confirmation remain required.

The focused App suite passed 28 tests with no failures/skips, about 1 second in
tests / 72.1205789 seconds including compilation. The integrated App suite then
passed **1063 tests, 0 failed, 0 skipped**, 37 seconds in tests / 40.3012703 seconds
wall time, without rebuilding. This integrated run also used the reviewed
OpenCV DLL in its existing test-output directory.

One actual after check used the same four owned fixtures, exited 0 with empty
stderr and no exceptions in **7.0355163 seconds**. Gray axes now returned the
exact plot as native review geometry with the ambiguity reason and empty ticks
(765.0532 ms in the composed path). The two-plot control still returned Rust
ambiguity after native rejection (1233.4365 ms). Black Rust geometry/ticks and
faint no-tick native review behavior were unchanged. All safety assertions
passed. Report SHA-256:
`0c1ccb9e0bf5806dbd5f5f2a41fa27d7b07120950ddecbbaac5c1a999d1cca20`.
The extra native attempt costs time; these controls do not establish a general
speedup or detection accuracy. Nothing was applied or exported.

The historical before-check mode remains separate from the after-check mode.
The subsequent probe-only compile included the final sanitized source-trace
failure handler; its pure owned self-test passed and its missing-argument check
returned the intended sanitized error / exit 1. It did not rerun native or
private inference. Independent source reviews found no material routing,
review-binding or trace-split issue.

The App rebuild records source revision `bc513f79` in assembly metadata. Managed
DLL hashes changed from the earlier owned Y run even where source was unchanged;
no old binary copies were retained merely to compare that metadata.

| Executed source identity | SHA-256 |
| --- | --- |
| App EXE | `0777827c284e6dbbfc6bf675d4662c23564f93a7cab276e0fa211c56942177e4` |
| App DLL | `68025084f67e6bd5ab7087a94e19904316dc3adee8948869625defc170c44683` |
| OCR DLL | `f43dfb3a29e98b10a34f91ecd3dee7f4f9fae18279223315894faa0c617fcbe1` |
| Rust worker | `cfc8e59a08b4aeaad84cc4742aa8a8c2f211a8f575f0d6bcfb6beb5f5a1880fd` |
| Final probe DLL | `e3d1df02a1960f7304fc46eb4ab5ae52607e4ad5b95b318e531dcd4414a3f1ad` |

```text
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Release --no-restore -m:1 -nr:false -p:BuildProjectReferences=false -p:UseSharedCompilation=false -p:NuGetAudit=false -o <existing-source-App-bin> --verbosity minimal
dotnet <existing-source-App-bin>/GraphReader.CalibrationOcrProbe.dll --owned-gray-axis-check <source-App.exe> <source-Rust.exe>
dotnet <existing-source-App-bin>/GraphReader.CalibrationOcrProbe.dll --owned-gray-axis-after-check <source-App.exe> <source-Rust.exe>
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:NuGetAudit=false --logger "trx;LogFileName=integrated-app.trx" --results-directory artifacts/gray-axis-owned-check --verbosity minimal
```

**PASS for the scoped source repair and owned checks.** Native UI acceptance,
representative real-data quality and production automatic-calibration approval
are not established by these controls. The separate fixed development trace
records the combined source changes without rerunning the baseline.

## Retention

`artifacts/gray-axis-owned-check` is bounded temporary metadata needed by the
active before/after integration consumer. Retire its results after this source
and final outcome reach `origin/main`. Its reviewed closeout scope is seven
files / 1,606,026 bytes. Preserve original inputs, active source
compiler dependencies, shared user Data and the sole 0.4.52 package. No sealed
payload or new packaged build is part of this owned check.
