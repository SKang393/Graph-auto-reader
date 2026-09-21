<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Correct the broader native diagnostic input

The broader 21-source diagnostic still used a historical generator defect
already repaired in the complete export fixture: isolated circles, triangles,
letters and short strokes were painted inside plots and named non-data without
their claimed context. Of 86 baseline extra detections, 71 fall within five
pixels of those injected requests. This spatial association does not relabel
the historical results or prove the origin of the other 15 extras.

The existing `synthetic-contextual-negatives-v1` renderer now supplies a
separate corrected derivative of every broader source. It samples actual graph
text, legends, ticks and other structures instead of painting the orphan
requests. This reuses the [completed generator repair](GOAL-22-CONTEXTUAL-NEGATIVE-REPAIR.md).
It does not reopen or repeat that implementation, change the shared bars, or
erase any old result. Historical orphan-glyph inputs remain stress evidence;
their inconsistent negative names must not define a production accuracy gate.

All 21 historical PNGs reproduce byte-for-byte. Every native annotation and
marker-mask byte remains identical in the derivatives: 31 panels, 838 true
points, 453 text labels and 3,100 characters. The profile replaces 189 injected
requests with 821 contextual negative samples. No source or truth is removed.

The same compiled application, V27 center model, V5 classifier, OCR, masks,
thresholds and CPU configuration run the corrected inputs. Only image pixels
and their negative annotations change. This is a fixture comparison, not an
application improvement or an approved model.

| Measure | Historical stress input | Corrected contextual input |
| --- | ---: | ---: |
| Matched native points, all 838 | 579 | 569 |
| Extra detections | 86 | 17 |
| Missing points | 259 | 269 |
| Marker precision | 87.07% | 97.10% |
| Marker recall | 69.09% | 67.90% |
| Exact text readings, all 453 | 339 | 337 |
| Correct text roles | 355 | 349 |
| Character errors | 843 | 867 |
| Axis corners within two pixels, all 93 | 85 | 84 |

The paired marker comparison retains 564 previous matches, recovers five and
loses 15. OCR and one axis endpoint regress despite unchanged native truth.
Their individual causes are not independently traced. All 31 panels are still
observed and every model uses CPU. Preparation takes 36.61 seconds; the native
retry takes 119.71 seconds and returns its expected failure exit code.

A separate feasibility check searches actual plot pixels using pixel-measured
legend symbols. It reads no authored truth during search. The fixed primary
similarity of 0.90 adds four correct points on the corrected fixture. The
descriptive 0.70 comparison adds 111 correct points and one extra, retaining all
569 baseline matches: 680/838 matched, 18 extra and 158 missing. This is still
below the unchanged recall bar. It has not passed classification, series,
phase or CSV checks and is not a selected production setting. The same search
on historical stress inputs is retained separately. No application code or
model is changed by these diagnostics.

Three launch failures remain recorded: an unavailable Windows PowerShell hash
command, a missing explicit native-library resolver, and Windows PowerShell
treating a runtime warning as fatal. Repairs use a supported hash API, the
existing authenticated native-loading pattern, and the observed PowerShell 7
runtime. No generator was rerun after its successful 21-source preparation.
The historical template replay finishes in 15.60 seconds including its build;
the contextual template follow-up and scoring take 18.46 seconds.

The [evidence index](GOAL-22-NATIVE-CONTEXT-COVERAGE.json) authenticates 45
artifacts and all unchanged runtime bindings. Source checkpoint 5ddb324 passes
CI 35618628882. Future marker diagnosis must distinguish the corrected fixture
from the historical stress set. Continue OCR and missed-marker repairs, without
training merely to suppress indistinguishable orphan symbols.

Code and generators retain Apache-2.0 licensing. No private/sealed data,
training, new weights, production approval, package or release is involved.
Build 433 remains 0.4.33. All four Goal 22 outcomes remain incomplete.
