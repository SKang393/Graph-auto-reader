<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Thin antialiased axis coverage

The owned WPF preparation control draws ordinary one-pixel black axes at
integer coordinates. Rasterization produces adjacent Gray8 pixels of 127;
the crossing includes 63. Rust's fixed `<96` dark-pixel test discarded the
axis strokes. A deterministic owned reproduction confirmed that exact-black
axes were accepted while the equivalent half-covered strokes were rejected.
This identifies a supported-input coverage defect, not a private-set accuracy
finding. The full rendered fixture also contains text and ticks; its separate
ambiguity result is not explained solely by the simplified reproduction.

The fixed cutoff now retains majority-covered black pixels: Gray8 `<128`,
or a three-channel sum `<384`. There is no adaptive threshold, extra retry,
image resampling, new dependency or model. All axis-selection, ambiguity,
tick-evidence, bridge and resource limits remain unchanged. Proposals still
require review and never supply invented scientific tick values.

Owned regression controls cover four subpixel phases (`0/255`, `63/191`,
`127/127`, `191/63`) in both Gray8 and three-channel inputs. All eight return
geometry within one original pixel of the expected axis bounds and no
fabricated ticks. Boundary/faint-stroke controls reject intensity 128;
competing gray lines and two gray grid panels remain ambiguous.

Command: `cargo test --offline --manifest-path native/graphreader-engine/Cargo.toml
--target-dir artifacts/native-engine`. Final source passed all **51 tests**,
zero failures, in **0.21 seconds** of test execution. The prior 49 checks remain
in the suite. A focused intermediate run also passed the subsequently added
two-panel negative before the lead ran the final suite.

A subsequent calibrated-workflow control added three observed outward ticks
on each axis at all four subpixel phases. It returns exactly three centers per
axis, each within one original pixel. No runtime correction was needed for
the resulting two-pixel stroke bands. The complete suite then passed **52/52**
in **0.21 seconds** (6.3 seconds command wall), including existing long-stroke
and ambiguity negatives. This was an owned test-only extension.

There were no private payload reads, sealed runs, optimizer steps or candidate
comparisons. The owned tests are regression evidence, not a 95% real-data
accuracy claim. Existing fixed real-dev results remain the frozen 449 outcome.
The change is newer than retained 449 and is not yet packaged or visually
verified. It changes proposal sensitivity to mid-gray pixels; downstream
review and consistency checks remain mandatory. Existing Apache-2.0 source
notices are retained; no license or frozen contract changed.

Hosted CI for runtime commit `8480f38a29cc40eae5eaf8a4a9532aac6dc81bf4`
completed successfully in [run 43646513613](https://github.com/SKang393/Graph-auto-reader/actions/runs/43646513613).
It reported 2,732 .NET passes and 30 skips, 51 Rust passes, 62 version-policy
checks, 6 ledger checks and 12 portable-packaging checks. This run preceded
the additional tick-band regression described above; that final 52-test
source passed locally. Release jobs were skipped. The earlier 4a6f7420 CI run
was superseded by this push and cancelled, not reported as a completed test run.
