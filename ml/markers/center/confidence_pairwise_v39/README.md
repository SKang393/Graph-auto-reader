<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Pairwise confidence training candidate

Status: the one authorized synthetic-train P1 run failed the fixed exploratory development precision bars. The canonical ledger is closed as `failed_dev_unconsumed`, with execution authorization disabled and no sealed split read. The candidate remains unapproved and is not release eligible.

The executed fixed objective is synthetic-label BCE plus the mean softplus
pairwise ranking loss over positive/negative pairs in the same authenticated
V37 `scope:transform` group in each batch, with a one-logit margin. BCE retains
absolute score calibration; the pairwise term directly rewards ordering. A
group without both classes contributes no pairwise term. All-negative
`crowded_mined_negative` groups remain BCE-only. These are cache groups, not
source- or scene-level identities.
The training cohort is the V37 1,106,472-row reflected synthetic
feature set, with group IDs derived from the authenticated report's group
row counts/order. The initializer was the exact V27 checkpoint used
by V37. Keep V37's fixed eight epochs, seed `20261002`, batch size 128, AdamW
learning rate `0.0003`, weight decay `0.0001`, gradient-norm limit `5`,
49,281 trainable confidence parameters, architecture, geometry, row order, and
final-epoch selection unchanged. That is 69,160 fixed optimizer steps. This
makes V37's BCE recipe the same-data, same-initializer comparison. Real
positive-only labels are excluded because there are no justified real negative
labels. The development operating threshold remains the existing 0.1; no
threshold search is part of this candidate.

## Baseline bindings checked

- Canonical ledger pre-registration baseline: `ml/markers/training-budgets/production-repair-v1.json`,
  SHA-256 `8264d43925a83661c06e0714ba25fd5d016ad1b3eb556cd0bb3f518a8d612ba0`.
- Shared evidence policy: `ml/policy/evidence-policy.json`, SHA-256
  `4dc18136c284b0b1805d3a3b22a9197ad06e6a41f4e43b4e1d4d9245b97e0aed`.
- V37 objective/config: `ml/markers/center/confidence_reflection_v37/p1.json`,
  SHA-256 `80a76817ec8525dd54ec7beff2b959eb9a33667f50c4584f3f9e0d0b24709626`.
- V37 protocol: `ml/markers/center/confidence_reflection_v37/protocol.json`,
  SHA-256 `e145bc65f5ac03dad5dd3ce34d1e6695d3ab1b49f76737fea6f53a2115f9dbb8`.
- V27 initializer used by V37: `artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/marker-v27-component-diversity/P1-recovery1/marker-center-component-diversity-v27-p1.pt`,
  SHA-256 `774ef9455e9ce7b489e640c5237b81275f24e42c2d2d0d3d87be4e953b3eb365`.
- V37 trained outcome: `artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/center-confidence-reflection-v37/P1/marker-center.pt`,
  SHA-256 `2daeedb92fdfb6c425cca6035040ca0e857c6d6bb1ba548281729fee71c24cd4`.
- V37 train-feature report: `artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/reflected-spatial-features-20261002-v1/report.json`,
  SHA-256 `284ebfa305b7b827c59383711122b1668398460ddde2489007252335ee55f500`.

The verified V27 initializer and V37 outcome both exist under the project
worktree and match their recorded hashes. The current report declares
1,106,472 reflected train rows, 20 train-only
groups, zero development tensor reads and zero sealed reads. All 55 unique
files referenced by those train groups and the parent feature report are
present under the resume worktree. The parent report hash is
`3a98504c7b31f16af8f4b127ab438cba3fe7918d51c9417021221f96bb6ad7cc`.
The runner revalidates the report, model, evidence, all reflected group
payloads, and parent-cache payloads before loading. Before canonical admission,
it requires at least 9 GiB of available physical memory and validates the pinned
RX 6600 XT runtime so resource refusal does not consume the single-use candidate
seal. Both checks run before admission; the device is rechecked immediately
after admission, before loading data or creating an optimizer. The V37 concatenating loader
has an estimated transient feature-array peak around 6.4 GB, before runtime
overhead; the 9 GiB guard leaves about 2.6 GiB beyond that estimate when it admits a run. The feature report and
initializer resolve relative to the pinned `artifacts/goal22-worktrees/
resume-20260908` data root, not the active checkout's `artifacts/goal22-runs`.

## Training implementation and invocation

The runner loads only the fixed V37 training feature report after canonical
admission. It derives `scope:transform` group IDs in report order; pairwise
pairs cannot cross a report group. The complete frozen base and every
confidence-head optimizer parameter, gradient, and optimizer-state tensor
must remain on HIP `cuda:0` reported as AMD Radeon RX 6600 XT / `gfx1032`.
AdamW matches V37's pinned update defaults, with `capturable=True` as the
optimizer-state placement change: PyTorch's default `capturable=False`
initializes its step counter on CPU. The runner checks the capturable flag and
parameter, gradient, and optimizer-state devices before and after each step.
The exact project Python, PyTorch and NumPy versions are checked, and there is
no CPU fallback. The completed run wrote
`marker-center-pairwise.pt` and `training-report.json` under the preregistered
output path. Training did not read development or sealed payloads, export ONNX, or
consume a sealed split.

## Completed training and fixed development evaluation

The single authorized candidate run completed 69,160 AdamW steps over eight
fixed epochs on the AMD Radeon RX 6600 XT (`cuda:0`, HIP `gfx1032`). The training
report records 599.63 seconds, zero development/private/sealed reads, and no
production approval. Training priority was lowered to BelowNormal mid-run; its exact transition time was not recorded. Evaluation was launched at BelowNormal. The checkpoint SHA-256 is
`61f90a4452f3f9814386f754c30bc431074a937879c26fec71344a1e381159bd`; the
training report SHA-256 is
`99b6393b11d2665aceb1f6a2b892883ddb826da52f7b38cf281b6cad8e341aea`.

The first eval-only attempt stopped before inference because the unchanged
evaluator's preflight summary omitted `data_root` and `hashes`, which its run
path later reads. The recovery wrapper calls the original full protocol
validator and merges its returned bindings with the unchanged summary; it does
not change the evaluator or candidate threshold. Five recovery tests passed,
including mocked verification that both missing fields are restored, all
runtime keys reach the evaluator, and the fresh output path is used.

The successful evaluation used `CPUExecutionProvider` at the fixed 0.1
threshold and 5 px matching tolerance. It wrote to a fresh recovery path because
the original preregistered output path had failed before inference:
`artifacts/goal22-runs/marker-center-confidence-pairwise-v39-P1-dev-eval-recovery-v2/output/report.json`.
The report SHA-256 is
`0f6050444a5c8bf86eae72fa8df33a7824b560e4a1b1762193a1e95b441721d9`; the
recovery receipt SHA-256 is
`888c552a93661e9f4bf3ea81c41dd77e45ae24e7ab1172c60953f2a7807f49a1`.
The recovery binding SHA-256 is
`23217e10d383bca7478a0f5bafd462942d7cf9aa0b2d23a6a20763bb39baed01`.

## Fixed 0.1 comparison with V37

The hash-pinned V37 P1 report and V39 P1 report both report the following scene
and truth denominators at 0.1:

| Scope | Scenes | Truths | V37 precision / recall | V39 precision / recall | V39 minus V37 TP / FP / FN |
| --- | ---: | ---: | ---: | ---: | ---: |
| Component | 167 | 2,004 | 92.13% / 96.96% | 91.57% / 95.96% | -20 / +11 / +20 |
| Family | 9 | 206 | 77.69% / 98.06% | 76.89% / 98.54% | +1 / +3 / -1 |

V39 component precision and family precision are both below the shared 95%
bar. Family prohibited-structure hits changed from 4 to 3. The machine-readable
comparison, including exact counts and metric deltas, is
`artifacts/goal22-runs/marker-center-confidence-pairwise-v39-P1-dev-eval-recovery-v2/v37-comparison.json`.

This is descriptive evidence, not an independent acceptance result. The V37
report is hash-pinned but does not expose a standalone dev-file hash in its
binding. V39 records per-scene train/dev overlap as unverified because training
features lack scene identifiers. Both reports retain their own status and
prohibited-hit accounting; no threshold search or production change was made.
The V39 report status is `exploratory_dev_scored_no_independent_acceptance`,
with production approval and release eligibility false.

## Admission ledger reconstruction

The frozen admission used commit `0979217fbd8f5338c76819309161dd1dbda02f66` and the pre-registration ledger SHA-256 `8264d43925a83661c06e0714ba25fd5d016ad1b3eb556cd0bb3f518a8d612ba0`. Apply `admission-ledger.patch` (SHA-256 `970965a568781a1cd4d514ff17b53c82d54fdd0c3484ded1ae648d5801dff2b9`) to that commit's ledger to reconstruct the admitted ledger SHA-256 `0885f7b401ffbeea7d6f6c44be0ba24559e16a3cdeee58869ea92a26849ea6b0`. This was verified with a separate temporary Git index before finalizing the candidate. The current ledger subsequently records the failed result; its bytes intentionally differ from the admission snapshot.
