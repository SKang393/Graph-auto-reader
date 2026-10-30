<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Train-only crop context coverage

This fixed-output diagnostic reconstructs owned synthetic marker drawing
primitives and compares the nearest reference primitive visible inside 33- and
65-pixel crops around saved V37 training proposals. No model is executed and no
optimizer, development, private, or sealed data is used. The code checks the
saved report, generator, train scenes, prediction identities, and 334 proposal
arrays against their hashes before producing a new report.

The nearest reference to an extra is geometric context, not a causal label.
Masks represent pre-blur synthetic primitives, excluding noise, connector
occlusion, and family panels. Wider crop coverage is an architectural
hypothesis, not proof of improvement or permission to change thresholds.

October 30 result: 133 extra and 1,982 matched proposals from 167 component
training scenes. A 65-pixel crop contains the entire nearest primitive where
the 33-pixel crop did not for 80 extras and 84 matches. However, 129/133 extras
and 1,877/1,982 matches also contain another reference primitive at 65 pixels
(80/133 and 1,734/1,982 at 33 pixels). A wider input can introduce crowding and
requires a fresh fixed model contract and end-to-end evaluation.

The project-local GPU Python launcher executes the audit script without any
optimizer steps. The report under `artifacts/goal22-tools/train-context-coverage-v1/run`
has SHA-256 `91f93775ce4349e4c0858c9ed4e54017211a1dd396307ceffe2b106fcd59bf57`.
The audit script has SHA-256 `13a70f159da315ce9754d9fa39a5186204fbe85a11b8cb8848201576082d64ad`.
The temporary report can be retired after its consumers finish, subject to
the current deletion controls.
