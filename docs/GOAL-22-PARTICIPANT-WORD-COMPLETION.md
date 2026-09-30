<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Complete clipped peripheral word crops, 2026-09-30

## Problem

An owned dev image contains a word at x=25..153, but its detector crop covers
only x=31..88. Original connected components confirm the missing prefix,
suffix and detached dots. The prior refiner only tightened boxes, and heading
suffix recovery did not cover this peripheral lane. The saved 31-panel
component probe took 35.685 seconds without model inference.

## Implementation

Complete horizontal, unreviewed, nonnumeric Participant/Other words above the
plot's left edge using adjacent original-pixel components. Permit an edge
glyph to overlap the clipped crop. Preserve the existing component size and
overlap envelope, reject detached words, graph strokes and other detected
labels, and require meaningful horizontal growth. Include detached dots only
inside the completed word. No expected text or participant whitelist is used.

Recognize new crops in a separate batch, preserving earlier crop tensors.
Retain conflicting readings for review. Remove only redundant contained text
under the existing fragment rule. Human review decisions and original pixels
remain unchanged. A numeric word misread stays Other and cannot become an axis
tick; a pipeline test exercises the geometry that would otherwise classify it
as YTick. Advance the result-cache identity.

## Files and validation

- `src/GraphReader.Ocr/ParticipantWordTextRegionRecovery.cs`
- `src/GraphReader.Ocr/OcrPipeline.cs`
- `src/GraphReader.Ocr/OcrResultCache.cs`
- `tests/GraphReader.Ocr.Tests/ParticipantWordTextRegionRecoveryTests.cs`
- This report, its JSON evidence and `docs/1.0-READINESS.md`.

`artifacts/goal22-tools/Run-ParticipantWordChecks.ps1` invokes guarded Release
builds and `dotnet test --no-build --no-restore` for OCR and App, then builds
`tools/GraphReader.RealAcceptance.Ocr/GraphReader.RealAcceptance.Ocr.csproj`.
All 535 OCR and 510 App tests pass, including 16 new recovery cases. The 16
existing optional OCR skips remain. All three builds have zero warnings and
errors. Final checks take 174.343 seconds.

`Run-ParticipantWordEvaluation.ps1` runs both frozen V27/V5 native and complete
CSV workflows serially, then the unchanged scoring scripts. Final evidence is
`artifacts/goal22-runs/participant-word-evaluation-v2/repair-summary.json` and
the [committed evidence](GOAL-22-PARTICIPANT-WORD-COMPLETION.json). Sources,
managed/native runtime bytes, model payloads, input manifests, protocols and
reports are checksum-bound. Every observed model provider is CPU.
The workflow harness returns exit 1 for the retained review-blocked cases;
the complete reports and unchanged scorers still cover every source. These
are diagnostic comparisons, not claims that every graph exported successfully.

Checks-v1 failed CS1519 at the new method declaration because of an extra
generic closing token. It stopped before tests or model evaluation; its source
and logs remain. Checks-v2 and evaluation-v1 passed before the numeric-role
guard was added. The final guarded version was rebuilt and fully evaluated.

## Measured result

| Native OCR measure | Before | After |
| --- | ---: | ---: |
| Matched regions / 453 | 389 | 392 |
| Extra regions | 110 | 108 |
| Missing regions | 64 | 61 |
| Exact readings / 453 | 348 | 348 |
| Correct roles / 453 | 358 | 360 |
| Character error rate | 24.9677% | 23.4839% |

All 348 previously exact readings and 733/838 marker matches are retained.
Marker extras stay 25, and axis corners stay 85/93. Four completed crops add
two Participant and two Other readings; three contained fragments disappear.
One already-rejected near-axis marker proposal changes with the nearby OCR
mask. Every accepted marker is unchanged. All 431 prior tick recognition
records, their review warnings and all 70 calibration records are unchanged.

All 15/23 successful CSV exports and every numeric value, phase and row count
remain unchanged: 414/706 correct values, 409/724 correct rows, 12 extra points,
14 extra rows and three wrong-phase rows. Eight sources still fail. All 426
actual audit rows have valid calibration and non-null x/y. Dev remains
156/206 correct values. No wrong-scale, duplicate or failed-source residual
rows are scored. All failed truths remain counted.

Final native processing takes 192.065 seconds and CSV processing
304.966 seconds; the full sequence with scoring takes
519.574 seconds. These are observed wall times,
not a controlled speed comparison. Execution uses one hidden Idle job at a
time, 12 CPUs and the existing shared 80% CPU guard.

## Limitations and acceptance

The recognizer still confuses `01` with `O1` in completed labels. Exact text
does not improve, and 61 regions remain missing. Recovery requires an existing
word anchor in the supported lane; it does not recover entirely undetected
words or infer participant names. Shared 95% per-item bars and calibration
safeguards remain unchanged. This is a verified crop repair, not OCR or Goal 22
acceptance. Keep V27/V5 and continue diagnosis.

Apache-2.0 project source only; no new dependency or model. No private/sealed
read, training, production activation, packaged build or release. Build 433
remains 0.4.33. Overall acceptance: **FAIL, incomplete**.
