<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Verified internal preview 0.4.52

The [hosted internal build](https://github.com/SKang393/Graph-auto-reader/actions/runs/43666480287)
completed for source `68781bcce22cf925bff2f99b21c139b622117c06`. Its build
metadata records ManualPreview, clean source and build time
`2026-11-15T02:14:03.3596275+00:00`. Hosted tests passed: App 1,051, Domain 23
and Integration 143, with seven Integration skips.
The executable reports FileVersion `0.4.52.0` and ProductVersion
`0.4.52+68781bcce22cf925bff2f99b21c139b622117c06`.

The [single artifact 14142820090](https://github.com/SKang393/Graph-auto-reader/actions/runs/43666480287/artifacts/14142820090)
expires `2027-02-13T02:10:10Z`. The server-provided outer digest was verified;
archive paths, source/version and receipt were checked, and all 477 extracted
files matched the inner ZIP, totaling 196,158,087 bytes. Mutable Data was not
included. Reviewed OpenCV, OCR model and manifest hashes matched the existing
pins; required project, OpenCV and model notices were present, and the Rust
standard-library notice matched its build metadata.

| Identity | SHA-256 |
| --- | --- |
| Hosted archive, 87,300,880 bytes | `e6003ae26183556067030966abc1994f98ec3d600e8938a71d5962f2597097ef` |
| Preview ZIP, 87,558,560 bytes | `47e7643e3f2ea2adb20d6899914228b0e5de5cd8e0c72e398638cfb593ebba8f` |
| App EXE | `c7979e7d88482fb239e6655d4939aea15755d605a472730c116776cda1da8e27` |
| App DLL | `c04429796bc17ee3f44cd1b7e548e3c524a3733910af2b2dc33440ef4d8e33e1` |
| Rust worker | `efed45d5c1de473599cf94ace56aa1e1b3bfd40228289db23ecce755b66e9794` |
| Build metadata | `03823e3c768059f6fe9726e7a24081090dc8c71e7f77dd060da9a7be4ee1f45f` |
| Hosted receipt | `65c609cd7aadb92be460b6a3016d450299e4cf8003af309afa73bd2c5bdfd02a` |

The staging `verification.json` SHA-256 is
`334b3b091a463f5774ab4321bca404e2c072702d837cab97e80af001861a7065`.
With an isolated staging data root, `--portable-smoke` exited 0 in 1,260 ms
and `--production-runtime-smoke` exited 2 in 438 ms, as expected for this
ManualPreview. Their receipt SHA-256 is
`85f94989ab31f7fd6393dfd1f94956d940ed55f1154659334e2bae9e8b6eea69`.

The separate exact-source [Windows CI run](https://github.com/SKang393/Graph-auto-reader/actions/runs/43666328944),
job `132666424267`, succeeded: 2,759 .NET tests passed across 13 suites with
30 skips, Rust passed 54/54, version policy 62/62, ledger 6/6 and portable
retention 12/12. The packaging validation steps also succeeded.

The verified files and inner ZIP were moved without duplication into
`artifacts/dev-portable/builds/0.4.52-20261115T021403359Z-68781bcc`.
The guarded staging command exited 0; all 477 payload files and the ZIP matched,
and shared Data was unchanged. The latest pointer still names 451 while its
window remains open. The ledger records 452 as a staged candidate and retains
451, keeping the existing launcher usable. Once the old window closes, a
separate retention update must be pushed before activation and old-build removal.

The reviewed helper is `artifacts/activate-preview452.ps1`, SHA-256
`49fc8bcc02873a4b46a47b82339febe493e215c0f16d70e39c5db1b62344241e`.
Its Stage ValidateOnly and Execute phases passed. Separate ActivateRetire
Activate/Retire validation and execution require the pushed ledger, exact
package hashes, no old/new app consumers, contained non-reparse paths and an
unchanged shared Data snapshot. The authorized retirement target is only
`artifacts/dev-portable/builds/0.4.51-20261114T235320548Z-e5491791`,
478 files / 283,715,292 bytes including its matching ZIP. No old build has been
removed at this checkpoint. Hosted staging remains until its evidence is
recorded and active verification consumers finish.

The [existing Flex5Laptop check](FLEX5-452-VERIFICATION-20261115.md) also passed
portable/Production-guard startup and two owned CPU OCR reads on one reused,
bounded worker. This internal ZIP is not an installer, clean-machine
acceptance, public release or production model approval. Native 452 interaction
is pending and is not implied by source CI, laptop or startup smoke results.
