<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR V45 development outcome

V45 completed training and application validation but failed all five central
development acceptance bars. It remains unapproved. No private or sealed data
was read, and no production model, packaged build, or release is authorized by
this result.

## What changed and what happened

The candidate added six project-owned training panels containing small,
three-row legends. Training used 34 panels and 862 text regions for 240 epochs
and 8,160 optimizer steps. Development stayed fixed at nine panels, 183 text
regions, and 1,019 truth characters. All 43 application panels completed.

The appropriate comparator is the saved V44 control evaluated with the same
runtime, requests, recognizer, native library, and scorer. Older V44 role
results used a different runtime and are not a controlled training comparison.

| Development measure | Same-runtime V44 | V45 | Required |
| --- | ---: | ---: | ---: |
| Detection precision | 74.33% | 72.92% | at least 95% |
| Detection recall | 75.96% | 76.50% | at least 95% |
| Exact text recognition | 68.85% | 68.85% | at least 95% |
| Character error rate | 61.24% | 64.08% | at most 5% |
| Text-role accuracy | 60.11% | 60.66% | at least 95% |

V45 found one additional true region, added four false detections, and left
exact text recognition unchanged at 126 of 183 regions. Character errors rose
from 624 to 653: two additional matched-text edits, eight additional deletion
edits, and 19 additional insertion edits. Better fit on the added training
panels did not establish better development performance.

## Integrity and runtime

Independent review verified the frozen 50-source snapshot, the 46-file runner
bundle, and 74 artifact, runtime, model, checkpoint, request, and license
bindings. Export parity passed on all 43 panels with maximum absolute error
8.07642936706543e-6 against the unchanged 1e-5 tolerance. There were no failed
application panels to exclude, and all truth denominators were retained.

Training took 45,935,412.837 ms, approximately 12 hours 46 minutes. Training
and the recorded validation stages together took approximately 13 hours
20 minutes. This run used the previously bound restrictive CPU policy. The
separately prepared 80% training duty budget did not alter this experiment.

The first attempt failed before any optimizer step because of an immutable
feature-buffer precondition. Its void evidence is preserved. The retry kept
the same training authorization and completed the declared 8,160 steps.
The repaired runner, export, candidate preparation, and scorer have 69 passing
focused tests with zero failures or skips in the saved validation report.
The real input-boundary check prepared all 34 panels in 8.791 seconds without
an optimizer step. Frozen source verification confirms those implementations
did not change during training or validation.

## Evidence and next diagnostic

- Canonical outcome: [P1_RESULT.json](../ml/ocr/legend_coverage_db_head_v45/P1_RESULT.json)
- Protocol: [protocol.json](../ml/ocr/legend_coverage_db_head_v45/protocol.json)
- Central bars: [acceptance-bars.json](../ml/policy/acceptance-bars.json)
- Local full score: `artifacts/goal22-runs/ocr-v45-legend-coverage/P1-retry1/full-score-v1.json`
- Same-runtime control: `artifacts/goal22-runs/ocr-v45-v44-control-score-v1.json`

Before another training run, compare saved V44 and V45 development outputs at
the unchanged product operating point. Attribute lost and gained matches,
extra detections, crop geometry changes, and character-error changes. Reuse
the existing pretrained OCR baseline evidence rather than repeating its
inference. This diagnostic may inspect synthetic development cases, but must
not read private or sealed cases, select thresholds, or approve a candidate.

The reviewed parent and recognizer retain their authenticated Apache-2.0
license and notices. No new dependency was introduced. Synthetic evidence
alone cannot authorize the final model-integrated 2.0.0 product.
