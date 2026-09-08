<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Maintainer review queue

Items stay here until resolved. Newest items are appended last.

## REV-001 — supply additional real graphs

- **Status:** resolved
- **Resolution:** the authoritative `data/manual data/` corpus was identified
  on 2026-09-02 with 40 studies, 171 complete `.dig` projects, and 3,055
  digitized points. The earlier five-image request is superseded.
- **Blocks:** none
- **Effort:** complete
- **Action:** no maintainer action remains; Phase 4 now owns the study-level
  `real-dev` and `real-sealed` harness.
- **Why:** the complete local corpus replaces the one-image acceptance inbox.
- **Files:** [real corpus](../data/manual%20data/),
  [historical aggregate report](GOAL-22-PHASE-4-PRIVATE-ACCEPTANCE.json),
  [golden-case criteria](../GOLDEN_CASE_CHANDLER_SPEC.md)
- **Unblocks when:** resolved by the local corpus inventory

## REV-002 — resolve marker fill gate compatibility

- **Status:** resolved
- **Resolution:** AGENTS.md Section 7.4 prohibits a new gate from rejecting a
  previously approved artifact. The approved payload retains its existing
  `0.90` fill gate; `0.95` is the target for future candidates.
- **Blocks:** none
- **Effort:** complete
- **Action:** no maintainer action remains
- **Why:** public-v3 fill accuracy is `0.9444444444444444`; enforcing the new
  `0.95` bar would retroactively reject a production-approved artifact, which
  AGENTS.md Section 7.4 prohibits
- **Files:** [acceptance bars](../ml/policy/acceptance-bars.json),
  [Phase 3 rescore](../ml/policy/goal22-phase3-rescore-result.json),
  [current approval evidence](../artifacts/production-model-store/evidence/graph-marker-classifier/0.1.0/marker-classifier-production-approval.json)
- **Unblocks when:** resolved by the authoritative compatibility rule

## REV-003 — reauthenticate GitHub as SKang393

- **Status:** resolved
- **Resolution:** GitHub CLI was reauthenticated as active account `SKang393`
  on 2026-09-04, and the 61 committed Goal 22 changes were pushed from
  `58e1e63` through `847c80b` to `origin/main`.
- **Blocks:** none
- **Effort:** complete
- **Action:** no maintainer action remains
- **Why:** the repository now uses the authorized `SKang393` credential and
  local `main` matches `origin/main`
- **Files:** [Goal 22 readiness](1.0-READINESS.md),
  [review queue](REVIEW_QUEUE.md)
- **Unblocks when:** resolved by successful authenticated push

## REV-004 — approve a pre-OCR structural provider design

- **Status:** open
- **Blocks:** OCR V39 candidate creation and OCR production approval
- **Effort:** about 10 minutes
- **Action:** choose whether the production workflow may add a raster-derived
  structural provider before OCR. The proposed internal provider consumes only
  immutable Gray8 raster evidence plus the existing axis/tick/divider mask and
  emits marker-like blob and thin-connector probability masks in original
  pixels. It cannot consume OCR boxes, marker results, connections, or truth.
  Approval includes requiring synthetic text-preservation precision/recall tests
  before the provider can enter the Production composition.
- **Why:** V38 attributes 40,722 false-positive pixels to marker or connecting
  lines, but marker centers and connection graphs currently run after OCR;
  available axis, tick, and divider geometry is already masked
- **Files:** [blocked V39 design](../ml/ocr/structural_suppression_v39/BLOCKED_DESIGN_REPORT.json),
  [V38 attribution](../ml/ocr/dice_loss_detector_v38/diagnostics/fp_attribution/ATTRIBUTION.json),
  [current readiness](1.0-READINESS.md)
- **Unblocks when:** the maintainer approves one pre-OCR input contract or
  explicitly rejects this direction

## REV-006 — define which executions advance the build ledger

- **Status:** open
- **Blocks:** Goal 22 integrated portable build, local synthetic seed-boundary
  compilation/tests and runtime-derived marker inputs, and any claim that every
  build is accounted for
- **Effort:** about 5 minutes
- **Action:** confirm whether the version rule applies only to packaged or
  runnable application artifacts, or also to compiler outputs produced by local
  tests and CI
- **Why:** `VERSIONING.md` says every produced build advances the version, while
  routine validation compiles test binaries many times. Counting every test or
  CI compilation would require a different ledger workflow than the existing
  portable-build ledger.
- **Files:** [versioning policy](../VERSIONING.md),
  [current readiness](1.0-READINESS.md),
  [build ledger](BUILD_LEDGER.json)
- **Unblocks when:** the maintainer defines the ledger unit in one sentence

## REV-005 — renew Git write approval after service failure

- **Status:** resolved
- **Resolution:** the maintainer explicitly approved staging and committing
  the prepared Goal 22 changes on 2026-09-04.
- **Blocks:** none
- **Effort:** complete
- **Action:** no maintainer action remains
- **Why:** the renewed approval authorizes the normal Git write path after the
  prior managed approval-service failure
- **Files:** [review queue](REVIEW_QUEUE.md),
  [retry6 result](../ml/markers/center/mask_preserving_v24/P1_RETRY6_RESULT.json),
  [retry7 config](../ml/markers/center/mask_preserving_v24/training/p1.json)
- **Unblocks when:** resolved by explicit maintainer approval
