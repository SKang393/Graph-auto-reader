# Goal 22 tick-seed replay outcome

Status: synthetic validation replay complete, no production approval. The
current-main replay after the tick-seed recovery change completed 1 of 21 sources,
the same aggregate count as the earlier replay. The saved reports contain no
seed identifiers, so they do not establish whether a specific tick seed changed
or why the other sources failed.

## Invocation and provenance

The fixed 21-image synthetic validation set and ordering are identical to V8.
V8 used source commit `d174c3d5fd367b0dc090637df3974c4c06a6993b`; V10 used
`4211afa1a53346684bedd5feaa04414d89fe4ca8` (tree
`4e6da4dfbda09c4bb0ac442b85c1a5f9243bea7a`). Both ran on CPU with graph
optimization disabled, intra-operation threads 10, inter-operation threads 1,
one worker, and no truth consumed by inference. The synthetic validation set
was reused. Private and sealed access, training, model selection, and production
approval are all recorded false.

```powershell
# Working directory: E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20260908
dotnet "E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20260908\artifacts\goal22-tools\v46-probe-current-main-replay-prep-20261030-v9\build\GraphReader.RealAcceptance.Ocr.dll" --run-frozen-candidate-synthetic --candidate-binding artifacts/goal22-tools/v46-probe-current-main-replay-prep-20261030-v10/candidate-binding.json --candidate-binding-sha256 e1ec3549f261ff906329be123524b8d7cf40f8e82dc763e2c7d3328865ab06ca --input-manifest artifacts/goal22-tools/v46-probe-current-main-replay-prep-20261030-v10/input-manifest.json --input-manifest-sha256 bc663209616040543e26726cabed2c3cbbdbbb64fb56ebe20f37c52d2c6af51e --output artifacts/goal22-runs/v46-probe-current-main-replay-20261030-v10
```

V10 reused the Release runner built from the clean `4211afa1` worktree. Build
completed with 0 warnings and 0 errors. No separate automated test suite was run
for this documentation batch. The V10 report's revision label contains `v9`
(`v46-current-main-probe-replay-20261030-v9-tick-seed-4211afa1`); its bound
candidate, input, protocol hashes and source commit identify the V10 invocation.

| Replay | Report SHA-256 | Completed / failed | Elapsed |
| --- | --- | ---: | ---: |
| V8 before tick-seed recovery | `2b48ae87a82759dc0e2a21f310ef653a2fd74ccc4acad42ea1319ae33a31a439` | 1 / 20 of 21 | 237,976.1708 ms |
| V10 after tick-seed recovery | `43603f4d153ae38b627fae534436844a795005c5e372210c288e2e0af5f22de2` | 1 / 20 of 21 | 285,389.5312 ms |

The equal aggregate completion count shows no measurable improvement in this
summary. It does not prove that tick-seed recovery had no effect: the reports do
not carry seed identifiers needed to attribute outcomes to particular seeds,
and the cause of the 20 failures is not established here. No production or
release acceptance is claimed.

## Axis tick evidence coverage

A direct count over the 31 stage-evidence files listed by the V10 report found
XAxis tick counts of **0 in 17 panels, 1 in 10, 2 in 2, 3 in 1, and 4 in 1**.
The audit rechecked each listed stage-evidence SHA-256 and counted entries in
`observation.axis.ticks` whose axis is `XAxis`. Thus 27 of 31 panels have fewer
than two detected XAxis tick strokes; a method requiring at least two has that
minimum evidence in only four panels. This is a coverage constraint, not an
explanation for the other panel failures or a causal claim about the tick-seed
change.

## Binding correction and budget

V9 failed before inference because its `native_files` entries for
`GraphReader.App.exe` and `GraphReader.RealAcceptance.Ocr.exe` omitted the
required `role` property. V10 restored the exact `{role,file,sha256}` shape and
role values used by accepted V8 bindings. The V10 preflight verified all 51
unique candidate file references and the source, model, input, and acceptance
hashes.

V8's protocol budget said `csv_runs: 0`, although its successful source exported
CSV artifacts. V10 corrected the maximum to 21 source export sets, one per
possible successful source, each set comprising a minimal and audit CSV. This is
a maximum for the fixed invocation, not a claim that all 21 exported.

## Evidence hashes

| Evidence | SHA-256 |
| --- | --- |
| V8 candidate binding | `d8f233f3c536dec5357800d1469c11efd1c160ddff55fed7d6074d0791c1d5ca` |
| V8 input manifest | `b313055ad43129d7988fff3487d739de0dea088a6b4dae1092e652bb90c2d637` |
| V8 protocol | `8acd8ba0f21c67b2b73cb02264d956383a2f820256edaa73e214a2a45d0f431c` |
| V10 candidate binding | `e1ec3549f261ff906329be123524b8d7cf40f8e82dc763e2c7d3328865ab06ca` |
| V10 input manifest | `bc663209616040543e26726cabed2c3cbbdbbb64fb56ebe20f37c52d2c6af51e` |
| V10 protocol | `d0e0b17d46f60a52f57c23f2b6447cdc4e60cf0161e90035c0a42dd98e862a91` |
| V10 source bindings | `1dd980f42cdc5e03106b56e9688b23614a744758cbc088b65bedf7c04ce22a47` |
| V10 preparation receipt | `130fb75104fc0d606d853fc40c6da7835fdab78f0d741606243256454d65a426` |
| Reused runner DLL | `aa8324f4e69e7c670ac31b0bdf7c51c8be7fc8a01ee722e094a618714dc130d8` |
| Runner command record | `f7443bf82e1dae2a3b3e7a35c0792d24c7940f23eeb6621f9bb01b17025c9e9b` |
| Tracked source inventory | `3867ecf99a94111564c1bac9eacf09ef9956eec32c3e105c0ce70b4765ed4452` |
| V46 detector payload / manifest | `497a3527820ce0359496084e5afb1e0d3241aca79e192187f7ab737b27818b7f` / `a690a324d43873b6b7833a6e8466c673477994d05be41f0ea8cc5e9dcdb3e75d` |
| PP-OCRv5 server recognizer payload | `0980049401eceb506952f769765a6b95be07cb1197dcbb13c93f107a328f8418` |
| Marker center V27 / classifier V5 payloads | `4979777a404be48c6c75b490218d1de31d2edb2550f5bc71c7aa6894c1bcca04` / `7f15c0ed0d8740bf4cc1d220764e670d999ca4f4a1dbfe5c631b01db5faaebba` |
| Shared acceptance bars | `aab9f2ab60cf166828f0928b8496f537341870fd457d0408952e22549fc53a56` |

The V10 report records `private_corpus_access: false`,
`sealed_corpus_access: false`, `truth_consumed_by_inference: false`,
`model_selection_performed: false`, and `production_approved: false`.

## Retention

The V8/V10 reports and V10 preparation/build outputs are temporary diagnostic
artifacts, not runtime dependencies. After this record is committed and pushed
and any active review consumer finishes, retire the completed run and prep
outputs under `docs/ARTIFACT-RETENTION.md`. This record preserves the measured
result and hashes; it does not promise those generated paths remain available.
