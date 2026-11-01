<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Exploratory tick-reading consensus

This is a crop-level diagnostic on previously exposed images. Preliminary
inspection of the development measurements found that several numeric-looking
other-text fragments lie farther left than the other numeric proposals in the
same image. The following rule was chosen after those observations, so the
development result is not independent evidence for promotion.

For each frozen proposed Y-axis box, record its right-edge distance from the
selected vertical axis divided by box height, and whether its original-pixel
ink is connected to that axis at cutoff 200. For proposals with a numeric
standard-padding reading, compute the median distance among at least three
such proposals on the same image. A crop qualifies only if its standard primary
is a literal simple number, the standard and zero-horizontal-padding primaries
parse to the same finite number, its ink is not axis-connected, and its
distance is no more than 1.25 box heights beyond the within-image median. All
other cases retain review. This is a comparison signal, not a change to the
application or its current competing-alternative safeguard.

Compare this signal with the current crop-level nonliteral, positive numeric
alternative, and axis-connected review signals on 76 previously reviewed
training boxes, 62 training-holdout proposals, and 134 development proposals.
Report complete inventories, labelled correct and incorrect values, qualified
and reviewed crops, and classifications of unscored numeric-looking crops.
Preserve all unlabelled proposals and missed labels. The 16 development images
span only five studies with repeated layouts, the labels are assistant-reviewed
and incomplete, and model output on omitted crops cannot establish precision.
No optimizer, model approval, production policy, or release gate changes are
authorized by this diagnostic. No sealed payload may be opened.
