<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Bracket captions and unsupported arrow associations

The [caption diagnosis](GOAL-22-OVERLAPPING-CAPTION-BRACKET-DIAGNOSIS.json)
shows detector boxes containing a bracket beneath the letters. Header role
resolution now scans that lower half for the existing spanning line and two
downward hooks. Reviewed roles, explicit hints, standalone phase codes and
the corroborated heading band remain protected. Glyph recovery keeps its
existing below-box scan.

The first complete comparison exposed a separate
[arrow association defect](GOAL-22-CAPTION-ARROW-ASSOCIATION-DIAGNOSIS.json):
wide captions could suppress unrelated plotted ink solely through proximity.
An arrow's backward tail ray must now intersect the actual annotation box
before nearest-label selection. Both composition versions enter the cache ID.

## Results and failed retention

The frozen original-pixel replay covers 154 observations. All 15 old arrow
associations fail both the axis-aligned ray and a ray measured through the
terminal ink centroids. Twelve already existed in the retained baseline;
three appeared after correcting caption roles. These are unsupported masks,
not slanted arrows. The complete delta audit confines physical changes to
the twelve affected retained panels, with no unexplained changes elsewhere.

| Configuration | Correct roles | Exact OCR | Native markers | Extra markers | CSV exports | Correct values | Correct rows |
| --- | --- | --- | --- | --- | --- | --- | --- |
| V44/English | 370 -> 376 | 358/453 | 742 -> 741/838 | 26 -> 27 | 15/23 | 414/706 | 409/724 |
| V46/server | 412 -> 420 | 427/453 | 746 -> 745/838 | 20 -> 21 | 16/23 | 429/706 | 416/724 |

Every prior exact text reading remains correct. **Native retention fails:**
each arm loses the same crowded hexagonal-marker truth and gains no truth.
The corrected association is retained because restoring unsupported masking
would conceal the defect. This is not a model-gate pass or promotion.

Physical marker output and calibration change on five native and two CSV
panels in V44, and four native and one CSV panel in V46. Nine V44 and five V46
numeric export rows change; full audit rows change in nine and twenty-four
rows respectively, including confidence changes. All 866 exported rows
remain finite, traced to original pixels and validly calibrated. Aggregate
CSV scores and case statuses are unchanged. Three V44 and eleven V46
wrong-phase rows remain; eight V44 and seven V46 cases still fail. The sample
has zero wrong-scale, duplicate and failed-source residual rows. These counts
are not a universal safety proof.

Both initial caption-only invariant audits failed. Their source and failures
remain frozen. The later combined audit records all physical and calibration
changes from the separately diagnosed mask defect. No acceptance bar was
weakened, and no failed accuracy result was converted into a pass. The
[complete outcome](GOAL-22-OVERLAPPING-CAPTION-BRACKET-REPAIR.json) binds the
full results, diagnoses, source snapshots, failed attempts and final audit.

## Verification

The current sources have 696 passing OCR tests, 16 optional OCR skips and
578 passing App tests. Twelve added OCR cases cover overlapping brackets and
role protections; sixteen raster cases cover aligned, perpendicular,
beyond-head and competing annotations in four orientations. Input pixels
remain immutable. The original OCR/App checks and three clean builds took
143.589 seconds. The arrow correction's App
tests and two clean builds took 123.083 seconds;
unchanged OCR results were reused after exact source-hash verification.

The four final CPU workflow comparisons took
1110.180 seconds. The earlier 970.168-second
caption-only comparison and its failed preservation audit are retained.
Direction replay took 22.527 seconds total, including 8.981 seconds of replay,
without model inference. Local helpers and commands are bound in the JSON.

Source and tests retain Apache-2.0 headers. Models, dependencies, licenses and
thresholds are unchanged. Keep V44/English/V27/V5; V46/server stays closed.
**Goal 22 acceptance: FAIL.** OCR, marker, export, real acceptance and release
work remain. No private/sealed reads, optimizer, production activation,
packaged build, tag or release occurred. Build 433 stays 0.4.33.
