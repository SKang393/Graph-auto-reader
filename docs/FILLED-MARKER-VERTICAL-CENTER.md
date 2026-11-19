# Vertical center measurement for filled markers

A classifier can correctly accept a symbol whose center proposal is several source pixels above or below the body of the ink. A connecting line can also extend the symbol's bounding box. Both errors matter when the vertical calibration maps each pixel to a measurable change in the exported value.

After classification and exclusions, the detection workflow measures bounded vertical runs of original ink across each eligible filled marker. More than half the observed columns must support a common midpoint within one pixel. The widest supported columns must form one continuous core, and the top and bottom edges must both recede on each flank. The seed and measured center must occupy the same ink run. The midpoint uses the inclusive indices of the first and last foreground pixels. A proposed move must pass the existing plot, text, frame and stroke exclusions. Insufficient evidence retains the original proposal.

Measurement preserves X, marker identity, classification, confidence and review status. The accepted marker and projected point receive the same source-pixel location. The workflow records the measurement stage and its timing. The source raster remains immutable.

## Controls

An owned graph with plateau and sloping traces had 13 matched markers, all within one Y pixel before and after measurement. Mean absolute Y error against the nominal drawing coordinates went from 0.417 to 0.511 pixels. On an independently drawn staggered control with thicker connecting strokes, 11 of 13 markers matched before and after. The number within one Y pixel rose from 10 to 11. Maximum Y error fell from 2.607 to 0.628 pixels and mean error from 0.528 to 0.407 pixels.

Odd- and even-height raster controls exposed a half-pixel exclusive-end error in the first implementation. They now recover the specified integer and half-integer centers exactly. Controls also cover straight strokes, crossings, a triangular wedge, an open outline, competing cores, image boundaries, padded or malformed raster storage, and enhanced-image candidates.

## Nonsealed development observation

Image-only inference and assisted native outputs were frozen before independent scoring. The calibration and review inputs were held fixed across runs. One source pixel corresponded to 0.027335 Y units for scoring.

| Metric | Parent | First measurement | Inclusive pixel indices |
| --- | ---: | ---: | ---: |
| Matched / extra / missing detected centers | 12 / 3 / 4 | 12 / 3 / 4 | 12 / 3 / 4 |
| Automatic numeric rows | 0 | 0 | 0 |
| Assisted matched / extra / missing sessions | 12 / 0 / 4 | 12 / 0 / 4 | 12 / 0 / 4 |
| Assisted Y within / outside one pixel | 8 / 4 | 8 / 4 | 10 / 2 |
| Assisted mean absolute Y error | 0.023239 | 0.023563 | 0.023951 |
| Assisted maximum absolute Y error | 0.084220 | 0.084220 | 0.084220 |
| Image-only workflow elapsed, seconds | 5.97 | 6.01 | 5.36 |

All 16 reference observations had a nearby center before classification. The classifier rejected four as artifacts. The final measurement changed Y for 12 of 15 accepted candidates. The two remaining out-of-tolerance rows retained their original proposals. One X reading and three Y readings had competing interpretations, and one seeded Y label lacked an associated stroke. Image-only calibration continued to require review.

The native WPF check imported the original image, ran the production workflow, applied three manual anchors, saved and reopened the project, and exported a scoped CSV with both audit formats. Review included one horizontal move, six series reassignments, one series role change, one shared-baseline link, one explicit point acceptance and acknowledgment of 55 warnings. The run supplied no manual marker Y values or phase labels. All 15 untouched points initially saved as unreviewed and both phase labels remained unconfirmed. Each exported Y and its source pixel matched the reopened project and both audit formats. These observations cover a single exposed development graph. Phase and series correctness remain unmeasured.
