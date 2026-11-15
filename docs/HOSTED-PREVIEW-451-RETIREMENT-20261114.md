<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Hosted preview 0.4.51 staging retirement

The hosted 0.4.51 delivery and active build ledger were first recorded in
`c2efa5b1`, then extended with native and calibration evidence in pushed scope
[`d5a34366`](https://github.com/SKang393/Graph-auto-reader/commit/d5a34366fcb3a42ecef7005f81cc7335a4a332bd).
Only the local `artifacts/hosted-preview-451/` staging folder is in scope for
retirement. The packaged 0.4.51 build remains the sole active preview at
`artifacts/dev-portable/builds/0.4.51-20261114T235320548Z-e5491791`; the remote
GitHub artifact is not a target.

The staging folder contains 11 files / 87,309,428 bytes. Its exact sorted
path/length/file-hash manifest is
`b2cda4f95a39b33602c78f5037a93bf9e766fc8ddab7926498c4e2bccd9a3188`. The
87,300,626-byte `hosted-artifact.zip` has SHA-256
`66b1844e7b5952f2eeb99a4fb856bb872efb538473d0af12c91516503f678b91`; the
remaining contents are smoke-data settings, smoke stdout/stderr, receipt,
startup-smoke and verification JSON, and the local `verify.py` script.

Exact-name searches in the active and primary source/config trees found no
consumer of this staging path. The separate completed calibration probe and
`corner-tick-worker-451-check/tool` remained outside scope. The helper also
preserved the active 451 package, shared `Data`, both native-449 and native-451
QA folders, and the referenced Flex5 and 450-workflow PNGs by pre/post hashes.

[`artifacts/retire-hosted-preview451.ps1`](../artifacts/retire-hosted-preview451.ps1)
requires pushed-scope validation, exact inventory and reparse checks, and a
normal-host CIM consumer scan before removing only that one staging folder. The
CIM query was initially denied in the restricted review shell. Separate
normal-host validation then passed with zero consumers, exact target inventory
and 16/478/1/4/5/1/1 protected files across Data, active build, pointer, native
folders and input PNGs. Execution repeated those checks and completed at
2026-11-15 00:43:45.5302253 UTC. The 11-file / 87,309,428-byte staging directory
is absent; all protected hashes and the sole active build remain unchanged.

Helper SHA-256:
`18ab767b9c0d0c63be175aef5d398ccf8bbb905fd27488bbb1d2546220f303ff`.
Receipt `artifacts/storage-retention-check/hosted-preview-451-retirement.json`,
SHA-256 `09115b61125940d9a50a1ed431702cc87764bd56ab938771426328c094944bea`.
