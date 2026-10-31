<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Real-positive component-preservation candidate

The separately registered candidate completed its fixed 2,264 optimizer steps
on the AMD Radeon RX 6600 XT (`gfx1032`, `cuda:0`). It combined authenticated
training-only real positives with synthetic examples and a fixed penalty on
upward confidence shifts for synthetic component negatives. No real unlabeled
pixels were treated as negative labels, and no sealed data was read.

At the registered 0.1 threshold, the pinned synthetic development evaluator
reported:

| Set | Precision | Recall | False positives | False negatives |
| --- | ---: | ---: | ---: | ---: |
| Component | 95.79% | 94.26% | 83 | 115 |
| Family | 81.82% | 96.12% | 44 | 8 |

Both precision and recall must reach 95% for each set. The candidate therefore
remains `failed_dev_unconsumed`, with no production approval or release
eligibility. These fixtures do not establish performance on real graphs.
ONNX parity had zero threshold-decision changes over 270,236 rows, with maximum
absolute error `8.106231689453125e-06`.

- Checkpoint SHA-256: `bba7a18ddf0cfd2aa14fa06042f5188763fb4d85d8ed23da6cf3405c93ff0d26`
- Training outcome SHA-256: `bba44563653d8b3ee863f420a6dc36182d6ec78831b1babee2dc197b01a61647`
- Development report SHA-256: `733856d2985cf6133ce881c21f19c110560d9e999c8664de2038e996fba5aadd`
- ONNX SHA-256: `88f22b586521213407f1e3392aceb92d95f5b2e3e98f456707feceeb587678a4`
