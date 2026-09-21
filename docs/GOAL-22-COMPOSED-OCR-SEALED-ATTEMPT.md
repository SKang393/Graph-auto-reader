<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Composed OCR sealed attempt, 2026-09-21

The frozen composition reached the guarded sealed worker, but the worker exited
with an error after a confirmed read. It returned no accuracy aggregate. This is
an execution error, not an accuracy pass or a measured accuracy failure.

The candidate uses unchanged V44 detector and official recognizer weights.
Its configuration, protocol and canonical authority snapshots bind 278 source
files. Current development authentication passes. The first command attempt was
void before admission because its preregistration omitted the shared policy
reference. Both authority objects and that exception were retained, the metadata
was repaired, and the same candidate was retried without changing models or bars.

The retry took 87.18 seconds including development replay. Its sanitized failure
is `OCR_SEALED_TRANSPORT_CHILD_FAILED`. Both canonical authority records close as
`sealed_error`, and the confirmed read remains counted. The registry retains two
unused reserves. No case identities, pixels, predictions or truth were emitted;
the used set is not retired. No private corpus was read and no optimizer step ran.

The [candidate outcome](../ml/ocr/composed_runtime_v1/P1_RESULT.json) records the
aggregate-only failure and exact evidence references. All 89 previous training
ledger rows remain unchanged. No model promotion is justified by this error;
production approval, real acceptance and release remain unfinished. Build 433
remains 0.4.33. The preceding `e652060` source checkpoint passed CI run 35566215876.

Next: reproduce the worker failure on a fresh, explicitly open development
cohort drawn from the public coverage specification. Diagnose archive handling,
input coverage and stage execution without inspecting or replaying sealed cases.
Keep the current development pass distinct from missing sealed accuracy.
All four Goal 22 outcomes remain incomplete.
