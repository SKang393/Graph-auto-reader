<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Composed OCR development prerequisite, 2026-09-21

Real-workflow admission can now consume the native composed OCR development
report through an explicit version-2 prerequisite. The mechanical writer
copies the actual source counts, verifies their rates and runtime identity,
and writes evidence only after all five existing OCR development bars pass.
It cannot overwrite existing evidence or emit a file for a failing score.

The prerequisite binds the complete workflow descriptor and operating point,
OCR models, source-report bytes, development request, stage descriptor, and
the shared native execution fingerprint. The source validator checks the
development split independently of training. Additional consistency checks
reject nonzero edit counts for supposedly exact text and unmatched edit
counts without unmatched regions.

The current composed protocol and shared Python evaluator use five full-source
OCR metrics: precision, recall, exact text, character error and role accuracy.
The new adapter applies their existing values from the canonical bar file.
It does not invent a separate graph-structure attribution score that the native
scorer does not measure. That field must be null; a fabricated zero is rejected.
False detections remain in the full precision denominator. Legacy prerequisite
formats retain their existing checks. Native execution identity is reported
honestly and is not presented as numerical ONNX-versus-training-framework parity.

The build has zero warnings and errors. Checks pass: 35 aggregate-evidence,
33 admission, eight worker and five runner checks, plus the existing V26 source
adapter self-test. Positive cases use fictitious data exactly at the existing
bars. Each failing OCR metric, hidden failure, model/input/runtime drift,
sealed substitution and fabricated attribution is rejected. Writer checks
verify a readable passing fixture, no overwrite, temporary-file cleanup, and
no output from a failing fixture. The complete final check took 54.20 seconds.
An earlier build failed CA1861 for a temporary constant array; it was repaired
with a static field. That failed build remains recorded and ran no models.

The producer command is `--check-composed-ocr-prerequisite-dev` in the synthetic
tool. The consumer/writer command is `--write-composed-ocr-dev-prerequisite`
in the real-workflow tool, taking the source report path/hash, frozen workflow
binding path/hash, request hash, stage-candidate hash, and a new artifact path.
Its output is development prerequisite evidence, not model approval.

See [source hashes and validation records](GOAL-22-COMPOSED-OCR-DEV-PREREQUISITE.json).
No actual model has acquired a passing prerequisite in this batch. Current
OCR development accuracy still fails. Sealed source adapters, marker/classifier
prerequisites, real acceptance, promotion and final distribution remain.
Existing real-data admission restrictions and the four required stage/split
roles remain in force. No policy file, application behavior, dependency,
license, model weight, private/sealed read, training or package changed.
Build 433 remains 0.4.33. Goal 22 is not complete.
