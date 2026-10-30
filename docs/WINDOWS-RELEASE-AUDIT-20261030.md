<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Windows release audit, October 30, 2026

`packaging/Build-Windows.ps1 -AuditOnly` was run against commit
`fe0aad0f6150cee2f7314ee0b815e2e5fc3f118c`. It emitted no artifacts.
Version 0.4.33 is not release-eligible and the working tree contains unrelated
user changes and an untracked, disabled duplicate-candidate draft.

The audit found only `marker_classifier` approved. The required OCR detector,
OCR recognizer, and marker-center payloads lack approved release-eligible
evidence. Production workflow, review/export persistence, PDF and batch
workflows, keyboard accessibility, public scoring, reviewed OpenCV evidence,
clean Windows installer and portable tests, and final artifact-pair parity
remain blocked. The exact reviewed OpenCV and PDFium evidence roots were not
supplied to this audit. The PDFium clean-machine-load gate is recorded as
passing, but that does not establish the complete PDF workflow or package.

The audit is a current blocker inventory, not an installer or portable build.
No release gates or model thresholds were weakened. A prior automatic approval
review rejected removal of generated output; the approved replacement and
retention sequence remains unresolved while deletion is blocked.
