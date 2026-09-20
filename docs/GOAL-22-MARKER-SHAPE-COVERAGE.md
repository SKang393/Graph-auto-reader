<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Marker shape coverage: failed development candidate

The owned marker-center training population lacked triangles, diamonds and
stars. On the unchanged development graph, five of twelve open triangles had
eligible, accurately localized proposals below the existing confidence threshold.
The [diagnosis](GOAL-22-MARKER-SHAPE-COVERAGE-DIAGNOSIS.json) records this gap.

V28 P1 warm-starts V27 on all 44,891 existing training rows plus 42,747 rows
from 2,160 owned shape rasters. It preserves the model, native input, threshold,
loss coefficients and all 176 development scenes with 2,210 truth points.
Eight fixed epochs complete 5,480 optimizer steps in 809.39 seconds, including
export and development evaluation. The final epoch is used without selection.

| Unchanged development population | V27 | V28 |
| --- | ---: | ---: |
| Component precision | 89.12% | 90.95% |
| Component recall | 89.47% | 94.26% |
| Full-graph family precision | 83.33% | 67.91% |
| Full-graph family recall | 92.23% | 97.57% |
| Full-graph false detections | 38 | 95 |

Both models use the explicitly bound balanced geometry and threshold 0.25 in
this comparison. The candidate fails the unchanged 95% precision and recall
requirements. Its family prohibited-structure hit rate is 2.027%, above 2%.
Higher recall alone is insufficient.

The complete graph comparison also regresses. With all other weights, runtime,
inputs and truth fixed, exporting sources fall from 13 to 8 of 23, correct
unique values from 360 to 121 of 706, and correct relational rows from 352 to
118 of 724. Runtime is 127.92 seconds. All 33 prior observed panels remain,
with 34 reached. Extra detections correctly trigger export guards. Seven extra
points and 585 missing truth points remain counted. Zero wrong-scale rows in
the surviving exports does not establish complete accuracy.

Validation passes 27 distinct checks. ONNX and Torch agree across all 270,236
development proposals: maximum error 0.0000057221, with zero operating-decision
changes. The old-model preflight reproduces the complete historical counts.
Completed guarded work/rest cycles remain below 80% duty, with all twelve
logical processors eligible. This is an average duty limit, not an instantaneous
per-core guarantee.

One registered pretraining plumbing failure is void, with zero optimizer steps
and no sealed read. Its split-label repair preserves every cached data byte.
The unsupported visible hollow-square training truth is retained explicitly;
no positive or scientific value is invented. A preflight matcher-wrapper error
is repaired before candidate acquisition. Original records remain available.

The [outcome and evidence](../ml/markers/center/shape_coverage_v28/P1_RESULT.json)
close this revision as **failed development, unconsumed**. Do not activate or
promote it. Diagnose the saved false detections and training background coverage
before another adaptation. Continue the independent panel repair with V27 fixed.

Code, weights and synthetic data remain project-owned Apache-2.0. No dependency,
private/sealed read, production approval, package or release occurs. Build 433
remains 0.4.33. All four Goal 22 outcomes remain incomplete.
