<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR repair: distinguish detached notes from phase headings

## Problem

The combined OCR trial reads detached notes correctly but labels them as phase
headings merely because they appear above the plot. That can mislead the phase
reasoner even when recognition itself is correct.

## Implementation

An opt-in layout resolver looks for a corroborated row of at least two separate
horizontal headings. It classifies a sufficiently separated note above that row
as an annotation. It uses geometry, with no word whitelist, and preserves explicit
role evidence and human-reviewed labels. Changed roles receive a review warning
and confidence capped at 0.64. Text, numeric values, original-pixel geometry, and
recognition alternatives remain unchanged. Cache identities separate this trial
from the earlier composition while reusing unchanged recognition crops.

## Files changed

- OCR header resolver, pipeline option, cache identity, and regression tests.
- Opt-in application composition, runtime evidence command, and scorer profile.
- This report, [aggregate evidence](GOAL-22-OCR-HEADER-CONTEXT.json), and readiness.

## Tests and commands

The complete OCR suite passes 297 tests with 16 existing optional skips. The
application factory/wrapper suite passes 40 tests, Python evidence checks pass
45, and the tool passes 31 self-checks. The compiler reports zero warnings and
errors. Actual application OCR completes all 37 fixed panels without failures.

Validation uses `dotnet test`, the evidence tool's
`--evaluate-header-context-candidate` command, and the scorer's `header_context`
mode. A separate Python replay verifies every changed role against the C# result,
including explicit-evidence, review-status, and corroboration safeguards. Initial
test compilation required an analyzer fix to a test fixture; the final complete
suite passes. No sealed budget is consumed.

## Metrics and timing

All 709 train labels, 183 development labels, and 1,019 development characters
remain in their original denominators. Missing and extra text still count.

| Measure | Combined trial | Header-context trial |
| --- | ---: | ---: |
| Correct development roles | 124 / 183 | 132 / 183 |
| Correct train roles | 386 / 709 | 400 / 709 |
| Exactly read development labels | 141 / 183 | 141 / 183 |
| Missing development labels | 31 | 31 |
| Extra development detections | 24 | 24 |
| Development character edits | 396 | 396 |

The audit verifies all 819 recognized regions. Only the 22 expected roles and
their confidence change, with a review warning for each. Raw detections, crops,
pixels, text, alternatives, models, manifests, and license inputs are unchanged.
Twelve runtime source bindings and four executing assemblies are authenticated.
The preliminary diagnostic suggested one additional train change; the application
safeguards exclude it, and the table reports actual application results.

Development precision is 86.36%, recall 83.06%, exact reading 77.05%, role accuracy
72.13%, and character error rate 38.86%. All five central bars fail. Application
evaluation takes 79.593 seconds, full scoring 30.992 seconds, and the integration
audit 0.388 seconds. These are observed wall times, not a controlled comparison.

## License review

No dependency, model, or native payload is added. Existing reviewed identities
remain unchanged. New code uses Apache-2.0. No private or sealed data is read.

## Known limitations

This repair does not recover missing detections or fix misread characters.
Legends, crowded synthetic labels, and peripheral titles remain unresolved.
Legitimate multi-level phase headings require real confirmation; the trial stays
unapproved and disabled by default. No production or real-data claim follows.

## Integration notes

The combined repair at `2b1c12e` passed Windows CI run `35485080685`. This is a
source-only repair: no new model revision, package, tag, release, or version
change. The retained portable remains build 433, version 0.4.33. Full Windows CI
run `35486072909` passed for source checkpoint `6006cfa`. Independent development
continues. No subagents
are used and no continuation approval is needed.

## Acceptance status

**FAIL** for Goal 22 completion. The role repair improves the development result;
the four required outcomes and the final 2.0.0 release remain incomplete.
