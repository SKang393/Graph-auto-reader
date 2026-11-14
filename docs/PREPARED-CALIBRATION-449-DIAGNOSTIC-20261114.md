<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Prepared calibration diagnostic, 0.4.49

## Purpose and scope

The 0.4.47 geometry-only diagnostic found a small availability difference
between raw images and production-panelized images, but did not explain why
complete OCR calibrations remained at zero. This bounded follow-up exercises
the delivered import panelizer, real OCR worker, and calibration refit together
on the same fixed real-dev assignment. It is a plumbing diagnostic, not model
selection or an accuracy run.

Before the private run, owned controls in
`artifacts/prepared-calibration-449-owned-check/` verified the path without
private payload reads. The previous antialiased two-plot fixtures returned
`AXIS_AMBIGUOUS` for both panels; this is a fixture-specific result, not a
general model conclusion. The exact Gray8 known control exercised OCR and
refitting successfully: the labeled raw image and single prepared panel each
returned six numeric rows and one complete measured calibration. Both stacked
prepared panels did the same. The unlabeled raw control returned zero numeric
rows and no complete calibration. Source pixels were unchanged, and prepared
panel crop offsets/mapping were checked against the original image.

Owned self-tests passed with zero private payload reads. They checked affine
residuals and the calibration-response funnel, rejected forbidden output, and
blocked missing capability before selection. The existing reader, model,
manifest, thresholds, and production runtime were not changed for this
diagnostic.

The final owned-control report SHA-256 is
`91f3fe29dd9daea9fbcfb12522cd395f7336ed3cedb1aa59c6cb23d1d9d08810`
(2,625 bytes, 44.246 seconds). The final self-test report SHA-256 is
`d022bb4a4eeea324aa7e10f30b30d5640dc8259ae2bde14fedbbcae71d1528c3`
(269 bytes, 1.283 seconds). Both exited 0. The probe compiled without warnings
or errors. These checks exercise the existing binaries, not a new package.

## Fixed candidate and reproduction

The immutable candidate is ManualPreview `0.4.49`, source
`2aea386bf24300416aa773a4cbf874717cf12752`, package SHA-256
`422eb96e35a7c4d73ae87c13a1a5079450f4d6d42f5ccd161a26c8b2b514d1c6`.
The delivered App EXE SHA-256 is
`f95dc00c01166c67656b8bfd165c7b6d92ca15bf489b874e85cdd36824b2f2fa`; the
Rust worker is
`e98e3ff2d8edd77141b85c096fd63d6cd670db82c9f745e6d8bac2c6daf1f4cc`.
The OCR model and manifest are pinned to
`7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` and
`17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a`.
The prepared diagnostic mode is `--real-dev-prepared-calibration-check`; it
uses the fixed 120-source real-dev assignment, processes every returned panel,
and requires `--explicit-opt-in`.

From the active worktree, with the delivered runtime libraries staged and
verified as in the 0.4.49 confirmation:

```powershell
$probe = Join-Path $PWD 'artifacts/hardware-final-build/GraphReader.CalibrationOcrProbe.exe'
$build = 'E:/Graph-auto-reader/artifacts/dev-portable/builds/0.4.49-20261114T202631548Z-2aea386b'
& $probe --real-dev-prepared-calibration-check 'E:/Graph-auto-reader/data/manual data' "$build/GraphReader.App.exe" "$build/review-assistance/ocr" "$build/native/graphreader-engine.exe" --explicit-opt-in
```

The run is bounded by the probe's 900-second overall deadline and per-case
deadlines. Its aggregate output cap is 1,000,000 bytes, below the 10 MiB
private-output budget. The mode writes no images or per-case records, does not
provide manual truth to inference, reads no sealed data, and performs no
optimizer steps. The completed aggregate outcome below contains no private identifiers,
pixels, or truth rows.

## Limits

The owned Gray8 control proves that OCR rows can flow through the prepared
panel path into a measured fit for that fixture only. The antialiased controls
remain incomplete. Neither result establishes real-data accuracy, improvement,
or production readiness. Manual truth is not used for inference or candidate
selection, and no calibration is applied automatically.

## Fixed real-dev outcome

The prepared-panel mode completed the same frozen 120-source assignment from
`2026-11-14T21:43:03.0650974Z` to `2026-11-14T21:46:59.1463753Z`. All 120
payloads were hashed; there were 118 distinct embedded images. Preparation
returned 112 panels and used 15 full-image fallbacks, so all 127 effective
panels were attempted. Geometry and OCR returned for 39 panels with 318
numeric readings. **Complete measured calibrations: 0/120. Complete estimated
calibrations: 0/120.** All 120 projects were fully processed without a complete
calibration. Residual metrics therefore have no samples; no error score is
implied.

There were zero processing errors, unsupported panels, unattempted panels,
unclassified preparations or incomplete projects. The first-observed source
identity lock is not a preregistered content hash. Prior real-dev exposure and
imperfect manual ground truth remain known limitations; this is not a new holdout.

Reason counts can overlap: `AXIS_AMBIGUOUS` 84,
`AXIS_NATIVE_UNAVAILABLE` 4, `TICK_AMBIGUOUS_ASSOCIATION` 2,
`TICK_DUPLICATE_ASSOCIATION` 1, `TICK_INCONSISTENT_SPACING` 2,
`TICK_INSUFFICIENT_PAIRS` 33, `TICK_INVALID_NUMERIC_LABEL` 13,
`TICK_INVALID_RANGE` 2, and `TICK_UNPAIRED_LABEL` 25. Preparation time over
120 sources had median 44.330 ms, p95 352.509 ms, and maximum 3,661.790 ms.
Aggregate pipeline time was 235,424.381 ms; wrapper process wall was
236,069.102 ms. Peak sampled parent memory was 98,365,440 bytes, parent working
set 153,776,128 bytes, and combined sample 466,759,680 bytes. Sampling is not a
hard memory ceiling. Parent preprocessing was capped at 0.4992 logical core;
child workers were independently capped, not jointly.

The run exited 0 with empty stderr. It read no sealed payloads, performed no
optimizer steps or candidate selection, wrote no images or per-case output,
and did not pass manual truth to inference. It was review-only, unapplied, and
not production approved. These fixed-set counts are not accuracy evidence or
an improvement claim. The result also confirms the owned-control limitation:
the successful Gray8 fixture does not generalize to complete real-dev
calibration.

| Run identity | SHA-256 |
| --- | --- |
| Diagnostic adapter | `e0fb91c76f22803ed17cad38c3cff8e0a01a80a428671e061fb4733d0e460d74` |
| Assignment | `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a` |
| Selected inventory | `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d` |
| Complete source identity digest | `bbe77dd52fc4a08dd2e2a039de234677bbf0e9267d6b812864c8e87e16e5931f` |
| Aggregate report | `548e9e531926a325ebfd846da857ef3effe973c1eb6f3ca226d39602bec3c624` |

The App and Rust identities are the frozen 0.4.49 hashes above. The adapter
verified the delivered runtime file pins before reading any private payload;
the OCR model and manifest hashes are listed above. No private identifiers,
pixels, or truth rows are included here. No dependency, license, model,
production approval or frozen application contract changed.

## Output retirement

After this source and outcome record is pushed and its review finishes,
`artifacts/prepared-calibration-449-owned-check` and
`artifacts/real-dev-prepared-449-check` are disposable. They contain only owned
controls and compact aggregate diagnostics. The compiled probe remains only
while an active diagnostic needs it; the retained 449 package, selected runtime
model, shared user Data and original private corpus are not disposable outputs.
