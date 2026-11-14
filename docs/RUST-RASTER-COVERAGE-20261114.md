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

There were no private payload reads, sealed runs, optimizer steps or candidate
comparisons. The owned tests are regression evidence, not a 95% real-data
accuracy claim. Existing fixed real-dev results remain the frozen 449 outcome.
The change is newer than retained 449 and is not yet packaged or visually
verified. It changes proposal sensitivity to mid-gray pixels; downstream
review and consistency checks remain mandatory. Existing Apache-2.0 source
notices are retained; no license or frozen contract changed.
