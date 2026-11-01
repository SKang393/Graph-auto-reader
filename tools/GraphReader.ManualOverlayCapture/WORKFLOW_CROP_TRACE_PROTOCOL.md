<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Workflow OCR crop discrepancy trace

Two previously inspected training-source panels produced incorrect numeric
workflow readings, while directly rereading their emitted boxes produced the
visually reviewed values. Trace those two sources only, using the same pinned
unapproved candidate models and original inputs. A forwarding recognizer
records crop dimensions, crop hashes, and raw recognition results without
changing the recognizer's identity or returned result. An existing in-memory
detection observer records raw detector geometry and orientation. No raster
pixels or reviewed transcriptions enter these hooks.

With the same frozen diagnostic binary, repeat the direct standard-padding
reread and record its crop hashes and dimensions. Compare the two previously
identified regions' full-workflow detection orientation, crop identity, raw
recognizer output, and final workflow reading. Also compare the traced panel
status, prior review reasons, and marker observations with the preceding full
workflow capture. A difference in hashes is evidence of different crop inputs;
it does not alone identify the cause. The two images and their errors are
exposed, so this is diagnosis rather than independent acceptance.

Hooks are restricted to the unapproved frozen candidate factory. This trace
does not alter an approved runtime, tune or train a model, open a sealed
payload, change any review rule, or approve a release. Per-region traces and
source images remain in ignored project-local artifacts.
