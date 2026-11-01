<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Development connectivity check

Before reviewing proposal crops, run the existing Y-label proposal rule on
the 16 study-partitioned development images and the previously captured
candidate axis geometry. Measure local connectivity to the selected vertical
axis using the existing 128 and 200 pixel cutoffs. Freeze proposal boxes and
connectivity flags before inspecting their contents. Do not train, read sealed
payloads, or change the application during this measurement.

After capture, visually classify each emitted proposal as text, graph structure,
or uncertain without displaying its connectivity flag. Report every emitted
proposal, unclassified items, missing panels, and source counts. Compare the
number of text and structure proposals with vertical-axis connectivity. The
review does not label undetected text, so it cannot measure whole-image recall.

These images were previously inspected for axis geometry and are not a blind
external acceptance set. Assistant classifications are not independently
adjudicated. Even a favorable result does not prove that connected text can be
discarded safely or approve a production model, threshold, or release.
