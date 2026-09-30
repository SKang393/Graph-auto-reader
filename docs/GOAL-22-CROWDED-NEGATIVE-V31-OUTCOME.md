# Crowded-background center V31 outcome

V31 fails the registered development bars. Retain V27/V5 with V44/English.
The full native comparison gains matches and reduces false points, but its
remaining errors and CSV results do not support production promotion.

## Measured change

The [training coverage diagnosis](GOAL-22-CROWDED-NEGATIVE-COVERAGE-DIAGNOSIS.json)
found 2,021 confident clear-background proposals whose patch bytes were absent
from the selected training negatives, among 2,433 such family proposals.
These are proposal counts, not a count of unique visual patterns.

Keep all 270,252 V30 rows and add all 6,366 newly mined background errors:
3,933 from component training and 2,433 from family training. Mining uses the
fixed authenticated V30 model and train data only. Every selected anchor is
more than eight pixels from every authored marker, and its decoded center is
more than five pixels away. Preserve all three input channels. No cap or
development-selected subset is applied.

The 276,618-row adaptation starts from V30's final checkpoint and runs eight
fixed epochs, 17,296 steps, with its existing recipe and 0.25 operating point.
The final epoch is used. All 176 development scenes and 2,210 truths remain
unchanged. No private or sealed data is read.

## Verification and results

All 45 preparation/admission tests pass. Two preauthorization helper errors,
a missing project import path and a misspelled field lookup, are preserved
with zero optimizer steps and no ledger mutation. Repaired preparation takes
247.167 seconds; preflight takes
22.119 seconds. Training and complete
development evaluation take 1654.035 seconds under the existing
12-thread, 80-percent CPU policy.

ONNX/PyTorch parity covers all 270,236 development proposals. Maximum absolute
error is 0.0000090599, with zero
confidence-cutoff decision changes. The frozen training source snapshot is
unchanged at close.

At 0.25, component recall is 94.21% and precision is
82.30%; family recall is 99.51% and precision
is 57.42%. The unchanged 95-percent bars fail. Other cutoffs
are descriptive and were not selected from the results.

The application comparison uses the exact tested `a6af334` runtime and retains
V44, the English reader, V5, every source image and all export safeguards.
V31 at 0.25 is compared with V27 at 0.10, so this compares configurations.

| Complete workflow measurement | Retained V27/V5 | V31/V5 |
| --- | ---: | ---: |
| Native matched markers, 838 truths | 739 | 753 |
| Native extra markers | 26 | 13 |
| Native missing markers | 99 | 85 |
| CSV exported sources, 23 sources | 15 | 13 |
| Correct CSV values, 706 truths | 414 | 331 |
| Correct CSV rows, 724 truths | 409 | 328 |

Native comparison retains 723 old matches, gains
30 and loses 16; 69 are
missed by both. OCR stays 348/453 exact and axis corners stay 85/93. All 31
native panels and 39 CSV panels are observed. The CSV comparison loses
2 prior exports and gains 0.
The complete source-status and numeric-value changes remain in the result.

All 339 emitted audit
rows have finite values, original-pixel coordinates and valid calibration.
Wrong-scale, duplicate and failed-source residual rows are zero. These are
observations; they do not establish a safety property from synthetic counts.
Every failed source and its truth remain included.

Native inference takes 133.854 seconds, CSV
inference 192.123 seconds, and the full comparison
chain 339.104 seconds. All recorded model
envelopes use CPU. No application source or model other than the center
candidate changes during comparison.

## Outcome

The [single revision outcome](../ml/markers/center/crowded_negative_v31/P1_RESULT.json)
binds the cache, recipe, source snapshot, tests, parity, application results and
decision. Ledger row 95 closes as `failed_dev_unconsumed`; the preceding 94
entries are unchanged. No sealed budget is consumed. Do not rerun this
unchanged revision or activate it.

Code, synthetic data and initializer are project-owned Apache-2.0. No dependency
or external weights are added. Build 433 remains 0.4.33. No package or release
is created. Goal 22 and overall acceptance remain incomplete.
