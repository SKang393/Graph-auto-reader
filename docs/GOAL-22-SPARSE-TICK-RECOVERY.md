<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Goal 22: recover labels on sparsely labeled axes

The OCR fallback required three readable labels before proposing a missing
label. A graph with only two printed labels could never satisfy that rule.
The repaired fallback reads the missing **1** from original pixels in the
diagnosed synthetic graph. It preserves all 700 earlier text readings and
every exported scientific row. Goal 22 remains incomplete.

## Implementation

The existing three-label lane remains unchanged. With one or two literal
original-image anchors, a separate path requires each anchor and candidate
crop to have a unique outward tick stroke connected to the measured plot
axis. Candidate height, alignment and label-to-tick offset must fit the
observed lane. A missing, detached, shared or ambiguous anchor tick cannot
support recovery. Existing detections are not replaced.

Only the image crop reaches the recognizer. The code supplies no character,
number, sequence or numeric-role hint. New text retains original-image
provenance and unreviewed status. Numeric calibration and session-origin
safeguards remain unchanged.

The recovery cache component advances to
`original-pixel-tick-lane-recovery-v2`. The frozen candidate composition
advances to `original-db-head-tick-header-and-legend-lanes-v4`, binding the
changed behavior without changing model weights or opening a model revision.

## Verification

Two independent x/y recovery cases failed before the repair. Both then pass,
together with the negative stroke, anchor and immutable-pixel checks.

| Check | Result |
| --- | --- |
| OCR tests | 380 passed; 16 existing optional skips |
| Application tests | 373 passed; no skips |
| Windows x64 runner build | Zero warnings/errors |
| Fictitious private-runner self-test | Passed; no corpus access |
| Source snapshots | All 265 verified against the frozen run |
| Actual 23-source workflow | 162.38 seconds |

The same fixed workflow visits the same 30 panels. All raw axis results,
accepted markers and 700 previous readings remain identical. Exactly one new
reading appears: original-pixel **1**, correctly classified as an x tick.

The affected graph previously failed with an ambiguous session pitch. With
both labels read, its first accepted marker maps to session 2. It now fails
the explicit session-origin safeguard. The missed first marker remains a
separate detection/classification defect, not a reason to invent its position.

Completed sources remain **11**, failed sources **12**. All 272 exported point
locations and scientific audit rows are preserved. Correct unique values remain
**226 / 706**, and correct relational rows **218 / 724**. There are still 480
missing truth points, 46 extra predictions and five wrong phase rows. No new
accuracy or production acceptance is claimed.

Commands: `dotnet test` for the OCR and App test projects, Release configuration
with isolated outputs; `dotnet build` for `GraphReader.RealAcceptance.Ocr`
with `-r win-x64`; its `--self-test-real-workflow-runner`,
`--run-frozen-candidate-synthetic` and `--score-frozen-workflow-csv` modes.
The [evidence record](GOAL-22-SPARSE-TICK-RECOVERY.json) binds exact sources,
snapshots, tests, invocation definition, model composition, reports and metrics.

Two helper launches initially referenced a missing x64 output after a build
without an explicit runtime identifier. Their failed logs remain preserved.
An explicit x64 build and the documented self-test flag fix that launch path.
The completed OCR/App suites were not repeated. No private or sealed data was
read by the failed launches.

## Remaining work

Trace the missing first marker using frozen model proposals and classifier
evidence. Preserve the origin gate and the rejected tighter-crop experiment.
Faint, rotated or tickless axes remain outside this sparse fallback's support.
Further OCR, marker and end-to-end acceptance work is required.

All new code is Apache-2.0, with no new dependency or external asset. No training,
private/sealed read, model revision, production activation or package occurred.
Build 433 remains 0.4.33; all four Goal 22 outcomes remain incomplete.
