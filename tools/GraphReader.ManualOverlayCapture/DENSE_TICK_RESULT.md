<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Dense tick crop diagnostic

Two previously inspected original images in the authorized training split had
many adjacent X-axis labels merged into a single OCR region. The existing
tick-lane recovery is disabled in the candidate composition. As an exploratory
counterfactual, the diagnostic reconstructed detected boxes from recognized
regions and ran that recovery both with and without the wide numeric region.
It returned four Y-axis proposals in one image and none in the other, with no
X-axis proposals in either case. Reconstructed boxes are not the complete
detector output and this does not establish how the enabled pipeline performs.

A separate diagnostic grouped observed tick strokes, required repeated measured
spacing, and froze non-overlapping crop windows only at observed strokes inside
the merged region. Four windows came from one internal build image and 15 from
one internal holdout image; both partitions belong to training. The assistant
visually reviewed their printed labels before OCR scoring. Those labels are
private, incomplete, and not independently adjudicated. One of the 19 windows
visibly clipped part of a digit.

| Reader and crop padding | Exact | Wrong | Empty | Exact on fully visible crops |
| --- | ---: | ---: | ---: | ---: |
| Numeric V5, no horizontal padding | 4 | 1 | 14 | 4/18 |
| General mobile reader, no horizontal padding | 18 | 1 | 0 | 17/18 |
| Candidate server reader, no horizontal padding | 17 | 2 | 0 | 17/18 |
| Candidate server reader, standard padding | 17 | 2 | 0 | 17/18 |

The server reader used the pinned model, production raster decoder, cropper,
and recognition options from the current candidate. The mobile and numeric
readers used their existing diagnostic compositions. CPU inference performed
no optimization or training. Neither the new crop windows nor the mobile
reader are integrated into the automatic workflow. Errors included a clipped
digit and a visible character confused with a letter. These observations do not
justify silently choosing a numeric alternative or inferring a label sequence.

The 19 selected crops do not measure whole-axis recall, false positives,
calibration, independent generalization, or product acceptance. The current
server composition remains unapproved. No sealed data was read.

| Private evidence | SHA-256 |
| --- | --- |
| First tick recovery probe | `9640618b667a2542946e6869f66f273f6b168af17a4f408f290d53ef4f2ce85b` |
| Second tick recovery probe | `0170554a0ed976dd597dca2020dd2d292d86ca5be45e9410800efe72212e0ef0` |
| Build crop input | `0de2129cc8e5c3c9b98625df095f48fe23cb159c23e9d8f29c091c2d03ed2f05` |
| Holdout crop input | `26e65944663b12902df7ae9630fee968b5db8435d9ed8e899c6d45f5e36ef86e` |
| Build reviewed labels | `05c2a6daca6abca9ce496b1a753345506891f443bad321232ae64712572d1c7f` |
| Holdout reviewed labels | `f9daad83ee9a785f4995bbda60ec1b30fc73533523cfd0d4e276899aa8422bd7` |
| Server runtime description | `31e551a0d78fdd2ad9f1e46fe5b52a9cc3729b36503a073b0d13c9a2e65ec677` |
| Hash-checked aggregate score | `81c1c212851909b9589236c33d1dd5dbffa3439b568e9bd2a3724e4510378581` |

The diagnostic's prior OpenCV image decode failed on these PNGs with the
reviewed native binary. Its local image loader now uses the Windows WPF decoder
and the same alpha compositing and grayscale conversion as the production raster
decoder. Its self-test covers PNG decoding and neighboring-ink exclusion. The
server run itself uses the production decoder directly. No runtime dependency
was added. Release builds of both diagnostic tools, the decoder self-test,
seven proposal/scoring unit tests, and pyflakes passed. Private manifests,
images, labels, predictions, and review crops remain project-local and are not
packaging inputs.
