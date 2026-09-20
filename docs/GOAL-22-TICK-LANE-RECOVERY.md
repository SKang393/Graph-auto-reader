<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recover missed axis labels from original pixels

The unapproved OCR candidate can now find a missed text crop along an already
observed axis-label row. At least three literal numeric readings must establish
the row's alignment and text height. Small connected components must fit that
measured row, lie outside the plot, and avoid existing detections. The existing
recognizer reads the new crop from the original image. The helper never supplies
a digit, expected tick value or sequence. Every added reading remains unreviewed
and carries an explicit review warning. Existing calibration safeguards remain
authoritative.

Recovery is disabled by default. Its candidate composition and result-cache
identity are distinct. Failed crops have explicit errors; cancellation propagates.
The evaluator independently replays the baseline, reproduces pixel proposals,
checks complete output coverage, and rejects any change to an existing reading.
Recovered components are recorded separately from raw model detections.

Actual CPU comparisons complete all 37 original and 37 corrected-layout panels:

| Exact readings | Before | After |
| --- | ---: | ---: |
| Original train | 396/709 | 402/709 |
| Original development | 141/183 | 147/183 |
| Corrected-layout train | 423/709 | 429/709 |
| Corrected-layout development | 165/183 | 172/183 |

All 12 original-input additions and all 13 corrected-input additions match
previously missing labels with correct text and roles. No correct reading or
role is lost, and no extra false label is added. All 819 original and 839
corrected baseline readings are preserved exactly. Raw model detections and
all image, model and threshold inputs remain unchanged.

Corrected development misses fall from 14 to 7, while extra labels stay at 12.
The complete output has 96.17% recall, 93.62% precision, 93.99% exact reading,
8.44% character error and 91.80% role accuracy. Only recall meets its Tier 1
bar. This diagnostic does not constitute a sealed pass or production approval.
Training-subset recognition remains poor and must also be addressed.

Validation: 334 OCR tests pass with 16 existing optional skips, 74 application
integration/approval tests pass, and 52 evaluator checks pass. Compilation takes
24.37 seconds with no warnings or errors. Reported comparison execution takes
88.48 seconds for original inputs and 48.49 seconds for corrected inputs,
including the independent baseline replay; scoring takes 1.19 seconds. These
are observed run times, not a speed comparison. One initial self-test invocation
used an unsupported switch and exited before inference; the corrected command
passed.

The repair reuses the existing licensed detector, recognizer and component
analysis. No dependency, training, private/sealed read, model revision, production
activation, package, tag or release was introduced. Panel and axis geometry is
replayed, so end-to-end acceptance remains owed. The retained portable is still
build 433, version 0.4.33. All four Goal 22 outcomes remain incomplete.

Source, model and evidence hashes: [aggregate record](GOAL-22-TICK-LANE-RECOVERY.json).
