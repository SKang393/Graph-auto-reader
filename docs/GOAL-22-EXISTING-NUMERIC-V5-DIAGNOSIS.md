<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Existing numeric specialist does not transfer to current crops

Keep the current general recognizer. The already trained V5 number specialist
returns no text for any of the 73 existing open development numeric regions,
with either refined or original detector bounds. The general recognizer reads
56/73 and 59/73 respectively. These are the same regions used in the closed
[crop-width diagnostic](GOAL-22-NATIVE-CROP-PADDING-DIAGNOSIS.md); no new
private or sealed data is involved.

This uses the existing V5 weights and exact current adapter settings: 128 by
32 crops, documented padding, the 0.75 structural-height rule and the 0.65
confidence rule. There is no threshold sweep or truth-based choice of reading.
The first check builds cleanly and takes 16.15 seconds, including 2.95 seconds
of crop preparation and recognition.

A separate trace reproduces every crop hash and result. Of 146 crops, 51 are
rejected before inference because a component meets the structural-height
rule. All remaining 95 CPU calls contain a glyph assigned the rejection class;
six also fall below the confidence rule. The Python training implementation
and the actual C# adapter produce exactly identical encoded glyph tensors for
all 95 calls, with maximum difference zero. This rules out a C# feature-encoding
discrepancy in this trace. It does not establish model accuracy or full
training/runtime parity. The trace takes 16.46 seconds, including 3.62 seconds
inside the diagnostic, and builds without warnings or errors.

Inspection shows that source padding includes nearby axis strokes in some
crops. However, the model also rejects unobstructed digits, so removing that
contamination alone is not an established repair. The historical synthetic
gate does not establish transfer to the current broader input distribution.
The specialist remains unapproved and is not integrated. This comparison is
closed; do not repeat it unchanged or train another model merely because it
failed.

The [evidence index](GOAL-22-EXISTING-NUMERIC-V5-DIAGNOSIS.json) binds the exact
model, adapter, crops, logits, aggregate scoring and diagnostic sources. The
project-owned model and implementation retain Apache-2.0 licensing. No
dependency, weight, optimizer step, production option, private/sealed read,
package or release changes. Continue pixel-based marker recovery and the
remaining OCR work. All four Goal 22 outcomes remain incomplete.
