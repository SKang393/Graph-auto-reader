<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Bind cached OCR to crop-width policy, 2026-09-21

OCR result-cache keys now include both the crop-width mode and maximum crop
width. Previously, changing either setting could return a result produced under
the old setting before preprocessing checked the new request.

The retained previous runtime reproduces both defects with deterministic fake
recognition and no model inference:

- A fixed-width 128-pixel result was returned for a later dynamic-width request
  that required a 480-pixel crop.
- A successful 480-pixel result, cached under a 512-pixel limit, bypassed a
  later 256-pixel limit.

The first case now performs fresh recognition at the requested width. The
second now returns `OCR_CROP_PREPROCESS_FAILED` before recognition. The
recognition cache still uses content-based crop identities; this repair only
changes result and request-alias identities.

Validation passes **478 OCR tests**, with 16 existing optional skips, and
**486 App tests**. Both new regression cases exercise a warm cache through the
pipeline. The native harness builds with zero warnings/errors. Prior-runtime
reproduction, tests and compilation take 109.21 seconds in one serial job.
The [evidence record](GOAL-22-CROP-WIDTH-CACHE-REPAIR.json) binds exact commands,
source snapshots, prior assembly, reproduction and test results.

The [padding diagnosis](GOAL-22-CAPTION-PADDING-DIAGNOSIS.md) separately traces
the two incidental caption-repair readings to their measured input widths.
No new width or model configuration was selected. Cold-model inference was
not repeated for this cache-only repair: crop generation, models, recognition
algorithms and operating settings are unchanged. The last complete workflow
evidence remains 345/453 exact text, 666/838 markers and 12/23 CSV exports,
which still falls short of Goal 22.

No new dependency, model import, training, private/sealed read, production
approval or package occurred. Source is Apache-2.0; reviewed dependencies are
unchanged. Build 433 remains 0.4.33. Remaining accuracy and real-acceptance
work, promotion and both final 2.0.0 distributions are unfinished.
