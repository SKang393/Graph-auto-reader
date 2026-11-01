<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Provisional session-origin repair

A provisional lattice establishes column order but has no absolute session
origin. Previously, an earlier unlabeled tick could make the first detected
marker appear to be session two, causing an `InvalidSessionOrigin` result even
though no absolute session was known. The fitter now leaves the assignment
ordinal-only and requests review. Separately, an explicit maximum session can
no longer be positioned by extrapolating from that provisional origin. The
fitter does not invent a maximum anchor in this case. Both regression tests
failed before their respective fixes and passed afterward.

The repeated unapproved candidate workflow covered the same 20 exposed training
images and 21 panels as the pinned prior capture. Four panels changed from
`InvalidSessionOrigin` to `NeedsReview`, removing all four unsupported first-
session claims; the other 17 retained their validity. All 21 retained the same
workflow failure, axis-review flag, tick-region records, center observations,
and accepted markers. A separate post-capture reason comparison found one new
orientation-review warning on one panel from the later OCR safeguard and no
other added or removed reasons apart from the four origin claims.

All 21 panels still returned `WORKFLOW_RECALIBRATION_REQUIRED`, and all 20
images failed automatic completion. The capture used the unapproved horizontal
orientation candidate and cannot establish release acceptance or independent
accuracy. It read no sealed payload and ran no optimizer.

| Evidence | SHA-256 |
| --- | --- |
| Protocol | `86bc8d9b9ae3d07c331988751e0edcd23faf7e9207ca63c91760fbcd14a1e228` |
| Pre-capture source registration | `8ed2b95dfe2c0fa2e47902aae4741fc3b9cc0fe9e3f474828af8c681e9a972b8` |
| Prior workflow capture | `1fb3a30c5d57ecd0ec802ba4394a5077e106f81b5d2229550ca0f90bc481a909` |
| Runtime description before and after | `a8d5989da9501a0b5c0b7afd217ca7250c0fe0a2405a644fb3ec2b7b04cc09df` |
| Current workflow capture | `f6d7e6e0dcfa88e3f77c398d4ce6071da59f2d885cba785bb834d985c02f0016` |
| Aggregate comparison | `fa3c8aef73ee9a1aa81cfdcb9a14885ec054f62ca14768c5181b1e0ca4a96f0d` |

`dotnet test GraphAutoReader.slnx -c Release --no-restore -m:1
-p:UseSharedCompilation=false` passed 2,432 tests, skipped 25, and failed none.
The diagnostic Release build had zero warnings and errors. Three comparison
tests and pyflakes passed. The 20-image capture returned exit code 1 because
all workflows still required review; CPU inference was used for the capture.
Private images and per-panel observations remain project-local.
