<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR repair: tighter boxes around printed text

## Problem

Some detected boxes include excessive blank margins. The text may be readable,
but the box does not match the actual label closely enough. The completed
[diagnosis](GOAL-22-OCR-REMAINING-ERRORS.md) supported one fixed pixel rule;
it did not justify another training run.

## Implementation

An optional development path trims margins using the original image. It keeps
every pixel darker than the existing foreground threshold and retains empty
proposals. It runs after the existing inside-plot word grouping, then reads
the adjusted crops with the same OCR models. It preserves region identities,
group membership, confidence, and original coordinates.

The option is off by default and has a separate cache identity. Its application
adapter remains explicitly unapproved. An independent Python check reproduces
every adjusted box from authenticated original pixels before loading truth.
No thresholds, acceptance bars, frozen schemas, or model weights changed.

## Files changed

- OCR refinement, pipeline option, cache identity, and application trial adapter.
- Evidence command, independent scoring replay, and regression tests.
- This report, the [aggregate evidence](GOAL-22-OCR-PIXEL-BOUNDS-APPLICATION.json),
  [remaining-error diagnosis](GOAL-22-OCR-REMAINING-ERRORS.md), and readiness record.

## Tests and commands

| Check | Result |
| --- | --- |
| Complete OCR test project | 288 passed; 16 existing optional skips |
| Application candidate factory checks | 27 passed |
| Python scoring and pixel diagnostic tests | 36 passed |
| Evidence-tool self-checks | 29 passed |
| Runtime compiler build | Zero warnings and errors |
| Actual OCR run | All 37 panels completed; zero recognition failures |
| Independent integration audit | 11 source bindings and four assemblies verified; original pixels, raw detections, and group memberships unchanged |

Commands were `dotnet test` for `GraphReader.Ocr.Tests` and the
`ProductionOcrLocalCandidateFactoryTests` filter, `pytest` for
`test_score_participant_lane_candidate.py` and `test_diagnose_ocr_pixel_bounds.py`,
and the evidence tool's `--self-test-official-head-candidate` and
`--evaluate-pixel-bounds-candidate` commands. Full scoring used `pixel_bounds`
mode with the exact historical evaluation request.

The initial test setup needed cached package restoration for the new compiler
directory and a workspace temporary directory for pytest. Both were corrected.
No product test failure was suppressed. Full Windows CI remains pending for
the resulting source commit.

## Metrics and timing

All 183 development labels and 1,019 characters remain in the denominator.
Missed labels and extra detections still count as errors.

| Development measure | Before this repair | After this repair |
| --- | ---: | ---: |
| Correctly located labels | 141 | 152 |
| Missed labels | 42 | 31 |
| Extra detections | 43 | 32 |
| Exactly read labels | 128 | 135 |
| Character edits | 567 | 436 |
| Correct text roles | 110 | 118 |

Detection precision is 82.61%, recall 83.06%, exact reading 73.77%, and role
accuracy 64.48%. Character error rate is 42.79%. All five acceptance bars still
fail: the first four require 95%, and character error rate must be at most 5%.

The 709-label train set has 635 geometry matches, up from 625. Exact reading
remains 396/709; character edits fall from 1,396 to 1,358. These train results
are diagnostic, not independent acceptance evidence.

The application run took 58.380 seconds and full scoring 28.402 seconds.
The earlier run took 34.935 and 18.820 seconds respectively. These are observed
wall times, not a controlled performance comparison. Training time was zero.

## License review

No dependency or model payload was added. Detector, recognizer, native runtime,
manifests, and license inputs match the existing reviewed trial byte for byte.
New project source is Apache-2.0. No private or sealed data was read.

## Known limitations

Development gained eight exact labels and lost one faint x-axis digit match.
Text was unchanged on all 140 labels matched before and after the repair.
On train, 12 exact labels were gained and 12 lost; among 625 common matches,
character edits increased from 1,054 to 1,068. Tighter boxes are therefore not
a universal recognition improvement. These regressions remain recorded.

Missing proposals, neighboring labels fused into one box, and missing role
context remain. Participant-lane grouping is a separate existing repair and
was not combined in this trial. Arbitrarily rotated text and real-image
performance have not been established by this synthetic comparison.

## Integration notes

This is a source-only development repair. It creates no model revision,
production approval, packaged application, public release, or version change.
The retained portable stays at build 433, version 0.4.33. The finished product
target remains 2.0.0.

Short checks and repairs continue without permission requests. Genuinely long
training or verification jobs notify on completion and end the active turn;
the agent does not repeatedly poll them. No subagents were used.

## Acceptance status

**FAIL** for Goal 22 completion. The local repair is validated and improves the
development result, but all OCR acceptance bars still fail and the four Goal 22
outcomes are not all verified. The next work is diagnosis of the remaining
proposal and role errors on train/dev, followed by actual runtime verification.
This result does not authorize training or selection on private or sealed data.
