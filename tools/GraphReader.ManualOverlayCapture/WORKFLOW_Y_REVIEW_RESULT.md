<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Visual review of detector-emitted Y ticks

An assistant reviewed all 115 Y-tick regions emitted by the frozen candidate
workflow on 20 previously exposed training images. The review sheets showed
original pixels and local context without OCR readings or confidence. The
assistant had inspected some images and crops previously, and the review was
not independently adjudicated. It measures emitted regions only and cannot
count labels the detector missed.

| Visual classification | Regions | Region-specific review before the lane safeguard | New lane warning |
| --- | ---: | ---: | ---: |
| Complete readable numeric tick | 105 | 47 | 0 |
| Partial numeric text | 5 | 2 | 0 |
| Other-text fragment | 3 | 2 | 3 |
| Graph structure | 1 | 0 | 0 |
| Uncertain | 1 | 1 | 0 |

Of the 105 complete numeric ticks, 103 primary readings matched the visible
transcription exactly and numerically. Two were different numeric values; both
already had region-specific competing-reading warnings. The partial, other,
structure, and uncertain crops were not scored as correct. One partial crop
visibly excluded part of a longer printed label, illustrating why a numeric
reading of the crop does not by itself establish the complete tick value.
The graph-structure crop and three of five partial crops had no prior
region-specific warning, although their panels separately required axis review.

The new position safeguard requests review on all three reviewed other-text
fragments and none of the 105 complete numeric ticks in this exposed sample.
It does not resolve the partial and graph-structure cases. These observations do
not establish detection recall, independent generalization, complete graph
calibration, or production acceptance.

| Evidence | SHA-256 |
| --- | --- |
| Visual-review protocol | `365dcec516813324725cf0c861a072a75433ec26a5ad26865c6746c017b2a7a7` |
| Prediction-blind sheet manifest | `6d793b8887d82f334d8adabcdd8bf2e557749f95842c3fc1a617d17303447e78` |
| Private visual labels | `c9c01aa57693311c04fa587e97b5c9cb3f82357f70489143a2312c6ffe33c309` |
| Scoring registration | `66ae7d57d12c9b7030cea1c8f6dc01a7de6b9d8d4a82be9523b4e71ffcb580e5` |
| Aggregate visual-review score | `62c13612933178b67262032338d590616de6b7b8dbdc0682145cf2c7cf6165ed` |

Five focused review and scoring tests and pyflakes passed. The underlying
captures used CPU inference, no optimizer steps, and no sealed-payload reads.
Private images, transcriptions, and per-region predictions remain project-local
while the partial-crop and graph-structure investigation continues.
