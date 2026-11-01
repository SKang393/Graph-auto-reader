<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Pending edits across graph tabs

A calibration or canvas move started on one graph previously remained active
after selecting another graph. Pending calibration anchors could therefore be
combined across panels, and a pending point move could target the newly selected
panel's point. Changing the selected tab now clears pending calibration anchors
and cancels the canvas action before publishing the new selection. Selecting
the same tab leaves the action intact. The existing localized cancellation
message is shown when the workspace is not running another operation.

Two tests using separate generated images reproduced the calibration and point
move defects before the change. Afterward, the application suite passed 779
tests. The integration suite passed 170 tests with seven expected skips before
the final change from the select instruction to the existing cancellation text;
the application suite was rerun after that text change. The tests exercise the
workspace and view model, not a visual inspection of a packaged application.
No model was trained or approved by this fix.
