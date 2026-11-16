<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Owned partial-axis review integration

One fixed owned control tested the newly delivered 0.4.57 automatic-affine
**review** route with real Rust tick measurement and reviewed CPU OCR. Geometry
was explicitly supplied and source-bound, tagged as an automatic native
suggestion; no detector accuracy was measured. No private/sealed input, model
selection, threshold change or training was used.

## Frozen control and runtime

The 640 by 480 PNG has three-pixel black axes tilted +2 degrees, three X ticks
with Segoe UI 16 labels `1`, `2`, `3`, and a deliberate 90-pixel gap in Y. It
contains no Y labels or markers. The vector drawing endpoints were initially
supplied directly as pixel anchors. That unverified fixture precondition caused
the measured-tick expectation to fail, as the later property audit establishes.

Source PNG: **5,499 bytes**, SHA-256
`afdd99f8af2d548cc46b2ee8cf01603ead200f222f73d09f3f52d6f162984d56`.
The original rendering, geometry and expectations were not adjusted during the
run or its property audit.

The [verified 457 package](INTERNAL-PREVIEW-457-VERIFICATION-20261116.md) supplied
the App executable, Rust worker and reviewed OCR assets. Twenty runtime libraries
(**33,557,624 bytes**) were independently matched to its checksum-bound ZIP and
staged into the existing probe output. Their canonical fingerprint is
`795c2d3bee675747a0a4d3d61fbf446a0f18ad62d80b0baea30689d59319ea2a`:
UTF-8, ordinal filename order, each filename plus TAB plus lowercase SHA-256
plus LF, including the final LF. The probe verifies this map and loaded assembly
identities before worker calls. No model or complete package was copied.

Parent and OCR child used verified **0.4992 logical-core** job limits on the
twelve-logical-processor desktop. The OCR child used CPU, one intra-op thread,
one inter-op thread, one worker/session, BelowNormal priority and no persistent
cache. The main control had a 55-second cancellation and 60-second hard limit.
Mutable Data was isolated from the package and shared user settings.

```text
GraphReader.CalibrationOcrProbe.exe --owned-affine-partial-review-check <owned-output-root> <457/GraphReader.App.exe> <457/review-assistance/ocr> <457/native/graphreader-engine.exe>
```

## Initial integration outcome

The single invocation exited **1**, `expectation_failed`, in **4.2605878 s** wall
and **3,709.2768 ms** reported work. All seven operation stages completed; none
crashed or remained unattempted. **Thirteen of sixteen checks passed**. The three
failed checks required measured X ticks, their exact echo into OCR and measured
partial-X rows. They remain failed; completed operations are not a passing case.

There was one supplied-geometry call, one partial-tick call, zero strict-tick
calls and one CPU OCR call. The native tick service returned
`AXIS_BOX_NOT_OBSERVED`, with no tick evidence. OCR nevertheless returned exactly
the three literal labels `1`, `2`, `3`, with no extra rows. The view model retained
three **estimated** X associations, kept Y maximum blank and disabled Apply both
before and after the review checkbox was selected. Refit invalidated review
without re-running tick measurement or OCR and preserved the raw receipt.
No calibration was applied and no data were exported.

Label reading took **2,506.1801 ms** including its preparation and worker stages.
This one owned startup sample is not an OCR accuracy or latency benchmark.
All eleven protected source/checkpoint/primary-edit hashes, sixteen shared Data
files and twenty keeper libraries remained unchanged. The generated PNG and
scientific project state were unchanged.

| Evidence | SHA-256 |
| --- | --- |
| Initial probe DLL | `6cff55dfe1053471e868a8b709d3171ca79e77edebdb8ace3f94dbbd1a74d8a0` |
| Initial result | `f1a7c77f8c899d084fd1b52594fdc49d27416c00baeb2c5d4ff740c622eed391` |
| Initial wrapper receipt | `77a9cfa0c19a49fba98fe42fd6807e533ddee6188807499773d034ea51ae0f19` |
| Derived PNG | `7bf7a8007ec73ef6b0cbfa3eaac9e42c30f209fed2b7e25686e17787284162f0` |

## Exact property audit

A separate, single property-only invocation read the existing PNG and saved
result. It recreated the same original frame and required the exact derived
hash, **658 by 503** dimensions and box **(109,91)-(589,391)** before interpreting
properties. It used the existing clipped horizontal-span replay unchanged.
Original nearest-pixel samples use `Math.Round` with midpoint-to-even; the report
also retains the positive-weight bilinear source lattice samples. No alternate
geometry, raster, window or threshold was tried.

```text
GraphReader.CalibrationOcrProbe.exe --owned-affine-partial-properties-check <existing owned-partial-affine.png> <saved initial result.json>
```

| Property | Recorded value |
| --- | ---: |
| Original nearest origin gray | 47 |
| Original nearest X-end gray | 255 |
| Original nearest Y-end gray | 127 |
| Derived origin / X-end / Y-end gray | 45 / 244 / 74 |
| X-span result | `TERMINAL_GAP`, length 1, bridge work 0 |
| First raw Y gap | 89 pixels, beyond the existing 41+4 gap limit |

The source X endpoint already lands on white pixels, and about 95.57% of its
bilinear weight is white. Thus the supplied "intact X" frame does **not** satisfy
the worker's inclusive ink-endpoint requirement. The derived X endpoint remains
outside the unchanged `Gray8 < 200` predicate. This establishes an invalid
fixture precondition, not a demonstrated production rectifier defect. Y was
intentionally broken. Its reported first raw gap is not a complete vertical
bridge replay, and no partial-worker diagnostic was fabricated from it.

The audit exited **0** in **0.7643183 s** wall / **379.8186 ms** reported work,
with all five stages completed, no failures/unattempted stages and all input,
runtime and external preservation checks passing. It made one rectification,
**zero worker/OCR/detector calls**, no image writes and no private/sealed reads.
Its 30/35-second limits and half-core cap were enforced.

Property probe DLL:
`aef77d3e93d6da4483592db4d2c2be96351290dc6975e4c0316a32dbd4524174`.
Property report:
`150bfb2fc36e1ffd3803135deacd4ae3474db1319b907b9c6fc53cbed25a2c15`.
Property wrapper:
`55b2d9881a582b5bbbc76bf442de005e525a662d87858be154b5546adb37b066`.

## Build attempts and scope

Only the probe project was compiled, with `BuildProjectReferences=false`,
`--no-restore`, one build node and no shared compilation. No App package was
rebuilt or version advanced. The first compile was void: ambiguous target-typed
`Point` construction and a dictionary analyzer error, exit 1 in 9.0766794 s.
Their narrow repair built cleanly in 11.8811584 s. Independent review then bound
the complete library map and protected the final report from fixture-access
errors; that changed source built cleanly in 9.8371783 s. The property mode built
once in 10.1655532 s. Successful builds had zero warnings/errors.

These controls establish actual literal-OCR fallback and missing-value review
safety on one owned image. They do **not** establish successful measured partial
ticks, automatic geometry, native UI, representative real-graph quality or
production approval. No model gate or historical failed result was regraded.

The exact earlier real affine case cannot be selected from retained tracked
hashes alone: its original case/crop report and later compact selector were
retired. No deleted predictions were recovered, originals scanned or twelve-case
geometry prefix repeated to rediscover it. A new confirmation of that exact
case needs an authoritative retained case-to-crop mapping.

The owned PNG, reports, small runtime map and probe outputs remain active only
for the bounded fixture-precondition follow-up. The current product package,
runtime models, original data and pending native-QA inputs remain protected.
After the coherent outcome is pushed and consumers finish, retire completed
`artifacts/owned-affine-partial-review-check` output and inactive probe binaries.
Project-authored source is Apache-2.0; no dependency/license change, paid credit
or reset redemption was used.
