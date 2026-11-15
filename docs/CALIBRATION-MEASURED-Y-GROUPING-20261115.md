<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Measured Y-tick grouping

## Defect and scope

The [fixed development trace](AUTOMATIC-CALIBRATION-DIAGNOSIS-20261115.md) exposed
a tall merged Y-text crop. Source inspection showed that the vertical-glyph
grouper can merge separated wide connected components from different label rows
and mark the combined crop as rotated. The private output was retired after
its outcome was pushed; this batch uses source inspection and owned controls,
without another private run or label-answer tuning.

An owned 200 x 200 image with three separate wide connected components and
supplied Y ticks reproduced the defect: one rotated crop instead of three
upright regions. This is a detector test with supplied geometry, distinct from
the automatic-geometry worker check below.

## Change

`OcrMeasuredTickGrouping` has optional Y ticks and a left plot edge. The detector
prevents a vertical merge only when both components lie wholly left of the plot,
cover different uniquely supported tick lanes, have a gap exceeding their
component heights, and have no crossing ink in their shared column. Outermost
lanes extend to the image edge so an observed endpoint label can straddle the
plot edge. Absent, sparse, invalid or transformed context retains prior behavior.
Close rotated glyphs and decimal/minus fragments remain grouped.

The private OCR worker request carries optional `MeasuredYTicks`. It is omitted
when null, bounded to 128 sorted in-plot/in-image positions, copied before
scheduling, and included in structural response-echo validation. The workspace
forwards actual geometry evidence; the session validates it before decoding and
supports Y-only grouping with at least three ticks. A request containing Y ticks
requires a matching new worker. This changes no persisted project, vision-result
or model schema. Session caching remains disabled; future cache keys must bind
both tick lists.

Only the current owned `TickProposalCheck` forwards the new evidence. Historical
fixed private and Mirage comparison routes remain unchanged. No model,
threshold, license, dependency, numeric calibration rule or Apply gate changed.

## Verification

- Owned failing-before reproduction: 1 failed, expected three regions, actual one;
  96 ms test time, 22.5875497 seconds including compilation.
- The first green compile stopped on CA1861 before tests. Replacing a constant
  test array with a static fixture corrected the build issue.
- Focused OCR suite: **51 passed, 0 failed, 0 skipped**, 18.2926572 seconds including
  compilation. Coverage includes upright rows, plot-edge labels, crossing ink,
  decimal/minus fragments, rotated text, invalid/absent context and cancellation.
- Focused App suite: **15 passed, 0 failed, 0 skipped**, 878 ms test time. It covers
  wire omission/round-trip, echo mismatch, mutable arrays, pre-decode rejection,
  Y-only context, workspace forwarding and stale-result rejection.
- Independent review found no material App wire or context-binding issue.

```text
dotnet test tests/GraphReader.Ocr.Tests/GraphReader.Ocr.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter "FullyQualifiedName~MeasuredYTickGroupingTests|FullyQualifiedName~AxisLabelGroupingReproductionTests|FullyQualifiedName~OwnedDigitOrientationReproductionTests|FullyQualifiedName~GraphReader.Ocr.Tests.TextRegionDetectorTests|FullyQualifiedName~GraphReader.Ocr.Tests.CropAndMaskTests" --logger "trx;LogFileName=y-tick-after.trx" --results-directory artifacts/y-tick-owned-check/test-results
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter "FullyQualifiedName~CalibrationOcrMeasuredTicksTests|FullyQualifiedName~CalibrationOcrWorkspaceTests|FullyQualifiedName~CalibrationOcrWorkerTests"
GraphReader.CalibrationOcrProbe.exe --tick-proposal-check <owned-request.json> <current-source-App.exe> <retained-452-ocr-assets> <current-source-Rust.exe>
```

## Actual owned worker

The probe compiled in 7.6781094 seconds with zero warnings/errors directly into
the existing source-test runtime. App/OCR/Rust hashes remained unchanged during
this probe-only compile. No second dependency set, model copy or package was
created. The existing 640 x 480 owned image was read in place, with SHA-256
`c1750cab62d79c96b75310cdb5419edcb91a1e99218b248894a26b731aa43dda` unchanged.
No known plot or numeric target was supplied to inference.

The one actual CPU run completed at `2026-11-15T06:29:36.1936832Z`, exit 0,
empty stderr, in **4.2419514 seconds** process wall time / **3776.8883 ms** pipeline
time. Rust detected three X and three Y ticks. All six OCR labels matched this
owned fixture's expected values and formed six measured pairs: X 1/2/3 and
Y 10/5/0. The complete draft used no estimated positions, required review, and
was not applied. Worker CPU budget was verified at 0.4992 logical core,
BelowNormal priority, one worker and one inference session; persistent cache
was disabled. Geometry took 127.7814 ms, cold OCR 2877.717 ms (including
1274.5674 ms initialization), and pairing 48.1497 ms. One control does not
establish a representative speedup or accuracy.

| Executed identity | SHA-256 |
| --- | --- |
| Unpackaged source App DLL | `3f1d5f72c2bbfcb6e67ce5c29a645e2e70afa555445dacf162db8fd73f8474f4` |
| Source OCR DLL | `5ad517d36153ae67abfad69930f96f9fb61a4297ece82d04822990a02235a995` |
| Source Rust worker | `cfc8e59a08b4aeaad84cc4742aa8a8c2f211a8f575f0d6bcfb6beb5f5a1880fd` |
| Probe DLL | `2652cb0b61f5d5e4d4bec35285dd2696f8d32a97b183b3eb06c6b81559f50eec` |
| Worker report, 5,115 bytes | `09d86d611c06608d149adf5e140cf6744e191a95ef5e5ddace37c2acdf69c3ce` |

The existing OCR model/manifest identities remain
`7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` /
`17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a`.
No model or runtime dependency was added.

## Acceptance and retention

**PASS for the scoped source repair and owned worker checks.** Full automatic
calibration remains unapproved; there was no post-fix private/independent
evaluation, native UI run or new package. Sparse rotated lettering can still
resemble separate labels; this conservative geometric protection is not a text
classifier. Gray-axis detection remains unresolved as previously recorded.

Keep 0.4.52 as the sole verified package. After source/outcome commit
`bc513f79b09134184d5e557f473f69348ea69b07` reached `origin/main` and the run and
integration consumers finished, `artifacts/y-tick-owned-check` was retired at
`2026-11-15T06:41:02.1187261Z`: 9 files / 106,629 bytes. The directory was confirmed
absent. Reused source compiler output remains an active dependency for the next
authorized batch. Originals, current runtime assets and user Data are preserved.
