<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Original-input fixed-parent OCR diagnostic

## Purpose and boundary

This single run measures the retained fixed detector on the original three
open development images with current composed OCR. It is historical diagnosis,
not a product acceptance result or a comparison with the retired GPU P1.
The original images have documented layout defects. Current acceptance uses
the separately authenticated, repaired 23-source inventory described in
[the composed prerequisite](GOAL-22-COMPOSED-OCR-DEV-PREREQUISITE.md).
No optimizer, private or sealed read, candidate selection, stage admission or
production approval occurred.

The input adapter in `ml/ocr/official_bakeoff/gpu_head_evaluation.py`
authenticates the original request, preflight, manifest, source images and two
independent cached truth records. It serializes only the existing `before`
text, role and pixel box, preserving all 183 truth IDs across three sources.
Prediction and corrected-layout fields are excluded. Eleven focused tests
passed in 3.354 seconds, 10.866 seconds wall time; independent review found no
concrete defect. These tests include poisoned prediction/after fields,
inventory mismatches, altered image/metadata bytes and lossless box conversion.

## Bound run

- Source: `488aa9b2232084d4d297860de000c67c8ae3a11c`.
- Fixed detector: `d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb`.
- English recognizer: `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`.
- Reviewed native runtime: `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`.
- Request: `49cbc6741341252d80649f6b593e46e2855f061ff7ddd5242f5ed7eaad459c19`.
- Candidate descriptor: `b205889546564da899e915fc04f3c940db0ea9507f84b596c110e629136c33e4`.
- Result: `6b94b11aa68340bee9e982d6eaaf3dd2cd8546e310d3d7c8060475e46e50abc7`.
- Outcome receipt: `f40097eb3ad523420ed6e4565432695b3d917b942a21df909c5d3b294cf8c1c6`.

The clean Release tool build passed with zero warnings/errors. Its actual
tool DLL hash was `a9f0db40a502cb55aeb541ba8b19da34d3f2ae877e6e247140f5bf3e274444ca`.
Post-run checks verified all 52 runtime files, 344 tracked runtime source files
and seven model/manifest/license/native inputs unchanged. The existing licensed
model assets and reviewed OpenCV were reused; no dependency or license changed.

The run directory is under the retained historical input checkout:
`artifacts/goal22-runs/original-dev-fixed-parent-composed-20261111`.
Input preparation used `prepare-parent-composed-inputs --historical-root <root>
--output <run>/inputs` through `Run-GpuPython.ps1`. The exact inference entry
point was:

```text
dotnet <run>/runtime/GraphReader.SyntheticRuntimeEvidence.dll --check-composed-ocr-memory-dev <run>/inputs/request.json 49cbc6741341252d80649f6b593e46e2855f061ff7ddd5242f5ed7eaad459c19 <run>/candidate.json b205889546564da899e915fc04f3c940db0ea9507f84b596c110e629136c33e4
```

## Measured result

All three sources and nine panels completed. Exit code was 0; elapsed time was
19.340 seconds wall, 18.845 seconds inside the runtime. No source or panel was
omitted and no explicit recognition failure was reported.

| Measure | Raw detector | Finished composition |
| --- | ---: | ---: |
| Truth regions | 183 | 183 |
| Predictions | 208 | 206 |
| True positives | 71 | 140 |
| False positives | 137 | 66 |
| False negatives | 112 | 43 |
| Precision | 34.1346% | 67.9612% |
| Recall | 38.7978% | 76.5027% |

Finished exact text was 126/183 (68.8525%), correct roles 132/183 (72.1311%),
and character errors 419/1,019 (41.1187%). Geometry matching used the existing
0.5 IoU criterion. The aggregate-only command emitted no case predictions.
These numbers do not identify which individual errors are caused by layout.

## Engineering decision and retention

[Annotation clearance](GOAL-22-ANNOTATION-CLEARANCE.md),
[legend clearance](GOAL-22-LEGEND-CLEARANCE.md) and
[layout clearance](GOAL-22-LAYOUT-CLEARANCE-OCR.md) document changed input pixels,
not improvements on the original images. The original three images therefore
must not become a new optimizer target or substitute for canonical acceptance.
The shared authenticator pins generator report
`2891886457562d2d49fb96ef3b3b635463299b2013b1eddac03332dc10c31598`,
20 train and three development sources. That repaired fixture's historical
181/183 matches, 177/183 exact texts and 174/183 correct roles bind a different
model/runtime/input context and are not comparable improvements from this run.

Next work is one current-runtime replay of that existing canonical development
prerequisite with its fixed models and existing checker. The temporary runtime,
annotations and receipts from this diagnostic remain only until this record is
pushed and that input/runtime identity handoff finishes, then are disposable.
Original required inputs and current model assets remain retained. Regeneration
uses the adapter, the bound original truth sources and a source-matched tool
build; no image or annotation content needs to be committed.
