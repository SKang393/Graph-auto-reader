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

An additional fixed 28-panel synthetic family-train replay reproduced the V37
baseline and reconstructed 9,053 selected rows byte-for-byte. The bidirectional
head matched 467 of 500 truths with 100 extras. Forty extras originated at
selected anchors and 60 at unselected anchors; all had negative anchor labels,
but six matched detections also had negative anchor labels. Runtime masks are
incomplete, and this train-only result does not justify a suppression rule.
In that fixed replay, 92 of the 100 extras were more than 16 pixels from the
nearest synthetic truth; 53 of those 92 came from unselected anchors. Of 60
unselected extras, 24 exact reflected crops matched an earlier V38 cache, two
matched selected family rows, and 34 matched neither of those two checked
sets. This does not establish novelty against other training groups or a safe
semantic negative category.
An exact-byte comparison against all 276,618 authenticated original synthetic
train rows found that 32 of those 34 crops already matched the crowded-mined
negative group. The two unmatched crops do not establish a distinct semantic
class, so another hard-negative run is not justified by this evidence alone.

- Checkpoint SHA-256: `8aa11e55fae8e642788e8a73f34a6a4badf17da2222b1dbb89f89ecb662791c0`
- Training outcome SHA-256: `52b1d1eb6072211140804adf19a55ac52a70b34a2cbfa670c418f1f469d70d11`
- Development report SHA-256: `42d5b7150934eb995a4606d889cfe25b9e1fdae0bc88669a5ba6a6175d9dbf95`
- ONNX SHA-256: `15266a41c2858945f79e91b6fcbf006efd18e89c28376c40c90d33575f22b473`
- Train-only comparison SHA-256: `d854a25e9792383b92313d44bf12cdfbee1b11841571404d0941e4c3d81fd5c9`
- Family train replay SHA-256: `ee1ef565bad2f633929c088b7c19a7c91f56689bccdcc35b0cc5652949640b3d`
- Family distance audit SHA-256: `be0bfb7ceed5163ce377e3e31d5dbde43e70be9c4c2e31d0bb42fc6ee19cb5a8`
- Full synthetic train coverage SHA-256: `fd89d162f3d7a8496f7506a0c8adb6a682bcdba53eaf7f9ca48b6b1ad9a1a998`
