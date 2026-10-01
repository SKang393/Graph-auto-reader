<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Marker-like ink stays reviewable at line crossings

The [frozen diagnosis](GOAL-22-MARKER-CORE-INTERSECTION-DIAGNOSIS.json) identifies
filled marker cores that also satisfy the line-intersection geometry rule.
Intersection analysis now consumes the morphology plane already computed by
the pipeline. If its existing five-by-five mask footprint overlaps marker-like
evidence at the existing 0.5 boundary, it leaves that crossing reviewable.
It does not create a point, move pixels or classify the conflict as a marker.
The composition version advances to raster-residual-v4-marker-core-review.
Models, crop rules, confidence cutoffs and suppression distances remain fixed.

## Complete comparison

| Configuration | Native matches | Extra markers | Retained / recovered / lost truths | Exact OCR | Exports | Correct values | Correct rows |
| --- | --- | --- | --- | --- | --- | --- | --- |
| V44/English | 741 -> 740/838 | 27 -> 28 | 739 / 1 / 2 | 358/453 | 15/23 | 414/706 | 409/724 |
| V46/server | 745 -> 744/838 | 21 -> 22 | 743 / 1 / 2 | 427/453 | 16/23 | 429/706 | 416/724 |

**Native retention fails.** Each arm recovers one filled square and loses two
truths in the crowded hexagonal-marker scene. Recall falls and extras rise.
The mask shadow removed masks near thirteen previously missed truths, but
the complete workflow recovered only one. No accuracy improvement is claimed.
The guard is retained to preserve conflicting evidence for review; the
downstream losses remain open defects. Shared acceptance bars stay unchanged.

Axis geometry and OCR regions, masks and readings are preserved. Accepted
marker properties and calibration change on four native panels and nine CSV
panels per arm; marker intermediates change on ten CSV panels. One V46 warning
association follows a changed physical marker without changing the OCR region.
The [complete outcome](GOAL-22-MARKER-CORE-INTERSECTION-REPAIR.json) binds the
full field-by-field audit, including every addition and removal.

CSV totals are unchanged. Five numeric rows change in each configuration;
eighteen additional V46 rows change only x confidence. Thus 421/426 V44 and
417/440 V46 full audit rows remain identical apart from identities. All 866
output rows retain valid calibration and finite original-pixel traces.
There are still three V44 and eleven V46 wrong-phase rows, eight and seven
failed cases, and no wrong-scale, duplicate or failed-source residual rows.
One already-failed source changes its diagnostic from session 8 to session 17
and still emits no rows. These local results are not a universal safety proof.

## Verification and limitations

All 586 App tests pass, including eight new filled-core cases. Two builds have
zero warnings and errors. The first attempt passed 585 tests and failed an old
three-line crossing expectation. The existing morphology descriptor marks
that thirteen-pixel dense center as marker-like, so it now remains reviewable.
That failure and the explicit expectation change are preserved; production
source stayed byte-identical between attempts. Ordinary thin two-line crossings
remain masked, and the tests verify that source pixels remain immutable.

Successful checks took 88.484 seconds. Four fixed CPU
workflow comparisons took 845.226 seconds;
the complete audit took 4.195 seconds.
Unchanged OCR sources retain their previous 717 passing tests and 16 optional
skips; that suite was not rerun. No dependencies, weights or license terms
changed, and Apache-2.0 source notices remain intact.

Keep V44/English/V27/V5. V46/server remains closed. **Goal 22 acceptance: FAIL.**
Accuracy gates, real acceptance and final distribution remain unfinished.
There were no private/sealed reads, optimizer steps, production activation,
packaged build, tag or release. Build 433 remains 0.4.33.
