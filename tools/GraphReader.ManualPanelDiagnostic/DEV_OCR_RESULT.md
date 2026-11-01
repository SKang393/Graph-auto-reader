<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Development crop OCR diagnostic

The existing flag-blind development sheets contain 134 frozen proposals from
15 images in five studies. Before viewing the candidate recognizer outputs, an
assistant transcribed 115 clearly readable numeric crops. Four clipped or
ambiguous numeric crops, 12 other-text crops, two graph-structure crops, and
one uncertain crop remained in the inventory without exact transcriptions. The
review was not independently adjudicated, and these images and layouts had
already been visually inspected during development.

The pinned candidate server recognizer read 111 of 115 transcriptions exactly.
The other four primaries were `l` for the expected digit `1`; the existing
numeric parser interprets them as 1, while the existing nonliteral-reading
safeguard requests review if such a reading is assigned an unreviewed tick
role. All 115 therefore had the expected numeric value under that parser. This
does not establish whole-image detection, reading accuracy on omitted crops,
calibration, or automatic workflow completion.

Of the 111 exact primaries, 70 had a different positive-confidence numeric
alternative. Eleven and four retained a competing numeric alternative above
0.001 and 0.01 respectively. These descriptive counts contain no wrong parsed
numeric primary among the 115 scored crops, so they do not establish a safe
alternative-confidence threshold.

Among the 15 nonnumeric or uncertain crops, six produced a numeric primary:
four other-text fragments, one graph-structure crop, and the uncertain crop.
The previously measured axis-connectivity signal flagged the latter two, but
not the four other-text fragments. The crop-level results do not establish that
all six proposals would receive a tick role or survive the full workflow, but
they demonstrate that a numeric OCR output alone is insufficient evidence of a
tick label. No confidence guard or model approval was relaxed.

| Private evidence | SHA-256 |
| --- | --- |
| Transcription protocol | `6b2bd4c4158008737e7f88fe91a936679cd898c0e22e551c93d3180d1ab186b4` |
| Pre-prediction registration | `dc0108f9e1d229be39f3edfd55b5565e85936ed114cdc42f69d6a471ad16d555` |
| Assistant transcriptions | `7c745f3c4f7a843294ab2cd4c972797b9329605d02175d2b0c8f5f886f81d1a5` |
| Truth-free crop input | `e53c8da9756930b87243a12dbd007c7af9e24ca01c96a44a85e5ab69c0ccb669` |
| Runtime description before and after | `a3d83f0e2db375a7bb37ae014f9ff4f10df122908cbe931d2ab560b3b2af6eec` |
| Frozen recognizer predictions | `de96c9969d771042395f467f08431168c6f63a015e86432a54d494e1b2894f7c` |
| Aggregate score | `70cc729db78a5b52a9980559490675243ec4b75ff5d0924db38911fe38a4d3d8` |

The diagnostic Release build succeeded with zero warnings and errors. Four
focused input-boundary and scoring tests and pyflakes passed. The run used CPU
inference, performed no optimizer steps, and read no sealed payloads. It is a
development diagnostic, not an independent product or release acceptance test.
Private source images, transcriptions, and per-crop predictions remain inside
the project-local artifacts directory.
