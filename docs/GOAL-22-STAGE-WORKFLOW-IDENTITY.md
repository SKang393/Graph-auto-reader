<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Bind a stage separately from its containing workflow

The V26 prerequisite checker required the complete OCR/marker workflow to have
the same revision and candidate name as its marker stage. That unnecessarily
rejected composition of independently identified stages, even when the exact
marker payload and stage evidence were unchanged.

The stage checker now verifies the stage's own report, evidence envelope,
configuration and authorization. The containing workflow retains its separate
protocol-bound identity. Model, runtime, operating-point, metrics, parity,
policy and privacy requirements remain unchanged.

Four fictitious checker commands pass after a warning-free x64 build in a
55.76-second validation job. The regression scenarios accept a distinct workflow
identity, reject changed stage identities, and reject workflow names that no
longer match their protocol. Existing changed-model, failed-gate, count-drift,
threshold, payload, source-snapshot and parity rejection scenarios still pass.

[Evidence](GOAL-22-STAGE-WORKFLOW-IDENTITY.json) binds the tested files. This
repair covers V26 evidence composition only. Current marker-report adapters
and OCR/marker-sealed source adapters remain incomplete. No real or sealed data
was read, no inference or training ran, and no production approval or package
was created. Build 433 remains 0.4.33; all four Goal 22 outcomes remain incomplete.
