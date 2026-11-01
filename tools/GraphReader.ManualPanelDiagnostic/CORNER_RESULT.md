<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Connected axis-corner repair

The OpenCV line provider can now connect both observed axis segments to a
covered corner when at least one segment is long, both endpoint gaps are within
the existing bounded distance, and the original raster contains a connected
ink path to the corner from each missing endpoint, subject to the existing
two-pixel endpoint tolerance. The paired empty-gap fixture remains disconnected.
This extends the existing corner-bridge behavior; it does not change the line
detector's default thresholds or approve an automatic model.

On the already exposed five-image training holdout, both panels of one source
previously chose visibly misplaced Y-axis origins. After the repair, both chose
the visible Y-axis origins. Their reported horizontal extents were still only
about 106 and 46 pixels on panels 1,634 pixels wide, so both remained marked for
review. Two other images still selected misplaced axes. The change does not
establish complete automatic recognition.

The eight earlier build images retained 8/8 vertical-axis locations within
five pixels of their manual calibration at the evaluated Y coordinate. Seven
additional images retained 7/7 within five pixels of the assistant's selected
visual reference columns; that review is described in `TICK_PROBE_RESULT.md`.
These checks measure only one axis location per image, use previously exposed
training sources, and do not verify full plot bounds or scientific data.

The pre-change panel report SHA-256 is
`ed9d593561370483efe5c74bb64843989784130b9035760b9b742f97e1b8a4b2`,
the post-change report is
`d4a6fcd822765214daa160735a7e2cbd445979e89d9d22eb68d669b29627fc79`,
and the comparison report is
`3ceeb613b9a844429740464bc3e5c79dea2453ed7f02ce1553c790c1a4b83d86`.
The two targeted regression cases initially failed before their respective
repairs; the axis suite then passed all 108 tests, including connected and
empty-corner cases. A full solution run passed 2,368 tests with 25 skipped and
no failures. No optimizer training or sealed payload read occurred.
Private reports and image crops remain for the active axis investigation and
will be retired when it finishes.

An earlier diagnostic-only wide profile changed the Hough minimum length and
its derived ink-bridge radius together. It did not recover complete horizontal
axes on the two target panels and was not integrated. Its report SHA-256 is
`be02cb707793921804951a51115fa4ddd5e9de31595fa2eb8c0c3cf55cbbfadb`.
Combining that profile with the corner repair subsequently recovered horizontal
extents of about 1,049 and 1,042 pixels on the two target panels. On another
image it selected the visible vertical origin but retained only about 192
pixels of the horizontal axis. Its report SHA-256 is
`2158fb9648fd7dfb967fd26fafcf0e19cf41e70a83f07a0e07970f1315feb851`.
The two changed provider parameters have not been isolated or validated for
production, so the application continues to use its existing defaults.
