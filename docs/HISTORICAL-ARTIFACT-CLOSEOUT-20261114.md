<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Historical generated-output closeout

The current retention policy requires completed outcomes in Git and removal of
disposable output after active consumers finish. A bounded metadata inventory
found at least 72,063,417,396 path bytes under the older `resume-20260908`
worktree's artifacts directory before reaching its time limit. This is a
partial inventory, not a measure of removable data or physical disk use.

The following completed generated-output trees have tracked outcome records
already present on `origin/main`. The paths below are relative to
`artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/` in the main
checkout. They are selected closeout targets, not a global size ranking.

| Output directory | Files | Path bytes | Pushed outcome |
| --- | ---: | ---: | --- |
| `marker-core-intersection-evaluation-20261001-v2` | 4,245 | 5,396,879,794 | [repair record](GOAL-22-MARKER-CORE-INTERSECTION-REPAIR.json) |
| `current-main-7ec80465-synthetic-20261030` | 987 | 2,240,664,314 | [fixed synthetic run](GOAL-22-CURRENT-SYNTHETIC-RUN-20261030.md) |
| `marker-balanced-support-checks-v1` | 665 | 293,276,946 | [support checks](GOAL-22-MARKER-BALANCED-SUPPORT.json) |
| **Total** | **5,897** | **7,930,821,054** | |

The intersection evaluation recorded failed native retention despite its
review guard. The synthetic run completed only one of 21 workflows and its
offline scoring limitations remain recorded. The support checks passed their
geometry repair checks without establishing full model acceptance. None of
these outcomes is promoted by cleanup. The records identify owned synthetic
or diagnostic inputs and zero private/sealed reads; the removed trees contain
generated results, inference caches, staged source/build copies and frozen
copies for those completed runs. Original private corpus, current model
stores, training environments and the retained 0.4.48 package are outside
these targets.

Current source/package searches found no exact path consumer. An independent
review checked the outcome records and found no descendant or ancestor reparse
points. The lead's process inventory at `2026-11-14T19:12:41.6524797Z` found no
process referring to the old worktree or these outputs. Repeat containment,
reparse and active-consumer checks immediately before removal, after this
closeout record is pushed. Historical source, configuration, input/output
digests and aggregate results stay in Git. Replays require regeneration from
the recorded source and input bindings; local frozen copies will not remain.
Exact byte reproduction of every historical intermediate is not promised.

Preserve the dirty axis-ranker source worktree and its cache during this pass.
Preserve the small v29 failed-model output because the tracked
`cascade_component_diagnostic.py` still names it; retirement of that diagnostic
is a separate source change. The inventory does not establish that all other
historical output is closed or that total disposable storage is below 1 GB.

After closeout record `267561f3d56ee4449fd41bc67b493ba582f27410` reached
`origin/main`, the repeated process, containment, reparse and exact size/count
checks passed. Cleanup completed at `2026-11-14T19:21:04.5641011Z` and retired
all three directories: **5,897 files, 7,930,821,054 path bytes**. All three
paths were absent afterward. Free E: space changed from 360,287,174,656 to
368,239,001,600 bytes during the operation; concurrent activity means that
difference is not an exact attribution of physical bytes reclaimed.
The original corpus and retained 0.4.48 executable remained present. No source
worktree, current model store, environment or user Data directory was removed.
The compact inventory and cleanup receipt are disposable once this
confirmation is pushed.

## Completed second retirement batch: confidence V32–V37 evaluations

The bounded inventory also identified these six completed evaluation trees
under `artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/`:

| Output directory | Files | Path bytes | Pushed outcome |
| --- | ---: | ---: | --- |
| `frozen-confidence-v32-evaluation-20261002-v2` | 4,674 | 5,578,651,365 | `ml/markers/center/confidence_head_v32/P1_RESULT.json` |
| `confidence-bce-v33-evaluation-20261002-v1` | 4,303 | 5,464,829,280 | `ml/markers/center/confidence_bce_v33/P1_RESULT.json` |
| `confidence-residual-v34-evaluation-20261002-v1` | 4,302 | 5,464,180,490 | `ml/markers/center/confidence_residual_v34/P1_RESULT.json` |
| `confidence-spatial-v35-evaluation-20261002-v1` | 4,198 | 5,455,811,889 | `ml/markers/center/confidence_spatial_v35/P1_RESULT.json` |
| `confidence-duration-v36-evaluation-20261002-v1` | 4,219 | 5,455,270,241 | `ml/markers/center/confidence_duration_v36/P1_RESULT.json` |
| `confidence-reflection-v37-evaluation-20261002-v1` | 4,225 | 5,456,124,023 | `ml/markers/center/confidence_reflection_v37/P1_RESULT.json` |
| **Total** | **25,921** | **32,874,867,288** | |

Each pushed result records `failed_dev_unconsumed`, no production approval, and
zero private/sealed reads. V37 also has
[`GOAL-22-CONFIDENCE-REFLECTION-EXPORT-DIAGNOSIS.json`](GOAL-22-CONFIDENCE-REFLECTION-EXPORT-DIAGNOSIS.json).
No exact current source or package consumer was found. These trees contain
local replay output; the aggregate outcomes and hashes remain in Git. Current
V44/V27 and reviewed-parent readiness model keepers are outside these targets.

After retirement record `06d9dc8b` reached `origin/main`, the lead repeated
literal-path containment, ancestor/descendant reparse, exact file-count/byte
and active-consumer checks. No old-worktree consumer was running. Removal
completed at `2026-11-14T19:55:23.6631475Z`: **25,921 files and
32,874,867,288 path bytes** were retired, and all six directories were absent
afterward. Free E: space changed from 368,238,456,832 to 401,208,856,576 bytes;
this is an observed drive change, not exact physical attribution.

Before/after SHA-256 checks matched for the V44 ONNX and detector manifest,
V27 ONNX, enclosed-support marker manifest, complete-workflow marker notice,
and reviewed parent ONNX. The retained 0.4.48 executable matched its latest
pointer, and the original private corpus remained present. The receipt in
`artifacts/storage-retention-check/cleanup-confidence.json` is disposable
after this confirmation is pushed. Across these two batches, **40,805,688,342
path bytes** of completed output were retired. Other historical trees still
require individual consumer/outcome review; this is not a claim that total
disposable storage is below 1 GB.

## Planned third retirement batch: 20261001 evaluations

The completed inventory identifies three more generated evaluation trees
under `artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/`:

| Output directory | Files | Path bytes | Pushed outcome |
| --- | ---: | ---: | --- |
| `phase-word-spacing-evaluation-20261001-v1` | 4,315 | 5,454,203,758 | [`GOAL-22-PHASE-WORD-SPACING.json`](GOAL-22-PHASE-WORD-SPACING.json) |
| `left-axis-role-evaluation-20261001-v1` | 4,315 | 5,453,646,950 | [`GOAL-22-LEFT-AXIS-ROLE.json`](GOAL-22-LEFT-AXIS-ROLE.json) |
| `two-heading-completion-evaluation-20261001-v1` | 4,308 | 5,440,406,407 | [`GOAL-22-TWO-HEADING-COMPLETION.json`](GOAL-22-TWO-HEADING-COMPLETION.json) |
| **Total** | **12,938** | **16,348,257,115** | |

The outcomes report repair checks as verified but accuracy incomplete, with
zero private/sealed reads and production approval false. Exact current source,
tool, and package consumers were not found. The left-axis path appears in
closed V32–V37 candidate records, whose evaluation outputs were retired in the
previous batch. Aggregate outcomes and hashes remain in Git; only local replay
outputs are proposed for retirement. This batch remains pending the lead's
pre-removal process, containment, and reparse checks; no deletion is claimed.

## Planned fourth retirement batch: server OCR comparison outputs

The completed inventory measured
`artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/server-ocr-comparison-v4`
at 7,801 files / 10,738,195,179 path bytes. Preserve its one small current
CenterBinding input, `models/recognizer.json` (453,397 bytes; file SHA-256
`0da38be8c09e12cd8db243b327805a04950eb9502b48ada4f896ac98aaad3e95`). It
describes `PP-OCRv5_server_rec` revision
`b26c3587fda8da3c8ec0ce357214b4d661ff1558`, Apache-2.0, but its accuracy is
pending comparison and it is not production approved.

The two current CenterBinding tools also bind the conversion model and its
notices outside this directory. Preserve these exact dependencies:

| Dependency | Bytes | SHA-256 |
| --- | ---: | --- |
| `server-ocr-conversion-v2/PP-OCRv5_server_rec/conversion-1.onnx` | 84,483,046 | `0980049401eceb506952f769765a6b95be07cb1197dcbb13c93f107a328f8418` |
| `server-ocr-intake-v1/PP-OCRv5_server_rec/Apache-2.0.txt` | 11,357 | `c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4` |
| `server-ocr-intake-v1/PP-OCRv5_server_rec/MODEL_NOTICE.md` | 777 | `ced966a17ac31dca6d58303f27a6e55f65896ffd1f16cf941956f6f4c17c9785` |

The remaining siblings are the partial-tree retirement targets:

| Sibling | Files | Path bytes |
| --- | ---: | ---: |
| `baseline/` | 1,975 | 400,383,742 |
| `server-both/` | 1,949 | 5,037,974,619 |
| `server-detector/` | 1,874 | 561,997,630 |
| `server-reader/` | 1,989 | 4,737,351,089 |
| `models/detector.json` | 1 | 2,123 |
| Root notifications, definitions, logs, and status files | 12 | 32,579 |
| **Disposable total** | **7,800** | **10,737,741,782** |

The outcome [`GOAL-22-SERVER-OCR-COMPARISON.json`](GOAL-22-SERVER-OCR-COMPARISON.json)
records the completed comparison with zero private/sealed reads, zero optimizer
steps, and production approval false. The only baseline references found are
in `off_center_context_v9/protocol.json`. Its P1 record closed
`failed_dev_unconsumed` on 2026-09-30; no current review-queue or goal-sequence
assignment was found, so this protocol is a historical record, not an active
consumer. The two CenterBinding tools reference only the recognizer manifest
here; the notice and conversion dependencies above remain preserved elsewhere.
The ignored inventory `artifacts/storage-retention-check/server-disposable.json`
(SHA-256 `d43f6304ebc0132158ae0b3b73975b154d8e01848283e897b4e9de090a07d85f`)
contains exact counts and hashes. This partial-tree retirement is pending the
lead's pre-removal consumer, process, containment, and reparse checks. No
deletion is claimed.

The first removal attempt stopped at its inventory-hash check before deleting
anything. The earlier captured digest differed from the completed inventory;
the cause was not established. The lead re-reviewed all 17 exact sibling paths,
counts and bytes, reconciled the same 7,800-file scope, and checked all four
preserved dependency identities. The digest above binds that reviewed inventory.
