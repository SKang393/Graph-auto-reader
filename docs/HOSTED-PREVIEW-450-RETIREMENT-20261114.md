<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Hosted preview 0.4.50 staging retirement

The 0.4.50 hosted-preview result is recorded in pushed scope commit
[`e80b5486`](https://github.com/SKang393/Graph-auto-reader/commit/e80b5486012338663273e6d2bd4700030b448818).
Before preparing the next package, retirement covered only these three local staging targets
under `artifacts/hosted-preview-450/`:

| Target | Files | Bytes | SHA-256 / verification |
| --- | ---: | ---: | --- |
| `build/` | 477 | 196,152,967 | Each file hash is checked against the inner ZIP. App EXE `da892f5a59faddfe24a42bf48ecca1548f6b2851921383d91c0f133421bc885e`; build-info `7eba7bc6d9442dfa698a54d6a2c60657f4cc7b6330f63dc0c0f81b001b1e35c5`. |
| `GraphAutoReader-0.4.50-win-x64-internal-preview.zip` | 1 | 87,556,113 | `492481605a4de11f777824fabac34e210719f7831c1334e39061401babdcee60` |
| `hosted-artifact.zip` | 1 | 87,298,192 | `d4916ea104c8ac04ef039aa7d16d0b4b118bbe334c9bbeaffb4250c58ba05581` |

The target total is 479 files / 371,007,272 bytes. The prepared helper is
[`artifacts/retire-hosted-preview450.ps1`](../artifacts/retire-hosted-preview450.ps1).
It required the exact pushed scope commit, validated the
sole retained 0.4.49 ledger entry and latest pointer, verified the staged ZIP
against all 477 extracted files, rejected reparse points or active CIM consumers,
and compared pre/post hashes for shared `Data`, the current 449 build/package,
the complete 450 workflow subtree, and native 449 QA files.

The entire `workflow/` subtree, including `owned-marker-workflow.png`, remains
in place. The helper does not target the hosted-preview parent, remote GitHub
artifact, current build, user data, or original corpus.

Separate `ValidateOnly` completed with zero target reparse points and zero
consumers. `Execute` then completed at `2026-11-14T23:40:12.4474713Z`: all three
targets were absent, 479 files / 371,007,272 path bytes retired, and keeper hashes
were unchanged. Preserved inventories were 15 shared-data files, 478 current
build/package files, five workflow files and four native QA files. The latest
pointer remained on 0.4.49. The hosted 450 artifact remains available through
its [delivery record](INTERNAL-PREVIEW-450-VERIFICATION-20261114.md).

Helper SHA-256: `db8581710b7786503c3a32ec18f72fca6b24359d0ef5c7aecac83bc3ad85ef06`.
Receipt: `artifacts/storage-retention-check/preview450-retirement.json`, SHA-256
`a8f9f8e679d5f82a7aaed7f9e98a7b072b495f4e33e18850a98161f99d59bf79`.
