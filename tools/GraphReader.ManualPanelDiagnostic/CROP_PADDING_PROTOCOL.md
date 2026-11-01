<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Exploratory OCR crop-padding comparison

Several already inspected development proposals enclose fragments of other text
near printed numeric labels. The padded candidate reader returned numeric
primaries on four such crops. This observation motivates an exploratory
comparison of the existing candidate crop settings with zero horizontal padding,
keeping the same vertical padding, model, decoder, recognizer, boxes, and
source bytes. The zero-padding choice was made after seeing the padded
development results; neither split is a blind or independent acceptance set.

Rerun both padding settings with the same frozen diagnostic binary on the 62
exposed training-holdout proposals and the 134 development proposals. Capture
must read no transcriptions, perform no optimizer steps, and open no sealed
payload. After both outputs are frozen, compare the 46 matched training boxes,
six missed labels, and 16 unmatched proposals using their existing review; also
compare the 115 transcribed development crops and preserve the remaining 19
unscored crops. Report exact readings, parsed numeric agreement, and numeric
primaries on nonnumeric or unmatched proposals. Treat unlabelled proposals as
unmatched, not automatically false positive. Do not change production padding,
review rules, model approvals, or release gates based solely on this comparison.
