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

- Checkpoint SHA-256: `8aa11e55fae8e642788e8a73f34a6a4badf17da2222b1dbb89f89ecb662791c0`
- Training outcome SHA-256: `52b1d1eb6072211140804adf19a55ac52a70b34a2cbfa670c418f1f469d70d11`
- Development report SHA-256: `42d5b7150934eb995a4606d889cfe25b9e1fdae0bc88669a5ba6a6175d9dbf95`
- ONNX SHA-256: `15266a41c2858945f79e91b6fcbf006efd18e89c28376c40c90d33575f22b473`
