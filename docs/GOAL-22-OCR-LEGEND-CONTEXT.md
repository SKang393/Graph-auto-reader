<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR repair: use visible legend frames and symbols

## Problem

Inside-plot legend labels default to annotations when their detections have no
legend context. Later workflow code searches for symbols only beside text already
classified as a legend, leaving a circular dependency.

## Implementation

The opt-in OCR trial supplies context from original image pixels after recognition:
four enclosing frame edges plus one separate, aligned compact symbol beside the
text. It preserves explicit hints, numeric roles, and human-reviewed text. Changes
carry a review warning and confidence capped at 0.70. No text or box is changed.
The pre-OCR structural provider and production defaults are unchanged.

## Files changed

- OCR frame/symbol resolver, pipeline option, cache identity, and safety tests.
- Application trial composition, evidence command, scorer profile, and tests.
- This report, [aggregate evidence](GOAL-22-OCR-LEGEND-CONTEXT.json), and readiness.

## Tests and commands

The OCR suite passes 307 tests with 16 existing optional skips. The app factory
suite passes 43 tests, Python checks pass 48, and tool self-checks pass 32.
Compilation has zero warnings and errors. The actual
`--evaluate-legend-context-candidate` run completes all 37 panels without failures.
The `legend_context` scorer retains all original denominators. Independent Python
pixel replay verifies every role change against actual C# output.

The first audit incorrectly checked every raw word's orientation in merged labels.
The corrected audit checks the first member's orientation, as the assembler does.
Its earlier source is retained; inference and scoring were not repeated.

## Metrics and timing

| Measure | Header trial | Legend trial |
| --- | ---: | ---: |
| Correct development roles | 132 / 183 | 134 / 183 |
| Correct train roles | 400 / 709 | 409 / 709 |
| Exactly read development labels | 141 / 183 | 141 / 183 |
| Missing / extra development labels | 31 / 24 | 31 / 24 |
| Development character edits | 396 | 396 |

All 819 recognized texts, alternatives, boxes, pixels, and non-role metrics remain
unchanged. Nine train roles and three dev roles change. One dev change remains an
unmatched prediction and receives no accuracy credit. The audit authenticates 13
runtime sources and four assemblies. Development role accuracy is 73.22%; all five
central OCR bars still fail. Application evaluation takes 62.335 seconds, scoring
27.725 seconds, and the audit 9.710 seconds. These are observed wall times.

## License review

No dependency, font, model, or native payload is added. Reviewed identities remain
unchanged; new code uses Apache-2.0. No private or sealed data is read.

## Known limitations

Unframed or occluded legends, misread text, and missing detections remain unresolved.
At this checkpoint the full workflow still derived legend glyph candidates from
marker-center output. The subsequent [symbol-to-series repair](GOAL-22-LEGEND-SYMBOL-SERIES.md)
connects independent crops in controlled workflow tests. This historical OCR result
does not claim full series-assignment or export accuracy.

## Integration notes

Header repair `6006cfa` passed Windows CI run `35486072909`. This source-only change
opens no model revision, approval, package, tag, or release. The retained portable
is still build 433, version 0.4.33. CI follows this push while development continues.
Windows CI run `35486779845` subsequently passed for this repair at `d9e6c2e`.
No subagents or further continuation permission are needed.

## Acceptance status

**FAIL** for Goal 22 completion. All four required outcomes remain to be verified.
