<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Combined marker coverage remains incomplete

The new classifier improves the complete graph workflow, but the broader
component examples expose a separate gap. It rejects narrow rectangular
markers and large or overlapping glyphs that its training examples do not
represent adequately. The original component renderer deliberately includes
these shapes. They must remain in the denominator.

| Complete development inventory | Before classifier | After classifier |
| --- | ---: | ---: |
| Component centers recovered | 1,862 / 2,004 | 1,382 / 2,004 |
| Extra component centers | 358 | 43 |
| Family centers recovered | 201 / 206 | 195 / 206 |
| Extra family centers | 75 | 24 |

The classifier raises component precision to 96.98%, but recall falls to
68.96%. Family precision is 89.04% and recall is 94.66%. These fail the unchanged
shared bars. The family replay uses historical masks and images; it does not
replace the current full native workflow's 201/206 development points with no
extras. Neither result authorizes production.

## What the check establishes

All 167 component scenes and nine family panels remain. The replay authenticates
original pixels, cached proposal coordinates, all V27 outputs and the new V5
classifier. It reproduces the historical raw-model failures exactly before
measuring the current diagnostic cutoff of 0.10 and classifier cutoff of 0.5.
All 176 scenes also reproduce the existing balanced decoder at 0.25. No center
model is rerun, no optimizer runs, and no private or sealed data is read.

The replay takes 7.09 seconds. Seven contract tests pass. Its work/rest guard
records a maximum completed-cycle duty of 79.8841%, with all logical processors
eligible. The classifier uses the unchanged native bilinear crop. This Python
replay is diagnostic; native runtime parity has not been established for this
new combined component evidence adapter.

Of the 622 final component misses, 480 first become unmatched at classification,
75 at scored decoding, seven at geometry and sixty at nonmaximum suppression.
The sequence uses the existing one-to-one matcher, so its stage attribution is
a diagnostic, not an assertion that greedy assignments can never change.

The suspected faint-ink cutoff is not the main cause: no truth-centered local
window has maximum ink below 0.12. Inspection instead finds strongly rejected
11- and 13-pixel elongated markers with less than one pixel of center error.
Larger examples also show crops around clipped-radius predictions and overlap.
The generator contains horizontal/vertical elongated rectangles, whereas the
current classifier's generic `other` drawing primitive does not cover these
morphologies. Its training radii mainly span 3 to 10 pixels; the component
inventory includes rendered diameters up to 49 pixels.

## Corrected next step

Keep this failure and all historical evidence. Add truthful marker-presence
coverage from the existing owned training component scenes, without inventing
shape or fill labels for arbitrary composite glyphs. Measure the center recall
ceiling too: even perfect classification cannot repair the 142 points already
missing before classification. Reuse existing center candidates before deciding
whether another training run is justified. Keep every development case and the
shared 95% bars, and rerun the actual complete workflow after any repair.

The [evidence index](GOAL-22-MARKER-CASCADE-COMPONENTS.json) binds all artifacts.
The code and examples are project-owned Apache-2.0 work. No dependency, model
activation, package or public release is added. Build 433 / 0.4.33 remains the
retained package. All four Goal 22 outcomes remain incomplete.
