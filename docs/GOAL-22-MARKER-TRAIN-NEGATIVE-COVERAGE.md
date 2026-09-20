<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Correct marker background coverage

The V28 shape supplement improves family development recall to 201/206 markers,
but produces 95 false detections. Saved-output inspection places many of them
on letters and legend symbols. No development crop is reused for training.

A fixed-model diagnostic then inspects all 122,900 native proposals from the
28 authenticated training panels. It finds 1,173 confident clear-background
proposals. Only 177 have exact three-channel pixels present among historical
selected negative rows; 996 are absent. Identical pixel patches share membership,
so this is a coverage measurement rather than a count of unique scene anchors.
The check takes 211.65 seconds and performs no optimizer step.

V29 adds all 2,320 clear training errors: 1,147 component proposals and 1,173
family proposals. Selection uses the fixed V28 model at the existing 0.25
threshold, an anchor more than eight pixels from every training marker, and a
decoded center more than five pixels away. It does not rank or cap the results.
All 87,638 previous rows remain, including difficult and repeated examples.
Preparation takes 221.80 seconds. The total is 89,958 rows.

The candidate starts from the owned V28 checkpoint and runs eight fixed epochs,
5,624 optimizer steps, with the previous architecture, loss, learning rate and
operating threshold. Only the final epoch is evaluated. The independent 167
component and nine family development scenes retain all 2,210 truth markers.
All processors remain eligible, with passive waits and the existing 80 percent
work/rest budget. This is a bounded adaptation, not training from scratch.

All 33 preparation, split-boundary, configuration, recovery and decoder checks
pass. They reject non-training pixels, attempts to label true-marker anchors as
background, malformed scores, changed initialization and altered read authority.

The final candidate fails development after 733.60 seconds and all 5,624 steps.
At the unchanged 0.25 operating threshold, component precision improves from
90.95% to 95.68%, but recall falls from 94.26% to 90.62%. Family precision
improves from 67.91% to 83.97%, while recall changes from 97.57% to 96.60%.
Four prohibited family detections remain. Both scopes retain every truth item.
ONNX/Torch comparison covers 270,236 proposals, with maximum error 0.000007153
and zero confidence-decision differences. Threshold sensitivity is descriptive;
no alternative threshold is selected. The cooperative duty-cycle maximum is
79.994%, which is not a guarantee about instantaneous per-core utilization.

The complete workflow also fails: 13/23 sources export, but correct unique
values fall from 322/706 to 264/706 and correct relational rows from 314/724 to
256/724. Nine extra points, five phase errors, 442 missing truth points and ten
failed sources remain counted. The run takes 140.79 seconds. All 33 previously
observed panels remain, with 35 now visited. The full workflow uses the frozen
axis-repair runtime, keeping the simultaneous phase-spacing repair out of the
model comparison. All 23 source images, 706 unique truth points and 724 relational
rows remain in the scoring denominator.

The [closed outcome](../ml/markers/center/train_negative_v29/P1_RESULT.json)
records failed development, no consumed candidate and no production approval.
Do not activate or promote this candidate. Continue with V27 for independent
runtime repairs and diagnose the saved precision/recall tradeoff before another
model change. No further training follows from this failed result alone.

The [candidate definition](../ml/markers/center/train_negative_v29/protocol.json)
uses the shared evidence policy and acceptance bars. No private/sealed read,
production activation, new dependency or package is authorized by this result.
Code, synthetic data and owned model lineage remain Apache-2.0. Build 433 remains
0.4.33; all four Goal 22 outcomes remain incomplete.
