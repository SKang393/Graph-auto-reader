<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# One additional OCR option, 2026-09-21

Two larger pretrained OCR models were evaluated locally.

Example: test whether the larger reader distinguishes a printed `100` from
`1.00`, and whether the larger detector finds small labels without treating
graph lines as text. A better result is possible, not established.

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
detector at that earlier checkpoint, so it was not selected. The server models
are different weights. Their 173,590,014-byte intake is now downloaded and
verified against both pinned official repositories. The
[completed comparison](GOAL-22-SERVER-OCR-COMPARISON.md) records conversion,
16 CPU parity cases per model and all four native/CSV configurations.

The server reader improves exact readings from 348 to 377 of 453 but raises
wrong-phase CSV rows from three to 11, while held-out CSV dev counts do not
improve. The server detector and combined configuration regress. None meets
the unchanged development bars, so no production model is replaced.

Import, provenance checks, conversion parity and native comparison are complete.
No production replacement or release acceptance is established by this comparison.
