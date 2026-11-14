<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Axis decision diagnostics, 0.4.47

## Scope and owned controls

The 0.4.47 package remains the fixed candidate. Its packaged Rust worker
`44258191be06bae2fa9536ebd84cca69631d7732fd3237bc6b001d71440a673d` was
compared against a separate diagnostic worker
`94893abf759ba074d1f857acd7c968645003ac2aec6082b16825a32b00d50d79`. The
diagnostic worker adds bounded decision traces; it does not replace packaged
Rust, change model/dependency/thresholds, or create a new package. The owned
checks verified ordinary-result parity across eight controls and 25 worker
invocations, with 14 parser checks (2 valid, 12 malformed). All eight controls
reported ordinary response equality; the self-test read zero source payloads
and wrote no images.

| Control | Trace result |
| --- | --- |
| Single axes | `PRIMARY`, `OK`, geometry returned |
| Unique outward grid | `GRID`, `OK`, 25 pairs checked, 1 qualified |
| Caption extent | `CONNECTED_RUNS`, `OK`, 1 pair checked and qualified |
| Two plots | `GRID`, `MULTIPLE_PAIRS`, 2 qualified; `AMBIGUOUS_AXIS` retained |
| Blank | `CONTENT_EXTENT`, `NO_AXIS` |
| Thick band | `GRID`, `THICK_ROW_BAND`; `AMBIGUOUS_AXIS` retained |
| Short axes | `CONTENT_EXTENT`, `INSUFFICIENT_TICKS`; `NO_AXIS` retained |
| Disconnected axes | `PRIMARY`, `DISCONNECTED`; `NO_INTERSECTION` retained |

The owned process exited 0 with empty stderr in `3.8185776 s`; internal elapsed
time was `3269.0082 ms`. Rust validation passed 44 tests. The instrumented
`main.rs` SHA-256 is
`5704c991986e3195f059190244cb4bb40916ae408e210d345b72ba543c08a93e`.

## Fixed real-dev diagnostic

The 120-item aggregate-only diagnostic uses the same frozen 0.4.47 assignment
and source identity as the preceding confirmations. It loads the pinned 447
App libraries for preparation, verifies the packaged reader and baseline Rust
identities, and executes only the separately pinned diagnostic Rust worker.
No OCR or App worker process is invoked. The supported invocation is:

```powershell
$probe = 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\hardware-final-build\GraphReader.CalibrationOcrProbe.exe'
$build = 'E:\Graph-auto-reader\artifacts\dev-portable\builds\0.4.47-20261114T093850726Z-76204ba4'
$trace = 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\native-engine\release\graphreader-engine.exe'
& $probe --axis-decision-self-test (Join-Path $build 'native\graphreader-engine.exe') $trace
& $probe --real-dev-axis-decision-check 'E:\Graph-auto-reader\data\manual data' (Join-Path $build 'GraphReader.App.exe') (Join-Path $build 'review-assistance\ocr') (Join-Path $build 'native\graphreader-engine.exe') $trace --explicit-opt-in
```

The 0.4.47 application and packaged worker identities remain those recorded in
the [0.4.47 confirmation](REAL-DEV-CALIBRATION-447-CONFIRMATION-20261114.md).
The diagnostic worker is separately pinned at
`94893abf759ba074d1f857acd7c968645003ac2aec6082b16825a32b00d50d79`; it is not
the candidate's packaged worker. Probe build metadata records 21 libraries
verified at 26 destinations and probe SHA-256
`f1e5383f9ea3f00b71ba3f2b2881e64b94e2aac3ffbac65f699b69a2101e37f9`. The
diagnostic build succeeded in `9.8899527 s`.

The fixed 120-item aggregate completed with 120 valid raw traces and 112
prepared-panel traces. All 120 payloads were hashed and images bound; there
were no OCR calls, sealed reads, case-level output, or image writes. Geometry
coverage matched the preparation diagnostic: 37 raw successes, 36 prepared
panel successes, and 38 effective-import source successes. This does not
demonstrate accuracy or calibration acceptance.

| Prepared trace result | Count | Unit |
| --- | ---: | --- |
| `NO_VALID_PAIR`: connected-run / grid routes | 46 / 19 | Images |
| Thick-band reasons | 7 | Images |
| Candidate pairs examined | 335 | Candidate pairs |
| Connection rejections | 301 | Candidate pairs |
| Span rejections | 15 | Candidate pairs |
| X / Y tick rejections | 18 / 18 | Candidate pairs, overlapping |
| Qualified pairs | 1 | Candidate pairs |

The trace counters describe examined resolution candidates, preserve the
existing early stops, and are not counts of failed images. Tick-rejection
counters can overlap. In particular, the 80 raw `AXIS_AMBIGUOUS` results and
the prepared route/reason counts do not establish that those sources contain
multiple plots or that any rejected pair corresponds to a true axis. No
threshold, selection, or error-handling rule changed based on these counts.

The run started at `2026-11-14T18:00:50.4503477Z` and ended at
`2026-11-14T18:04:18.6407613Z`. It exited 0 with empty stderr in
`208.1938866 s` process wall time; the
pipeline total was `201174.2316 ms`. Aggregate report SHA-256 is
`f4ff1e025050a9474076a64d75b14e855fb46f439fb072e9a989a502f062541c` (6,731
bytes). Peak sampled parent memory was 110,407,680 bytes and peak parent
working set was 173,273,088 bytes; these are samples, not a hard memory limit.
Parent preprocessing and child workers were independently capped at 0.4992
logical core each, not jointly. The result remains review-only and cannot
approve accuracy, calibration, or production.

## Retention and limitations

The diagnostic run does not change selection behavior when tracing is off;
owned parity checks show identical ordinary responses on the eight controls.
No model, dependency, threshold, or package changed. The under-2-MB raw
diagnostic outputs should be retired after this outcome is pushed and consumers
finish. No new model selection, training, private image copies, or sealed reads
are authorized by this record.
