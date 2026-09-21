<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Aligned raster column context, 2026-09-21

The open development diagnosis found six vertically aligned graphs split into
separate figure containers. Their local crops lost outer labels even though
all six panels were detected. Standalone raster import now joins containers
whose common horizontal overlap is at least the existing 80% duplicate bar.
All observed axes are retained for panel cuts, source margins are preserved,
and the result carries a review warning. Side-by-side figures and default PDF
panelization retain their existing paths. The change does not infer values.

All 21 open sources still import to 31 panels. With the same weights and all
453 labels counted, matched regions increase from 311 to 324, exact text from
260 to 275, and correct roles from 247 to 258. Recall is 71.52%, precision
75.35%, exact text 60.71%, role accuracy 56.95% and character error rate 43.32%.
Precision and character error rate are slightly worse than the prior result;
the broader OCR gate still fails. This is an import fix, not model approval.

Validation: 430 application tests and 59 PDF tests pass, with one existing
external-renderer skip. Native archive/memory/worker checks pass 13/30/15 and
the build is warning-free. The check job takes 212.68 seconds. All 23 baseline
sources and 39 panels produce identical scientific results, including 17
exports, six calibration failures, 479/706 correct unique values and 474/724
correct rows. The complete native comparison takes 173.50 seconds including
build/preparation, using reviewed c96 source-native bytes. Exact inputs,
source/result hashes and inference timing are in the
[evidence report](GOAL-22-ALIGNED-RASTER-CONTEXT.json).

Next: verify the separate unapproved source-scale OCR adapter on all open
sources and the existing baseline. It preserves text size through padding and
overlapping windows, without training or changing detector thresholds. OCR,
marker/calibration, real acceptance and final distribution gates remain.

No dependencies, licenses, weights, private/sealed reads, promotion or package
changes. Two unused sealed reserves remain. Build 433 stays 0.4.33. Previous
checkpoint d11db97 passed CI run 35573502307. All four Goal 22 outcomes remain
incomplete. Continue development without stopping at this checkpoint.
