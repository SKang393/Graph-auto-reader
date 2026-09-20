<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve OCR's decision about which text may suppress markers

OCR already withholds exclusion masks for weak or graph-like readings. The
application recreated those masks from every reading, then removed classified
markers inside every text polygon. This erased a real open marker read as G
with recognition confidence 0.31. Saved observations contain 20 such exclusions
without a corresponding OCR mask, all in the owned training inventory.

The detector mask and final text exclusion now use OCR's existing mask-to-region
bindings. Final exclusion still uses the original text polygon, without padded
bounding-box expansion. Missing eligibility retains the reading and marker with
an overlap warning for Review. Rejected or empty readings cannot suppress
markers; duplicate, orphaned or non-original mask bindings fail with a structured
error. Model probabilities, operating thresholds and calibration rules remain
unchanged. Mask and exclusion identities advance so cached behavior is distinct.

All 400 application tests pass, including credible-text rejection, uncertain
marker retention through calibration and export, both mask-composition routes,
rotated polygons and invalid bindings. The Windows x64 tool build has zero
warnings/errors. Both fictitious runner and workflow/evaluator checks pass.
A launch that ended before testing is retained as a void attempt; its original
cause was not captured. The replacement helper captures startup output.

The complete workflow uses all 23 unchanged PNGs and model weights, taking
148.71 seconds. Every source and all 706 points remain scored. All 30 previously
observed panels are retained, with identical axis and OCR observations. Accepted
markers and calibration change in five panels.

| Complete workflow metric | Before | After |
| --- | ---: | ---: |
| Sources that export | 13 / 23 | 13 / 23 |
| Correct unique exported points | 285 / 706 | 230 / 706 |
| Correct relational rows | 278 / 724 | 222 / 724 |
| Wrong matched phases | 3 | 3 |
| Extra unique points | 46 | 46 |

The diagnosed two-point graph now exports, with its uncertain reading retained
for review. Another 57-point graph now fails export: the classifier accepts two
blurred text fragments as squares. Their session values are unknown, and the
existing `INVALID_POINT` safeguard correctly blocks export. No previously
accepted markers are lost in that graph's three panels. The original classifier
reports artifact probabilities near zero and 0.403 for the two fragments, so
its weak context discrimination remains a real problem. This is a verified
evidence-handoff repair with a measured workflow regression, not an accuracy or
acceptance pass. Do not hide the regression, invent session values, or remove
the export safeguard.

Next: repair classifier coverage using the verified original-raster preparation,
authentic graph context and separately defined train/dev data. Preserve the
failed workflow and every source when evaluating the resulting model. Remaining
detector misses, OCR and calibration failures also remain open.

The [evidence record](GOAL-22-OCR-MASK-HANDOFF.json) binds source snapshots,
tests, runtime, models, inputs and complete CSV scoring. All changes are original
Apache-2.0 code with existing dependencies. No training, private/sealed read,
formal model revision, production activation, package, tag or release occurs.
Build 433 remains 0.4.33. All four Goal 22 outcomes remain incomplete.
