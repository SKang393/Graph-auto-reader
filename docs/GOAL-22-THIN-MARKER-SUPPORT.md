<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Goal 22 thin marker support

The model found a missing open-circle point within 0.40 pixels. The subsequent
geometric check discarded it because its sparse sample locations missed the
one-pixel outline. This was a postprocessing loss, before classification or
text exclusion. The actual frozen C# replay reproduces 3,695 proposals, 13
outputs above the unchanged 0.25 threshold, two geometry rejections, eleven
pre-suppression candidates and eight final candidates in 2.82 seconds.

An explicit **synthetic-only candidate setting** adds a check for visible ink
enclosing the decoded pixel. It uses the existing 12-pixel maximum support
radius and 0.12 ink threshold, with four-connected background. An open region
reaching the image or window edge fails. Coordinates, scores, masks and
suppression remain unchanged. The legacy and approved factories retain their
existing behavior. The new setting requires a distinct, checksum-bound manifest
and adapter identity; private-data admission and production approval reject it.

On 2,160 balanced owned primitive cases, geometric support rises from 2,125 to
2,156. All 31 additional cases are open outlines. These counts measure support
at supplied diagnostic centers, not detection accuracy. The affected workflow
point passes, as does another proposal at an already-detected decoy. This does
not establish whether the full workflow gains a correct export.

The complete cached development replay retains all 176 scenes and 2,210 truths.
It authenticates all cached outputs and reconstructed proposal-patch hashes,
uses no new model inference, and takes 130.20 seconds. Final predictions and
truth matches are unchanged on both splits:

| Development split | Correct detections | Extra detections | Missed points | Precision | Recall |
| --- | ---: | ---: | ---: | ---: | ---: |
| Component, 167 scenes | 1,796 | 238 | 208 | 88.30% | 89.62% |
| Graph family, 9 scenes | 190 | 39 | 16 | 82.97% | 92.23% |

Three additional proposals disappear during the existing suppression step.
No previously matched truth loses its match. The detector still fails the
shared 95% bars. This repair does not authorize a sealed run or promotion.

All 379 application tests pass. The Windows x64 runner builds with zero
warnings or errors; both fictitious runner and binding checks pass. All 21
Python geometry tests pass, with one non-failing pytest cache-directory warning.
Python/C# geometric decisions agree on all 2,194 cases, including the saved
workflow outputs and negative open-stroke/edge cases. The C# parity check takes
0.31 seconds. Existing training data, model weights, approval records and
packaged build 433 (`0.4.33`) remain unchanged.

Original code and the owned diagnostic raster are Apache-2.0. Dependencies and
licensed model weights are unchanged. No private or sealed input, optimization,
public release or packaged build is involved. Detailed aggregate evidence and
artifact checksums are in [the evidence record](GOAL-22-THIN-MARKER-SUPPORT.json).

The same 23-source full workflow completes in 166.33 seconds. It preserves
all 701 OCR readings and raw axes, and changes accepted markers in four panels.
The missing first point now survives. Its graph still needs review because a
marker-like decoy conflicts with the printed session pitch. One other source
becomes exportable while another loses exportability for that same conflict.

The total remains eleven completed sources and twelve failures, with 226/706
correct unique values and 218/724 correct relational rows. Of 272 exported
point locations, 270 are retained with identical scientific audit rows; two
points from the newly failing source are replaced by two from the newly
completing source. All full accuracy metrics are unchanged. This mixed result
is retained as unapproved evidence, not presented as a net accuracy gain.
Next, diagnose the marker-like decoys and their synthetic supervision without
discarding failed cases or weakening calibration safety.
