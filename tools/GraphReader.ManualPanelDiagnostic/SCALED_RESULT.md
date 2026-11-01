<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Scale-aware connected-ink bridge

The line provider now scales its existing local ink-bridge search with the
smaller image dimension, capped at 80 pixels, while keeping its Hough line
settings unchanged. Candidate segments must still be collinear and linked by
observed original-pixel ink. The allowed detour is bounded independently, and
the existing two-pixel endpoint tolerance remains. Synthetic checks cover
filled and outlined markers, equally sized empty gaps, and an unrelated
U-shaped trace that detours too far from the axis.

On the two previously truncated panels, the recovered horizontal extents
increased from approximately 106 and 46 pixels to 1,048 and 1,042 pixels,
respectively. Both remained marked for review. On the 15 other exposed
training images, the measured Y-axis location remained within five pixels of
the same manual or assistant-reviewed reference on all 15. These checks do not
verify the entire plot geometry or independent generalization.

The existing label proposal rule and official OCR reader were then applied to
the panelized, repaired geometry on the already exposed 52-label sample. They
matched and read 35 labels exactly, missed 17, and emitted 17 unmatched boxes,
including two with numeric text. The earlier frozen full-image diagnostic read
23 exactly and missed 29, with 16 unmatched boxes. The comparison changes both
panelization and geometry, so it does not isolate the bridge's contribution.
Unmatched numeric text is not automatically a false positive. Labels were
assistant reviewed after the earlier freeze, are not independently adjudicated,
and historical or sealed source overlap is unverified. This is not a model or
product acceptance pass.

| Evidence | SHA-256 |
| --- | --- |
| Repaired panel report | `74ed5eaf7b446c3fbce0a08e9fdf2f859c1f173f94cb566fa041de2e760a6ed6` |
| Fifteen-image location comparison | `014a4a60c813fc15568448d8bed8e9af9224c793c49a75245f8d575381185c1f` |
| OCR input | `4f64d264b97443118cfcf21d40325ac62a380596da1bde6d012a661b580377c7` |
| OCR predictions | `faddd6a183735bddfba756b669516b2bf055a5bb2ec200dc03e5a6a236b50e02` |
| OCR aggregate score | `e7824e7126d137f3d3a6df13156b37270e7c4b33281823d5026528bdb0522414` |

The full solution test run passed 2,373 tests, skipped 25, and failed none.
Afterward, a finite-distance guard was moved ahead of the search-radius cast;
the final axis suite passed all 113 tests and the five-image diagnostic report
was byte-identical to the earlier report.
The OCR pass used CPU inference and performed zero optimizer steps; no model
training, sealed payload read, or production model promotion occurred. The
private images, crops, predictions, and per-case reports remain only for the
active recognition investigation.
