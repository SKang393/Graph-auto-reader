<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Completed diagnostic output retirement

The four completed output folders below have pushed outcome records in scope
commit [`e5491791`](https://github.com/SKang393/Graph-auto-reader/commit/e54917917d1acdc3aed2c556f3d2faf0bcbff253). Their owners released active consumers. Exact-name searches in the active and primary
`src`, `tests`, `tools`, `ml`, `packaging`, and workflow sources found no folder
consumer. The prepared-calibration probe command remains in the tool source and
README, but it does not consume this output directory; the tool and its inputs
are preserved.

| Output folder | Files | Bytes | SHA-256 of sorted path/length/file-hash inventory | Pushed outcome |
| --- | ---: | ---: | --- | --- |
| `artifacts/tick-human-correction-check/` | 223 | 99,593,857 | `ec36bc85bbf1493f6393cc6b38ec9b18c754fd9931847642d8e5356e46f8d6e8` | [Human-corrected tick evidence](HUMAN-CORRECTED-TICK-EVIDENCE-20261114.md) |
| `artifacts/app-integrated-20261114/` | 220 | 100,980,337 | `4a44607036247ed320f947ae57d3e4d35228c91856e9fb5636c07a7f10e7a0e0` | [App review integration tests](APP-REVIEW-INTEGRATION-TEST-20261114.md) |
| `artifacts/corner-tick-classification-check/` | 401 | 202,854,766 | `c0de240c5ec3d3b160bceb23c8dadc46dc7769ec579322154e0fa176bd8dd276` | [Bottom-corner tick lanes](BOTTOM-CORNER-TICK-LANES-20261114.md) |
| `artifacts/prepared-calibration-450-check/` | 53 | 42,670,058 | `c3bdcfc580035c428711fe02a5d502cb8b5fb84a39fab2b1fde405bc64e40232` | [Prepared calibration 450 diagnostic](PREPARED-CALIBRATION-450-DIAGNOSTIC-20261114.md) |
| **Total** | **897** | **446,099,018** |  |  |

The guarded helper is [`artifacts/retire-completed-checks-20261114.ps1`](../artifacts/retire-completed-checks-20261114.ps1). It requires the exact pushed scope commit, checks each full file inventory and reparse-point boundary, and refuses if normal-host CIM process inspection fails or finds a consumer. It hashes protected keepers before and after the exact four deletions: shared portable `Data`, the sole active 0.4.49 build and package, the complete hosted-450 workflow subtree, `artifacts/flex5-449-check/owned-ticks.png`, hosted-450 `owned.png` and `padded.png`, and native-449 UI QA files. It writes a compact receipt outside the target folders.

The hosted-450 parent and its workflow assets, current runtime/model inputs,
original corpus, user data, source, ledger, and native QA files are outside the
deletion list. The initial restricted-shell process query was denied; the lead
subsequently ran the required normal-host preflight successfully with zero
consumers and exact inventory matches.

Execution completed at `2026-11-15T00:04:46.5693377Z`: all four targets absent,
897 files / 446,099,018 path bytes retired, current 449 unchanged and all keeper
hashes unchanged. Keeper inventories were 15 shared-data files, 478 current
build/package files, five workflow files, four native QA files and the three
additional source PNGs. Original data and runtime inputs were outside scope.

The final helper, including the saved project's Flex5 input keeper, has SHA-256
`99d8cb0e99da0b4129a7757766ce74d4d6a6454fe9cab8d06cfc4fc05bec17b5`.
Receipt: `artifacts/storage-retention-check/completed-checks-20261114.json`,
SHA-256 `13e96c43c0d5e36157b843e40517a99f8b03940f79b585802f30415ae2bb0e2f`.
