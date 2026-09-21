<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# One additional OCR option, 2026-09-21

**Decision: allow a local comparison of two larger, already trained OCR models.**

Example: test whether the larger reader distinguishes a printed `100` from
`1.00`, and whether the larger detector finds small labels without treating
graph lines as text. A better result is possible, not established.

| Choice | What happens |
| --- | --- |
| Test the larger models | Download about 174 MB once, convert with the existing local toolchain, and compare on owned synthetic development images. Measure accuracy and CPU time before selecting anything. |
| Keep the existing models only | Continue repairs with the current detector and English recognizer; do not import either new model. |

The exact models are **PaddlePaddle/PP-OCRv5_server_det** and
**PaddlePaddle/PP-OCRv5_server_rec**. Both official model cards identify Apache-2.0.
Their upstream files occupy about 88.4 MB and 85.2 MB respectively. Conversion
creates additional local copies. See the pinned official
[detector files](https://huggingface.co/PaddlePaddle/PP-OCRv5_server_det/tree/ca867c897ecbca8873081573a802ad70d499cb94)
and [recognizer files](https://huggingface.co/PaddlePaddle/PP-OCRv5_server_rec/tree/b26c3587fda8da3c8ec0ce357214b4d661ff1558).

This comparison runs locally, uses no private graphs, uploads no images and
requires no training. Larger models may be slower on this CPU. Conversion and
native-runtime parity must pass before interpreting their scores. Existing
acceptance, licensing, checksum and final production gates still apply.

The already available original mobile detector has just been checked against
the repaired pipeline. It reads 295 of 453 labels exactly, versus 328 for the
current detector, so it was not selected. The proposed server models are
different weights and have not been downloaded or evaluated.

Only this additional model import waits for a decision. Runtime repairs and
other Goal 22 work continue. No other new maintainer decision is currently
identified; final 2.0.0 activation and release already have conditional approval.
