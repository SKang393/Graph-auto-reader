<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Keep recognized text out of plotted data

The mask-preserving marker model receives OCR masks as input, but can still
predict centers inside recognized text. The classifier then accepted some of
those centers as marker shapes. Calibration treated them as data columns and
correctly refused the resulting inconsistent spacing.

The workflow now excludes centers inside nonempty, non-rejected OCR polygons
before calibration, grouping and point projection. It uses the actual polygon,
not an expanded rectangle. Original classifier probabilities are unchanged.
Excluded detections remain rejected marker records with OCR-region provenance;
they cannot enter exported points. Legend symbol crops remain separate evidence.
Both production and candidate paths share the rule. No model threshold or
calibration tolerance changes.

All 340 application tests pass with no skips, covering rotated text polygons,
rejected/empty text, role independence, cancellation, coordinate validation and
candidate calibration through export. The x64 diagnostic tool builds with zero
warnings/errors. The fictitious private-runner self-test passes. An initial
test-only analyzer error is retained; no test ran until it was corrected.

Actual inference repeats the complete 23-source synthetic inventory. On all
24 previously visited panels, OCR is identical and every non-text accepted
center is unchanged. Exactly 23 previously accepted text centers are removed;
none is within the fixed five-pixel tolerance of a true data point. Processing
reaches one additional panel. Eleven of 25 visited calibrations are valid,
compared with four of 24 before the repair. This is not a full-panel recall score.

| Complete workflow metric | Before | After |
| --- | ---: | ---: |
| Sources that export | 1 / 23 | 3 / 23 |
| Correct unique exported points | 1 / 706 | 3 / 706 |
| Missing unique points | 705 | 703 |
| Extra unique points under series matching | 1 | 3 |
| Correct relational rows | 0 / 724 | 0 / 724 |

All final files pass integrity checks. The full denominator remains 39 physical
panels, 41 series and 706 points. Incorrect splitting of series still makes
some geometrically correct detections extra under whole-series matching.
Twenty sources still fail. This is a verified integration repair and failed
development acceptance, not a production pass.

Next: repair printed-session export's handling of explicitly estimated x values.
The existing contract already stores estimated values and an estimated audit
source, but export currently demands a literal printed value on every point.
Keep calibration, first-session and unknown-value safeguards unchanged. Remaining
missing/misread ticks, panel geometry, marker and grouping failures stay open.

The [evidence record](GOAL-22-TEXT-MARKER-EXCLUSION.json) binds source snapshots,
runtime, immutable input identities, per-stage comparison and complete scoring.
No training, private/sealed read, new model revision, activation, package or
release occurs. Goal 22 remains incomplete; portable build 433 is still 0.4.33.
