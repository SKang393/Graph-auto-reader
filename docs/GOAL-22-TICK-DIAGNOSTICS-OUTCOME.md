# Goal 22 tick-lane diagnostics outcome

**Status:** synthetic diagnostic only. No production approval or release eligibility.

## Result

The V13 run completed **1 of 21** synthetic sources and failed calibration on 20. It observed **31 panels**. Of those, 14 had 21 usable x-axis strokes in total. Tick-lane diagnostics examined 280 connected components; all 280 were rejected by the combined seed-bounds predicate, and **no seed crops were proposed**.

The failing calibration remains unresolved. No component reached tick-stroke association or OCR recognition, so this evidence does not show whether the bounds predicate or upstream component selection should change. The 20 failure messages reduce to: 15 without an absolute session-1 origin and only ordinal x evidence; 2 with an additional YTick alternative review; and one each mapping the first observed column to sessions 29, 8, or 2 instead of session 1.

V13 matches V10’s **1/21** completion count under the same source-scale OCR composition. V12’s **0/21** is non-comparable: its binding mistakenly used `graph-structure-consensus-v1` / `model_polygon`, rather than V10/V13’s `original-db-head-source-scale-windows-v1` / `original_pixel_context_regions`. Do not treat V12 as a measured regression.

## Provenance

- Source commit/tree: `4c7690f52bc18d7a3d66e04b2be93e6c6d73e854` / `8e7199f04457f3a169f74b2bb62261411fde2a78`
- Candidate binding: `58c7ace046f5abc94bd8dc7624dfbe7464137e4e7025862f5d990f228eaa25c5`
- Input manifest: `4d113841b0905fede9fe558aca7f1642cd2585d29bb8046f6720137ee1caa9b2`; frozen synthetic DEV split, 21 sources
- Protocol: `55448a8db5bde6fb59e9d876bcc624438120c4ffe6611b5f0273cce1c62d147e`; 21 CSV-run maximum, zero optimizer steps
- Runtime source inventory: `689a504e793171799a2414c72ae973db84f87de7c81e8208f3a4e4b7b3485471` (291 files); test/evaluator inventory: `51d61bf1dd1c49cf7b69e57a2890f75f555b7bf9515d7b5f6082fc92bd566fc9` (366 files)
- Runner DLL: `8ecce5033abc82211467c61c1076aaffdfd6e8d49ac7c3a7772be9a1dbd7015d`; preflight receipt: `b4a7e343ff738a77b8f3f71539e67b9ffed97af2b599476b4780e3cc56546c98`
- V13 report: `43949aca7a1dcefa07407f501d29b4a83974ce314d631db1682d15c907ef5781`; tick-lane index: `690eef7f79f6e17f3ccc851a8caf1a332dd21e0f4fce6438ca0ae1f2f01a4607`
- Runtime: CPU, graph optimization disabled, 10 intra-op and 1 inter-op thread. Elapsed: 261706.3 ms.
- Models: OCR detection `497a3527820ce0359496084e5afb1e0d3241aca79e192187f7ab737b27818b7f`; OCR recognition `0980049401eceb506952f769765a6b95be07cb1197dcbb13c93f107a328f8418`; marker center `4979777a404be48c6c75b490218d1de31d2edb2550f5bc71c7aa6894c1bcca04`.
- Privacy/approval: private reads 0; sealed reads 0; truth not consumed by inference; training 0; production approved false.

Exact runner arguments are preserved in `GOAL-22-TICK-DIAGNOSTICS-OUTCOME.json` and the temporary V13 preparation receipt. The run report and raw diagnostic artifacts remain temporary; retire them after the record is pushed and active consumers finish. Their paths are not promised to persist.
