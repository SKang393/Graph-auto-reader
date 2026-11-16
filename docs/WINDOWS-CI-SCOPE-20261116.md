<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Avoid repeated product compilation for verified documentation changes

## Problem and change

Build-ledger and outcome-only pushes reran the complete Windows product build,
Rust tests and .NET suites. The workflow now skips those stages only for an
eligible documentation change with an already successful exact predecessor.
This changes CI scheduling, not application behavior or acceptance criteria.
It creates no package and introduces no dependency.

Eligibility requires a push to `refs/heads/main`, matching checked-out HEAD,
a valid nonzero `event.before`, and a nonempty NUL-delimited Git diff containing
only additions/modifications of `docs/**/*.md` or `docs/BUILD_LEDGER.json`.
The GitHub API must confirm a completed successful push run of this exact
`windows-ci.yml` workflow at `event.before`, on main in the same repository.
This prevents a documentation push from hiding a cancelled source-validation
run. API errors, missing authorization, malformed data and all uncertainty
default to full validation. Deletions, source/config/workflow changes, pull
requests, tags and manual dispatches also run full validation.

Version, checkpoint, ledger, retention, localization and every existing
packaging/evidence check remain unconditional. Only product Rust/.NET build,
test and model-free probe stages are skipped. The existing job/check name is
unchanged. Release-candidate packaging additionally requires this run's
completed product-validation marker; a documentation-only run cannot package.
Existing release approvals, tags and distribution gates are unchanged.

## Verification

`pwsh -NoProfile -ExecutionPolicy Bypass -File
packaging/tests/Test-WindowsCiScope.Tests.ps1` passed **34 assertions in
2.412 seconds**. These cover eligible documentation, exact-parent rejection,
pending/cancelled/failed runs, event and path exclusions, parsing failures,
local Git command handling and empty top-level inputs. An independent read-only
review found no material issue in the classifier, tests and workflow wiring.

One live read-only API check at `2026-11-16T08:54:38.9631919Z` classified the
documentation commit `4d5df76cbc45af2feab584cc155eccb1c6dd795c` against exact
parent `bfb1847ea94d88afcfcc55f021b0669519f00c05`. It returned
`docs_only_verified_parent`, referencing successful
[run 43833003490](https://github.com/SKang393/Graph-auto-reader/actions/runs/43833003490),
in **0.972 seconds**. No secret was emitted. Its disposable text receipt,
`artifacts/ci-scope-live-check.txt` (98 bytes), was retired after this record
was pushed in `68595526`. No private/sealed inputs, inference or model selection
ran.

The changed workflow on `6859552613dcc515921c9635af16b9a832241dc5` passed
[full CI 43835295159](https://github.com/SKang393/Graph-auto-reader/actions/runs/43835295159),
job `133190291302`. It correctly emitted `full_product_validation=true` and
`scope_reason=full_changed_path`. All **34 classifier assertions**, **3,243
.NET tests**, **80 Rust tests** and **68 packaging controls** passed, with
**30 explicit .NET skips** and zero failures. Both release jobs were skipped.

The subsequent necessary documentation-only retention/outcome push remains
pending. No hosted time saving is claimed before that path completes. Full
product and native acceptance remain separate.
