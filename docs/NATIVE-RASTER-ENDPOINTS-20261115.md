<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native raster endpoint support

## Defect and correction

The [owned contrast controls](NATIVE-CONTRAST-TICKS-20261115.md) exposed four
native endpoint errors: three rounded beyond observed ink and one ended before
the final tick. The exact-box scanner correctly rejected or withheld evidence.
These controls provided the defect reproduction without private answers.

`NativeAxisRasterGeometry.Adjust` now refines only the right and top endpoints
within the native provider's existing two-pixel uncertainty radius. It requires
a supported unchanged origin, continuous `<200` ink from that origin to an
interior seed, and a nearby transition to background or image boundary. It
cannot jump a gap. Unsupported endpoints keep their original review geometry.
Left/bottom, confidence, rank and warnings are preserved. Work is bounded by
the image width plus height per candidate, with cancellation polling.

Both encoded-image routes use the same decoded Gray8 buffer and helper. The
explicit chooser refines before its existing exact-box deduplication/reindexing.
Pixel-less line controls retain their original rounding behavior. GRT1 remains
strict: it measures the final exact box without moving it or inventing ticks.

## Owned validation and failures

The initial Debug test build stopped after **65.823 seconds** on three test-only
analyzer errors: two CA1861 static-array requirements and MSTEST0044 requiring
`TestMethod` rather than `DataTestMethod`. No tests ran. After correction, the
same build succeeded with zero warnings/errors in **12.931 seconds**. The
reviewed `c96f91b3...3b31` OpenCV runtime was restored in App and test outputs.

The focused native-axis suite passed **45/45**, zero failures/skips, in **1 second**
test time / **5.259 seconds** wall. Controls cover both endpoint directions and
radius boundaries, larger offsets, disconnected origin, interior gaps, detached
nearby ink and cancellation. An encoded 63/191 phase control verifies actual
normal/candidate-zero coordinate agreement, the observed right endpoint, unique
candidate boxes and contiguous indices. Independent source review found no
material issue after the full-line support guard was added.

The separate diagnostic profile initially placed its verifier outside the
nested `Candidate` type. Read-only review caught the accessibility error before
compilation; the verifier was moved into a private partial `Candidate`. No private
read or failed execution occurred for that wiring error.

One probe-only build then passed, zero warnings/errors, **7.50 seconds** MSBuild
/ **8.1559942 seconds** wall; all 20 runtime libraries were verified and staged.
The new profile self-test, including the existing exact-box binding self-test,
passed in **0.3740002 seconds**, without private reads.

The missing-arguments boundary check returned the expected exit 1 and sanitized
`REAL_DEV_NATIVE_RASTER_ENDPOINT_TRACE_FAILED` result in **0.2513046 seconds**,
with empty stderr and zero sealed reads.

One actual owned native-to-Rust run completed from
`2026-11-15T18:24:13.6839560Z` to `2026-11-15T18:24:20.2453376Z`, exit 0,
empty stderr, **6.5608591 seconds** wall. All **9/9 native calls** and **8/8
attempted measurements** completed with zero stage exceptions. All nine image
hashes matched the preceding owned run. Origins/confidence were unchanged and
every endpoint correction was within two pixels.

All **six positive graphs** returned their full three X and three Y ticks,
including all four antialias phases. Previously only two positives had complete
tick sets and one had Y ticks alone. The no-tick graph retained empty lists;
blank retained no geometry; the disconnected corner retained measurement failure.
Fixture bounds were annotations only: the worker detected each box itself.
No OCR or numeric calibration was involved in these owned controls.

## Fixed development confirmation

One integrated run used the unchanged first 12 sorted development identities,
same preparation/fallback policy, automatic geometry, literal OCR, production
exact-box binding helper and numeric association. The new source profile is
separate; historical runtime pins and unchanged451/Mirage outcomes were not
altered or rerun. This transient binding probe does not establish workspace
currentness or native UI acceptance.

The run completed from `2026-11-15T18:25:08.5570550Z` to
`2026-11-15T18:25:45.9499484Z`, exit 0, empty stderr, **37.3878213 seconds**.
All **12/12** sources/panels finished: 11 prepared and one genuine full-image
fallback. Geometry/OCR coverage remains **8/12**, with three native pair-budget
failures and one rotated-best-candidate rejection. No timeout, exception,
unsupported dimension or unattempted panel was omitted.

The one native exact-box attempt now bound **20 observed X ticks and seven Y
ticks**, in **54.6254 ms**, rather than `AXIS_BOX_NOT_OBSERVED`. It retained its
multiple-candidate and Rust-ambiguity review reasons. Overall counts remain
**128 OCR regions, 79 literal numeric rows and 70 associations**. Associations
using measured positions increased **58 to 65**; those using estimated positions
decreased **12 to five**, and panels using estimates decreased **two to one**.

Complete session drafts remain **zero**. Printed-X drafts remain **two**,
partial-X drafts **zero**, and partial-Y drafts **three**. The affected native
panel's X labels remain in one compound OCR region with multiple numeric
tokens, so single-number validation rejects it. No values were split or guessed.
This is an evidence-quality improvement, not complete calibration acceptance.

Stage totals: preparation **1,955.529 ms**, axes **10,614.0975 ms**, OCR
**23,646.2635 ms**, association **66.4761 ms**. Median/max OCR time:
**2,383.77225 / 6,800.1309 ms**. The prior fixed run took 43.4724307 seconds,
but these single CPU runs do not establish a performance improvement.

Assignment, inventory and source digest match the previous fixed trace:
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`,
`03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`,
`4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1`.
All 51 independent cases remained excluded. Sealed reads, truth scoring, image
writes, training, Apply and export were zero.

## Commands, identity and retention

```text
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-build --no-restore --filter "FullyQualifiedName~NativeAxisRasterGeometryTests|FullyQualifiedName~NativeAxisCandidateReviewTests|FullyQualifiedName~NativeAxisWorkerDetectionFailureTests|FullyQualifiedName~OpenCvAxisProposalTests"
dotnet <probe.dll> --real-dev-native-raster-endpoint-trace-self-test
dotnet <probe.dll> --owned-exact-box-contrast-check <source-App.exe> <source-Rust.exe>
dotnet <probe.dll> --real-dev-native-raster-endpoint-trace <corpus-root> <source-App.exe> <reviewed-ocr-assets> <source-Rust.exe> --explicit-opt-in
```

Source base: `0642f21f42894cfc2997dab40dcce4ae932ad480`.

| Item | SHA-256 |
| --- | --- |
| App EXE | `a7c3a7757d2f70c3adb553b7f01e71538dcd603f55126f5bcf320e5ccd827444` |
| App DLL | `567eecf5ab59a2905b3a2feba5e7fe84a493c8b50f46ea42bf1f2668329dbd9a` |
| Rust worker | `2cc6f263a00544243291f2605031c4df7e147175bd345480bd0a44f92fde0b7a` |
| Probe DLL | `6823f34cf3e5482ffa40dbb072ba2c4ebad10193cb5ccfe56921222477ad26bc` |
| Focused TRX | `3bcadfd59a08272c8a662773420c175e9a3276de86ca0b5ec3bbaf138ed2680b` |
| Runtime pin manifest | `ca71652dec2bde05c55a1a4a41ce5b67d8f62a8f4e64a5ea29f3e3874f61069a` |
| Owned endpoint report, 9,390 bytes | `951c23ac5a088f39ae119114068ca4ce1631190bc315bb18d7a30a78e3cdc0e3` |
| Development report, 65,406 bytes | `c052507d9185b7f1132c36d551592aa5b24103c5277ea3188ba795dfbfc30e87` |

All exact runtime pins are in `RealDevNativeRasterEndpointTracePins.cs`. No new
dependency, model, license or public schema was introduced; source is Apache-2.0.
The sole packaged 0.4.54 build is unchanged. Full Windows CI
[43748183530](https://github.com/SKang393/Graph-auto-reader/actions/runs/43748183530)
passed on `308d7a823afbec316a73f4e2e16b528f27ca9c2f`: 13 .NET suites,
3,031 passed / 30 skipped / zero failed, all 72 Rust tests, and applicable
policy/packaging checks including 68 packaging verification tests. App passed
1,193 tests. Release jobs were skipped. No production automatic-calibration or
native UI pass is claimed.

Retain `artifacts/native-raster-endpoint-dev-trace` only for the compound-row
grouping diagnosis; retire after that consumer finishes and outcomes are pushed.
The focused checks, owned before/after reports and obsolete exact-box raw report
can be retired after integration consumers finish and this record is pushed.
Keep the current runtime pin manifest only until the next runtime transition.
Originals, independent inputs and active model/runtime assets are preserved.

After this source/outcome reached `origin/main` as
`308d7a823afbec316a73f4e2e16b528f27ca9c2f`, the completed endpoint comparison and
runtime-transition consumers released **nine files / 78,161 bytes** at
`2026-11-15T18:49:19.7880628Z`: the previous exact-box development trace (three
files / 65,639 bytes), its obsolete runtime-pin directory (one / 2,318 bytes),
and the preceding owned contrast report directory (five / 10,204 bytes).
Exact key hashes, complete inventories, resolved workspace paths, absence of
reparse points and active process consumers were checked before removal. These
three targets are absent. The current endpoint development trace remains only
for the X-row component diagnosis; original and independent inputs are intact.
