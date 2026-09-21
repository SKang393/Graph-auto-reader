<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Feature refinement outcome: rejected

V7 improves the broader component diagnostic but does not meet the shared
marker bars. The complete native workflow also loses one successful export.
Keep V5 as the current native diagnostic baseline. Do not activate V7 or start
another training revision before diagnosing the remaining structure errors.

| Same detector and inputs within each comparison | V5 | V7 |
| --- | ---: | ---: |
| V28 component centers recovered | 1,450 / 2,004 | 1,819 / 2,004 |
| Extra component centers | 53 | 105 |
| V28 family centers recovered | 199 / 206 | 199 / 206 |
| Extra family centers | 27 | 29 |
| Native V27 graph exports | 17 / 23 | 16 / 23 |
| Correct unique exported values | 479 / 706 | 422 / 706 |
| Correct relational rows | 474 / 724 | 418 / 724 |

Combined component precision/recall are 94.54%/90.77%; family precision/recall
are 87.28%/96.60%. Eleven family predictions hit prohibited structures,
11/228 = 4.82%, above the existing 2% limit. This cached diagnostic is separate
from the native application: the native development subset retains 201/206
markers with no extras. All 39 native panels, 706 points and 724 expected rows
remain in the evaluation. Axis and OCR outputs are unchanged. Export continues
to reject unvalidated coordinates. No wrong scale, duplicate or wrong export
mode is introduced; three existing phase errors remain.

## Implementation and verification

The fixed 12-epoch recipe refines all 78,601 parameters of the existing V5
architecture using the same owned training cache: 62,512 original rows plus
15,337 presence-only rows. Presence-only rows contribute only to rejection
loss; no shape or fill label is invented. All 3,136 development rows remain
unchanged. The final epoch is used without development-based weight selection.

Training completes 7,308 optimizer steps in 897.17 seconds. Patch development
shape/fill accuracy is 97.33%/99.20%, with 2,878/2,880 markers retained and
1/256 artifacts accepted. Those patch results do not override the combined
failure. All 80,985 train/dev/presence tensors pass the unchanged 1e-5 ONNX
parity bound with zero changed decisions; maximum error is 9.835e-6.
Fifteen runner and cache tests pass. The cached combined diagnostic takes
5.28 seconds and native workflow takes 152.90 seconds.

All twelve logical processors remain eligible, with Idle priority and passive
waits. Maximum completed work/rest duty is 79.9942%; this is an average work
budget, not a claim about instantaneous per-core utilization.

The [outcome](../ml/markers/classifier/presence_feature_v7/P1_RESULT.json) and
[evidence index](GOAL-22-CLASSIFIER-PRESENCE-FEATURE.json) bind source snapshot,
configuration, canonical receipt, models, numerical checks and all failures.
All 88 earlier ledger entries are unchanged. No private or sealed data,
dependency change, activation, package or release is involved. Training data
and code are project-owned under Apache-2.0; generated weights remain ignored.

Goal 22 remains incomplete. Continue the current composed OCR evidence path
and diagnose the actual marker workflow before choosing further implementation.
