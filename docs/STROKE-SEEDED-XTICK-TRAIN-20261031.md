<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Stroke-seeded XTick training crops

The production OCR recovery currently associates detected text components
with axis strokes. It does not generate a crop from each stroke. This
train-only experiment defines a new 48-by-48 source-window proposal and
records its reversible mapping to the original panel and model canvas.

The verified V6 materialization rendered 24 procedural source groups and
681 crops: 235 complete one-label positives, 409 text-free backgrounds and
37 excluded crops containing other text. The verifier checked all 1,362 image
and annotation files. Only rows marked `trainable_single_box` may be used for
the one-box model. The generator uses project-local Noto fonts and preserves
source group identities for future separation. It applies the renderer's
recorded affine warp to nested source-frame annotations, rejecting non-affine
cases. No model training or production activation was performed.

- Manifest SHA-256: `12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a`
- Generator SHA-256: `98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d`
- Focused tests: 8 passed, including warp, crop mapping and target admission.

Earlier attempted outputs are superseded and must not be consumed. Synthetic
crop verification alone does not demonstrate image recognition accuracy or
independent source performance.

## GPU train-fit experiment

The first model used 644 admitted V6 crops from 24 correlated synthetic
sources, skipping the 37 excluded rows. Fresh initialization and 30 epochs
produced 2,760 optimizer steps on the AMD Radeon RX 6600 XT. At score, NMS IoU
and match IoU 0.5, training-image recall was 0.9957, precision 0.1302 and
false detections averaged 2.43 per image. The highest-scoring box reached
IoU 0.5 in 155 of 235 positive images. These are training-fit results and
show substantial false positives; the model is not approved for production.

- Trainer SHA-256: `34ea86ba936af7e2f2d80acf656270acfcc524a2cb126680715adf91358b275c`
- Training report SHA-256: `68ae7bfdf505609b3b6a92d6c2db85dd8c43da579202081d6e99baffb1ba7d77`
- Checkpoint SHA-256: `82df31aa572a3920de34b6bd80f0bd9f354880175f76bb0b9916250297fb2cec`
- Training admission and geometry tests: 4 passed.

## Hard-negative train-fit experiment

P2 kept the V6 inputs, architecture, seeds and 30-epoch schedule, but used the
32 highest-scoring negative cells per image for the background loss. On the
RX 6600 XT it completed 2,760 optimizer steps. At score, NMS IoU and match IoU
0.5, precision increased from 0.1302 to 0.3687, recall decreased from 0.9957
to 0.9915, and false detections fell from 2.427 to 0.620 per training image.
The top-scoring box reached IoU 0.5 on 205 of 235 positives. The results are
limited to correlated training crops and are not production acceptance.

- Trainer SHA-256: `dfa9fc50455c9102198084062a4a0f391a4a2b674697a26a87abeb2bb30ab9d6`
- P2 report SHA-256: `cc013b5eda70af9681dbd87cc3e496a668cc29d0dcfba8d0841250782bdcb615`
- P2 checkpoint SHA-256: `9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351`
- P1/P2 admission and loss tests: 6 passed.

## Source-disjoint procedural probe

Frozen P2 inference used 323 admitted crops from 16 new source groups.
Source IDs, identities, full-panel pixels and retained crop-image hashes did
not overlap the V6 train corpus. At score, NMS IoU and match IoU 0.5, recall
was 0.9273, precision 0.2898 and false detections averaged 1.161 per image.
The highest-scoring box reached IoU 0.5 for 133 of 165 positive rows.

The probe also excludes four unsupported perspective-warp recipes, 36 crops
with text confusers and 63 background crops whose image hashes matched train
images. The 323 scored rows include 88 within-probe image repeats. These
choices can bias the background error rate; the result is a synthetic
development diagnostic, not formal product acceptance or a real-graph test.

- Probe manifest SHA-256: `127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba`
- Frozen P2 checkpoint SHA-256: `9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351`
- Corrected evaluation SHA-256: `97932d6f2b12debdd009014d2cbcfd5ff63db8754ccc664079df2551d7242340`
- Probe tests: 3 passed; inference used the RX 6600 XT with no optimizer steps.

## One-box-per-seed analysis

An RX 6600 XT inference-only analysis compared the frozen P2 model's full NMS
output with retaining the top-scoring box per unambiguous seed. At score and
match IoU 0.5, probe precision rose from 0.2898 to 0.8160, while recall fell
from 0.9273 to 0.8061. The count of false positives fell from 375 to 30, but
20 previously matched positive rows were lost. Excluded crops stayed out of
scoring. The consumed probe's repetition and background exclusions above
still limit interpretation; this does not establish production readiness.

- Analyzer SHA-256: `732650575a1878e380387c4a066b6e3c5b80755d61293e5f1aafd711bed8254d`
- Analysis report SHA-256: `66ba6032cdd9ef66fb14ac8b810bbb3de5e79422dbf9eaca04b096e8cacab970`
- Top-one scoring tests: 4 passed; no optimizer steps ran.
