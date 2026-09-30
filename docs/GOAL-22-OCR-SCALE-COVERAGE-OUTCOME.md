<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# V46 scale-coverage outcome, 2026-09-30

Keep the V44 detector and the `f3480d8` application. V46 improves text-region
recall and exact readings but reduces marker and CSV coverage. P1 is closed as
`failed_dev_unconsumed`, with no private or sealed read. This is not a model
promotion or a completed Goal 22 acceptance.

## Problem and implementation

The [diagnosis](GOAL-22-OCR-SCALE-COVERAGE-DIAGNOSIS.json) found that 23 of 27
labels on two small native development panels exceeded their role's maximum
training text height after DB resizing. V44 training had 28 panels, all 1200
source pixels wide, and 709 label short sides spanning 4.267 to 15.36 model
pixels. The earlier runtime padding-removal attempt was rejected and remains
reverted.

The separate [V46 recipe](../ml/ocr/scale_coverage_db_head_v46/README.md)
preserves all 28 original training tensors, features and targets, plus all
nine historical development tensors. It adds 84 training windows after
bilinear 2x enlargement of authenticated normalized BGR pixels, before the
frozen trunk. Every original label remains complete in at least one enlarged
window. Seventy-two cut-word occurrences are recorded and ignored in the
loss without erasing overlapping full positive targets.

The exact V44 head initializes 24 fixed AdamW epochs, 112 windows per epoch,
2688 optimizer steps, learning rate 0.00003 and seed 20260930. Selection uses
the earliest minimum complete training loss, selecting epoch 24. The mean
training loss falls from 0.302372 at epoch 1 to 0.172919 at epoch 24. No
development example enters optimization or selection. The pretrained trunk
and batch-normalization buffers remain frozen.

## Verification

All 22 tests pass with zero failures or skips; the test XML records 8.530
seconds. Preparation, including checks, takes 108.007 seconds. Training and
export parity take 1634.868 seconds. The two
complete application comparisons take 415.491 seconds.
One hidden Idle job runs at a time, with 12 CPUs and the existing 80% guard.

Initializer reconstruction reproduces V44's exact ONNX bytes. The trained
graph changes only nine declared head constants. All 121 training/development
inputs pass the registered 0.0001 numerical conversion tolerance, with maximum
absolute error 1.16229057312e-05. Four inputs
exceed the stricter historical 0.00001 tolerance. There are zero changes at
the 0.3 probability cutoff. The initial stricter preflight failure, precision
probes and failed wrapper invocation remain recorded. No accuracy threshold
or historical result was changed.

The final payload SHA-256 is
`497a3527820ce0359496084e5afb1e0d3241aca79e192187f7ab737b27818b7f`.
The checkpoint SHA-256 is
`5fbc167a7cf87f9ded9f19faa22e9834a0e38cbfce931d80c6ea53ac98ea2ca3`.

## Application results

Both comparisons use identical application binaries, English reader, V27
center model, V5 classifier, thresholds, source-scale padding and source bytes.
Every failure and truth remains counted. The application keeps graph
optimization disabled, matching the baseline; numerical export parity also
reports its separate optimized CPU setting.

| Native measure | Retained V44 | V46 |
| --- | ---: | ---: |
| Text-region matches / 453 | 392 | 434 |
| Extra text regions | 108 | 127 |
| Missing text regions | 61 | 19 |
| Exact readings / 453 | 348 | 387 |
| Correct roles / 453 | 360 | 392 |
| Character error rate | 23.4839% | 12.2258% |
| Marker matches / 838 | 733 | 715 |
| Extra markers | 25 | 17 |
| Axis corners within 2 pixels / 93 | 85 | 85 |

Text-region recall reaches 95.8057%. Precision is 77.3619%; exact recognition,
character error and role accuracy also fail their unchanged shared bars.
Paired text counts retain 331 correct readings, gain 56 and lose 17. Marker
counts retain 709 matches, gain six and lose 24. The higher net text score
therefore does not establish a safe replacement for the complete workflow.

| Complete CSV measure | Retained V44 | V46 |
| --- | ---: | ---: |
| Exported sources / 23 | 15 | 13 |
| Correct unique point values / 706 | 414 | 257 |
| Correct relational rows / 724 | 409 | 252 |
| Extra points | 12 | 5 |
| Extra rows | 14 | 7 |
| Wrong-phase rows | 3 | 3 |
| Failed sources | 8 | 10 |
| Development correct values / 206 | 156 | 23 |
| Development correct rows / 224 | 156 | 23 |

All 262 emitted audit rows have valid calibration and non-null x/y. Wrong-scale,
duplicate and failed-source residual rows remain zero. A workflow exit code 1
reflects retained review-blocked sources; no source is omitted to improve the
score. The current CSV fixture contains 20 training and three development
sources, and both splits are reported separately.

The historical nine-panel, 183-label development population is not treated as
the current CSV development set: all three source hashes differ after the
earlier caption-layout repair. Its tensors participate only in the declared
export-parity check here, not a claimed directly comparable OCR accuracy run.

## Disposition and next diagnosis

Of the 24 lost native markers, 23 lack a center proposal within five pixels;
only one truth center is directly covered by a new OCR mask. These are
observed associations, not proof of a mask defect. The 127 extra text regions
comprise 72 annotations, 44 Other regions, six legend regions and five phase
headings; 26 have text masks. Trace these regions through candidate generation
and the newly failed calibrations before changing a model or runtime rule.
Do not weaken numeric review guards or repeat this closed recipe.

The [outcome](GOAL-22-OCR-SCALE-COVERAGE-OUTCOME.json) binds training, parity,
tests, application observations, scorers, failure evidence and the canonical
P1 result. The ledger retains all 93 earlier revision rows and adds this one
complete outcome. Application source and the retained V44 payload are unchanged.

Existing reviewed Apache-2.0 PaddleOCR licensing and original notices are
preserved, with an owned adaptation notice. No new dependency, private/sealed
read, production activation, packaged build or release. Generated inputs and
weights remain ignored local artifacts. Build 433 remains 0.4.33. V46 and
overall Goal 22 acceptance remain **FAIL**.
