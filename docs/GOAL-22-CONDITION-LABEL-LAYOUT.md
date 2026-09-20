<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Keep synthetic condition captions inside their authored phases

The historical layout could place an A/B caption in a neighboring phase even
when it did not overlap other ink. Explicit authored caption-to-bar bindings
now place each caption within its own phase header. Repeated labels are never
assigned by their potentially incorrect position. This is an opt-in owned
train/dev generator repair; historical rendering and application code remain
unchanged.

All 30 relevant tests pass. The full audit reproduces all 23 previous PNGs,
moves 25 captions, repairs 21 out-of-phase glyph footprints and resolves four
additional clearance problems. There are no unresolved placements. All 706
points, scientific panel fields, marker masks, strings and degradations remain
unchanged. Thirteen training images change; all three development images are
byte-identical. The two initial pre-inference audit failures are retained with
their exceptions and repairs. They consume no sealed budget.

The exact prior runtime and model files process all 23 sources in 151.79
seconds. Every failure remains in the denominator. This measures changed
generator pixels, not improved models.

| Complete workflow metric | Previous pixels | Corrected pixels |
| --- | ---: | ---: |
| Sources that export | 14 / 23 | 13 / 23 |
| Correct unique exported points | 287 / 706 | 285 / 706 |
| Correct relational rows | 277 / 724 | 278 / 724 |
| Wrong matched phases | 5 | 3 |
| Extra unique points | 46 | 46 |

All output integrity checks pass. The unchanged development sources preserve
their outcomes and exported values. A training source newly fails export:
OCR reads a real open marker as a low-confidence G. OCR already withholds that
region's exclusion mask, but downstream integration still treats every reading
as authoritative text and removes the point. Only its baseline point remains,
so the existing empty-export safeguard correctly blocks the result. That
integration inconsistency is the next repair. Do not remove the safeguard or
exclude the failing source.

The [evidence record](GOAL-22-CONDITION-LABEL-LAYOUT.json) binds source snapshots,
the authored inventory, audit, tests, frozen inference and complete CSV scoring.
This generator profile does not automatically enter training or acceptance.
All additions are original Apache-2.0 code using existing dependencies. No
training, private/sealed read, formal model revision, activation, package, tag
or release occurs. Build 433 remains 0.4.33. All four Goal 22 outcomes remain
incomplete.
