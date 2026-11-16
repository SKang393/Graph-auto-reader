<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Internal preview 0.4.59 verification

This single integrated ManualPreview packages five accumulated runtime changes
since 0.4.58: [original-pixel support for affine geometry](AFFINE-ORIGINAL-SUPPORT-20261116.md),
[Rust content-span recovery](RUST-CONTENT-SPAN-RECOVERY-20261116.md),
[identical-family final-fit reuse](AXIS-FINAL-FIT-REUSE-20261116.md),
[stale label-read status prevention](CALIBRATION-STALE-STATUS-20261116.md), and
[abandoned label-read cancellation](CALIBRATION-LABEL-CANCELLATION-20261116.md).
The new Rust fixture also completed one
[owned automatic calibration integration check](OWNED-CONTENT-SPAN-CALIBRATION-20261116.md).
The recorded real-development geometry refusal and its unreached downstream
stages remain unchanged. This package does not imply representative accuracy,
native visual acceptance or production approval.

## Source and one hosted build

Prepared source `d243e5b1d83556bdb3f363821934570c75e1f9ee` passed
[full Windows CI 43886948510](https://github.com/SKang393/Graph-auto-reader/actions/runs/43886948510),
job `133367556732`, from **2026-11-16T14:33:20Z** to **14:41:22Z**.
Scope was `full_changed_path`, `full_product_validation=true`, with no reused
parent run. Results: **3,276 .NET passes**, 30 explicit skips and zero failures;
all **1,348 App tests**, **272 Axis tests**, **89 Rust tests**, 44 CI-scope
assertions, 68 packaging checks, 62 version checks, six ledger checks,
12 portable checks and nine localization checks passed. Release jobs skipped.

One [hosted build 43888727889](https://github.com/SKang393/Graph-auto-reader/actions/runs/43888727889),
job `133373828185`, was created at **14:43:25Z** and completed at **14:48:17Z**.
It passed **1,512 tests**, with nine explicit skips and zero failures:
App 1,346/2 skipped, Domain 23, Integration 143/7 skipped. The two optional
actual-worker App cases were skipped only in this hosted fast-test workflow;
both ran in the exact-source full CI above. The existing reviewed OpenCV donor
and pinned OCR materializer were reused. No new dependency or model was added.

## Uploaded artifact and download

[Artifact 14237011079](https://github.com/SKang393/Graph-auto-reader/actions/runs/43888727889/artifacts/14237011079)
is named `GraphAutoReader-0.4.59-internal-preview-43888727889`. GitHub reports
creation at **2026-11-16T14:48:09Z**, expiry **2027-02-14T14:43:26Z**, and the
exact prepared source above. The single local download ran from
**14:50:28.7463257Z** to **14:50:32.2196056Z**, taking **3.473455 seconds**,
with empty stderr. Its **87,398,469 bytes** match GitHub's digest:
`d63751a116fc6cea8f5903c4adb05c8f6a6669634d35d15b5e3dc5a08dcfb86a`.

The downloaded build confirms directory `0.4.59-20261116T144747343Z-d243e5b1`
and build time **2026-11-16T14:47:47.3430437Z**. Its inner
`GraphAutoReader-0.4.59-win-x64-internal-preview.zip` is **87,656,367 bytes**.
All **477 payload files / 196,430,471 bytes** matched the ZIP before and after
the checks. Safe paths, no private/user files, no reparse entries, no package
Data, clean source/version, ManualPreview mode and static Rust provenance passed.

| Identity | SHA-256 |
| --- | --- |
| Inner ZIP | `1334f7262845f92000134d39d4559fbd9d94fe64ab44ba42794cd81b210138c9` |
| Original hosted receipt, 350 bytes | `631de31dac5991f1a8db6bd00eaf3aec62a15cc08fb83f6efb7432f8e242480f` |
| App EXE | `9fac4f86967bef508988d3deb9f8a06f8817f87a53981011d1c125ed6f7a5306` |
| App DLL | `efaa5b367b6fd09298662105df1528b199c12488d37cbda1521258aceb5c9a8b` |
| Build metadata | `4f16f2a6c130735f54fbdb01c009471be60a490cd43d06710f29cc1269a08c5d` |
| Rust worker | `f4acf754e0669962f139d43858559715ff6ce9108d4e1156247c8ba7a28c7dbe` |
| Local verification result | `b47c34d9d7b9732cfb202f2f8ca0278c9f78aab92d44f7f8b6ba9fe514413fcd` |

The EXE's FileVersion is `0.4.59.0`; ProductVersion is
`0.4.59+d243e5b1d83556bdb3f363821934570c75e1f9ee`. All 27 `LICENSES` files,
five review-OCR files and eight runtime binaries match 0.4.58. The Rust
standard-library notice is also unchanged:
`5647be074c8edf7339fd863055923a8fc80bc5610a8d4661ec3b767b9d392c27`.
The hosted compiler remains Rust 1.99.1 and the Cargo lock hash is unchanged.
OpenCV remains `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`,
with clean-machine and release approvals false. Review OCR remains
`7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`.

## Verification boundaries

The disposable verifier reuses the repository's safe-archive, payload and
hosted-preview identity checks. Review required exact uploaded receipt bytes,
embedded executable version/source checks and clearing inherited optional
runtime overrides in smoke children. Both scripts then passed syntax parsing.
The verifier binds the independently checked outer digest/size and source,
checks every payload byte, compares current runtime/model/license files, and
uses isolated mutable Data for bounded hidden startup checks. Production mode
must continue to refuse with exit 2. One owned Rust transport/recovery request
is permitted only if the hosted worker differs from the already exercised
local worker; identical bytes require zero repeated worker calls.

One actual invocation completed all eight declared stages, with zero failed
or unreached stages, no retries and empty stderr. Wrapper wall time was
**21.5267 seconds**; verifier time was **20,674.9141 ms**. The command was:

```text
pwsh -NoProfile -File artifacts/preview459-check/Verify-Preview459.ps1 -ExpectedOuterSha256 d63751a116fc6cea8f5903c4adb05c8f6a6669634d35d15b5e3dc5a08dcfb86a -ExpectedOuterBytes 87398469 -ArtifactId 14237011079 -RunId 43888727889
```

Script SHA-256 values were `eacdfe1b68fa17108ab0348873de23400b6063b5b63f11e86c3195be4debe507`
for the verifier and `b52ddf0010199d628e5828dccdb4360cf8b6924d0f6c6c7daf61e56ecb9730c2`
for its conditional worker client. These disposable clients reuse the tracked
archive helpers and the existing `AxisDecisionDiagnosticClient` protocol.

Hidden `--portable-smoke` returned **0 in 1,007.4482 ms**.
`--production-runtime-smoke` returned the required refusal **2 in 376.6068 ms**.
Both had empty stdout/stderr and a 30-second limit. Two three-byte settings
files were created only in isolated `preview459-check/smoke-data`, outside
the package and user Data.

The packaged Rust differs from the local `eeb2515a...` worker, which was built
with Rust 1.99.0. Exactly **one** direct `--diagnostics` request therefore sent
only the unchanged 512 by 512 Gray8 owned PNG, SHA-256
`2a8b612aa33cb899dd4dc08689693ee53b57641be7a1f15c9c7fb1875ffa53b1`.
The worker exited **0 in 318.3286 ms**, with 100 stdout bytes and empty stderr:
`TRACE1 CONNECTED_RUNS OK 1 1 1 0 0 0 0 1`. It returned the exact fixed
box and observed tick arrays. The child retained its existing half-core Job
and the client enforced a 20-second outer limit. No parent CPU cap is claimed.
This single transport/recovery result is not a speed comparison or a repeat
of the complete workspace/OCR calibration control.

All sixteen shared Data files, the 0.4.58 keeper ZIP and owned input stayed
unchanged. No Data appeared inside the staged package. No private or independent
input, OCR comparison, training, Apply, export, native desktop input, paid
credit or banked reset is part of this delivery. It is an internal portable
preview, without an installer or public release.

## Activation and bounded retirement scope

Actual download and headless verification pass. Activation is pending the
delivery ledger/source record reaching `origin/main`. Use the existing helper's
`Inspect` then `Activate` modes with this exact ZIP/source and previous keeper
`0.4.58-20261116T082429970Z-4279db15`. Preserve shared Data and all original
inputs. The current 0.4.58 keeper remains intact until that transaction succeeds.

After activation and the actual latest-launcher smoke, retire the superseded
keeper (478 files / 284,040,423 bytes), completed `hosted-preview-459` download
staging and `preview459-check` scripts/receipts/isolated settings after their
outcome is pushed and consumers finish. The moved 0.4.59 payload and ZIP are
the new keeper, not staging leftovers. The owned content-span PNG's named
consumer is now complete; retire its 2,333 bytes after the result is pushed.
Native tilted calibration Apply/Undo remains blocked by the desktop tool's
stale Escape-stop state despite renewed permission and no new Escape.
