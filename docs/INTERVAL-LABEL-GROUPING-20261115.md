<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Interval-centered X-label grouping

## Failure mode and scoped change

The cached component-only diagnosis found a row with **38 components**: **10 of
19** midpoint cuts crossed glyph ink, while **zero** measured-tick columns were
covered and **zero** overlapping components reached the baseline. This is
consistent with interval-centered labels defeating the existing tick-centered
split, not with raster axis ink. See [the cached X-label diagnosis](X-LABEL-COMPONENT-DIAGNOSIS-20261115.md).

`ConnectedComponentTextRegionDetector.GroupIntoLines` still uses midpoint cuts
and crossing-ink vetoes. After the ordinary row grouping, the new conservative
interval path considers only original members of that exact row. It requires
identity pixel coordinates, a valid measured X-tick context, at least three
components, and members strictly inside at least three distinct measured-tick
intervals. A member on or beyond an interval boundary, insufficient interval
coverage, a narrow gap between adjacent groups, or insufficient vertical
overlap rejects the whole split. Eligible groups keep their original pixel
boxes and receive `x_label_between_measured_ticks` provenance. Measured tick
coordinates are unchanged.

Calibration treats those OCR rows as estimated positions at each label's own
pixel center, not as the nearest edge tick. The flag is accepted only for X
rows, when the request's measured X ticks exactly match the fitted geometry and
the label box lies within one interval. Raw OCR text/value and measured Y ticks
remain intact. The draft displays its estimated-position warning and still
requires the existing explicit review before Apply; Undo remains available.
No scientific value is fabricated or silently applied.

## Owned fixture and guards

The owned 160x100 Gray8 fixture has four measured ticks, a connected baseline,
three fragmented digit/decimal-shaped label boxes centered at pixels 41, 61,
and 81 inside successive intervals, and a tick-centered control. OCR tests assert
the three interval boxes and their provenance. Ordinary tick-centered labels,
a dense single 14x8 number, fewer than three intervals, a boundary-crossing
member, crossing ink, digits around a measured tick, and nonidentity pixel
coordinates remain unmarked or unsplit. Both source buffers remain unchanged.

The App regression preserves four measured X ticks, three original OCR labels
and numeric values, and unchanged measured Y ticks. It verifies estimated
positions 40, 80, and 120 are not snapped to any measured X tick; Apply is
disabled until explicit review, then the applied calibration can be undone.

## Validation

The first OCR run had **0 passed / 1 failed** in 153 ms (24.3976695 seconds
build-plus-run). The failure was a mistaken test assumption that dense fragmented
14x8 labels were already separated by the ordinary tick-centered path. The
control was corrected to simple 3x8 tick-centered components; the interval
fixture remained unchanged.

Final OCR suite: **926 passed, 16 asset skips, 0 failed**, 9 seconds reported
test time / 24.443091 seconds wall; TRX SHA-256
`7671dbc3a0e97db5c28239fc75ee84f3e6149358c520ac0183fcbc59f58a158c`.

The first App test build reported CA1861 twice and MSTEST0032 three times in the
test files (58.2395824 seconds wall). The static-array and
constant-assertion issues were corrected. Retry: zero warnings/errors in 12.0016053 seconds.
Full App suite: **1,207 passed, 0 skipped, 0 failed**, 45 seconds reported test
time / 49.1906291 seconds wall; TRX SHA-256
`ac1e205dad3384739f44e90da3dd11011f91f5e7df5fba1095ef2cff26711d2f`.

Source base: `11196b00d4afa18c208700ddae456d4236c74b40`. The independently
documented native-capacity limit remains in
[NATIVE-CAPACITY-256-20261115.md](NATIVE-CAPACITY-256-20261115.md).

No model, dependency, threshold, frozen project schema, or public contract was
changed. These owned fixtures and automated tests do not establish visual UI
acceptance or broad accuracy. No private text, labels, or coordinates are
included here.

## Fixed development confirmation: no observed improvement

The separate `--real-dev-interval-label-trace` profile combines this change with
the normal native worker's 256-pair bound. It reuses the unchanged first 12
development sources and baseline pipeline. One confirmation ran from
`2026-11-15T19:27:53.2901528Z` to `2026-11-15T19:28:40.8766524Z`, exit 0,
empty stderr, **47.577568 seconds** wall. All 12 sources and panels finished:
11 prepared panels and one genuine no-panel fallback; zero timeouts,
exceptions, unsupported dimensions, unfinished or unattempted panels.

| Stage | Endpoint baseline | Interval/capacity runtime |
| --- | ---: | ---: |
| Geometry and OCR returned | 8 | 8 |
| No geometry | 4 | 4 |
| Native pair-budget refusals | 3 | 3 |
| Rotated geometry refusal | 1 | 1 |
| OCR regions / numeric rows | 128 / 79 | 128 / 79 |
| Associated / estimated rows | 70 / 5 | 70 / 5 |
| Interval-provenance rows | 0 | 0 |
| Complete session drafts | 0 | 0 |
| Printed-X drafts | 2 | 2 |
| Partial X / Y drafts | 0 / 3 | 0 / 3 |

All panel geometry, OCR rows and association reasons matched the saved
endpoint baseline after excluding the new false provenance field. Exact-box
measurement was attempted once and bound 20 X / 7 Y ticks in 64.2411 ms;
ambiguity and multiple-candidate warnings remained. No calibration was applied
or exported. This is a negative development result, not an accuracy pass.

| Stage time, milliseconds | Baseline | Current |
| --- | ---: | ---: |
| Preparation total | 1,955.529 | 2,644.010 |
| Axis total | 10,614.0975 | 15,913.8041 |
| OCR total | 23,646.2635 | 27,368.2276 |
| Association total | 66.4761 | 89.6767 |

Current OCR median was 3,032.9589 ms and maximum 7,053.175 ms. Single ordered
runs include scheduling effects and increased bounded-search work; no latency
improvement is claimed. Increasing capacity again based on these failures is
not the next step. The remaining geometry work needs complete-search diagnosis.

The cached component-property replay suggests that two of 18 neighboring gaps
equal, rather than exceed, the current glyph-width guard. All 38 overlapping
boxes fit 19 occupied intervals. This is only an approximation: the cache did
not preserve exact merged-row membership. It does not justify relaxing the
guard or claim to identify the actual rejection predicate. No threshold was
changed and no private recognition was repeated for that inspection.

## Runtime and failure accounting

The probe's initial build failed CA1859 for an `object` return type in its new
provenance wrapper, before any private reads: 6.8746976 seconds wall. Changing
the return type to its concrete dictionary passed, zero warnings/errors,
8.3502917 seconds wall. The owned profile self-test passed in 0.4290345 seconds;
the missing-argument check correctly refused in 0.1926202 seconds, exit 1.
All 20 probe runtime libraries were matched to the frozen App before execution.

| Identity | SHA-256 |
| --- | --- |
| App EXE | `528db47ea435ca63834a8175bd0b660202742e211b3e7983bad1d6e4cd4b3b92` |
| App DLL | `d932a86015c0fe3a1d8b50a15ec5f5d8ceebea5d36e9f8b2f3f2b25b0a120f43` |
| Probe DLL | `f17e269a7f1dee1c293abdf38d320d938f4b446126df5d1dfec342f007c838d4` |
| Rust worker | `2cc6f263a00544243291f2605031c4df7e147175bd345480bd0a44f92fde0b7a` |
| Runtime pin manifest | `01614dd862d032225d715e21e7540725d877b50ab4e2463531bc2027029e3115` |
| Current report, 69,617 bytes | `ca6261ffd33061661a0c8e150861cc7ba76c324d37c5b1a39ee794869ce18755` |
| Saved baseline report | `c052507d9185b7f1132c36d551592aa5b24103c5277ea3188ba795dfbfc30e87` |
| Assignment | `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a` |
| Selected inventory | `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d` |
| Combined source digest | `4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1` |

Assignment, inventory and combined source digest matched the baseline. The
existing 120-development / 51-reserved assignment was preserved. Earlier
171-source inventory exposure means this record does not assert an independently
audited pristine holdout. Sealed payload reads, truth scoring, training, image
writes, paid calls and model selection were zero. Source is Apache-2.0 with no
new dependency. The full trace uses the existing materializer, not the separate
image-only streaming property reader.

Commands used the documented probe mode plus `--explicit-opt-in`; tests used
the full OCR project with `--no-restore` and full App project with
`--no-build --no-restore`. Current raw reports remain local only through the
specific follow-up geometry/grouping diagnosis and pushed outcome. Test reports
and completed owned capacity outputs are disposable after their result records
are pushed. The sole delivered package remains 0.4.54; no new package or model
promotion was performed. Full CI for this source batch remains pending.
