<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Residual axis candidate ranker: exploratory result

One fixed synthetic-training candidate starts from the native axis-pair score
and adds a 17-to-32-to-1 tanh residual. Its sharper target distribution and
residual penalty also differ from the earlier linear experiment, so this run
does not isolate which change caused the result. Candidate generation,
calibration review, and production acceptance were not changed.

The RX 6600 XT (`gfx1032`, HIP `7.16.26385`, `cuda:0`) completed all 372
registered optimizer steps in 11.73 seconds with no CPU fallback. The recipe
used seed 20261101 and four passes over 93 matched training panels from 64
synthetic sources. Three training fallback panels were unscored. The runner
verified GPU model, input, gradient, and optimizer-state residency, and used a
durable step journal. The pre-run source snapshot was based on commit
`fa9d6f10` and bound the not-yet-committed trainer and policy bytes.

| Split | Scored / fallback | Improved / equal / worsened | Native mean error | Candidate mean error |
| --- | ---: | ---: | ---: | ---: |
| Synthetic training | 93 / 3 | 46 / 26 / 21 | 0.01165690 | 0.01023479 |
| Synthetic development | 46 / 2 | 24 / 7 / 15 | 0.01278144 | 0.01146679 |

Error is mean four-corner distance normalized by plot dimensions. Development
candidate ranks were written before opening development annotations. These 32
development sources are separate from training sources, but share generator
and font families and were previously inspected. This is an exploratory
comparison, not an independent acceptance test. All 39 changed, scored
development selections had a native top-two score margin below 0.001041; the
existing geometry detector flags margins below 0.08 for review. Therefore this
model does not establish automatic calibration. Its 15 worsened cases and two
unscored development fallbacks remain material limitations.

The checkpoint is unapproved, is not packaged, and is retained only for the
ongoing axis feasibility investigation. No real-development or sealed data was
read in this run, and no threshold was selected after scoring. The GPU and CPU
forward passes selected the same ranks on all 93 training and 48 development
panels; maximum absolute score differences were `2.3842e-7` and `1.1921e-7`,
respectively. This checks these frozen candidate vectors, not runtime ONNX or
end-to-end application parity.

| Evidence | SHA-256 |
| --- | --- |
| Registered config / trainer bundle | `31d1fdf9605295e709122a761394dcb7f6145b27e18f67e8ddc4647afab78a65` / `3d789e3cbb436265024df9ee8d3572377a166cc90610b023de5e15ccdd83917a` |
| Synthetic train groups / targets | `d9c5928ffea0407fd5b1041d3c3ca9146f128d71054cca0026a51e88a3dc67b5` / `15193d25c5346799d16aadbaaffb303cdcb0b361aa2a7ebdedf336d706289582` |
| Training report / checkpoint | `129d8f7284a877f76e52887b5e94d30ff382e500d136d2f5ce8c9c3af7a88f64` / `4c35bd9424f209fe7e9912560fc4fdcfb1d0580f1af591543bac2461ba4d0adb` |
| Train / dev aggregate reports | `5c392067f9582635144fa87599ed59c3900c08628962f5c5d1baff2ef3350d2d` / `1799539d02f56d2d6c9175311c7bc45ac93cf69ea02434501c71ee435492da30` |
| Dev predictions / preceding native comparison | `d961aa205694daf77a18bce551413c4856cb2687f1af7c598e5c913817583dba` / `362850c0788abdb95e03dc595e92f5988ae34438d86d74d23b03e3893c9dc7e6` |

Eight focused unit tests and pyflakes passed. The development evaluation
recomputes candidate features, verifies synthetic source and annotation hashes,
rechecks geometric panel associations, and reproduces the preceding native
baseline before reporting the comparison. No optimizer step is performed by
either evaluation script.
