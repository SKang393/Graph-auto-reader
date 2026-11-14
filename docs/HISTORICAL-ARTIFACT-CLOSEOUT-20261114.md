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

## Completed third retirement batch: 20261001 evaluations

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
outputs were proposed for retirement. They were removed with the fourth batch
after the pushed closeout record and the lead's pre-removal checks.

## Completed fourth retirement batch: server OCR comparison outputs

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
contains exact sibling counts and hashes. After closeout `5f263f04` reached
`origin/main`, the lead repeated consumer, process, containment, and reparse
checks. The guarded cleanup completed at `2026-11-14T20:12:22.4065316Z` with
zero old consumers: **20,738 files / 27,085,998,897 path bytes** removed across
these two batches. All target paths were absent afterward. The server OCR tree
now contains only the required recognizer manifest; its conversion model and
license/notice dependencies remain at their referenced paths.

The initial removal attempt stopped at its inventory-hash check before deleting
anything. The earlier captured digest differed from the final reviewed
inventory; the cause was not established. The lead re-reviewed all 17 sibling
paths and the same 7,800-file scope, then revalidated all four preserved
dependency identities before cleanup. Ten protected keeper hashes remained
unchanged, including the retained recognizer and its three external
dependencies. The retained 0.4.48 executable hash and original corpus were
preserved. Free E: space changed from 401,206,902,784 to 428,369,891,328 bytes;
this is an observed drive change, not exact physical attribution. Across all
four batches, **67,891,687,239 path bytes** of completed output were retired.

## Completed fifth retirement batch: 20261001 evaluation outputs

The complete inventory identifies five more completed evaluation trees under
`artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/`:

| Output directory | Files | Path bytes | Pushed outcome |
| --- | ---: | ---: | --- |
| `compact-header-orientation-evaluation-20261001-v1` | 4,300 | 5,416,039,580 | [`GOAL-22-COMPACT-HEADER-ORIENTATION.json`](GOAL-22-COMPACT-HEADER-ORIENTATION.json) |
| `marker-suppression-recovery-evaluation-20261001-v3` | 4,289 | 5,394,624,047 | [`GOAL-22-MARKER-SUPPRESSION-RECOVERY.json`](GOAL-22-MARKER-SUPPRESSION-RECOVERY.json) |
| `phase-role-vocabulary-evaluation-20261001-v1` | 4,243 | 5,393,853,062 | [`GOAL-22-PHASE-ROLE-VOCABULARY-REPAIR.json`](GOAL-22-PHASE-ROLE-VOCABULARY-REPAIR.json) |
| `overlapping-caption-bracket-evaluation-20261001-v2` | 4,242 | 5,393,283,930 | [`GOAL-22-OVERLAPPING-CAPTION-BRACKET-REPAIR.json`](GOAL-22-OVERLAPPING-CAPTION-BRACKET-REPAIR.json) |
| `overlapping-caption-bracket-evaluation-20261001-v1` | 4,238 | 5,392,920,117 | [`GOAL-22-OVERLAPPING-CAPTION-BRACKET-REPAIR.json`](GOAL-22-OVERLAPPING-CAPTION-BRACKET-REPAIR.json) |
| **Total** | **21,312** | **26,990,720,736** | |

The pushed outcomes record accuracy-incomplete or retention-failed results,
zero private/sealed reads, and production approval false. License reviews say
no dependency, model, or license changes; fixed payloads, manifests, and license
inputs were checksum verified. Exact directory references were found only in
tracked outcome/diagnosis documents and plan context, not current executable
source, tools, or packaging. No active model keeper was located in these trees;
the current V44/V27/reviewed-parent inputs remained outside them.

After record `6399fe7c` reached `origin/main`, the lead completed guarded
cleanup at `2026-11-14T20:23:07.6164987Z`. All five paths were absent afterward;
**21,312 files / 26,990,720,736 path bytes** were retired. Ten protected keeper
hashes were unchanged, the retained 0.4.48 hash matched, and the original
corpus remained present. Free E: space changed from 428,374,274,048 to
455,443,701,760 bytes; this is an observed drive change, not exact physical
attribution. The receipt is `artifacts/storage-retention-check/cleanup-fifth.json`.
Across all five batches, **94,882,407,975 path bytes** of completed output
were retired; remaining historical trees still require individual review.

## Completed sixth retirement batch: 20260930–20261001 evaluations

Using the existing complete inventory after excluding absent/retired targets
and the retained server recognizer, these are the next ten largest completed
trees that still exist under
`artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/`:

| Output directory | Files | Path bytes | Pushed outcome | Outcome status |
| --- | ---: | ---: | --- | --- |
| `session-run-pitch-evaluation-20261001-v1` | 4,236 | 5,392,240,325 | [`GOAL-22-SESSION-RUN-PITCH-REPAIR.json`](GOAL-22-SESSION-RUN-PITCH-REPAIR.json) | `runtime_repair_verified` |
| `separated-header-glyph-evaluation-20260930-v2` | 4,231 | 5,392,233,125 | [`GOAL-22-SEPARATED-HEADER-GLYPH-RECOVERY.json`](GOAL-22-SEPARATED-HEADER-GLYPH-RECOVERY.json) | `runtime_repair_verified` |
| `marker-glyph-disambiguation-evaluation-20260930-v2` | 4,224 | 5,380,324,885 | [`GOAL-22-MARKER-GLYPH-DISAMBIGUATION.json`](GOAL-22-MARKER-GLYPH-DISAMBIGUATION.json) | `runtime_repair_verified` |
| `bracket-caption-glyph-evaluation-20260930-v1` | 4,219 | 5,378,534,861 | [`GOAL-22-BRACKET-CAPTION-GLYPH-RECOVERY.json`](GOAL-22-BRACKET-CAPTION-GLYPH-RECOVERY.json) | `runtime_repair_verified` |
| `header-fragment-word-evaluation-20260930-v4` | 4,212 | 5,372,718,923 | [`GOAL-22-HEADER-FRAGMENT-WORD-RECOVERY.json`](GOAL-22-HEADER-FRAGMENT-WORD-RECOVERY.json) | `runtime_repair_verified` |
| `bound-header-word-evaluation-20260930-v1` | 4,214 | 5,372,617,906 | [`GOAL-22-BOUND-HEADER-WORD-RECOVERY.json`](GOAL-22-BOUND-HEADER-WORD-RECOVERY.json) | `runtime_repair_verified` |
| `header-glyph-evaluation-20260930-v3` | 4,206 | 5,368,418,775 | [`GOAL-22-HEADER-GLYPH-CROP-COMPLETION.json`](GOAL-22-HEADER-GLYPH-CROP-COMPLETION.json) | `runtime_repair_verified` |
| `symbol-run-mask-evaluation-20260930-v2` | 4,156 | 5,286,771,268 | [`GOAL-22-SYMBOL-RUN-MASK-REVIEW.json`](GOAL-22-SYMBOL-RUN-MASK-REVIEW.json) | `repair_verified_accuracy_incomplete` |
| `legend-pixel-separation-evaluation-20260930-v1` | 4,158 | 5,268,333,151 | [`GOAL-22-LEGEND-PIXEL-SEPARATION.json`](GOAL-22-LEGEND-PIXEL-SEPARATION.json) | `repair_verified_accuracy_incomplete` |
| `server-reader-v46-context-evaluation-20260930-v1` | 2,099 | 4,892,930,508 | [`GOAL-22-V46-SERVER-READER-CONTEXT-OUTCOME.json`](GOAL-22-V46-SERVER-READER-CONTEXT-OUTCOME.json) | `diagnostic_candidate_rejected` |
| **Total** | **39,955** | **53,105,123,727** | | |

All ten pushed outcomes report zero private/sealed reads. The nine repair records
do not approve production; their license reviews report unchanged Apache-2.0
project source, dependencies, or fixed model/license inputs. The V46 context
candidate was rejected. Exact directory strings were found in outcome or
diagnosis documents only, not current executable source, tools, or packaging;
no current model keeper is located in these trees. The active V44/V27/reviewed-
parent inputs remain outside this batch.

After scope `be8c922161bb41b7f498d78d9da50fa094a29249` reached `origin/main`,
the exact inventory, live CIM consumer, path containment and ancestor/descendant
reparse checks passed. Cleanup completed at `2026-11-14T20:59:49.2652223Z`:
all ten targets were absent, **39,955 files / 53,105,123,727 path bytes** retired,
zero old-worktree consumers, all ten keeper hashes unchanged. The retained
0.4.49 executable hash matched and original corpus remained present. Observed
E: free space changed from 455,258,443,776 to 508,511,121,408 bytes, not an exact
physical-space attribution. The receipt is
`artifacts/storage-retention-check/cleanup-sixth.json`.

Across all six historical batches, **147,987,531,702 path bytes** of completed
output have been retired. Other historical trees remain under review; this
does not establish a repository-wide disposable-output total below 1 GB.

## Candidate seventh batch: completed evaluation and replay outputs

The ignored inventory shortlist is
`artifacts/storage-retention-check/next-cleanup-candidates.json`. It contains
the next twenty large present run-output trees with matching pushed outcome
records, after excluding prior/planned batches, protected model keepers, and
exact-path references in current `src`, `tools`, `packaging`, and `ml` files.
The shortlist totals **41,023 files / 27,261,690,944 path bytes**. These are
review candidates only; no deletion is claimed or authorized by this list.

| Output directory | Files | Path bytes | Pushed outcome | Recorded status |
| --- | ---: | ---: | --- | --- |
| `server-rec-head-v44-trained-eval-20261029-v1` | 114 | 2,614,487,068 | [`GOAL-22-SERVER-RECOGNITION-HEAD-OUTCOME.json`](GOAL-22-SERVER-RECOGNITION-HEAD-OUTCOME.json) | completed fixed-budget record; production false |
| `server-rec-head-v44-baseline-eval-20261029-v1` | 114 | 2,614,448,919 | [`GOAL-22-SERVER-RECOGNITION-HEAD-OUTCOME.json`](GOAL-22-SERVER-RECOGNITION-HEAD-OUTCOME.json) | recorded baseline; production false |
| `server-rec-head-v44-legend-context-eval-20261029-v1` | 113 | 2,594,452,924 | [`GOAL-22-V44-SERVER-LAYOUT-CONTEXT-OUTCOME.json`](GOAL-22-V44-SERVER-LAYOUT-CONTEXT-OUTCOME.json) | panels completed; production false |
| `v46-probe-current-main-replay-20261030-v13` | 977 | 2,240,351,084 | [`GOAL-22-PROBE-REPLAY-OUTCOME.json`](GOAL-22-PROBE-REPLAY-OUTCOME.json) | synthetic dev diagnostic incomplete |
| `v46-probe-current-main-replay-20261030-v10` | 945 | 2,240,254,077 | [`GOAL-22-PROBE-REPLAY-OUTCOME.json`](GOAL-22-PROBE-REPLAY-OUTCOME.json) | synthetic dev diagnostic incomplete |
| `v46-probe-current-main-replay-20261030-v9-attribution` | 946 | 2,240,218,253 | [`GOAL-22-PROBE-REPLAY-OUTCOME.json`](GOAL-22-PROBE-REPLAY-OUTCOME.json) | synthetic dev attribution diagnostic |
| `v46-probe-current-main-replay-20261030-v8` | 945 | 2,240,215,998 | [`GOAL-22-PROBE-REPLAY-OUTCOME.json`](GOAL-22-PROBE-REPLAY-OUTCOME.json) | synthetic dev diagnostic incomplete |
| `participant-row-paired-20261029-v3` | 4,302 | 940,971,543 | [`GOAL-22-PARTICIPANT-ROW-ANCHOR-OUTCOME.md`](GOAL-22-PARTICIPANT-ROW-ANCHOR-OUTCOME.md) | completed experiment; no model approved or packaged |
| `ocr-multirow-legend-v1` | 1,596 | 926,962,890 | [`GOAL-22-MULTIROW-LEGEND.json`](GOAL-22-MULTIROW-LEGEND.json) | development improvement below acceptance |
| `ocr-tick-lane-v1` | 1,532 | 838,295,701 | [`GOAL-22-TICK-LANE-RECOVERY.json`](GOAL-22-TICK-LANE-RECOVERY.json) | development improvement below acceptance |
| `legend-symbol-overhang-evaluation-20260930-v1` | 4,151 | 810,633,026 | [`GOAL-22-LEGEND-SYMBOL-OVERLAP.json`](GOAL-22-LEGEND-SYMBOL-OVERLAP.json) | repair verified; accuracy incomplete |
| `qualified-heading-evaluation-v1` | 4,131 | 807,262,264 | [`GOAL-22-QUALIFIED-PHASE-HEADINGS.json`](GOAL-22-QUALIFIED-PHASE-HEADINGS.json) | repair verified; accuracy incomplete |
| `recovery-mask-evaluation-v1` | 4,123 | 806,487,597 | [`GOAL-22-RECOVERY-MASK-ELIGIBILITY.json`](GOAL-22-RECOVERY-MASK-ELIGIBILITY.json) | repair verified; accuracy incomplete |
| `legend-frame-evaluation-v2` | 4,049 | 803,263,292 | [`GOAL-22-LEGEND-FRAME-EXCLUSION.json`](GOAL-22-LEGEND-FRAME-EXCLUSION.json) | repair verified; accuracy incomplete |
| `stroke-gap-evaluation-20260930-v3` | 4,157 | 803,205,158 | [`GOAL-22-STROKE-GAP-EXCLUSION.json`](GOAL-22-STROKE-GAP-EXCLUSION.json) | runtime repair verified; production false |
| `verified-template-evaluation-v1` | 4,083 | 801,511,104 | [`GOAL-22-VERIFIED-TEMPLATE-SEEDS.json`](GOAL-22-VERIFIED-TEMPLATE-SEEDS.json) | synthetic workflow verified; production false |
| `ocr-legend-completion-v1` | 1,190 | 734,777,292 | [`GOAL-22-LEGEND-CROP-COMPLETION.json`](GOAL-22-LEGEND-CROP-COMPLETION.json) | development diagnostic; not acceptance |
| `ocr-header-glyph-v1` | 1,187 | 734,647,718 | [`GOAL-22-HEADER-GLYPH.json`](GOAL-22-HEADER-GLYPH.json) | development improvement; not acceptance |
| `ocr-legend-completion-v2` | 1,184 | 734,645,811 | [`GOAL-22-LEGEND-CROP-COMPLETION.json`](GOAL-22-LEGEND-CROP-COMPLETION.json) | development diagnostic; not acceptance |
| `ocr-legend-completion-v3` | 1,184 | 734,599,225 | [`GOAL-22-LEGEND-CROP-COMPLETION.json`](GOAL-22-LEGEND-CROP-COMPLETION.json) | development diagnostic; not acceptance |
| **Total** | **41,023** | **27,261,690,944** | | |

The linked records report no private/sealed corpus access for these runs and no
production approval. The participant-row record additionally notes an earlier
consumed sealed-child failure; that history is retained in the linked record.
License notes report unchanged project/model dependencies or no added external
payloads. These are completed evaluation/replay outputs, not current runtime
keepers: exact-path searches found no current references in `src`, `tools`,
`packaging`, or `ml`; the current named OCR/marker/pretrained model keepers and
their active cache inputs were excluded. Before retirement, the lead still
needs to confirm pushed scope, zero live processes, literal-path containment,
reparse checks, exact counts, and unchanged hashes for the ten established
keepers and retained package. No cleanup has been run for this candidate batch.
