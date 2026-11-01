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

## Review-required workflow experiment

An opt-in, unapproved frozen-candidate composition now proposes crops at
observed axis strokes inside a single wide numeric X-tick region. It requires
repeated measured spacing and trims overlapping crop windows. It reads original
pixels without horizontal padding, retains the original concatenated text as
unreviewed `Other` evidence when crop readings are available, and adds an
unresolved tick-review warning. It never supplies a missing digit. A failed or
incomplete recognition batch retains the original region and does not cache a
partial result. This path cannot be enabled by the approved production factory.

On the same two previously inspected images the candidate reproduced all 19
selected windows, with one window trimmed by one pixel. It read 17/19 labels
exactly, including 17/18 fully visible labels. The labels remain
assistant-reviewed rather than independently adjudicated. Both panels reached
the marker stage but still failed calibration, and the separation review warning
was present on both. One returned `NeedsReview`, the other
`InvalidSessionOrigin`.

On all 20 original training images, the candidate proposed separation on two
of 21 panels. All 21 reached the marker stage, all 20 images failed automatic
calibration, and no final workflow points were produced. Ten panels returned
`InvalidSessionOrigin` and 11 returned `NeedsReview`; 20 also retained axis
geometry review flags. On the 332 imperfect recorded points, the center and
accepted-marker stages matched 250 and 207 respectively, identical to the
earlier candidate run. All 21 center observations retained identical geometry
and confidence. The other 19 panels retained the earlier calibration status and
error message in the prior overlap-trimmed comparison. These point annotations
cannot establish precision. The final run used CPU inference only, no training,
and took about 2 minutes 36 seconds.

| Integrated evidence | SHA-256 |
| --- | --- |
| Runtime description before and after | `732fb2289103d81223d466bebcc6cfe5295b0de9d3a695d3939f771e67f9839c` |
| Build-image workflow report | `ff4feff3f672c719bee1d5ba22a7613684ce317ed00871027f931e6a0c1bf356` |
| Holdout-image workflow report | `ebcf3351cbfc91e88f4c093d9ffc11f384d77ee85eeb9595e399da6421a6e529` |
| Hash-checked crop comparison | `dc07543727473f6a2fe1ef799a3c85d353ca700a887e4251061db78215cc8053` |
| Full 20-image workflow report | `99f88a13003e8b367f5f0b195864bd7507d92c81340fadc91d4f7551861b2a02` |
| Full workflow point score | `bec9bfaffc7b970ebe42ed95bf9fac500921bcc1e7962ecf6946d979ffd42fe2` |

Before the final guard against zero-length strokes, the complete solution run
passed 2,391 tests, skipped 25, and failed none. After the guard, all ten
focused dense-tick tests passed and the diagnostic rebuilt with no warnings or
errors. The original workflow adapter already filters zero-length strokes;
the additional guard protects direct OCR callers. Eleven diagnostic Python
tests and pyflakes passed after the final scoring changes.

These results do not establish complete tick detection, correct axis geometry,
an approved OCR model, or product acceptance. The two source images were
already inspected during development; neither is an independent release test.
