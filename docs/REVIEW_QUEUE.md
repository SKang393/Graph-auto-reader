<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Maintainer review queue

Items stay here until resolved. Newest items are appended last.

## Current maintainer decisions, 2026-09-08

The maintainer approved REV-004 and REV-006 and authorized completion of the
model-integrated product as **2.0.0**. Existing basic/manual functionality is
the first product generation. The previous proposed 1.0.0 finish is superseded.

Final production activation and the 2.0.0 GitHub installer/portable release
are authorized conditionally on all required accuracy, scientific-safety,
privacy, license, runtime, and distribution checks passing. This decision does
not approve an unverified candidate or waive any gate. No intermediate public
release is authorized. Routine implementation decisions do not require another
confirmation; present a short demonstration, results, and limitations at the
finish. An actual frozen-contract incompatibility still requires its concrete
impact and migration proposal to be reviewed before changing the contract.

The current repository's GitHub release list was empty when checked on
2026-09-08. The local ledger does record portable builds. Preserve that history;
do not invent earlier 1.x tags or renumber existing artifacts. See
[version and publication policy](VERSIONING-AND-RELEASES.md).

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

- **Status:** resolved
- **Resolution:** on 2026-09-08 the maintainer approved building and testing
  the proposed raster-derived pre-OCR structural provider, including synthetic
  text-preservation checks. Production activation remains conditional on all
  required acceptance evidence.
- **Blocks:** none requiring maintainer input; implementation and validation remain
- **Effort:** complete
- **Action:** no further design confirmation is needed. Implement and validate
  the approved structural provider before OCR. The internal provider consumes only
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
- **Unblocks when:** resolved by the explicit design approval above

## REV-006 — define which executions advance the build ledger

- **Status:** resolved
- **Resolution:** on 2026-09-08 the maintainer approved counting packaged
  application builds and rebuilds. Routine local-test and CI compiler outputs
  do not advance the product version. Matching installer and portable packages
  from one common publish output count as one build and share a version.
- **Blocks:** none requiring maintainer input; compilation/tests and integrated
  packaging may proceed under the clarified unit
- **Effort:** complete
- **Action:** no further build-unit confirmation is needed. Apply the packaged
  application build unit consistently in versioning guidance and validation
- **Why:** `VERSIONING.md` says every produced build advances the version, while
  routine validation compiles test binaries many times. Counting every test or
  CI compilation would require a different ledger workflow than the existing
  portable-build ledger.
- **Files:** [versioning policy](../VERSIONING.md),
  [current readiness](1.0-READINESS.md),
  [build ledger](BUILD_LEDGER.json)
- **Unblocks when:** resolved by the explicit build-unit decision above

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


## REV-007 — restore workspace execution credits

- **Status:** resolved
- **Resolution:** managed approval recovered; build, 15 self-tests and 197 cross-language fixtures completed successfully.
- **Blocks:** none requiring account action
- **Effort:** account owner action; service restoration time unknown
- **Action:** ask the workspace owner to refill credits, then resume this task
- **Why:** automatic approval review rejected the authorized build and test launch because workspace credits were exhausted. The command never started. Repeating it through another execution route would bypass that rejection.
- **Files:** [pending scorer](../tools/GraphReader.SyntheticRuntimeEvidence/OriginalDbOcrAggregateScorer.cs), [parity harness](../tools/GraphReader.SyntheticRuntimeEvidence/check_original_db_ocr_aggregate_parity.py), [readiness](1.0-READINESS.md)
- **Unblocks when:** managed approval is available and the pending build, self-test and cross-language parity checks can run
