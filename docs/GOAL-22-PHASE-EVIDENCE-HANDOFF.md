<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Goal 22: phase evidence handoff

The repair corrects 26 phase assignments without changing any exported point
location or numeric value. It does not complete Goal 22.

Two integration defects were visible in saved owned train/dev evidence. A real
divider with 2.0086 pixels of horizontal drift passed the axis geometry stage,
then disappeared under the phase module's unrelated two-pixel raw-segment
default. Elsewhere, text and a marker were joined into a false divider. The
existing pixel-support check ran only when a candidate crossed a legend frame.

The application now checks every measured divider against original pixels,
excluding recognized text, legend frames and accepted marker bounds. It reuses
the axis stage's span, coverage and positional tolerances. Heading layout can
still resolve an existing ambiguous line, but cannot create one. The phase
request inherits the existing axis angular allowance scaled to plot height.
Measured endpoints remain unchanged; the standalone phase module retains its
two-pixel default. Rejected geometry remains visible in audit warnings.

## Same inputs, unchanged models

All 23 contextual synthetic source PNGs, model payloads and model operating
thresholds are unchanged. Truth remains isolated in the CSV evaluator.

| Full workflow result | Before | After |
| --- | ---: | ---: |
| Completed sources | 14 / 23 | 14 / 23 |
| Correct unique point values | 287 / 706 | 287 / 706 |
| Correct relational export rows | 251 / 724 | 277 / 724 |
| Wrong matched phase labels | 31 | 5 |
| Missing unique points | 419 | 419 |
| Extra unique points | 46 | 46 |

The run takes 173.69 seconds. All 30 visited panels retain identical raw axes,
OCR readings, accepted markers and calibration evidence. All 333 exported
unique point locations and numeric/provenance values are preserved. All 348
actual rows remain counted. Artifact integrity passes with no duplicate or
unexpected rows. Source completion and all nine failure messages are unchanged.

## Verification and limits

All **388 application tests pass**, including tilted solid, dashed and dotted
dividers, absent pixel support, text/marker exclusion, original coordinates,
cancellation and actual phase-to-export integration. The x64 tool builds with
zero warnings/errors. Both the fictitious private-runner boundary check and
workflow/evaluator self-test pass without private or sealed reads.

Initial checks exposed test fixtures that declared measured lines on blank
rasters. The fixtures now draw those lines; assertions were retained. The two
failed application checks and the separate failed tool self-test are preserved.
The tool's fixture-only repair followed the full workflow compilation. Its
final self-test has its own source and assembly binding; the actual inference
uses the earlier frozen runtime. All production source bytes match that runtime
snapshot. Different build-directory assemblies are not claimed byte-identical.
No model inference was repeated for this fixture-only repair.

Commands: `dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj
-c Release`; x64 build of `tools/GraphReader.RealAcceptance.Ocr`; its
`--self-test-real-workflow-runner`, `--self-test`, authenticated
`--run-frozen-candidate-synthetic` and `--score-frozen-workflow-csv` routes.
The [evidence record](GOAL-22-PHASE-EVIDENCE-HANDOFF.json) binds exact arguments,
source snapshots, reports and runtime hashes.

The remaining five phase errors involve unclear OCR. Nine graphs remain
blocked by calibration evidence, including missing ticks and missing first
markers. Classifier coverage and detector misses also remain. These are
retained failures, not permission to weaken scientific safeguards.

All changes are original Apache-2.0 code using existing dependencies. No
training, formal model revision, private/sealed read, model activation, package,
tag or release occurred. Build 433 remains 0.4.33. Next: trace the remaining
calibration and OCR failures and implement the separately defined classifier
coverage repair. All four Goal 22 outcomes remain incomplete.
