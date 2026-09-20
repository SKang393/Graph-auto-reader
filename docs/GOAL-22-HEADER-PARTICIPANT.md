<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Participant names in panel headers

The OCR pipeline now joins aligned name fragments in the left panel header as
well as beside the plot. A complete participant label in that header is treated
as participant metadata. Incomplete or ambiguous names remain reviewable.
Explicit role evidence keeps priority, nearby offset phase labels stay separate,
and cached results use new assembly and role-classifier versions.

Actual CPU OCR completes all 37 original and 37 corrected-layout panels with
the same models, images and thresholds. Raw detections are unchanged. Original
train and development metrics remain identical to their baselines.

On corrected layouts:

| Measurement | Before | After |
| --- | ---: | ---: |
| Exact development readings | 158/183 | 165/183 |
| Correct development roles | 139/183 | 148/183 |
| Extra development labels | 19 | 12 |
| Development character edits | 128 | 93 |
| Exact train readings | 423/709 | 423/709 |
| Correct train roles | 410/709 | 426/709 |

All nine development participant names have the correct role. Seven exact
readings improve, with no exact-reading or role losses on either inventory.
The full denominators remain 709 train and 183 development labels. Both previous
scores are independently reproduced before comparing the new output.

The OCR suite passes 312 tests with 16 existing optional skips. Compilation has
zero warnings and errors and takes 20.50 seconds. Original and corrected model
runs take 35.14 and 36.07 seconds. Tests cover aligned header grouping, offset
phase text, ambiguous names, repeated grouping and stronger explicit context.

All five accuracy comparisons and all four Goal 22 outcomes remain incomplete.
Remaining work includes missed axis numbers, left-margin axis-title ambiguity,
detached phase codes and other missing text. Historical panel/axis geometry is
replayed here, so this is not end-to-end acceptance. No training, private/sealed
read, new model revision, production activation or packaged build occurred.
The retained portable remains build 433, version 0.4.33.

Source and evidence hashes: [aggregate record](GOAL-22-HEADER-PARTICIPANT.json).
