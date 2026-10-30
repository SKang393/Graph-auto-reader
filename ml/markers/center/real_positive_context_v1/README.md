# Real-positive confidence foundation

This module is an unapproved modeling and loss foundation, not a trainer or a
production model. A zero-initialized linear residual sees the frozen V37
spatial features from 33-pixel three-channel crops. It changes only confidence;
the original geometry and 0.1 operating threshold remain fixed. Its synthetic
binary labels are immutable bytes checked against an expected SHA-256. The
caller must authenticate the expected hash and source manifest. Real-center
logits have only a positive loss; unmarked real pixels are not negative labels.
The loss rejects CPU tensors and devices other than RX 6600 XT on `cuda:0`.

Before training, a separately registered candidate and implementation must
verify the exact V37 initializer, synthetic source and label provenance,
user-authorized real-project snapshots, study-grouped splits, device and
tensor residency on the RX 6600 XT, and the fixed evaluation plan. Actual
runtime-bound three-channel crops, including OCR and artifact masks, are not
yet available for this real dataset; do not substitute fabricated masks or
claim training readiness. Sealed-source overlap and manual-label quality remain
unverified. This module does not read data, run an optimizer, or establish
release acceptance.

The foundation parity test looks for the V37 checkpoint in the project-root
`artifacts/goal22-runs/center-confidence-reflection-v37/P1/marker-center.pt`
or under the existing `artifacts/goal22-worktrees/resume-20260908` worktree.
It checks the hash against the tracked V37 `P1_RESULT.json`. If neither copy is
present, only that foundation test is skipped. A future trainer must require
and verify the exact initializer; this test skip does not waive that requirement.
