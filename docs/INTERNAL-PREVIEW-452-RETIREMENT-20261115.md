<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Internal preview 0.4.52 staging retirement

Prepared cleanup scope for the uploaded 0.4.52 staging directory only. This is
not a build, package, release, or production-approval action.

| Target | Files | Bytes | Manifest SHA-256 |
| --- | ---: | ---: | --- |
| `artifacts/hosted-preview-452/` | 7 | 87,306,643 | `8ded3193a7138f96bc564db015047cd89b7d6011c8bd6f747f25deea4feefc3a` |

The hosted ZIP is `87,300,880` bytes with SHA-256
`e6003ae26183556067030966abc1994f98ec3d600e8938a71d5962f2597097ef`.
The retained verification and receipt record the upload. The final 0.4.52
build and ZIP are separate keepers and are checked by the helper before and
after cleanup.

The guarded helper is
[`retire-hosted-preview452.ps1`](../artifacts/storage-retention-check/retire-hosted-preview452.ps1).
Its reviewed SHA-256 is
`889f15be5191604edeace88a171c03e7bbb8ba273793623a81247fe7b40d880c`.
PowerShell parsing passed. The original owned source keeper uses its verified
primary-checkout path, not a nonexistent worktree copy.
It requires a pushed scope commit, exact file inventory and hashes, path
containment and reparse checks, a normal-host process-consumer check, and
before/after snapshots of shared Data, QA evidence, and package keepers. It
accepts either the staged state (0.4.51 remains latest and retained while 0.4.52
is pending) or the activated state (0.4.52 is latest and retained). It removes
only the directory in the table and writes a receipt. The helper was not
executed for this record; no deletion is claimed.

Preserved scope includes the final 0.4.52 build/ZIP, the currently retained
0.4.51 build/ZIP until activation, shared `Data`, and the named native/Flex 5
QA inputs. The 0.4.51 application was still open during preparation; it is not
a consumer of the hosted staging directory, and the helper snapshots shared
Data to ensure it remains unchanged. No user projects or source files are in
the target.

**Status:** pending root review, pushed-scope validation, and explicit helper
execution. Do not infer cleanup completion from this preparation record.
