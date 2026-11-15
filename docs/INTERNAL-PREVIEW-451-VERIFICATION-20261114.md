<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Verified internal preview 0.4.51

Build 451 integrates the [native review corrections](NATIVE-REVIEW-CORRECTIONS-20261114.md),
[human-corrected failed OCR rows](HUMAN-CORRECTED-TICK-EVIDENCE-20261114.md), and
[bottom-corner Y-label repair](BOTTOM-CORNER-TICK-LANES-20261114.md).
The exact source is `e54917917d1acdc3aed2c556f3d2faf0bcbff253`, built at
`2026-11-14T23:53:20.5485517+00:00` as ManualPreview.

The [hosted build](https://github.com/SKang393/Graph-auto-reader/actions/runs/43655078718)
passed 1,215 tests with seven existing skips and uploaded
[artifact 14137494218](https://github.com/SKang393/Graph-auto-reader/actions/runs/43655078718/artifacts/14137494218).
The separate [Windows CI run](https://github.com/SKang393/Graph-auto-reader/actions/runs/43655058286)
passed 2,753 .NET tests across 13 suites, with 30 existing skips and no failures;
52 Rust tests and 62 version-policy checks also passed. Its packaging validation
completed successfully. The corrected device test passed in both hosted runs.
Earlier run `43653973013` failed that test's premature completion condition;
the [integration record](APP-REVIEW-INTEGRATION-TEST-20261114.md) preserves that result.

## Downloaded package verification

All archive paths and extracted bytes were verified before execution: 477 files,
196,157,063 bytes, no mutable Data payload, expected source/version, clean source
identity and ManualPreview mode. The current reviewed OpenCV runtime, OCR model,
manifest and model notices match their prior hashes. No dependency, model or
license changed.

| Identity | SHA-256 |
| --- | --- |
| Hosted archive, 87,300,626 bytes | `66b1844e7b5952f2eeb99a4fb856bb872efb538473d0af12c91516503f678b91` |
| Internal preview ZIP, 87,558,229 bytes | `6bf48f3f16513a2c3766381d43ad47b8e65406e9499663c3ecde31319c31bd44` |
| App EXE | `144d4cfab07d81429de6366a76a211fe515fa8227578b9c857ed76e5cca6efed` |
| App DLL | `5be01923609dfc144f407db353bc7097f47c773bc44140b87af04d387ce041ff` |
| Rust worker | `efed45d5c1de473599cf94ace56aa1e1b3bfd40228289db23ecce755b66e9794` |
| Build metadata | `58249e62ebcf727a126f287f74a648be532cc67370ed195d0bf4430cb20fba46` |
| Receipt | `4d020da8bc54aa8bc612dba98c9e915ecaa398d2d4eed392f092d0f27a466556` |

The local `verification.json` hash is
`1ac1e347de306d625daf4eed382c2b3a93a46a71d0722e7ee63489aabb18b483`.
GitHub retention expires `2027-02-12T23:48:43Z`.

## Packaged execution

With isolated staging data, `--portable-smoke` exited 0 in 1,631.2818 ms.
`--production-runtime-smoke` returned its expected exit 2 in 759.6927 ms because
production approval remains incomplete. Startup receipt SHA-256:
`f9d6b24164e1f3a15d735df610c53440b21301cb1ffdb3cc772565891d3e4605`.

The [owned actual-worker check](BOTTOM-CORNER-WORKER-451-DIAGNOSTIC-20261114.md)
passed both narrow and wide controls: six numeric labels, six measured pairs,
complete proposals and correct pre-recognition/final Y roles for the corner
zero. It used explicit owned geometry, not Rust detection or private answers.
Cold/reused CPU pipeline times were 8,699.2094/2,140.098 ms. The 48.833-second
whole probe also includes fixture rendering, identity checks and lifecycle work;
it is not the latency of one interactive OCR call. Two controls are not an
accuracy or latency-distribution benchmark.

## Activation and limits

The verified package is staged at
`artifacts/dev-portable/builds/0.4.51-20261114T235320548Z-e5491791`
under the primary checkout. Stage preflight and execution found zero consumers
and preserved shared Data. The ledger now identifies 451 for retention; latest
pointer activation and separate 449 retirement await this record's push.
The guarded helper SHA-256 is
`03aab1be01ac5805185bca0dcc894788dc7689d0d13de5cae96bc754097ab727`.

Native 451 correction controls and fresh-process restoration remain pending.
This internal ZIP is not a public release, installer verification, clean-machine
acceptance or production model approval. The latest real-data diagnostic still
has zero complete calibrations; no private rerun was performed for this record.
Retire staging leftovers after their active consumers finish, preserving the
owned images still referenced by the saved native QA project.
