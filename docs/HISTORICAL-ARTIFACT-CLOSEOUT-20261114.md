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

## Planned second retirement batch: confidence V32–V37 evaluations

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

This is a planned retirement batch, pending the lead's pre-removal process,
containment, and reparse checks. No deletion is claimed here.
