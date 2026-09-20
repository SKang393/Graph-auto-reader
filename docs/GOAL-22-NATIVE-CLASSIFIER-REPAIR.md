<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native-image classifier repair

The first bounded adaptation substantially improves the owned synthetic
development result, but **fails the required shape-accuracy bar**. It is an
unapproved candidate. Existing production approval is unchanged.

| Measure on the same development examples | Existing classifier | Adapted classifier |
| --- | ---: | ---: |
| Correct shape | 1,298 / 2,880 (45.1%) | 2,453 / 2,880 (85.2%) |
| Correct fill, including explicit unknown | 2,210 / 2,880 (76.7%) | 2,773 / 2,880 (96.3%) |
| Real markers retained | 2,106 / 2,880 (73.1%) | 2,878 / 2,880 (99.9%) |
| Non-marker examples incorrectly retained | 65 / 256 | 1 / 256 |

The required bars come from [the shared acceptance policy](../ml/policy/acceptance-bars.json).
Patch accuracy does not establish point detection, grouping, calibration or
export correctness. Private and sealed data have not been read.

## What changed

The existing owned classifier is fine-tuned on 7,840 newly authored training
examples. Each is rendered as an original image and sampled through the verified
application crop calculation. The 3,136 evaluation examples use a separate
synthetic recipe family, font, raster scale, blur and layouts. Both splits
cover all nine shapes and authentic line, neighbor and non-marker context.
The drawing primitives remain shared; this is not independent real evidence.

Every example is retained. A pretraining audit found 28 training examples whose
intended answers differed despite identical pixels: eight shape examples and
twenty fill examples. Shape training uses equal weights for the indistinguishable
alternatives; fill uses the existing unknown category. Authored labels remain
recorded and shape accuracy still counts each authored answer. No evaluation
example required this reconciliation. There is no exact cross-split tensor
overlap and no conflicting training target after reconciliation. The initial
invalid cache is retained as diagnostic evidence.

The run uses the authenticated existing checkpoint, unchanged architecture,
probability output, crop and operating threshold. It performs 40 fixed epochs
and 2,480 optimizer steps, then evaluates the final checkpoint without selecting
an epoch from development results. The declared adaptation includes balanced
artifact loss and consistent ambiguity targets. It is not evidence that one
individual change caused the improvement.

## Verification and CPU use

The Python preparation, metrics, admission and CPU checks pass 78 tests. The
application route passes 404 tests; the Windows x64 checker builds with zero
warnings/errors and passes both fictitious workflow checks. An initial test
compile failure from a missing fixture helper and constant-array analyzer
requirements was repaired; its original logs are retained.

Training, export and evaluation take 321.36 seconds. All twelve logical
processors remain eligible, with Idle priority, the 80% Windows job backstop,
passive worker waits and cooperative work/rest cycles. The largest measured
completed-cycle active fraction is 79.982%. This measures the workload's
work/rest cycle average, not instantaneous utilization of each core.

ONNX matches the trained framework output over all 10,976 patches. Maximum
absolute error is 0.000004232 against the existing 0.00001 tolerance; no shape,
fill or artifact decision changes. Generated pixels and weights stay local.

## Full-workflow boundary

The checker now has an explicit checksum-bound synthetic classifier route.
Its manifest states that the model is unapproved. Ordinary production execution
rejects it, and real-data admission rejects this route before reading a corpus.
The existing approved-classifier route remains supported. This prevents a
synthetic test from masquerading as production promotion.

The comparison retains the same 23 source images and all 706 authored points.
It finishes in 133.26 seconds: eleven graphs complete and twelve fail, compared
with thirteen complete and ten failed previously. Correct unique values fall
from 230 to 152 / 706; correct relational rows fall from 222 to 148 / 724.
Seventeen extra unique points remain, compared with 46 previously. There are
zero duplicate or wrong-scale rows, but that does not make the failed result
acceptable. All failures remain in the denominators.

All thirty previously observed panels remain available; two additional panels
are reached. Axes and OCR are unchanged on the common panels. A saved-observation
diagnostic with one-to-one five-pixel matching finds 521 previously accepted
true centers versus 497 now, against 578 truths in those common panels. Accepted
unmatched centers rise from twenty to twenty-two. The full 706-point workflow
scorer remains authoritative. The candidate's patch gains do not transfer to
complete graphs, and it is not selected for production.

## Remaining work

Shape correctness is only 288 / 576 when a sloping connection crosses the
marker, versus 528 to 548 / 576 in the other contexts. The training recipes have
only two fixed oblique angles. Wider crops also perform worse. The full-graph
regression additionally requires a diagnostic of actual synthetic proposal
context, rejected true markers and text fragments. Sloping-line augmentation
alone is not yet justified as a sufficient repair. The next step is to measure
that gap using owned train/dev graphs, then correct the training population
without private answers, source exclusions or a weaker bar.

The [machine-readable record](GOAL-22-NATIVE-CLASSIFIER-REPAIR.json) binds
the datasets, sources, candidate, tests, failed preparation and full comparison.
The [revision outcome](../ml/markers/classifier/native_context_v3/P1_RESULT.json)
closes this failed development run without consuming a sealed candidate.

All new source is Apache-2.0. The existing permissive training/runtime dependencies
are reused. Host fonts are rendered locally and no font binaries are copied.
No new dependency, public release, production activation or packaged build is
created. Build 433 remains 0.4.33. **All four Goal 22 outcomes remain incomplete.**
