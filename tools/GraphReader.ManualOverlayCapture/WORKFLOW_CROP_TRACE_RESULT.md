<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Workflow OCR crop discrepancy trace

The two previously inspected training panels with wrong numeric workflow
readings were traced through the same unapproved candidate models. Both used
the full original source image as their panel. The raw detector reported
orientations of -90 degrees and approximately 79.82 degrees; both are treated
as rotated by the cropper. The visually reviewed tick text is horizontal.

For each case the workflow and direct reread crops were both 320 by 48 pixels,
but their crop hashes differed. The workflow's raw recognizer and final output
were wrong, while the direct horizontal reread was exact. In one case later
sequence processing changed the raw reading to another wrong value; in the
other the raw and final readings agreed. The trace identifies a concrete crop
orientation discrepancy, but its hashes alone do not isolate every possible
preprocessing difference or establish general accuracy.

The traced runs preserved the previous panel status, failure message, center
observations, and accepted markers. They remained blocked by calibration review.
The diagnostic recognizer wrapper forwards the same model identity,
configuration, input, and returned result; a synthetic self-test passed. Its
hooks are available only through the unapproved frozen candidate factory.
They are not installed in an approved production composition.

| Evidence | SHA-256 |
| --- | --- |
| Trace protocol | `c4e85216a42f58e018707df4c063e007fe7ed0cfbb2ec3936289a4031254546c` |
| Pre-run registration | `4dd7d3917dcfc0caee606420292214343abdb0aaf409b05040d3d918729f28ce` |
| Runtime description before and after | `990401523722cb6a141ca85fdfee879bed5ebbf4c3a75593a72e65e3ef450507` |
| First / second workflow trace | `46da3ebe6045cce2d24570eae2f4c15fca1eb30936bfa81a0f9184675273e5c3` / `130541a64335d3aed237f9569fda40a3cbf8bf7fa40126e91083c78cf4b92689` |
| Direct crop reread | `1658ac4b5532a2ab26188c90446b820d0ce566e37617085787d32d680cac0722` |
| Aggregate trace comparison | `a7f0432d6a1efb125b7d62800b0baa483f8c0340b5863fb91bcb03f40118811a` |

All 775 application tests passed; the forwarding self-test and two Python
comparison tests passed, and pyflakes was clean. The first diagnostic build
failed a constant-array analyzer rule; replacing the test fixture allocation
made the Release build pass with no warnings. CPU inference was used, with no
optimizer steps, sealed-payload reads, model approval, or release acceptance.
Private per-region outputs remain project-local for the orientation repair.
