<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Internal preview 0.4.50 verification

## Build and hosted delivery

Preview 0.4.50 was built from clean source `99864561c4f4e18e6e39b34e258a0ed7434ed623`
at `2026-11-14T22:31:56.2731873Z`. The receipt sets `releaseApproved=false`.
The extracted package has 477 files / 196,152,967 bytes; App EXE SHA-256 is
`da892f5a59faddfe24a42bf48ecca1548f6b2851921383d91c0f133421bc885e` and App DLL
SHA-256 is `49e375f3110f62c386245210d091010f3ccd722216f26ca440c6958afdf9855a`.
The packaged build lists no available model IDs and marks enhancement, axis,
OCR, markers, legends and phases unavailable.

Source CI [run 43648137432](https://github.com/SKang393/Graph-auto-reader/actions/runs/43648137432),
job `132610399273`, succeeded: 2,732 .NET tests passed, 30 skipped, 0 failed;
52 Rust tests passed; 62 version-policy checks and 68 packaging verification
tests passed. Release candidate and publish jobs were skipped. The hosted
internal-preview upload [run 43648170771](https://github.com/SKang393/Graph-auto-reader/actions/runs/43648170771),
job `132610506945`, succeeded.

- Artifact [14134289339](https://github.com/SKang393/Graph-auto-reader/actions/runs/43648170771/artifacts/14134289339):
  `GraphAutoReader-0.4.50-internal-preview-43648170771`, 87,298,192 bytes;
  created `2026-11-14T22:32:26Z`, expires `2027-02-12T22:27:16Z`.
- Hosted wrapper ZIP SHA-256:
  `d4916ea104c8ac04ef039aa7d16d0b4b118bbe334c9bbeaffb4250c58ba05581`.
- Inner portable ZIP SHA-256:
  `492481605a4de11f777824fabac34e210719f7831c1334e39061401babdcee60`.

The local hosted-artifact ZIP hash and size matched the verification metadata;
the inner package hash matched as well. Verification checked all 477 extracted
files. The release mode and publication remain disabled.

## Owned runtime checks

The packaged calibration workflow check used the owned 640x480 fixture and
completed with three CSV rows and three reopened points. It simulated explicit
review/acceptance, saved a user-confirmed manual three-anchor calibration, and
retained zero tick-evidence IDs. Rust was not invoked. The review-only report
does not indicate production approval. The first harness attempt was rejected
by a generic precondition before processing; its summary indicated that the
output directory needed to be empty. An empty output directory was created and
the retry completed. Workflow elapsed time was
5,529.245 ms. The OCR report's own worker total was 2,579.835 ms; these are
different timing scopes.

The native-axis check covered clean, padded, connected-corner, empty-corner,
two-panel and blank controls. The expected two-panel ambiguity and empty/blank
refusals remained. Cancellation was observed during startup and the child exited
at completion; this does not demonstrate cancellation during OpenCV computation.
The native-tick report used OpenCV review geometry and six estimated OCR label
positions, with no Apply and no production approval. Its report total was
5,012.385 ms, including 2,291.274 ms for the OCR worker and 810.988 ms for
recognition. These are report timings, not a separately measured process wall.

The exact-package process smoke reported portable exit 0 (4,060 ms) and
production-runtime exit 2 (1,097 ms), the expected rejection for a
ManualPreview build. It used
`GRAPHREADER_DEV_PORTABLE_DATA_ROOT=artifacts/hosted-preview-450/smoke-data`.
The smoke results came from command output and have no separate persisted
receipt in the staging directory.

## Limits and activation

These checks cover one owned workflow and diagnostic controls, not recognition
accuracy, native 0.4.50 correction-UI acceptance, a clean-machine installer, or
a public release. No installer was created. The 0.4.49 build remains the active
retained package and pointer; 0.4.50 is not yet activated. This record does not
change the build ledger or latest pointer.
