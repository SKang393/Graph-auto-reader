<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR repair: combine the existing label repairs

## Problem

The participant-label repair and the newer plot-label/pixel-bound repairs ran
in separate trial configurations. Consequently, the newer trial still read
labels such as `Participant 01` as two pieces. Their individual improvements
did not prove that combining them would work.

## Implementation

The new opt-in application configuration applies the existing participant
grouping, plot grouping, and pixel-bound refinement in that order, then reads
the resulting original-image crops. It changes no model, threshold, production
default, frozen contract, or acceptance requirement.

The evidence report preserves all raw detections and records both types of
grouping. Independent Python replay verifies the complete membership, geometry,
original pixels, and recognition/failure accounting before loading truth.
The detector wrapper now uses the same supported-composition check as the
application, preventing the two internal allowlists from disagreeing.

## Files changed

- Application candidate composition and original-input wrapper.
- Evidence command, independent scorer, and regression tests.
- This report, [aggregate evidence](GOAL-22-OCR-COMBINED-ASSEMBLY.json), and readiness.
- Previous repair report updated with its successful CI and current execution rule.

## Tests and commands

The application factory/wrapper checks pass 36 tests. Python scoring and pixel
checks pass 42 tests. The OCR suite passes 288 existing tests with 16 existing
optional skips; the new combined-path test passes after correcting its crop-order
assumption. The evidence tool passes 30 self-checks. The final compiler build
has zero warnings and errors. Actual OCR completes all 37 panels with no
recognition failures.

Validation used `dotnet test`, the evidence tool's
`--evaluate-combined-assembly-candidate` command, and the independent scorer's
`combined_assembly` mode. The first application trial exposed a missing internal
allowlist entry before OCR. That void result and its runtime are retained; the
repaired trial uses a separate output directory. A scoring invocation made
before its input report existed also failed before scoring and was rerun after
application completion. Neither failure consumed sealed budget.

## Metrics and timing

All 709 train labels, 183 development labels, and 1,019 development characters
remain in their original denominators. Missing and extra text still count.

| Development measure | Pixel-bound trial | Combined trial |
| --- | ---: | ---: |
| Correctly located labels | 152 | 152 |
| Missing labels | 31 | 31 |
| Extra detections | 32 | 24 |
| Exactly read labels | 135 | 141 |
| Character edits | 436 | 396 |
| Correct text roles | 118 | 124 |

The six newly exact labels are participant labels; no exact labels are lost.
All 811 unchanged crops retain the same text and role. Train metrics remain
unchanged. The integration audit verifies 11 source bindings, four assemblies,
original pixel identities, raw detections, and complete raw-region membership.

Detection precision is 86.36%, recall 83.06%, exact reading 77.05%, role
accuracy 67.76%, and character error rate 38.86%. All five central bars fail.
The application run takes 73.260 seconds, scoring 29.287 seconds, and the
integration audit 18.200 seconds. These are observed wall times, not a
controlled speed comparison. No training runs.

## License review

No dependency, model, or native payload is added. Existing reviewed model,
runtime, manifest, and license identities remain unchanged. Project changes
use Apache-2.0. No private or sealed data is read.

## Known limitations

Combining existing repairs does not recover missing proposals. Legend and arrow
context, crowded labels, and ambiguous peripheral text remain unresolved.
Exploratory header grouping with both broad and narrow spacing rules lost train
matches and was rejected. It is not included in the application trial.

## Integration notes

This is a source-only repair, with no model revision, production activation,
package, tag, release, or version change. The retained portable remains build
433, version 0.4.33. Windows CI run `35485080685` passed for source checkpoint
`2b1c12e`.
Work continues while checks run; no further continuation permission is needed.
No subagents are used.

## Acceptance status

**FAIL** for Goal 22 completion. The combined development path improves reading,
but the four Goal 22 outcomes and the final 2.0.0 release are still incomplete.
