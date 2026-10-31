# Real-positive marker confidence trial, 2026-10-30

The owner authorized the manual Engauge projects for training or testing and
noted that the labels are imperfect. This trial uses only the frozen real-dev
training partition. Exact duplicate images were grouped before extracting
positive proposals; no unmarked real pixels were assigned negative labels.
The reserved real-sealed partition was not read by this trial. Earlier inventory
activity opened its files before checking the split, so that partition must not
be described as previously unread.

The production-candidate axis, OCR, and artifact-mask pipeline produced 2,171
positive proposals from 102 grouped train images. Of 1,743 labeled centers,
1,262 had an eligible proposal within three pixels; 415 were blocked only by
the artifact mask, 57 only by the OCR mask, and nine by mixed causes. These
training-source diagnostics do not establish end-to-end recognition accuracy.
The private proposal archive remains in the project artifacts directory.

Candidate P1 combined those positive proposals with 36,212 reflected synthetic
family rows. The synthetic artifact masks lack representative-input approval,
component-development source disjointness is unverified, manual-label quality
is not approved, and overlap with sealed-source images is unverified. The
fixed protocol is [protocol.json](../ml/markers/center/real_positive_context_v1/protocol.json).

The RX 6600 XT completed 2,264 of 2,264 optimizer steps through HIP `cuda:0`.
The single-use run outcome SHA-256 is
`91c2ff0a6c1dee60f88c5f7d7b8d241225ad2a6772fb537a329f4b4259925739`.
The verified checkpoint SHA-256 is
`9d7abce386d6f0e61104a23a856ee49549e2840f7a715dbd5f77c09cd50bef20`.
The fixed synthetic development evaluation failed. At the preregistered 0.1
threshold, component precision was 48.85% and recall 97.75%; family precision
was 42.65% and recall 98.54%. The required precision and recall bars are 95%.
The model produced 2,051 component and 273 family false positives. CPU ONNX
and GPU outputs differed by at most `8.1063e-6` over 270,236 proposals, with no
threshold decision changes. The evaluation report SHA-256 is
`5137ee6b49ae2aa60b8938ed318e5fe2ac1ca223cb98adc4543ea8501a33f002`;
the exported ONNX SHA-256 is
`293c0f506f3da062b07829f9db21cb5387a100a342642dc3e0e1c32c1225fce5`.
The checkpoint and ONNX are unapproved and ineligible for release. The
component set is a historical regression benchmark with unverified source
disjointness; neither these scores nor the mask audit establish real-workflow
acceptance.

A subsequent aggregate audit attributed the 415 artifact-only exclusions to
252 residual-mask exclusions, 152 geometry-seed exclusions, and 11 involving
both. That diagnostic does not prove that any individual label or mask is
wrong; a truth-free correction must still preserve structural rejection.
