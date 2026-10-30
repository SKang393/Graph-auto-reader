# Goal 22 Probe replay outcome

Status: synthetic DEV diagnostic complete, workflow acceptance incomplete. This
record describes a replay of the recorded V46 Probe header-merge incident on
current main source commit `d174c3d5fd367b0dc090637df3974c4c06a6993b`. It is not a
passing full baseline, product approval, or release decision.

## Invocation and identities

The runner processed the complete frozen 21-image synthetic validation set from
`artifacts/goal22-tools/v46-probe-current-main-replay-prep-20261030-v8`. It used
CPU execution with graph optimization disabled, intra-operation threads 10,
inter-operation threads 1, queue capacity 1, and one worker. The thread count
changed from 12 to 10 after the V6 launch stopped before inference because the
current .NET runtime reported a maximum of 10. Models, algorithm identities,
thresholds, source set, and shared acceptance bars did not change. The protocol
notes that `Run-TrainingCpuBudget.ps1`, if used, applies an 80% aggregate CPU
job-rate and generic OMP/MKL/OpenBLAS limits; it does not mutate serialized ONNX
`intra_operation_threads`. The report does not prove that launcher was used or
bind other process environment variables.

```powershell
# Working directory: E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20260908
dotnet "E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20260908\artifacts\goal22-tools\v46-probe-current-main-replay-prep-20261030-v4\build\GraphReader.RealAcceptance.Ocr.dll" --run-frozen-candidate-synthetic --candidate-binding artifacts/goal22-tools/v46-probe-current-main-replay-prep-20261030-v8/candidate-binding.json --candidate-binding-sha256 d8f233f3c536dec5357800d1469c11efd1c160ddff55fed7d6074d0791c1d5ca --input-manifest artifacts/goal22-tools/v46-probe-current-main-replay-prep-20261030-v8/input-manifest.json --input-manifest-sha256 b313055ad43129d7988fff3487d739de0dea088a6b4dae1092e652bb90c2d637 --output artifacts/goal22-runs/v46-probe-current-main-replay-20261030-v8
```

Source commit: `d174c3d5fd367b0dc090637df3974c4c06a6993b`. Git tree OID:
`0ee505ddf0af405778b55e01ddbebed7f9104e91`. Tracked runtime source inventory
SHA-256: `7e93653ea10bc4ba9e3d92dc162215a0d9b40a691e1b3f777b2a8575a2cf60b2`.
The reused runner DLL SHA-256 is
`585ddf4e469937fd45b9246b323b86bded3164953bc3192b9b31b0c74f8014fd`.

| Evidence | SHA-256 |
| --- | --- |
| V8 report | `2b48ae87a82759dc0e2a21f310ef653a2fd74ccc4acad42ea1319ae33a31a439` |
| Candidate binding | `d8f233f3c536dec5357800d1469c11efd1c160ddff55fed7d6074d0791c1d5ca` |
| Input manifest | `b313055ad43129d7988fff3487d739de0dea088a6b4dae1092e652bb90c2d637` |
| Protocol | `8acd8ba0f21c67b2b73cb02264d956383a2f820256edaa73e214a2a45d0f431c` |
| Probe calibration stage evidence | `e0e1518d4db169b767c8cf633368175339b9f71710c770c9689ab487ca7f3792` |
| Probe raw OCR evidence | `3f515bc650ec0bcc1ce3640190a4c5a0996e29aef89c21327c374707a066056a` |

## Result

The report accounts for 21 sources: **1 completed and 20 failed** in
237,976.1708 ms. All 20 failures were `ProductionWorkflowStageException` at
automatic calibration. Each lacked an absolute session-1 origin and final
printed session, leaving X calibration ordinal-only. Fifteen had the same
missing-origin message, two also flagged an ambiguous YTick alternative, and
three reported first observed columns at sessions 29, 8, and 2.

The one completed non-Probe case emitted a prototype CSV and audit files with
two rows, despite the protocol's planned `csv_runs: 0` budget. Its CSV SHA-256
is `cece26144bb817e99fbb4a85632f06ca0cf1187c12abd21c2c749243538e89b3`; its
audit CSV SHA-256 is
`6d61b005cb1ae321a2c7c5307708f02ded1acda6782bc0e1eb54ee7c8de0379a`. The Probe
case emitted no CSV, and the 21-source run produced no complete end-to-end CSV
acceptance result. The prototype report states `production_approved: false`.
Shared acceptance bars were not modified; the bound file SHA-256 is
`aab9f2ab60cf166828f0928b8496f537341870fd457d0408952e22549fc53a56`.

The report records `private_corpus_access: false`, `sealed_corpus_access:
false`, `truth_consumed_by_inference: false`, and `model_selection_performed:
false`. No training or optimizer steps were performed.

## Probe header evidence

The Probe image SHA-256 is
`dfa5093f9a18e1e2a22dd77d61e8ff4be8f8f48dbc427c96c1b8ddfe9dd60f53`; panel
ID is `70743889-afdc-9e54-b170-77aff3bc2552`. OCR succeeded before the workflow
failed at calibration. The raw detector recorded 23 regions; the OCR stage
recorded 25.

The OCR stage preserved four separate regions instead of recreating the prior
composite: `sgbe` (`Other`, bounds `[917,67,950,79]`), `Interventions con`
(`Other`, `[771,72,888,85]`), `nt` (`Other`, `[884,73,899,84]`), and `n`
(`Other`, `[903,77,912,84]`). No `header-fragment-word` region or
`ocr_header_fragment_replaced` warning appeared. The separate `Interventions`
region at `[575,71,666,84]` was classified `PhaseHeading`.

This is stage-level evidence that the V46 four-fragment merge did not recur.
It does **not** show correct Probe recognition: the expected Probe text remains
misread as `sgbe`, and the Probe case failed before CSV output. The historical
repair record documents the old composite as `Interventions continleabe` and
the loss of the `Probe` match; its SHA-256 is
`f33d8a2cca802ee06a9e528e1421dce18699b5d6c664eeb8ec0f5fa7e7debb1c`.

## Failed preparation attempts

- The first preflight assumed the historical V46 report was 21/21 complete. It
  was actually 1/20; the denominator was corrected, and that historical report
  is not treated as a passing baseline.
- An early prep stopped because it resolved `source-bindings.json` before
  writing it. That output was preserved; later preparation used a fresh path.
- An isolated build attempt failed with `NETSDK1005`: `$(MSBuildProjectName)`
  remained literal in the custom intermediate-output path, so projects shared
  an invalid assets file. The v4 build used the isolated worktree's default
  `obj` and succeeded with 0 warnings and 0 errors.
- The V5 replay stopped before inference because native binding entries lacked
  the strict `role` field. V6 restored the archived roles.
- The V6 replay stopped before inference because its 12-thread override
  exceeded the reported runtime cap of 10. V8 changed the serialized runtime
  thread count to 10.
- A later descriptor-preparation attempt stopped on a receipt-key mismatch
  before inference; its partial output was preserved and v8 was prepared at a
  fresh path.

## Retention

The V8 report, raw/stage dumps, preparatory descriptors, and reused v4 build
are temporary diagnostic outputs, not runtime dependencies. After this outcome
record is committed and pushed and any active review consumer finishes, retire
the completed replay/preparation outputs under the retention policy. The
documentation records the result and hashes; it does not promise the generated
directories remain available. Preserve shared runtime models, licenses, source
inputs, and unrelated active work.
