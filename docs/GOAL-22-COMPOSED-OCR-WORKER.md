<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Composed OCR worker connection, 2026-09-21

The aggregate-only worker can now execute the full composed OCR pipeline.
Its request and result schemas are distinct from the original detector-only
worker. Raw detector geometry and assembled OCR geometry remain independent;
recovered or merged regions do not imply a negative recognition failure count.
The original worker format remains supported without changing its identity fields.

Both modes authenticate the candidate before the first-read handshake and
require a durable authorization acknowledgement and positive read receipt.
No case-level output is accepted. The controller integration is still in progress;
these fixture results do not admit a candidate or spend a sealed run.

Validation: 99 Python tests, 15 checks for each worker mode, four existing native
fixture suites, and native-to-Python result-envelope checks pass. The x64 build
has zero warnings and errors. The complete job takes 94.32 seconds. An existing
admission test used a non-hash attempt identifier; it now uses the real bound
fixture identifier. The authorization checks were preserved.

The first two failed fixture runs are retained. The successful evidence is in
`artifacts/goal22-runs/composed-ocr-worker-checks-v3` in the integration worktree.
[Machine-readable evidence](GOAL-22-COMPOSED-OCR-WORKER.json) records source and
result checksums. No model inference, private read, sealed read, model revision,
production activation, packaged build or public release occurred.

Next: authenticate current composed development evidence, connect the controller
and recovery path, and then run the genuine gates under canonical authorization.
Marker and calibration failures also remain. All four Goal 22 outcomes are incomplete.
