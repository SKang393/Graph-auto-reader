<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Complete synthetic panel diagnostics

An early panel failure previously prevented diagnostics for later panels in the
same source image. The synthetic runner now attempts each unvisited later panel
once. It preserves the original source failure and does not export diagnostic
results. The failing panel is not retried; missing failure identity is recorded
without guessing. Cancellation and memory exhaustion still propagate.

The warning-free x64 build and six fictitious checker commands pass in 65.95
seconds. Ten new checks cover continuation, retained failures, missing observations,
duplicate and foreign identities, cancellation and memory exhaustion. An initial
compiler rejection of the deliberately injected memory exception is retained;
the analyzer suppression applies only to that fixture.

Actual inference with the unchanged four models and all 23 source images takes
150.72 seconds. All 39 prepared panels are now observed, up from 33. The six
additional observations include four panel failures, which remain recorded.
Every prior axis, OCR, marker and calibration observation is unchanged. All
eleven source failures remain, and every exported numeric value and CSV metric
is unchanged: 12/23 sources export, 322/706 unique values and 316/724 relational
rows are correct. Two errors now report required recalibration instead of an
invalid point, as expected from the separately tested unknown-session repair.
The initial comparison and this explanation are both retained.

Saved-output OCR scoring uses authenticated source annotations and the unchanged
one-to-one geometry/text matcher. It retains all 709 train labels, 183 development
labels and 1,019 development characters, including labels on failed sources.

| Complete development OCR output | Result |
| --- | ---: |
| Matched labels | 181 / 183 |
| Extra labels | 8 |
| Exact text | 177 / 183 (96.72%) |
| Correct roles | 174 / 183 (95.08%) |
| Character edits | 16 / 1,019 (1.57%) |

The measured geometry is the assembled recognized output. Raw detector proposals
are not recorded by this observer, so no raw-detector or sealed-gate pass is
claimed. The apparent gain over the earlier incomplete diagnostic is increased
coverage, not a model accuracy improvement. Training results remain weaker:
416/709 exact texts and 465/709 correct roles. No inference is repeated for scoring.

[Evidence](GOAL-22-COMPLETE-PANEL-DIAGNOSTICS.json) records the source, runtime,
checks and diagnostic hashes. The prior identity repair passed GitHub run
35543395235. No private/sealed read, training, new dependency, approval or package
occurs. Apache-2.0 project changes and existing reviewed payloads remain in use.
Build 433 remains 0.4.33. Continue stage-evidence integration and marker diagnosis;
all four Goal 22 outcomes remain incomplete.
