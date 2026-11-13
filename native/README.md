<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native processing worker

`graphreader-engine` is a small Windows x64 Rust process used by the WPF
application's **Suggest axes (review only)** action. It proposes geometry from
original pixels. It does not recognize numeric labels or apply calibration.
The user checks or corrects the three anchors, enters the actual axis labels,
and confirms them before Apply changes the project.

The initial detector uses dark row/column projections and, where present,
outward x-axis tick strokes. Ambiguous or absent axes return a failure. This
is assistance for review, not an approved automatic graph reader. It requires
upright, separated panels; skewed, faint, interrupted or unusually styled axes
can require manual calibration.

## Build

Install a Windows x64 Rust MSVC toolchain and the Visual C++ build tools. Keep
the toolchain's `share/doc/rust/COPYRIGHT-library.html` available. From the
repository root:

```powershell
./packaging/native-engine/Build-NativeEngine.ps1
cargo test --manifest-path native/graphreader-engine/Cargo.toml --target-dir artifacts/native-engine --offline --locked
```

The crate has no third-party Cargo dependencies. The application build invokes
the same helper and includes the worker, standard-library notices and compiler
identity in its `native` directory. The MSVC C runtime is linked statically;
an independently installed VC runtime is not required by this worker. This
does not establish clean-machine readiness of the rest of the application.

## Runtime boundary

The child uses below-normal priority and a Windows Job Object hard CPU limit
of approximately half a logical core in aggregate. It fails before processing
if the limit cannot be applied. The limit is interval-based and does not
prevent momentary per-core peaks. It applies only to the child, not to other
in-process ONNX work or the WPF interface.

The bridge accepts packed grayscale or RGB frames with dimensions 32 through
4096 and at most 16,777,216 pixels. Input and response sizes are bounded. A
request has a 60-second deadline; cancellation kills the child process tree.
No image, result or cache is written by the worker. All proposed coordinates
refer to the input's original pixel space.

`examples/blocking_test_worker.rs` is a test-only cancellation fixture. It is
not included in application output. To exercise the process bridge:

```powershell
cargo build --manifest-path native/graphreader-engine/Cargo.toml --target-dir artifacts/native-engine --example blocking_test_worker --offline --locked
$env:GRAPHREADER_RUST_WORKER = "$PWD/artifacts/native-engine/release/graphreader-engine.exe"
$env:GRAPHREADER_RUST_BLOCKING_WORKER = "$PWD/artifacts/native-engine/debug/examples/blocking_test_worker.exe"
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj -c Release --filter FullyQualifiedName~RustAxisProposalTests
```
