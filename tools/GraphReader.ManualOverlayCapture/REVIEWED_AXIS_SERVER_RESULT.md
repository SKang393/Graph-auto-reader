<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Server OCR on reviewed training-axis crops

The diagnostic ran the current unapproved candidate's pinned server recognizer
on 76 previously selected label boxes from eight authorized training images.
The input contained image hashes and crop boxes, but no transcriptions. The
production raster decoder, cropper, and standard recognizer options were used;
the run did not execute text detection or the full workflow. Labels were
assistant-reviewed before this comparison and were not independently
adjudicated. The images and labels were already exposed during development.

All 76 primary transcriptions matched their reviewed text exactly. All 76 were
numeric. Fifty-two had a different numeric alternative with positive
confidence, which is the condition currently treated as a review conflict for
an unreviewed tick in the workflow. At alternative-confidence cutoffs of
0.001 and 0.01, 12 and two of these correct primaries respectively still had a
competing numeric alternative above the cutoff. These counts are descriptive;
the alternatives' confidence has not been calibrated as a safe threshold.

This set contains no wrong primary reads and therefore cannot establish how
many incorrect values a cutoff would permit. The separate dense-tick diagnostic
did contain wrong readings, including a nonnumeric primary. These selected-box
results do not establish automatic localization, axis calibration, precision
over unlabelled image content, model approval, or production readiness.

| Private evidence | SHA-256 |
| --- | --- |
| Truth-blind crop input | `06d77e4621e536ea3328e6baed894d2a8f449bc5d6c2534df0c0f31dba2b8d81` |
| Reviewed label join | `bb2a2f113a3e42904367c408e145b59538302b9f056d45ce8d217d85d9efc7be` |
| Runtime description before and after | `54297a130a04ca733624a5f75b455c523a9c25a620c0f82e06f25959d42e1008` |
| Frozen predictions | `417a7603480cf618ad3043a1e201f9cf0ecb8ebbbf7c618cfff1c9ed8bd76e50` |
| Aggregate score | `f4a4bba2f141502a07e1547b08549a57d0a32da704458b43f7c380852c8a6f06` |

The diagnostic Release build passed with zero warnings. The first build
failed because the new file omitted the application integration namespace;
adding that import fixed it. Two scorer tests and pyflakes passed. The capture
used CPU inference, performed no optimizer steps, read no sealed payloads, and
took about ten seconds. Private source images, labels, and per-crop readings
remain project-local for the active recognition investigation.

## Previously exposed training-holdout proposals

The same reader then processed 62 pre-existing tick-aligned Y-label proposals
from five other training images. Of 52 assistant-reviewed labels, 46 had a
proposal with IoU at least 0.5 and all 46 primary readings matched the label
exactly; six labels had no matching proposal. Thirty of the 46 correct numeric
readings had a competing numeric alternative with positive confidence.

Sixteen proposals did not match the reviewed labels. Seven of those produced
text that the graph-number parser interpreted as numeric. An assistant
inspected the seven original-pixel crops after inference: two were portions of
markers, one was a boxed symbol, and four were axis strokes or ticks. This
visual classification is not independently adjudicated. The seven examples
show why a numeric-looking OCR result alone cannot establish a text label, and
why the unreviewed ambiguity guard was not relaxed.

| Private evidence | SHA-256 |
| --- | --- |
| Frozen proposal input | `469b5ee37f1d2ad80b4dcb638a3d2183c4949e003de3322d9a186e140228779a` |
| Proposal-to-source join | `537684e62452558a6370980e761f9b92e488959ce691b467f73835f3545f412d` |
| Reviewed labels | `0d80b0663457f25a89e7d39a0e60f5e156604a0d382dcb9e8f132f7c29331713` |
| Runtime description before and after | `9498e565bbd2d72e86076c7fb6a74b7e940b9210334ec0597939887e534e0163` |
| Frozen server predictions | `314e6698241d34f9c11b6076c22d75d720dc99046849ea70c526d6ceb714ca37` |
| Coverage and recognition score | `c1bdec7a9c67e954362f043a8e43245827449a58e05996b8db7f61a8a00dc260` |
| Private visual review sheet | `f2cdad3410c7fc4e241741fcdd201af060ce2578c8da097bf12d04736547c716` |

The proposal rule and these five images had already been inspected during
development; this is not independent or sealed acceptance. Unmatched proposals
outside the seven visually reviewed cases remain unclassified. The run used
CPU inference with no optimizer or sealed-data reads. The diagnostic built with
zero warnings, and four scorer tests plus pyflakes passed.
