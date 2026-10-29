<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Residual-negative confidence V38 outcome

**FAIL. Retain the existing production selection.** V38's precision remains
below the existing 95 percent development bar. It is not approved for sealed
evaluation or production use. The [outcome](../ml/markers/center/confidence_residual_v38/P1_RESULT.json)
binds the candidate result, source snapshot, eligibility evidence and cache.

The experiment retained V37's initializer, model, BCE objective, seed,
eight-epoch schedule, 0.10 cutoff, existing training rows and four fixed
reflection views. It added four views of 90 distinct synthetic training
patches whose original anchors and decoded centers satisfy the existing
negative-mining distance rules. The resulting population was 1,106,832 views
and 69,184 optimizer steps. Exact patch identity does not establish visual
coverage or causality.

The historical cache manifest's `source_sha256.protocol.json` entry refers to
the V37 training protocol. The builder overwrote the residual-cache protocol
hash before writing that manifest, so the manifest does not independently bind
the cache protocol bytes used for this run. The retained cache tensor and
eligibility report remain checksum-bound; this protocol provenance limitation
is not repaired retroactively by later builder changes.

| Development split | Matched | Extras | Precision | Recall |
| --- | ---: | ---: | ---: | ---: |
| Component | 1,941 / 2,004 | 164 | 92.21% | 96.86% |
| Family | 202 / 206 | 50 | 80.16% | 98.06% |

The complete development evaluation included 176 scenes and 270,236 proposal
outputs. CPU ONNX parity had maximum absolute error 6.20e-6 and zero raw
confidence-boundary changes. Frozen geometry differed by at most 3.82e-6.
Training and development took 497.75 seconds. The exporter emitted legacy
ONNX API and input-shape tracing warnings.

Ten focused V38 tests passed. A separate synthetic preflight verified all 90
cache rows, four reflected feature groups, the initializer identity and
unchanged frozen state. No private or sealed data was read. No new dependency,
external weight, production activation, package or release was introduced.
Because development precision failed, complete native/CSV workflow comparison
was not run under this protocol. Goal 22 remains incomplete; real acceptance,
approved production composition and final installer/portable validation remain.
