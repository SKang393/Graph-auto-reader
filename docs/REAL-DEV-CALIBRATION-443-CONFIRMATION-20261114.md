<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Fixed delivered 0.4.43 confirmation

## Frozen scope before the run

Confirm the already committed `bf0d0a56946abc01e889605fbaaecabb4db5d177`
application, packaged as 0.4.43, once on all 120 frozen real-dev projects.
The source repairs were selected and verified on owned inputs before this
confirmation. This run does not choose between models, thresholds, architectures
or candidates. It performs no training, reads no sealed payloads, applies no
calibration and grants no production approval. The historical
[0.4.42 diagnostic](REAL-DEV-CALIBRATION-DIAGNOSTIC-20261114.md) remains separate.

Use the unchanged locked parser, full-original image inputs, 120 denominator,
aggregate-only output, evaluator-only manual anchors, continuous normalized
residuals and existing 45-second case / 900-second overall deadlines. Every
failure stays in the denominator. No new pass threshold or case-level report is
introduced. The existing 51 sealed project payloads remain excluded.

Only delivered runtime identity pins and the version label changed in the
diagnostic. The actual uploaded/downloaded ZIP SHA-256 is
`77ca2f1207d6430b9fe600dbe1cfe5143fee8ee5e549865391362da52198c6a8`;
all 477 extracted entries matched. All 21 supporting libraries and actual loaded
managed-library paths are checked before private payloads. The reader, manifest,
licenses and notices are unchanged.

The isolated probe-only build passed in 6.18 seconds with zero warnings/errors.
After compilation, the exact package libraries were restaged and verified.
Both owned self-tests passed with zero payload reads: two affine residual checks,
forbidden-output rejection, parent job breakaway, independent child half-core
budget and child exit. Adapter SHA-256:
`3e50dabab6f54d4d1b228520d79682789e50b5a84ae4a9e54644efc7c79cfb7f`.

The diagnostic keeps one image in memory and writes only aggregate output under
`artifacts/real-dev-calibration-443-check`, expected below 1 MB. It has no image,
dataset, prediction or model copy consumer. After the result and adapter update
are pushed, retire that scratch once its readers finish. Preserve original
user data, the current package/model and active native QA dependencies.

## Outcome

The fixed run exited 0 after 122.5673699 seconds. All 120 frozen projects were
attempted, hashed, and image-bound. Geometry and OCR returned for 36; those
produced 296 numeric readings, but zero complete calibration proposals. All
120 cases therefore remain failures or incomplete. Reason counts were
unchanged from 0.4.42. There are no scored complete proposals and no residual
summary values.

The 36 successful OCR returns included one cold read and 35 reused-session
reads. Reused OCR median was 2,704.621 ms, versus 4,132.389 ms previously.
This is not an extraction-success speed claim. The all-case median of 141.4428
ms includes 84 early geometry failures; case p95 was 4,132.3286 ms and maximum
5,999.1475 ms. Axis coverage remains the open limitation; automatic acceptance
has not been established; complete-proposal coverage remains 0/120.

The report records known dev exposure, imperfect manual truth, and unavailable
tick-transcription truth. It read zero sealed payloads, performed zero optimizer
steps, used no manual truth for inference, applied no calibration, wrote no
images, and used no persistent cache. Output is aggregate-only and the run does
not establish independent accuracy or production approval.

The parent ran with a verified 0.4992-logical-core CPU budget on 12 logical
processors and BelowNormal priority. Peak sampled parent memory was
114,688,000 bytes; peak working set was 174,649,344 bytes; peak sampled combined
memory was 499,621,888 bytes. Sampling is not a hard memory ceiling.

| Evidence | SHA-256 |
| --- | --- |
| Aggregate report | `0cc174046852f518468e4fc81de7610154661ccb877a16b6459019548100e568` |
| Run metadata | `2f64f12c6992e46d0c4aefe7031cdf1ecb94c0b8f521d798e7513a4a9a7d1f79` |
| App executable | `8c012440a7a433aff98e5df3cab33a760544b024d920f92cc326ac07d45f7778` |
| App DLL | `86bb16ae0d89c0b12230d009a657120d5430a81992f710aa881ac3d18dc5bd6c` |
| Rust worker | `4dfab71143dc8d448f8daf4cd6c537c4d98de7573b1faa3139bd8abb85c6cbe4` |
| Reader / manifest | `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` / `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a` |

## Reproduction

The exact fixed probe invocation was:

```powershell
$probe = 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\hardware-final-build\GraphReader.CalibrationOcrProbe.exe'
$build = 'E:\Graph-auto-reader\artifacts\dev-portable\builds\0.4.43-20261114T055422359Z-bf0d0a56'
& $probe --real-dev-calibration-check 'E:\Graph-auto-reader\data\manual data' (Join-Path $build 'GraphReader.App.exe') (Join-Path $build 'review-assistance\ocr') (Join-Path $build 'native\graphreader-engine.exe') --explicit-opt-in
```

After record `46b6d039` was pushed and readers finished, the output scratch at
`artifacts/real-dev-calibration-443-check` was retired: 11 files, 11,144 path
bytes. The original corpus, delivered package, model and native QA data were
preserved. Commands here describe the adapter at that historical commit.

## Geometry-only diagnosis

After the fixed confirmation, measure aggregate image/plot dimensions and
the number of measured X and Y ticks returned by the same delivered 443 worker.
Use all 120 fixed real-dev inputs, with 0/1/2/3-or-more buckets among geometry
successes and explicit overall failure denominators. Empty arrays mean the
worker could not supply unambiguous measurements, not that printed ticks are
absent. No OCR calls, manual-anchor evaluation, threshold or candidate selection
are performed. Runtime pins, locked parsing, output privacy and deadlines stay
unchanged. The expected additional aggregate output is below 1 MB in the same
scratch directory; retire it with the confirmation after its outcome is pushed.

The geometry-only extension was added after the full confirmation and does not
relabel its adapter. Its probe-only build passed in 12.11 s with zero warnings
or errors. The expanded owned self-test validated its aggregate structure and
privacy guard without reading source payloads. Exact delivered libraries were
restaged again after compilation.

The geometry-only run exited 0 in **20.956984 s** (20.2929106 s pipeline), with
3,971 bytes of aggregate JSON and empty stderr. All 120 projects were attempted,
hashed and image-bound. The combined input digest matched the full confirmation
and the earlier 442 run:
`bbe77dd52fc4a08dd2e2a039de234677bbf0e9267d6b812864c8e87e16e5931f`.
Geometry returned on 36. The other 84 were 57 not found, 25 ambiguous and two
without an axis intersection. No unsupported-dimension or parser failures were
reported. No OCR was called.

| Aggregate dimension, pixels | Count | Mean | Median | p95 | Maximum |
| --- | ---: | ---: | ---: | ---: | ---: |
| Image width | 120 | 1236.867 | 1167 | 2000 | 3200 |
| Image height | 120 | 553.133 | 461.5 | 1264 | 1639 |
| Detected plot width | 36 | 895.056 | 733 | 1855 | 1862 |
| Detected plot height | 36 | 348.639 | 289.5 | 637 | 1370 |

Among the 36 returned geometries, **23 had no usable X tick array**, while 13
had at least three X positions. **13 had no usable Y tick array**, while 23
had at least three Y positions. Neither axis returned one- or two-position
arrays. Those counts overlap and must not be added as disjoint case counts.
They establish a measurement-availability gap, not the absence of printed ticks
or correctness of the returned geometry. This is diagnostic evidence for
reproducing unsupported geometry on owned inputs, not permission to tune
thresholds or select a model on private answers.

The command used the same paths above, replacing the mode with
`--real-dev-geometry-properties-check` and retaining `--explicit-opt-in`.
Adapter SHA-256:
`a6171a14962e28f36bd9c2484407babd65bf044878d0834d1ec38f55dc353699`.
Report SHA-256:
`9f29d4529d963ffcf6c4ecef6e0d2507782d46ae02ff8239835f308b4aba78d7`.
Private/sealed protections and the scratch retirement condition above remain
unchanged.
