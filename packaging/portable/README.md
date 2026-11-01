<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Windows portable definition

`portable.json` records the enforceable portable behavior. The build
script creates an empty `portable.mode` beside `GraphReader.App.exe` in
portable staging. The domain path policy resolves that sentinel to `Data`
beneath the extracted application directory. App startup initializes the
mutable roots, verifies write access, and reports a portable-specific actionable
error when initialization fails. Focused domain and integration tests verify
sentinel path selection, installed/portable isolation, and structured read-only
failure handling. They do not prove every mutable subsystem uses those roots in
an extracted application. Runtime sentinels are generated and remain ignored by
Git.

The ZIP is created directly from that staging directory and retains root-level
application, license, contract, manifest, and build-metadata files. Publication
still requires clean-machine launch, registry independence and path isolation
checks from paths containing spaces and Unicode characters, including
verification under an actual write-denied profile. The focused read-only
integration test uses a recording directory-access fake, so clean-machine
runtime verification remains
`unverified-clean-machine`; the tracked implementation status is
`implementation-complete-runtime-unverified`.
