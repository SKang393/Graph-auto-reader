<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Explicit participant metadata, September 20, 2026

The legend stage discarded explicitly recognized participant labels outside
the right edge. In the frozen synthetic workflow, this replaced the correctly
read participant with an unrelated annotation in export filenames.

Eligible explicit participant labels now take precedence throughout the panel.
The previous right-edge inference remains available only when there is no
eligible explicit label. Rejected text, insufficient confidence, legend text
and callouts remain excluded. Original wording and review state are preserved.
The legend algorithm version advances from 0.1.1 to 0.1.2; schemas are unchanged.

Three independent fixtures failed before the repair. The completed verification
passes all 47 legend tests and 373 application tests, with no skips. The Windows
x64 diagnostic tool builds with zero warnings/errors; its fictitious private
runner self-test passes without accessing the private corpus.

Actual inference on the unchanged 20-training/3-development inventory takes
163.70 seconds. Thirty observed panels preserve identical axis, OCR, marker and
calibration evidence. Participant filenames improve in 34 of 35 exports.
Every previous scientific row, all 272 exported point locations and all series
memberships remain unchanged. Eleven sources complete and twelve fail. Scores
remain 226/706 correct unique values and 218/724 correct relational rows.

The first byte-equality audit correctly failed: two rows with the same session
number reversed order in one CSV. The existing exporter breaks ties using
random point IDs. The subsequent comparison records this difference and verifies
identical row multisets and equal phase/session tie keys. It does not claim
byte-identical output. Deterministic tie ordering and series fragmentation
remain follow-up defects.

The [machine-readable evidence](GOAL-22-PARTICIPANT-HANDOFF-INTEGRATION.json)
binds the tested sources, frozen runtime, inputs, results, prior failure and
comparison. No training, private/sealed read, new model revision, activation,
package or release occurred. Build 433 remains 0.4.33. All four Goal 22 outcomes
remain incomplete; this is a verified metadata repair, not product acceptance.
