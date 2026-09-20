<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Read split heading words together

The opt-in word assembler now joins aligned header fragments separated by a
word-sized gap, within one measured phase. It rejects overlapping boxes, wider
gaps, participant-margin crossings and phase-boundary crossings. Joined crops
are recognized from original pixels. General alternating-treatment and withdrawal
terms receive heading roles only with heading geometry; explicit context wins.
Non-heading text centered inside the plot now retains its annotation role near
the top border. Assembly and role-cache versions advance.

Both unchanged 37-panel synthetic inventories complete through actual CPU OCR:

| Corrected development measure | Before | After |
| --- | ---: | ---: |
| Exactly read labels | 172/183 | 173/183 |
| Correct roles | 168/183 | 170/183 |
| Missing labels | 7 | 6 |
| Extra labels | 12 | 10 |
| Character edits | 86 | 43 |

Character error reaches 4.22% and recall 96.72%, meeting those two Tier 1 bars.
Precision 94.65%, exact reading 94.54% and role accuracy 92.90% remain below
their bars. Original development gains two exact readings and one correct role.
Train gains eight correct original-input roles and seven corrected-input roles.
No previously correct text or role is lost.

One regression is retained: two unreadable neighboring labels merge in a heavily
distorted training source. This adds two misses, one false region and 19 character
edits to corrected training results. Training exact reading remains 429/709.
The tradeoff is visible in the full metrics; no failed source is removed.

Validation passes 341 OCR tests with 16 existing optional skips and 52 evaluator
checks. Compilation has no warnings or errors and takes 23.87 seconds. Original
and corrected comparisons take 46.20 and 49.20 seconds; scoring takes 1.67 seconds.
All raw model detections, image inputs, weights and thresholds are unchanged.

The parallel print-quality investigation was reconciled with completed work.
The corner-sampling probe exercised the frozen legacy renderer. Its corrected
V2/V3 replacement already exists and is used by these inputs; both tested pixel
alignments retain ink there. Do not repeat that repair. Severe morphology still
has the counter-closure/readability limitations recorded in the earlier
[generator outcome](GOAL-22-VISIBLE-CONTENT-GENERATOR-OUTCOME.json).

No training, private/sealed read, new model revision, dependency, production
activation, package, tag or release occurred. Geometry is replayed, so this is
not end-to-end acceptance. Build 433 remains version 0.4.33, and all four Goal 22
outcomes remain incomplete. Missing isolated heading glyphs and incomplete text
crops remain the next OCR defects to diagnose.

Source and evidence hashes: [aggregate record](GOAL-22-HEADER-WORDS.json).
