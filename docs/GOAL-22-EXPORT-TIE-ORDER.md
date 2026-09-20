<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Repeatable equal-session export order, September 20, 2026

The participant workflow comparison exposed two equal-session rows changing
order between runs. Export sorted the tie by generated point IDs. Independent
fixtures confirmed this also changed the values associated with sequential
observation numbers in observation-order mode.

The exporter now uses measured original-pixel x/y and scientific row content
before generated identifiers when phase, x and observation index are equal.
It preserves all points, values, phases, memberships and audit provenance.
It does not merge or discard conflicting same-session observations.

Four independent cases failed before the repair. They cover both export modes
and ties within one series or between a target and its applicable probe. After
the repair, all 36 export tests and 373 application tests pass, without skips.
The tests change point and series IDs and input enumeration, require identical
minimal CSV contents/hashes, and verify every retained audit point and owner.
Compilation passes with production warnings treated as errors. The initial
test-only analyzer error is retained in the evidence.

The [evidence record](GOAL-22-EXPORT-TIE-ORDER.json) binds the source snapshots,
tests and original workflow comparison. No model inference was repeated for
this serialization-only change. The preceding 23-source workflow still has
11 completed sources and 12 failures, with 226/706 correct unique values and
218/724 correct relational rows. Series fragmentation remains the next defect.

No training, private/sealed read, new model revision, activation or package
occurred. Build 433 remains 0.4.33. Goal 22 remains incomplete.
