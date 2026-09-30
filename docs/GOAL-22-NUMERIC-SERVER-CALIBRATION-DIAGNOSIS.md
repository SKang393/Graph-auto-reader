# Numeric server-reader calibration diagnosis

The larger server reader improves saved native tick readings, but numeric-only
substitution does not unlock another CSV source. Keep the current OCR runtime.
This diagnostic does not establish a three-model application composition or
improved CSV accuracy.

## Method

Use the current `b1b6417` application runtime and saved owned synthetic outputs.
Select only unreviewed tick regions with identical region identity, polygon and
role in both readers. The server primary must be a strict decimal with a
matching recognition alternative at the existing 0.65 confidence minimum.
Selection occurs before truth scoring. Preserve words, axes, accepted markers,
OCR masks and every existing review warning.

Replay the unchanged calibration first for each of 31 native and 39 CSV panels.
All 70 controls reproduce exactly apart from elapsed time. Then replay the
selected OCR through the same tick geometry and calibration code. One missing
saved server calibration panel remains in the comparison with baseline OCR.

## Results

| Measurement | Baseline | Numeric substitution |
| --- | ---: | ---: |
| Native exact text, all 453 truths | 348 | 365 |
| Native correct roles | 358 | 358 |
| Complete native panel calibrations, 31 panels | 0 | 1 |
| Complete CSV panel calibrations, 39 panels | 24 | 24 |
| CSV sources with all calibrations complete, 23 sources | 12 | 12 |

There are 85 eligible native regions and 312 eligible CSV regions. Text changes
in 17 and nine regions respectively. Confidence and alternatives also change:
27 native and 36 CSV calibration records differ, despite largely unchanged
completion status. Among the 24 previously complete panels, session assignments
remain identical. Applying the old and new transforms to held marker pixels
changes graph x by at most 0.000837 and graph y by at most 0.573028. These are
differences, not truth accuracy or observed exports.

The replay takes 64.216 seconds, including a clean build with zero warnings or
errors. Current-output OCR scoring takes 1.157 seconds and verifies that its
selected OCR exactly matches the calibration replay inputs. No model inference,
optimizer steps, private data or sealed data are used.

The first preparation attempt was void: historical authentication required a
completed calibration for every raw OCR panel, while one saved server panel
had none. The repaired preparation authenticates the two inventories separately
and retains that baseline panel. It does not remove a failed source or truth.

## Disposition and evidence

Defer runtime integration. Numeric readings improve on the native diagnostic,
but CSV calibration eligibility does not, and confidence substitutions alter
otherwise complete fits. A later composition still requires explicit provenance
for both readers and full workflow validation. No review warning is cleared.

The [machine-readable record](GOAL-22-NUMERIC-SERVER-CALIBRATION-DIAGNOSIS.json)
binds all saved reports, replay inputs, runtime files and analysis sources.
Only previously reviewed Apache-2.0 models and owned code are involved. No
dependency, production switch, packaged build or release changes. Build 433
remains 0.4.33; Goal 22 and accuracy acceptance remain incomplete.
