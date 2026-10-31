# Axis candidate linear GPU experiment

On 2026-10-31, candidate P1 completed one exploratory, train-only run of the
linear 17-feature axis-candidate ranker. The canonical result records
`training_complete_unapproved`. This is not production approval or evidence
that automatic recognition passes. A source-disjoint synthetic development
comparison was subsequently completed with the limitations described below.

## Training and scope

- The frozen synthetic corpus contained 64 source images and 96 plots. Production
  panelization returned 93 detected panels eligible for training and three
  full-image fallbacks, whose plots were excluded. The joined input has 93
  groups and 27,776 candidates; its data receipt is SHA-256
  `2f3eadd30b19fd8d86930571f230e01fe8fb8832bce999e4c98759ca21859622`.
- The recipe uses a 17-to-1 linear scorer, fixed feature scaling, listwise
  soft-target cross-entropy with temperature 0.1, AdamW at learning rate 0.001
  and weight decay 0.0001, seed 20261031, and four epochs. The report records
  372 steps and mean training loss 4.948786732971027. The attempt directory
  contains 372 step intents and 372 step completions. Training loss is not an
  accuracy or acceptance metric.
- The report records AMD Radeon RX 6600 XT, `gfx1032`, `cuda:0`, and HIP
  `7.16.26385`; the trainer contains device and residency guards. The
  project-local environment lock lists PyTorch `2.13.0+rocm10.1.0` and ROCm
  `10.1.0`; its environment documentation identifies Python 3.13.7. Those
  environment records are not independent hardware attestation.
- The report states that development and reserved data were not read during
  training. The 32-source development set has disjoint source identities and
  seeds from training, but was previously inspected and shares synthetic
  generator and font families. It is not independent external acceptance
  evidence. No sealed run was authorized by this protocol.

## Frozen identities

| Artifact | SHA-256 |
| --- | --- |
| [P1 config](../ml/axis/gpu_candidate_ranker_v1/p1-v4.json) | `4acacf53a23a7bc948a482c2fde7daddb839e682082891964805126eb0c745cd` |
| Runner source bundle, recorded in opened receipt | `ecd61cacb94d50ee108608d449994d9f98c909ee15366adf56931db8f84856a0` |
| Train groups | `d9c5928ffea0407fd5b1041d3c3ca9146f128d71054cca0026a51e88a3dc67b5` |
| Continuous train targets | `15193d25c5346799d16aadbaaffb303cdcb0b361aa2a7ebdedf336d706289582` |
| Training report | `3c196ec8f935d797f709efd48a415051b4d68e2bf22e085d32064d50aaee57a1` |
| Finalization receipt | `4e113ba64158c60ae285f58bb8e1231f40c55970177e81673548ea81b0d9140c` |
| Canonical result receipt | `2506dfd364536cff2590db750fe093a99aa44b3fb3bc32a69c502a7f9ad9a676` |
| Checkpoint | `42a57afd0428bf028dbbf24279a0547ab02769dec1db4d49c5313d5735483adf` |
| Exploratory development report | `362850c0788abdb95e03dc595e92f5988ae34438d86d74d23b03e3893c9dc7e6` |

The output report has status `training_complete_pending_finalization`; the
separate finalization receipt says `finalized` and binds the same report and
checkpoint. The canonical result records `training_complete_unapproved`.
Artifacts are under `artifacts/goal22-runs/axis-ranker-gpu-training-attempt-v1`
and the candidate receipt is under
`ml/markers/training-seals/axis-candidate-ranker/axis-candidate-linear-gpu-v1/P1`.

## Exploratory development comparison

The independently audited report at
`artifacts/goal22-runs/axis-ranker-dev-eval-20261031-v3/report.json` compares
the candidate with the production observer's first-ranked axis pair across 32
synthetic sources and 48 panels. It scores 46 uniquely matched detected
panels; the two full-image fallbacks remain in the denominator as missed plots.
On the scored panels, 23 improved, nine were equal and 14 worsened. Mean
four-corner error changed from 7.1513 to 5.8153 pixels; mean error normalized
independently by plot width and height changed from 0.01278144 to 0.01157865.

The worst observed regression selected production candidate rank 2 rather than
rank 1: mean pixel error rose from 0.5019 to 2.4684 pixels, while normalized
error rose from 0.00152712 to 0.01411746. These results describe the inspected
development set only. The shared synthetic generator and fonts and previous
development exposure preclude an independent acceptance claim or promotion;
the existing production uncertainty gate remains unchanged.

All 37 changed selections came from panels whose native top-two geometry score
margin was below 0.00105. The current detector marks a margin below 0.08 as
ambiguous, and the automatic workflow requires review for that condition. On
this sample the proposed changes therefore provide no demonstrated gain in
automatic calibration. The ranker was not integrated into the application.

## Initial attempt

The first invocation failed while acquiring the candidate's source snapshot,
before the optimizer start or any step. Its log,
`artifacts/goal22-runs/axis-ranker-gpu-training-v1-20261031.log`, reports that
the snapshot could not obtain a valid Git HEAD. Git in the same checkout reports
the repository's ownership as dubious without a trust exception. The retry
used a process-local Git trust setting and completed; its log is
`artifacts/goal22-runs/axis-ranker-gpu-training-v1-20261031-retry.log`. The
optimizer-start intent and completed step receipts belong to the successful
attempt. Neither attempt authorizes model promotion or a sealed evaluation.
