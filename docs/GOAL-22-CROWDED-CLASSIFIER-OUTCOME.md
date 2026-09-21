<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Crowded classifier adaptation rejected

V8 improves the unchanged small-patch development test but damages actual graph
reading. Keep V27 centers with the existing V5 classifier. Do not repeat V8,
relax its threshold, promote it, or spend a sealed run on this result.

The fixed 12-epoch adaptation starts from V5 and appends 6,912 owned crowded
marker examples. All 62,512 earlier training rows and targets remain unchanged,
including every earlier negative. All 3,136 development rows are byte-identical.
Target-absent images check visibility only; they are not new training negatives.
Architecture, losses, crop, temperatures and artifact cutoff remain unchanged.

| Measure | V5 baseline | V8 candidate |
| --- | ---: | ---: |
| Patch shape accuracy | 97.64% | 98.13% |
| Patch fill accuracy | 99.44% | 99.72% |
| Native correct points | 726/838 | 746/838 |
| Native false points | 25 | 89 |
| Native precision | 96.67% | 89.34% |
| Native recall | 86.63% | 89.02% |
| Completed CSV sources | 12/23 | 8/23 |
| Correct unique CSV values | 409/706 | 190/706 |
| Correct CSV rows | 405/724 | 186/724 |

V8 retains 715 earlier native matches, recovers 31 and loses 11. Every source
and failed export stays in the comparison. All 39 workflow panels are observed.
OCR readings and axis observations are unchanged; native OCR stays 345/453 exact
and axis corners 84/93. Four additional sources fail export. There are zero
wrong-scale, duplicate or failed-source residual rows. The export safeguards
remain active. Every observed model actually uses CPU.

All 86 data and runner tests pass. Cache roundtrip, source snapshots, earlier
training targets and development bytes are verified; the example sheet was
inspected. Training performs 6,516 optimizer steps in 484.81 seconds, 490.93
seconds including its launcher. ONNX parity covers all 72,560 train/dev rows;
maximum error is 0.00000608 with no changed shape, fill or artifact decisions.
Native and CSV comparisons take 149.07 and 162.71 seconds. The maximum completed
work/rest cycle is 79.993%; this is not an instantaneous per-core guarantee.

The outcome is `failed_dev_unconsumed`. The patch-only canonical result remains
`dev_pass` and is explicitly distinguished from this failed native outcome.
Preregistration, authorization and outcome close together; the previous 90 ledger
entries remain unchanged. No private or sealed data, external weights, new
dependency, production approval or packaged build is involved. Code and the owned
model retain Apache-2.0 provenance. Build 433 remains 0.4.33.
Three narrowly scoped Git attributes preserve the exact recorded initializer,
configuration and protocol bytes across checkouts; execution hashes are retained.

Continue the diagnosed OCR crop repair and investigate native false-center
rejection using open development evidence. All four Goal 22 outcomes remain
incomplete. Evidence and source hashes are in the
[aggregate record](GOAL-22-CROWDED-CLASSIFIER-OUTCOME.json) and
[candidate outcome](../ml/markers/classifier/crowded_context_v8/P1_RESULT.json).
