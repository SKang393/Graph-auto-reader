<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Carry explicit withdrawal headings into phase semantics

OCR recognized the heading `Withdrawal`, but the phase module discarded its
meaning. `Reintroduction` was absent from both explicit heading vocabulary and
phase normalization. Two independent synthetic cases reproduced the mismatch.

Exact withdrawal/reintroduction headings, including a `continued` suffix,
now map to baseline/intervention under the SCD AB profile. This follows the
[WWC description of an ABAB design](https://ies.ed.gov/ncee/wwc/Docs/InterventionReports/wwc_srsd_111417.pdf).
Spatial requirements, original wording, manual overrides and unknown later
phases are preserved. No fuzzy spelling repair or article-specific rule is
added. The deterministic phase adapter advances to 0.1.1; schemas and model
weights are unchanged.

All 43 phase, 373 application and 359 OCR tests pass. The 16 existing optional
OCR tests remain skipped. The x64 tool builds with zero warnings/errors and
the fictitious private-runner check passes. The initial test-only compiler
diagnostic and the two expected pre-repair failures remain recorded.

The unchanged 23-source workflow takes 160.15 seconds. Five correctly read
`Reintroduction` regions gain the phase-heading role. All OCR text, alternatives,
geometry and review states, raw axes and accepted markers, calibration values
and 250 exported points remain unchanged. Scores remain ten completed sources,
thirteen failed, 218/706 correct unique values and 210/724 relational rows.
The five wrong exported phases still have misrecognized headings; this repair
does not invent their missing meaning.

[Full evidence and source bindings](GOAL-22-PHASE-VOCABULARY-INTEGRATION.json)
retain the unchanged denominator. No training, private/sealed read, model
revision, production activation or package occurred. Build 433 remains 0.4.33;
all four Goal 22 outcomes remain incomplete. Next: address the diagnosed missing
legend proposal using original-pixel evidence and synthetic validation.
