<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Source-scale OCR development, 2026-09-21

The optional, unapproved OCR candidate keeps text at the existing source-pixel
scale using white padding and overlapping 1200-pixel windows. Recognition uses
the immutable original image. The detector contract, weights and thresholds
are unchanged. Normal 1200-pixel inputs preserve the prior path exactly. The
native evidence tool now captures raw observations for this composition too.

On the same 21 open sources, matched labels improve from 324 to 345 of 453,
exact text from 275 to 291, and correct roles from 258 to 269. False detections
increase from 106 to 152. This mixed result fails OCR acceptance and does not
authorize promotion or a sealed read.

The broader fixture also omitted existing layout repairs used by the smaller
baseline. A separate diagnostic reuses those repairs, retaining all 453 labels,
3100 characters, scientific geometry and marker masks across 21 sources. These
are changed input pixels, not improved results on the historical fixture.
On these corrected layouts the old composition reads 301 labels exactly and
assigns 317 correct roles; source-scale reads 317 exactly and assigns 325 roles.
Source-scale still has 156 false detections and 37.23% character error. Five
labels retain six layout findings; the preparation's 15 includes intermediate
findings resolved by later placement steps. This diagnostic still fails.

Validation: 439 App tests pass; 391 OCR tests pass with 16 existing optional
skips. Native archive/memory/worker checks pass 13/30/15. The complete native
workflow and calibration safety self-test pass. All 23 baseline sources and
39 panels retain identical scientific outputs: 17 exports, six calibration
failures, 479/706 correct unique values and 474/724 correct rows. Native
inference takes 146.81 seconds. The earlier missing-native and missing-observer
attempts are retained and identified in the [evidence report](GOAL-22-SOURCE-SCALE-OCR-DEVELOPMENT.json).

Continue with direct open-development OCR diagnosis so text defects can be
investigated without rerunning markers and export. No training, private or
sealed reads, dependencies, licensing changes, model promotion or packaged
build occurred. Reviewed c96 native bytes were used. Build 433 stays 0.4.33.
Commit 0f647a8 passed CI 35574661654. OCR, marker/calibration, real acceptance
and final distribution remain incomplete; all four Goal 22 outcomes are open.
