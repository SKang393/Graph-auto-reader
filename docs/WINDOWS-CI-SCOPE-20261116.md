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
only additions/modifications of `docs/**/*.md`, `docs/BUILD_LEDGER.json`, or
the exact developer-tool documentation path
`tools/GraphReader.CalibrationOcrProbe/README.md`.
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

The necessary documentation-only retention/outcome push `148a03c5` passed
[CI 43837679762](https://github.com/SKang393/Graph-auto-reader/actions/runs/43837679762),
job `133198283609`. It emitted `full_product_validation=false`,
`docs_only_verified_parent`, and exact predecessor run `43835295159`.
Exactly the seven scoped product/marker steps were skipped. All **34 scope
assertions**, **62 version checks**, **six ledger checks**, **12 portable checks**
and **68 packaging controls** passed; the other retained checks also succeeded.
Both release jobs were skipped. No product-test count is attributed to this run.

The full run's log spans `08:56:04.8922661` to `09:08:57.8161862 UTC`
(**12m53s**); the documentation run spans `09:12:41.6721677` to
`09:21:32.0173908 UTC` (**8m50s**). These are two observed hosted runs, not
a controlled performance benchmark. The remaining packaging work is deliberate.
Full product and native acceptance remain separate.

## Exact probe documentation exception

The outcome-only commit `a8fabe1362b406a0dcb81579acd4e61d476e6a5d` changed four
`docs/*.md` files and the probe README's retirement wording. Its predecessor
`1c4091cd` had already passed full Windows CI, but this README was outside the
allowlist, causing `full_changed_path` and another product build. Searches in
source, tests, packaging, workflows and tools found no runtime, build or
packaging consumer of this exact developer-usage document.

That pre-correction [run 43867346914](https://github.com/SKang393/Graph-auto-reader/actions/runs/43867346914)
finished successfully from **2026-11-16T12:31:28Z** to **12:43:06Z**. It ran
full product validation: **3,258 .NET passes**, 30 explicit skips, 89 Rust passes,
68 packaging checks and the old 34 classifier assertions, zero failures.
Release jobs skipped. This verifies the outcome checkpoint, not the subsequent
classifier correction, and demonstrates the redundant product work directly.

The classifier now permits only that exact case-sensitive README path. It does
not permit a `tools/**` pattern, other README files, code or project files.
Existing exact-parent trust, event, diff, status and fail-closed checks remain
unchanged. The focused command
`pwsh -NoProfile -File packaging/tests/Test-WindowsCiScope.Tests.ps1` passed
**44 assertions in 1.5854 s**, exit 0, at **2026-11-16T12:37:40.0539794Z**.
Added cases cover mixed documentation, missing trusted parent, deletions,
renames, nearby code/project files, case/backup variants and traversal paths.
Independent read-only review found no material issue. This changes CI scheduling
only; source changes still require full product validation and documentation
reuse still cannot package a release. No runtime, dependency or model changes
are part of this correction. Hosted validation of the correction is pending.
