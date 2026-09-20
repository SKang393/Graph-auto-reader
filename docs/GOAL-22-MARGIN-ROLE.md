<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Horizontal axis titles and detached phase codes

General measurement words such as frequency, duration, percentage and count
now provide a reviewable axis-title cue when the text is horizontal, left of
the plot, and vertically aligned with it. Explicit role evidence keeps priority.
An unrecognized name or a word merely containing a measurement term stays
ambiguous. The pipeline emits a review warning for the new cue. It never uses
these words to supply calibration values.

Standalone phase codes, for example `Phase 12`, retain their heading role when
placed above the main heading row. A longer note such as `Phase 4 completed`
still follows the detached-note rule. Both role/cache versions advance; model
payloads, inputs, detector thresholds, recognized text and coordinates do not.

Actual CPU runs complete 37 original and 37 corrected-layout panels. Independent
comparison verifies every non-role output is identical to its corresponding
baseline, reproduces all baseline metrics, and retains the full denominators.

| Correct roles | Before | After |
| --- | ---: | ---: |
| Original train | 409/709 | 422/709 |
| Original development | 135/183 | 143/183 |
| Corrected-layout train | 426/709 | 441/709 |
| Corrected-layout development | 148/183 | 161/183 |

No previously correct role is lost. Exact corrected development readings remain
165/183, with 14 missing labels, 12 extra labels and 93 character edits. All five
existing accuracy comparisons still fail, and Goal 22 remains incomplete.

Validation passes 325 OCR tests with 16 existing optional skips. Tests cover
general measurement vocabulary, word boundaries, geometric constraints, stronger
role evidence, emitted review warnings, phase-code syntax and freeform notes.
Compilation takes 31.16 seconds with zero warnings or errors. Actual model runs
take 66.21 seconds on original inputs and 49.32 seconds on corrected inputs;
the comparison takes 1.27 seconds. These are observed timings, not a speed claim.

No training, private/sealed evaluation, model revision, production activation,
package, tag or release occurred. Panel and axis geometry is replayed, so this
does not establish end-to-end acceptance. The retained portable remains build
433, version 0.4.33. Axis-number recovery is the next bounded diagnostic.

Source and evidence hashes: [aggregate record](GOAL-22-MARGIN-ROLE.json).
