<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# User-confirmed first-visible session

Manual calibration can now use a user-entered first-visible session greater
than one and a later final session. The user supplies these values when
confirming the three anchors. The application preserves distinct first-visible
anchor kinds and a source/panel/calibration-bound confirmation record. Session
values obtained from those anchors remain estimated, not printed OCR readings.
The export audit records the explicit origin override and uses the first
observation among the series actually exported. Production export refreshes
that value after point edits.

Release checks on October 30, 2026:

- App suite: 696 passed. Domain suite: 23 passed.
- Manual-preview, production-workflow, and multi-panel integration selection:
  45 passed. Coverage includes 29-to-35 anchors, preserved gaps, save/reopen,
  a changed receipt, an unrelated series, and export after deleting the first
  detected point.
- A native manual-preview window accepted synthetic 29-to-35 anchors and
  displayed session 31 for a placed test point. This does not establish the
  accuracy of automatic recognition on real documents.

Existing session-1 projects remain supported. Projects saved with the new
first-visible anchor kinds can be unreadable by older application versions;
keep the original project before opening them with an older build. The receipt
uses an unkeyed consistency digest, which detects tested stale changes but is
not an authenticity signature. Automatic-origin acceptance is unchanged.
No model approval, installer, or new portable release was produced here.
