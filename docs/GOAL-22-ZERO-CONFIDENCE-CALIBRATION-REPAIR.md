<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Zero-confidence calibration repair, 2026-09-30

Numeric OCR with confidence zero no longer reaches the confidence-weighted
axis fitter. The reading stays in Review, calibration receives an explicit
`ocr_tick_sequence_needs_review` reason, and export remains blocked. Explicitly
rejected readings stay excluded. No numeric value or confidence is invented.

The server OCR comparison exposed two source failures and one failed diagnostic
continuation with `ArgumentException: Printed x-tick evidence must be named,
finite, and confidence weighted.` The repair validates confidence at the OCR to
calibration boundary and retains the existing structured failure path.

All 504 App tests pass, including four new X/Y tick cases covering unreviewed
and rejected zero-confidence readings. The native tool builds with zero
warnings/errors. Tests and build take 106.14 seconds.

Targeted native evaluation covers every affected source/configuration pair
and a successful development control under two OCR configurations. All 12 panel
executions produce complete diagnostics. Four zero-confidence tick readings
remain visible in evidence, three review warnings are emitted, and all three
affected source/configuration pairs retain structured review failures. There
are zero unstructured argument failures and zero exports from failed sources.
The two controls preserve all 26 numeric rows each exactly. The five source
executions take 100.33 seconds including the runner; they reuse three distinct
synthetic graphs. All observed model providers are CPU.

The first compile exposed CA1859; changing the warning collection parameter
to `List<string>` repairs it without changing behavior. Two preparation errors
are retained: the targeted input first omitted the harness's required split
declarations, then required an additional dev control. Original train/dev
membership is preserved. Neither error ran model inference. No acceptance
threshold or frozen application contract changed.

Evidence and exact commands are bound in the
[verification record](GOAL-22-ZERO-CONFIDENCE-CALIBRATION-REPAIR.json). The
changed files are the production automatic-detection adapter and its tests.
The source snapshot preserves the exact compiled bytes. The committed adapter
uses the repository's LF line endings; byte comparison confirms that this is
the only difference from its compiled source. No dependency, license change, model import, training,
private/sealed read, production approval, packaged build or release.

This is a verified calibration defect repair. It does not replace the full
accuracy evaluation, private acceptance, WPF manual checks or distribution
gates. Goal 22 remains incomplete; build 433 remains version 0.4.33.
