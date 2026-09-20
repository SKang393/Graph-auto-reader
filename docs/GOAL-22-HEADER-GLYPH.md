<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recover isolated heading glyphs from original pixels

The opt-in OCR candidate now finds missed isolated glyphs along an established
heading row. At least three aligned headings corroborate that row. Existing
detections, adjacent word suffixes, plot markers and incompatible geometry are
excluded. The existing recognizer reads each original-pixel crop without a
supplied character or phase code. Every new reading remains unreviewed and
carries a review warning. Cache and candidate composition versions advance.

Actual CPU OCR completes both unchanged 37-panel synthetic inventories:

| Corrected development measure | Before | After |
| --- | ---: | ---: |
| Exactly read labels | 173/183 | 177/183 |
| Correct roles | 170/183 | 174/183 |
| Missing labels | 6 | 2 |
| Extra labels | 10 | 10 |
| Character edits | 43 | 39 |

All five proposed crops, one training and four development, are read correctly.
All 829 original-input and 850 corrected-input baseline readings remain exactly
unchanged. Raw model detections, images, weights and thresholds are unchanged.
Original-input metrics are unchanged. Corrected training exact reading rises
429 to 430 of 709 and roles 454 to 455.

Full-output precision remains 94.76%, below its 95% bar. Recall is 98.91%, exact
reading 96.72%, character error 3.83% and role accuracy 95.08%. Role accuracy is
only just above its bar. These small development results do not authorize a
sealed evaluation or production activation. Distorted training failures and
the previous header-merge regression remain included.

Validation passes 346 OCR tests with 16 existing optional skips, 74 application
tests and 52 evaluator checks. Compilation has no warnings or errors and takes
14.68 seconds. Original and corrected comparisons take 64.55 and 48.81 seconds;
full scoring takes 1.37 seconds. Source snapshots and executing assembly hashes
bind the evidence to the measured implementation.

No training, private/sealed read, new model revision, dependency, production
activation, package, tag or release occurred. Axis and panel geometry are
replayed, so this is not end-to-end acceptance. Build 433 remains version
0.4.33. All four Goal 22 outcomes remain incomplete. Incomplete text crops and
extra detections are the next OCR defects to address.

Source and evidence hashes: [aggregate record](GOAL-22-HEADER-GLYPH.json).
