<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Framed legend word assembly, 2026-09-21

Detected legend words outside the plot could remain separate because the
existing crop-completion path protected every other detection. The optional
legend recovery path now joins adjacent words only when original pixels
establish a closed frame, detached symbol and one continuous text row.
It preserves protected contexts, separate rows, numeric values and original
pixels. The joined crop is recognized once, remains unreviewed and carries a
review warning. Its cache identity includes the updated recovery composition.

The paired check uses exactly the same 21 corrected owned input images and
model bytes. Exact readings increase from 323 to 327 of 453; correct roles
increase from 326 to 330. Matched regions rise from 375 to 378, false detections
fall from 158 to 152, and character errors fall from 1134 to 1046 of 3100.
The prior historical layout inputs show no score change. These results still
fail OCR acceptance and do not authorize promotion or a sealed read.

All 402 OCR tests pass with 16 existing optional skips; all 439 App tests pass.
Native memory/worker/archive checks pass 50/15/13. The first test compile found
helper visibility missing from the public resolver API; that attempt performed
no inference and is retained. The repaired test/build/open/baseline job takes
306.68 seconds. The complete native workflow takes 183.67 seconds, including
153.02 seconds inference, using reviewed c96 native bytes. All 23 source images
and 39 panels retain identical scientific results: 17 exports, six calibration
failures, 479/706 correct unique values and 474/724 correct rows.

See [hash-bound evidence](GOAL-22-FRAMED-LEGEND-ASSEMBLY.json). Next: use measured
brackets to distinguish detached notes from the confirmed phase-heading row.
No private/sealed read, training, dependency/license change, model activation
or packaged build occurred. Build 433 stays 0.4.33. All four Goal 22 outcomes
remain incomplete. Continue development after this checkpoint.
