<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Synthetic family false-positive diagnostic result

The diagnostic reproduced the frozen 0.1-threshold candidate predictions for
all 45,396 proposal rows in nine synthetic family scenes, byte for byte. It
also reproduced all 58 unmatched detections against 206 annotated markers
using the original five-pixel matcher. This is a description of an already
failed development run, not an independent evaluation or a new model.

Only five detections were within five pixels of an annotated hard-negative
center: three arrowheads and two legend symbols. The other 53 were more than
32 pixels from every annotated hard-negative center. For reference, one
unmatched detection was between five and eight pixels from an annotated marker,
10 were between eight and 16 pixels, 26 were between 16 and 32 pixels, and 21
were farther than 32 pixels. These distance bins are disjoint; they do not
establish the cause of a detection.

The synthetic contact sheet shows detections around legend graphics and text,
as well as other marks. This visual inspection is not an independent label
audit, and the hard-negative annotations do not cover every non-marker
structure. Consequently, the five annotated hits must not be interpreted as
the full number of text or legend errors. Nor does this diagnostic justify
silently suppressing candidates near text: genuine markers may overlap text or
other graphics, and runtime masks may differ from synthetic annotations.

| Evidence | SHA-256 |
| --- | --- |
| Pre-run registration | `c7074c2676de3a16647d949b2cb1495c51631595a148a02031ed3855e6a1e349` |
| Diagnostic protocol | `08cf99c991833cf7e61919ddb398439ed39c37527b7bfcaa7a6f2db13a7ced47` |
| Diagnostic source | `f4e41046cb5d63dd536ea5db17495f06bb2c4cfdc68fa1fcd1d8c8af3b8fd6ba` |
| Frozen development report | `056aaca3479a8c77b7d5eda37ee55b6d77013282d2079082cbe887ec5956cd19` |
| Candidate ONNX | `35b50adbd6961293b888c5de3f2e5594b9cda7a9cb95d6c68b425999271a1535` |
| Diagnostic report | `62fd13a809d32e323308b5ea155222624fe9ee217c72ea6064afcc09fdd691ad` |
| Synthetic contact sheet | `65185bc841979c13797280008f6735ce08598d85f9e3dbdc54ef1006696924af` |

The diagnostic used CPU ONNX inference for the frozen model, no optimizer
steps, no real-image reads, and no sealed-payload reads. The candidate remains
unapproved. The exact synthetic inputs and diagnostic output remain in the
project's active run folder for the continuing failure investigation.
