<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Printed phase codes and plural headings

The [frozen diagnosis](GOAL-22-PHASE-ROLE-VOCABULARY-DIAGNOSIS.json) identifies
correct literal M, G and Interventions readings routed to Other even though
the phase reasoner already supports them. The role classifier now accepts
compact A/B/M/G codes with optional ASCII digits, plus plural interventions
and treatments. New code forms require width no greater than text height
times character count plus one. Existing context, word boundaries and geometry
precedence remain. The classifier version enters the workflow cache identity.
The classifier preserves its input reading. Phase reasoning, OCR weights and
numeric-value logic are unchanged.

## Complete comparison

| Configuration | Correct roles | Exact OCR | Matched text geometry | Extra text regions | Character errors | Native markers | Extra markers |
| --- | --- | --- | --- | --- | --- | --- | --- |
| V44/English | 376 -> 379/453 | 358/453 | 403/453 | 54 | 485 | 741/838 | 27 |
| V46/server | 420 -> 423/453 | 427/453 | 444 -> 443/453 | 49 -> 47 | 123 -> 120 | 745/838 | 21 |

Every earlier exact text reading and matched marker truth is retained. CSV
results remain 15 V44 exports with 414/706 correct values and 409/724 correct
rows, and 16 V46 exports with 429 values and 416 rows. All 866 exported audit
rows are identical apart from provenance identities, including confidence.
They retain finite original-pixel traces and valid calibration. Three V44 and
eleven V46 wrong-phase rows remain; eight V44 and seven V46 cases still fail.

**The role-only preservation hypothesis failed.** In V44, corrected G becomes
eligible for its existing text mask. In V46, a recovered heading retains its
literal text and geometry with changed fragment provenance. Another recovered
word merges four ambiguous fragments, including the neighboring misread Probe
annotation, into one still-incorrect Interventions reading. Matched text geometry
falls by one. One intermediate decoded point moves about 0.106 pixels; geometry
filtering and every later marker stage retain identical physical results.
Two V46 crop counts change by minus one and plus one. No such changes occur
in either CSV comparison.

The original preservation audit and first complete-delta audit both failed;
their scripts and measured differences remain bound in the
[complete outcome](GOAL-22-PHASE-ROLE-VOCABULARY-REPAIR.json). The final audit
records every role, recovery, mask, crop, warning and intermediate-point change.
It verifies unchanged accepted markers, axis geometry, calibration and CSV
rows. It does not turn the text-geometry retention failure into a pass.

## Verification and remaining work

717 OCR tests and 578 App tests pass, with 16 optional OCR skips. Twenty-one
new cases cover plural boundaries, compact codes, wide detector boxes,
damaged text, annotation/legend geometry and explicit roles. Three clean
builds plus tests took 116.897 seconds. The four CPU
workflow comparisons took 1045.757 seconds.
Source snapshots, commands, model checksums and complete results are bound
in the JSON. No new dependency, model, font or license term was introduced;
source and tests retain Apache-2.0 headers.

Keep V44/English/V27/V5. V46/server stays closed. The preceding caption/arrow
repair's one lost native marker per arm remains unresolved, as does the V46
word-recovery merge reported here. **Goal 22 acceptance: FAIL.** Shared OCR,
marker and export bars, real acceptance and final release work remain.
There were no private/sealed reads, optimizer steps, production activation,
packaged build, tag or release. Build 433 stays 0.4.33.
