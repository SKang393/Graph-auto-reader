<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Adjacent-ink Y-tick review check

Some previously reviewed detector boxes contain only part of a printed number,
or fall on graph marks, while their OCR primary is numeric. An exploratory
original-pixel check found that nearby dark components immediately left of the
box can identify some of these cases. A nearby component can also be unrelated
ink or a correctly read label, so it is a review signal, not permission to
alter or remove a reading.

For an unreviewed numeric Y-tick box, inspect original grayscale pixels in the
bounded strip to its left, extending 1.5 box heights horizontally and 0.75 box
heights above and below. Use 8-connected ink at or below gray 128. A component
is relevant when its height and vertical overlap each reach half the box height,
its nearest horizontal gap is at most half the box height, and its area is at
least three pixels and three percent of the squared box height. A uniform or
oversized strip supplies no positive signal. Keep user-confirmed readings and
all OCR evidence intact.

Compare the native workflow's new warnings against all 115 previously frozen,
assistant-reviewed detector regions and against the preceding 20-image workflow
capture. Retain complete numeric, partial, graph-structure, other-text, and
uncertain classifications in their separate denominators. This rule was chosen
after inspecting these images and is not an independent validation. No optimizer,
sealed-payload read, model approval, or release gate change is authorized.
