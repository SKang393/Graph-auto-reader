<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Connected-ink axis repair

A padded owned graph with a marker outline connecting an interrupted axis was
rejected as `NO_AXIS`, even though the outline supplied an observed ink path.
The matching empty-gap control was rejected as intended. The original focused
Rust reproduction reported `CONTENT_EXTENT DISCONNECTED` for the connected case.

The fallback now accepts a bounded local eight-connected path between exact dark
axis endpoints. It does not alter pixels, infer an intersection, or relax the
existing tick requirements. Tick evidence within a recovered hole and its two
adjacent endpoints is omitted; a real tick there can therefore remain unavailable.
The request-wide bridge search is capped at 16,777,216 visited pixels, and
candidate-pair search fails closed if that work budget is exhausted. The
existing 128-pair and Windows CPU Job limits remain in place.

## Owned validation

The retained 0.4.48 worker and the new release worker were exercised as actual
child processes with in-memory GRA1 grayscale requests. Two 768 × 768 controls
used the same 5-pixel interrupted axis, once vertical and once horizontal. Both
old-worker connected-outline requests returned `ERR NO_AXIS`; both new-worker
requests returned the expected original-pixel axes and three unaffected measured
ticks per axis. For both orientations, clean axes and matching empty gaps had
identical response text and exit code across workers. A 2048 × 2048 blank control
also returned identical `ERR NO_AXIS` results. New-worker diagnostic output's
first line and exit code matched its ordinary output for each control.

One-run wall times include process startup and are not a benchmark: connected
outlines took 31.273 ms vertical and 25.243 ms horizontal with the new worker;
the blank took 27.658 ms. Clean and empty controls ranged from 23.326 to
214.724 ms on the new worker. The explicit work-budget exhaustion regression
is a unit test; the child-process check does not claim runtime exhaustion was
observed. The complete 48-test Rust suite passed in 0.05 seconds test time.
Independent source review found no material issue.

```powershell
cargo test --offline --manifest-path native/graphreader-engine/Cargo.toml --target-dir artifacts/native-engine
powershell.exe -NoProfile -ExecutionPolicy Bypass -File packaging/native-engine/Build-NativeEngine.ps1 -RepositoryRoot E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2
python artifacts/connected-ink-check/verify.py
```

The verification script and raw owned result are temporarily retained in
`artifacts/connected-ink-check` for the active integrator. No private images,
model inference, package build, or production-approval decision was involved.
This demonstrates the specified synthetic outline cases, not general axis
accuracy or robustness on all marker shapes.

| Identity | SHA-256 |
| --- | --- |
| Rust source | `2d532f8af28a1ee32bcec4c4af5777756285b67406bb92bddb902cd416e75b2b` |
| Retained 0.4.48 worker | `99cb320d3f9674d856c638a0fb7af5dbd9d565004f3b32ec1760588facd4e830` |
| New local release worker | `46e6dcf885760e4ce61c956ef93802b53e2dd51ddb0118ac0c84691e3dda94a0` |

## Integrated owned workflow

One additional label-bearing 640 × 480 control used the existing grid fixture,
with the same connected outline interrupting its vertical axis. On the exact
processed grayscale frame, the retained 0.4.48 worker returned
`ERR AMBIGUOUS_AXIS`; the repaired worker returned the original-pixel plot
(100, 80) to (580, 380), X ticks 140/340/540 and Y ticks 80/230/380. A SHA-256
comparison confirmed that the frame sent to both geometry workers matched the
image consumed by the integrated workflow after its deterministic marker drawing.

The unchanged 0.4.48 App/OCR and existing headless workflow probe completed
with the local repaired Rust worker: all six numeric labels paired to measured
ticks, with no estimated positions. The control simulated explicit review and
applied calibration (1,0), (1,10), (3,0), then accepted two marker suggestions,
exported three CSV rows and reopened the user-confirmed manual calibration and
three points. The CSV uses the existing observation-order X values 1, 2, 3;
the three Y values are approximately 5. Process wall time was 5.518 seconds,
including 2,464.724 ms reported OCR and 170.442 ms marker processing. Exit was
zero and stderr empty. This is owned headless service evidence, not a WPF visual
check, general accuracy result or production approval.

The owned result SHA-256 is
`3a5a5f144b360e94cd51972d2d4988fa0db8ca260567dcd4920fe45ec9693a7e`;
the reopened project SHA-256 is
`155815fce751c7942b519392127de6ad79c49366ad15d85a5e36fffa9cdc23a1`,
and the CSV SHA-256 is
`a5cab504b91b88ae3c3f6ff976e298cc3d75e47139102400dc64c0e335635302`.
The verification scripts, source, result and workflow artifacts remain in the
active `artifacts/connected-ink-check` directory for the integrator.
