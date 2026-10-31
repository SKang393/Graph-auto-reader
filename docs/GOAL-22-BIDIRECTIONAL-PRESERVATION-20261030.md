<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Bidirectional marker-preservation candidate

The registered exploratory candidate completed 2,264 optimizer steps on the
AMD Radeon RX 6600 XT (`gfx1032`, HIP `cuda:0`). It retained the fixed mixed and
component-negative losses and added fixed penalties for downward shifts on
synthetic component positives and upward shifts on synthetic family negatives.
No sealed data was read, and unmarked real pixels were not labeled negative.

At the registered 0.1 threshold, the pinned synthetic development evaluator
reported:

| Set | Precision | Recall | False positives | False negatives |
| --- | ---: | ---: | ---: | ---: |
| Component | 92.70% | 97.01% | 153 | 60 |
| Family | 80.97% | 97.09% | 47 | 6 |

Both precision and recall must reach 95% for each set. The candidate is
`failed_dev_unconsumed`, without production approval or release eligibility.
These fixtures do not establish real-graph accuracy. ONNX parity had zero
threshold-decision changes over 270,236 rows and maximum absolute error
`8.106231689453125e-06`.

A fixed-threshold train-only comparison found that the bidirectional candidate
recovered 101 of 3,258 synthetic component-positive identity-view anchors
relative to the previous preservation candidate, with none newly rejected. It
also accepted 461 of 32,580 component negatives, compared with 335 previously.
On 8,230 family negatives it accepted 278, compared with 360 previously. These
are anchor-label diagnostics, not decoded-object truth or evidence that another
weight change will improve development or real-graph accuracy.

- Checkpoint SHA-256: `8aa11e55fae8e642788e8a73f34a6a4badf17da2222b1dbb89f89ecb662791c0`
- Training outcome SHA-256: `52b1d1eb6072211140804adf19a55ac52a70b34a2cbfa670c418f1f469d70d11`
- Development report SHA-256: `42d5b7150934eb995a4606d889cfe25b9e1fdae0bc88669a5ba6a6175d9dbf95`
- ONNX SHA-256: `15266a41c2858945f79e91b6fcbf006efd18e89c28376c40c90d33575f22b473`
- Train-only comparison SHA-256: `d854a25e9792383b92313d44bf12cdfbee1b11841571404d0941e4c3d81fd5c9`
